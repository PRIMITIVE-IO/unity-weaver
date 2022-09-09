using System;
using System.Collections.Generic;
using System.Linq;

namespace Weaver.Editor
{
    public static class IEnumerableUtils
    {
        public static IEnumerable<TResult> SelectNotNull<TSource, TResult>(this IEnumerable<TSource> e,
            Func<TSource, TResult?> f) where TResult : notnull
        {
            return e.Select(f)
                .Where(it => it != null)
                .Cast<TResult>();
        }

        public static IEnumerable<TResult> SelectNotNull<TSource, TResult>(this IEnumerable<TSource> e,
            Func<TSource, int, TResult?> f) where TResult : notnull
        {
            return e.Select(f)
                .Where(it => it != null)
                .Cast<TResult>();
        }

        public static IEnumerable<T> EnumerableOfNotNull<T>(params T?[] list) where T : notnull
        {
            return list.Where(it => it != null)
                .Cast<T>();
        }

        public static Dictionary<K, V> ConcatDict<K, V>(this Dictionary<K, V> t, Dictionary<K, V> o)
        {
            return t.Concat(o).ToDictionary(it => it.Key, it => it.Value);
        }

        public static string JoinToString<T>(this IEnumerable<T> e, string separator)
        {
            return string.Join(separator, e);
        }

        public static Dictionary<K, V> ToDictIgnoringDuplicates<T, K, V>(
            this IEnumerable<T> elems,
            Func<T, K> keyExtractor,
            Func<T, V> valueTransformer,
            Action<T> onKeyDuplication = null)
        {
            Dictionary<K, V> dict = new Dictionary<K, V>();
            foreach (T elem in elems)
            {
                K key = keyExtractor(elem);
                V value = valueTransformer(elem);

                if (!dict.TryAdd(key, value))
                {
                    onKeyDuplication?.Invoke(elem);
                }
            }

            return dict;
        }

        public static Dictionary<K, V> ToDictIgnoringDuplicates<K, V>(
            this IEnumerable<V> e,
            Func<V, K> keyExtractor,
            Action<V> onKeyDuplication = null)
        {
            return e.ToDictIgnoringDuplicates(
                keyExtractor: keyExtractor,
                valueTransformer: v => v,
                onKeyDuplication: onKeyDuplication
            );
        }

        public static R MaxOrDefault<T, R>(this List<T> list, Func<T, R> extractor)
        {
            return !list.Any()
                ? default
                : list.Max(extractor);
        }

        public static T MinOrDefault<T>(this IEnumerable<T> source) where T : IComparable<T>
        {
            using IEnumerator<T> e = source.GetEnumerator();
            if (!e.MoveNext())
            {
                return default;
            }

            T value = e.Current;
            while (e.MoveNext())
            {
                T x = e.Current;
                if (x.CompareTo(value) < 0)
                {
                    value = x;
                }
            }

            return value;
        }

        public static T MaxOrDefault<T>(this IEnumerable<T> source) where T : IComparable<T>
        {
            using IEnumerator<T> e = source.GetEnumerator();
            if (!e.MoveNext())
            {
                return default;
            }

            T value = e.Current;
            while (e.MoveNext())
            {
                T x = e.Current;
                if (x.CompareTo(value) > 0)
                {
                    value = x;
                }
            }

            return value;
        }

        public static V GetOrCreate<K, V>(this Dictionary<K, V> dict, K key, Func<K, V> valueProducer)
        {
            dict.TryGetValue(key, out V value);
            if (value == null)
            {
                value = valueProducer(key);
                dict.Add(key, value);
            }

            return value;
        }
    }
}