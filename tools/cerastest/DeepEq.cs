using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

// Structural comparison of two object graphs (public and private instance fields, arrays, collections).
static class DeepEq
{
    sealed class RefEq : IEqualityComparer<object>
    {
        public new bool Equals(object a, object b) => ReferenceEquals(a, b);
        public int GetHashCode(object o) => RuntimeHelpers.GetHashCode(o);
    }

    public static string Diff(object a, object b) => Diff(a, b, "root", new HashSet<object>(new RefEq()), 0);

    static string Diff(object a, object b, string path, HashSet<object> seen, int depth)
    {
        if (a == null || b == null) return a == null && b == null ? null : path + ": null vs non-null";
        if (a.GetType() != b.GetType()) return $"{path}: type {a.GetType()} vs {b.GetType()}";
        var t = a.GetType();
        if (a is Type ta) return ta == (Type)b ? null : $"{path}: {ta} vs {b}";
        if (t.IsPrimitive || t.IsEnum || a is string || a is decimal)
        {
            if (a is float fa && b is float fb && (fa.Equals(fb) || (float.IsNaN(fa) && float.IsNaN(fb)))) return null;
            return a.Equals(b) ? null : $"{path}: {a} vs {b}";
        }
        if (depth > 60) return null;
        if (!t.IsValueType && !seen.Add(a)) return null;
        if (a is IDictionary da)
        {
            var db = (IDictionary)b;
            if (da.Count != db.Count) return $"{path}: count {da.Count} vs {db.Count}";
            var ea = da.GetEnumerator(); var eb = db.GetEnumerator();
            int i = 0;
            while (ea.MoveNext() && eb.MoveNext())
            {
                var r = Diff(ea.Key, eb.Key, $"{path}.key[{i}]", seen, depth + 1) ?? Diff(ea.Value, eb.Value, $"{path}[{ea.Key}]", seen, depth + 1);
                if (r != null) return r;
                i++;
            }
            return null;
        }
        if (a is Array aa)
        {
            var ab = (Array)b;
            if (aa.Length != ab.Length) return $"{path}: length {aa.Length} vs {ab.Length}";
            for (int i = 0; i < aa.Length; i++)
            {
                var r = Diff(aa.GetValue(i), ab.GetValue(i), $"{path}[{i}]", seen, depth + 1);
                if (r != null) return r;
            }
            return null;
        }
        if (a is IEnumerable ena && !(a is string))
        {
            var la = new List<object>(); foreach (var x in ena) la.Add(x);
            var lb = new List<object>(); foreach (var x in (IEnumerable)b) lb.Add(x);
            return Diff(la.ToArray(), lb.ToArray(), path, seen, depth + 1);
        }
        for (var x = t; x != null && x != typeof(object) && x != typeof(ValueType); x = x.BaseType)
            foreach (var f in x.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                var r = Diff(f.GetValue(a), f.GetValue(b), path + "." + f.Name, seen, depth + 1);
                if (r != null) return r;
            }
        return null;
    }
}
