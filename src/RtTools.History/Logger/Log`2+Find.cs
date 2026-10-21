using System;
using System.Collections.Generic;
using RtTools;
using System.IO;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Diagnostics;

namespace RtTools.Logger
{
    public partial class Log<TRecord, TKey>
    {
        private const int DirectMemoryFindAllThreshold = 512;

        #region internal find structure

        internal sealed class FindStructure
        {
            private const int IncrementalMergeRecordLimit = 4096;

            private readonly List<TRecord> list = [];
            private readonly List<TRecord[]> runs = [];
            private int runRecordCount;
            private bool sawRunBoundary;
            private TRecord[] incrementalResult = [];

            public Stream? Extended { get; set; }
            public Func<Stream>? GetExtended { get; set; }

            private Stream GetExtendedForRead() => this.Extended ??=
                (this.GetExtended ?? throw new InvalidOperationException("Extended stream provider is not configured."))();

#pragma warning disable IDE0060 // Remove unused parameter
            public void OnNextIndex(IndexedRange<TKey> indexRange, bool memIndex)
#pragma warning restore IDE0060 // Remove unused parameter
            {
                this.sawRunBoundary = true;
                this.CompleteRun();
            }

            public TRecord[] ToSortedArray()
            {
                if (!this.sawRunBoundary)
                    return this.list.ToArray();

                this.CompleteRun();
                return this.MergeRuns();
            }

            private void CompleteRun()
            {
                if (this.list.Count == 0)
                    return;

                TRecord[] run = this.list.ToArray();
                if (this.runs.Count == 0 && this.runRecordCount + run.Length <= IncrementalMergeRecordLimit)
                {
                    this.incrementalResult = RtTools.Statics.MergeSorted(this.incrementalResult, run, Log<TRecord, TKey>.Comparer);
                    this.runRecordCount += run.Length;
                    this.list.Clear();
                    return;
                }

                if (this.incrementalResult.Length > 0)
                {
                    this.runs.Add(this.incrementalResult);
                    this.incrementalResult = [];
                }

                this.runs.Add(run);
                this.runRecordCount += run.Length;
                this.list.Clear();
            }

            private TRecord[] MergeRuns()
            {
                if (this.runs.Count == 0)
                    return this.incrementalResult;

                if (this.incrementalResult.Length > 0)
                {
                    this.runs.Add(this.incrementalResult);
                    this.incrementalResult = [];
                }

                if (this.runs.Count == 0)
                    return [];
                if (this.runs.Count == 1)
                    return this.runs[0];

                if (this.runs.Count <= 1)
                {
                    TRecord[] result = [];
                    for (int i = 0; i < this.runs.Count; i++)
                        result = RtTools.Statics.MergeSorted(result, this.runs[i], Log<TRecord, TKey>.Comparer);
                    return result;
                }

                TRecord[] ret = new TRecord[this.runRecordCount];
                int offset = 0;
                for (int i = 0; i < this.runs.Count; i++)
                {
                    TRecord[] run = this.runs[i];
                    Array.Copy(run, 0, ret, offset, run.Length);
                    offset += run.Length;
                }

                Array.Sort(ret, Log<TRecord, TKey>.Comparer);
                return ret;
            }

            public bool OnRecordFound(LogRecord<TKey> record)
            {
                StoreType options = record.Options;
                byte[] data = LogRecord<TKey>.HasExtendedData(options)
                    ? record.readExtendedRecordData(options, this.GetExtendedForRead())
                    : record.readFixedRecordData(options);
                this.list.Add(new TRecord() { Key = record.Key, Data = data });
                return true;
            }

            public bool OnNotDeletedRecordFound(LogRecord<TKey> record)
            {
                if (record.IsDeleted) return false;
                StoreType options = record.Options;
                byte[] data = LogRecord<TKey>.HasExtendedData(options)
                    ? record.readExtendedRecordData(options, this.GetExtendedForRead())
                    : record.readFixedRecordData(options);
                this.list.Add(new TRecord() { Key = record.Key, Data = data });
                return true;
            }
        }

        #endregion

        internal delegate void OnMemBuffer();

        internal delegate void OnNextIndex(IndexedRange<TKey> indexRange, bool memIndex);

        internal delegate bool OnRecordFound(LogRecord<TKey> record);

        internal static int LowerBound(LogRecord<TKey>[] records, TKey key)
        {
            int low = 0;
            int high = records.Length;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (records[middle].Key.CompareTo(key) < 0)
                    low = middle + 1;
                else
                    high = middle;
            }

            return low;
        }

        internal long ScanMemoryForward(TKey from, TKey to, OnRecordFound recordCallback, Predicate<TKey>? predicate = null, int max = 0)
        {
            long ret = 0;
            IndexedRange<TKey> range = this.memBufferRange;
            if (!range.Intersects(from, to))
                return 0;

            LogRecord<TKey>[] memIndex = this.memBuffer;
            int start = LowerBound(memIndex, from);
            while (start < memIndex.Length)
            {
                LogRecord<TKey> record = memIndex[start++];
                TKey key = record.Key;
                if (key.CompareTo(to) >= 0)
                    break;

                if (predicate == null || predicate(key))
                    if (recordCallback(record))
                    {
                        ret++;
                        if (max > 0 && ret >= max)
                            return ret;
                    }
            }

            return ret;
        }

        private TRecord[] FindAllMemory(TKey from, TKey to, Predicate<TKey>? predicate, bool skipDeletedCheck, Func<Stream> getExtended)
        {
            IndexedRange<TKey> range = this.memBufferRange;
            if (!range.Intersects(from, to))
                return [];

            LogRecord<TKey>[] memIndex = this.memBuffer;
            int start = LowerBound(memIndex, from);
            Stream? extended = null;
            byte[] ReadData(LogRecord<TKey> record)
            {
                StoreType options = record.Options;
                return LogRecord<TKey>.HasExtendedData(options)
                    ? record.readExtendedRecordData(options, extended ??= getExtended())
                    : record.readFixedRecordData(options);
            }

            if (predicate == null)
            {
                int end = LowerBound(memIndex, to);
                int count = Math.Max(0, end - start);
                if (skipDeletedCheck)
                {
                    if (count == 0)
                        return [];

                    TRecord[] records = new TRecord[count];
                    for (int i = 0; i < records.Length; i++)
                    {
                        LogRecord<TKey> record = memIndex[start + i];
                        records[i] = new TRecord() { Key = record.Key, Data = ReadData(record) };
                    }

                    return records;
                }

                List<TRecord> filtered = count > 0 ? new List<TRecord>(count) : [];
                for (int i = 0; i < count; i++)
                {
                    LogRecord<TKey> record = memIndex[start + i];
                    if (!record.IsDeleted)
                        filtered.Add(new TRecord() { Key = record.Key, Data = ReadData(record) });
                }

                return filtered.ToArray();
            }

            List<TRecord> ret = [];
            while (start < memIndex.Length)
            {
                LogRecord<TKey> record = memIndex[start++];
                TKey key = record.Key;
                if (key.CompareTo(to) >= 0)
                    break;

                if (!skipDeletedCheck && record.IsDeleted)
                    continue;
                if (!predicate(key))
                    continue;

                ret.Add(new TRecord() { Key = key, Data = ReadData(record) });
            }

            return ret.ToArray();
        }

        /// <summary>
        /// Scan forward records from key.from to key.to and execute command
        /// </summary>
        /// <param name="main">main stream</param>
        /// <param name="from"></param>
        /// <param name="to"></param>
        /// <param name="recordCallback">callback to call on record found</param>
        /// <param name="indexCallback">callback to find next index</param>
        /// <param name="membuffer">callback to call for mem buffer index</param>
        /// <param name="predicate">records comparer</param>
        /// <param name="max">max returning</param>
        /// <returns>records scanned /found/</returns>
        internal long ScanForward(Stream main, TKey from, TKey to, OnRecordFound recordCallback, OnNextIndex? indexCallback = null, OnMemBuffer? membuffer = null, Predicate<TKey>? predicate = null, int max = 0, OnNextIndex? rangeStartCallback = null)
        {

            long ret = 0;
            LogRecord<TKey>? record = null;
            LogRecord<TKey>? search = null;
            TKey key;

            #region scan stored indices
            int rindex = this.IndexRanges.FindLastLessIndex(new IndexedRange<TKey> { EndKey = from });
            while (++rindex < this.IndexRanges.Count)
            {// scan next indices
                IndexedRange<TKey> ir = this.IndexRanges[rindex];
                if (ir.Intersects(from, to))// key in range
                { // range could contain data
                    search ??= new LogRecord<TKey>(this.RecordSize) { Key = from, compareFlag = sbyte.MinValue };
                    long start = search.BinarySearch(main, ir.Start, ir.End);
                    if (start < 0) start = ~start;
                    if (start < ir.End)
                    { // found read record
                        rangeStartCallback?.Invoke(ir, false);
                        record ??= new LogRecord<TKey>(this.RecordSize);
                        main.Seek(start * this.RecordSize, SeekOrigin.Begin);
                        while (start++ < ir.End && // read up to end
                            record.ReadHeader(main) && // read header
                            (key = record.Key).CompareTo(to) < 0) // record.Key < to
                            if (predicate == null || predicate(key))
                                if (recordCallback(record))
                                {
                                    ret++;
                                    if (max > 0 && ret >= max) return ret;
                                }
                        indexCallback?.Invoke(ir, false);
                    }
                }
            }
            #endregion

            membuffer?.Invoke();

            #region second - search in memory index

            IndexedRange<TKey> mbr = this.memBufferRange;
            if (mbr.Intersects(from, to))// time stamp in range
            { // range could contain data
                rangeStartCallback?.Invoke(mbr, true);
                LogRecord<TKey>[] memIndex = this.memBuffer;
                int start = LowerBound(memIndex, from);
                while (start < memIndex.Length)
                {
                    record = this.memBuffer[start++];
                    if (!((key = record.Key).CompareTo(to) < 0)) break; // !(record.Key < to) - break

                    if (predicate == null || predicate(key))
                        if (recordCallback(record))
                        {
                            ret++;
                            if (max > 0 && ret >= max)
                                return ret;
                        }
                }

                indexCallback?.Invoke(mbr, true);
            }
            #endregion

            return ret;
        }

        /// <summary>
        /// Scan backward records from key.from to key.to and execute command
        /// </summary>
        /// <param name="main">main stream</param>
        /// <param name="from"></param>
        /// <param name="to"></param>
        /// <param name="recordCallback">callback to call on record found</param>
        /// <param name="indexCallback">callback to find next index</param>
        /// <param name="predicate">records comparer</param>
        /// <param name="max">max returning</param>
        /// <returns>records scanned /found/</returns>
        internal long ScanBackward(Stream main, TKey from, TKey to, OnRecordFound recordCallback, OnNextIndex? indexCallback = null, Predicate<TKey>? predicate = null, int max = 0)
        {

            long ret = 0;
            LogRecord<TKey> record; // = new LogRecord<TKey>(this.RecordSize);
            LogRecord<TKey>? search = null;
            TKey key;

            #region first - search in memory index

            IndexedRange<TKey> mbr = this.memBufferRange;
            if (mbr.Intersects(from, to))// timestamp in range
            { // range could contain data
                LogRecord<TKey>[] memIndex = this.memBuffer;
                int end = LowerBound(memIndex, to);
                while (0 <= --end)
                {
                    record = this.memBuffer[end];
                    if (!(from.CompareTo(key = record.Key) <= 0)) break; // if !(from <= record.Key) - break

                    if (key.CompareTo(to) < 0) // record.Key < to
                        if (predicate == null || predicate(key))
                            if (recordCallback(record))
                            {
                                ret++;
                                if (max > 0 && ret >= max)
                                    return ret;
                            }
                }

                indexCallback?.Invoke(mbr, true);
            }
            #endregion

            #region next - scan stored indices

            const int RecordBufferSize = 100;
            LogRecord<TKey>?[]? recBuff = null;

            int stopIndex = this.IndexRanges.FindLastLessIndex(new IndexedRange<TKey> { EndKey = from });
            int rindex = this.IndexRanges.Count;
            while (stopIndex < --rindex)
            { // scan backward indices
                IndexedRange<TKey> ir = this.IndexRanges[rindex];
                if (ir.Intersects(from, to))// key in range
                { // range could contain data
                    search ??= new LogRecord<TKey>(this.RecordSize) { Key = to, compareFlag = sbyte.MaxValue };
                    long end = search.BinarySearch(main, ir.Start, ir.End);
                    if (end < 0) end = ~end;
                    if (ir.Start <= --end)
                    { // found read record

                        // to read more records at once
                        recBuff = (recBuff ?? new LogRecord<TKey>[RecordBufferSize]);
                        long buffStart = long.MaxValue;

                        do
                        { // scan index records - backwards
                            if (end < buffStart)
                            { // buffer is empty
                                buffStart = Math.Max(ir.Start, end - RecordBufferSize + 1);
                                // seek record back
                                main.Seek(buffStart * this.RecordSize, SeekOrigin.Begin);

                                long buffLastIndex = end - buffStart;

                                for (int i = 0; i <= buffLastIndex; i++) // read records
                                    (recBuff[i] ?? (recBuff[i] = new LogRecord<TKey>(this.RecordSize))).ReadHeader(main);

                                record = recBuff[buffLastIndex]!;
                            }
                            else // seek record back
                                record = recBuff[end - buffStart]!;

                            if (from.CompareTo(key = record.Key) <= 0)
                            { // from < record.Key
                                if (key.CompareTo(to) < 0) // record.Key < to
                                    if (predicate == null || predicate(key))
                                        if (recordCallback(record))
                                        {
                                            ret++;
                                            if (max > 0 && ret >= max)
                                                return ret;
                                        }
                            }
                            else break; // stop looking backwards

                        } while (ir.Start <= --end);

                        indexCallback?.Invoke(ir, false);
                    }
                }
            }
            #endregion

            return ret;
        }

        /// <summary>
        /// Create DB client
        /// </summary>
        /// <param name="recBufferSize"></param>
        /// <param name="extBufferSize"></param>
        /// <returns></returns>
        public LogClient<TRecord, TKey> CreateClient(int recBufferSize = 0x1000, int extBufferSize = Log<TRecord, TKey>.DefaultExtendedBufferSize)
        {
            this.ThrowIfUnavailable();
            return new(this, recBufferSize, extBufferSize);
        }

        /// <summary>
        /// Find all record between 2 keys
        /// </summary>
        /// <param name="from"></param>
        /// <param name="to"></param>
        /// <param name="predicate">records key comparer</param>
        /// <param name="deleted">if to include deleted records</param>
        /// <returns></returns>
        public TRecord[] FindAll(TKey from, TKey to, Predicate<TKey>? predicate = null, bool deleted = false)
        {
            // now check if mem index is to be written
            this.EnterIndexReadLock();
            try
            {
                bool memoryOnly = this.IndexRanges.Count == 0;
                using Stream main = memoryOnly
                    ? Stream.Null
                    : this.GetMainStream(bufferSize: 0x1000 * (long)this.RecordSize);
                Stream? ext = null;
                try
                {
                    if (memoryOnly && this.memBuffer.Length >= DirectMemoryFindAllThreshold)
                        return this.FindAllMemory(from, to, predicate, this.CanSkipDeletedCheck(deleted), () => ext ??= this.GetExtendedStream(bufferSize: Log<TRecord, TKey>.DefaultExtendedBufferSize));

                    FindStructure fs = new() { GetExtended = () => ext ??= this.GetExtendedStream(bufferSize: Log<TRecord, TKey>.DefaultExtendedBufferSize) };
                    if (this.CanSkipDeletedCheck(deleted))
                    {
                        if (memoryOnly)
                            this.ScanMemoryForward(from, to, fs.OnRecordFound, predicate: predicate);
                        else
                            this.ScanForward(main, from, to, fs.OnRecordFound, indexCallback: fs.OnNextIndex, predicate: predicate);
                    }
                    else
                    {
                        if (memoryOnly)
                            this.ScanMemoryForward(from, to, fs.OnNotDeletedRecordFound, predicate: predicate);
                        else
                            this.ScanForward(main, from, to, fs.OnNotDeletedRecordFound, indexCallback: fs.OnNextIndex, predicate: predicate);
                    }
                    return fs.ToSortedArray();
                }
                finally
                {
                    ext?.Dispose();
                }
            }
            finally { this.ExitIndexReadLock(); }
        }

        internal static bool countNotDeletedRecords(LogRecord<TKey> record) => !record.IsDeleted;
        internal static bool countAllRecords(LogRecord<TKey> record) => true;

        internal bool CanSkipDeletedCheck(bool deleted) =>
            deleted || (this.IndexRanges.Count == 0 && this.memoryDeletedCount == 0);

        internal long MainRecordCount => this.mainStreamW.Length / this.RecordSize;

        /// <summary>
        /// Count all records in range
        /// </summary>
        /// <param name="from"></param>
        /// <param name="to"></param>
        /// <param name="predicate"></param>
        /// <param name="deleted"></param>
        /// <returns></returns>
        public long CountAll(TKey from, TKey to, Predicate<TKey>? predicate = null, bool deleted = false)
        {
            // now check if mem index is to be written
            this.EnterIndexReadLock();
            try
            {
                if (this.TryFastCountAllWithoutMain(from, to, predicate, deleted, out long count))
                    return count;
                using Stream main = this.IndexRanges.Count == 0 ? Stream.Null : this.GetMainStream();
                return predicate == null
                    ? this.CountRanges(main, from, to, deleted)
                    : this.ScanForward(main, from, to, deleted ? countAllRecords : countNotDeletedRecords, predicate: predicate);
            }
            finally { this.ExitIndexReadLock(); }
        }

        /// <summary>
        /// Get if record exists
        /// </summary>
        /// <param name="from"></param>
        /// <param name="to"></param>
        /// <param name="predicate"></param>
        /// <param name="deleted"></param>
        /// <returns>true if exists</returns>
        public bool Exists(TKey from, TKey to, Predicate<TKey>? predicate = null, bool deleted = false)
        {
            // now check if mem index is to be written
            this.EnterIndexReadLock();
            try
            {
                if (this.TryFastExistsWithoutMain(from, to, predicate, deleted, out bool found))
                    return found;
                using Stream main = this.IndexRanges.Count == 0 ? Stream.Null : this.GetMainStream();
                return predicate == null
                    ? this.ExistsInRanges(main, from, to, deleted)
                    : this.ScanBackward(main, from, to, deleted ? countAllRecords : countNotDeletedRecords, predicate: predicate, max: 1) > 0;
            }
            finally { this.ExitIndexReadLock(); }
        }

        /// <summary>
        /// Get if record exists (scan backwards)
        /// </summary>
        /// <param name="from"></param>
        /// <param name="to"></param>
        /// <param name="predicate"></param>
        /// <param name="deleted"></param>
        /// <returns>true if exists</returns>
        public bool ExistsBackward(TKey from, TKey to, Predicate<TKey>? predicate = null, bool deleted = false)
        {
            this.EnterIndexReadLock();
            try
            {
                using Stream main = this.GetMainStream(bufferSize: 0x1000 * (long)this.RecordSize);
                return this.CanSkipDeletedCheck(deleted)
                    ? this.ScanBackward(main, from, to, countAllRecords, predicate: predicate, max: 1) > 0
                    : this.ScanBackward(main, from, to, countNotDeletedRecords, predicate: predicate, max: 1) > 0;
            }
            finally { this.ExitIndexReadLock(); }
        }

        /// <summary>
        /// Delete all records in range
        /// </summary>
        /// <param name="from"></param>
        /// <param name="to"></param>
        /// <param name="predicate"></param>
        /// <returns></returns>
        public long DeleteAll(TKey from, TKey to, Predicate<TKey>? predicate = null)
        {
            this.RejectCallbackReentry();
            this.ThrowIfUnavailable();
            long ret = 0;

            if (!this.DeleteSupported)
                throw new NotSupportedException("Logger does not support delete");

            lock (this.bufferLock)
            {
                this.ThrowIfUnavailable();
                // do not allow any readers/writers
                this.EnterIndexWriteLock();
                try
                {
                    RangeDeletionState? rangeState = null;
                    bool inBuffer = false;
                    int deletedInBuffer = 0;

                    ret = this.ScanForward(this.mainStreamW, from, to,
                        record =>
                        {
                            if (record.IsDeleted) return false;

                            this.MarkDeletedStateSome();
                            record.IsDeleted = true;
                            if (inBuffer)
                            { // record is in buffer
                                deletedInBuffer++;
                                this.memoryDeletedCount++;
                            }
                            else
                            { // record is in main file - should be marked as deleted
                                rangeState?.RecordDeletion();
                                long pos = this.mainStreamW.Position;
                                // go to options position
                                this.mainStreamW.Seek(pos - this.RecordSize + LogRecord<TKey>.KeySize, SeekOrigin.Begin);
                                this.mainStreamW.WriteByte((byte)record.Options);
                                // restore position
                                this.mainStreamW.Seek(pos, SeekOrigin.Begin);
                            }
                            return true;
                        },
                        membuffer: () => inBuffer = true,
                        predicate: predicate,
                        rangeStartCallback: (range, memory) => rangeState = memory ? null : range.DeletionState);

                    this.mainStreamW.Seek(0, SeekOrigin.End); // go to last position

                    if (deletedInBuffer > 0)
                    { // clear buffer file
                        this.bufferStreamW.Close();
                        string temp = Path.ChangeExtension(this.BufferFileName, ".temp");
                        using (FileStream copy = new(temp, FileMode.Create, FileAccess.Write, FileShare.None, this.RecordSize * 0x1000))
                        {
                            copy.SetLength(this.memBuffer.Length * (long)this.RecordSize);
                            foreach (LogRecord<TKey> rec in this.memBuffer)
                                rec.writeRecordHeader(copy);
                        }
                        File.Delete(this.BufferFileName);
                        File.Move(temp, this.BufferFileName);
                        // Reopen only after success. On failure this instance is fail-stop;
                        // a second open failure must not hide the original write error.
                        this.bufferStreamW = new FileStream(this.BufferFileName, FileMode.Append, FileAccess.Write, FileShare.None, this.RecordSize * 0x1000);
                    }
                    this.mainStreamW.Flush();
                    if (ret != 0) this.ReaderGeneration++;
                }
                catch (Exception error) { this.RememberWriteError(error); throw; }
                finally { this.ExitIndexWriteLock(); }
            }

            return ret;
        }
    }
}
