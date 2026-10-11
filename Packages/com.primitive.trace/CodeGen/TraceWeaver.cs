// Copied from fody-dotnet-trace/src/Primitive.Trace.Weaving/TraceWeaver.cs by tools/sync-unity.ps1; edit the original.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace Primitive.Trace.Weaving
{
    /// <summary>What to instrument, and how the host wants to hear about it.</summary>
    public sealed class WeaveOptions
    {
        /// <summary>Type patterns ("Namespace.Type", * wildcards) to instrument; empty: every type.</summary>
        public List<string> Include { get; } = new List<string>();

        /// <summary>Type or method patterns ("Namespace.Type" or "Namespace.Type.Method") to leave alone.</summary>
        public List<string> Exclude { get; } = new List<string>();

        /// <summary>
        /// Source files are recorded relative to this folder (a Fody build's repository), so a trace written without a
        /// root still lines up with the codemap; otherwise as the PDB has them and the runtime makes them relative.
        /// </summary>
        public string SourceRoot { get; set; }

        /// <summary>Version of the Primitive.Trace reference added to woven modules (Unity builds it as 0.0.0.0).</summary>
        public Version RuntimeVersion { get; set; } = new Version(1, 0, 0, 0);

        /// <summary>Further per-host rules (the Unity host skips MonoBehaviour constructors, which run on every reload).</summary>
        public Func<MethodDefinition, bool> SkipMethod { get; set; }

        public Action<string> Log { get; set; }
    }

    /// <summary>
    /// Instruments a module for Primitive.Trace. Each method becomes
    /// <code>
    /// frame = Tracer.Enter(site, this, new object[] { args });
    /// try { body }                                 // returns become: result = …; leave exit
    /// catch (Exception e) { Tracer.Raise(frame, e); throw; }
    /// exit: Tracer.Return(frame, result) / Tracer.Exit(frame); return result;
    /// </code>
    /// where <c>site</c> is one string literal carrying the method's name, namespace, source file and line (from the
    /// PDB, so the proxy can link each call to the codemap) and argument names. Constructors call Enter before their
    /// base constructor (so the object's real type is known when it is born) and protect only what follows it.
    /// Compiler-generated code (lambdas, local functions, async and iterator state machines) is named after the
    /// method it belongs to, and records the object that method runs on. Value types, auto-properties, static
    /// constructors and methods of the tracer itself are left alone.
    /// </summary>
    public static class TraceWeaver
    {
        public const string RuntimeAssembly = "Primitive.Trace";
        const string MarkerType = "<PrimitiveTraceWoven>";

        sealed class Refs
        {
            public MethodReference Enter, Exit, Return, Raise;
            public TypeReference Exception, Object;
        }

        /// <summary>Weave a module (read with its symbols); returns the number of methods instrumented, 0 when it was woven already.</summary>
        public static int Weave(ModuleDefinition module, WeaveOptions options)
        {
            if (module.Types.Any(t => t.Name == MarkerType) || module.Assembly?.Name.Name == RuntimeAssembly) return 0;
            Refs refs = References(module, options.RuntimeVersion);
            var include = options.Include.Select(Glob).ToList();
            var exclude = options.Exclude.Select(Glob).ToList();
            var typeFiles = new Dictionary<TypeDefinition, string>();
            int count = 0;
            foreach (TypeDefinition type in module.GetTypes().ToList())
            {
                if (!ShouldWeave(type, include, exclude)) continue;
                foreach (MethodDefinition method in type.Methods.ToList())
                {
                    if (!ShouldWeave(method, exclude, options)) continue;
                    try
                    {
                        Instrument(method, refs, Site(method, typeFiles, options.SourceRoot));
                        count++;
                    }
                    catch (Exception e)
                    {
                        options.Log?.Invoke($"skipped {method.FullName}: {e.Message}");
                    }
                }
            }
            // a marker, so the same assembly is never woven twice
            module.Types.Add(new TypeDefinition("", MarkerType, TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object));
            options.Log?.Invoke($"{module.Name}: {count} methods instrumented");
            return count;
        }

        static Refs References(ModuleDefinition module, Version version)
        {
            AssemblyNameReference runtime = module.AssemblyReferences.FirstOrDefault(a => a.Name == RuntimeAssembly);
            if (runtime == null)
            {
                runtime = new AssemblyNameReference(RuntimeAssembly, version);
                module.AssemblyReferences.Add(runtime);
            }
            var tracer = new TypeReference("Primitive.Trace", "Tracer", module, runtime);
            TypeSystem ts = module.TypeSystem;
            var exception = new TypeReference("System", "Exception", module, ts.CoreLibrary);
            MethodReference Method(string name, TypeReference returns, params TypeReference[] parameters)
            {
                var m = new MethodReference(name, returns, tracer) { HasThis = false };
                foreach (TypeReference p in parameters) m.Parameters.Add(new ParameterDefinition(p));
                return m;
            }
            return new Refs
            {
                Enter = Method("Enter", ts.Int32, ts.String, ts.Object, new ArrayType(ts.Object)),
                Exit = Method("Exit", ts.Void, ts.Int32),
                Return = Method("Return", ts.Void, ts.Int32, ts.Object),
                Raise = Method("Raise", ts.Void, ts.Int32, exception),
                Exception = exception,
                Object = ts.Object,
            };
        }

        // ------------------------------------------------------------------ what to weave

        static Regex Glob(string pattern) =>
            new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        static bool IsCompilerGenerated(TypeDefinition t) => t.Name.StartsWith("<", StringComparison.Ordinal);

        /// <summary>The user type a (possibly compiler-generated, nested) type belongs to.</summary>
        static TypeDefinition UserType(TypeDefinition t)
        {
            while (t.DeclaringType != null && IsCompilerGenerated(t)) t = t.DeclaringType;
            return t;
        }

        static string FullName(TypeDefinition t) => t.FullName.Replace('/', '.');

        static bool ShouldWeave(TypeDefinition type, List<Regex> include, List<Regex> exclude)
        {
            if (type.IsInterface || type.Name == "<Module>" || type.Name == MarkerType) return false;
            TypeDefinition user = UserType(type);
            if (user.IsValueType || user.Namespace.StartsWith("Primitive.Trace", StringComparison.Ordinal)) return false;
            // struct state machines (release builds) are fine; other structs are not
            if (type.IsValueType && !IsCompilerGenerated(type)) return false;
            if (type.CustomAttributes.Any(a => a.AttributeType.Name == "PrimitiveNoTraceAttribute")) return false;
            string name = FullName(user);
            if (include.Count > 0 && !include.Any(r => r.IsMatch(name))) return false;
            return !exclude.Any(r => r.IsMatch(name));
        }

        static bool ShouldWeave(MethodDefinition method, List<Regex> exclude, WeaveOptions options)
        {
            if (!method.HasBody || method.IsAbstract || method.IsPInvokeImpl || method.IsRuntime) return false;
            if (method.IsConstructor && method.IsStatic) return false;
            // the entry point the compiler writes around an async Main
            if (method.Name == "<Main>") return false;
            if (method.CustomAttributes.Any(a => a.AttributeType.Name is "PrimitiveNoTraceAttribute"
                    // the stub that starts a state machine: its MoveNext is traced, under the stub's name
                    or "AsyncStateMachineAttribute" or "IteratorStateMachineAttribute" or "AsyncIteratorStateMachineAttribute"))
                return false;
            // auto-properties and other compiler-written accessors
            if ((method.IsGetter || method.IsSetter || method.IsAddOn || method.IsRemoveOn) &&
                method.CustomAttributes.Any(a => a.AttributeType.Name == "CompilerGeneratedAttribute"))
                return false;
            TypeDefinition type = method.DeclaringType;
            // in compiler-generated types only the code a person wrote: state machine steps, lambdas, local functions
            if (IsCompilerGenerated(type) && !(method.Name == "MoveNext" || method.Name.StartsWith("<", StringComparison.Ordinal)))
                return false;
            if (method.Body.Instructions.Any(i => i.OpCode.Code is Code.Jmp or Code.Tail or Code.Arglist or Code.Localloc)) return false;
            if (method.ReturnType.IsByReference || IsByRefLike(method.ReturnType)) return false;
            if (options.SkipMethod != null && options.SkipMethod(method)) return false;
            if (IsGenerated(method)) return false;
            string qualified = FullName(UserType(type)) + "." + method.Name;
            return !exclude.Any(r => r.IsMatch(qualified));
        }

        /// <summary>
        /// Code a source generator wrote (Unity's MonoScript tables, *.g.cs, *.generated.cs, anything under a build folder):
        /// nobody reads it in the codemap, and Unity runs it while reloading scripts.
        /// </summary>
        static bool IsGenerated(MethodDefinition method)
        {
            if (UserType(method.DeclaringType).Name.StartsWith("UnitySourceGenerated", StringComparison.Ordinal)) return true;
            string file = FirstPoint(method)?.Document.Url;
            if (string.IsNullOrEmpty(file)) return false;
            file = file.Replace('\\', '/');
            return file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
                   file.Contains("/Library/Bee/") || file.Contains("/obj/");
        }

        static bool IsByRefLike(TypeReference t)
        {
            if (!t.IsValueType || t.IsPrimitive) return false;
            try
            {
                TypeDefinition def = t.Resolve();
                return def == null || def.CustomAttributes.Any(a => a.AttributeType.Name == "IsByRefLikeAttribute");
            }
            catch (Exception)
            {
                return true; // cannot tell: do not box it
            }
        }

        // ------------------------------------------------------------------ the site literal

        /// <summary>"Outer.Inner.Method" for any method, compiler-generated ones named after the method they belong to.</summary>
        public static string DisplayName(MethodDefinition method)
        {
            TypeDefinition type = method.DeclaringType;
            MethodDefinition kickoff = method.DebugInformation?.StateMachineKickOffMethod;
            string name;
            if (kickoff != null)
            {
                type = kickoff.DeclaringType;
                name = kickoff.IsConstructor ? Plain(type.Name) : kickoff.Name;
            }
            else if (IsCompilerGenerated(type) && method.Name == "MoveNext")
                name = Between(type.Name); // <Parse>d__3
            else if (method.Name == "<Main>$")
                name = "Main"; // top-level statements
            else if (method.Name.StartsWith("<", StringComparison.Ordinal))
            {
                // <Main>b__0_0 is a lambda in Main; <Main>g__Local|0_0 is Main's local function Local
                int g = method.Name.IndexOf(">g__", StringComparison.Ordinal);
                string outer = Between(method.Name);
                // in a constructor or a static initializer: named after the class, as constructors are
                if (outer is ".ctor" or ".cctor" || outer.Length == 0) outer = Plain(UserType(type).Name);
                name = g >= 0 ? outer + "." + method.Name.Substring(g + 4).Split('|')[0] : outer + ".<lambda>";
            }
            else name = method.IsConstructor ? Plain(type.Name) : method.Name;
            TypeDefinition user = UserType(type);
            return TypePath(user) + "." + name;
        }

        static string Between(string s)
        {
            int a = s.IndexOf('<'), b = s.IndexOf('>');
            return a >= 0 && b > a ? s.Substring(a + 1, b - a - 1) : s;
        }

        static string Plain(string typeName)
        {
            int tick = typeName.IndexOf('`');
            return tick > 0 ? typeName.Substring(0, tick) : typeName;
        }

        /// <summary>"Outer.Inner" (no namespace, no generic arity).</summary>
        static string TypePath(TypeDefinition t) => t.DeclaringType != null ? TypePath(t.DeclaringType) + "." + Plain(t.Name) : Plain(t.Name);

        static string Namespace(TypeDefinition t)
        {
            while (t.DeclaringType != null) t = t.DeclaringType;
            return t.Namespace;
        }

        static SequencePoint FirstPoint(MethodDefinition m) =>
            m.DebugInformation != null && m.DebugInformation.HasSequencePoints
                ? m.DebugInformation.SequencePoints.FirstOrDefault(p => !p.IsHidden)
                : null;

        static string FileOf(TypeDefinition type, Dictionary<TypeDefinition, string> cache)
        {
            if (cache.TryGetValue(type, out string file)) return file;
            file = type.Methods.Select(FirstPoint).FirstOrDefault(p => p != null)?.Document.Url
                   ?? type.NestedTypes.Select(n => FileOf(n, cache)).FirstOrDefault(f => f.Length > 0) ?? "";
            cache[type] = file;
            return file;
        }

        static string Site(MethodDefinition method, Dictionary<TypeDefinition, string> typeFiles, string sourceRoot)
        {
            TypeDefinition user = UserType(method.DebugInformation?.StateMachineKickOffMethod?.DeclaringType ?? method.DeclaringType);
            SequencePoint point = FirstPoint(method);
            bool receiver = HasReceiver(method, out _);
            var sb = new StringBuilder(160);
            char sep = '\u001f';
            sb.Append('1').Append(sep)
                .Append(method.IsConstructor ? "c" : "").Append(receiver ? "" : "s").Append(sep)
                .Append(DisplayName(method)).Append(sep)
                .Append(Namespace(user)).Append(sep)
                .Append(Relative(point?.Document.Url, sourceRoot)).Append(sep)
                .Append(point?.StartLine ?? 0).Append(sep)
                .Append(string.Join(",", method.Parameters.Select(p => p.Name))).Append(sep)
                .Append(ReflectionName(user)).Append(sep)
                .Append(Relative(FileOf(user, typeFiles), sourceRoot));
            return sb.ToString();
        }

        static string Relative(string file, string root)
        {
            if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(root)) return file ?? "";
            string f = file.Replace('\\', '/'), r = root.Replace('\\', '/').TrimEnd('/') + "/";
            return f.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? f.Substring(r.Length) : file;
        }

        /// <summary>The type's name as System.Type.FullName spells it (Namespace.Outer+Inner`1).</summary>
        static string ReflectionName(TypeDefinition t) =>
            t.DeclaringType != null ? ReflectionName(t.DeclaringType) + "+" + t.Name : (t.Namespace.Length > 0 ? t.Namespace + "." : "") + t.Name;

        /// <summary>
        /// Whether a call has an object to record: an instance method of a user class, or compiler-generated code
        /// that captured one (its &lt;&gt;4__this field).
        /// </summary>
        static bool HasReceiver(MethodDefinition method, out FieldDefinition captured)
        {
            captured = null;
            if (method.IsStatic) return false;
            TypeDefinition type = method.DeclaringType;
            if (!IsCompilerGenerated(type)) return !type.IsValueType;
            captured = type.Fields.FirstOrDefault(f => f.Name == "<>4__this");
            return captured != null && !captured.FieldType.IsValueType;
        }

        // ------------------------------------------------------------------ IL

        static void Instrument(MethodDefinition method, Refs refs, string site)
        {
            MethodBody body = method.Body;
            ModuleDefinition module = method.Module;
            body.SimplifyMacros();
            List<Instruction> original = body.Instructions.ToList();
            ILProcessor il = body.GetILProcessor();

            var frame = new VariableDefinition(module.TypeSystem.Int32);
            var exception = new VariableDefinition(refs.Exception);
            body.Variables.Add(frame);
            body.Variables.Add(exception);
            bool hasResult = method.ReturnType.MetadataType != MetadataType.Void;
            VariableDefinition result = null;
            if (hasResult)
            {
                result = new VariableDefinition(method.ReturnType);
                body.Variables.Add(result);
            }
            body.InitLocals = true;

            // prologue: frame = Tracer.Enter(site, this, args)
            Instruction first = original[0];
            var prologue = new List<Instruction> { il.Create(OpCodes.Ldstr, site) };
            if (!HasReceiver(method, out FieldDefinition captured)) prologue.Add(il.Create(OpCodes.Ldnull));
            else if (captured == null) prologue.Add(il.Create(OpCodes.Ldarg_0));
            else
            {
                prologue.Add(il.Create(OpCodes.Ldarg_0));
                prologue.Add(il.Create(OpCodes.Ldfld, Generic(captured, method.DeclaringType)));
            }
            if (method.Parameters.Count == 0) prologue.Add(il.Create(OpCodes.Ldnull));
            else
            {
                prologue.Add(il.Create(OpCodes.Ldc_I4, method.Parameters.Count));
                prologue.Add(il.Create(OpCodes.Newarr, refs.Object));
                for (int i = 0; i < method.Parameters.Count; i++)
                {
                    ParameterDefinition p = method.Parameters[i];
                    TypeReference pt = p.ParameterType;
                    if (pt.IsByReference || pt.IsPointer || pt.IsFunctionPointer || IsByRefLike(pt)) continue; // left null
                    prologue.Add(il.Create(OpCodes.Dup));
                    prologue.Add(il.Create(OpCodes.Ldc_I4, i));
                    prologue.Add(il.Create(OpCodes.Ldarg, p));
                    if (pt.IsValueType || pt.IsGenericParameter) prologue.Add(il.Create(OpCodes.Box, pt));
                    prologue.Add(il.Create(OpCodes.Stelem_Ref));
                }
            }
            prologue.Add(il.Create(OpCodes.Call, refs.Enter));
            prologue.Add(il.Create(OpCodes.Stloc, frame));
            foreach (Instruction ins in prologue) il.InsertBefore(first, ins);

            // the protected region starts after a constructor's base (or this) constructor call
            Instruction tryStart = first;
            if (method.IsConstructor)
            {
                Instruction baseCall = original.FirstOrDefault(i => i.OpCode.Code == Code.Call && i.Operand is MethodReference m &&
                    m.Name == ".ctor" && (m.DeclaringType.FullName == method.DeclaringType.BaseType?.FullName ||
                                          m.DeclaringType.FullName == method.DeclaringType.FullName));
                if (baseCall?.Next != null) tryStart = baseCall.Next;
            }

            // epilogue and handler at the end
            Instruction exitStart = il.Create(OpCodes.Ldloc, frame);
            var epilogue = new List<Instruction> { exitStart };
            if (hasResult)
            {
                epilogue.Add(il.Create(OpCodes.Ldloc, result));
                if (method.ReturnType.IsValueType || method.ReturnType.IsGenericParameter) epilogue.Add(il.Create(OpCodes.Box, method.ReturnType));
                epilogue.Add(il.Create(OpCodes.Call, refs.Return));
                epilogue.Add(il.Create(OpCodes.Ldloc, result));
            }
            else epilogue.Add(il.Create(OpCodes.Call, refs.Exit));
            epilogue.Add(il.Create(OpCodes.Ret));

            Instruction handlerStart = il.Create(OpCodes.Stloc, exception);
            var handler = new List<Instruction>
            {
                handlerStart,
                il.Create(OpCodes.Ldloc, frame),
                il.Create(OpCodes.Ldloc, exception),
                il.Create(OpCodes.Call, refs.Raise),
                il.Create(OpCodes.Rethrow),
            };

            // existing handlers that ran to the end of the method end where ours begins
            foreach (ExceptionHandler h in body.ExceptionHandlers)
            {
                if (h.TryEnd == null) h.TryEnd = handlerStart;
                if (h.HandlerEnd == null) h.HandlerEnd = handlerStart;
            }
            foreach (Instruction ins in handler) il.Append(ins);
            foreach (Instruction ins in epilogue) il.Append(ins);

            // returns leave to the epilogue (the ret instruction itself stays the branch target)
            foreach (Instruction ret in original.Where(i => i.OpCode.Code == Code.Ret))
            {
                if (hasResult)
                {
                    ret.OpCode = OpCodes.Stloc;
                    ret.Operand = result;
                    il.InsertAfter(ret, il.Create(OpCodes.Leave, exitStart));
                }
                else
                {
                    ret.OpCode = OpCodes.Leave;
                    ret.Operand = exitStart;
                }
            }

            body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = tryStart, TryEnd = handlerStart, HandlerStart = handlerStart, HandlerEnd = exitStart, CatchType = refs.Exception,
            });
            body.OptimizeMacros();
        }

        /// <summary>A field of a (possibly generic) declaring type, instantiated over its own generic parameters.</summary>
        static FieldReference Generic(FieldDefinition field, TypeDefinition type)
        {
            if (!type.HasGenericParameters) return field;
            var instance = new GenericInstanceType(type);
            foreach (GenericParameter p in type.GenericParameters) instance.GenericArguments.Add(p);
            return new FieldReference(field.Name, field.FieldType, instance);
        }
    }
}
