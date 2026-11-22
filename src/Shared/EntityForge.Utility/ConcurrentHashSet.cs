using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    /// <summary>
    /// A concurrent hash set implementation that uses a ConcurrentDictionary.
    /// </summary>
    /// <typeparam name="T">The object type.</typeparam>
    public class ConcurrentHashSet<T> : ConcurrentDictionary<T, T>
    {
        /// <summary>Adds an item atomically and reports whether it was newly inserted.</summary>
        public bool Add(T item)
        {
            // Use TryAdd to ensure thread safety
            return TryAdd(item, item);
        }

        /// <summary>Removes an item atomically and reports whether it was present.</summary>
        public bool Remove(T item)
        {
            // Use TryRemove to ensure thread safety
            return TryRemove(item, out _);
        }
    }
}
