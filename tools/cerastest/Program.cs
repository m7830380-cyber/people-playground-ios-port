using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ceras;
using Ceras.Formatters;
using PPGShim;

static class Program
{
    class StatsLike
    {
        public string Identifier;
        public Dictionary<string, float> Stats = new Dictionary<string, float>();
    }

    static int s_Fail;

    static void Check(string what, byte[] a, byte[] b)
    {
        if (a.SequenceEqual(b)) return;
        s_Fail++;
        int i = 0;
        while (i < Math.Min(a.Length, b.Length) && a[i] == b[i]) i++;
        Console.WriteLine($"  MISMATCH {what}: lengths {a.Length} vs {b.Length}, first difference at byte {i}");
        var dump = Environment.GetEnvironmentVariable("CERASTEST_DUMP");
        if (!string.IsNullOrEmpty(dump) && s_Fail == 1)
        {
            File.WriteAllBytes(Path.Combine(dump, "a.bin"), a);
            File.WriteAllBytes(Path.Combine(dump, "b.bin"), b);
        }
    }

    static int Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : @"C:\Program Files (x86)\Steam\steamapps\common\People Playground\Contraptions";
        string managed = args.Length > 1 ? args[1] : @"C:\Program Files (x86)\Steam\steamapps\common\People Playground\People Playground_Data\Managed";
        // the type binders search loaded assemblies only: load everything the game ships
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            var p = Path.Combine(managed, new System.Reflection.AssemblyName(e.Name).Name + ".dll");
            return File.Exists(p) ? System.Reflection.Assembly.LoadFrom(p) : null;
        };
        foreach (var dll in Directory.GetFiles(managed, "*.dll"))
        {
            var n = Path.GetFileNameWithoutExtension(dll);
            if (n == "mscorlib" || n.StartsWith("System") || n == "netstandard" || n == "Mono.Security") continue;
            try { System.Reflection.Assembly.LoadFrom(dll); } catch { }
        }
        // the game's contraption serializer
        var config = new SerializerConfig();
        CerasUnityFormatters.ApplyToConfig(config);
        var custom = new CustomStringFormatter();
        var ceras = new CerasSerializer(config);
        StringFormatter.DeserializeOverride = custom.Deserialize;
        StringFormatter.SerializeOverride = custom.Serialize;

        int files = 0, unreadable = 0;
        foreach (var path in Directory.EnumerateFiles(dir, "*.jaap", SearchOption.AllDirectories))
        {
            files++;
            var data = File.ReadAllBytes(path);
            string name = Path.GetFileName(path);
            var real = new Contraption(new ObjectState[0]);
            try { ceras.Deserialize(ref real, data); }
            catch (Exception e)
            {
                // the game can't load it either (made with mods, or an older game version); we must not crash
                unreadable++;
                try { var m = new Contraption(new ObjectState[0]); CerasCompat.Deserialize<Contraption>(null, ref m, data); }
                catch (Exception) { }
                string msg = e.Message.Split('\n')[0];
                Console.WriteLine($"{name}: not loadable by the game either ({e.GetType().Name}: {msg.Substring(0, Math.Min(80, msg.Length))})");
                continue;
            }
            try
            {
                var realBytes = ceras.Serialize(real);
                bool realRoundTrips = realBytes.SequenceEqual(data);

                var mine = new Contraption(new ObjectState[0]);
                CerasCompat.Deserialize<Contraption>(null, ref mine, data);
                var mineBytes = CerasCompat.Serialize<Contraption>(null, mine);

                // the reinterpret formatter copies 4 bytes per bool from a 1-byte-per-bool array, so Ceras' output
                // contains stray memory after bool[] contents: compare content, and list where bytes differ
                string d1 = DeepEq.Diff(real, mine);
                if (d1 != null) { s_Fail++; Console.WriteLine("  MISMATCH read: " + d1); }
                var back = new Contraption(new ObjectState[0]);
                ceras.Deserialize(ref back, mineBytes);
                string d2 = DeepEq.Diff(real, back);
                if (d2 != null) { s_Fail++; Console.WriteLine("  MISMATCH my write read back by Ceras: " + d2); }
                int diffs = 0;
                if (mineBytes.Length != realBytes.Length) { s_Fail++; Console.WriteLine($"  MISMATCH length {mineBytes.Length} vs {realBytes.Length}"); }
                else for (int k = 0; k < mineBytes.Length; k++) if (mineBytes[k] != realBytes[k]) { diffs++; if (mineBytes[k] != 0) { s_Fail++; Console.WriteLine($"  MISMATCH byte {k}: {mineBytes[k]} vs {realBytes[k]}"); break; } }
                if (diffs > 0) Console.WriteLine($"  {diffs} bytes differ where Ceras wrote stray memory");
                Console.WriteLine($"{name}: {data.Length} bytes, {mine.ObjectStates.Length} objects, original file round-trips through Ceras: {realRoundTrips}");
            }
            catch (Exception e)
            {
                s_Fail++;
                Console.WriteLine($"{name}: EXCEPTION {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
            }
        }

        // trust cache / stats use default-config serializers
        var plain = new CerasSerializer();
        var hs = new HashSet<uint> { 1, 2, 300, 4000000000, 77 };
        Check("HashSet<uint>", CerasCompat.Serialize<HashSet<uint>>(null, hs), plain.Serialize(hs));
        var hs2 = CerasCompat.Deserialize<HashSet<uint>>(null, plain.Serialize(hs));
        if (!hs2.SetEquals(hs)) { s_Fail++; Console.WriteLine("  MISMATCH HashSet<uint> read"); }
        var st = new StatsLike { Identifier = "abc\u00e9" };
        st.Stats["BODY_COUNT"] = 12; st.Stats["X"] = -0.5f;
        Check("stats", CerasCompat.Serialize<StatsLike>(null, st), plain.Serialize(st));
        var st2 = CerasCompat.Deserialize<StatsLike>(null, plain.Serialize(st));
        if (st2.Identifier != st.Identifier || st2.Stats.Count != 2 || st2.Stats["X"] != -0.5f) { s_Fail++; Console.WriteLine("  MISMATCH stats read"); }

        Console.WriteLine($"{files} contraption files ({unreadable} not loadable by the game itself), {s_Fail} problems");
        return s_Fail == 0 ? 0 : 1;
    }
}
