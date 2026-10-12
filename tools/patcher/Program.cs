// Patches the retail People Playground managed assemblies so they can be AOT-compiled with IL2CPP and
// run on iOS.
//
//   PpgPatcher patch   <retailManagedDir> <PPGShim.dll> <outDir>
//   PpgPatcher analyze <managedDir>
//
// All assemblies:
//  * every P/Invoke (Steam, NAudio/winmm, Harmony's mprotect, ...) becomes a managed stub that throws
//    DllNotFoundException, so IL2CPP never links against native libraries that don't exist on iOS.
//  * Process.Start & co. go to PPGShim.Platform (logs and does nothing; http(s) URLs open in Safari).
// Input-reading assemblies (game, UnityEngine.UI, TextMeshPro, ...):
//  * every UnityEngine.Input member goes to PPGShim.VInput (virtual mouse/keys driven by touch).
// Assembly-CSharp:
//  * Steam is never initialised (SteamworksInitialiser does nothing, IsInitialised stays false).
//  * the mod system is off: the mod compiler server is never started, no mod is ever loaded.
//  * Application.isMobilePlatform reads false (the game quits on mobile when objects collide).
//  * vSyncCount / targetFrameRate go through PPGShim.Platform (iOS needs an explicit frame rate).
//  * Windows "\" separators in path literals become "/".
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

static class Program
{
    // Assemblies shipped in the iOS player (patched copies go to outDir).
    public static readonly string[] Shipped =
    {
        "0Harmony.dll", "Assembly-CSharp-firstpass.dll", "Assembly-CSharp.dll", "Ceras.dll",
        "endel.nativewebsocket.dll", "Facepunch.Steamworks.Win64.dll", "ImmersiveVRTools.Common.Runtime.dll",
        "LeTai.TranslucentImage.dll", "ModModels.dll", "NAudio-Unity.dll", "NaughtyAttributes.Core.dll",
        "Newtonsoft.Json.dll", "Newtonsoft.Json.UnityConverters.dll", "Newtonsoft.Json.UnityConverters.Addressables.dll",
        "System.Buffers.dll", "System.Runtime.CompilerServices.Unsafe.dll",
        "Unity.Addressables.dll", "Unity.Mathematics.dll", "Unity.Postprocessing.Runtime.dll",
        "Unity.RenderPipeline.Universal.ShaderLibrary.dll", "Unity.RenderPipelines.Core.Runtime.dll",
        "Unity.RenderPipelines.Core.ShaderLibrary.dll", "Unity.RenderPipelines.ShaderGraph.ShaderGraphLibrary.dll",
        "Unity.RenderPipelines.Universal.Runtime.dll", "Unity.RenderPipelines.Universal.Shaders.dll",
        "Unity.ResourceManager.dll", "Unity.ScriptableBuildPipeline.dll", "Unity.TextMeshPro.dll",
        "Unity.VisualEffectGraph.Runtime.dll", "UnityEngine.UI.dll", "WatsonTcp.dll",
    };

    // Assemblies whose UnityEngine.Input reads are redirected to the virtual input.
    static readonly HashSet<string> InputRedirect = new HashSet<string>
    {
        "Assembly-CSharp", "Assembly-CSharp-firstpass", "UnityEngine.UI", "Unity.TextMeshPro",
        "LeTai.TranslucentImage", "ImmersiveVRTools.Common.Runtime",
    };

    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "analyze") return Analyze.Run(args[1]);
        if (args.Length >= 4 && args[0] == "patch") return Patch(args[1], args[2], args[3]);
        Console.Error.WriteLine("usage: PpgPatcher patch <managedDir> <PPGShim.dll> <outDir> | analyze <managedDir>");
        return 1;
    }

    static int Patch(string inDir, string shimPath, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(inDir);
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(shimPath)));
        var rp = new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate, ReadSymbols = false };
        var shim = AssemblyDefinition.ReadAssembly(shimPath, rp);

        int total = 0, failures = 0;
        foreach (var name in Shipped)
        {
            var asm = AssemblyDefinition.ReadAssembly(Path.Combine(inDir, name), rp);
            var ctx = new Ctx(asm.MainModule, shim.MainModule);
            int n = StubPInvokes(ctx);
            n += RedirectProcess(ctx);
            if (InputRedirect.Contains(asm.Name.Name))
                n += RedirectInput(ctx);
            if (asm.Name.Name == "Assembly-CSharp")
                n += PatchGame(ctx);
            total += n;
            failures += ctx.Failures;
            asm.Write(Path.Combine(outDir, name));
            Console.WriteLine($"{name}: {n} patches{(ctx.Failures > 0 ? $", {ctx.Failures} FAILED" : "")}");
        }
        File.Copy(shimPath, Path.Combine(outDir, "PPGShim.dll"), true);
        Console.WriteLine($"total {total} patches, {failures} failures");
        return failures == 0 ? 0 : 2;
    }

    sealed class Ctx
    {
        public readonly ModuleDefinition Mod;
        public readonly ModuleDefinition Shim;
        public int Failures;
        public Ctx(ModuleDefinition mod, ModuleDefinition shim) { Mod = mod; Shim = shim; }

        public TypeDefinition ShimType(string name) => Shim.GetType("PPGShim." + name) ?? throw new Exception("shim type missing: " + name);

        public MethodReference ShimMethod(string type, string name, params string[] paramTypes)
        {
            var m = ShimType(type).Methods.FirstOrDefault(x => x.Name == name && x.Parameters.Count == paramTypes.Length &&
                x.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(paramTypes));
            if (m == null) throw new Exception($"shim method missing: {type}.{name}({string.Join(",", paramTypes)})");
            return Mod.ImportReference(m);
        }

        public void Fail(string what)
        {
            Console.WriteLine("  !! " + what);
            Failures++;
        }
    }

    static IEnumerable<(MethodDefinition m, Instruction ins)> Calls(ModuleDefinition mod)
    {
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions.ToList())
                    if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) && ins.Operand is MethodReference)
                        yield return (m, ins);
            }
    }

    static string Sig(MethodReference mr) => string.Join(",", mr.Parameters.Select(p => p.ParameterType.FullName));

    // ------------------------------------------------------------------------------------------
    static int StubPInvokes(Ctx c)
    {
        int n = 0;
        var mod = c.Mod;
        var dnfType = new TypeReference("System", "DllNotFoundException", mod, mod.TypeSystem.CoreLibrary);
        var dnfCtor = new MethodReference(".ctor", mod.TypeSystem.Void, dnfType) { HasThis = true };
        dnfCtor.Parameters.Add(new ParameterDefinition(mod.TypeSystem.String));
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.IsPInvokeImpl || m.PInvokeInfo == null) continue;
                string lib = m.PInvokeInfo.Module.Name;
                m.PInvokeInfo = null;     // Cecil's setter turns IsPInvokeImpl back on,
                m.IsPInvokeImpl = false;  // so clear the flag afterwards
                m.IsPreserveSig = false;
                m.ImplAttributes &= ~MethodImplAttributes.PreserveSig;
                m.ImplAttributes |= MethodImplAttributes.IL | MethodImplAttributes.Managed;
                m.Body = new MethodBody(m);
                var il = m.Body.GetILProcessor();
                il.Emit(OpCodes.Ldstr, lib + " is not available on iOS");
                il.Emit(OpCodes.Newobj, dnfCtor);
                il.Emit(OpCodes.Throw);
                n++;
            }
        return n;
    }

    // ------------------------------------------------------------------------------------------
    static int RedirectProcess(Ctx c)
    {
        int n = 0;
        foreach (var (m, ins) in Calls(c.Mod))
        {
            var mr = (MethodReference)ins.Operand;
            if (mr.DeclaringType.FullName != "System.Diagnostics.Process") continue;
            MethodReference to = null;
            if (mr.Name == "Start" && !mr.HasThis)
            {
                string sig = Sig(mr);
                if (sig == "System.Diagnostics.ProcessStartInfo") to = c.ShimMethod("Platform", "ProcessStart", "System.Diagnostics.ProcessStartInfo");
                else if (sig == "System.String") to = c.ShimMethod("Platform", "ProcessStart", "System.String");
                else if (sig == "System.String,System.String") to = c.ShimMethod("Platform", "ProcessStart", "System.String", "System.String");
                else { c.Fail($"{m.FullName}: unhandled Process.Start({sig})"); continue; }
            }
            else if (mr.Name == "Start" && mr.HasThis && mr.Parameters.Count == 0)
                to = c.ShimMethod("Platform", "ProcessStartInstance", "System.Diagnostics.Process");
            else if (mr.Name == "GetProcessesByName" && Sig(mr) == "System.String")
                to = c.ShimMethod("Platform", "GetProcessesByName", "System.String");
            if (to == null) continue;
            ins.OpCode = OpCodes.Call;
            ins.Operand = to;
            n++;
        }
        return n;
    }

    // ------------------------------------------------------------------------------------------
    static int RedirectInput(Ctx c)
    {
        int n = 0;
        var vinput = c.ShimType("VInput");
        foreach (var (m, ins) in Calls(c.Mod))
        {
            var mr = (MethodReference)ins.Operand;
            if (mr.DeclaringType.FullName != "UnityEngine.Input") continue;
            var target = vinput.Methods.FirstOrDefault(x => x.Name == mr.Name && x.IsStatic &&
                x.Parameters.Count == mr.Parameters.Count && Sig(x) == Sig(mr) && x.ReturnType.FullName == mr.ReturnType.FullName);
            if (target == null)
            {
                c.Fail($"{m.FullName}: no VInput equivalent for Input.{mr.Name}({Sig(mr)})");
                continue;
            }
            ins.OpCode = OpCodes.Call;
            ins.Operand = c.Mod.ImportReference(target);
            n++;
        }
        return n;
    }

    // ------------------------------------------------------------------------------------------
    static readonly Regex PathLiteral = new Regex(@"^[\w .\-]+(\\[\w .\-]*)+$");

    static int PatchGame(Ctx c)
    {
        var mod = c.Mod;
        int n = 0;

        // Application.isMobilePlatform -> false, vSync/targetFrameRate -> shim
        var setVSync = c.ShimMethod("Platform", "SetVSyncCount", "System.Int32");
        var setFps = c.ShimMethod("Platform", "SetTargetFrameRate", "System.Int32");
        foreach (var (m, ins) in Calls(mod))
        {
            var mr = (MethodReference)ins.Operand;
            string dt = mr.DeclaringType.FullName;
            if (dt == "UnityEngine.Application" && mr.Name == "get_isMobilePlatform")
            {
                ins.OpCode = OpCodes.Ldc_I4_0;
                ins.Operand = null;
                n++;
            }
            else if (dt == "UnityEngine.QualitySettings" && mr.Name == "set_vSyncCount")
            {
                ins.OpCode = OpCodes.Call; ins.Operand = setVSync; n++;
            }
            else if (dt == "UnityEngine.Application" && mr.Name == "set_targetFrameRate")
            {
                ins.OpCode = OpCodes.Call; ins.Operand = setFps; n++;
            }
        }

        // Windows path separators in path-like string literals
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.OpCode != OpCodes.Ldstr) continue;
                    var s = (string)ins.Operand;
                    if (s == "\\" || PathLiteral.IsMatch(s))
                    {
                        ins.Operand = s.Replace('\\', '/');
                        n++;
                    }
                }
                foreach (var f in t.Fields)
                    if (f.HasConstant && f.Constant is string cs && PathLiteral.IsMatch(cs))
                        f.Constant = cs.Replace('\\', '/');
            }

        // --- Steam: never initialise ----------------------------------------------------------
        var steamInit = mod.GetType("SteamworksInitialiser") ?? throw new Exception("SteamworksInitialiser missing");
        {
            var awake = steamInit.Methods.First(x => x.Name == "Awake");
            var isInit = steamInit.Fields.First(x => x.Name == "IsInitialised");
            var core = mod.AssemblyReferences.First(r => r.Name == "UnityEngine.CoreModule");
            var objType = new TypeReference("UnityEngine", "Object", mod, core);
            var ddol = new MethodReference("DontDestroyOnLoad", mod.TypeSystem.Void, objType);
            ddol.Parameters.Add(new ParameterDefinition(objType));
            var debugType = new TypeReference("UnityEngine", "Debug", mod, core);
            var log = new MethodReference("Log", mod.TypeSystem.Void, debugType);
            log.Parameters.Add(new ParameterDefinition(mod.TypeSystem.Object));
            var compType = new TypeReference("UnityEngine", "Component", mod, core);
            var getGo = new MethodReference("get_gameObject", new TypeReference("UnityEngine", "GameObject", mod, core), compType) { HasThis = true };

            awake.Body = new MethodBody(awake);
            var il = awake.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, getGo);
            il.Emit(OpCodes.Call, ddol);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, ddol);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stsfld, isInit);
            il.Emit(OpCodes.Ldstr, "[PPG-iOS] Steamworks disabled on iOS");
            il.Emit(OpCodes.Call, log);
            il.Emit(OpCodes.Ret);
            n++;
            n += MakeNoOp(c, steamInit, "Update");
            n += MakeNoOp(c, steamInit, "OnDestroy");
        }
        // Workshop upload checks this first; "family shared" makes it refuse cleanly without Steam.
        n += ReturnConst(c, mod.GetType("Utils"), "IsFamilyShared", 1);

        // --- mod system off ------------------------------------------------------------------
        var compilerClient = mod.GetType("CompilerClient") ?? throw new Exception("CompilerClient missing");
        foreach (var name in new[] { "InitialiseConfig", "Start", "Stop", "Dispose" })
            n += MakeNoOp(c, compilerClient, name);
        n += MakeNoOp(c, mod.GetType("CompilerServerBehaviour"), "OnApplicationQuit");
        {
            // ModLoader.LoadModAt(path) -> null: no mod is ever compiled or loaded.
            var loadModAt = mod.GetType("ModLoader")?.Methods.FirstOrDefault(x => x.Name == "LoadModAt");
            if (loadModAt == null) c.Fail("ModLoader.LoadModAt missing");
            else
            {
                loadModAt.Body = new MethodBody(loadModAt);
                var il = loadModAt.Body.GetILProcessor();
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ret);
                n++;
            }
        }

        // --- fields the touch layer drives directly (pinch zoom / two-finger pan) -------------
        n += MakePublic(c, mod.GetType("CameraControlBehaviour"), "targetZoom", "targetPos");
        return n;
    }

    static int MakePublic(Ctx c, TypeDefinition t, params string[] fields)
    {
        int n = 0;
        foreach (var name in fields)
        {
            var f = t?.Fields.FirstOrDefault(x => x.Name == name);
            if (f == null) { c.Fail($"{t?.Name}.{name} missing"); continue; }
            f.Attributes = (f.Attributes & ~FieldAttributes.FieldAccessMask) | FieldAttributes.Public;
            n++;
        }
        return n;
    }

    static int MakeNoOp(Ctx c, TypeDefinition t, string name)
    {
        var m = t?.Methods.FirstOrDefault(x => x.Name == name && x.ReturnType.FullName == "System.Void");
        if (m == null) { c.Fail($"{t?.Name}.{name} missing"); return 0; }
        m.Body = new MethodBody(m);
        m.Body.GetILProcessor().Emit(OpCodes.Ret);
        return 1;
    }

    static int ReturnConst(Ctx c, TypeDefinition t, string name, int value)
    {
        var m = t?.Methods.FirstOrDefault(x => x.Name == name);
        if (m == null) { c.Fail($"{t?.Name}.{name} missing"); return 0; }
        m.Body = new MethodBody(m);
        var il = m.Body.GetILProcessor();
        il.Emit(OpCodes.Ldc_I4, value);
        il.Emit(OpCodes.Ret);
        return 1;
    }
}
