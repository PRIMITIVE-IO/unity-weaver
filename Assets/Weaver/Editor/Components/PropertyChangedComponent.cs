using Mono.Cecil;

namespace Weaver.Editor.Components
{
    public class PropertyChangedComponent : WeaverComponent
    {
        public override string ComponentName => "Property Changed";

        public override DefinitionType AffectedDefinitions => DefinitionType.Module | DefinitionType.Property;

        public override void VisitProperty(PropertyDefinition propertyDefinition) { }
    }
}
