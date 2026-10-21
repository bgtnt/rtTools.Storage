using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace RtTools
{

    /// <summary>
    /// Array-backed sorted list for values ordered by an explicit comparer.
    /// !!! use Comparable instead (not using <see cref="Comparer{T}.Default"/> for performance reasons)
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class SortedArrayCollection<T> : ICollection<T>, IList<T>
    {
        private static readonly bool ContainsReferences = RuntimeHelpers.IsReferenceOrContainsReferences<T>();
        private static readonly T[] EmptyArray = [];

        private readonly IComparer<T> itemComparer;
        public virtual int Compare(T x, T y) => itemComparer.Compare(x, y);

        private T[] items;

        public int Count { get; private set; }

        private static T[] collection2array(IEnumerable<T> collection)
        {
            T[] ret;
            if (collection is ICollection<T> is2)
            {
                int count = is2.Count;
                if (count == 0) return EmptyArray;
                is2.CopyTo(ret = new T[count], 0);
            }
            else
            {
                ret = EmptyArray;
                int size = 0;
                foreach (T item in collection)
                {
                    if (ret.Length == size)
                        Array.Resize(ref ret, size == 0 ? 4 : size * 2);
                    ret[size++] = item;
                }
                Array.Resize(ref ret, size);
            }
            return ret;
        }

        T[] ToSortedArray(IEnumerable<T> collection)
        {
            T[] ret = collection2array(collection);
            if (ret.Length > 0) Array.Sort(ret, this.itemComparer);
            return ret;
        }

        /// <summary>
        /// Search item (binary)
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public int BinarySearch(T value) => this.BinarySearch(value, 0, this.Count);

        /// <summary>
        /// Search item (binary)
        /// </summary>
        /// <param name="value"></param>
        /// <param name="index"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        public int BinarySearch(T value, int index, int length)
        {
            if (length > 0)
            {
                T[] array = this.items;
                int mid = (index + length) - 1;
                while (index <= mid)
                {
                    int mid1 = index + ((mid - index) >> 1);
                    int res = this.Compare(array[mid1], value);
                    if (res == 0) return mid1;
                    if (res < 0) index = mid1 + 1;
                    else mid = mid1 - 1;
                }
            }
            return ~index;
        }

        /// <summary>
        /// Create new sorted list
        /// </summary>
        /// <param name="capacity"></param>
        /// <param name="comparer"></param>
        public SortedArrayCollection(int capacity = 0, IComparer<T>? comparer = null)
        {
            this.itemComparer = comparer ?? Comparer<T>.Default;
            this.items = capacity == 0 ? EmptyArray : new T[capacity];
        }

        /// <summary>
        /// Create new sorted list
        /// </summary>
        /// <param name="collection"></param>
        /// <param name="comparer"></param>
        public SortedArrayCollection(IEnumerable<T> collection, IComparer<T>? comparer = null)
        {
            ArgumentNullException.ThrowIfNull(collection);
            this.itemComparer = comparer ?? Comparer<T>.Default;
            this.Count = (this.items = this.ToSortedArray(collection)).Length;
        }

        /// <summary>
        /// Update structure array (!!! internally used in structure)
        /// </summary>
        /// <param name="items"></param>
        /// <param name="comparer"></param>
        /// <param name="checkSorting"></param>
        public void SetItems(T[] items, bool checkSorting)
        {
            if (checkSorting)
            { // check items are sorted
                int len = items.Length;
                if (len > 1)
                { // has at least 2 elements
                    T last = items[0];
                    for (int i = 1; i < len; i++)
                    {
                        T item = items[i];
                        if (this.itemComparer.Compare(last, item) > 0)
                        { // unsorted elements found
                            Array.Sort(items, this.itemComparer);
                            break;
                        }
                        last = item;
                    }
                }
            }
            this.Count = (this.items = items ?? throw new ArgumentNullException(nameof(items))).Length;
        }

        public int Capacity
        {
            get => this.items.Length;
            set
            {
                if (value < this.Count)
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Capacity cannot be less than Count.");

                if (value != this.items.Length)
                {
                    if (value > 0)
                    {
                        T[] destinationArray = new T[value];
                        if (this.Count > 0)
                            Array.Copy(this.items, 0, destinationArray, 0, this.Count);
                        this.items = destinationArray;
                    }
                    else
                        this.items = EmptyArray;
                }
            }
        }

        bool ICollection<T>.IsReadOnly => false;

        public void RemoveAt(int index)
        {
            if ((uint)index >= (uint)this.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            if (index < --this.Count)
                Array.Copy(this.items, index + 1, this.items, index, this.Count - index);
            if (ContainsReferences) Array.Clear(this.items, this.Count, 1);
        }

        public void RemoveRange(int index, int count)
        {
            if (index < 0)
                throw new ArgumentOutOfRangeException(nameof(index));
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            if ((this.Count - index) < count) throw new ArgumentException();

            if (count > 0)
            {
                this.Count -= count;
                if (index < this.Count)
                    Array.Copy(this.items, index + count, this.items, index, this.Count - index);
                Array.Clear(this.items, this.Count, count);
            }
        }

        public int RemoveAll(Predicate<T> remove)
        {
            ArgumentNullException.ThrowIfNull(remove);
            int count = this.Count, index = 0, left = 0;
            while (index < count)
            {
                if (!remove(this.items[index]))
                { // items will stay
                    if (left != index) // if has deleted
                        this.items[left] = this.items[index];
                    left++;
                }
                index++;
            }
            // index == count, left - items to leave
            if ((index -= left) > 0) // index = deleted items
                Array.Clear(this.items, this.Count = left, index);
            return index;
        }

        private void EnsureCapacity(int min)
        {
            if (this.items.Length < min)
            {
                int num = (this.items.Length == 0) ? 4 : (this.items.Length * 2);
                if (num < min) num = min;
                this.Capacity = num;
            }
        }

        private void insert(int index, T item)
        {
            if (this.Count == this.items.Length)
                this.EnsureCapacity(this.Count + 1);

            if (index < this.Count)
                Array.Copy(this.items, index, this.items, index + 1, this.Count - index);

            this.items[index] = item;
            this.Count++;
        }

        private void appendSortedRange(T[] items, int start, int count)
        {
            this.EnsureCapacity(this.Count + count);
            Array.Copy(items, start, this.items, this.Count, count);
            this.Count += count;
        }

        private void prependSortedRange(T[] items, int start, int count)
        {
            this.EnsureCapacity(this.Count + count);
            Array.Copy(this.items, 0, this.items, count, this.Count);
            Array.Copy(items, start, this.items, 0, count);
            this.Count += count;
        }

        private void mergeSortedRange(T[] items, int start, int count)
        {
            int oldCount = this.Count;
            this.EnsureCapacity(oldCount + count);

            T[] target = this.items;
            int oldIndex = oldCount - 1;
            int newIndex = start + count - 1;
            int writeIndex = oldCount + count - 1;

            this.Count = oldCount + count;

            while (oldIndex >= 0 && newIndex >= start)
                target[writeIndex--] = this.Compare(target[oldIndex], items[newIndex]) > 0
                    ? target[oldIndex--]
                    : items[newIndex--];

            int remaining = newIndex - start + 1;
            if (remaining > 0)
                Array.Copy(items, start, target, writeIndex - remaining + 1, remaining);
        }

        /// <summary>
        /// Behavior on Equal item found
        /// </summary>
        public enum EqualFound
        {
            /// <summary>
            /// Add new item as well
            /// </summary>
            Add,
            /// <summary>
            /// Skip adding new item
            /// </summary>
            Skip,
            /// <summary>
            /// Replace item (remove existing item) with the new one
            /// </summary>
            Replace
        };

        /// <summary>
        /// Add sorted item info sorted list
        /// </summary>
        /// <param name="item">item to add</param>
        /// <param name="found">behavior on equal item found
        /// on <see cref="EqualFound.Add"/> - just insert it before it
        /// on <see cref="EqualFound.Skip"/> - do nothing
        /// on <see cref="EqualFound.Replace"/> - replace item with given
        /// </param>
        /// <returns>if not found or <see cref="EqualFound.Add"/> - return inserted item index, 
        /// on equal item found and <see cref="EqualFound.Skip"/> or <see cref="EqualFound.Replace"/> - negated index value
        /// </returns>
        public int Add(T item, EqualFound found)
        {
            int index;
            if (this.Count <= 0) index = 0; // if no elements - add first
            else if ((index = this.BinarySearch(item)) < 0) index = ~index; // get element index to add
            else switch (found)
                { // equal item found
                    case EqualFound.Replace: // replace found item with given one
                        this.items[index] = item;
                        return ~index;
                    case EqualFound.Skip: // do nothing - just return
                        return ~index;
                        //case EqualItemFound.Add:
                }
            this.insert(index, item);
            return index;
        }

        /// <summary>
        /// Add sorted item - identical to <see cref="Add(T, EqualFound)"/> with <see cref="EqualFound.Add"/>
        /// </summary>
        /// <param name="item">item to add</param>
        /// <returns>item index</returns>
        public int Add(T item) => this.Add(item, EqualFound.Add);

        /// <summary>
        /// Add Sorted range (with grouped index range)
        /// The idea is to have performance gain if items to be inserted are in close index range
        /// 
        /// If randomly added, better just use <see cref="Add(T)"/>
        /// </summary>
        /// <param name="items"></param>
        /// <param name="start"></param>
        /// <param name="count"></param>
        public void AddSortedRange(T[] items, int start = 0, int count = int.MinValue)
        {
            if (count == int.MinValue) count = items.Length - start;
            if (count == 0) return;
            if (count == 1) { this.Add(items[start]); return; }

            if (this.Count == 0)
            {
                this.EnsureCapacity(count);
                Array.Copy(items, start, this.items, 0, this.Count = count);
                return;
            }

            T first = items[start];
            T last = items[start + count - 1];
            if (this.Compare(this.items[this.Count - 1], first) <= 0)
            {
                this.appendSortedRange(items, start, count);
                return;
            }

            if (this.Compare(last, this.items[0]) <= 0)
            {
                this.prependSortedRange(items, start, count);
                return;
            }

            int firstIndex = this.BinarySearch(first);
            if (firstIndex < 0) firstIndex = ~firstIndex;

            int lastIndex = this.BinarySearch(last);
            if (lastIndex < 0) lastIndex = ~lastIndex;

            if (count > 32 && lastIndex - firstIndex > 32)
            {
                this.mergeSortedRange(items, start, count);
                return;
            }

            this.EnsureCapacity(this.Count + count);

            { // non empty list
                ref T item = ref last;
                int hi = lastIndex;

                T[] targetItems = this.items;
                if (hi < this.Count) // move all higher items forward
                    Array.Copy(targetItems, hi, targetItems, hi + count, this.Count - hi);

                this.Count += count--;
                targetItems[hi + count] = item; // set last item

                int lo = firstIndex;
                count += start;
                while (start < count)
                {
                    item = ref items[start++];
                    if (lo < hi)
                    { // possible last index is before first
                        if ((lo = this.BinarySearch(item, lo, hi - lo)) < 0) lo = ~lo;
                        if (lo < hi)
                            Array.Copy(targetItems, lo, targetItems, lo + 1, hi++ - lo);
                    }
                    targetItems[lo++] = item; // set last item
                }
            }
        }

        /// <summary>
        /// Add unsorted range. The new items are sorted first and then merged into the existing sorted list.
        /// For already sorted items, use <see cref="AddSortedRange(T[], int, int)"/> to skip the first sort.
        /// </summary>
        /// <param name="collection"></param>
        public void AddRange(IEnumerable<T> collection) => this.AddSortedRange(this.ToSortedArray(collection));

        /// <summary>
        /// return item to array
        /// </summary>
        /// <returns></returns>
        public T[] ToArray()
        {
            if (this.Count == 0) return EmptyArray;
            T[] destinationArray = new T[this.Count];
            Array.Copy(this.items, 0, destinationArray, 0, this.Count);
            return destinationArray;
        }

        /// <summary>
        /// Replace, but in reality - 2 operations
        /// 1. Remove item at removeAt index
        /// 2. Add new item
        /// 
        /// This is for performance - with maximum one <see cref="Array.Copy(Array, int, Array, int, int)"/> command
        /// </summary>
        /// <param name="removeAt"></param>
        /// <param name="item"></param>
        /// <returns></returns>
        public int Replace(int removeAt, T item)
        {
            int index = this.BinarySearch(item);
            if (index < 0) index = ~index;

            if (removeAt < index)
            { // item is after removed
                if (removeAt < --index) // index decrease (item will be removed)
                    Array.Copy(this.items, removeAt + 1, this.items, removeAt, index - removeAt);
            }
            else if (index < removeAt) // item is before removed item
                Array.Copy(this.items, index, this.items, index + 1, removeAt - index);

            this.items[index] = item;
            return index;
        }

        /// <summary>
        /// Remove one item and add sorted array of items (in close indexing range)
        /// The idea is to have performance gain if items to be inserted are in close index range
        /// 
        /// If randomly added, better just use <see cref="Add(T)"/>
        /// </summary>
        /// <param name="removeAt"></param>
        /// <param name="items"></param>
        /// <param name="start"></param>
        /// <param name="count"></param>
        public void ReplaceSortedRange(int removeAt, T[] items, int start = 0, int count = int.MinValue)
        {
            if (count == int.MinValue) count = items.Length - start;

            if (count == 0)
            {
                this.RemoveAt(removeAt);
                return;
            }

            if (this.Count == 1)
            {
                System.Diagnostics.Debug.Assert(removeAt == 0);
                this.EnsureCapacity(count);
                Array.Copy(items, start, this.items, 0, this.Count = count);
            }
            else
            {
                if (count == 1)
                {
                    this.Replace(removeAt, items[start]);
                    return;
                }

                ref T item = ref items[start + --count];
                int hi = this.Replace(removeAt, item);

                this.EnsureCapacity(this.Count + count);

                T[] targetItems = this.items;
                if (hi < this.Count) // move all higher items forward
                    Array.Copy(targetItems, hi, targetItems, hi + count, this.Count - hi);

                this.Count += count;
                targetItems[hi + count] = item; // set last item

                int lo = 0;
                count += start;
                while (start < count)
                {
                    item = ref items[start++];
                    if (lo < hi)
                    { // possible last index is before first
                        if ((lo = this.BinarySearch(item, lo, hi - lo)) < 0) lo = ~lo;
                        if (lo < hi)
                            Array.Copy(targetItems, lo, targetItems, lo + 1, hi++ - lo);
                    }
                    targetItems[lo++] = item; // set last item
                }
            }
        }

        /// <summary>
        /// Remove one item and add items(in close indexing range)
        /// The idea is to have performance gain if items to be inserted are in close index range
        /// 
        /// If randomly added, better just use <see cref="Add(T)"/>
        /// </summary>
        /// <param name="removeAt"></param>
        /// <param name="collection"></param>
        public void ReplaceRange(int removeAt, IEnumerable<T> collection) => this.ReplaceSortedRange(removeAt, this.ToSortedArray(collection));

        void ICollection<T>.Add(T item) => this.Add(item);

        /// <summary>
        /// clear all items
        /// </summary>
        public void Clear()
        {
            this.Count = 0;
            this.items = EmptyArray;
        }

        /// <summary>
        /// Clear all items, keeping the allocated array for immediate reuse.
        /// </summary>
        public void ClearKeepingCapacity()
        {
            if (ContainsReferences && this.Count > 0)
                Array.Clear(this.items, 0, this.Count);

            this.Count = 0;
        }

        /// <summary>
        /// get item at index
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public T this[int index] => this.items[index];
        T IList<T>.this[int index] { get => this.items[index]; set => throw new NotSupportedException("Use Add() instead"); }

        void IList<T>.Insert(int index, T item) => throw new NotSupportedException("Use Add() instead");

        #region Find methods

        private int GetFirstEqualIndexFrom(T item, int index)
        {
            while (index > 0) // has room to check
                if (this.Compare(item, this.items[--index]) != 0) // not equal found
                    return index + 1;
            return index;
        }

        /// <summary>
        /// Find index of first equal item (if repeated - binary search will give random)
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public int FindFirstEqualIndex(T item) => this.GetFirstEqualIndexFrom(item, this.BinarySearch(item));

        private int GetLastEqualIndexFrom(T item, int index)
        {
            if (index < 0) return index;
            int last = this.Count - 1;
            while (index < last)
                if (this.Compare(item, this.items[++index]) != 0) // not equal found
                    return index - 1;
            return index;
        }

        /// <summary>
        /// Find index of last equal item (if repeated - binary search will give random)
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public int FindLastEqualIndex(T item) => this.GetLastEqualIndexFrom(item, this.BinarySearch(item));

        /// <summary>
        /// Get Equal indices as index range
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public Int32Interval FindEqualIndices(T item)
        {
            int index = this.BinarySearch(item);
            if (index < 0) return new Int32Interval(index, index);
            return new Int32Interval(this.GetFirstEqualIndexFrom(item, index), this.GetLastEqualIndexFrom(item, index) + 1);
        }

        /// <summary>
        /// Get all equal elements as array
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public T[] FindEqual(T item)
        {
            Int32Interval range = this.FindEqualIndices(item);
            T[] ret = new T[range.Count];
            int ii = 0;
            for (int i = range.Start; i < range.End; i++)
                ret[ii++] = this.items[i];
            return ret;
        }

        /// <summary>
        /// Find index of last item less than value
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public int FindLastLessIndex(T item)
        {
            int index = this.FindFirstEqualIndex(item);
            if (index < 0) index = ~index;
            return index - 1;
        }

        /// <summary>
        /// Find index of first greater item than value
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public int FindFirstGreaterIndex(T item)
        {
            int index = this.FindLastEqualIndex(item);
            if (index < 0) index = ~index;
            else index++;
            return index >= this.Count ? -1 : index;
        }

        #endregion

        /// <summary>
        /// Check if contains equal item
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public bool ContainsEqual(T item) => this.BinarySearch(item) >= 0;

        /// <summary>
        /// Check if contains item by comparer
        /// </summary>
        /// <param name="item"></param>
        /// <param name="comparer"></param>
        /// <returns></returns>
        public bool Contains(T item) => this.IndexOf(item) >= 0;

        /// <summary>
        /// Check index of item, using comparer to search just equal range)
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public int IndexOf(T item) => this.IndexOf(item, null);

        /// <summary>
        /// Check index of item, using comparer to search just equal range)
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public int IndexOf(T item, IEqualityComparer<T>? comparer)
        {
            int start = this.BinarySearch(item);
            if (start < 0) return start;

            if ((comparer ??= EqualityComparer<T>.Default).Equals(item, this.items[start]))
                return start;

            // search down
            int index = start;
            while (--index >= 0)
                if (this.Compare(item, this.items[index]) != 0) break;
                else if (comparer.Equals(item, this.items[index])) return index;

            // search up
            index = start;
            while (++index < this.Count)
                if (this.Compare(item, this.items[index]) != 0) break;
                else if (comparer.Equals(item, this.items[index])) return index;

            return ~start; // not found
        }

        public void CopyTo(T[] array, int arrayIndex) => Array.Copy(this.items, 0, array, arrayIndex, this.Count);

        /// <summary>
        /// Remove item
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public bool Remove(T item)
        {
            int index = this.IndexOf(item);
            if (index < 0) return false;
            this.RemoveAt(index);
            return true;
        }

        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < this.Count; i++)
                yield return this.items[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
    }
}
