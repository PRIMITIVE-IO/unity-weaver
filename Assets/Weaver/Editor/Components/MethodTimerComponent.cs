using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Weaver.Attributes;
using Weaver.Editor.Type_Extensions;
using Debug = UnityEngine.Debug;

namespace Weaver.Editor.Components
{
    public class MethodTimerComponent : WeaverComponent
    {
        public struct StopwatchDefinition
        {
            public MethodReference consturctor;
            public MethodReference start;
            public MethodReference stop;
            public MethodReference getElapsedMilliseconds;

            public StopwatchDefinition(TypeDefinition stopwatchTypeDef, ModuleDefinition module)
            {
                consturctor = module.ImportReference(stopwatchTypeDef.GetMethod(".ctor"));
                start = module.ImportReference(stopwatchTypeDef.GetMethod("Start"));
                stop = module.ImportReference(stopwatchTypeDef.GetMethod("Stop"));
                getElapsedMilliseconds =
                    module.ImportReference(stopwatchTypeDef.GetProperty("ElapsedMilliseconds").GetMethod);
            }
        }

        StopwatchDefinition m_StopWatchTypeDef;
        MethodReference m_StringConcatMethodRef;
        MethodReference m_DebugLogMethodRef;
        TypeReference m_StopwatchTypeReference;

        public override string ComponentName => "Method Timer";


        public override DefinitionType AffectedDefinitions => DefinitionType.Module | DefinitionType.Method;

        public override void VisitModule(ModuleDefinition moduleDefinition)
        {
            // Import our stopwatch type reference 
            m_StopwatchTypeReference = moduleDefinition.ImportReference(typeof(Stopwatch));
            // Resolve it so we can get the type definition
            TypeDefinition stopwatchTypeDef = m_StopwatchTypeReference.Resolve();
            // Create our value holder
            m_StopWatchTypeDef = new StopwatchDefinition(stopwatchTypeDef, moduleDefinition);
            // String
            TypeDefinition stringTypeDef = typeSystem.String.Resolve();
            m_StringConcatMethodRef = moduleDefinition.ImportReference(stringTypeDef.GetMethod("Concat", 2));

            TypeReference debugTypeRef = moduleDefinition.ImportReference(typeof(Debug));
            TypeDefinition debugTypeDeff = debugTypeRef.Resolve();
            m_DebugLogMethodRef = moduleDefinition.ImportReference(debugTypeDeff.GetMethod("Log", 1));
        }

        public override void VisitMethod(MethodDefinition methodDefinition)
        {
            // Check if we have our attribute
            CustomAttribute customAttribute = methodDefinition.GetCustomAttribute<MethodTimerAttribute>();
            if (customAttribute == null)
            {
                return;
            }

            // Remove the attribute
            methodDefinition.CustomAttributes.Remove(customAttribute);

            string methodName = $"{methodDefinition.DeclaringType.FullName}.{methodDefinition.Name}";

            // get body and processor for code injection
            MethodBody body = methodDefinition.Body;
            ILProcessor bodyProcessor = body.GetILProcessor();

            // define and add variables for starting the stopwatch and getting elapsed time
            VariableDefinition stopwatchVariable = new VariableDefinition(m_StopwatchTypeReference);
            VariableDefinition elapsedMilliseconds = new VariableDefinition(typeSystem.Int64);

            body.Variables.Add(stopwatchVariable);
            body.Variables.Add(elapsedMilliseconds);

            // Inject at the start of the function
            // see: https://en.wikipedia.org/wiki/List_of_CIL_instructions
            {
                List<Instruction> preEntryInstructions = new()
                {
                    // instantiate stopwatch with New Object
                    Instruction.Create(OpCodes.Newobj, m_StopWatchTypeDef.consturctor),
                    // pop variable from stack with Stack Local (Stloc)
                    Instruction.Create(OpCodes.Stloc, stopwatchVariable),
                    // load variable onto stack Load Local (ldloc)
                    Instruction.Create(OpCodes.Ldloc, stopwatchVariable),
                    // start stopwatch with Call Virtual
                    Instruction.Create(OpCodes.Callvirt, m_StopWatchTypeDef.start)
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
                    // load stopwatch onto stack with Load Local
                    Instruction.Create(OpCodes.Ldloc, stopwatchVariable),
                    // stop the stopwatch with Call Virtual
                    Instruction.Create(OpCodes.Callvirt, m_StopWatchTypeDef.stop),
                    // Pushes the integer value of 0 onto the evaluation stack as an int32.
                    Instruction.Create(OpCodes.Ldc_I4_0),
                    // Converts the value on top of the evaluation stack to int64.
                    Instruction.Create(OpCodes.Conv_I8),
                    // Pops the current value from the top of the evaluation stack and stores it in a the local variable list at index 1.
                    Instruction.Create(OpCodes.Stloc, elapsedMilliseconds),
                    // Loads the local variable at index 0 onto the evaluation stack.
                    Instruction.Create(OpCodes.Ldloc, stopwatchVariable),
                    // get the elapsed milliseconds from the stopwatch with Call Virtual
                    Instruction.Create(OpCodes.Callvirt, m_StopWatchTypeDef.getElapsedMilliseconds),
                    // Pops the current value from the top of the evaluation stack and stores it in a the local variable list at index 1.
                    Instruction.Create(OpCodes.Stloc, elapsedMilliseconds),
                    // Pushes a new object reference to a string literal stored in the metadata.
                    Instruction.Create(OpCodes.Ldstr, methodName),
                    // Loads the local variable at index 1 onto the evaluation stack.
                    Instruction.Create(OpCodes.Ldloc, elapsedMilliseconds),
                    // Converts a value type to an object reference (type O).
                    Instruction.Create(OpCodes.Box, typeSystem.Int64),
                    // Concatenate the name of the method and the elapsed time using Call
                    Instruction.Create(OpCodes.Call, m_StringConcatMethodRef),
                    // Run the debug log output with Call
                    Instruction.Create(OpCodes.Call, m_DebugLogMethodRef),
                    // Returns from the current method, pushing a return value (if present) from the callee's evaluation
                    // stack onto the caller's evaluation stack.
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