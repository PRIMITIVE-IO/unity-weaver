using Mono.Cecil;

namespace Weaver.Editor.Utility_Types.Implementations
{
    public struct MethodImplementation
    {
        public MethodReference reference;
        public MethodDefinition definition;
        readonly ModuleDefinition m_Module;

        public MethodImplementation(ModuleDefinition module, MethodDefinition methodDefinition)
        {
            m_Module = module;
            reference = m_Module.ImportReference(methodDefinition);
            definition = reference.Resolve();
        }
    }
}