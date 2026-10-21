using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics.CodeAnalysis;

namespace RtTools
{
    /// <summary>
    /// Represents dictionary, persistent in file
    /// </summary>
    /// <typeparam name="TKey"></typeparam>
    /// <typeparam name="TValue"></typeparam>
    public sealed class DictionaryPersistent<TKey, TValue> : IDictionary<TKey, TValue>, IDisposable where TKey : notnull
    {
        #region DictionaryItem class

        private sealed class DictionaryItem(TKey key, TValue value, long persistId = long.MinValue)
        {
            public TKey Key { get; } = key;
            public TValue Value { get; set; } = value;
            public long PersistId { get; set; } = persistId;
        }

        private readonly Converter<byte[], KeyValuePair<TKey, TValue>>? itemReader;
        private readonly Converter<KeyValuePair<TKey, TValue>, byte[]>? itemWriter;

        private DictionaryItem FromBytes(byte[] data, long persistId = long.MinValue)
        {
            if (this.itemReader != null)
            {
                KeyValuePair<TKey, TValue> item = this.itemReader(data);
                return new DictionaryItem(item.Key, item.Value, persistId);
            }

            int keyLen = BinaryPrimitives.ReadInt32LittleEndian(data);
            TKey key = Statics.FromBytes(data, sizeof(Int32), keyLen) is TKey typedKey
                ? typedKey
                : throw new InvalidDataException($"Stored dictionary key is not {typeof(TKey).FullName}.");
            int valueLen = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(sizeof(Int32) + keyLen));
            object? rawValue = Statics.FromBytes(data, 2 * sizeof(Int32) + keyLen, valueLen);
            TValue value = rawValue is TValue typedValue
                ? typedValue
                : rawValue == null ? default! : throw new InvalidDataException($"Stored dictionary value is not {typeof(TValue).FullName}.");

            return new DictionaryItem(key, value, persistId);
        }

        private byte[] ToBytes(DictionaryItem item)
        {
            if (this.itemWriter != null)
                return this.itemWriter(new KeyValuePair<TKey, TValue>(item.Key, item.Value));

            byte[] keyData = Statics.ToBytes(item.Key);
            byte[] valueData = Statics.ToBytes(item.Value);
            int keyLen = keyData.Length;
            int valueLen = valueData.Length;
            byte[] ret = new byte[2 * sizeof(Int32) + keyLen + valueLen];

            BinaryPrimitives.WriteInt32LittleEndian(ret, keyLen);
            Buffer.BlockCopy(keyData, 0, ret, sizeof(Int32), keyLen);

            BinaryPrimitives.WriteInt32LittleEndian(ret.AsSpan(sizeof(Int32) + keyLen), valueLen);
            Buffer.BlockCopy(valueData, 0, ret, 2 * sizeof(Int32) + keyLen, valueLen);

            return ret;
        }

        #endregion

        private readonly Dictionary<TKey, DictionaryItem> itemsByKey;
        /// <summary>
        /// Internal storage
        /// </summary>
        public SynchronizedDataStorage Storage { get; }

        /// <summary>
        /// On Data scan
        /// </summary>
        /// <param name="pos"></param>
        /// <param name="data"></param>
        private void OnDataScan(long pos, byte[] data)
        {
            DictionaryItem item = this.FromBytes(data, pos);
            this.itemsByKey[item.Key] = item;
        }

        /// <summary>
        /// Open new file dictionary persistent
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="mode"></param>
        /// <param name="access"></param>
        /// <param name="share"></param>
        /// <param name="headerSize"></param>
        public DictionaryPersistent(
            string fileName,
            FileMode mode = FileMode.OpenOrCreate,
            FileAccess access = FileAccess.ReadWrite,
            FileShare share = FileShare.None,
            ushort headerSize = 0,
            Converter<byte[], KeyValuePair<TKey, TValue>>? reader = null,
            Converter<KeyValuePair<TKey, TValue>, byte[]>? writer = null,
            DataStorage.OnException onException = DataStorage.OnException.Default,
            DataStorage.OpenStateDelegate? listener = null
            ) : this(OpenFile(fileName, mode, access, share, reader, writer), headerSize, reader, writer, onException, listener) { }

        /// <summary>
        /// new persistent dictionary
        /// </summary>
        /// <remarks>
        /// Argument validation leaves the caller's stream open. After validation, the dictionary owns the stream until disposal.
        /// It attempts to close the stream on initialization failure; cleanup errors do not replace the initialization error.
        /// </remarks>
        public DictionaryPersistent(
            Stream stream,
            ushort headerSize = 0,
            Converter<byte[], KeyValuePair<TKey, TValue>>? reader = null,
            Converter<KeyValuePair<TKey, TValue>, byte[]>? writer = null,
            DataStorage.OnException onException = DataStorage.OnException.Default,
            DataStorage.OpenStateDelegate? listener = null)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ValidateCodecPair(reader, writer);
            this.itemReader = reader;
            this.itemWriter = writer;
            try
            {
                this.itemsByKey = [];
                DataStorage storage = new(stream, this.OnDataScan, null, headerSize, serializer: Statics.DefaultSerializer, onException: onException,
                    listener: (state, data) =>
                    {
                        if (state == DataStorage.OpenState.Recovered ||
                            (state & DataStorage.OpenState.Cleared) == DataStorage.OpenState.Cleared)
                            this.itemsByKey.Clear();
                        listener?.Invoke(state, data);
                    });
                this.Storage = new SynchronizedDataStorage(storage);
            }
            catch
            {
                try { stream.Dispose(); }
                catch { } // Preserve the initialization failure after attempting to release the owned stream.
                throw;
            }
        }

        private static void ValidateCodecPair(Converter<byte[], KeyValuePair<TKey, TValue>>? reader, Converter<KeyValuePair<TKey, TValue>, byte[]>? writer)
        {
            if ((reader == null) != (writer == null))
                throw new ArgumentException("reader-writer pair should be provided");
        }

        private static FileStream OpenFile(string fileName, FileMode mode, FileAccess access, FileShare share,
            Converter<byte[], KeyValuePair<TKey, TValue>>? reader, Converter<KeyValuePair<TKey, TValue>, byte[]>? writer)
        {
            ValidateCodecPair(reader, writer);
            return File.Open(fileName, mode, access, share);
        }

        /// <summary>
        /// Add new dictionary element
        /// </summary>
        /// <param name="key">element key</param>
        /// <param name="value">element value</param>
        public void Add(TKey key, TValue value)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            lock (this.Storage.SynchronizationRoot)
            {
                if (this.itemsByKey.ContainsKey(key))
                    throw new ArgumentException("An item with the same key has already been added.", nameof(key));

                DictionaryItem item = new(key, value);
                byte[] data = this.ToBytes(item);
                // A custom writer can re-enter the dictionary while the lock is held.
                if (this.itemsByKey.ContainsKey(key))
                    throw new ArgumentException("An item with the same key has already been added.", nameof(key));

                item.PersistId = this.Storage.Storage.Add(data);
                this.itemsByKey.Add(key, item);
                this.Storage.FlushNeeded();
            }
        }

        /// <summary>
        /// Get if dictionary contains key
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public bool ContainsKey(TKey key)
        {
            lock (this.Storage.SynchronizationRoot)
                return this.itemsByKey.ContainsKey(key);
        }

        /// <summary>
        /// Get dictionary keys
        /// </summary>
        public ICollection<TKey> Keys
        {
            get
            {
                lock (this.Storage.SynchronizationRoot)
                    return [.. this.itemsByKey.Keys];
            }
        }

        /// <summary>
        /// Remove element from dictionary
        /// </summary>
        /// <param name="key">key to remove</param>
        /// <returns></returns>
        public bool Remove(TKey key)
        {
            lock (this.Storage.SynchronizationRoot)
            {
                if (!this.itemsByKey.TryGetValue(key, out DictionaryItem? item)) // not found
                    return false;
                this.Storage.Remove(item.PersistId);
                return this.itemsByKey.Remove(key);
            }
        }

        /// <summary>
        /// Get dictionary value
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
        {
            lock (this.Storage.SynchronizationRoot)
            {
                if (this.itemsByKey.TryGetValue(key, out DictionaryItem? item))
                {
                    value = item.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        /// <summary>
        /// Get result casted to different type
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="key"></param>
        /// <param name="converter">converter to output type</param>
        /// <returns></returns>
        [return: MaybeNull]
        public T Get<T>(TKey key, Converter<TValue, T>? converter = null)
        {
            this.TryGetValue(key, out T? ret, converter);
            return ret;
        }

        /// <summary>
        /// Try To get setting, casted to different type
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="key"></param>
        /// <param name="result"></param>
        /// <param name="converter">converter to output type</param>
        /// <returns></returns>
        public bool TryGetValue<T>(TKey key, [MaybeNullWhen(false)] out T result, Converter<TValue, T>? converter = null)
        {
            if (this.TryGetValue(key, out TValue? value))
            { // setting is found
                if (value == null)
                { // null value provided
                    result = default;
                    return typeof(T).IsClass;
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
            }
            result = default;
            return false;
        }

        /// <summary>
        /// Get/Set dictionary element
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public TValue this[TKey key]
        {
            get
            {
                lock (this.Storage.SynchronizationRoot)
                    return this.itemsByKey.TryGetValue(key, out DictionaryItem? item) ? item.Value : throw new KeyNotFoundException();
            }
            set
            {
                if (key == null) throw new ArgumentNullException(nameof(key));

                lock (this.Storage.SynchronizationRoot)
                {
                    this.itemsByKey.TryGetValue(key, out DictionaryItem? originalItem);
                    DictionaryItem candidate = new(originalItem != null ? originalItem.Key : key, value);
                    byte[] data = this.ToBytes(candidate);
                    // Re-read after serialization: a custom writer may remove or update the entry.
                    if (this.itemsByKey.TryGetValue(key, out DictionaryItem? item))
                    {
                        if (!ReferenceEquals(item, originalItem))
                            throw new InvalidOperationException("The dictionary entry was replaced during serialization.");

                        long persistId = this.Storage.Storage.Update(item.PersistId, data);
                        item.Value = value;
                        item.PersistId = persistId;
                    }
                    else
                    {
                        candidate.PersistId = this.Storage.Storage.Add(data);
                        this.itemsByKey.Add(candidate.Key, candidate);
                    }
                    this.Storage.FlushNeeded();
                }
            }
        }

        /// <summary>
        /// Clear dictionary
        /// </summary>
        public void Clear()
        {
            lock (this.Storage.SynchronizationRoot)
            {
                this.Storage.Clear();
                this.itemsByKey.Clear();
            }
        }

        /// <summary>
        /// Get dictionary count
        /// </summary>
        public int Count
        {
            get
            {
                lock (this.Storage.SynchronizationRoot)
                    return this.itemsByKey.Count;
            }
        }

        /// <summary>
        /// Get values enumerator
        /// </summary>
        /// <returns></returns>
        public IEnumerable<TValue> GetValues()
        {
            TValue[] snapshot;
            lock (this.Storage.SynchronizationRoot)
            {
                snapshot = new TValue[this.itemsByKey.Count];
                int index = 0;
                foreach (DictionaryItem item in this.itemsByKey.Values)
                    snapshot[index++] = item.Value;
            }

            foreach (TValue value in snapshot)
                yield return value;
        }

        /// <summary>
        /// Get items enumeration
        /// </summary>
        /// <returns></returns>
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            KeyValuePair<TKey, TValue>[] snapshot;
            lock (this.Storage.SynchronizationRoot)
            {
                snapshot = new KeyValuePair<TKey, TValue>[this.itemsByKey.Count];
                int index = 0;
                foreach (DictionaryItem item in this.itemsByKey.Values)
                    snapshot[index++] = new KeyValuePair<TKey, TValue>(item.Key, item.Value);
            }

            foreach (KeyValuePair<TKey, TValue> item in snapshot)
                yield return item;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.GetEnumerator();

        ICollection<TValue> IDictionary<TKey, TValue>.Values
        {
            get
            {
                lock (this.Storage.SynchronizationRoot)
                {
                    TValue[] values = new TValue[this.itemsByKey.Count];
                    int index = 0;
                    foreach (DictionaryItem item in this.itemsByKey.Values)
                        values[index++] = item.Value;
                    return values;
                }
            }
        }

        bool ICollection<KeyValuePair<TKey, TValue>>.Contains(KeyValuePair<TKey, TValue> item) =>
            this.TryGetValue(item.Key, out TValue? value) && EqualityComparer<TValue>.Default.Equals(value, item.Value);

        void ICollection<KeyValuePair<TKey, TValue>>.CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            ArgumentNullException.ThrowIfNull(array);
            lock (this.Storage.SynchronizationRoot)
            {
                if ((uint)arrayIndex > (uint)array.Length || array.Length - arrayIndex < this.itemsByKey.Count)
                    throw new ArgumentOutOfRangeException(nameof(arrayIndex));

                foreach (DictionaryItem item in this.itemsByKey.Values)
                    array[arrayIndex++] = new KeyValuePair<TKey, TValue>(item.Key, item.Value);
            }
        }

        bool ICollection<KeyValuePair<TKey, TValue>>.IsReadOnly => false;

        void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> item) => this.Add(item.Key, item.Value);

        bool ICollection<KeyValuePair<TKey, TValue>>.Remove(KeyValuePair<TKey, TValue> item)
        {
            lock (this.Storage.SynchronizationRoot)
            {
                if (!this.itemsByKey.TryGetValue(item.Key, out DictionaryItem? stored) ||
                    !EqualityComparer<TValue>.Default.Equals(stored.Value, item.Value))
                    return false;

                this.Storage.Remove(stored.PersistId);
                return this.itemsByKey.Remove(item.Key);
            }
        }

        /// <summary>
        /// Dispose resources
        /// </summary>
        public void Dispose() => this.Storage.Dispose();
    }
}
