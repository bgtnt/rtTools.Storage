using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace RtTools
{
    /// <summary>
    /// A file-backed FIFO queue with a bounded cache of the earliest queued values.
    /// </summary>
    /// <remarks>
    /// Opening scans all stored records. Enumeration creates a complete decoded FIFO snapshot.
    /// The buffer limit bounds the queue's front cache, not enumeration or startup work.
    /// Use trusted data with the default serializer, or supply a consistent reader/writer pair on every reopen.
    /// Serialization callbacks must not reenter queue operations other than Count or BufferedCount.
    /// </remarks>
    /// <typeparam name="T"></typeparam>
    public sealed class BufferedQueuePersistent<T> : IEnumerable<T>, IDisposable
    {
        #region QueueItem class

        private sealed class QueueItem
        {
            public static Comparer<QueueItem> IndexComparer { get; } = Comparer<QueueItem>.Create((x, y) => x.Index.CompareTo(y.Index));

            public ulong Index { get; }
            public T Value { get; set; }
            public long PersistId { get; set; }

            public QueueItem(ulong index, T value)
            {
                this.Index = index;
                this.PersistId = long.MinValue;
                this.Value = value;
            }

            public QueueItem(byte[] data, Converter<byte[], T>? reader)
            {
                const int prefixSize = sizeof(ulong) + sizeof(int);
                if (data.Length < prefixSize)
                    throw new InvalidDataException("Stored queue item is shorter than its ordering/length prefix.");
                this.PersistId = long.MinValue;
                this.Index = BinaryPrimitives.ReadUInt64LittleEndian(data);
                int len = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(sizeof(ulong)));
                if (len < 0 || len != data.Length - prefixSize)
                    throw new InvalidDataException("Stored queue item payload length does not match its record.");
                object? decoded = reader != null
                    ? reader(data.AsSpan(prefixSize, len).ToArray())
                    : Statics.FromBytes(data, prefixSize, len);
                this.Value = decoded is T value
                    ? value
                    : decoded == null && default(T) == null
                        ? default!
                        : throw new InvalidDataException($"Stored queue item is not {typeof(T).FullName}.");
            }

            public byte[] GetData(Converter<T, byte[]>? writer)
            {
                byte[] data = (writer != null ? writer(this.Value) : Statics.ToBytes(this.Value))
                    ?? throw new InvalidDataException("Queue writer returned null data.");
                int len = data.Length;
                byte[] ret = new byte[checked(sizeof(ulong) + sizeof(Int32) + len)];
                BinaryPrimitives.WriteUInt64LittleEndian(ret, this.Index);
                BinaryPrimitives.WriteInt32LittleEndian(ret.AsSpan(sizeof(ulong)), len);
                Buffer.BlockCopy(data, 0, ret, sizeof(ulong) + sizeof(Int32), len);
                return ret;
            }

            public override string ToString() => string.Format("{0}: {1}, ({2})", this.Index, this.Value, this.PersistId);
        }

        #endregion

        private static readonly Comparer<KeyValuePair<ulong, long>> recordIndexComparer =
            Comparer<KeyValuePair<ulong, long>>.Create((x, y) => x.Key.CompareTo(y.Key));

        private readonly LimitedList<QueueItem> buffer;
        private readonly Converter<byte[], T>? itemReader;
        private readonly Converter<T, byte[]>? itemWriter;
        private bool disposed;
        private bool codecActive;
        /// <summary>
        /// max index (counter) - index of last enqueue item
        /// </summary>
        private ulong maxIndex;
        /// <summary>
        /// max item index in buffer - index of last item stored in buffer
        /// </summary>
        private ulong lastBufferedIndex;

        /// <summary>
        /// Internal storage
        /// </summary>
        public SynchronizedDataStorage Storage { get; }

        /// <summary> 
        /// Open new persistent queue file.
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="maxBuffer">max buffer size</param>
        /// <param name="mode"></param>
        /// <param name="access"></param>
        /// <param name="share"></param>
        /// <param name="headerSize"></param>
        /// <param name="reader">Payload decoder, supplied together with writer; null selects the trusted-data default serializer.</param>
        /// <param name="writer">Payload encoder, supplied together with reader. Use the same codec on every reopen.</param>
        public BufferedQueuePersistent(string fileName, int maxBuffer, FileMode mode = FileMode.OpenOrCreate, FileAccess access = FileAccess.ReadWrite, FileShare share = FileShare.None, ushort headerSize = 0,
            Converter<byte[], T>? reader = null, Converter<T, byte[]>? writer = null) :
            this(OpenFile(fileName, maxBuffer, mode, access, share, reader, writer), maxBuffer, headerSize, reader: reader, writer: writer) { }

        /// <summary>
        /// Open a persistent queue over an existing stream.
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="maxBuffer">max buffer size</param>
        /// <param name="headerSize"></param>
        /// <remarks>After argument validation, the queue owns the stream and closes it on disposal or opening failure.</remarks>
        public BufferedQueuePersistent(Stream stream, int maxBuffer, ushort headerSize = 0, DataStorage.OnException onException = DataStorage.OnException.Default, DataStorage.OpenStateDelegate? listener = null,
            Converter<byte[], T>? reader = null, Converter<T, byte[]>? writer = null)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ValidateArguments(maxBuffer, reader, writer);
            this.itemReader = reader;
            this.itemWriter = writer;
            try
            {
                LimitedList<QueueItem> queueBuffer = this.buffer = new LimitedList<QueueItem>(maxBuffer);
                ulong maxIndex = ulong.MinValue, lastBufferIndex = ulong.MinValue;

                DataStorage storage = new(stream,
                    (pos, data) =>
                    {
                        QueueItem item = this.ReadItem(data, pos);
                        var itemIndex = item.Index;
                        maxIndex = Math.Max(maxIndex, itemIndex);

                        if (itemIndex <= lastBufferIndex || queueBuffer.Count < maxBuffer)
                        {
                            int index = queueBuffer.BinarySearch(item, QueueItem.IndexComparer);
                            if (index < 0) index = ~index;
                            if (queueBuffer.InsertLastOut(index, item) >= 0) // insert, removing last if limited
                                lastBufferIndex = queueBuffer[queueBuffer.Count - 1].Index;
                        }

                    }, null, headerSize, serializer: Statics.DefaultSerializer, onException: onException,
                    listener: (state, data) =>
                    {
                        if (state == DataStorage.OpenState.Recovered ||
                            (state & DataStorage.OpenState.Cleared) == DataStorage.OpenState.Cleared)
                        {
                            queueBuffer.Clear();
                            maxIndex = ulong.MinValue;
                            lastBufferIndex = ulong.MinValue;
                        }
                        listener?.Invoke(state, data);
                    });

                // store max index
                this.maxIndex = maxIndex;
                this.lastBufferedIndex = lastBufferIndex;

                this.Storage = new SynchronizedDataStorage(storage);
            }
            catch
            {
                try { stream.Dispose(); }
                catch { } // Preserve the opening/decoder failure after attempting to release the owned stream.
                throw;
            }
        }

        private static void ValidateArguments(int maxBuffer, Converter<byte[], T>? reader, Converter<T, byte[]>? writer)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxBuffer, 1);
            if ((reader == null) != (writer == null))
                throw new ArgumentException("reader-writer pair should be provided");
        }

        private static FileStream OpenFile(string fileName, int maxBuffer, FileMode mode, FileAccess access, FileShare share,
            Converter<byte[], T>? reader, Converter<T, byte[]>? writer)
        {
            ValidateArguments(maxBuffer, reader, writer);
            return File.Open(fileName, mode, access, share);
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(this.disposed, this);

        private void RejectCodecReentry()
        {
            if (this.codecActive)
                throw new InvalidOperationException("Serialization callbacks cannot reenter queue operations.");
        }

        private QueueItem ReadItem(byte[] data, long position)
        {
            bool previous = this.codecActive;
            this.codecActive = true;
            try { return new QueueItem(data, this.itemReader) { PersistId = position }; }
            finally { this.codecActive = previous; }
        }

        private byte[] WriteItem(QueueItem item)
        {
            bool previous = this.codecActive;
            this.codecActive = true;
            try { return item.GetData(this.itemWriter); }
            finally { this.codecActive = previous; }
        }

        /// <summary>
        /// Enqueue new element into queue
        /// </summary>
        /// <param name="item">element</param>
        public void Enqueue(T item)
        {
            lock (this.Storage.SynchronizationRoot)
            {
                this.ThrowIfDisposed();
                this.RejectCodecReentry();
                ulong index = checked(this.maxIndex + 1);

                QueueItem li = new(index, item);
                li.PersistId = this.Storage.Add(this.WriteItem(li));

                if (this.lastBufferedIndex == this.maxIndex)
                { // items are fully buffered
                    if (this.buffer.Count < this.buffer.Limit)
                    { // buffer has room to grow
                        this.lastBufferedIndex = index;
                        this.buffer.Add(li);
                    }
                }

                this.maxIndex = index;
            }
        }

        /// <summary>
        /// Get elements in buffer (BufferedCount != Count - means there are non-buffered items)
        /// </summary>
        public int BufferedCount
        {
            get
            {
                lock (this.Storage.SynchronizationRoot)
                {
                    this.ThrowIfDisposed();
                    return this.buffer.Count;
                }
            }
        }

        /// <summary>
        /// Peek element from list
        /// </summary>
        /// <returns>item peeked</returns>
        public T Peek()
        {
            lock (this.Storage.SynchronizationRoot)
            {
                this.ThrowIfDisposed();
                this.RejectCodecReentry();
                if (this.Count == 0)
                    throw new InvalidOperationException("Queue is empty");

                if (this.buffer.Count == 0)
                    this.readToBuffer();

                return this.buffer[0].Value;
            }
        }

        /// <summary>
        /// Dequeue element from list
        /// </summary>
        /// <returns>item dequeued</returns>
        public T Dequeue()
        {
            lock (this.Storage.SynchronizationRoot)
            {
                this.ThrowIfDisposed();
                this.RejectCodecReentry();
                if (this.Count == 0)
                    throw new InvalidOperationException("Queue is empty");

                if (this.buffer.Count == 0)
                    this.readToBuffer();

                QueueItem ret = this.buffer[0];
                this.Storage.Remove(ret.PersistId);
                this.buffer.RemoveAt(0);

                if (this.buffer.Count == 0) // buffer is empty
                    this.lastBufferedIndex = ulong.MinValue;

                return ret.Value;
            }
        }

        private void readToBuffer()
        {
            LimitedList<QueueItem> queueBuffer = this.buffer;
            int limit = queueBuffer.Limit;
            var selected = new LimitedList<KeyValuePair<ulong, long>>(limit);
            byte[] prefix = new byte[sizeof(ulong)];
            DataStorage storage = this.Storage.Storage;
            // Physical order can differ from FIFO after space reuse. Select only bounded
            // index/position metadata before reading any selected record's full payload.
            long position = storage.FirstPosition;
            while (position != long.MinValue)
            {
                long recordPosition = position;
                position = storage.ReadDataPrefix(recordPosition, prefix);
                ulong itemIndex = BinaryPrimitives.ReadUInt64LittleEndian(prefix);
                if (selected.Count == limit && itemIndex > selected[selected.Count - 1].Key) continue;
                var record = new KeyValuePair<ulong, long>(itemIndex, recordPosition);
                int index = selected.BinarySearch(record, recordIndexComparer);
                selected.InsertLastOut(index < 0 ? ~index : index, record);
            }

            // A failed selected read/decoder leaves the empty buffer available for retry.
            QueueItem[] ready = new QueueItem[selected.Count];
            for (int i = 0; i < ready.Length; i++)
                ready[i] = this.ReadItem(storage.GetData(selected[i].Value), selected[i].Value);
            foreach (QueueItem item in ready) queueBuffer.Add(item);
            this.lastBufferedIndex = ready.Length == 0 ? ulong.MinValue : ready[ready.Length - 1].Index;
        }

        /// <summary>
        /// Clear queue
        /// </summary>
        public void Clear()
        {
            lock (this.Storage.SynchronizationRoot)
            {
                this.ThrowIfDisposed();
                this.RejectCodecReentry();
                this.Storage.Clear();
                this.buffer.Clear();
                this.maxIndex = this.lastBufferedIndex = ulong.MinValue;
            }
        }

        /// <summary>
        /// Get queue count.
        /// </summary>
        public int Count
        {
            get
            {
                lock (this.Storage.SynchronizationRoot)
                {
                    this.ThrowIfDisposed();
                    return this.Storage.Count;
                }
            }
        }

        /// <summary>
        /// Get items enumeration
        /// </summary>
        /// <returns></returns>
        public IEnumerator<T> GetEnumerator()
        {
            QueueItem[] snapshot;
            lock (this.Storage.SynchronizationRoot)
            {
                this.ThrowIfDisposed();
                this.RejectCodecReentry();
                List<QueueItem> items = new(this.Storage.Count);
                foreach (KeyValuePair<long, byte[]> data in this.Storage.Storage.ReadData)
                    items.Add(this.ReadItem(data.Value, data.Key));
                items.Sort(QueueItem.IndexComparer);
                snapshot = [.. items];
            }

            foreach (QueueItem item in snapshot)
                yield return item.Value;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.GetEnumerator();

        /// <summary>
        /// Dispose resources
        /// </summary>
        public void Dispose()
        {
            lock (this.Storage.SynchronizationRoot)
            {
                this.RejectCodecReentry();
                if (this.disposed) return;
                this.disposed = true;
            }
            this.Storage.Dispose();
        }

    }
}
