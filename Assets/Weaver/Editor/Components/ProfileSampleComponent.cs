using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Weaver.Attributes;
using Weaver.Editor.Type_Extensions;
using MethodBody = Mono.Cecil.Cil.MethodBody;

namespace Weaver.Editor.Components
{
    public class ProfileSampleComponent : WeaverComponent
    {
        MethodReference onEntryMethodRef;
        MethodReference onExitMethodRef;

        public override string ComponentName => "Profile Sample";

        public override DefinitionType AffectedDefinitions => DefinitionType.Module | DefinitionType.Method;

        bool skip = true;
        bool isMonoBehaviour = false;
        
        public override void VisitModule(ModuleDefinition moduleDefinition)
        {
            // get reference to Debug.Log so that it can be called in the opcode with a string argument
            TypeReference primitiveTracerRef = moduleDefinition.ImportReference(typeof(PrimitiveTracker));
            TypeDefinition primitiveTracerDef = primitiveTracerRef.Resolve();
            onEntryMethodRef = moduleDefinition.ImportReference(
                primitiveTracerDef.GetMethod("OnEntry", 2));
            
            onExitMethodRef = moduleDefinition.ImportReference(
                primitiveTracerDef.GetMethod("OnExit", 2));
        }

        public override void VisitType(TypeDefinition typeDefinition)
        {
            skip = CheckSkip(typeDefinition);
            isMonoBehaviour = CheckMonoBehaviour(typeDefinition);
        }

        public override void VisitMethod(MethodDefinition methodDefinition)
        {
            if (skip) return;

            if (isMonoBehaviour && methodDefinition.Name == ".ctor") return; // don't ever record MonoBehaviour constructors -> they run on recompile

            MethodName methodName = MethodNameFromDefinition(methodDefinition);
            methodName.IsStatic = methodDefinition.IsStatic;
                
            // get body and processor for code injection
            MethodBody body = methodDefinition.Body;
            ILProcessor bodyProcessor = body.GetILProcessor();

            // Inject at the start of the function
            // see: https://en.wikipedia.org/wiki/List_of_CIL_instructions
            {
                List<Instruction> preEntryInstructions = new()
                {
                    Instruction.Create(OpCodes.Ldarg_0),// Loads 'this' (0-th arg of current method) to stack in order to call 'this.GetInstanceIDs()' method
                    Instruction.Create(OpCodes.Ldstr, methodName.FullyQualifiedName),
                    Instruction.Create(OpCodes.Call, onEntryMethodRef)
                };

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
                List<Instruction> exitInstructions = new()
                {
                    Instruction.Create(OpCodes.Ldarg_0),// Loads 'this' (0-th arg of current method) to stack in order to call 'this.GetInstanceIDs()' method
                    Instruction.Create(OpCodes.Ldstr, methodName.FullyQualifiedName),
                    Instruction.Create(OpCodes.Call, onExitMethodRef),
                    // .....
                    Instruction.Create(OpCodes.Ret)
                };

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

            ClassName parentClass = new ClassName(
                new FileName(""),
                new PackageName(namespaceName),
                classNameString);
            MethodName methodName = new MethodName(
                parentClass,
                methodNameString,
                methodDefinition.ReturnType.Name,
                methodDefinition.Parameters.Select(x => new Argument(x.Name, TypeName.For(x.ParameterType.Name))));
            return methodName;
        }
        
        static bool CheckSkip(TypeDefinition typeDefinition)
        {
            if (typeDefinition.Namespace.StartsWith("Weaver"))
            {
                // don't trace self
                return true;
            }
            
            CustomAttribute profileSample = typeDefinition.GetCustomAttribute<ProfileSampleAttribute>();

            // Check if we have our attribute
            if (profileSample == null)
            {
                return true;
            }

            typeDefinition.CustomAttributes.Remove(profileSample);
            return false;
        }
        
        static bool CheckMonoBehaviour(TypeDefinition typeDefinition)
        {
            while (true)
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
        }
    }
}