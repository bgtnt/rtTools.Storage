using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace RtTools
{

    /// <summary>
    /// Manages byte[] objects as stored into stream - Add, Remove, Update
    /// 
    /// File structure is:
    /// 0..3: int header payload size
    /// 4..11: long = position of first RecordHeader structure in file (if = long.MinValue -> no data, otherwise value>=12)
    /// 12..end sequence of empty space and chain of blocks
    /// data is stored in chained positions as: firstId -> firstId.Next -> firstId.Next.Next ... lastId.Prev, lastId
    /// each "Id" points RecordHeader structure with (Previous, Next, info, Length)
    /// 
    /// empty space blocks bigger than min_space are stored into spaces list 
    /// 
    /// if new data is persisted, the first empty place, bigger than data size is used to store it
    /// if data is updated and its position is after the move-factor threshold, first try to move it into empty space
    /// on every data move, last used block is checked and if there is more than 
    /// min_to_truncate empty place at the end of file - file is truncated
    /// 
    /// </summary>
    public sealed partial class DataStorage : IDisposable, ICollection<byte[]>
    {

        #region fields/properties

        /// <summary>
        /// File header offset
        /// </summary>
        public const int HeaderOffset = 4 + 8;

        /// <summary>
        /// File header size (the real data begins)
        /// </summary>
        public int DataOffset { get; }

        /// <summary>
        /// In start, if file has unused bytes in the end - truncate
        /// </summary>
        private readonly uint minTruncate = 1000000;

        /// <summary>
        /// if position is late enough in the file, force move-on-update to decrease defragmentation
        /// </summary>
        private readonly double moveFactor = 0.9;

        /// <summary>
        /// Original owned stream. Direct access bypasses failure tracking and can invalidate
        /// allocator metadata and resident caches; coordinate access with the owning wrapper.
        /// </summary>
        public Stream Stream { get; private set; }

        /// <summary>
        /// Get First block Position 
        /// </summary>
        public long FirstPosition { get; private set; }

        /// <summary>
        /// Get Last block ID
        /// </summary>
        public long LastPosition { get; private set; } = long.MinValue;

        /// <summary>
        /// Get objects stored count
        /// </summary>
        public int Count { get; private set; }

        /// <summary>
        /// Read all block Positions
        /// </summary>
        public IEnumerable<long> ReadPositions
        {
            get
            {
                this.ThrowIfUnavailable();
                RecordHeader header = new();
                byte[] buffer = new byte[RecordHeader.HeaderSize];
                long pos = this.FirstPosition;
                while (pos != long.MinValue)
                {
                    header = this.ReadRecordHeader(pos, buffer);
                    yield return pos;
                    this.ThrowIfUnavailable();
                    pos = header.Next;
                }
            }
        }

        /// <summary>
        /// Read all block Positions
        /// </summary>
        public IEnumerable<KeyValuePair<long, byte[]>> ReadData
        {
            get
            {
                this.ThrowIfUnavailable();
                RecordHeader header = new();
                byte[] buffer = new byte[RecordHeader.HeaderSize];
                long pos = this.FirstPosition;
                while (pos != long.MinValue)
                {
                    header = this.ReadRecordHeader(pos, buffer);

                    byte[] data = new byte[header.DataLength];
                    ReadExactly(this.ioStream, data, header.DataLength);
                    yield return new KeyValuePair<long, byte[]>(pos, data);
                    this.ThrowIfUnavailable();

                    pos = header.Next;
                }
            }
        }

        /// <summary>
        /// Read all block Positions
        /// </summary>
        public IEnumerable<KeyValuePair<long, object?>> ReadObjects
        {
            get
            {
                this.ThrowIfUnavailable();
                foreach (KeyValuePair<long, byte[]> data in this.ReadData)
                {
                    this.ThrowIfUnavailable();
                    object? value = this.FromBytes(data.Value);
                    this.ThrowIfUnavailable();
                    yield return new KeyValuePair<long, object?>(data.Key, value);
                }
            }
        }

        #endregion

        /// <summary>
        /// Delegate to do operation on initial scan (to avoid rescan)
        /// </summary>
        /// <param name="pos"></param>
        /// <param name="data"></param>
        public delegate void DataScan(long pos, byte[] data);

        /// <summary>
        /// Delegate to do operation on initial scan (to avoid rescan)
        /// </summary>
        /// <param name="pos"></param>
        /// <param name="data"></param>
        public delegate void ObjectScan(long pos, object? data);

        private static void ReadExactly(Stream stream, byte[] buffer, int count) =>
            ReadExactly(stream, buffer, 0, count);

        private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = stream.Read(buffer, offset, count);
                if (read <= 0)
                    UnexpectedEnd(stream);

                offset += read;
                count -= read;
            }
        }

        private static void ReadExactly(Stream stream, Span<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                int read = stream.Read(buffer);
                if (read <= 0)
                    UnexpectedEnd(stream);

                buffer = buffer[read..];
            }
        }

        private RecordHeader ReadRecordHeader(long position, Span<byte> buffer)
        {
            this.ThrowIfUnavailable();
            long streamLength = this.ioStream.Length;
            if (position < this.DataOffset || position > streamLength - RecordHeader.HeaderSize)
                throw new DataSourceException("record header is outside the stream at " + position);
            RecordHeader header = new(this.ioStream, position, buffer);
            header.ValidateBounds(streamLength);
            return header;
        }

        private static int ReadInt32LittleEndian(byte[] buffer, int offset = 0) =>
            BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset));

        private static long ReadInt64LittleEndian(byte[] buffer, int offset = 0) =>
            BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset));

        private static void WriteInt64LittleEndian(Stream stream, long value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
            stream.Write(buffer);
        }

        #region Header Values maintaining

        /// <summary>
        /// Get header value structure
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="offset"></param>
        /// <returns></returns>
        public T GetHeaderValue<T>(int offset) where T : unmanaged
        {
            this.ThrowIfUnavailable();
            int len = Unsafe.SizeOf<T>();
            if (offset < HeaderOffset || offset > (this.DataOffset - len))
                throw new DataSourceException(offset + " is not between 12 and " + (this.DataOffset - len));
            Span<byte> buffer = len <= 256 ? stackalloc byte[len] : new byte[len];
            // read from file as byte[]
            this.ioStream.Seek(offset, SeekOrigin.Begin);
            ReadExactly(this.ioStream, buffer);

            return MemoryMarshal.Read<T>(buffer);
        }

        /// <summary>
        /// Set header value
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="offset"></param>
        /// <param name="value"></param>
        public void SetHeaderValue<T>(int offset, T value) where T : unmanaged
        {
            this.ThrowIfUnavailable();
            int len = Unsafe.SizeOf<T>();
            if (offset < HeaderOffset || offset > (this.DataOffset - len))
                throw new DataSourceException(offset + " is not between 12 and " + (this.DataOffset - len));

            Span<byte> buffer = len <= 256 ? stackalloc byte[len] : new byte[len];
            MemoryMarshal.Write(buffer, in value);

            // write data into stream
            this.ioStream.Seek(offset, SeekOrigin.Begin);
            this.ioStream.Write(buffer);
        }

        /// <summary>
        /// Operation when direct update persistent structure
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="original"></param>
        /// <returns></returns>
        public delegate T UpdateStructureDelegate<T>(T original) where T : unmanaged;

        /// <summary>
        /// Updates Header value at specified offset
        /// 
        /// 1. Direct Read from internal stream at offset => value of type T;
        /// 2. Call value = callback(value);
        /// 3. Direct store new value into stream;
        /// 4. returns new value
        /// <example>emulates Sequence - every time returns stored value, increased by 1
        /// <code>Storage.UpdateHeaderValue&lt;int&gt;(24, (i => i + 1));</code></example>
        /// </summary>
        /// <typeparam name="T">type to treat value at specified position</typeparam>
        /// <param name="offset">data position to update data (>=12)</param>
        /// <param name="callback">callback to update data read at position</param>
        /// <returns>callback result over data read</returns>
        public T UpdateHeaderValue<T>(int offset, UpdateStructureDelegate<T> callback) where T : unmanaged
        {
            this.ThrowIfUnavailable();
            int len = Unsafe.SizeOf<T>();
            if (offset < HeaderOffset || offset > (this.DataOffset - len))
                throw new DataSourceException("offset should be between 12 and " + (this.DataOffset - len));

            Span<byte> buffer = len <= 256 ? stackalloc byte[len] : new byte[len];

            // read from file as byte[]
            this.ioStream.Seek(offset, SeekOrigin.Begin);
            ReadExactly(this.ioStream, buffer);
            T value = callback(MemoryMarshal.Read<T>(buffer));
            this.ThrowIfUnavailable();
            MemoryMarshal.Write(buffer, in value);

            // write data into stream
            this.ioStream.Seek(offset, SeekOrigin.Begin);
            this.ioStream.Write(buffer);

            return value;
        }

        #endregion

        #region auto serialization/deserialization members

        /// <summary>
        /// to convert byte[] to object and vice versa
        /// </summary>
        public IDataSerializer Serializer { get; }

        /// <summary>
        /// Serializes an object to data using <see cref="IDataSerializer"/>.
        /// </summary>
        /// <param name="obj">object to serialize</param>
        /// <returns>serialized data</returns>
        public byte[] ToBytes(object? obj)
        {
            if (obj == null) return [];

            using (MemoryStream ms = new())
            { // serialize into byte[]
                this.Serializer.Serialize(ms, obj);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Deserializes data into an object using <see cref="IDataSerializer"/>.
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public object? FromBytes(byte[]? data) => this.FromBytes(data, 0, data?.Length ?? 0);

        /// <summary>
        /// Deserializes data into an object using <see cref="IDataSerializer"/>.
        /// </summary>
        /// <param name="data">data to deserialize</param>
        /// <returns>deserialized object</returns>
        public object? FromBytes(byte[]? data, int start, int len)
        {
            if (data == null || len == 0) return null;

            using (MemoryStream ms = new(data, start, len)) // deserialize from byte[]
                try { return this.Serializer.Deserialize(ms); }
                catch (Exception ex) { throw new DataSourceException("deserialization", ex); }
        }

        #endregion

        public delegate void OpenStateDelegate(OpenState state, object data);

        /// <summary>
        /// </summary>
        /// <param name="stream">stream to store data into</param>
        /// <param name="scanBytesCallback">callback to call for each byte[] array read</param>
        /// <param name="scanObjectsCallback">callback to call for each deserialized object (if defined byte[] is deserialized to object using <see cref="IDataSerializer"/>)</param>
        /// <param name="headerSize">reserved user-header bytes after the 12-byte storage header</param>
        /// <param name="minDataChunk">min data chunk stored (helps for fragmentation)</param>
        /// <param name="moveFactorPercent">percentage to move data forward</param>
        /// <param name="minUnusedTruncate">mid unused data at the stream end to remove</param>
        /// <param name="serializer">serializer to be used for serialize/deserialize</param>
        /// <param name="onException">on exception action</param>
        /// <param name="listener">action on Recover !!! it is called after recovery, but before re-reading - so = point to put Objects re-initialization</param>
        public DataStorage(Stream stream,
            DataScan? scanBytesCallback = null,
            ObjectScan? scanObjectsCallback = null,
            ushort headerSize = 0,
            int minDataChunk = 256,
            byte moveFactorPercent = 90,
            uint minUnusedTruncate = 1000000,
            IDataSerializer? serializer = null,
            OnException onException = OnException.Default,
            OpenStateDelegate? listener = null)
            : this(new SourceCleanup(true), stream, scanBytesCallback, scanObjectsCallback, headerSize, minDataChunk,
                  moveFactorPercent, minUnusedTruncate, serializer, onException, listener) { }

        // Owning wrappers with their own construction catch delegate physical-failure
        // cleanup to that catch, avoiding a second close on a caller-supplied stream.
        internal DataStorage(SourceCleanup sourceCleanup, Stream stream,
            DataScan? scanBytesCallback = null,
            ObjectScan? scanObjectsCallback = null,
            ushort headerSize = 0,
            int minDataChunk = 256,
            byte moveFactorPercent = 90,
            uint minUnusedTruncate = 1000000,
            IDataSerializer? serializer = null,
            OnException onException = OnException.Default,
            OpenStateDelegate? listener = null)
        {
            OpenState state = OpenState.Default;

            ArgumentNullException.ThrowIfNull(sourceCleanup);
            this.sourceCleanup = sourceCleanup;
            ArgumentNullException.ThrowIfNull(stream);
            this.Stream = stream;
            this.ioStream = new SourceIO(this);
            this.DataOffset = HeaderOffset + headerSize; // header(4), firstId(8) [lastId(8) is not stored];
            this.minDataChunk = minDataChunk;
            this.moveFactor = moveFactorPercent / 100.0;
            this.minTruncate = minUnusedTruncate;
            this.Serializer = serializer ?? DataContractDataSerializer.Default;

            try
            {
                long streamLength = this.ioStream.Length;
                while (true)
                {
                    bool canRecoverRecords = false;
                    try
                    {
                        this.FirstPosition = this.LastPosition = long.MinValue; // reset scan state
                        this.Count = 0;
                        this.spaces.Clear();

                    if (streamLength != 0)
                    { // open existing file

                        #region read first position

                        if (streamLength < HeaderOffset)
                            throw new DataSourceException("invalid stream header - storage header is incomplete");

                        byte[] buffer = new byte[HeaderOffset];
                        this.ioStream.Seek(0, SeekOrigin.Begin);
                        ReadExactly(this.ioStream, buffer, HeaderOffset);
                        int hsize = ReadInt32LittleEndian(buffer);
                        if ((uint)hsize > ushort.MaxValue || HeaderOffset + hsize > streamLength)
                            throw new DataSourceException("invalid stream header - header size is outside the stream: " + hsize);

                        if (hsize != headerSize)
                            if ((onException & OnException.Recover) == OnException.Recover) // if set to recoverHeaderSize
                                headerSize = (ushort)hsize;
                            else
                                throw new DataSourceException("invalid stream header - header size mismatch - " + headerSize + " needed, " + hsize + " found");

                        this.DataOffset = HeaderOffset + headerSize;
                        canRecoverRecords = true;

                        long pos = ReadInt64LittleEndian(buffer, 4);
                        if (pos != long.MinValue && !(this.DataOffset <= pos && pos < streamLength)) // check first position is in range
                            throw new DataSourceException(pos + " is not between " + this.DataOffset + " and " + streamLength);

                        this.FirstPosition = pos;

                        #endregion

                        // skip header bytes
                        // Skip the unread header bytes before scanning records.
                        #region scan all file to obtain last Id

                        RecordHeader header = new();
                        buffer = new byte[RecordHeader.HeaderSize];

                        // initialize range to check
                        long start = this.DataOffset;
                        long free = 0;

                        bool scanner = scanBytesCallback != null || scanObjectsCallback != null;

                        while (pos != long.MinValue)
                        {
                            if (!(this.DataOffset <= pos && pos < streamLength)) // check position if is in range
                                throw new DataSourceException(pos + " is not between " + this.DataOffset + " and " + streamLength);

                            // read record header
                            header = this.ReadRecordHeader(pos, buffer);

                            if (header.Prev != this.LastPosition)
                                throw new DataSourceException("invalid chain detected at " + pos);

                            if (pos > start)
                            {
                                free += (pos - start);
                                this.MarkFree(start, pos);
                            }

                            if (scanner)
                            { // data callback provided
                                byte[] data = new byte[header.DataLength];
                                ReadExactly(this.ioStream, data, header.DataLength);
                                scanBytesCallback?.Invoke(pos, data);
                                this.ThrowIfUnavailable();
                                if (scanObjectsCallback != null)
                                {
                                    object? item = this.FromBytes(data);
                                    this.ThrowIfUnavailable();
                                    scanObjectsCallback(pos, item);
                                    this.ThrowIfUnavailable();
                                }
                            }

                            if (header.RecordLength < this.minDataChunk) // if have persistent, less than current
                                this.minDataChunk = header.RecordLength;

                            // increase objects count
                            this.Count++;

                            this.LastPosition = pos;

                            start = pos + header.RecordLength;

                            pos = header.Next;

                            if (pos < start && pos != long.MinValue) // error detected
                                throw new DataSourceException("invalid chain detected at " + this.LastPosition);
                        }
                        #endregion

                        if (streamLength > start)
                        {
                            long truncateTo = Math.Max(start, this.minTruncate);
                            if (streamLength - truncateTo >= this.minTruncate) // could be stripped
                                this.ioStream.SetLength(streamLength = truncateTo);

                            // mark bytes left as free;
                            this.MarkFree(start, streamLength);
                        }

                        //Debug.WriteLine(free + " free of " + streamLength);
                    }
                    else // create new file
                        this.Clear();

                        state |= OpenState.Opened;
                        listener?.Invoke(state, this);
                        this.ThrowIfUnavailable();
                        break;
                    }
                    catch (Exception ex)
                    { // exception - do OnException action
                        if (this.IsFaulted) throw;
                        ExceptionDispatchInfo primaryFailure = ExceptionDispatchInfo.Capture(ex);
                        state |= OpenState.Failed;
                        this.NotifyOpeningFailure(listener, state, ex, primaryFailure);

                        if (onException == OnException.Default) onException = OnException.CloseAndThrow;

                        if (canRecoverRecords && (onException & OnException.Recover) == OnException.Recover)
                            try
                            {
                                if (this.TryToRecover(ex as DataSourceException, headerSize, scanObjectsCallback != null)) // header is recovered
                                {
                                    onException &= ~OnException.Recover; // remove Recover flag - try just once
                                    state |= OpenState.Recovered;
                                    listener?.Invoke(state, this);
                                    streamLength = this.ioStream.Length;
                                    continue;
                                }
                            }
                            catch (Exception recoverException)
                            {
                                if (this.IsFaulted) throw;
                                Debug.WriteLine(recoverException);
                            }

                        if ((onException & OnException.Clear) == OnException.Clear)
                        { // !!! Clear all data
                            this.Clear();
                            this.ClearHeader();
                            state |= OpenState.Cleared;
                            listener?.Invoke(state, this);
                        }

                        if ((onException & OnException.CloseSource) == OnException.CloseSource)
                        {
                            this.ioStream.Close();
                            state |= OpenState.Closed;
                            this.NotifyOpeningFailure(listener, state, this, primaryFailure);
                        }

                        if ((onException & OnException.Throw) == OnException.Throw)
                        {
                            state |= OpenState.Throw;
                            this.NotifyOpeningFailure(listener, state, ex, primaryFailure);
                            throw;
                        }

                        break;
                    }
                }
            }
            catch (Exception) when (this.IsFaulted)
            {
                Exception first = this.StorageException!;
                if (this.sourceCleanup.CloseOnFailure)
                {
                    try { this.CloseSource(); }
                    catch (Exception) { } // Retain the original source/partial-mutation failure.
                }
                ExceptionDispatchInfo.Capture(first).Throw();
                throw;
            }
            catch
            {
                // A secondary application notification must not skip an owning
                // constructor's configured close. An actual close failure remains
                // a source failure and propagates with its original identity.
                if (this.sourceCleanup.CloseOnFailure &&
                    (onException == OnException.Default || (onException & OnException.CloseSource) != 0))
                    this.CloseSource();
                throw;
            }
        }

        private void NotifyOpeningFailure(OpenStateDelegate? listener, OpenState state, object data, ExceptionDispatchInfo primary)
        {
            try { listener?.Invoke(state, data); }
            catch (Exception) when (!this.IsFaulted)
            {
                primary.Throw();
                throw;
            }
        }

        /// <summary>
        /// flush internal stream
        /// </summary>
        public void Flush()
        {
            this.ThrowIfUnavailable();
            this.ioStream.Flush();
        }

        /// <summary>
        /// Get object by resource position (ID) 
        /// </summary>
        /// <param name="pos">position to read object</param>
        /// <returns>deserialized object from data using <see cref="IDataSerializer"/></returns>
        public object? this[long pos]
        {
            get
            {
                this.ThrowIfUnavailable();
                object? value = this.FromBytes(this.GetData(pos));
                this.ThrowIfUnavailable();
                return value;
            }
        }

        /// <summary>
        /// Get data at resource position (ID)
        /// </summary>
        /// <param name="pos">position to read data</param>
        /// <returns>data read</returns>
        public byte[] GetData(long pos)
        {
            Span<byte> buffer = stackalloc byte[RecordHeader.HeaderSize];
            RecordHeader header = this.ReadRecordHeader(pos, buffer);
            byte[] ret = new byte[header.DataLength];
            ReadExactly(this.ioStream, ret, header.DataLength);
            return ret;
        }

        // Read ordering metadata without allocating or reading the remaining payload.
        // Internal callers coordinate stream access using their storage lock.
        internal long ReadDataPrefix(long pos, byte[] prefix)
        {
            Span<byte> buffer = stackalloc byte[RecordHeader.HeaderSize];
            RecordHeader header = this.ReadRecordHeader(pos, buffer);
            if (header.DataLength < prefix.Length)
                throw new InvalidDataException("Stored record is shorter than the requested prefix.");
            ReadExactly(this.ioStream, prefix, prefix.Length);
            return header.Next;
        }

        /// <summary>
        /// Copy internal stream into destination
        /// </summary>
        /// <param name="destination"></param>
        public void CopyTo(Stream destination)
        {
            this.ThrowIfUnavailable();
            ArgumentNullException.ThrowIfNull(destination);
            const int bufferSize = 0x1000;
            int read;

            this.ioStream.Seek(0, SeekOrigin.Begin);
            byte[] buffer = new byte[bufferSize];
            while ((read = this.ioStream.Read(buffer, 0, bufferSize)) != 0)
                destination.Write(buffer, 0, read);
        }

        #region Add, Update, Remove methods

        /// <summary>
        /// Data Version (increases on every add/update/remove)
        /// </summary>
        public int Version { get; private set; }

        /// <summary>
        /// Add data into storage
        /// </summary>
        /// <param name="data">data to add</param>
        /// <returns>data position (resource ID)</returns>
        public long Add(byte[] data) => this.update(long.MinValue, data, false);
        /// <summary>
        /// Add object into storage (serializes into data, using <see cref="IDataSerializer"/>) <see cref="Add(byte[])"/>
        /// </summary>
        /// <param name="obj"></param>
        /// <returns>data position (resource ID)</returns>
        public long Add(object? obj)
        {
            this.ThrowIfUnavailable();
            return this.Add(this.ToBytes(obj));
        }

        /// <summary>
        /// Update existing storage data
        /// </summary>
        /// <param name="pos">existing data storage position</param>
        /// <param name="data">new data to replace old</param>
        /// <returns>new data position (resource ID)</returns>
        public long Update(long pos, byte[] data) => this.update(pos, data, true);
        /// <summary>
        /// Update existing storage object (serializes into data, using <see cref="IDataSerializer"/>) <see cref="Update(long, byte[])"/>
        /// </summary>
        /// <param name="pos"></param>
        /// <param name="obj"></param>
        /// <returns></returns>
        public long Update(long pos, object? obj)
        {
            this.ThrowIfUnavailable();
            return this.Update(pos, this.ToBytes(obj));
        }

        private long update(long pos, in byte[] data, in bool existing)
        {
            this.ThrowIfUnavailable();
            ArgumentNullException.ThrowIfNull(data);
            long epoch = Volatile.Read(ref this.mutationEpoch);
            try { return this.updateCore(pos, data, existing); }
            catch (Exception error) { this.RememberMutationFailure(epoch, error); throw; }
        }

        private long updateCore(long pos, in byte[] data, in bool existing)
        {
            int dataLength = data.Length;
            Span<byte> buffer = stackalloc byte[RecordHeader.HeaderSize];
            bool createRecord = !existing;
            if (existing)
            { // data is to be updated
                RecordHeader header = this.ReadRecordHeader(pos, buffer);
                if ((header.Next == long.MinValue) || // not last record - no need to worry about next
                    ((header.Next - pos - RecordHeader.HeaderSize) < dataLength) || // not enough space to store data
                    (pos > this.ioStream.Length * this.moveFactor))  // id is close to the end of file - so move forward to defragment
                { // persistence is in the end of file, so move it to decrease defragmentation
                    pos = this.remove(header, buffer); // remove current data record
                    createRecord = true;
                }
                else if (header.DataLength != dataLength)
                { // the only thing to do is to update DataLength in header
                    this.MarkFree(pos, header.Next, true);
                    header.DataLength = dataLength;
                    header.Write(this.ioStream, buffer);
                }
            }

            if (createRecord)
            {
                if (this.LastPosition == long.MinValue)
                { // file is empty
                    pos = this.DataOffset;
                    new RecordHeader() { Position = pos, Prev = this.LastPosition, DataLength = dataLength }.Write(this.ioStream, buffer);
                }
                else
                { // nonempty file
                    Interval<long>? found = this.FindFirstToFit(RecordHeader.HeaderSize + dataLength);

                    if (found != null && (found.End < this.ioStream.Length))
                    { // empty space found
                        RecordHeader hNext = this.ReadRecordHeader(found.End, buffer);
                        pos = found.Start;
                        if (hNext.Prev != long.MinValue)
                        {
                            RecordHeader h = this.ReadRecordHeader(hNext.Prev, buffer);
                            Debug.Assert(h.Next == found.End, h.Next + "!=" + found.End);
                            h.Next = pos;
                            h.Write(this.ioStream, buffer);
                        }

                        RecordHeader header = new() { Position = pos, Prev = hNext.Prev, Next = found.End, DataLength = dataLength };
                        // update last record to point current as next
                        hNext.Prev = pos;
                        hNext.Write(this.ioStream, buffer);

                        // write down new header
                        header.Write(this.ioStream, buffer);
                    }
                    else
                    { // no empty space found - write at the end of file
                        RecordHeader header = this.ReadRecordHeader(this.LastPosition, buffer);
                        Debug.Assert(header.Next == long.MinValue, header.Next + "!=" + long.MinValue);

                        // update last record to point current as next
                        header.Next = pos = this.LastPosition + header.RecordLength;
                        header.Write(this.ioStream, buffer);

                        new RecordHeader() { Position = pos, Prev = this.LastPosition, DataLength = dataLength }.Write(this.ioStream, buffer);
                    }
                }
                this.Count++;
            }

            this.ioStream.Write(data, 0, dataLength);
            this.MarkUsed(pos, pos + RecordHeader.HeaderSize + dataLength);

            if (pos > this.LastPosition) this.LastPosition = pos;

            if ((this.FirstPosition == long.MinValue) || (pos < this.FirstPosition))
            { // file was empty, so this is first id as well
                this.ioStream.Seek(4, SeekOrigin.Begin);
                WriteInt64LittleEndian(this.ioStream, this.FirstPosition = pos);
            }
            this.Version++;
            return pos;
        }

        private long remove(RecordHeader header, Span<byte> buffer)
        {
            RecordHeader hPrev = default;
            bool hasPrev = header.Prev != long.MinValue;

            if (header.Prev != long.MinValue)
            { // has previous (should mark previous to point next)
                hPrev = this.ReadRecordHeader(header.Prev, buffer);
                Debug.Assert(hPrev.Next == header.Position, hPrev.Next + "!=" + header.Position);
                hPrev.Next = header.Next;
                hPrev.Write(this.ioStream, buffer);
            }
            else
            { // record is first in chain - mark new last
                Debug.Assert(header.Position == this.FirstPosition, header.Position + "!=" + this.FirstPosition);
                // write down new first Id
                this.ioStream.Seek(4, SeekOrigin.Begin);
                WriteInt64LittleEndian(this.ioStream, this.FirstPosition = header.Next);
            }

            if (header.Next != long.MinValue)
            { // has next (should mark next to point previous)
                RecordHeader h = this.ReadRecordHeader(header.Next, buffer);
                Debug.Assert(h.Prev == header.Position, h.Prev + "!=" + header.Position);
                h.Prev = header.Prev;
                h.Write(this.ioStream, buffer);
            }
            else
            { // record is last in chain - mark new last
                Debug.Assert(header.Position == this.LastPosition, header.Position + "!=" + this.LastPosition);

                long streamLength = this.ioStream.Length;
                if ((this.LastPosition = header.Prev) == long.MinValue)
                { // last item was deleted - clear file
                    Debug.Assert(!hasPrev, "prev!=null");
                    Debug.Assert(this.Count == 1, this.Count + "!=1");

                    if (streamLength - this.DataOffset >= this.minTruncate)
                    {
                        this.Clear(this.minTruncate);
                        this.Version++;
                        return long.MinValue;
                    }
                }
                else
                { // previous is not last
                    if (!hasPrev)
                        throw new DataSourceException("Missing previous data header.");

                    long usedLength = Math.Max(header.Prev + hPrev.RecordLength, this.minTruncate);
                    if (streamLength - usedLength >= this.minTruncate)
                    {
                        this.ioStream.SetLength(usedLength);
                        this.MarkUsed(usedLength, streamLength);
                    }
                }
            }

            this.MarkFree(header.Position, header.Next, true);
            this.Count--;

            this.Version++;
            return long.MinValue;
        }

        /// <summary>
        /// Remove data at resource position (ID)
        /// </summary>
        /// <param name="pos">resource position (ID) to remove data at</param>
        /// <returns>should return long.MinValue</returns>
        public long Remove(long pos)
        {
            this.ThrowIfUnavailable();
            long epoch = Volatile.Read(ref this.mutationEpoch);
            try
            {
                Span<byte> buffer = stackalloc byte[RecordHeader.HeaderSize];
                return this.remove(this.ReadRecordHeader(pos, buffer), buffer);
            }
            catch (Exception error) { this.RememberMutationFailure(epoch, error); throw; }
        }

        #endregion

        /// <summary>
        /// Disposes storage resources
        /// </summary>
        public void Dispose()
        {
            this.ThrowIfInIoCallback();
            if (Interlocked.Exchange(ref this.disposed, 1) != 0) return;
            this.CloseSource();
        }

        #region Fragmentation (spaces) members
        /// <summary>
        /// min space to store in bytes
        /// </summary>
        private readonly int minDataChunk;
        /// <summary>
        /// store empty spaces
        /// </summary>
        private readonly List<Interval<Int64>> spaces = [];

        private void MarkUsed(long start, long end)
        {
            if (end == long.MinValue) end = this.ioStream.Length;

            this.MutationStarting();

            Int64Interval range = new(start, end);
            int i = range.RemoveFrom(this.spaces);
            if (i >= 0) // removes next 2 empty blocks if <min_space
                if (i < this.spaces.Count)
                {
                    if (this.spaces[i].Count < this.minDataChunk) this.spaces.RemoveAt(i);
                    else i++;

                    if (i < this.spaces.Count && this.spaces[i].Count < this.minDataChunk) this.spaces.RemoveAt(i);
                }
        }

        private void MarkFree(long start, long end, bool noCheck = false)
        {
            this.MutationStarting();
            if (end == long.MinValue) end = this.ioStream.Length;

            if (noCheck ? (end > start) : ((end - start) >= this.minDataChunk))
            { // store space-range if size is OK
                Int64Interval range = new(start, end);
                range.AddTo(this.spaces);
            }
        }

        private Interval<long>? FindFirstToFit(int size) => this.spaces.Find(range => range.Count >= size);

        #endregion

        void ICollection<byte[]>.Add(byte[] item) => this.Add(item);

        /// <summary>
        /// Clears all data storage
        /// </summary>
        public void Clear() => this.Clear(0);

        /// <summary>
        /// Clear all header (write bytes 0)
        /// </summary>
        public void ClearHeader()
        {
            this.ThrowIfUnavailable();
            int headerSize = this.DataOffset - HeaderOffset;
            // prepare header
            byte[] headerData = new byte[this.DataOffset];
            BinaryPrimitives.WriteInt32LittleEndian(headerData, headerSize);
            BinaryPrimitives.WriteInt64LittleEndian(headerData.AsSpan(4), this.FirstPosition);
            this.ioStream.Seek(0, SeekOrigin.Begin);
            this.ioStream.Write(headerData, 0, this.DataOffset);// write header bytes
        }

        /// <summary>
        /// Clears all data storage
        /// </summary>
        /// <param name="reserveSpace">file size for data (to have used space)</param>
        public void Clear(uint reserveSpace)
        {
            this.ThrowIfUnavailable();
            long epoch = Volatile.Read(ref this.mutationEpoch);
            try { this.clear(reserveSpace); }
            catch (Exception error) { this.RememberMutationFailure(epoch, error); throw; }
        }

        private void clear(uint reserveSpace)
        {
            long newLen = this.DataOffset + reserveSpace;
            if (this.ioStream.Length != newLen)
                this.ioStream.SetLength(newLen); // trim file to clear

            int headerSize = this.DataOffset - HeaderOffset;
            // prepare header
            byte[] headerData = new byte[HeaderOffset];
            BinaryPrimitives.WriteInt32LittleEndian(headerData, headerSize);
            BinaryPrimitives.WriteInt64LittleEndian(headerData.AsSpan(4), long.MinValue);

            byte[] testData = new byte[HeaderOffset];
            this.ioStream.Seek(0, SeekOrigin.Begin);
            ReadExactly(this.ioStream, testData, HeaderOffset);

            this.FirstPosition = this.LastPosition = long.MinValue;

            if (!PersistenceBinary.Equals(testData, headerData))
            { // read not what is expected
                this.ioStream.Seek(0, SeekOrigin.Begin);
                this.ioStream.Write(headerData, 0, HeaderOffset);// write header bytes
            }

            this.spaces.Clear(); // clear spaces list
            if (reserveSpace > 0)
                this.MarkFree(this.DataOffset, newLen);

            this.Count = 0; // clear size
        }

        bool ICollection<byte[]>.Contains(byte[] item)
        {
            this.ThrowIfUnavailable();
            if (item != null)
                foreach (KeyValuePair<long, byte[]> data in this.ReadData)
                    if (PersistenceBinary.Equals(item, data.Value))
                        return true;
            return false;
        }

        void ICollection<byte[]>.CopyTo(byte[][] array, int arrayIndex)
        {
            foreach (KeyValuePair<long, byte[]> data in this.ReadData)
                array[arrayIndex++] = data.Value;
        }

        bool ICollection<byte[]>.IsReadOnly => false;

        bool ICollection<byte[]>.Remove(byte[] item)
        {
            this.ThrowIfUnavailable();
            if (item != null)
                foreach (KeyValuePair<long, byte[]> data in this.ReadData)
                    if (PersistenceBinary.Equals(item, data.Value))
                    {
                        this.Remove(data.Key);
                        return true;
                    }
            return false;
        }

        IEnumerator<byte[]> IEnumerable<byte[]>.GetEnumerator()
        {
            foreach (KeyValuePair<long, byte[]> data in this.ReadData)
                yield return data.Value;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            foreach (KeyValuePair<long, byte[]> data in this.ReadData)
                yield return data.Value;
        }
    }
}

