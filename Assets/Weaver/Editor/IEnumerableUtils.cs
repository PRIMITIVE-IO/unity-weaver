using System.Collections.Generic;
using System.Linq;

namespace Weaver.Editor
{
    public static class IEnumerableUtils
    {
        public static IEnumerable<T> EnumerableOfNotNull<T>(params T?[] list) where T : notnull
        {
            return list.Where(it => it != null)
                .Cast<T>();
        }

        public static string JoinToString<T>(this IEnumerable<T> e, string separator)
        {
            return string.Join(separator, e);
        }
    }
}