using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using UnityEngine;
using Weaver.Editor.Settings;
using Weaver.Editor.Type_Extensions;
using MethodBody = Mono.Cecil.Cil.MethodBody;

namespace Weaver.Editor.Components
{
    public class ProfileSampleComponent : WeaverComponent
    {
        MethodReference onInstanceEntryMethodRef;
        MethodReference onInstanceExitMethodRef;
        MethodReference onStaticEntryMethodRef;
        MethodReference onStaticExitMethodRef;

        public override string ComponentName => "Profile Sample";

        public override DefinitionType AffectedDefinitions => DefinitionType.Module | DefinitionType.Method;

        bool skip = true;
        bool isMonoBehaviour = false;

        static List<string> TypesToSkip => WeaverSettings.Instance != null
            ? WeaverSettings.Instance.m_TypesToSkip
            : new List<string>();

        static List<string> MethodsToSkip => WeaverSettings.Instance != null
            ? WeaverSettings.Instance.m_MethodsToSkip
            : new List<string>();

        public override void VisitModule(ModuleDefinition moduleDefinition)
        {
            TypeReference primitiveTracerRef = moduleDefinition.ImportReference(typeof(PrimitiveTracker));
            TypeDefinition primitiveTracerDef = primitiveTracerRef.Resolve();

            onInstanceEntryMethodRef = moduleDefinition.ImportReference(
                primitiveTracerDef.GetMethod("OnInstanceEntry", 1));

            onInstanceExitMethodRef = moduleDefinition.ImportReference(
                primitiveTracerDef.GetMethod("OnInstanceExit", 1));

            onStaticEntryMethodRef = moduleDefinition.ImportReference(
                primitiveTracerDef.GetMethod("OnStaticEntry"));

            onStaticExitMethodRef = moduleDefinition.ImportReference(
                primitiveTracerDef.GetMethod("OnStaticExit"));
        }

        public override void VisitType(TypeDefinition typeDefinition)
        {
            // don't trace self
            skip = typeDefinition.IsValueType || // skip structs. the IL injection trips on them
                   typeDefinition.Namespace.StartsWith("Weaver") ||
                   typeDefinition.Namespace.StartsWith("Unity") ||
                   TypesToSkip.Contains(typeDefinition.FullName);

            isMonoBehaviour = CheckMonoBehaviour(typeDefinition);
        }

        public override void VisitMethod(MethodDefinition methodDefinition)
        {
            if (skip) return;

            if (methodDefinition.Name == ".cctor") return; // don't ever record static constructors

            // don't ever record MonoBehaviour constructors -> they run on recompile
            if (isMonoBehaviour && methodDefinition.Name is ".ctor") return;

            MethodName methodName = MethodNameFromDefinition(methodDefinition);

            string methodFqn = $"{((ClassName)methodName.ContainmentParent).FullyQualifiedName}.{methodName.ShortName}";
            if (MethodsToSkip.Contains(methodFqn)) return;

            // get body and processor for code injection
            MethodBody body = methodDefinition.Body;
            if (body == null)
            {
                Debug.Log($"Missing body for: {methodDefinition.FullName}");
                return;
            }

            ILProcessor bodyProcessor = body.GetILProcessor();

            // Inject at the start of the function
            // see: https://en.wikipedia.org/wiki/List_of_CIL_instructions
            List<Instruction> preEntryInstructions;
            if (methodDefinition.IsStatic)
            {
                preEntryInstructions = new List<Instruction>
                {
                    Instruction.Create(OpCodes.Nop),
                    Instruction.Create(OpCodes.Call, onStaticEntryMethodRef),
                    Instruction.Create(OpCodes.Nop)
                };
            }
            else
            {
                preEntryInstructions = new List<Instruction>
                {
                    Instruction.Create(OpCodes.Nop),
                    // Loads 'this' (0-th arg of current method) to stack in order to get the instance ID of the object
                    Instruction.Create(OpCodes.Ldarg_0),
                    Instruction.Create(OpCodes.Call, onInstanceEntryMethodRef),
                    Instruction.Create(OpCodes.Nop)
                };
            }

            Instruction firstPreEntryInstruction = preEntryInstructions.First();
            Instruction lastPreEntryInserted = firstPreEntryInstruction;

            bodyProcessor.InsertBefore(body.Instructions.First(), firstPreEntryInstruction);
            for (int ii = 1; ii < preEntryInstructions.Count; ii++)
            {
                Instruction toInsert = preEntryInstructions[ii];
                bodyProcessor.InsertAfter(lastPreEntryInserted, toInsert);
                lastPreEntryInserted = toInsert;
            }

            // [Normal part of function]

            // Inject at the end
            List<Instruction> exitInstructions;
            if (methodDefinition.IsStatic)
            {
                exitInstructions = new List<Instruction>
                {
                    Instruction.Create(OpCodes.Nop),
                    Instruction.Create(OpCodes.Call, onStaticExitMethodRef),
                    Instruction.Create(OpCodes.Nop)
                };
            }
            else
            {
                exitInstructions = new List<Instruction>
                {
                    Instruction.Create(OpCodes.Nop),
                    // Loads 'this' (0-th arg of current method) to stack in order to get the instance ID of the object
                    Instruction.Create(OpCodes.Ldarg_0),
                    Instruction.Create(OpCodes.Call, onInstanceExitMethodRef),
                    Instruction.Create(OpCodes.Nop)
                };
            }

            Instruction firstExitInstruction = exitInstructions.First();
            Instruction lastExitInserted = firstExitInstruction;

            bodyProcessor.InsertBefore(body.Instructions.Last(), firstExitInstruction);
            for (int ii = 1; ii < exitInstructions.Count; ii++)
            {
                Instruction toInsert = exitInstructions[ii];
                bodyProcessor.InsertAfter(lastExitInserted, toInsert);
                lastExitInserted = toInsert;
            }
        }

        static MethodName MethodNameFromDefinition(MethodDefinition methodDefinition)
        {
            string methodNameString = methodDefinition.Name;
            if (methodNameString == ".ctor")
            {
                methodNameString = methodDefinition.DeclaringType.Name;
            }

            string classNameString = methodDefinition.DeclaringType.FullName;

            string namespaceName = methodDefinition.DeclaringType.Namespace;
            if (!string.IsNullOrEmpty(namespaceName))
            {
                classNameString = classNameString[(namespaceName.Length + 1)..];
            }
            else if (classNameString.Contains('.'))
            {
                // inner classes don't have namespaces defined, even if they are in a namespace
                namespaceName = classNameString[..classNameString.LastIndexOf('.')];
                classNameString = classNameString[(classNameString.LastIndexOf('.') + 1)..];
            }

            classNameString = classNameString.Replace('/', '$'); // inner class separator

            string javaReturnType =
                $"()L{methodDefinition.ReturnType.Name};"; // TODO compatible with java runtime-to-unity

            ClassName parentClass = new(
                new FileName(""),
                new PackageName(namespaceName),
                classNameString);
            MethodName methodName = new(
                parentClass,
                methodNameString,
                javaReturnType,
                methodDefinition.Parameters.Select(x => new Argument(x.Name, TypeName.For(x.ParameterType.Name))));
            return methodName;
        }

        static bool CheckMonoBehaviour(TypeDefinition typeDefinition)
        {
            while (true)
            {
                try
                {
                    TypeDefinition baseDef = typeDefinition.BaseType?.Resolve();
                    if (baseDef == null)
                    {
                        return false;
                    }

                    if (baseDef.Name == "MonoBehaviour")
                    {
                        return true;
                    }

                    typeDefinition = baseDef;
                }
                catch (AssemblyResolutionException e)
                {
                    Debug.Log($"Could not resolve MonoBehaviour type: {typeDefinition.FullName} {e.Message}");
                    return false;
                }
            }
        }
    }
}