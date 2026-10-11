using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Unity.CompilationPipeline.Common.Diagnostics;
using Unity.CompilationPipeline.Common.ILPostProcessing;

namespace Primitive.Trace.Weaving
{
    /// <summary>
    /// Instruments the project's own assemblies for Primitive.Trace while the PRIMITIVE_TRACE scripting define is set
    /// (Primitive > Trace > Trace Play Mode Startup). This replaces the old unity-weaver, which rewrote
    /// Library/ScriptAssemblies after compilation: an IL post-processor is Unity's supported way (it runs inside the
    /// compilation pipeline, on every compile, with the PDB, so file and line information is kept). Fody cannot run here,
    /// as Unity compiles with its own pipeline on Mono.
    ///
    /// Which assemblies: Assembly-CSharp and Primitive.Viewer, or those listed in ProjectSettings/PrimitiveTrace.json
    /// ({"assemblies": [..], "exclude": ["Namespace.Type", "Namespace.Type.Method", ..]}).
    /// </summary>
    public sealed class TracePostProcessor : ILPostProcessor
    {
        public const string Define = "PRIMITIVE_TRACE";
        static readonly string[] DefaultAssemblies = { "Assembly-CSharp", "Primitive.Viewer" };

        public override ILPostProcessor GetInstance() => this;

        public override bool WillProcess(ICompiledAssembly compiledAssembly)
        {
            if (compiledAssembly.Defines == null || !compiledAssembly.Defines.Contains(Define)) return false;
            if (compiledAssembly.Name == TraceWeaver.RuntimeAssembly) return false;
            return Settings(compiledAssembly).Assemblies.Contains(compiledAssembly.Name);
        }

        public override ILPostProcessResult Process(ICompiledAssembly compiledAssembly)
        {
            var diagnostics = new List<DiagnosticMessage>();
            var (_, exclude) = Settings(compiledAssembly);
            using (var resolver = new ReferenceResolver(compiledAssembly.References))
            using (AssemblyDefinition assembly = Read(compiledAssembly, resolver))
            {
                var options = new WeaveOptions
                {
                    // Unity builds assembly definitions as version 0.0.0.0
                    RuntimeVersion = new Version(0, 0, 0, 0),
                    SkipMethod = m => m.IsConstructor && IsUnityObject(m.DeclaringType),
                    Log = s => { },
                };
                options.Exclude.AddRange(exclude);
                int n = TraceWeaver.Weave(assembly.MainModule, options);
                if (n == 0) return new ILPostProcessResult(null, diagnostics);
                diagnostics.Add(new DiagnosticMessage
                {
                    DiagnosticType = DiagnosticType.Warning,
                    MessageData = $"Primitive Trace: {n} methods of {compiledAssembly.Name} instrumented ({Define} is set; Primitive > Trace to turn it off)",
                });
                return new ILPostProcessResult(Write(assembly), diagnostics);
            }
        }

        /// <summary>
        /// MonoBehaviour and ScriptableObject constructors run whenever the editor reloads or serializes; the original
        /// weaver skipped them too.
        /// </summary>
        static bool IsUnityObject(TypeDefinition type)
        {
            try
            {
                for (TypeReference t = type.BaseType; t != null; t = t.Resolve()?.BaseType)
                    if (t.FullName == "UnityEngine.Object")
                        return true;
            }
            catch (Exception)
            {
                // an unresolvable base: assume not
            }
            return false;
        }

        static (List<string> Assemblies, List<string> Exclude) Settings(ICompiledAssembly compiled)
        {
            var assemblies = new List<string>(DefaultAssemblies);
            var exclude = new List<string>();
            string file = Path.Combine(ProjectRoot(compiled), "ProjectSettings", "PrimitiveTrace.json");
            if (!File.Exists(file)) return (assemblies, exclude);
            try
            {
                string json = File.ReadAllText(file);
                List<string> Array(string key)
                {
                    Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
                    return m.Success ? Regex.Matches(m.Groups[1].Value, "\"((?:[^\"\\\\]|\\\\.)*)\"").Cast<Match>().Select(x => x.Groups[1].Value).ToList() : null;
                }
                List<string> listed = Array("assemblies");
                if (listed != null && listed.Count > 0) assemblies = listed;
                exclude = Array("exclude") ?? exclude;
            }
            catch (IOException)
            {
            }
            return (assemblies, exclude);
        }

        /// <summary>The project folder: the references include its Library, and the post-processor may run elsewhere.</summary>
        static string ProjectRoot(ICompiledAssembly compiled)
        {
            foreach (string r in compiled.References)
            {
                string p = r.Replace('\\', '/');
                int library = p.IndexOf("/Library/", StringComparison.Ordinal);
                if (library > 0) return p.Substring(0, library);
            }
            return Environment.CurrentDirectory;
        }

        static AssemblyDefinition Read(ICompiledAssembly compiled, IAssemblyResolver resolver)
        {
            byte[] pdb = compiled.InMemoryAssembly.PdbData;
            bool symbols = pdb != null && pdb.Length > 0;
            return AssemblyDefinition.ReadAssembly(new MemoryStream(compiled.InMemoryAssembly.PeData), new ReaderParameters
            {
                AssemblyResolver = resolver,
                ReadingMode = ReadingMode.Immediate,
                ReadWrite = true,
                ReadSymbols = symbols,
                SymbolReaderProvider = symbols ? new PortablePdbReaderProvider() : null,
                SymbolStream = symbols ? new MemoryStream(pdb) : null,
            });
        }

        static InMemoryAssembly Write(AssemblyDefinition assembly)
        {
            var pe = new MemoryStream();
            var pdb = new MemoryStream();
            assembly.Write(pe, new WriterParameters
            {
                SymbolWriterProvider = new PortablePdbWriterProvider(), WriteSymbols = true, SymbolStream = pdb,
            });
            return new InMemoryAssembly(pe.ToArray(), pdb.ToArray());
        }

        /// <summary>Resolves references from the compiled assembly's reference paths (absolute, given by the pipeline).</summary>
        sealed class ReferenceResolver : IAssemblyResolver
        {
            readonly string[] paths;
            readonly Dictionary<string, AssemblyDefinition> cache = new Dictionary<string, AssemblyDefinition>();

            public ReferenceResolver(string[] paths) => this.paths = paths;

            public AssemblyDefinition Resolve(AssemblyNameReference name) => Resolve(name, new ReaderParameters(ReadingMode.Deferred));

            public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
            {
                lock (cache)
                {
                    if (cache.TryGetValue(name.Name, out AssemblyDefinition cached)) return cached;
                    string path = paths.FirstOrDefault(r => Path.GetFileNameWithoutExtension(r) == name.Name);
                    if (path == null || !File.Exists(path)) return null;
                    parameters.AssemblyResolver = this;
                    AssemblyDefinition definition = AssemblyDefinition.ReadAssembly(path, parameters);
                    cache[name.Name] = definition;
                    return definition;
                }
            }

            public void Dispose()
            {
                lock (cache)
                {
                    foreach (AssemblyDefinition d in cache.Values) d.Dispose();
                    cache.Clear();
                }
            }
        }
    }
}
