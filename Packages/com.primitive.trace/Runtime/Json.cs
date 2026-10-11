// Copied from fody-dotnet-trace/src/Primitive.Trace/Json.cs by tools/sync-unity.ps1; edit the original.
using System;
using System.Collections;
using System.Globalization;
using System.Text;

namespace Primitive.Trace
{
    /// <summary>Minimal JSON rendering for trace lines (no dependencies, so it also runs inside Unity).</summary>
    internal static class Json
    {
        public const int MaxString = 120;

        public static void String(StringBuilder sb, string s, int max = int.MaxValue)
        {
            if (s == null)
            {
                sb.Append("null");
                return;
            }
            sb.Append('"');
            int n = Math.Min(s.Length, max);
            for (int i = 0; i < n; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            if (n < s.Length) sb.Append('…');
            sb.Append('"');
        }

        /// <summary>
        /// A value as the primitive-trace format writes it: JSON scalars (strings cut to 120 characters),
        /// {"ref":id} for an object the trace knows, otherwise {"repr","type"} — never by calling ToString(),
        /// which could run arbitrary code (or native code, in Unity).
        /// </summary>
        public static void Value(StringBuilder sb, object v, Func<object, int> knownId)
        {
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string s: String(sb, s, MaxString); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case char c: String(sb, c.ToString()); return;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case short sh: sb.Append(sh.ToString(CultureInfo.InvariantCulture)); return;
                case byte by: sb.Append(by.ToString(CultureInfo.InvariantCulture)); return;
                case sbyte sb8: sb.Append(sb8.ToString(CultureInfo.InvariantCulture)); return;
                case ushort us: sb.Append(us.ToString(CultureInfo.InvariantCulture)); return;
                case uint ui: sb.Append(ui.ToString(CultureInfo.InvariantCulture)); return;
                case ulong ul: sb.Append(ul.ToString(CultureInfo.InvariantCulture)); return;
                case float f: Number(sb, f); return;
                case double d: Number(sb, d); return;
                case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); return;
                case Enum e: String(sb, e.GetType().Name + "." + Enum.GetName(e.GetType(), e)); return;
            }
            int id = knownId(v);
            if (id > 0)
            {
                sb.Append("{\"ref\":").Append(id).Append('}');
                return;
            }
            Type t = v.GetType();
            string repr = TypeNames.Display(t);
            if (v is ICollection col && !IsUnityObject(t))
            {
                try
                {
                    repr += "[" + col.Count + "]";
                }
                catch (Exception)
                {
                    // a collection that cannot count right now
                }
            }
            sb.Append("{\"repr\":");
            String(sb, repr, MaxString);
            sb.Append(",\"type\":");
            String(sb, TypeNames.Display(t), MaxString);
            sb.Append('}');
        }

        static void Number(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) String(sb, d.ToString(CultureInfo.InvariantCulture));
            else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        static bool IsUnityObject(Type t)
        {
            for (Type at = t; at != null; at = at.BaseType)
                if (at.FullName == "UnityEngine.Object")
                    return true;
            return false;
        }
    }

    /// <summary>Type names as the trace shows them: namespace apart, nested types joined by '.', generic arity dropped.</summary>
    public static class TypeNames
    {
        /// <summary>"Outer.Inner" for Namespace.Outer+Inner`1.</summary>
        public static string Display(Type t)
        {
            if (t.IsArray) return Display(t.GetElementType()) + "[]";
            string name = Strip(t.Name);
            if (t.IsGenericType)
            {
                Type[] args = t.GetGenericArguments();
                var parts = new string[args.Length];
                for (int i = 0; i < args.Length; i++) parts[i] = args[i].IsGenericParameter ? args[i].Name : Display(args[i]);
                name += "<" + string.Join(",", parts) + ">";
            }
            return t.DeclaringType != null && !t.IsGenericParameter ? Display(t.DeclaringType) + "." + name : name;
        }

        /// <summary>The class name without generic arguments: "Outer.Inner".</summary>
        public static string Plain(Type t) => t.DeclaringType != null ? Plain(t.DeclaringType) + "." + Strip(t.Name) : Strip(t.Name);

        static string Strip(string name)
        {
            int tick = name.IndexOf('`');
            return tick > 0 ? name.Substring(0, tick) : name;
        }
    }
}
