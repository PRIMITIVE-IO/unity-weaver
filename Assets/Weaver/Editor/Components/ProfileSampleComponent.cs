﻿using System.Collections.Generic;
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

        static List<string> TypesToSkip => WeaverSettings.Instance().m_TypesToSkip;

        static List<string> MethodsToSkip => WeaverSettings.Instance().m_MethodsToSkip;

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
            skip = typeDefinition.Namespace.StartsWith("Weaver") || TypesToSkip.Contains(typeDefinition.FullName);

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
            {
                List<Instruction> preEntryInstructions;
                if (methodDefinition.IsStatic)
                {
                    preEntryInstructions = new()
                    {
                        Instruction.Create(OpCodes.Nop),
                        Instruction.Create(OpCodes.Ldstr, methodName.FullyQualifiedName),
                        Instruction.Create(OpCodes.Call, onStaticEntryMethodRef),
                        Instruction.Create(OpCodes.Nop)
                    };
                }
                else
                {
                    preEntryInstructions = new()
                    {
                        Instruction.Create(OpCodes.Nop),
                        // Loads 'this' (0-th arg of current method) to stack in order to call 'this.GetInstanceIDs()' method
                        Instruction.Create(OpCodes.Ldarg_0),
                        // load FQN as second argumment
                        Instruction.Create(OpCodes.Ldstr, methodName.FullyQualifiedName),
                        Instruction.Create(OpCodes.Call, onInstanceEntryMethodRef),
                        Instruction.Create(OpCodes.Nop)
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
                        Instruction.Create(OpCodes.Nop),
                        Instruction.Create(OpCodes.Ldstr, methodName.FullyQualifiedName),
                        Instruction.Create(OpCodes.Call, onStaticExitMethodRef),
                        Instruction.Create(OpCodes.Nop)
                    };
                }
                else
                {
                    exitInstructions = new()
                    {
                        Instruction.Create(OpCodes.Nop),
                        // Loads 'this' (0-th arg of current method) to stack in order to call 'this.GetInstanceIDs()' method
                        Instruction.Create(OpCodes.Ldarg_0), 
                        // Load FQN as second argument
                        Instruction.Create(OpCodes.Ldstr, methodName.FullyQualifiedName),
                        Instruction.Create(OpCodes.Call, onInstanceExitMethodRef),
                        Instruction.Create(OpCodes.Nop)
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