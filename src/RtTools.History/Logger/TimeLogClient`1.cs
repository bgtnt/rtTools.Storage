using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using RtTools;
using System.Threading;

namespace RtTools.Logger
{
    /// <summary>
    /// Reads timestamp-indexed history over inclusive time intervals.
    /// </summary>
    /// <typeparam name="TRecord"></typeparam>
    /// <remarks>
    /// Queries snapshot the number of complete fixed records; a partial trailing fixed record is ignored.
    /// Physical EOF during the sequential scan stops that scan and returns its prefix. Header-only queries
    /// do not read payloads; ForEach counts a header before reading its payload, so its returned count can
    /// exceed callback invocations when a payload is incomplete. Physical EOF during the initial binary
    /// search propagates. Decoder, action and predicate exceptions always propagate unchanged, including
    /// EndOfStreamException; they are not treated as physical EOF.
    /// </remarks>
    public sealed class TimeLogClient<TRecord> : IDisposable
    {
        private readonly Stream main;
        private readonly Stream ext;
        private readonly Converter<byte[], TRecord>? itemReader;
        private readonly object syncRoot = new();

        /// <summary>Opens the fixed-record and payload files for reading.</summary>
        public TimeLogClient(string mainFileName, string extFileName, int recBufferSize = 0x1000, int extBufferSize = 0x10000, Converter<byte[], TRecord>? reader = null)
        {
            this.itemReader = reader;
            this.main = new FileStream(mainFileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, recBufferSize * TimeLog<TRecord>.RecordSize, FileOptions.RandomAccess);
            try
            {
                this.ext = new FileStream(extFileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, extBufferSize, FileOptions.RandomAccess);
            }
            catch
            {
                try { this.main.Dispose(); }
                catch { } // Preserve the opening failure after attempting to release the acquired reader.
                throw;
            }
        }

        internal TimeLogClient(TimeLog<TRecord> dbase, int recBufferSize = 0x1000, int extBufferSize = 0x10000)
            : this(dbase.MainFileName, dbase.ExtendedFileName, recBufferSize, extBufferSize, dbase.ItemReader) { }

        private TRecord FromBytes(byte[] data)
        {
            TRecord? record = this.itemReader != null
                ? this.itemReader(data)
                : (TRecord?)PersistenceBinary.FromBytes(data);
            return record is null
                ? throw new InvalidDataException("The time log record deserialized to null.")
                : record;
        }

        // Physical record extraction only: no decoder, action or predicate runs inside its caller's EOF catch.
        private byte[] ReadRecordData(byte[] buffer, long dbin)
        {
            if (dbin >= 0)
            {
                this.ext.Seek(BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(sizeof(long))), SeekOrigin.Begin);
                this.ext.ReadExactly(buffer, 0, sizeof(int));
                byte[] data = new byte[BinaryPrimitives.ReadInt32LittleEndian(buffer)];
                this.ext.ReadExactly(data, 0, data.Length);
                return data;
            }

            int length = buffer[sizeof(long)];
            byte[] inline = new byte[length];
            Array.Copy(buffer, sizeof(long) + 1, inline, 0, length);
            return inline;
        }

        static long BinarySearch(DateTime date, Stream stream, long startRec, long endRec)
        {
            long ldate = date.ToUniversalTime().ToBinary();
            byte[] buffer = new byte[sizeof(long)];

            --endRec;
            while (startRec <= endRec)
            {
                long middle = startRec + ((endRec - startRec) >> 1);
                stream.Seek(middle * TimeLog<TRecord>.RecordSize, SeekOrigin.Begin);
                stream.ReadExactly(buffer, 0, sizeof(long));
                long dbin = BinaryPrimitives.ReadInt64LittleEndian(buffer);
                if (dbin < 0) dbin = -dbin;

                int result = ldate.CompareTo(dbin);
                if (result == 0) return middle;
                if (result > 0)
                    startRec = middle + 1;
                else
                    endRec = middle - 1;
            }
            return ~startRec;
        }

        /// <summary>
        /// Read array of records (from key.from to key.to)
        /// </summary>
        /// <param name="from">from key</param>
        /// <param name="to"></param>
        /// <param name="action">action to do</param>
        /// <returns>count of records processed</returns>
        /// <remarks>Counts each matching fixed header, including one whose payload ends prematurely. User exceptions propagate.</remarks>
        public long ForEach(DateTime from, DateTime to, Action<KeyValuePair<DateTime, TRecord>>? action = null)
        {
            to = to.ToUniversalTime();
            lock (this.syncRoot)
            {
            long ret = 0;
            long count = this.main.Length / TimeLog<TRecord>.RecordSize;
            long start = BinarySearch(from, this.main, 0, count);
            if (start < 0) start = ~start;
            if (start < count)
            { // found read record
                this.main.Seek(start * TimeLog<TRecord>.RecordSize, SeekOrigin.Begin);

                byte[] buffer = new byte[TimeLog<TRecord>.RecordSize];

                do
                {
                    try { this.main.ReadExactly(buffer, 0, TimeLog<TRecord>.RecordSize); }
                    catch (EndOfStreamException) { break; }
                    long dbin = BinaryPrimitives.ReadInt64LittleEndian(buffer);
                    DateTime time = DateTime.FromBinary(dbin >= 0 ? dbin : -dbin);
                    if (time > to) break;
                    ret++;
                    if (action != null)
                    {
                        byte[] data;
                        try { data = this.ReadRecordData(buffer, dbin); }
                        catch (EndOfStreamException) { break; }
                        action(new KeyValuePair<DateTime, TRecord>(time, this.FromBytes(data)));
                    }
                } while (++start < count);
            }
            return ret;
            }
        }

        /// <summary>
        /// Read array of records (from key.from to key.to)
        /// </summary>
        /// <param name="from">from key</param>
        /// <param name="to"></param>
        /// <param name="predicate">comparer to use</param>
        /// <returns></returns>
        /// <remarks>Returns fully decoded records before physical EOF. Decoder and predicate exceptions propagate unchanged.</remarks>
        public KeyValuePair<DateTime, TRecord>[] FindAll(DateTime from, DateTime to, Predicate<KeyValuePair<DateTime, TRecord>>? predicate = null)
        {
            lock (this.syncRoot)
            {
            List<KeyValuePair<DateTime, TRecord>> ret = [];
            this.ForEach(from, to, r => { if (predicate == null || predicate(r)) ret.Add(r); });
            return ret.ToArray();
            }
        }

        /// <summary>
        /// Count records
        /// </summary>
        /// <param name="from">from key</param>
        /// <param name="to">to key</param>
        /// <param name="predicate">comparer to use</param>
        /// <returns>records found</returns>
        /// <remarks>Without a predicate counts headers without reading payloads; with a predicate counts fully decoded matches. User exceptions propagate.</remarks>
        public long CountAll(DateTime from, DateTime to, Predicate<KeyValuePair<DateTime, TRecord>>? predicate = null)
        {
            lock (this.syncRoot)
            {
            if (predicate == null)
                return this.ForEach(from, to);

            long ret = 0;
            this.ForEach(from, to, r => { if (predicate(r)) ret++; });
            return ret;
            }
        }

        /// <summary>
        /// Check if record exists
        /// </summary>
        /// <param name="from">from key</param>
        /// <param name="to">to key</param>
        /// <param name="predicate">comparer to use</param>
        /// <returns>true if exists</returns>
        /// <remarks>Without a predicate inspects headers only; with a predicate stops at physical payload EOF. User exceptions propagate.</remarks>
        public bool Exists(DateTime from, DateTime to, Predicate<KeyValuePair<DateTime, TRecord>>? predicate = null)
        {
            to = to.ToUniversalTime();
            lock (this.syncRoot)
            {
            long count = this.main.Length / TimeLog<TRecord>.RecordSize; 
            long start = BinarySearch(from, this.main, 0, count);
            if (start < 0) start = ~start;
            if (start < count)
            { // found read record
                this.main.Seek(start * TimeLog<TRecord>.RecordSize, SeekOrigin.Begin);

                byte[] buffer = new byte[TimeLog<TRecord>.RecordSize];

                do
                {
                    try { this.main.ReadExactly(buffer, 0, TimeLog<TRecord>.RecordSize); }
                    catch (EndOfStreamException) { break; }
                    long dbin = BinaryPrimitives.ReadInt64LittleEndian(buffer);
                    DateTime time = DateTime.FromBinary(dbin >= 0 ? dbin : -dbin);
                    if (time > to) break;
                    if (predicate == null) return true;

                    byte[] data;
                    try { data = this.ReadRecordData(buffer, dbin); }
                    catch (EndOfStreamException) { break; }
                    if (predicate(new KeyValuePair<DateTime, TRecord>(time, this.FromBytes(data))))
                        return true;
                } while (++start < count);
            }
            return false;
            }
        }

        /// <summary>
        /// Dispose client
        /// </summary>
        public void Dispose()
        {
            this.main.Close();
            this.ext.Close();
        }
    }
}
