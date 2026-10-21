using System;
using System.Buffers.Binary;
using System.IO;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;

namespace RtTools.Logger
{
    /// <summary>
    /// Logger record abstraction
    /// </summary>
    /// <typeparam name="TKey"></typeparam>
    internal sealed class LogRecord<TKey> : IComparable<LogRecord<TKey>>, IComparable<TKey> where TKey : Key, new()
    {
        #region Properties

        // for binary search compare
        internal sbyte compareFlag;

        /// <summary>
        /// Get/Set Current Record Key
        /// </summary>
        public TKey Key
        {
            get => this.key;
            set
            {
                value.Write(this.FixedData, this.fixedDataOffset);
                this.key = value;
            }
        }
        private TKey key;

        private readonly byte[] FixedData;
        private readonly int fixedDataOffset;
        private readonly int recordSize;

        /// <summary>
        /// Min record size to try to compress data
        /// </summary>
        private const int MinRecordSizeToCompress = 512;

        /// <summary>
        /// Record Key size
        /// </summary>
        internal static readonly int KeySize = new TKey().Size;

        /// <summary>
        /// Gets if record is deleted
        /// </summary>
        public bool IsDeleted
        {
            get => ((StoreType)this.FixedData[this.fixedDataOffset + KeySize] & StoreType.Deleted) == StoreType.Deleted;
            internal set
            {
                if (value)
                {
                    this.FixedData[this.fixedDataOffset + KeySize] |= (byte)StoreType.Deleted;
                }
                else
                {
                    this.FixedData[this.fixedDataOffset + KeySize] &= 0xFF ^ (byte)StoreType.Deleted;
                }
            }
        }

        /// <summary>
        /// Gets if record is compressed
        /// </summary>
        public bool IsCompressed => ((StoreType)this.FixedData[this.fixedDataOffset + KeySize] & StoreType.Compressed) == StoreType.Compressed;

        /// <summary>
        /// Gets if record payload has an extended part.
        /// </summary>
        public bool IsExtended => ((StoreType)this.FixedData[this.fixedDataOffset + KeySize] & StoreType.Extended) == StoreType.Extended;

        /// <summary>
        /// Return record Options
        /// </summary>
        public StoreType Options => (StoreType)this.FixedData[this.fixedDataOffset + KeySize];

        #endregion

        public LogRecord(int recordSize)
        {
            if (recordSize <= KeySize - sizeof(StoreType) - sizeof(long))
                throw new ArgumentOutOfRangeException(nameof(recordSize), "should be over " + (KeySize - sizeof(StoreType) - sizeof(long)));
            this.recordSize = recordSize;
            this.FixedData = new byte[recordSize];
            this.key = new TKey();
        }

        [ThreadStatic]
        private static TKey? binarySearchKey;

        internal LogRecord(int recordSize, Stream stream)
            : this(recordSize)
        {
            int read = stream.Read(this.FixedData, this.fixedDataOffset, this.recordSize);
            if (read != this.recordSize && (read <= 0 || !ReadRemaining(stream, this.FixedData, this.fixedDataOffset + read, this.recordSize - read)))
                throw new EndOfStreamException();
            this.key = new TKey();
            this.key.Read(this.FixedData, this.fixedDataOffset);
        }

        internal LogRecord(int recordSize, byte[] source, int offset)
        {
            Debug.Assert(recordSize > KeySize - sizeof(StoreType) - sizeof(long));
            Debug.Assert(source != null && offset >= 0 && source.Length - offset >= recordSize);

            this.recordSize = recordSize;
            this.FixedData = source;
            this.fixedDataOffset = offset;
            this.key = new TKey();
            this.key.Read(this.FixedData, this.fixedDataOffset);
        }

        internal bool ReadHeader(Stream stream)
        {
            int bytes = stream.Read(this.FixedData, this.fixedDataOffset, this.recordSize);
            if (bytes != this.recordSize)
                return false;

            this.key = new TKey();
            this.key.Read(this.FixedData, this.fixedDataOffset);
            return true;
        }

        #region Read/Write stream methods

        /// <summary>
        /// Read record data into byte[]
        /// </summary>
        /// <param name="extended"></param>
        /// <returns></returns>
        internal byte[] readRecordData(Stream extended) =>
            this.readRecordData((StoreType)this.FixedData[this.fixedDataOffset + KeySize], extended);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal byte[] readRecordData(StoreType options, Stream extended) =>
            HasExtendedData(options)
                ? this.readExtendedRecordData(options, extended)
                : this.readFixedRecordData(options);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool HasExtendedData(StoreType options) =>
            (options & StoreType.Extended) == StoreType.Extended;

        internal byte[] readExtendedRecordData(StoreType options, Stream extended)
        {
            byte[] data = this.FixedData;
            int fixedRecordSize = this.recordSize;
            int baseOffset = this.fixedDataOffset;
            long extPos = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(baseOffset + fixedRecordSize - sizeof(long)));

            extended.Seek(extPos, SeekOrigin.Begin);
            Span<byte> lengthBuffer = stackalloc byte[sizeof(int)];
            int read = extended.Read(lengthBuffer);
            if (read != lengthBuffer.Length)
                ReadFullRemaining(extended, lengthBuffer, read);
            int extLen = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);

            int extendedDataRoom = fixedRecordSize - KeySize - sizeof(StoreType) - sizeof(long);
            // allocate data space 
            byte[] ret = new byte[extLen + extendedDataRoom];

            Debug.Assert(ret.Length > fixedRecordSize - KeySize - sizeof(StoreType) - getByteLen(fixedRecordSize));

            // read data part from main record
            Buffer.BlockCopy(data, baseOffset + KeySize + 1, ret, 0, extendedDataRoom);
            // read extended data part
            read = extended.Read(ret, extendedDataRoom, extLen);
            if (read != extLen)
                ReadRemainingOrThrow(extended, ret, extendedDataRoom + Math.Max(read, 0), extLen - Math.Max(read, 0));

            if ((options & StoreType.Compressed) == StoreType.Compressed)
            { // compressed data - uncompress
                using (DeflateStream unzip = new(new MemoryStream(ret), CompressionMode.Decompress)) // decompressor
                    ret = Statics.ReadToEnd(unzip);
            }

            return ret;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal byte[] readFixedRecordData(StoreType options)
        {
            byte[] data = this.FixedData;
            int fixedRecordSize = this.recordSize;
            int offset = this.fixedDataOffset + KeySize + 1;
            int len = data[offset++]; // next byte is data size
            if (fixedRecordSize >= 0xFF)
            {
                len += (data[offset++] << 8);
                if (fixedRecordSize >= 0xFFFF)
                {
                    len += (data[offset++] << 16);
                    if (fixedRecordSize >= 0xFFFFFF)
                        len += (data[offset++] << 24);
                }
            }

            Debug.Assert(len <= fixedRecordSize - KeySize - sizeof(StoreType) - getByteLen(fixedRecordSize));

            if ((options & StoreType.Compressed) == StoreType.Compressed)
            { // compressed data - uncompress
                using (DeflateStream unzip = new(new MemoryStream(data, offset, len), CompressionMode.Decompress)) // decompressor
                    return Statics.ReadToEnd(unzip);
            }

            byte[] ret = new byte[len];
            Buffer.BlockCopy(data, offset, ret, 0, len);
            return ret;
        }

        private static bool ReadRemaining(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = stream.Read(buffer, offset, count);
                if (read <= 0)
                    return false;

                offset += read;
                count -= read;
            }

            return true;
        }

        private static void ReadFullRemaining(Stream stream, Span<byte> buffer, int read)
        {
            if (read <= 0)
                throw new EndOfStreamException();

            buffer = buffer[read..];
            while (!buffer.IsEmpty)
            {
                read = stream.Read(buffer);
                if (read <= 0)
                    throw new EndOfStreamException();

                buffer = buffer[read..];
            }
        }

        private static void ReadRemainingOrThrow(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = stream.Read(buffer, offset, count);
                if (read <= 0)
                    throw new EndOfStreamException();

                offset += read;
                count -= read;
            }
        }

        internal void writeRecordHeader(Stream main) => main.Write(this.FixedData, this.fixedDataOffset, this.recordSize);

        /// <summary>
        /// Write Fill record data into main and extended streams
        /// </summary>
        /// <param name="main"></param>
        /// <param name="extended"></param>
        /// <param name="data">data to write</param>
        internal void writeRecordData(Stream main, Stream extended, byte[] data)
        {
            int dataLength = data.Length;
            byte[] buffer = this.FixedData;
            int baseOffset = this.fixedDataOffset;

            int fixedRecordSize = this.recordSize;

            int fixedDataRoom = fixedRecordSize - KeySize - sizeof(StoreType) - 1;
            if (fixedRecordSize >= 0xFF)
            {
                fixedDataRoom--;
                if (fixedRecordSize >= 0xFFFF)
                {
                    fixedDataRoom--;
                    if (fixedRecordSize >= 0xFFFFFF)
                        fixedDataRoom--;
                }
            }

            StoreType st = StoreType.None;
            if (dataLength > fixedDataRoom && dataLength > MinRecordSizeToCompress)
            { // length is ok to try to compress
                MemoryStream compressedStream = new();
                using (DeflateStream zipStream = new(compressedStream, CompressionLevel.Fastest))
                    zipStream.Write(data, 0, dataLength);

                byte[] compressed = compressedStream.ToArray();
                if (compressed.Length < dataLength)
                {
                    data = compressed;
                    dataLength = data.Length;
                    st = StoreType.Compressed;
                }
            }

            if (dataLength > fixedDataRoom)
            { // extended record
                // write data options (extended)
                buffer[baseOffset + KeySize] = (byte)(st | StoreType.Extended);
                // prepare buffer
                int extendedDataRoom = fixedRecordSize - KeySize - sizeof(StoreType) - sizeof(long);
                Buffer.BlockCopy(data, 0, buffer, baseOffset + KeySize + 1, extendedDataRoom);
                BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(baseOffset + fixedRecordSize - sizeof(long)), extended.Position);
                // Write the extension before exposing its fixed slot; this is not an atomic write.
                dataLength -= extendedDataRoom;
                Span<byte> lengthBuffer = stackalloc byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(lengthBuffer, dataLength);
                extended.Write(lengthBuffer);
                extended.Write(data, extendedDataRoom, dataLength);
                main.Write(buffer, baseOffset, fixedRecordSize);
            }
            else
            { // fixed record
                // write data options (not extended)
                int offset = baseOffset + KeySize;
                buffer[offset++] = (byte)st;

                // store data length
                buffer[offset++] = (byte)dataLength;
                if (fixedRecordSize >= 0xFF)
                {
                    buffer[offset++] = (byte)(dataLength >> 8);
                    if (fixedRecordSize >= 0xFFFF)
                    {
                        buffer[offset++] = (byte)(dataLength >> 16);
                        if (fixedRecordSize >= 0xFFFFFF)
                            buffer[offset++] = (byte)(dataLength >> 24);
                    }
                }

                // write data itself
                Buffer.BlockCopy(data, 0, buffer, offset, dataLength);

                // write record (i bytes are used)
                main.Write(buffer, baseOffset, fixedRecordSize);
            }
        }

        static int getByteLen(int size)
        {
            if (size <= 0x100)
                return 1;
            if (size <= 0x10000)
                return 2;
            if (size <= 0x1000000)
                return 3;

            return 4;
        }

        #endregion

        #region comparison/BinarySearch methods

        public int CompareTo(LogRecord<TKey>? other)
        {
            if (other is null)
                return 1;

            int ret = this.key.CompareTo(other.key);
            return (ret == 0) ? this.compareFlag.CompareTo(0) : ret;
        }

        public int CompareTo(TKey? other)
        {
            int ret = this.key.CompareTo(other);
            return (ret == 0) ? this.compareFlag.CompareTo(0) : ret;
        }

        /// <summary>
        /// Binary search into string area
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="startRec"></param>
        /// <param name="endRec"></param>
        /// <returns></returns>
        internal long BinarySearch(Stream stream, long startRec, long endRec)
        {
            TKey key = binarySearchKey ??= new TKey();
            int fixedRecordSize = this.recordSize;
            --endRec;
            while (startRec <= endRec)
            {
                long middle = startRec + ((endRec - startRec) >> 1);
                stream.Seek(middle * fixedRecordSize, SeekOrigin.Begin);
                key.Read(stream);

                int result = this.CompareTo(key);
                if (result == 0) return middle;
                if (result > 0)
                    startRec = middle + 1;
                else
                    endRec = middle - 1;
            }
            return ~startRec;
        }

        #endregion

        public override string ToString() => this.key + ": " + BitConverter.ToString(this.FixedData, this.fixedDataOffset, this.recordSize);
    }
}
