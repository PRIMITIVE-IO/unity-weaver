using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using UnityEngine;
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
        
        public override void VisitModule(ModuleDefinition moduleDefinition)
        {
            // get reference to Debug.Log so that it can be called in the opcode with a string argument
            TypeReference primitiveTracerRef = moduleDefinition.ImportReference(typeof(PrimitiveTracker));
            TypeDefinition primitiveTracerDef = primitiveTracerRef.Resolve();
            
            onInstanceEntryMethodRef = moduleDefinition.ImportReference(
                primitiveTracerDef.GetMethod("OnInstanceEntry", 2));
            
            onInstanceExitMethodRef = moduleDefinition.ImportReference(
                primitiveTracerDef.GetMethod("OnInstanceExit", 2));
            
            onStaticEntryMethodRef = moduleDefinition.ImportReference(
                primitiveTracerDef.GetMethod("OnStaticEntry", 1));
            
            onStaticExitMethodRef = moduleDefinition.ImportReference(
                primitiveTracerDef.GetMethod("OnStaticExit", 1));
        }

        public override void VisitType(TypeDefinition typeDefinition)
        {
            // don't trace self
            skip = typeDefinition.Namespace.StartsWith("Weaver"); // don't trace self

            isMonoBehaviour = CheckMonoBehaviour(typeDefinition);
        }

        public override void VisitMethod(MethodDefinition methodDefinition)
        {
            if (skip) return;

            if (isMonoBehaviour && methodDefinition.Name == ".ctor") return; // don't ever record MonoBehaviour constructors -> they run on recompile

            MethodName methodName = MethodNameFromDefinition(methodDefinition);

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
            {
                List<Instruction> preEntryInstructions;
                if (methodDefinition.IsStatic)
                {
                    preEntryInstructions = new()
                    {
                        Instruction.Create(OpCodes.Ldstr, methodName.FullyQualifiedName),
                        Instruction.Create(OpCodes.Call, onStaticEntryMethodRef)
                    };
                }
                else
                {
                    preEntryInstructions = new()
                    {
                        // Loads 'this' (0-th arg of current method) to stack in order to call 'this.GetInstanceIDs()' method
                        Instruction.Create(OpCodes.Ldarg_0),
                        // load FQN as second argumment
                        Instruction.Create(OpCodes.Ldstr, methodName.FullyQualifiedName),
                        Instruction.Create(OpCodes.Call, onInstanceEntryMethodRef)
                    };
                }

                Instruction firstInstruction = preEntryInstructions.First();
                Instruction lastInserted = firstInstruction;

                bodyProcessor.InsertBefore(body.Instructions.First(), firstInstruction);
                for (int ii = 1; ii < preEntryInstructions.Count; ii++)
                {
                    Instruction toInsert = preEntryInstructions[ii];
                    bodyProcessor.InsertAfter(lastInserted, toInsert);
                    lastInserted = toInsert;
                }
            }

            // [Normal part of function]

            // Inject at the end
            {
                List<Instruction> exitInstructions;
                if (methodDefinition.IsStatic)
                {
                    exitInstructions = new()
                    {
                        Instruction.Create(OpCodes.Ldstr, methodName.FullyQualifiedName),
                        Instruction.Create(OpCodes.Call, onStaticExitMethodRef),
                        Instruction.Create(OpCodes.Ret)
                    };
                }
                else
                {
                    exitInstructions = new()
                    {
                        // Loads 'this' (0-th arg of current method) to stack in order to call 'this.GetInstanceIDs()' method
                        Instruction.Create(OpCodes.Ldarg_0), 
                        // Load FQN as second argument
                        Instruction.Create(OpCodes.Ldstr, methodName.FullyQualifiedName),
                        Instruction.Create(OpCodes.Call, onInstanceExitMethodRef),
                        Instruction.Create(OpCodes.Ret)
                    };
                }

                Instruction firstInstruction = exitInstructions.First();
                Instruction lastInserted = firstInstruction;

                bodyProcessor.InsertBefore(body.Instructions.Last(), firstInstruction);
                for (int ii = 1; ii < exitInstructions.Count; ii++)
                {
                    Instruction toInsert = exitInstructions[ii];
                    bodyProcessor.InsertAfter(lastInserted, toInsert);
                    lastInserted = toInsert;
                }
            }
        }
        
        static MethodName MethodNameFromDefinition(MethodDefinition methodDefinition)
        {
            string methodNameString = methodDefinition.Name;
            string classNameString = methodDefinition.DeclaringType.Name;
            if (methodNameString == ".ctor")
            {
                methodNameString = classNameString;
            }
            string namespaceName = methodDefinition.DeclaringType.Namespace;
            string javaReturnType = $"()L{methodDefinition.ReturnType.Name};"; // TODO compatible with java runitme-to-unity

            ClassName parentClass = new ClassName(
                new FileName(""),
                new PackageName(namespaceName),
                classNameString);
            MethodName methodName = new MethodName(
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