using System.Linq;
using Mono.Cecil;

namespace Weaver.Extensions
{
    public static class ConstructorArguments
    {
        public static T GetValue<T>(this CustomAttribute customAttribute, string propertyName)
        {
            foreach (CustomAttributeNamedArgument argument in customAttribute.Properties
                         .Where(x => string.Equals(propertyName, x.Name, System.StringComparison.Ordinal)))
            {
                return (T)argument.Argument.Value;
            }

            return default;
        }
    }
}