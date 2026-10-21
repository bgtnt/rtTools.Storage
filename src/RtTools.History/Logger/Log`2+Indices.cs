using System;
using System.Collections.Generic;
using RtTools;
using System.IO;
using System.Threading;
using System.Diagnostics;

namespace RtTools.Logger
{
    public partial class Log<TRecord, TKey>
    {
        internal const int LockTimeoutMilliseconds = 1000 * 5;

        #region static methods
        /// <summary>
        /// Read range into memory array (prepare to sort)
        /// </summary>
        /// <param name="main">main stream</param>
        /// <param name="range"></param>
        /// <param name="recs"></param>
        /// <param name="startIndex"></param>
        /// <returns>IndexedRange data record for block</returns>
        private void readRange(Stream main, Interval<long> range, LogRecord<TKey>[] recs, int startIndex = 0)
        {
            ArgumentNullException.ThrowIfNull(recs);

            int count = (int)range.Count;

            if (startIndex < 0 || (recs.Length - startIndex) < count)
                throw new ArgumentOutOfRangeException(nameof(startIndex));

            // read all records in range
            main.Seek(range.Start * this.RecordSize, SeekOrigin.Begin);
            int endIndex = startIndex + count;
            while (startIndex < endIndex)
                recs[startIndex++] = new LogRecord<TKey>(this.RecordSize, main);
        }

        private void AddIndexRangesForSortedRecords(LogRecord<TKey>[] records, long startRecord)
        {
            int indexBlockRecordCount = GetIndexBlockRecordCount(records.Length);
            List<int>? bucketEnds = this.TryGetBucketRangeEnds(records, indexBlockRecordCount);
            int plannedRange = 0;
            for (int start = 0; start < records.Length;)
            {
                int end = bucketEnds == null
                    ? start + Math.Min(indexBlockRecordCount, records.Length - start)
                    : bucketEnds[plannedRange++];
                int deletedCount = 0;
                for (int i = start; i < end; i++)
                    if (records[i].IsDeleted)
                        deletedCount++;

                IndexedRange<TKey> range = new()
                {
                    Start = startRecord + start,
                    End = startRecord + end,
                    StartKey = CopyKey(records[start].Key),
                    EndKey = CopyKey(records[end - 1].Key),
                    DeletionState = new RangeDeletionState(deletedCount),
                };

                this.IndexRanges.Add(range);
                range.Write(this.indexStreamW, flush: false);
                start = end;
            }

            this.indexStreamW.Flush();
        }

        // An optional projection affects only range granularity. All persisted keys
        // and all query comparisons retain the original full-key format and order.
        private bool bucketIndexHintDisabled;

        private List<int>? TryGetBucketRangeEnds(LogRecord<TKey>[] records, int indexBlockRecordCount)
        {
            ILogKeyProjection<TKey>? projection = this.KeyProjection;
            if (projection == null || this.bucketIndexHintDisabled || records.Length == 0)
                return null;

            try
            {
                List<int> ends = new(1 + ((records.Length - 1) / indexBlockRecordCount));
                uint previousBucket = projection.GetBucketId(records[0].Key);
                for (int i = 1; i < records.Length; i++)
                {
                    uint bucket = projection.GetBucketId(records[i].Key);
                    if (bucket < previousBucket)
                        throw new InvalidOperationException("Bucket order must follow the full log key order.");
                    if ((i % indexBlockRecordCount) == 0 || bucket != previousBucket)
                        ends.Add(i);
                    previousBucket = bucket;
                }
                ends.Add(records.Length);
                return ends;
            }
            catch (Exception error)
            {
                // Plan every boundary before writing any index ranges. A failed
                // hint cannot leave a partially applied projected index plan.
                this.bucketIndexHintDisabled = true;
                Trace.TraceError("Log bucket range hint disabled; using ordinary index ranges: {0}", error);
                return null;
            }
        }

        private static IndexedRange<TKey>[] LoadIndexRanges(string indexFileName)
        {
            // open index file
            using (FileStream fs = new(indexFileName, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read, 0x1000 * IndexedRange<TKey>.Size))
            {
                long length = fs.Length;
                if ((length % IndexedRange<TKey>.Size) != 0)
                    throw new InvalidDataException("invalid index file size: " + length);
                long rangeCount = length / IndexedRange<TKey>.Size;
                if (rangeCount > int.MaxValue)
                    throw new InvalidDataException("too many index ranges: " + rangeCount);

                // load index ranges
                IndexedRange<TKey>[] ret = new IndexedRange<TKey>[(int)rangeCount];
                int index = 0;
                if (length > 0 && length <= MaxBulkIndexLoadBytes)
                {
                    byte[] buffer = GC.AllocateUninitializedArray<byte>((int)length);
                    fs.ReadExactly(buffer, 0, buffer.Length);
                    for (int offset = 0; offset < buffer.Length; offset += IndexedRange<TKey>.Size)
                        ret[index++] = new IndexedRange<TKey>(buffer, offset);
                    return ret;
                }

                byte[] ibuffer = new byte[IndexedRange<TKey>.Size];
                while (--rangeCount >= 0)
                    ret[index++] = new IndexedRange<TKey>(fs, ibuffer);
                return ret;
            }
        }

        private static void SortBufferedRecords(LogRecord<TKey>[] records)
        {
            if (records.Length < 2)
                return;

            List<(int Start, int Count)>? runs = null;
            int runStart = 0;
            for (int i = 1; i < records.Length; i++)
            {
                if (records[i - 1].CompareTo(records[i]) <= 0)
                    continue;

                runs ??= new List<(int Start, int Count)> { (0, i) };
                if (runs.Count > 64)
                {
                    Array.Sort(records);
                    return;
                }

                runStart = i;
                runs.Add((runStart, 0));
            }

            if (runs == null)
                return;

            runs[^1] = (runs[^1].Start, records.Length - runs[^1].Start);
            for (int i = 0; i < runs.Count - 1; i++)
                runs[i] = (runs[i].Start, runs[i + 1].Start - runs[i].Start);

            LogRecord<TKey>[] source = records;
            LogRecord<TKey>[] target = new LogRecord<TKey>[records.Length];
            List<(int Start, int Count)> current = runs;
            List<(int Start, int Count)> next = new(current.Count);

            while (current.Count > 1)
            {
                next.Clear();
                int targetStart = 0;
                for (int i = 0; i < current.Count; i += 2)
                {
                    (int Start, int Count) left = current[i];
                    if (i + 1 >= current.Count)
                    {
                        Array.Copy(source, left.Start, target, targetStart, left.Count);
                        next.Add((targetStart, left.Count));
                        targetStart += left.Count;
                        continue;
                    }

                    (int Start, int Count) right = current[i + 1];
                    RtTools.Statics.MergeSorted(source, left.Start, left.Count, source, right.Start, right.Count, target, targetStart);
                    int mergedCount = left.Count + right.Count;
                    next.Add((targetStart, mergedCount));
                    targetStart += mergedCount;
                }

                (source, target) = (target, source);
                (current, next) = (next, current);
            }

            if (!ReferenceEquals(source, records))
                Array.Copy(source, 0, records, 0, records.Length);
        }

        #endregion

        /// <summary>
        /// Index locker 
        /// - Read locked on read (using indices)
        /// - Write locked on write - reindexing
        /// </summary>
        private readonly ReaderWriterLockSlim indexLock = new();
        private const long MaxBulkIndexLoadBytes = 64L * 1024 * 1024;
        private const long MaxBulkBufferLoadBytes = 64L * 1024 * 1024;

        internal void EnterIndexReadLock()
        {
            this.ThrowIfUnavailable();
#if DEBUG
            if (!this.indexLock.TryEnterReadLock(LockTimeoutMilliseconds))
                throw new TimeoutException("Timed out while waiting for the log index read lock.");
#else
            this.indexLock.EnterReadLock();
#endif
            try { this.ThrowIfUnavailable(); }
            catch { this.indexLock.ExitReadLock(); throw; }
        }

        internal void ExitIndexReadLock() => this.indexLock.ExitReadLock();

        internal void EnterIndexWriteLock()
        {
#if DEBUG
            if (!this.indexLock.TryEnterWriteLock(LockTimeoutMilliseconds))
                throw new TimeoutException("Timed out while waiting for the log index write lock.");
#else
            this.indexLock.EnterWriteLock();
#endif
        }

        internal void ExitIndexWriteLock() => this.indexLock.ExitWriteLock();

        /// <summary>
        /// indexed ranges list (used on FindAll)
        /// </summary>
        internal readonly ComparableSortedArrayCollection<IndexedRange<TKey>> IndexRanges;

        /// <summary>
        /// Current memindex range
        /// </summary>
        internal IndexedRange<TKey> memBufferRange;
        /// <summary>
        /// memindex (records in memory)
        /// </summary>
        internal LogRecord<TKey>[] memBuffer = [];

        /// <summary>
        /// Caller holds bufferLock and indexLock in write mode.
        /// 1. is added into pending records (indexed in memory)
        /// 2. On StoreUpdates(called into Driver.OnInternalTimer) 
        ///     2.1. pending records are stored into Buffer file 
        ///     2.2. pending records are merged into MemIndex, available for index search
        ///     2.3. pending array is cleared
        /// 3. When Buffer file reaches size, it's content is moved into main file
        /// </summary>
        /// <param name="records">records to copy</param>
        /// <param name="recordsRange">range to data</param>
        private bool AddToMemBufferAndCopyToMain(LogRecord<TKey>[] records, IndexedRange<TKey> recordsRange)
        {
            // Caller excludes readers throughout publication, including a failed write.
            this.memBufferRange.MergeWith(recordsRange);
            this.memBuffer = RtTools.Statics.MergeSorted(this.memBuffer, records);
            foreach (LogRecord<TKey> record in records)
                if (record.IsDeleted)
                    this.memoryDeletedCount++;

            if (this.memBuffer.Length <= this.MaxBufferSize)
                return false;

            // exeeded max buffer size
            // copy BUFFER Into main data file
            //Trace.WriteLine(string.Format("log: store buffer/memindex {0}", this.memBuffer.Length));
            // copy all memBuffered records into main data file
            long startRec = this.memBufferRange.Start;
            foreach (LogRecord<TKey> rec in this.memBuffer)
                rec.writeRecordHeader(this.mainStreamW);
            this.mainStreamW.Flush();

            // add new index records
            this.AddIndexRangesForSortedRecords(this.memBuffer, startRec);
            // remove memory index (already written to disk)
            this.memBuffer = [];
            this.memoryDeletedCount = 0;
            this.memBufferRange = new IndexedRange<TKey>()
            {
                StartKey = (TKey)EmptyKey.MaxValue,
                EndKey = (TKey)EmptyKey.MinValue,
            };

            // returns true to say buffer should be cleared
            return true;
        }

        /// <summary>
        /// read all buffered records into memory
        /// </summary>
        private void loadBufferIntoMem()
        { // load buffer into memory index
            long fileLength = GetTruncatedLength(this.BufferFileName);
            EnsureFileExists(this.BufferFileName);
            int recordCount = checked((int)(fileLength / this.RecordSize));
            this.memBuffer = new LogRecord<TKey>[recordCount];

            if (recordCount > 0)
            {
                if (fileLength <= MaxBulkBufferLoadBytes)
                {
                    byte[] buffer = GC.AllocateUninitializedArray<byte>((int)fileLength);
                    using FileStream buffered = new(this.BufferFileName, FileMode.Open, FileAccess.Read, FileShare.Read, this.RecordSize * 0x1000, FileOptions.SequentialScan);
                    buffered.ReadExactly(buffer, 0, buffer.Length);
                    for (int i = 0, offset = 0; i < recordCount; i++, offset += this.RecordSize)
                        this.memBuffer[i] = new LogRecord<TKey>(this.RecordSize, buffer, offset);
                }
                else
                {
                    using FileStream buffered = new(this.BufferFileName, FileMode.Open, FileAccess.Read, FileShare.Read, this.RecordSize * 0x1000);
                    for (int i = 0; i < recordCount; i++)
                        this.memBuffer[i] = new LogRecord<TKey>(this.RecordSize, buffered);
                }
            }

            SortBufferedRecords(this.memBuffer); // sort in memory
            this.memoryDeletedCount = 0;
            foreach (LogRecord<TKey> record in this.memBuffer)
                if (record.IsDeleted)
                    this.memoryDeletedCount++;
            TKey minKey = recordCount == 0 ? (TKey)EmptyKey.MaxValue : CopyKey(this.memBuffer[0].Key);
            TKey maxKey = recordCount == 0 ? (TKey)EmptyKey.MinValue : CopyKey(this.memBuffer[^1].Key);
            long startRec = this.mainStreamW.Length / this.RecordSize;
            this.memBufferRange = new IndexedRange<TKey>()
            {
                Start = startRec,
                End = startRec + recordCount,
                StartKey = minKey,
                EndKey = maxKey
            };
            //Trace.WriteLine(string.Format("log: load memory index: {0}", this.memBuffer.Length));
        }

        private long GetTruncatedLength(string fileName)
        {
            long length = File.Exists(fileName) ? new FileInfo(fileName).Length : 0;
            long truncated = length - (length % this.RecordSize);
            if (truncated != length)
            {
                using FileStream stream = new(fileName, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, this.RecordSize * 0x1000);
                stream.SetLength(truncated);
            }

            return truncated;
        }

        private static void EnsureFileExists(string fileName)
        {
            if (!File.Exists(fileName))
                using (File.Create(fileName)) { }
        }

        /// <summary>
        /// initial reindexing
        /// </summary>
        internal void initReindex()
        {
            long count = GetTruncatedLength(this.MainFileName) / this.RecordSize;
            List<Interval<long>> ranges = [new Int64Interval(0, count)];
            //this.IndexRanges.ForEach(r => new Int64Interval(r.Start, r.End).RemoveFrom(ranges));
            foreach (IndexedRange<TKey> r in this.IndexRanges) new Int64Interval(r.Start, r.End).RemoveFrom(ranges);
            bool hasUnindexedRange = false;
            foreach (Interval<long> range in ranges)
            {
                if (range.Count > 0)
                {
                    hasUnindexedRange = true;
                    break;
                }
            }

            if (!hasUnindexedRange)
                return;

            EnsureFileExists(this.MainFileName);
            using (FileStream main = new(this.MainFileName, FileMode.Open, FileAccess.ReadWrite, FileShare.None, this.RecordSize * 0x1000))
            {
                foreach (Interval<long> range in ranges)
                { // inindexed parts found
                    if (range.Count > 0)
                    {
                        LogRecord<TKey>[] recs = new LogRecord<TKey>[range.Count];
                        this.readRange(main, range, recs);
                        Array.Sort(recs);
                        main.Seek(range.Start * this.RecordSize, SeekOrigin.Begin);
                        foreach (LogRecord<TKey> rec in recs) 
                            rec.writeRecordHeader(main);

                        // add new index records
                        this.AddIndexRangesForSortedRecords(recs, range.Start);
                        //Trace.WriteLine(string.Format("log: reindex: {0} - {1}", ir.Start, ir.End));
                    }
                }
            }
        }
    }
}
