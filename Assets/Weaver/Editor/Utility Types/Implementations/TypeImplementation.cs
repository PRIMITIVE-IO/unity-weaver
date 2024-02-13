using System;
using Mono.Cecil;
using Weaver.Editor.Type_Extensions;

namespace Weaver.Editor.Utility_Types.Implementations
{
    public struct TypeImplementation
    {
        public TypeReference reference;
        public TypeDefinition definition;
        readonly ModuleDefinition m_Module;

        public TypeImplementation(ModuleDefinition module, Type type)
        {
            m_Module = module;
            reference = m_Module.ImportReference(type);
            definition = reference.Resolve();
        }

        public MethodImplementation GetConstructor()
        {
            return GetMethod(".ctor");
        }

        public MethodImplementation GetConstructor(params Type[] parameterTypes)
        {
            return GetMethod(".ctor", parameterTypes);
        }

        public MethodImplementation GetMethod(string methodName)
        {
            MethodDefinition methodDefinition = definition.GetMethod(methodName);
            MethodImplementation methodImplementation = new(m_Module,methodDefinition);
            return methodImplementation;
        }

        public MethodImplementation GetMethod(string methodName, params Type[] parameterTypes)
        {
            MethodDefinition methodDefinition = definition.GetMethod(methodName, parameterTypes);
            MethodImplementation methodImplementation = new(m_Module, methodDefinition);
            return methodImplementation;
        }

        public PropertyImplementation GetProperty(string methodName)
        {
            PropertyDefinition propertyDefinition = definition.GetProperty(methodName);
            PropertyImplementation methodImplementation = new(m_Module, propertyDefinition);
            return methodImplementation;
        }
    }
}
