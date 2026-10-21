using System;
using System.Collections.Generic;

namespace RtTools
{

    /// <summary>
    /// Array-backed sorted list optimized for values that implement <see cref="IComparable{T}"/>.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public sealed class ComparableSortedArrayCollection<T> : SortedArrayCollection<T> where T : IComparable<T>
    {
        /// <summary>
        /// Much faster than <see cref="Comparer{T}.Compare(T, T)"/> method
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        public override int Compare(T x, T y) => x.CompareTo(y);

        /// <summary>
        /// Create new sorted list
        /// </summary>
        /// <param name="capacity"></param>
        public ComparableSortedArrayCollection(int capacity = 0) : base(capacity, null) { }
        public ComparableSortedArrayCollection(IEnumerable<T> collection) : base(collection, null) { }
    }
}
