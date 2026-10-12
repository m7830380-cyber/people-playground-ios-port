using Mono.Cecil;
using Mono.Cecil.Cil;

// `PpgPatcher analyze <managedDir>`: lists, per assembly, its references and every member it uses from
// APIs the iOS port has to care about (Input, Process, Registry, Application, Screen, ...).
static class Analyze
{
    static readonly string[] Interesting =
    {
        "UnityEngine.Input", "UnityEngine.Cursor", "UnityEngine.Application", "UnityEngine.Screen",
        "UnityEngine.QualitySettings", "UnityEngine.TouchScreenKeyboard", "System.Diagnostics.Process",
        "Microsoft.Win32.", "System.Windows.Forms.", "System.Reflection.Emit.", "HarmonyLib.",
        "System.Net.Sockets.", "System.IO.Pipes.", "System.Threading.Thread",
    };

    public static int Run(string dir)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(dir);
        foreach (var path in Directory.GetFiles(dir, "*.dll").OrderBy(p => p))
        {
            string name = Path.GetFileName(path);
            if (name.StartsWith("UnityEngine.") && name != "UnityEngine.UI.dll") continue;
            if (name.StartsWith("System.") || name.StartsWith("Mono.") || name == "mscorlib.dll" || name == "netstandard.dll") continue;
            AssemblyDefinition asm;
            try { asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = resolver }); }
            catch (Exception e) { Console.WriteLine($"== {name}: unreadable ({e.Message})"); continue; }
            var mod = asm.MainModule;
            Console.WriteLine($"== {name}  refs: {string.Join(", ", mod.AssemblyReferences.Select(r => r.Name))}");
            var uses = new SortedDictionary<string, int>();
            int pinv = 0;
            foreach (var t in mod.GetTypes())
                foreach (var m in t.Methods)
                {
                    if (m.IsPInvokeImpl) pinv++;
                    if (!m.HasBody) continue;
                    foreach (var ins in m.Body.Instructions)
                    {
                        if (!(ins.Operand is MemberReference mr) || mr.DeclaringType == null) continue;
                        string dt = mr.DeclaringType.FullName;
                        if (!Interesting.Any(p => dt == p || (p.EndsWith(".") && dt.StartsWith(p)))) continue;
                        string key = dt + "::" + mr.Name + (mr is MethodReference meth ? "(" + string.Join(",", meth.Parameters.Select(p => p.ParameterType.Name)) + ")" : "");
                        uses[key] = uses.TryGetValue(key, out int c) ? c + 1 : 1;
                    }
                }
            if (pinv > 0) Console.WriteLine($"   pinvokes: {pinv}");
            foreach (var kv in uses) Console.WriteLine($"   {kv.Value,4}  {kv.Key}");
        }
        return 0;
    }
}
