using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using UnityEngine;
using Mono.Cecil.Cil;
using Weaver.Extensions;
using MethodBody = Mono.Cecil.Cil.MethodBody;
using Object = UnityEngine.Object;

namespace Weaver
{
    public class ProfileSampleComponent : WeaverComponent
    {
        MethodReference m_GetGameObjectMethodRef;
        MethodReference m_GetObjectInstanceId;
        MethodReference m_DebugLogMethodRef;
        private ModuleDefinition moduleDefinition;

        public override string ComponentName => "Profile Sample";

        public override DefinitionType AffectedDefinitions => DefinitionType.Module | DefinitionType.Method;

        public override void VisitModule(ModuleDefinition moduleDefinition)
        {
            this.moduleDefinition = moduleDefinition;
            // Get the Component.gameObject property so that it can be retrieved
            TypeReference componentTypeRef = moduleDefinition.ImportReference(typeof(Component));
            TypeDefinition componentTypeDef = componentTypeRef.Resolve();
            m_GetGameObjectMethodRef = moduleDefinition.ImportReference(
                componentTypeDef.GetProperty("gameObject").GetMethod);

            TypeReference objectTypeRef = moduleDefinition.ImportReference(typeof(Object));
            TypeDefinition objectTypeDef = objectTypeRef.Resolve();
            m_GetObjectInstanceId = moduleDefinition.ImportReference(
                objectTypeDef.GetMethod("GetInstanceID", 0));

            // get =reference to Debug.Log so that it can be called in the opcode with a string argument
            TypeReference debugTypeRef = moduleDefinition.ImportReference(typeof(Debug));
            TypeDefinition debugTypeDef = debugTypeRef.Resolve();
            m_DebugLogMethodRef = moduleDefinition.ImportReference(
                debugTypeDef.GetMethod("Log", 1));
        }

        public override void VisitMethod(MethodDefinition methodDefinition)
        {
            CustomAttribute profileSample = methodDefinition.GetCustomAttribute<ProfileSampleAttribute>();

            // Check if we have our attribute
            if (profileSample == null)
            {
                return;
            }
            
            methodDefinition.CustomAttributes.Remove(profileSample);

            string methodName = $"{methodDefinition.DeclaringType.Name}.{methodDefinition.Name}";
            
            // get body and processor for code injection
            MethodBody body = methodDefinition.Body;
            ILProcessor bodyProcessor = body.GetILProcessor();

            // Inject at the start of the function
            // see: https://en.wikipedia.org/wiki/List_of_CIL_instructions
            {
                MethodDefinition textMethod = methodDefinition.DeclaringType.GetMethod("text");
                MethodReference textMethodRef = moduleDefinition.ImportReference(textMethod);
                
                List<Instruction> preEntryInstructions = new()
                {
                    Instruction.Create(OpCodes.Ldstr, methodName),
                    Instruction.Create(OpCodes.Call, m_DebugLogMethodRef),
                    
                    Instruction.Create(OpCodes.Ldarg_0),// Loads 'this' (0-th arg of current method) to stack in order to call 'this.text()' method
                    Instruction.Create(OpCodes.Callvirt, textMethodRef),
                    Instruction.Create(OpCodes.Call, m_DebugLogMethodRef), // prints text returned by `text()` method
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
    }
}