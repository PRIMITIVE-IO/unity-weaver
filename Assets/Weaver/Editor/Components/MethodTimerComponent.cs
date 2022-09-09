using Mono.Cecil;

namespace Weaver.Editor.Components
{
    public class MethodTimerComponent : WeaverComponent
    {
        public override string ComponentName => "Method Timer";


        public override DefinitionType AffectedDefinitions => DefinitionType.Module | DefinitionType.Method;

        public override void VisitModule(ModuleDefinition moduleDefinition)
        {

        }

        public override void VisitMethod(MethodDefinition methodDefinition)
        {
            
        }
    }
}