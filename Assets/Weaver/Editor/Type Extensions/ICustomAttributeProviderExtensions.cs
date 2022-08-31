using Mono.Cecil;
using Mono.Collections.Generic;
using System;
using System.Linq;

namespace Weaver.Extensions
{
    public static class ICustomAttributeProviderExtensions
    {
        public static bool HasCustomAttribute<T>(this ICustomAttributeProvider instance)
        {
            if (!instance.HasCustomAttributes) return false;

            Collection<CustomAttribute> attributes = instance.CustomAttributes;

            return attributes.Any(t => t.AttributeType.FullName.Equals(typeof(T).FullName, StringComparison.Ordinal));
        }

        public static CustomAttribute GetCustomAttribute<T>(this ICustomAttributeProvider instance)
        {
            if (!instance.HasCustomAttributes) return null;

            Collection<CustomAttribute> attributes = instance.CustomAttributes;

            return attributes.FirstOrDefault(t => t.AttributeType.FullName.Equals(typeof(T).FullName, StringComparison.Ordinal));
        }
    }
}
