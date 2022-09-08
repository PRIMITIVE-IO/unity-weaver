using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using UnityEngine;
using Weaver.Attributes;
using Weaver.Editor.Type_Extensions;
using MethodBody = Mono.Cecil.Cil.MethodBody;

namespace Weaver.Editor.Components
{
    public class ProfileSampleComponent : WeaverComponent
    {
        MethodReference m_DebugLogMethodRef;

        public override string ComponentName => "Profile Sample";

        public override DefinitionType AffectedDefinitions => DefinitionType.Module | DefinitionType.Method;

        public override void VisitModule(ModuleDefinition moduleDefinition)
        {
            // get reference to Debug.Log so that it can be called in the opcode with a string argument
            TypeReference debugTypeRef = moduleDefinition.ImportReference(typeof(Debug));
            TypeDefinition debugTypeDef = debugTypeRef.Resolve();
            m_DebugLogMethodRef = moduleDefinition.ImportReference(
                debugTypeDef.GetMethod("Log", 1));
        }

        public override void VisitType(TypeDefinition typeDefinition)
        {
            
        }

        public override void VisitMethod(MethodDefinition methodDefinition)
        {
            if (CheckSkip(methodDefinition)) return;
            bool isMonobehaviour = CheckMonoBehaviour(methodDefinition.DeclaringType);

            string methodName = $"{methodDefinition.DeclaringType.Name}.{methodDefinition.Name}";
            
            MethodDefinition instanceIdMethodDef = methodDefinition.DeclaringType.GetMethod("GetInstanceIDs");
            MethodReference instanceIdMethodRef = methodDefinition.Module.ImportReference(instanceIdMethodDef);
            
            // get body and processor for code injection
            MethodBody body = methodDefinition.Body;
            ILProcessor bodyProcessor = body.GetILProcessor();

            // Inject at the start of the function
            // see: https://en.wikipedia.org/wiki/List_of_CIL_instructions
            {
                List<Instruction> preEntryInstructions = new()
                {
                    Instruction.Create(OpCodes.Ldstr, methodName),
                    Instruction.Create(OpCodes.Call, m_DebugLogMethodRef),
                    
                    Instruction.Create(OpCodes.Ldarg_0),// Loads 'this' (0-th arg of current method) to stack in order to call 'this.GetInstanceIDs()' method
                    Instruction.Create(OpCodes.Callvirt, instanceIdMethodRef),
                    Instruction.Create(OpCodes.Call, m_DebugLogMethodRef), // prints text returned by `GetInstanceIDs()` method
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
                    // .....
                    // .....
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
        
        static bool CheckSkip(IMemberDefinition memberDefinition)
        {
            TypeDefinition typeDefinition = memberDefinition.DeclaringType;
            if (typeDefinition.Namespace.StartsWith("Weaver"))
            {
                // don't trace self
                return true;
            }
            
            CustomAttribute profileSample = memberDefinition.GetCustomAttribute<ProfileSampleAttribute>();

            // Check if we have our attribute
            if (profileSample == null)
            {
                return true;
            }

            memberDefinition.CustomAttributes.Remove(profileSample);
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