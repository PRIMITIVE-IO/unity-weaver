// Copied from fody-dotnet-trace/src/Primitive.Trace/Site.cs by tools/sync-unity.ps1; edit the original.
using System;

namespace Primitive.Trace
{
    /// <summary>
    /// What the weaver knows about one instrumented method, passed as a single interned string literal so a call
    /// costs no allocation: fields separated by U+001F — version, flags (c constructor, s static), name
    /// ("Outer.Inner.Method"), module (namespace), source file, definition line, argument names (comma separated),
    /// declaring type's full name, the file declaring that type.
    /// </summary>
    internal sealed class Site
    {
        public const char Separator = '\u001f';

        public readonly string Name, Module, File, TypeName, TypeFile;
        public readonly int Line;
        public readonly string[] ArgNames;
        public readonly bool IsConstructor, IsStatic;

        // the original ActiveRepetitions, per method: calls in the current window of HotWindow calls
        public int WindowCalls, Window = -1;
        public long Suppressed;

        /// <summary>The call's "fn" object, rendered once.</summary>
        public readonly string FnJson;

        public Site(string text, Func<string, string> relative)
        {
            string[] f = text.Split(Separator);
            string Field(int i) => i < f.Length ? f[i] : "";
            string flags = Field(1);
            IsConstructor = flags.IndexOf('c') >= 0;
            IsStatic = flags.IndexOf('s') >= 0;
            Name = Field(2);
            Module = Field(3);
            File = relative(Field(4));
            int.TryParse(Field(5), out Line);
            string args = Field(6);
            ArgNames = args.Length == 0 ? Array.Empty<string>() : args.Split(',');
            TypeName = Field(7);
            TypeFile = relative(Field(8));
            var sb = new System.Text.StringBuilder(96);
            sb.Append("{\"file\":");
            Json.String(sb, File);
            sb.Append(",\"line\":").Append(Line).Append(",\"name\":");
            Json.String(sb, Name);
            sb.Append(",\"module\":");
            Json.String(sb, Module);
            sb.Append('}');
            FnJson = sb.ToString();
        }
    }
}
