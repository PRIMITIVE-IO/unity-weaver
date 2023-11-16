using System.Collections.Generic;

namespace Weaver.Editor
{

    /// <summary>
    /// Maintains a list of items, capped at a certain number with a sliding window so older entries are removed as
    /// newer ones are added. Able to answer the query: given the items in the current window, how many times does a
    /// particular element occur?
    /// </summary>
    public class SlidingWindowCounter
    {
        readonly Queue<MethodName> items = new();
        readonly Dictionary<MethodName, long> counts = new();
        readonly long windowSize;

        public SlidingWindowCounter(long windowSize)
        {
            this.windowSize = windowSize;
        }
        
        /// <summary>
        /// Add the given element to the set of items, bumping out the oldest item
        /// if the window size has already been reached.
        /// </summary>
        /// <param name="item">The item to add</param>
        /// <returns>The number of times the added item occurs in the current window,
        /// after the item has been added</returns>
        public long Add(MethodName item)
        {
            while (items.Count >= windowSize)
            {
                MethodName removed = items.Dequeue();
                counts[removed]--;

                if (counts[removed] < 0L)
                {
                    counts[removed] = 0;
                }
            }

            items.Enqueue(item);
            if (counts.ContainsKey(item))
            {
                counts[item]++;
            }
            else
            {
                counts.Add(item, 1);
            }

            return counts[item];
        }
    }
}