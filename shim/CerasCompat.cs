using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;

namespace PPGShim
{
    // Drop-in replacement for the Ceras serializer calls the game makes (contraptions, trust cache, stats).
    //
    // Ceras builds its (de)serializers with System.Linq.Expressions and instantiates generic formatters over
    // value types at runtime (ReinterpretFormatter<Vector2>, CollectionFormatter<HashSet<uint>, uint>, ...).
    // Under IL2CPP neither works: byref delegates can't be interpreted and the value-type instantiations were
    // never AOT-compiled. This is a reflection-only, non-generic reimplementation of the same wire format
    // (Ceras 4.x with the game's settings: references preserved, no version tolerance, reinterpret formatter
    // for blittable structs, no known types, no checksum), so .jaap files stay compatible both ways.
    //
    // The patcher redirects CerasSerializer.Serialize<T>(T) / Deserialize<T>(byte[]) /
    // Deserialize<T>(ref T, byte[]) here; the CerasSerializer instance is passed along and ignored.
    public static class CerasCompat
    {
        public static byte[] Serialize<T>(object ceras, T obj)
        {
            var w = new Writer();
            w.WriteRef(typeof(T), obj);
            return w.ToArray();
        }

        public static T Deserialize<T>(object ceras, byte[] buffer)
        {
            T value = default(T);
            Deserialize(ceras, ref value, buffer);
            return value;
        }

        public static void Deserialize<T>(object ceras, ref T value, byte[] buffer)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            var r = new Reader(buffer);
            value = (T)r.ReadRef(typeof(T), value);
            if (r.Offset > buffer.Length)
                throw new IndexOutOfRangeException("The read cursor ended up beyond the array bounds, the data is corrupted");
        }

        // ---- type model ----------------------------------------------------------------------------
        enum Kind
        {
            String, TypeRef, Bool, Byte, SByte, Char, Int16, UInt16, Int32, UInt32, Int64, UInt64, Single, Double,
            Enum, Raw, DateTime, DateTimeOffset, TimeSpan, Nullable, KeyValuePair,
            RawArray, Array, Collection, Dynamic,
        }

        sealed class Member
        {
            public MemberInfo Info;
            public Type Type;
            public string Name;
            public int WriteBackOrder;
            public object Get(object o) => Info is FieldInfo f ? f.GetValue(o) : ((PropertyInfo)Info).GetValue(o, null);
            public void Set(object o, object v)
            {
                if (Info is FieldInfo f) f.SetValue(o, v);
                else ((PropertyInfo)Info).GetSetMethod(true).Invoke(o, new[] { v });
            }
        }

        sealed class Info
        {
            public Type Type;
            public Kind Kind;
            public Kind Prim;                 // Enum: underlying primitive kind
            public Type Elem;                 // arrays/collections: item type; Nullable: inner
            public Type Key, Value;           // KeyValuePair / Dictionary
            public bool IsDictionary;
            public MethodInfo AddMethod;      // ICollection<T>.Add for non-dictionaries
            public Member[] Members;          // Dynamic, in wire order
            public Member[] WriteBack;        // Dynamic, in write-back order
            public MethodInfo AfterDeserialize;
            public bool FormatterConstructed; // Ceras does not construct these before handing them to the formatter
            public Layout Layout;             // Raw / RawArray element
        }

        static readonly Dictionary<Type, Info> s_Infos = new Dictionary<Type, Info>();
        static readonly object s_Lock = new object();

        static Info GetInfo(Type t)
        {
            lock (s_Lock)
            {
                if (s_Infos.TryGetValue(t, out var i)) return i;
                i = Build(t);
                s_Infos[t] = i;
                return i;
            }
        }

        static readonly Dictionary<Type, Kind> s_Prims = new Dictionary<Type, Kind>
        {
            [typeof(bool)] = Kind.Bool, [typeof(byte)] = Kind.Byte, [typeof(sbyte)] = Kind.SByte, [typeof(char)] = Kind.Char,
            [typeof(short)] = Kind.Int16, [typeof(ushort)] = Kind.UInt16, [typeof(int)] = Kind.Int32, [typeof(uint)] = Kind.UInt32,
            [typeof(long)] = Kind.Int64, [typeof(ulong)] = Kind.UInt64, [typeof(float)] = Kind.Single, [typeof(double)] = Kind.Double,
        };

        static Info Build(Type t)
        {
            var i = new Info { Type = t };
            if (t == typeof(string)) { i.Kind = Kind.String; i.FormatterConstructed = true; return i; }
            if (typeof(Type).IsAssignableFrom(t)) { i.Kind = Kind.TypeRef; i.FormatterConstructed = true; return i; }
            if (s_Prims.TryGetValue(t, out var pk)) { i.Kind = pk; return i; }
            if (t.IsEnum) { i.Kind = Kind.Enum; i.Prim = s_Prims[Enum.GetUnderlyingType(t)]; return i; }
            if (t == typeof(IntPtr) || t == typeof(UIntPtr) || t.IsPointer)
                throw new NotSupportedException("CerasCompat: pointers are not supported (" + t + ")");
            if (IsBlittable(t)) { i.Kind = Kind.Raw; i.Layout = Layout.Of(t); return i; }
            if (t == typeof(DateTime)) { i.Kind = Kind.DateTime; return i; }
            if (t == typeof(DateTimeOffset)) { i.Kind = Kind.DateTimeOffset; return i; }
            if (t == typeof(TimeSpan)) { i.Kind = Kind.TimeSpan; return i; }
            if (t.IsGenericType)
            {
                var def = t.GetGenericTypeDefinition();
                if (def == typeof(Nullable<>)) { i.Kind = Kind.Nullable; i.Elem = t.GetGenericArguments()[0]; return i; }
                if (def == typeof(KeyValuePair<,>))
                {
                    i.Kind = Kind.KeyValuePair;
                    var a = t.GetGenericArguments();
                    i.Key = a[0]; i.Value = a[1];
                    return i;
                }
                if (t.FullName.StartsWith("System.Tuple`") || t.FullName.StartsWith("System.ValueTuple`"))
                    throw new NotSupportedException("CerasCompat: tuples are not supported (" + t + ")");
            }
            if (t.IsArray)
            {
                i.FormatterConstructed = true;
                if (t.GetArrayRank() != 1) throw new NotSupportedException("CerasCompat: multi-dimensional arrays are not supported (" + t + ")");
                i.Elem = t.GetElementType();
                if (IsBlittable(i.Elem)) { i.Kind = Kind.RawArray; i.Layout = Layout.Of(i.Elem); }
                else i.Kind = Kind.Array;
                return i;
            }
            if (FindClosed(t, typeof(Stack<>)) != null || FindClosed(t, typeof(Queue<>)) != null)
                throw new NotSupportedException("CerasCompat: Stack/Queue are not supported (" + t + ")");
            var coll = FindClosed(t, typeof(ICollection<>));
            if (coll != null)
            {
                i.Kind = Kind.Collection;
                i.Elem = coll.GetGenericArguments()[0];
                var dict = FindClosed(t, typeof(IDictionary<,>));
                if (dict != null && typeof(IDictionary).IsAssignableFrom(t))
                {
                    i.IsDictionary = true;
                    var a = dict.GetGenericArguments();
                    i.Key = a[0]; i.Value = a[1];
                }
                else
                {
                    i.AddMethod = t.GetMethod("Add", BindingFlags.Instance | BindingFlags.Public, null, new[] { i.Elem }, null)
                                  ?? coll.GetMethod("Add");
                }
                if (t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(List<>) || t.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
                    i.FormatterConstructed = true;
                return i;
            }
            if (typeof(MemberInfo).IsAssignableFrom(t) || typeof(Delegate).IsAssignableFrom(t))
                throw new NotSupportedException("CerasCompat: reflection members and delegates are not supported (" + t + ")");
            if (t.IsAbstract || t.IsInterface)
                throw new InvalidOperationException("CerasCompat: no formatter for abstract type " + t);
            if (t.GetInterfaces().Any(x => x.FullName == "Ceras.Resolvers.IExternalRootObject"))
                throw new NotSupportedException("CerasCompat: IExternalRootObject is not supported (" + t + ")");

            i.Kind = Kind.Dynamic;
            BuildMembers(i);
            return i;
        }

        static Type FindClosed(Type t, Type openGeneric)
        {
            for (var x = t; x != null; x = x.BaseType)
                if (x.IsGenericType && x.GetGenericTypeDefinition() == openGeneric) return x;
            if (openGeneric.IsInterface)
                foreach (var it in t.GetInterfaces())
                    if (it.IsGenericType && it.GetGenericTypeDefinition() == openGeneric) return it;
            return null;
        }

        // Ceras ReflectionHelper.IsBlittableType
        static bool IsBlittable(Type t)
        {
            if (!t.IsValueType) return false;
            if (t.IsEnum) return true;
            if (t.IsPointer || t == typeof(IntPtr) || t == typeof(UIntPtr)) return false;
            if (t.IsPrimitive) return true;
            if (t.ContainsGenericParameters) return false;
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>)) return false;
            if (t.IsAutoLayout) return false;
            foreach (var f in InstanceFields(t))
            {
                if (f.GetCustomAttributes(typeof(FixedBufferAttribute), false).Length > 0) continue;
                if (!IsBlittable(f.FieldType)) return false;
            }
            return true;
        }

        const BindingFlags AllFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        static IEnumerable<FieldInfo> InstanceFields(Type t)
        {
            for (var x = t; x != null; x = x.BaseType)
                foreach (var f in x.GetFields(AllFlags))
                    if (f.DeclaringType == x && !f.IsStatic) yield return f;
        }

        static bool HasAttr(MemberInfo m, string fullName, out object attr)
        {
            foreach (var a in m.GetCustomAttributes(true))
                if (a.GetType().FullName == fullName) { attr = a; return true; }
            attr = null;
            return false;
        }
        static bool HasAttr(MemberInfo m, string fullName) => HasAttr(m, fullName, out _);

        static object AttrProp(object attr, string name) =>
            attr.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(attr, null)
            ?? attr.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(attr);

        // Ceras TypeConfig / MemberConfig / TypeConfigDefaults with the default SerializerConfig
        // (DefaultTargets = AllPublic, readonly fields excluded except in structs, [NonSerialized] respected,
        // compiler-generated members skipped), schema sorted by SchemaMemberComparer.
        const int PublicFields = 1, PrivateFields = 2, PublicProperties = 4, PrivateProperties = 8, AllPublic = 5;
        const int RoExclude = 0, RoMembers = 1, RoForced = 2;

        static void BuildMembers(Info i)
        {
            var t = i.Type;
            int targets = AllPublic;
            int? typeReadonly = null;
            if (HasAttr(t, "Ceras.MemberConfigAttribute", out var mca))
            {
                typeReadonly = Convert.ToInt32(AttrProp(mca, "ReadonlyFieldHandling"));
                targets = Convert.ToInt32(AttrProp(mca, "TargetMembers"));
            }
            if (t.IsValueType) typeReadonly = RoForced;

            var list = new List<Member>();
            for (var x = t; x != null; x = x.BaseType)
            {
                foreach (var f in x.GetFields(AllFlags))
                {
                    if (f.DeclaringType != x || f.IsStatic) continue;
                    if (Include(f, f.IsPublic ? PublicFields : PrivateFields, f.IsInitOnly, targets, typeReadonly))
                        list.Add(MakeMember(f, f.FieldType));
                }
                foreach (var p in x.GetProperties(AllFlags))
                {
                    if (p.DeclaringType != x) continue;
                    var get = p.GetGetMethod(true);
                    if (get != null && get.GetParameters().Length != 0) continue;
                    var acc = p.GetAccessors(true);
                    if (acc.Length == 0 || acc[0].IsStatic) continue;
                    if (acc.Length <= 1) continue;                     // computed property
                    if (get == null) continue;
                    if (Include(p, get.IsPublic ? PublicProperties : PrivateProperties, false, targets, typeReadonly))
                        list.Add(MakeMember(p, p.PropertyType));
                }
            }
            list.Sort((a, b) => string.CompareOrdinal(SortKey(a), SortKey(b)));
            i.Members = list.ToArray();
            i.WriteBack = list.Select((m, idx) => (m, idx)).OrderBy(x => x.m.WriteBackOrder).ThenBy(x => x.idx).Select(x => x.m).ToArray();
            foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (m.ReturnType == typeof(void) && m.GetParameters().Length == 0 && HasAttr(m, "Ceras.OnAfterDeserializeAttribute"))
                {
                    i.AfterDeserialize = m;
                    break;
                }
        }

        static Member MakeMember(MemberInfo mi, Type type)
        {
            var m = new Member { Info = mi, Type = type, Name = mi.Name };
            if (HasAttr(mi, "Ceras.PreviousNameAttribute", out var pn)) m.Name = (string)AttrProp(pn, "Name");
            if (HasAttr(mi, "System.Runtime.Serialization.DataMemberAttribute", out var dm)) m.WriteBackOrder = Convert.ToInt32(AttrProp(dm, "Order"));
            return m;
        }

        static bool Include(MemberInfo m, int mask, bool isReadonly, int typeTargets, int? typeReadonly)
        {
            bool compilerGenerated = m.GetCustomAttributes(typeof(CompilerGeneratedAttribute), false).Length > 0 || m.Name.StartsWith("<");
            if (compilerGenerated) return false;
            bool exclude = HasAttr(m, "Ceras.ExcludeAttribute");
            bool include = HasAttr(m, "Ceras.IncludeAttribute");
            bool nonSerialized = m is FieldInfo fi && fi.IsNotSerialized;
            if (exclude) return false;
            if (include) return true;
            if (nonSerialized) return false;
            if (isReadonly)
            {
                int ro;
                if (HasAttr(m, "Ceras.ReadonlyConfigAttribute", out var rca)) ro = Convert.ToInt32(AttrProp(rca, "ReadonlyFieldHandling"));
                else ro = typeReadonly ?? RoExclude;
                if (ro == RoExclude) return false;
            }
            return (typeTargets & mask) != 0;
        }

        static string SortKey(Member m) =>
            m.Type.FullName + m.Name + m.Info.DeclaringType.FullName + (m.Info is FieldInfo ? "f" : "p");

        // ---- reinterpret layout (blittable structs) ------------------------------------------------
        // Ceras' ReinterpretFormatter copies the struct's managed bytes and advances by Marshal.SizeOf.
        // Both are computed here from the field layout so no unsafe code or Marshal support is needed.
        sealed class Layout
        {
            public int ManagedSize, MarshalSize, ManagedAlign, MarshalAlign;
            public Kind Prim;                 // primitive / enum leaf
            public bool IsLeaf;
            public FieldInfo[] Fields;
            public int[] Offsets;             // managed offsets
            public Layout[] FieldLayouts;

            static readonly Dictionary<Type, Layout> s_Cache = new Dictionary<Type, Layout>();

            public static Layout Of(Type t)
            {
                if (s_Cache.TryGetValue(t, out var l)) return l;
                l = Compute(t);
                s_Cache[t] = l;
                return l;
            }

            static Layout Compute(Type t)
            {
                if (t.IsEnum) t = Enum.GetUnderlyingType(t);
                if (s_Prims.TryGetValue(t, out var k))
                {
                    int size = k == Kind.Bool || k == Kind.Byte || k == Kind.SByte ? 1
                             : k == Kind.Char || k == Kind.Int16 || k == Kind.UInt16 ? 2
                             : k == Kind.Int32 || k == Kind.UInt32 || k == Kind.Single ? 4 : 8;
                    int msize = k == Kind.Bool ? 4 : k == Kind.Char ? 1 : size;
                    return new Layout { IsLeaf = true, Prim = k, ManagedSize = size, ManagedAlign = size, MarshalSize = msize, MarshalAlign = msize };
                }
                var sl = t.StructLayoutAttribute;
                int pack = sl != null && sl.Pack > 0 ? sl.Pack : 8;
                if (t.IsExplicitLayout)
                    throw new NotSupportedException("CerasCompat: explicit-layout structs are not supported (" + t + ")");
                var fields = InstanceFields(t).Where(f => f.GetCustomAttributes(typeof(FixedBufferAttribute), false).Length == 0).ToArray();
                var res = new Layout { Fields = fields, Offsets = new int[fields.Length], FieldLayouts = new Layout[fields.Length], ManagedAlign = 1, MarshalAlign = 1 };
                int off = 0, moff = 0;
                for (int n = 0; n < fields.Length; n++)
                {
                    var fl = Of(fields[n].FieldType);
                    res.FieldLayouts[n] = fl;
                    int a = Math.Min(fl.ManagedAlign, pack), ma = Math.Min(fl.MarshalAlign, pack);
                    res.ManagedAlign = Math.Max(res.ManagedAlign, a);
                    res.MarshalAlign = Math.Max(res.MarshalAlign, ma);
                    off = Align(off, a);
                    res.Offsets[n] = off;
                    off += fl.ManagedSize;
                    moff = Align(moff, ma) + fl.MarshalSize;
                }
                int min = sl != null ? sl.Size : 0;
                res.ManagedSize = Math.Max(Align(Math.Max(off, 1), res.ManagedAlign), min);
                res.MarshalSize = Math.Max(Align(Math.Max(moff, 1), res.MarshalAlign), min);
                return res;
            }

            static int Align(int v, int a) => (v + a - 1) / a * a;

            public void Write(byte[] img, int at, object v)
            {
                if (IsLeaf) { WritePrim(img, at, Prim, v); return; }
                for (int n = 0; n < Fields.Length; n++)
                    FieldLayouts[n].Write(img, at + Offsets[n], Fields[n].GetValue(v));
            }

            public object Read(byte[] img, int at, Type t)
            {
                if (IsLeaf)
                {
                    object p = ReadPrim(img, at, Prim);
                    return t.IsEnum ? Enum.ToObject(t, p) : p;
                }
                object box = Activator.CreateInstance(t);
                for (int n = 0; n < Fields.Length; n++)
                    Fields[n].SetValue(box, FieldLayouts[n].Read(img, at + Offsets[n], Fields[n].FieldType));
                return box;
            }

            static void WritePrim(byte[] b, int at, Kind k, object v)
            {
                byte[] src;
                switch (k)
                {
                    case Kind.Bool: b[at] = (bool)v ? (byte)1 : (byte)0; return;
                    case Kind.Byte: b[at] = Convert.ToByte(v); return;
                    case Kind.SByte: b[at] = unchecked((byte)Convert.ToSByte(v)); return;
                    case Kind.Char: src = BitConverter.GetBytes(Convert.ToChar(v)); break;
                    case Kind.Int16: src = BitConverter.GetBytes(Convert.ToInt16(v)); break;
                    case Kind.UInt16: src = BitConverter.GetBytes(Convert.ToUInt16(v)); break;
                    case Kind.Int32: src = BitConverter.GetBytes(Convert.ToInt32(v)); break;
                    case Kind.UInt32: src = BitConverter.GetBytes(Convert.ToUInt32(v)); break;
                    case Kind.Int64: src = BitConverter.GetBytes(Convert.ToInt64(v)); break;
                    case Kind.UInt64: src = BitConverter.GetBytes(Convert.ToUInt64(v)); break;
                    case Kind.Single: src = BitConverter.GetBytes((float)v); break;
                    case Kind.Double: src = BitConverter.GetBytes((double)v); break;
                    default: throw new InvalidOperationException();
                }
                Buffer.BlockCopy(src, 0, b, at, src.Length);
            }

            static object ReadPrim(byte[] b, int at, Kind k)
            {
                switch (k)
                {
                    case Kind.Bool: return b[at] != 0;
                    case Kind.Byte: return b[at];
                    case Kind.SByte: return unchecked((sbyte)b[at]);
                    case Kind.Char: return BitConverter.ToChar(b, at);
                    case Kind.Int16: return BitConverter.ToInt16(b, at);
                    case Kind.UInt16: return BitConverter.ToUInt16(b, at);
                    case Kind.Int32: return BitConverter.ToInt32(b, at);
                    case Kind.UInt32: return BitConverter.ToUInt32(b, at);
                    case Kind.Int64: return BitConverter.ToInt64(b, at);
                    case Kind.UInt64: return BitConverter.ToUInt64(b, at);
                    case Kind.Single: return BitConverter.ToSingle(b, at);
                    case Kind.Double: return BitConverter.ToDouble(b, at);
                    default: throw new InvalidOperationException();
                }
            }
        }

        // Types whose "reference formatter" is the specific formatter itself (no null/reference/type header):
        // value types, and string / Type, which Ceras registers with SetFormatters(type, f, f).
        static bool IsDirect(Type st) => st.IsValueType || st == typeof(string) || typeof(Type).IsAssignableFrom(st);

        // ---- type binder ---------------------------------------------------------------------------
        static readonly Dictionary<string, Type> s_TypeByName = new Dictionary<string, Type>();

        static string BaseName(Type t) => t.IsGenericType ? t.GetGenericTypeDefinition().FullName : t.FullName;

        static Type TypeFromBase(string name)
        {
            lock (s_TypeByName)
            {
                if (s_TypeByName.TryGetValue(name, out var t)) return t;
                t = typeof(object).Assembly.GetType(name) ?? typeof(Uri).Assembly.GetType(name) ?? typeof(Enumerable).Assembly.GetType(name);
                if (t == null)
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try { t = a.GetType(name); } catch { }
                        if (t != null) break;
                    }
                if (t == null) throw new Exception("Cannot find type " + name + " in any loaded assembly");
                s_TypeByName[name] = t;
                return t;
            }
        }

        // ---- writer --------------------------------------------------------------------------------
        sealed class Writer
        {
            byte[] m_Buf = new byte[4096];
            int m_Off;
            readonly Dictionary<object, int> m_Objects = new Dictionary<object, int>(64);
            readonly Dictionary<Type, int> m_Types = new Dictionary<Type, int>();
            static readonly UTF8Encoding s_Utf8 = new UTF8Encoding(false, true);

            public byte[] ToArray()
            {
                var r = new byte[m_Off];
                Buffer.BlockCopy(m_Buf, 0, r, 0, m_Off);
                return r;
            }

            void Ensure(int n)
            {
                if (m_Off + n <= m_Buf.Length) return;
                int size = m_Buf.Length;
                while (size < m_Off + n) size *= 2;
                Array.Resize(ref m_Buf, size);
            }

            void Byte(byte b) { Ensure(1); m_Buf[m_Off++] = b; }
            void Bytes(byte[] b, int count)
            {
                Ensure(count);
                Buffer.BlockCopy(b, 0, m_Buf, m_Off, Math.Min(count, b.Length));
                if (count > b.Length) Array.Clear(m_Buf, m_Off + b.Length, count - b.Length);
                m_Off += count;
            }
            void VarInt(ulong v)
            {
                Ensure(10);
                do
                {
                    ulong n = v & 0x7F;
                    v >>= 7;
                    if (v != 0) n |= 0x80;
                    m_Buf[m_Off++] = (byte)n;
                } while (v != 0);
            }
            void Bias(int v, int bias) => VarInt((ulong)(long)(v + bias));
            void Int32(int v) => VarInt((ulong)(((long)v << 1) ^ ((long)v >> 31)));
            void UInt32(uint v) => VarInt(v);
            void Fixed(byte[] b) => Bytes(b, b.Length);

            void String(string s)
            {
                if (s == null) { Bias(-1, 1); return; }
                var b = s_Utf8.GetBytes(s);
                Bias(b.Length, 1);
                Fixed(b);
            }

            public void Type(System.Type t)
            {
                if (t == null) { Bias(-1, 3); return; }
                if (m_Types.TryGetValue(t, out int id)) { Bias(id, 3); return; }
                if (!t.ContainsGenericParameters && t.IsGenericType)
                {
                    Bias(-2, 3);
                    Type(t.GetGenericTypeDefinition());
                    var args = t.GetGenericArguments();
                    Byte((byte)args.Length);
                    foreach (var a in args) Type(a);
                }
                else
                {
                    Bias(-3, 3);
                    String(BaseName(t));
                }
                m_Types[t] = m_Types.Count;
            }

            // CerasSerializer.GetReferenceFormatter(staticType)
            public void WriteRef(System.Type st, object v)
            {
                if (IsDirect(st))
                {
                    WriteSpecific(GetInfo(st), v);
                    return;
                }
                // ReferenceFormatter<T>
                if (v == null) { Bias(-1, 5); return; }
                var rt = v.GetType();
                if (v is System.Type tv) { Bias(-5, 5); Type(tv); return; }
                if (m_Objects.TryGetValue(v, out int id)) { Bias(id, 5); return; }
                m_Objects.Add(v, m_Objects.Count);
                if (rt == st) Bias(-3, 5);
                else { Bias(-2, 5); Type(rt); }
                WriteSpecific(GetInfo(rt), v);
            }

            void WriteSpecific(Info i, object v)
            {
                switch (i.Kind)
                {
                    case Kind.String: String((string)v); return;
                    case Kind.TypeRef: Type((System.Type)v); return;
                    case Kind.Enum: WritePrim(i.Prim, v); return;
                    case Kind.Raw:
                    {
                        var img = new byte[i.Layout.ManagedSize];
                        i.Layout.Write(img, 0, v);
                        Bytes(img, i.Layout.MarshalSize);
                        return;
                    }
                    case Kind.DateTime: Fixed(BitConverter.GetBytes(((DateTime)v).ToBinary())); return;
                    case Kind.DateTimeOffset:
                    {
                        var d = (DateTimeOffset)v;
                        Fixed(BitConverter.GetBytes(d.Ticks));
                        Fixed(BitConverter.GetBytes((short)d.Offset.TotalMinutes));
                        return;
                    }
                    case Kind.TimeSpan: Fixed(BitConverter.GetBytes(((TimeSpan)v).Ticks)); return;
                    case Kind.Nullable:
                        // a boxed Nullable<T> is either null or a boxed T
                        if (v == null) Byte(0);
                        else { Byte(1); WriteSpecific(GetInfo(i.Elem), v); }
                        return;
                    case Kind.KeyValuePair:
                        WriteRef(i.Key, i.Type.GetProperty("Key").GetValue(v, null));
                        WriteRef(i.Value, i.Type.GetProperty("Value").GetValue(v, null));
                        return;
                    case Kind.RawArray:
                    {
                        var a = (Array)v;
                        int n = a.Length;
                        UInt32((uint)n);
                        var img = new byte[n * i.Layout.ManagedSize];
                        for (int k = 0; k < n; k++) i.Layout.Write(img, k * i.Layout.ManagedSize, a.GetValue(k));
                        Bytes(img, n * i.Layout.MarshalSize);
                        return;
                    }
                    case Kind.Array:
                    {
                        var a = (Array)v;
                        if (a == null) { Bias(-1, 1); return; }
                        Bias(a.Length, 1);
                        for (int k = 0; k < a.Length; k++) WriteRef(i.Elem, a.GetValue(k));
                        return;
                    }
                    case Kind.Collection:
                    {
                        if (i.IsDictionary)
                        {
                            var d = (IDictionary)v;
                            UInt32((uint)d.Count);
                            var e = d.GetEnumerator();
                            var elem = GetInfo(i.Elem);
                            while (e.MoveNext())
                            {
                                if (elem.Kind == Kind.Raw) WriteSpecific(elem, e.Current); // blittable KeyValuePair
                                else { WriteRef(i.Key, e.Key); WriteRef(i.Value, e.Value); }
                            }
                        }
                        else
                        {
                            var items = new List<object>();
                            foreach (var x in (IEnumerable)v) items.Add(x);
                            UInt32((uint)items.Count);
                            foreach (var x in items) WriteRef(i.Elem, x);
                        }
                        return;
                    }
                    case Kind.Dynamic:
                        foreach (var m in i.Members) WriteRef(m.Type, m.Get(v));
                        return;
                    default:
                        WritePrim(i.Kind, v);
                        return;
                }
            }

            void WritePrim(Kind k, object v)
            {
                switch (k)
                {
                    case Kind.Bool: Int32((bool)v ? 1 : 0); return;
                    case Kind.Byte: Byte(Convert.ToByte(v)); return;
                    case Kind.SByte: Byte(unchecked((byte)Convert.ToSByte(v))); return;
                    case Kind.Char: Fixed(BitConverter.GetBytes(unchecked((short)Convert.ToChar(v)))); return;
                    case Kind.Int16: Fixed(BitConverter.GetBytes(Convert.ToInt16(v))); return;
                    case Kind.UInt16: Fixed(BitConverter.GetBytes(Convert.ToUInt16(v))); return;
                    case Kind.Int32: Int32(Convert.ToInt32(v)); return;
                    case Kind.UInt32: UInt32(Convert.ToUInt32(v)); return;
                    case Kind.Int64: Fixed(BitConverter.GetBytes(Convert.ToInt64(v))); return;
                    case Kind.UInt64: Fixed(BitConverter.GetBytes(Convert.ToUInt64(v))); return;
                    case Kind.Single: Fixed(BitConverter.GetBytes((float)v)); return;
                    case Kind.Double: Fixed(BitConverter.GetBytes((double)v)); return;
                    default: throw new InvalidOperationException("CerasCompat: unexpected primitive kind " + k);
                }
            }
        }

        // ---- reader --------------------------------------------------------------------------------
        sealed class Reader
        {
            readonly byte[] m_Buf;
            public int Offset;
            readonly List<object> m_Objects = new List<object>(64);
            readonly List<Type> m_Types = new List<Type>();
            static readonly UTF8Encoding s_Utf8 = new UTF8Encoding(false, true);

            public Reader(byte[] b) { m_Buf = b; }

            byte Byte() => m_Buf[Offset++];
            byte[] Bytes(int n)
            {
                if (Offset + n > m_Buf.Length) throw new IndexOutOfRangeException("CerasCompat: data ends early");
                var r = new byte[n];
                Buffer.BlockCopy(m_Buf, Offset, r, 0, n);
                Offset += n;
                return r;
            }
            ulong VarInt(int bits)
            {
                int shift = 0;
                ulong result = 0;
                while (true)
                {
                    long b = m_Buf[Offset++];
                    result |= (ulong)(b & 0x7F) << shift;
                    if (shift > bits) throw new ArgumentOutOfRangeException("bytes", "Malformed VarInt");
                    if ((b & 0x80) != 128) break;
                    shift += 7;
                }
                return result;
            }
            int Bias(int bias) => (int)VarInt(32) - bias;
            int Int32()
            {
                ulong v = VarInt(32);
                return (int)((v & 1) == 1 ? -1L * (long)((v >> 1) + 1) : (long)(v >> 1));
            }
            uint UInt32() => (uint)VarInt(32);

            string String()
            {
                int n = Bias(1);
                if (n == -1) return null;
                var s = s_Utf8.GetString(m_Buf, Offset, n);
                Offset += n;
                return s;
            }

            System.Type Type()
            {
                int n = Bias(3);
                if (n == -1) return null;
                if (n >= 0) return m_Types[n];
                if (n == -2)
                {
                    var def = Type();
                    int count = Byte();
                    var args = new System.Type[count];
                    for (int k = 0; k < count; k++) args[k] = Type();
                    int slot = m_Types.Count;
                    m_Types.Add(null);
                    var t = TypeFromBase(def.FullName).MakeGenericType(args);
                    m_Types[slot] = t;
                    return t;
                }
                {
                    int slot = m_Types.Count;
                    m_Types.Add(null);
                    var t = TypeFromBase(String());
                    m_Types[slot] = t;
                    return t;
                }
            }

            public object ReadRef(System.Type st, object existing)
            {
                if (IsDirect(st))
                    return ReadSpecific(GetInfo(st), existing);
                int n = Bias(5);
                if (n == -1) return null;
                if (n == -5) return Type();
                if (n >= 0) return m_Objects[n];
                if (n == -4) throw new NotSupportedException("CerasCompat: external objects are not supported");
                var rt = n == -2 ? Type() : st;
                var ri = GetInfo(rt);
                object value = null;
                if (!rt.IsValueType)
                {
                    if (existing != null && existing.GetType() == rt) value = existing;
                    else if (!ri.FormatterConstructed) value = Construct(rt);
                }
                int slot = m_Objects.Count;
                m_Objects.Add(value);
                value = ReadSpecific(ri, value);
                m_Objects[slot] = value;
                return value;
            }

            static object Construct(System.Type t)
            {
                var ctor = t.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, System.Type.EmptyTypes, null);
                if (ctor != null) return ctor.Invoke(null);
                return FormatterServices.GetUninitializedObject(t);
            }

            object ReadSpecific(Info i, object existing)
            {
                switch (i.Kind)
                {
                    case Kind.String: return String();
                    case Kind.TypeRef: return Type();
                    case Kind.Enum: return Enum.ToObject(i.Type, ReadPrim(i.Prim));
                    case Kind.Raw:
                    {
                        var raw = Bytes(i.Layout.MarshalSize);
                        var img = new byte[i.Layout.ManagedSize];
                        Buffer.BlockCopy(raw, 0, img, 0, Math.Min(raw.Length, img.Length));
                        return i.Layout.Read(img, 0, i.Type);
                    }
                    case Kind.DateTime: return DateTime.FromBinary(BitConverter.ToInt64(Bytes(8), 0));
                    case Kind.DateTimeOffset:
                    {
                        long ticks = BitConverter.ToInt64(Bytes(8), 0);
                        short min = BitConverter.ToInt16(Bytes(2), 0);
                        return new DateTimeOffset(ticks, TimeSpan.FromMinutes(min));
                    }
                    case Kind.TimeSpan: return new TimeSpan(BitConverter.ToInt64(Bytes(8), 0));
                    case Kind.Nullable:
                        return Byte() != 0 ? ReadSpecific(GetInfo(i.Elem), null) : null;
                    case Kind.KeyValuePair:
                    {
                        object k = ReadRef(i.Key, null);
                        object v = ReadRef(i.Value, null);
                        return Activator.CreateInstance(i.Type, k, v);
                    }
                    case Kind.RawArray:
                    {
                        int n = (int)UInt32();
                        var a = existing is Array ea && ea.Length == n && ea.GetType() == i.Type ? ea : System.Array.CreateInstance(i.Elem, n);
                        if (n == 0) return a;
                        var raw = Bytes(n * i.Layout.MarshalSize);
                        var img = new byte[n * i.Layout.ManagedSize];
                        Buffer.BlockCopy(raw, 0, img, 0, Math.Min(raw.Length, img.Length));
                        for (int k = 0; k < n; k++) a.SetValue(i.Layout.Read(img, k * i.Layout.ManagedSize, i.Elem), k);
                        return a;
                    }
                    case Kind.Array:
                    {
                        int n = Bias(1);
                        if (n == -1) return null;
                        var a = existing is Array ea && ea.Length == n && ea.GetType() == i.Type ? ea : System.Array.CreateInstance(i.Elem, n);
                        for (int k = 0; k < n; k++) a.SetValue(ReadRef(i.Elem, a.GetValue(k)), k);
                        return a;
                    }
                    case Kind.Collection:
                    {
                        uint n = UInt32();
                        object c = existing ?? Construct(i.Type);
                        if (i.IsDictionary)
                        {
                            var d = (IDictionary)c;
                            if (d.Count > 0) d.Clear();
                            var elem = GetInfo(i.Elem);
                            for (uint k = 0; k < n; k++)
                            {
                                if (elem.Kind == Kind.Raw)
                                {
                                    object kvp = ReadSpecific(elem, null);
                                    d.Add(elem.Type.GetProperty("Key").GetValue(kvp, null), elem.Type.GetProperty("Value").GetValue(kvp, null));
                                }
                                else
                                {
                                    object key = ReadRef(i.Key, null);
                                    object val = ReadRef(i.Value, null);
                                    d.Add(key, val);
                                }
                            }
                        }
                        else
                        {
                            if (c is IList l) { if (l.Count > 0) l.Clear(); }
                            else c.GetType().GetMethod("Clear", System.Type.EmptyTypes)?.Invoke(c, null);
                            var args = new object[1];
                            for (uint k = 0; k < n; k++)
                            {
                                args[0] = ReadRef(i.Elem, null);
                                i.AddMethod.Invoke(c, args);
                            }
                        }
                        return c;
                    }
                    case Kind.Dynamic:
                    {
                        object obj = existing;
                        if (obj == null) obj = i.Type.IsValueType ? Activator.CreateInstance(i.Type) : Construct(i.Type);
                        var locals = new object[i.Members.Length];
                        for (int k = 0; k < i.Members.Length; k++)
                            locals[k] = ReadRef(i.Members[k].Type, i.Members[k].Get(obj));
                        foreach (var m in i.WriteBack)
                            m.Set(obj, locals[System.Array.IndexOf(i.Members, m)]);
                        i.AfterDeserialize?.Invoke(obj, null);
                        return obj;
                    }
                    default:
                        return ReadPrim(i.Kind);
                }
            }

            object ReadPrim(Kind k)
            {
                switch (k)
                {
                    case Kind.Bool: return Int32() != 0;
                    case Kind.Byte: return Byte();
                    case Kind.SByte: return unchecked((sbyte)Byte());
                    case Kind.Char: return (char)BitConverter.ToInt16(Bytes(2), 0);
                    case Kind.Int16: return BitConverter.ToInt16(Bytes(2), 0);
                    case Kind.UInt16: return BitConverter.ToUInt16(Bytes(2), 0);
                    case Kind.Int32: return Int32();
                    case Kind.UInt32: return UInt32();
                    case Kind.Int64: return BitConverter.ToInt64(Bytes(8), 0);
                    case Kind.UInt64: return BitConverter.ToUInt64(Bytes(8), 0);
                    case Kind.Single: return BitConverter.ToSingle(Bytes(4), 0);
                    case Kind.Double: return BitConverter.ToDouble(Bytes(8), 0);
                    default: throw new InvalidOperationException("CerasCompat: unexpected primitive kind " + k);
                }
            }
        }
    }
}
