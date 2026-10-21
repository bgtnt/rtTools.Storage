using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics.CodeAnalysis;

namespace RtTools
{
    using SortIndex = System.Single;

    /// <summary>
    /// Represents a file-backed list.
    /// </summary>
    /// <remarks>
    /// Existing finite equal sort labels are accepted for compatibility. Ties load in physical
    /// record-ID order, which may change after a relocating update; their original logical order
    /// cannot be recovered. Insertions require a strictly available label and do not renumber old records.
    /// </remarks>
    /// <typeparam name="T"></typeparam>
    public sealed class ListPersistent<T> : IList<T>, IDisposable
    {
        #region ListItem class

        private sealed class ListItem(SortIndex index, T value, long persistId = long.MinValue)
        {
            public SortIndex Index { get; } = index;
            public T Value { get; set; } = value;
            public long PersistId { get; set; } = persistId;
        }

        private readonly Converter<byte[], T>? itemReader;
        private readonly Converter<T, byte[]>? itemWriter;

        private ListItem FromBytes(byte[] data, long persistId = long.MinValue)
        {
            SortIndex index = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data));
            T value;
            if (this.itemReader != null)
            {
                byte[] shifted = new byte[data.Length - sizeof(SortIndex)];
                Array.Copy(data, sizeof(SortIndex), shifted, 0, shifted.Length);
                value = this.itemReader(shifted);
            }
            else
            {
                int len = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(sizeof(SortIndex)));
                object? rawValue = Statics.FromBytes(data, sizeof(SortIndex) + sizeof(Int32), len);
                value = rawValue is T typedValue
                    ? typedValue
                    : rawValue == null ? default! : throw new InvalidDataException($"Stored list item is not {typeof(T).FullName}.");
            }
            return new ListItem(index, value, persistId);
        }

        private byte[] ToBytes(ListItem item)
        {
            byte[] data, ret;

            if (this.itemWriter != null)
            {
                data = this.itemWriter(item.Value);
                ret = new byte[sizeof(SortIndex) + data.Length];
                BinaryPrimitives.WriteInt32LittleEndian(ret, BitConverter.SingleToInt32Bits(item.Index));
                Buffer.BlockCopy(data, 0, ret, sizeof(SortIndex), data.Length);
                return ret;
            }

            data = Statics.ToBytes(item.Value);
            int len = data.Length;
            ret = new byte[sizeof(SortIndex) + sizeof(Int32) + len];
            BinaryPrimitives.WriteInt32LittleEndian(ret, BitConverter.SingleToInt32Bits(item.Index));
            BinaryPrimitives.WriteInt32LittleEndian(ret.AsSpan(sizeof(SortIndex)), len);
            Buffer.BlockCopy(data, 0, ret, sizeof(SortIndex) + sizeof(Int32), len);
            return ret;
        }

        #endregion

        private readonly List<ListItem> records;
        private bool insertingRange;
        /// <summary>
        /// Internal storage
        /// </summary>
        public SynchronizedDataStorage Storage { get; }

        /// <summary>
        /// Open new persistent list file.
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="mode"></param>
        /// <param name="access"></param>
        /// <param name="share"></param>
        /// <param name="headerSize"></param>
        public ListPersistent(
            string fileName, 
            FileMode mode = FileMode.OpenOrCreate, 
            FileAccess access = FileAccess.ReadWrite, 
            FileShare share = FileShare.None, 
            ushort headerSize = 0,
            Converter<byte[], T>? itemReader = null,
            Converter<T, byte[]>? itemWriter = null,
            DataStorage.OnException onException = DataStorage.OnException.Default,
            DataStorage.OpenStateDelegate? listener = null) :
            this(OpenFile(fileName, mode, access, share, itemReader, itemWriter), headerSize, itemReader, itemWriter, onException, listener) { }

        /// <summary>
        /// Open a persistent list over an existing stream.
        /// </summary>
        /// <remarks>
        /// Argument validation leaves the caller's stream open. After validation, the list owns the stream until disposal.
        /// It attempts to close the stream on initialization failure; cleanup errors do not replace the initialization error.
        /// </remarks>
        public ListPersistent(
            Stream stream,
            ushort headerSize = 0,
            Converter<byte[], T>? itemReader = null,
            Converter<T, byte[]>? itemWriter = null,
            DataStorage.OnException onException = DataStorage.OnException.Default,
            DataStorage.OpenStateDelegate? listener = null)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ValidateCodecPair(itemReader, itemWriter);
            this.itemReader = itemReader;
            this.itemWriter = itemWriter;
            try
            {
                this.records = [];
                DataStorage storage = new(stream, (pos, data) => this.records.Add(this.FromBytes(data, pos)), null, headerSize, serializer: Statics.DefaultSerializer, onException: onException,
                    listener: (state, data) =>
                    {
                        if (state == DataStorage.OpenState.Recovered ||
                            (state & DataStorage.OpenState.Cleared) == DataStorage.OpenState.Cleared)
                            this.records.Clear();
                        listener?.Invoke(state, data);
                    });
                this.records.Sort((x, y) =>
                {
                    int order = x.Index.CompareTo(y.Index);
                    return order != 0 ? order : x.PersistId.CompareTo(y.PersistId);
                });
                foreach (ListItem item in this.records)
                {
                    if (!IsFiniteIndex(item.Index))
                        throw new InvalidDataException("Stored list sort indices must be finite.");
                }
                this.Storage = new SynchronizedDataStorage(storage);
            }
            catch
            {
                try { stream.Dispose(); }
                catch { } // Preserve the initialization failure after attempting to release the owned stream.
                throw;
            }
        }

        private static void ValidateCodecPair(Converter<byte[], T>? itemReader, Converter<T, byte[]>? itemWriter)
        {
            if ((itemReader == null) != (itemWriter == null))
                throw new ArgumentException("reader-writer pair should be provided");
        }

        private static FileStream OpenFile(string fileName, FileMode mode, FileAccess access, FileShare share,
            Converter<byte[], T>? itemReader, Converter<T, byte[]>? itemWriter)
        {
            ValidateCodecPair(itemReader, itemWriter);
            return File.Open(fileName, mode, access, share);
        }

        /// <summary>
        /// Add new list element
        /// </summary>
        /// <param name="item">element</param>
        /// <exception cref="InvalidOperationException">No finite sort index remains after the last item.</exception>
        public void Add(T item)
        {
            lock (this.Storage.SynchronizationRoot)
            {
                this.ThrowIfInsertingRange();
                int count = this.records.Count;
                ListItem? previous = count == 0 ? null : this.records[count - 1];
                SortIndex index = this.GetInsertionIndex(count);
                ListItem li = new(index, item);
                byte[] data = this.ToBytes(li);
                this.CheckInsertionPoint(count, count, previous, null);
                li.PersistId = this.Storage.Storage.Add(data);
                this.records.Add(li);
                this.Storage.FlushNeeded();
            }
        }

        /// <summary>
        /// Add range of new elements
        /// The input values and sort labels are prepared before writing. Each item is then serialized and
        /// stored separately; serialization or storage failure retains the successfully stored prefix.
        /// </summary>
        /// <remarks>
        /// Serialization proceeds one item at a time; encoded payloads are not retained for the whole range.
        /// The successful prefix is published with one bulk insertion before return or failure.
        /// Callback reads see the pre-range resident list; nested list mutations and disposal are rejected.
        /// Direct mutation through Storage during callbacks is unsupported. Partial storage writes are not rolled back.
        /// </remarks>
        /// <param name="items">elements collection to add</param>
        /// <exception cref="InvalidOperationException">Insufficient finite sort indices remain for the entire range.</exception>
        public void AddRange(IEnumerable<T> items)
        {
            ArgumentNullException.ThrowIfNull(items);
            lock (this.Storage.SynchronizationRoot)
                this.insertRange(this.Count, items);
        }

        /// <summary>
        /// Insert range of elements
        /// The input values and sort labels are prepared before writing. Each item is then serialized and
        /// stored separately; serialization or storage failure retains the successfully stored prefix.
        /// </summary>
        /// <remarks>
        /// Serialization proceeds one item at a time; encoded payloads are not retained for the whole range.
        /// The successful prefix is published with one bulk insertion before return or failure.
        /// Callback reads see the pre-range resident list; nested list mutations and disposal are rejected.
        /// Direct mutation through Storage during callbacks is unsupported. Partial storage writes are not rolled back.
        /// </remarks>
        /// <param name="index"></param>
        /// <param name="items"></param>
        /// <exception cref="InvalidOperationException">Insufficient finite sort indices remain for the entire range.</exception>
        public void InsertRange(int index, IEnumerable<T> items)
        {
            ArgumentNullException.ThrowIfNull(items);
            lock (this.Storage.SynchronizationRoot)
                this.insertRange(index, items);
        }

        private void insertRange(int index, IEnumerable<T> items)
        {
            ArgumentNullException.ThrowIfNull(items);
            this.ThrowIfInsertingRange();
            this.insertingRange = true;
            try
            {
                int count = this.records.Count;
                if (index < 0 || index > count)
                    throw new ArgumentOutOfRangeException(nameof(index));

                List<T> snapshot = new(items);
                int itemsCount = snapshot.Count;
                if (itemsCount == 0)
                    return;

                int capacity = checked(count + itemsCount);
                SortIndex[] indices = this.GetInsertionIndices(index, itemsCount);
                List<ListItem> prepared = new(itemsCount);
                if (this.records.Capacity < capacity)
                    this.records.Capacity = Math.Max(capacity, (int)Math.Min((long)this.records.Capacity * 2, int.MaxValue));

                try
                {
                    foreach (T item in snapshot)
                    {
                        ListItem li = new(indices[prepared.Count], item);
                        li.PersistId = this.Storage.Storage.Add(this.ToBytes(li));
                        prepared.Add(li);
                    }
                }
                finally
                {
                    if (prepared.Count > 0)
                    { // Preallocated metadata lets every successful write publish with one bulk shift.
                        this.records.InsertRange(index, prepared);
                        this.Storage.FlushNeeded();
                    }
                }
            }
            finally
            {
                this.insertingRange = false;
            }
        }

        private void ThrowIfInsertingRange()
        {
            if (this.insertingRange)
                throw new InvalidOperationException("List mutations and disposal are not allowed while inserting a range.");
        }

        private void CheckInsertionPoint(int index, int count, ListItem? previous, ListItem? next)
        {
            if (this.records.Count != count ||
                (index > 0 && !ReferenceEquals(this.records[index - 1], previous)) ||
                (index < count && !ReferenceEquals(this.records[index], next)))
                throw new InvalidOperationException("The list insertion point changed while preparing persistent items.");
        }

        private static bool IsFiniteIndex(SortIndex index) => !SortIndex.IsNaN(index) && !SortIndex.IsInfinity(index);

        private SortIndex GetArithmeticInsertionIndex(int index, int itemsCount, int offset)
        {
            if (this.records.Count == 0)
                return offset;
            if (index == this.records.Count)
                return (SortIndex)((double)this.records[index - 1].Index + 1 + offset);
            if (index == 0)
                return (SortIndex)((double)this.records[0].Index - itemsCount + offset);

            double previous = this.records[index - 1].Index;
            double next = this.records[index].Index;
            return (SortIndex)(previous + (next - previous) * (offset + 1) / (itemsCount + 1d));
        }

        private SortIndex GetInsertionIndex(int index)
        {
            SortIndex candidate = this.GetArithmeticInsertionIndex(index, 1, 0);
            if (IsFiniteIndex(candidate) &&
                (index == 0 || candidate > this.records[index - 1].Index) &&
                (index == this.records.Count || candidate < this.records[index].Index))
                return candidate;

            this.GetRankInsertionBounds(index, 1, out uint start, out uint span);
            return IndexFromRank(start + span / 2);
        }

        private SortIndex[] GetInsertionIndices(int index, int itemsCount)
        {
            SortIndex[] indices = new SortIndex[itemsCount];
            SortIndex previous = index == 0 ? SortIndex.NegativeInfinity : this.records[index - 1].Index;
            SortIndex next = index == this.records.Count ? SortIndex.PositiveInfinity : this.records[index].Index;
            bool valid = true;
            for (int i = 0; i < itemsCount; i++)
            {
                SortIndex candidate = this.GetArithmeticInsertionIndex(index, itemsCount, i);
                if (!IsFiniteIndex(candidate) || candidate <= previous || candidate >= next)
                {
                    valid = false;
                    break;
                }
                indices[i] = previous = candidate;
            }
            if (valid)
                return indices;

            // Numeric float ranks retain every available finite value, including subnormals.
            this.GetRankInsertionBounds(index, itemsCount, out uint start, out uint span);
            ulong divisor = (ulong)itemsCount + 1;
            for (int i = 0; i < itemsCount; i++)
                indices[i] = IndexFromRank(start + (uint)((ulong)span * (uint)(i + 1) / divisor));
            return indices;
        }

        private const uint ZeroIndexRank = 0x7F800000;
        private const uint MaximumIndexRank = 0xFEFFFFFF;

        private static uint IndexRank(SortIndex index)
        {
            uint bits = unchecked((uint)BitConverter.SingleToInt32Bits(index));
            uint magnitude = bits & 0x7FFFFFFF;
            return (bits & 0x80000000) == 0 ? ZeroIndexRank + magnitude : ZeroIndexRank - magnitude;
        }

        private static SortIndex IndexFromRank(uint rank)
        {
            uint bits = rank >= ZeroIndexRank ? rank - ZeroIndexRank : 0x80000000 | (ZeroIndexRank - rank);
            return BitConverter.Int32BitsToSingle(unchecked((int)bits));
        }

        private void GetRankInsertionBounds(int index, int itemsCount, out uint start, out uint span)
        {
            uint required = (uint)itemsCount;
            span = required + 1;
            if (this.records.Count == 0)
            {
                start = ZeroIndexRank - required / 2 - 1;
                return;
            }
            if (index == this.records.Count)
            {
                start = IndexRank(this.records[index - 1].Index);
                if (MaximumIndexRank - start >= required)
                    return;
            }
            else if (index == 0)
            {
                uint next = IndexRank(this.records[0].Index);
                if (next - 1 >= required)
                {
                    start = next - required - 1;
                    return;
                }
                start = 0;
            }
            else
            {
                start = IndexRank(this.records[index - 1].Index);
                span = IndexRank(this.records[index].Index) - start;
                if (span > required)
                    return;
            }
            throw new InvalidOperationException("The list has insufficient finite sort index space at this insertion point. Copy the list into a new store to restore index space.");
        }

        /// <summary>
        /// Insert in the middle of the list
        /// </summary>
        /// <param name="index"></param>
        /// <param name="item"></param>
        /// <exception cref="InvalidOperationException">No finite sort index remains at the insertion point.</exception>
        public void Insert(int index, T item)
        {
            if (index < 0)
                throw new ArgumentOutOfRangeException(nameof(index));

            lock (this.Storage.SynchronizationRoot)
            {
                this.ThrowIfInsertingRange();
                int count = this.records.Count;
                if (index > count)
                    throw new ArgumentOutOfRangeException(nameof(index));

                ListItem? previous = index == 0 ? null : this.records[index - 1];
                ListItem? next = index == count ? null : this.records[index];
                ListItem li = new(this.GetInsertionIndex(index), item);
                byte[] data = this.ToBytes(li);
                this.CheckInsertionPoint(index, count, previous, next);
                li.PersistId = this.Storage.Storage.Add(data);
                this.records.Insert(index, li);
                this.Storage.FlushNeeded();
            }
        }

        /// <summary>
        /// Remove element from list
        /// </summary>
        /// <param name="item">item to remove</param>
        /// <returns></returns>
        public bool Remove(T item)
        {
            lock (this.Storage.SynchronizationRoot)
            {
                this.ThrowIfInsertingRange();
                int index = this.IndexOf(item);

                if (index < 0) // not found
                    return false;

                // index found
                this.Storage.Remove(this.records[index].PersistId);
                this.records.RemoveAt(index);
                return true;
            }
        }

        /// <summary>
        /// Remove item by index
        /// </summary>
        /// <param name="index"></param>
        public void RemoveAt(int index)
        {
            if (index < 0)
                throw new ArgumentOutOfRangeException(nameof(index));

            lock (this.Storage.SynchronizationRoot)
            { // index is OK
                this.ThrowIfInsertingRange();
                if (index >= this.records.Count)
                    throw new ArgumentOutOfRangeException(nameof(index));
                this.Storage.Remove(this.records[index].PersistId);
                this.records.RemoveAt(index);
            }
        }

        /// <summary>
        /// Return first index of element
        /// </summary>
        /// <param name="item"></param>
        /// <returns>The zero-based index of the first occurrence if found; otherwise, -1.</returns>
        public int IndexOf(T item)
        {
            lock (this.Storage.SynchronizationRoot)
                return this.IndexOfCore(item, 0, this.records.Count);
        }

        /// <summary>
        /// Searches for the specified object and returns the zero-based index of the first occurrence within the range of elements
        /// </summary>
        /// <param name="item">The object to locate. The value can be null for reference types.</param>
        /// <param name="start">The zero-based starting index of the search. 0 (zero) is valid in an empty list.</param>
        /// <returns>The zero-based index of the first occurrence if found; otherwise, -1.</returns>
        public int IndexOf(T item, int start)
        {
            lock (this.Storage.SynchronizationRoot)
                return this.IndexOfCore(item, start, this.records.Count - start);
        }

        /// <summary>
        /// Searches for the specified object and returns the zero-based index of the first occurrence within the range of elements 
        /// </summary>
        /// <param name="item">The object to locate. The value can be null for reference types.</param>
        /// <param name="start">The zero-based starting index of the search. 0 (zero) is valid in an empty list.</param>
        /// <param name="count">The number of elements in the section to search.</param>
        /// <returns>The zero-based index of the first occurrence if found; otherwise, -1.</returns>
        public int IndexOf(T item, int start, int count)
        {
            lock (this.Storage.SynchronizationRoot)
                return this.IndexOfCore(item, start, count);
        }

        private int IndexOfCore(T item, int start, int count)
        {
            EqualityComparer<T> comparer = EqualityComparer<T>.Default;
            int end = start + count;
            if ((uint)start > (uint)this.records.Count || count < 0 || end > this.records.Count)
                throw new ArgumentOutOfRangeException(count < 0 ? nameof(count) : nameof(start));

            for (int i = start; i < end; i++)
                if (comparer.Equals(this.records[i].Value, item))
                    return i;

            return -1;
        }

        /// <summary>
        /// Check if contains value
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public bool Contains(T item)
        {
            lock (this.Storage.SynchronizationRoot)
                return this.IndexOfCore(item, 0, this.records.Count) >= 0;
        }

        readonly struct ListItemComparer(IComparer<T>? comparer) : IComparer<ListItem>
        {
            readonly IComparer<T> itemComparer = comparer ?? Comparer<T>.Default;

            public readonly int Compare(ListItem? x, ListItem? y)
            {
                if (x == null) return y == null ? 0 : -1;
                if (y == null) return 1;
                return this.itemComparer.Compare(x.Value, y.Value);
            }
        }

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
        /// <param name="item">The object to locate. The value can be null for reference types.</param>
        /// <param name="comparer">The IComparer implementation to use when comparing elements, or null to use the default comparer </param>
        /// <returns>The zero-based index of item in the sorted if found; 
        /// otherwise, a negative number that is the bitwise complement of the index of the next element that is larger than item or, 
        /// if there is no larger element, the bitwise complement of Count</returns>
        public int BinarySearch(int index, int count, T item, IComparer<T>? comparer)
        {
            lock (this.Storage.SynchronizationRoot)
                return this.records.BinarySearch(index, count, new ListItem(0, item), new ListItemComparer(comparer));
        }

        /// <summary>
        /// Get result casted to different type
        /// </summary>
        /// <typeparam name="V"></typeparam>
        /// <param name="index">index of item to get</param>
        /// <param name="converter">converter to output type</param>
        /// <returns></returns>
        [return: MaybeNull]
        public V Get<V>(int index, Converter<T, V>? converter = null) => this.TryGet(index, out V? ret, converter) ? ret : default;

        /// <summary>
        /// Try To get setting, casted to different type
        /// </summary>
        /// <typeparam name="V"></typeparam>
        /// <param name="index"></param>
        /// <param name="result"></param>
        /// <param name="converter">converter to output type</param>
        /// <returns></returns>
        public bool TryGet<V>(int index, [MaybeNullWhen(false)] out V result, Converter<T, V>? converter = null)
        {
            T value = this[index];
            // setting is found
            if (value == null)
            { // null value provided
                result = default;
                return typeof(V).IsClass;
            }
            else
            { // not null
                if (converter != null)
                { // converter provided
                    result = converter(value);
                    return true;
                }

                if (Statics.TryChangeType(value, out result))
                    return true;
            }
            result = default;
            return false;
        }

        /// <summary>
        /// Get or set list element.
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public T this[int index]
        {
            get
            {
                lock (this.Storage.SynchronizationRoot)
                    return this.records[index].Value;
            }
            set
            {
                if (index < 0)
                    throw new ArgumentOutOfRangeException(nameof(index));

                lock (this.Storage.SynchronizationRoot)
                {
                    this.ThrowIfInsertingRange();
                    if (index >= this.records.Count)
                        throw new ArgumentOutOfRangeException(nameof(index));
                    ListItem li = this.records[index];
                    byte[] data = this.ToBytes(new ListItem(li.Index, value));
                    if (index >= this.records.Count || !ReferenceEquals(this.records[index], li))
                        throw new InvalidOperationException("The list item changed while preparing its persistent value.");
                    long persistId = this.Storage.Storage.Update(li.PersistId, data);
                    li.Value = value; // update value
                    li.PersistId = persistId;
                    this.Storage.FlushNeeded();
                }
            }
        }

        /// <summary>
        /// Clear list.
        /// </summary>
        public void Clear()
        {
            lock (this.Storage.SynchronizationRoot)
            {
                this.ThrowIfInsertingRange();
                this.Storage.Clear();
                this.records.Clear();
            }
        }

        /// <summary>
        /// Get list count.
        /// </summary>
        public int Count
        {
            get
            {
                lock (this.Storage.SynchronizationRoot)
                    return this.records.Count;
            }
        }

        /// <summary>
        /// Get items enumeration
        /// </summary>
        /// <returns></returns>
        public IEnumerator<T> GetEnumerator()
        {
            T[] snapshot = this.ToArray();
            foreach (T item in snapshot)
                yield return item;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.GetEnumerator();

        /// <summary>
        /// Copy list values to an array.
        /// </summary>
        /// <param name="array"></param>
        /// <param name="arrayIndex"></param>
        public void CopyTo(T[] array, int arrayIndex)
        {
            ArgumentNullException.ThrowIfNull(array);
            lock (this.Storage.SynchronizationRoot)
            {
                int count = this.records.Count;
                if ((uint)arrayIndex > (uint)array.Length || array.Length - arrayIndex < count)
                    throw new ArgumentOutOfRangeException(nameof(arrayIndex));

                for (int i = 0; i < count; i++)
                    array[arrayIndex++] = this.records[i].Value;
            }
        }

        /// <summary>
        /// Return list copy as array.
        /// </summary>
        /// <returns></returns>
        public T[] ToArray()
        {
            lock (this.Storage.SynchronizationRoot)
            {
                int count = this.records.Count;
                T[] ret = new T[count];
                for (int i = 0; i < count; i++)
                    ret[i] = this.records[i].Value;
                return ret;
            }
        }

        bool ICollection<T>.IsReadOnly => false;

        /// <summary>
        /// Dispose resources
        /// </summary>
        public void Dispose()
        {
            lock (this.Storage.SynchronizationRoot)
                this.ThrowIfInsertingRange();
            // Storage.Dispose drains its timer before taking this lock itself.
            this.Storage.Dispose();
        }
    }
}
