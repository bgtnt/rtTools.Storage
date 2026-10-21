using System;
using System.Collections;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace RtTools
{

    /// <summary>
    /// Class, representing list with Limited count of elements
    /// Until Count==Limit it is as usual List,
    /// when Count=Limit condition reached it behaves as auto FIFO queue (if new element is added, first(oldest) is out)
    /// Count>Limit is impossible situation
    /// </summary>
    /// <typeparam name="T"></typeparam>
    
    public sealed class LimitedList<T> : IList<T>, ICollection<T>, IEnumerable<T>, IEnumerable
    {

        private const string CollectionModifiedExceptionMessage = "Collection size was modified while enumerated";
        private const string IndexOutOfRangeMessage = "Index out of range";
        private const string CountOutOfRangeMessage = "Count out of range";
        private const string LimitRangeMessage = "Limit must be >0";

        /// <summary>
        /// collection storage array
        /// </summary>
        private T[] items;
        /// <summary>
        /// collection data start index
        /// </summary>
        private int start;

        /// <summary>
        /// collection size limit
        /// </summary>
        private int limit;

        /// <summary>
        /// Constructs new list
        /// </summary>
        /// <param name="limit">list limit</param>
        public LimitedList(int limit)
        {
            if (limit < 1)
                throw new ArgumentOutOfRangeException(nameof(limit), limit, LimitRangeMessage);

            this.start = this.Count = 0;
            this.limit = limit;
            this.items = new T[this.limit];
        }

        #region public properties

        /// <summary>
        /// Gets current count (less or equal to Limit)
        /// </summary>
        [XmlIgnore]
        public int Count { get; private set; }

        [XmlIgnore]
        bool ICollection<T>.IsReadOnly => false;

        /// <summary>
        /// Get item by index
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public T this[int index]
        {
            get
            {
                if (index < 0 || this.Count <= index)
                    throw new IndexOutOfRangeException(IndexOutOfRangeMessage);
                return this.items[(this.start + index) % this.limit];
            }
            set
            {
                if (index < 0 || this.Count <= index)
                    throw new IndexOutOfRangeException(IndexOutOfRangeMessage);
                this.items[(this.start + index) % this.limit] = value;
            }
        }

        /// <summary>
        /// Get/Set limited queue limit
        /// </summary>
        [XmlIgnore]
        public int Limit
        {
            get => this.limit;
            set
            {
                if (this.limit != value)
                {
                    if (value < 1)
                        throw new ArgumentOutOfRangeException(nameof(value), value, LimitRangeMessage);
                    T[] newArray = new T[value];

                    if (this.Count > 0)
                    { // has elements to copy
                        int start = this.start;
                        int end = (start + this.Count) % this.limit;
                        if (start < end)
                        { // normal order start..end-1
                            if (value < this.Count)
                            { // do not copy full size - just last (value) items
                                start += (this.Count - value);
                                this.Count = value;
                            }
                            Array.Copy(this.items, start, newArray, 0, this.Count);
                        }
                        else
                        { // order circled start..limit-1,0..end-1
                            if (value <= end)
                            { // second chunk is greater than new limit
                                Array.Copy(this.items, end - value, newArray, 0, this.Count = value);
                            }
                            else
                            { // value > end(second chunk) should copy both chunks
                                int size0 = Math.Min(this.Count, value) - end;
                                // copy second chunk
                                Array.Copy(this.items, 0, newArray, size0, end);
                                // copy first chunk
                                Array.Copy(this.items, this.limit - size0, newArray, 0, size0);
                                this.Count = size0 + end;
                            }
                        }
                    }
                    this.items = newArray;
                    this.start = 0;
                    this.limit = value;
                }
            }
        }

        /// <summary>
        /// like List.ForEach
        /// </summary>
        /// <param name="action"></param>
        public void ForEach(Action<T> action)
        {
            ArgumentNullException.ThrowIfNull(action);

            int index = this.start, limit = this.limit, count = this.Count;
            while (count-- > 0)
            {
                action(this.items[index++]);
                index %= limit;
            }
        }

        /// <summary>
        /// Like List.ConvertAll
        /// </summary>
        /// <typeparam name="TOutput"></typeparam>
        /// <param name="converter"></param>
        /// <returns>new LimitedList01 with the same limit as original</returns>
        public LimitedList<TOutput> ConvertAll<TOutput>(Converter<T, TOutput> converter)
        {
            ArgumentNullException.ThrowIfNull(converter);

            int index = this.start, limit = this.limit, count = this.Count;
            LimitedList<TOutput> ret = new(this.limit);
            while (count-- > 0)
            {
                ret.Add(converter(this.items[index++]));
                index %= limit;
            }
            return ret;
        }

        /// <summary>
        /// Returns List of elements
        /// </summary>
        /// <typeparam name="TOutput"></typeparam>
        /// <param name="converter"></param>
        /// <returns></returns>
        public List<TOutput> ConvertToList<TOutput>(Converter<T, TOutput> converter)
        {
            ArgumentNullException.ThrowIfNull(converter);

            int index = this.start, limit = this.limit, count = this.Count;
            List<TOutput> ret = new(count);
            while (count-- > 0)
            {
                ret.Add(converter(this.items[index++]));
                index %= limit;
            }
            return ret;
        }

        /// <summary>
        /// Returns List of elements
        /// </summary>
        /// <typeparam name="TOutput"></typeparam>
        /// <param name="converter"></param>
        /// <returns></returns>
        public TOutput[] ConvertToArray<TOutput>(Converter<T, TOutput> converter)
        {
            ArgumentNullException.ThrowIfNull(converter);

            int index = this.start, limit = this.limit, count = this.Count;
            TOutput[] ret = new TOutput[count];
            for (int i = 0; i < count; i++)
            {
                ret[i] = converter(this.items[index++]);
                index %= limit;
            }
            return ret;
        }

        #endregion

        #region IList<T> Members IndexOf, Insert, RemoveAt

        /// <summary>
        /// Get item index
        /// </summary>
        /// <param name="item">item to find</param>
        /// <returns>item index, or -1</returns>
        public int IndexOf(T item)
        {
            int ret = -1;
            if (this.Count > 0)
            { // has elements to copy
                int start = this.start;
                int end = (start + this.Count) % this.limit;
                if (start < end)
                { // normal order start..end-1
                    ret = Array.IndexOf(this.items, item, start, end - start);
                    if (ret >= start)
                        ret -= start;
                }
                else
                { // order circled start..limit-1,0..end-1
                    int size0 = this.limit - start;

                    ret = Array.IndexOf(this.items, item, start, size0);
                    if (ret >= start) // found
                        ret -= start;
                    else
                    { // not found
                        ret = Array.IndexOf(this.items, item, 0, end);
                        if (ret >= 0) // found
                            ret += size0;
                    }
                }
            }
            return ret;
        }

        /// <summary>
        /// Insert item at index (if limit is reached, first element (#0) is removed)
        /// </summary>
        /// <param name="index"></param>
        /// <param name="item"></param>
        public void Insert(int index, T item) => this.InsertFirstOut(index, item);

        /// <summary>
        /// Insert item before #index, if limit is reached, first item is out
        /// 1. Insert item at #index
        /// 2. If size>limit, remove item#0
        /// 
        /// means: 
        /// - if insert #index=0, and size==limit, nothing is done, returns -1
        /// - if size==limit, item is inserted at #index, but item#0 is removed, so actually is inserted at #index-1
        /// </summary>
        /// <param name="index">index, before which to be inserted</param>
        /// <param name="item">item to be inserted</param>
        /// <returns>actually inserted item index, or -1 (if item is inserted at #0 and removed, because of limit)</returns>
        public int InsertFirstOut(int index, T item)
        {
            if (index < 0 || index > this.Count)
                throw new ArgumentOutOfRangeException(nameof(index), index, IndexOutOfRangeMessage);

            if (index == 0)
            { // insert at first position (has reason only if size<limit)

                if (this.Count < this.limit)
                { // has room to grow

                    // increase list size
                    this.Count++;
                    // decrease start position and update first element 
                    this.items[this.start == 0 ? (this.start = this.limit - 1) : (--this.start)] = item;
                }
                else // nothing to insert (immediately remove inserted item, because of list limit)
                    index--;

            }
            else if (index == this.Count)
            { // insert at last index position =add(item)
                this.items[(this.start + this.Count) % this.limit] = item;

                if (this.Count < this.limit) // has room to grow
                    this.Count++;
                else
                { // shift start index
                    if (++this.start == this.limit)
                        this.start = 0;

                    index--; // newly inserted item is at index-1 (inserted at index, but due to limit, shifted one left)
                }
            }
            else
            { // 0 < index < count
                int start = this.start;
                bool grow;
                if (grow = (this.Count < this.limit))
                { // has room to grow - increase size, decrease start index
                    this.Count++;
                }
                else // list is limited
                    if (index == 1)
                    { // if max size and insert before 1 (new 0) - just replace 0 index
                        this.items[start] = item;
                        return 0;
                    }

                int iindex = (start + index) % this.limit;

                if (start < iindex) // not reverted before index
                { // start..iindex - shift this slice back

                    iindex--;

                    if (grow)
                    { // shift starting index one left/backwards
                        if (start == 0)
                            this.items[this.start = this.limit - 1] = this.items[0]; // shift
                        else
                            start = --this.start;
                    }

                    // shift all previous elements one left/backwards
                    Array.Copy(this.items, start + 1, this.items, start, iindex - start);

                    // shift index one left/backwards
                    this.items[iindex >= 0 ? iindex : (this.limit - 1)] = item; // update element 
                }
                else
                { // reverted before index (0..iindex..end..start..limit-1)
                    int end = (start + this.Count) % this.limit;

                    if (grow) // list extended 
                        end--;
                    else // shift starting index one right/forwards
                        this.start = (start + 1) % this.limit;

                    // shift all next elements one right/forwards
                    Array.Copy(this.items, iindex, this.items, iindex + 1, end - iindex);

                    // update element
                    this.items[iindex] = item;
                }

                if (!grow) // if list is at limit, inserted index is shifted one left
                    index--;

            }

            return index; // newly inserted item is at index
        }

        /// <summary>
        /// Insert item before #index, if limit is reached, last item is out
        /// 1. Insert item at #index
        /// 2. If size>limit, remove item #count-1
        /// 
        /// means: 
        /// - if insert #index=count, and size==limit, nothing is done, returns -1
        /// </summary>
        /// <param name="index">index, before which to be inserted</param>
        /// <param name="item">item to be inserted</param>
        /// <returns>actually inserted item index, or -1 (if item is inserted at #0 and removed, because of limit)</returns>
        public int InsertLastOut(int index, T item)
        {
            if (index < 0 || index > this.Count)
                throw new ArgumentOutOfRangeException(nameof(index), index, IndexOutOfRangeMessage);

            if (index == 0)
            { // insert at first position (has reason only if size<limit)
                if (this.Count < this.limit)
                { // has room to grow
                    this.Count++; // increase size
                }

                // decrease start position and update first element 
                this.items[this.start == 0 ? (this.start = this.limit - 1) : (--this.start)] = item;
            }
            else if (index == this.Count)
            { // insert at last index position =add(item)

                if (this.Count >= this.limit)
                    // nothing to insert (immediately remove inserted item, because of list limit)
                    return -1;

                // has room to grow
                this.items[(this.start + this.Count++) % this.limit] = item;
            }
            else
            { // 0 < index < count
                int start = this.start;
                int iindex = (start + index) % this.limit;

                if (this.Count < this.limit)
                { // has room to grow - increase size, decrease start index
                    this.Count++;
                }
                else // list is limited
                    if (index == this.Count - 1)
                    { // if max size and insert at last index - just replace it
                        this.items[iindex] = item;
                        return index;
                    }


                if (start > iindex)
                { // reverted after index (0..iindex..end..start..limit-1)

                    // iindex..end - shift this slice forward
                    int end = (start + this.Count - 1) % this.limit;

                    // shift all previous elements one right/forwards
                    Array.Copy(this.items, iindex, this.items, iindex + 1, end - iindex);

                    // update element 
                    this.items[iindex] = item;
                }
                else // not reverted before index (start < index)
                { // shift slice (start..iindex) back

                    iindex--;

                    // shift starting index one left/backwards
                    if (start == 0)
                        this.items[this.start = this.limit - 1] = this.items[0]; // shift
                    else
                        start = --this.start;

                    // shift all previous elements one left/backwards
                    Array.Copy(this.items, start + 1, this.items, start, iindex - start);

                    // shift index one left/backwards
                    this.items[iindex >= 0 ? iindex : (this.limit - 1)] = item; // update element 
                }
            }

            return index;
        }

        /// <summary>
        /// Remove item by index
        /// </summary>
        /// <param name="index">index to remove</param>
        public void RemoveAt(int index)
        {
            if (index < 0 || index >= this.Count)
                throw new ArgumentOutOfRangeException(nameof(index), index, IndexOutOfRangeMessage);

            this.Count--;
            int start = this.start;

            // if index == size do nothing
            if (index == 0)
            { // remove first - just shift start index
                this.items[start++] = default!;
                this.start = (start == this.limit) ? 0 : start;
            }
            else
            {
                int itemIndex = (start + index) % this.limit;

                if (index == this.Count) // removing last means - just decrease size
                    this.items[itemIndex] = default!;
                else
                { // 0 < index < count-1
                    if (start < itemIndex)
                    { // not reverted before itemIndex (start..itemIndex)
                        Array.Copy(this.items, start, this.items, start + 1, itemIndex - start);
                        this.items[this.start++] = default!;
                    }
                    else
                    { // reverted before itemIndex (0..itemIndex..end..start..limit-1)
                        int end = (start + this.Count + 1) % this.limit;
                        Array.Copy(this.items, itemIndex + 1, this.items, itemIndex, end-- - itemIndex);
                        this.items[end] = default!;
                    }
                }
            }
        }

        /// <summary>
        /// Remove items range
        /// </summary>
        /// <param name="index">index to remove</param>
        /// <param name="count">count to remove</param>
        public void RemoveRange(int index, int count)
        {
            if (index < 0 || index > this.Count)
                throw new ArgumentOutOfRangeException(nameof(index), index, IndexOutOfRangeMessage);

            if (count < 0 || count > this.Count - index)
                throw new ArgumentOutOfRangeException(nameof(count), count, CountOutOfRangeMessage);

            if (count > 0)
            { // has leas something to remove
                int start = this.start;

                // if index == size do nothing
                if (index == 0)
                { // remove from the beginning - just shift start index right

                    // increase start
                    if ((start += count) < this.limit) // not reverted
                        Array.Clear(this.items, this.start, count);
                    else
                    { // start is over limit
                        Array.Clear(this.items, this.start, this.limit - this.start);
                        Array.Clear(this.items, 0, (start -= this.limit));
                    }
                    // set new start position
                    this.start = start;
                }
                else
                {
                    int itemIndex = (start + index) % this.limit;
                    int toIndex = (itemIndex + count) % this.limit;

                    if (index + count >= this.Count)
                    { // remove last elements means - just decrease size
                        if (itemIndex <= toIndex) // not reverted
                            Array.Clear(this.items, itemIndex, count);
                        else
                        { // toIndex is at reverted side
                            Array.Clear(this.items, itemIndex, this.limit - itemIndex);
                            Array.Clear(this.items, 0, toIndex);
                        }
                    }
                    else
                    { // remove somewhere in the middle (0 < index < count-1)
                        if (itemIndex <= toIndex)
                        { // not reverted
                            if (start < itemIndex)
                            { // not reverted before itemIndex (start..itemIndex..toIndex) - just shift (start..itemIndex) right by count + increase start
                                Array.Copy(this.items, start, this.items, start + count, index);
                                Array.Clear(this.items, start, count);
                                this.start += count;
                            }
                            else
                            { // reverted before itemIndex (0..itemIndex..toIndex..end..start..limit-1) - shift (toIndex..end) left by count
                                int end = (start + this.Count) % this.limit;
                                Array.Copy(this.items, toIndex, this.items, itemIndex, end - toIndex);
                                Array.Clear(this.items, end - count, count);
                            }
                        }
                        else
                        { // toIndex is at reverted side (0..toIndex..end..start..itemIndex..limit-1)
                            if (index <= toIndex)
                            { // move (start..itemIndex) into the end of the interval (0..clearEnd)
                                Array.Copy(this.items, start, this.items, (this.start = toIndex - index), index);
                                Array.Clear(this.items, start, this.limit - start);
                                Array.Clear(this.items, 0, this.start);
                            }
                            else
                            {
                                int end = (start + this.Count) - this.limit;
                                System.Diagnostics.Debug.Assert(end >= 0);

                                if (end < count) // [start + Count - count < limit] <=> [start + Count - limit < count]
                                { // move (toIndex..end) into start of  the interval (itemIndex..limit-1)
                                    int copyCount = end - toIndex;
                                    Array.Copy(this.items, toIndex, this.items, itemIndex, copyCount);
                                    int clearEnd = itemIndex + copyCount;
                                    Array.Clear(this.items, clearEnd, this.limit - clearEnd);
                                    Array.Clear(this.items, 0, end);
                                }
                                else
                                { // worst case - have to move both (toIndex..end) - left to #0; and (start..itemIndex) right to limit-1
                                    // (0..toIndex..end..start..itemIndex..limit-1)

                                    // move (toIndex..end) - left to #0;
                                    int clearEnd = end - toIndex;
                                    Array.Copy(this.items, toIndex, this.items, 0, clearEnd);
                                    Array.Clear(this.items, clearEnd, toIndex);

                                    // move (start..itemIndex) right to limit-1
                                    int copyCount = this.limit - itemIndex;
                                    Array.Copy(this.items, start, this.items, (this.start += copyCount), index);
                                    Array.Clear(this.items, start, copyCount);
                                }
                            }
                        }
                    }
                }
                // decrease size
                this.Count -= count;
            }
        }

        #endregion

        #region ICollection<T> Members Add, Remove, Clear, CopyTo,

        /// <summary>
        /// Add new item
        /// </summary>
        /// <param name="item"></param>
        public void Add(T item)
        {
            this.items[(this.start + this.Count) % this.limit] = item;

            if (this.Count < this.limit) // increase size
                this.Count++;
            else // remove first element
                if (++this.start == this.limit)
                    this.start = 0;
        }

        /// <summary>
        /// Clear all items
        /// </summary>
        public void Clear()
        {
            this.items = new T[this.limit];
            this.Count = this.start = 0;
        }

        /// <summary>
        /// Remove item (if exists)
        /// </summary>
        /// <param name="item">item to remove</param>
        /// <returns>true if removed</returns>
        public bool Remove(T item)
        {
            int i = this.IndexOf(item);
            if (i >= 0)
            {
                this.RemoveAt(i);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Check if list contains item
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public bool Contains(T item)
        {
            return this.IndexOf(item) >= 0;
        }

        /// <summary>
        /// Get Elements as array
        /// </summary>
        /// <returns></returns>
        public T[] ToArray()
        {
            T[] ret = new T[this.Count];
            this.CopyTo(ret, 0);
            return ret;
        }

        /// <summary>
        /// Copy all list elements into array
        /// </summary>
        /// <param name="array">array to copy elements into</param>
        /// <param name="arrayIndex">array element to start</param>
        public void CopyTo(T[] array, int arrayIndex)
        {
            ArgumentNullException.ThrowIfNull(array);

            int length = array.Length;
            int size = this.Count;
            if (((arrayIndex < 0) || (arrayIndex > length)) || (length < (arrayIndex + size)))
                throw new ArgumentOutOfRangeException(IndexOutOfRangeMessage);

            int copy = (length < (arrayIndex + size)) ? (length - arrayIndex) : size;
            if (copy > 0)
            {
                int start = this.start;
                int end = (start + this.Count) % this.limit;
                if (start < end)
                { // normal order start..end-1
                    Array.Copy(this.items, start, array, arrayIndex, copy);
                }
                else
                { // order circled start..limit-1,0..end-1
                    int size0 = this.limit - start;
                    if (copy <= size0)
                    { // first chunk is greater than new limit
                        Array.Copy(this.items, start, array, arrayIndex, copy);
                    }
                    else
                    { // should copy both chunks
                        // copy first chunk
                        Array.Copy(this.items, start, array, arrayIndex, size0);
                        int left = Math.Min(end, copy - size0); // room left in new array
                        Array.Copy(this.items, 0, array, arrayIndex + size0, left);
                    }
                }
            }
        }

        #region IEnumerable<T> Members

        /// <summary>
        /// Get items enumerator
        /// </summary>
        /// <returns></returns>
        public IEnumerator<T> GetEnumerator() => new Enumerator(this);

        IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this);

        #endregion

        #endregion

        #region Enumerator structure

        private struct Enumerator : IEnumerator<T>, IDisposable, IEnumerator
        {
            private readonly LimitedList<T> list;
            private int index;
            private readonly int size, start;
            private T current;
            internal Enumerator(LimitedList<T> list)
            {
                this.list = list;
                this.index = 0;
                this.start = list.start;
                this.size = list.Count;
                this.current = default!;
            }

            public readonly void Dispose() { }

            public bool MoveNext()
            {
                if (this.size != this.list.Count || this.start != this.list.start)
                    throw new InvalidOperationException(CollectionModifiedExceptionMessage);

                if (this.index < this.size)
                {
                    this.current = this.list.items[(this.start + this.index) % this.list.limit];
                    this.index++;
                    return true;
                }
                this.index = this.size + 1;
                this.current = default!;
                return false;
            }

            public readonly T Current => this.current;

            readonly object IEnumerator.Current
            {
                get
                {
                    if ((this.index <= 0) || (this.index > this.list.Count))
                        throw new ArgumentOutOfRangeException(IndexOutOfRangeMessage);
                    return this.Current!;
                }
            }
            void IEnumerator.Reset()
            {
                if (this.size != this.list.Count || this.start != this.list.start)
                    throw new InvalidOperationException(CollectionModifiedExceptionMessage);
                this.index = 0;
                this.current = default!;
            }
        }

        #endregion

        /// <summary>
        /// Creates a shallow copy of the list.
        /// </summary>
        /// <returns></returns>
        public LimitedList<T> Copy()
        {
            LimitedList<T> ret = new(this.limit);
            ret.items = (T[])this.items.Clone();
            ret.start = this.start;
            ret.Count = this.Count;
            return ret;
        }

        #region BinarySearch methods

        /// <summary>
        /// Uses a binary search algorithm to locate a specific element in the sorted list
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public int BinarySearch(T item) => this.BinarySearch(0, this.Count, item, null);

        /// <summary>
        /// Uses a binary search algorithm to locate a specific element in the sorted list
        /// </summary>
        /// <param name="item"></param>
        /// <param name="comparer"></param>
        /// <returns></returns>
        public int BinarySearch(T item, IComparer<T>? comparer) => this.BinarySearch(0, this.Count, item, comparer);

        /// <summary>
        /// Uses a binary search algorithm to locate a specific element in the sorted list or a portion of it.
        /// </summary>
        /// <param name="index">The zero-based starting index of the range to search.</param>
        /// <param name="count">The length of the range to search.</param>
        /// <param name="value">The object to locate. The value can be null for reference types.</param>
        /// <param name="comparer">The IComparer implementation to use when comparing elements, or null to use the default comparer </param>
        /// <returns>The zero-based index of item in the sorted if found; 
        /// otherwise, a negative number that is the bitwise complement of the index of the next element that is larger than item or, 
        /// if there is no larger element, the bitwise complement of Count</returns>
        public int BinarySearch(int index, int count, T value, IComparer<T>? comparer)
        {
            if ((index < 0) || (count < 0))
                throw new ArgumentOutOfRangeException((index < 0) ? "index" : "count");

            if ((this.Count - index) < count)
                throw new ArgumentException("Invalid list index");

            comparer ??= Comparer<T>.Default;

            int limit = this.limit;
            int start = this.start;
            T[] array = this.items;

            int i = index;
            int mid = (index + count) - 1;
            while (i <= mid)
            {
                int mid1 = (i + mid) / 2;
                int res = comparer.Compare(value, array[(start + mid1) % limit]);
                if (res == 0) return mid1;
                if (res < 0)
                    mid = mid1 - 1;
                else
                    i = mid1 + 1;
            }
            return ~i;
        }

        #endregion
    }
}

