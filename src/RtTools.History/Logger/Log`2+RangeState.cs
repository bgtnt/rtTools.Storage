using System;
using System.Buffers;
using System.IO;

namespace RtTools.Logger
{
    public partial class Log<TRecord, TKey>
    {
        // Caller holds indexLock for reading. Summary writes are shared between
        // readers; tombstone changes and newly published ranges require its write lock.
        private long GetDeletedCount(Stream main, IndexedRange<TKey> range)
        {
            RangeDeletionState state = range.DeletionState
                ?? throw new InvalidOperationException("A stored range has no deletion summary.");
            long known = state.DeletedCount;
            if (known >= 0) return known;
            lock (state)
            {
                known = state.DeletedCount;
                if (known >= 0) return known;
                long count = this.CountDeletedFlags(main, range.Start, range.End);
                state.SetKnown(count); // Only a complete successful read establishes a summary.
                return count;
            }
        }

        private long CountDeletedFlags(Stream main, long start, long end)
        {
            if (start >= end) return 0;
            int blockRecords = Math.Max(1, 65536 / this.RecordSize);
            byte[] bytes = ArrayPool<byte>.Shared.Rent(checked(blockRecords * this.RecordSize));
            long count = 0;
            try
            {
                main.Seek(checked(start * this.RecordSize), SeekOrigin.Begin);
                while (start < end)
                {
                    int records = (int)Math.Min(blockRecords, end - start);
                    main.ReadExactly(bytes, 0, checked(records * this.RecordSize));
                    for (int i = 0; i < records; i++)
                        if ((bytes[i * this.RecordSize + LogRecord<TKey>.KeySize] & (byte)StoreType.Deleted) != 0) count++;
                    start += records;
                }
                return count;
            }
            finally { ArrayPool<byte>.Shared.Return(bytes); }
        }

        private long CountMemory(TKey from, TKey to, bool deleted)
        {
            if (!this.memBufferRange.Intersects(from, to)) return 0;
            int start = LowerBound(this.memBuffer, from);
            int end = LowerBound(this.memBuffer, to);
            if (deleted || this.memoryDeletedCount == 0) return Math.Max(0, end - start);
            long count = 0;
            for (int i = start; i < end; i++)
                if (!this.memBuffer[i].IsDeleted) count++;
            return count;
        }

        private bool ExistsInMemory(TKey from, TKey to, bool deleted)
        {
            if (!this.memBufferRange.Intersects(from, to)) return false;
            int start = LowerBound(this.memBuffer, from);
            int end = LowerBound(this.memBuffer, to);
            if (deleted || this.memoryDeletedCount == 0) return end > start;
            for (int i = end - 1; i >= start; i--)
                if (!this.memBuffer[i].IsDeleted) return true;
            return false;
        }

        private static bool Covers(IndexedRange<TKey> range, TKey from, TKey to) =>
            from.CompareTo(range.StartKey) <= 0 && range.EndKey.CompareTo(to) < 0;

        private static long LowerBound(Stream main, LogRecord<TKey> search, IndexedRange<TKey> range)
        {
            long position = search.BinarySearch(main, range.Start, range.End);
            return position < 0 ? ~position : position;
        }

        internal bool TryFastCountAllWithoutMain(TKey from, TKey to, Predicate<TKey>? predicate, bool deleted, out long count)
        {
            count = 0;
            if (from.CompareTo(to) >= 0) return true;
            if (predicate != null) return false;
            int index = this.IndexRanges.FindLastLessIndex(new IndexedRange<TKey> { EndKey = from });
            while (++index < this.IndexRanges.Count)
            {
                IndexedRange<TKey> range = this.IndexRanges[index];
                // Ranges are ordered by END key. Their start keys need not be monotone.
                if (!range.Intersects(from, to)) continue;
                long tombstones = deleted ? 0 : range.DeletionState?.DeletedCount ?? -1;
                if (tombstones == range.Count) continue;
                if (tombstones < 0 || !Covers(range, from, to)) return false;
                count += range.Count - tombstones;
            }
            count += this.CountMemory(from, to, deleted);
            return true;
        }

        internal long CountRanges(Stream main, TKey from, TKey to, bool deleted)
        {
            if (from.CompareTo(to) >= 0) return 0;
            long count = 0;
            LogRecord<TKey>? lower = null, upper = null;
            int index = this.IndexRanges.FindLastLessIndex(new IndexedRange<TKey> { EndKey = from });
            while (++index < this.IndexRanges.Count)
            {
                IndexedRange<TKey> range = this.IndexRanges[index];
                if (!range.Intersects(from, to)) continue;
                bool whole = Covers(range, from, to);
                long start = range.Start, end = range.End;
                if (!whole)
                {
                    lower ??= new LogRecord<TKey>(this.RecordSize) { Key = from, compareFlag = sbyte.MinValue };
                    upper ??= new LogRecord<TKey>(this.RecordSize) { Key = to, compareFlag = sbyte.MinValue };
                    start = LowerBound(main, lower, range);
                    end = LowerBound(main, upper, range);
                }
                if (start >= end) continue;
                long tombstones = deleted ? 0 : this.GetDeletedCount(main, range);
                if (whole) count += range.Count - tombstones;
                else if (tombstones == 0) count += end - start;
                else if (tombstones != range.Count)
                    count += end - start - this.CountDeletedFlags(main, start, end);
            }
            return count + this.CountMemory(from, to, deleted);
        }

        internal bool TryFastExistsWithoutMain(TKey from, TKey to, Predicate<TKey>? predicate, bool deleted, out bool found)
        {
            found = false;
            if (from.CompareTo(to) >= 0) return true;
            if (predicate != null) return false;
            if (this.ExistsInMemory(from, to, deleted)) { found = true; return true; }
            bool needsMain = false;
            int index = this.IndexRanges.FindLastLessIndex(new IndexedRange<TKey> { EndKey = from });
            while (++index < this.IndexRanges.Count)
            {
                IndexedRange<TKey> range = this.IndexRanges[index];
                if (!range.Intersects(from, to)) continue;
                long tombstones = deleted ? 0 : range.DeletionState?.DeletedCount ?? -1;
                if (tombstones == range.Count) continue;
                if (tombstones >= 0 && Covers(range, from, to)) { found = true; return true; }
                needsMain = true;
            }
            return !needsMain;
        }

        internal bool ExistsInRanges(Stream main, TKey from, TKey to, bool deleted)
        {
            if (from.CompareTo(to) >= 0) return false;
            if (this.ExistsInMemory(from, to, deleted)) return true;
            LogRecord<TKey>? lower = null, upper = null;
            int stop = this.IndexRanges.FindLastLessIndex(new IndexedRange<TKey> { EndKey = from });
            for (int index = this.IndexRanges.Count - 1; index > stop; index--)
            {
                IndexedRange<TKey> range = this.IndexRanges[index];
                if (!range.Intersects(from, to)) continue;
                long tombstones = deleted ? 0 : range.DeletionState?.DeletedCount ?? -1;
                if (tombstones == range.Count) continue;
                bool whole = Covers(range, from, to);
                if (whole && tombstones >= 0) return true;
                long start = range.Start, end = range.End;
                if (!whole)
                {
                    lower ??= new LogRecord<TKey>(this.RecordSize) { Key = from, compareFlag = sbyte.MinValue };
                    upper ??= new LogRecord<TKey>(this.RecordSize) { Key = to, compareFlag = sbyte.MinValue };
                    start = LowerBound(main, lower, range);
                    end = LowerBound(main, upper, range);
                }
                if (start >= end) continue;
                if (tombstones == 0) return true;
                // Exists needs one live record, not a mandatory whole-range cache fill.
                for (long ordinal = end - 1; ordinal >= start; ordinal--)
                {
                    main.Seek(checked(ordinal * this.RecordSize + LogRecord<TKey>.KeySize), SeekOrigin.Begin);
                    int flag = main.ReadByte();
                    if (flag < 0) throw new EndOfStreamException("Missing record deletion flag.");
                    if ((flag & (byte)StoreType.Deleted) == 0) return true;
                }
            }
            return false;
        }
    }
}
