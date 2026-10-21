using System;
using System.Collections.Generic;
using System.IO;
using RtTools;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace RtTools.Logger
{
    /// <summary>
    /// Reads published log records in half-open key ranges [from, to).
    /// </summary>
    /// <typeparam name="TRecord"></typeparam>
    /// <typeparam name="TKey"></typeparam>
    public sealed class LogClient<TRecord, TKey> : IDisposable
        where TRecord : DataRecord<TKey>, new()
        where TKey : Key, new()
    {
        private readonly Log<TRecord, TKey> dbase;
        private readonly int recBufferSize;
        private readonly int extBufferSize;
        private readonly object syncRoot = new();
        private bool disposed;
        private long seenGeneration;
        private Stream? main;
        private Stream? ext;

        internal LogClient(Log<TRecord, TKey> dbase, int recBufferSize = 0x1000, int extBufferSize = Log<TRecord, TKey>.DefaultExtendedBufferSize)
        {
            this.dbase = dbase;
            this.recBufferSize = recBufferSize;
            this.extBufferSize = extBufferSize;
        }

        private Stream Main => this.main ??= this.dbase.GetMainStream(bufferSize: this.recBufferSize * (long)this.dbase.RecordSize);

        private Stream Ext => this.ext ??= this.dbase.GetExtendedStream(bufferSize: this.extBufferSize);

        private Stream MainIfNeeded => this.dbase.IndexRanges.Count == 0 ? Stream.Null : this.Main;

        // Caller holds syncRoot and the database index read lock. In-place deletion
        // changes may otherwise remain hidden by an existing FileStream read buffer.
        private void RefreshMainReader()
        {
            long generation = this.dbase.ReaderGeneration;
            if (generation == this.seenGeneration) return;
            this.main?.Dispose();
            this.main = null;
            this.seenGeneration = generation;
        }

        private const int DirectMemoryFindAllThreshold = 512;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte[] ReadRecordData(LogRecord<TKey> record)
        {
            StoreType options = record.Options;
            return LogRecord<TKey>.HasExtendedData(options)
                ? record.readExtendedRecordData(options, this.Ext)
                : record.readFixedRecordData(options);
        }

        private TRecord[] FindAllMemory(TKey from, TKey to, Predicate<TKey>? predicate, bool skipDeletedCheck)
        {
            IndexedRange<TKey> range = this.dbase.memBufferRange;
            if (!range.Intersects(from, to))
                return [];

            LogRecord<TKey>[] memIndex = this.dbase.memBuffer;
            int start = Log<TRecord, TKey>.LowerBound(memIndex, from);
            Stream? extended = this.ext;
            byte[] ReadData(LogRecord<TKey> record)
            {
                StoreType options = record.Options;
                return LogRecord<TKey>.HasExtendedData(options)
                    ? record.readExtendedRecordData(options, extended ??= this.Ext)
                    : record.readFixedRecordData(options);
            }

            if (predicate == null)
            {
                int end = Log<TRecord, TKey>.LowerBound(memIndex, to);
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
                if (predicate != null && !predicate(key))
                    continue;

                ret.Add(new TRecord() { Key = key, Data = ReadData(record) });
            }

            return ret.ToArray();
        }

        private long ForEachMemory(TKey from, TKey to, Action<TRecord> action, Predicate<TKey>? predicate, bool skipDeletedCheck)
        {
            IndexedRange<TKey> range = this.dbase.memBufferRange;
            if (!range.Intersects(from, to))
                return 0;

            LogRecord<TKey>[] memIndex = this.dbase.memBuffer;
            int start = Log<TRecord, TKey>.LowerBound(memIndex, from);
            Stream? extended = this.ext;
            byte[] ReadData(LogRecord<TKey> record)
            {
                StoreType options = record.Options;
                return LogRecord<TKey>.HasExtendedData(options)
                    ? record.readExtendedRecordData(options, extended ??= this.Ext)
                    : record.readFixedRecordData(options);
            }

            long count = 0;
            if (predicate == null)
            {
                int end = Log<TRecord, TKey>.LowerBound(memIndex, to);
                if (skipDeletedCheck)
                {
                    for (int i = start; i < end; i++)
                    {
                        LogRecord<TKey> record = memIndex[i];
                        action(new TRecord() { Key = record.Key, Data = ReadData(record) });
                    }

                    return Math.Max(0, end - start);
                }

                for (int i = start; i < end; i++)
                {
                    LogRecord<TKey> record = memIndex[i];
                    if (record.IsDeleted)
                        continue;

                    action(new TRecord() { Key = record.Key, Data = ReadData(record) });
                    count++;
                }

                return count;
            }

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

                action(new TRecord() { Key = key, Data = ReadData(record) });
                count++;
            }

            return count;
        }

        /// <summary>
        /// Visit published records in the half-open key range [from, to).
        /// </summary>
        /// <param name="from">Inclusive lower key.</param>
        /// <param name="to">Exclusive upper key.</param>
        /// <param name="action">action to do</param>
        /// <param name="predicate">comparer to use</param>
        /// <param name="deleted">if to include deleted records</param>
        /// <returns>count of records processed</returns>
        public long ForEach(TKey from, TKey to, Action<TRecord> action, Predicate<TKey>? predicate = null, bool deleted = false)
        {
            this.dbase.RejectCallbackReentry();
            lock (this.syncRoot)
            {
                ObjectDisposedException.ThrowIf(this.disposed, this);
                this.dbase.EnterIndexReadLock();
                try
                {
                    this.RefreshMainReader();
                    bool memoryOnly = this.dbase.IndexRanges.Count == 0;
                    bool skipDeletedCheck = this.dbase.CanSkipDeletedCheck(deleted);
                    if (memoryOnly && this.dbase.memBuffer.Length >= DirectMemoryFindAllThreshold)
                        return this.ForEachMemory(from, to, action, predicate, skipDeletedCheck);

                    Stream mainStream = memoryOnly ? Stream.Null : this.Main;
                    Stream? extended = this.ext;
                    byte[] ReadData(LogRecord<TKey> record)
                    {
                        StoreType options = record.Options;
                        return LogRecord<TKey>.HasExtendedData(options)
                            ? record.readExtendedRecordData(options, extended ??= this.Ext)
                            : record.readFixedRecordData(options);
                    }

                    return skipDeletedCheck
                        ? (memoryOnly
                            ? this.dbase.ScanMemoryForward(from, to, record => { action(new TRecord() { Key = record.Key, Data = ReadData(record) }); return true; }, predicate: predicate)
                            : this.dbase.ScanForward(mainStream, from, to, record => { action(new TRecord() { Key = record.Key, Data = ReadData(record) }); return true; }, predicate: predicate))
                        : (memoryOnly
                            ? this.dbase.ScanMemoryForward(from, to, record =>
                            {
                                if (record.IsDeleted) return false;
                                action(new TRecord() { Key = record.Key, Data = ReadData(record) }); return true;
                            }, predicate: predicate)
                            : this.dbase.ScanForward(mainStream, from, to, record =>
                        {
                            if (record.IsDeleted) return false;
                            action(new TRecord() { Key = record.Key, Data = ReadData(record) }); return true;
                        }, predicate: predicate));
                }
                finally { this.dbase.ExitIndexReadLock(); }
            }
        }

        /// <summary>
        /// Read records in the half-open key range [from, to), sorted by key.
        /// </summary>
        /// <param name="from">Inclusive lower key.</param>
        /// <param name="to">Exclusive upper key.</param>
        /// <param name="predicate">comparer to use</param>
        /// <param name="deleted">if to include deleted records</param>
        /// <returns></returns>
        public TRecord[] FindAll(TKey from, TKey to, Predicate<TKey>? predicate = null, bool deleted = false)
        {
            this.dbase.RejectCallbackReentry();
            lock (this.syncRoot)
            {
                ObjectDisposedException.ThrowIf(this.disposed, this);
                this.dbase.EnterIndexReadLock();
                try
                {
                    this.RefreshMainReader();
                    bool memoryOnly = this.dbase.IndexRanges.Count == 0;
                    bool skipDeletedCheck = this.dbase.CanSkipDeletedCheck(deleted);
                    if (memoryOnly && this.dbase.memBuffer.Length >= DirectMemoryFindAllThreshold)
                        return this.FindAllMemory(from, to, predicate, skipDeletedCheck);

                    Stream mainStream = memoryOnly ? Stream.Null : this.Main;
                    Log<TRecord, TKey>.FindStructure fs = new() { GetExtended = () => this.Ext };
                    if (skipDeletedCheck)
                    {
                        if (memoryOnly)
                            this.dbase.ScanMemoryForward(from, to, fs.OnRecordFound, predicate: predicate);
                        else
                            this.dbase.ScanForward(mainStream, from, to, fs.OnRecordFound, indexCallback: fs.OnNextIndex, predicate: predicate);
                    }
                    else
                    {
                        if (memoryOnly)
                            this.dbase.ScanMemoryForward(from, to, fs.OnNotDeletedRecordFound, predicate: predicate);
                        else
                            this.dbase.ScanForward(mainStream, from, to, fs.OnNotDeletedRecordFound, indexCallback: fs.OnNextIndex, predicate: predicate);
                    }
                    return fs.ToSortedArray();
                }
                finally { this.dbase.ExitIndexReadLock(); }
            }
        }

        /// <summary>
        /// Count records in the half-open key range [from, to).
        /// </summary>
        /// <param name="from">Inclusive lower key.</param>
        /// <param name="to">Exclusive upper key.</param>
        /// <param name="predicate">comparer to use</param>
        /// <param name="deleted">if to count deleted</param>
        /// <returns>records found</returns>
        public long CountAll(TKey from, TKey to, Predicate<TKey>? predicate = null, bool deleted = false)
        {
            this.dbase.RejectCallbackReentry();
            lock (this.syncRoot)
            {
                ObjectDisposedException.ThrowIf(this.disposed, this);
                this.dbase.EnterIndexReadLock();
                try
                {
                    this.RefreshMainReader();
                    if (this.dbase.TryFastCountAllWithoutMain(from, to, predicate, deleted, out long count))
                        return count;
                    return predicate == null
                        ? this.dbase.CountRanges(this.MainIfNeeded, from, to, deleted)
                        : this.dbase.ScanForward(this.MainIfNeeded, from, to,
                            deleted ? Log<TRecord, TKey>.countAllRecords : Log<TRecord, TKey>.countNotDeletedRecords, predicate: predicate);
                }
                finally { this.dbase.ExitIndexReadLock(); }
            }
        }

        /// <summary>
        /// Check whether a record exists in the half-open key range [from, to).
        /// </summary>
        /// <param name="from">Inclusive lower key.</param>
        /// <param name="to">Exclusive upper key.</param>
        /// <param name="predicate">comparer to use</param>
        /// <param name="deleted">if to count deleted</param>
        /// <returns>true if exists</returns>
        public bool Exists(TKey from, TKey to, Predicate<TKey>? predicate = null, bool deleted = false)
        {
            this.dbase.RejectCallbackReentry();
            lock (this.syncRoot)
            {
                ObjectDisposedException.ThrowIf(this.disposed, this);
                this.dbase.EnterIndexReadLock();
                try
                {
                    this.RefreshMainReader();
                    if (this.dbase.TryFastExistsWithoutMain(from, to, predicate, deleted, out bool found))
                        return found;
                    return predicate == null
                        ? this.dbase.ExistsInRanges(this.MainIfNeeded, from, to, deleted)
                        : this.dbase.ScanBackward(this.MainIfNeeded, from, to,
                            deleted ? Log<TRecord, TKey>.countAllRecords : Log<TRecord, TKey>.countNotDeletedRecords, predicate: predicate, max: 1) > 0;
                }
                finally { this.dbase.ExitIndexReadLock(); }
            }
        }

        /// <summary>
        /// Check whether a record exists in [from, to), scanning backward.
        /// </summary>
        /// <param name="from">Inclusive lower key.</param>
        /// <param name="to">Exclusive upper key.</param>
        /// <param name="predicate">comparer to use</param>
        /// <param name="deleted">if to count deleted</param>
        /// <returns>true if exists</returns>
        public bool ExistsBackward(TKey from, TKey to, Predicate<TKey>? predicate = null, bool deleted = false)
        {
            this.dbase.RejectCallbackReentry();
            lock (this.syncRoot)
            {
                ObjectDisposedException.ThrowIf(this.disposed, this);
                this.dbase.EnterIndexReadLock();
                try
                {
                    this.RefreshMainReader();
                    Stream mainStream = this.MainIfNeeded;
                    return this.dbase.CanSkipDeletedCheck(deleted)
                        ? this.dbase.ScanBackward(mainStream, from, to, Log<TRecord, TKey>.countAllRecords, predicate: predicate, max: 1) > 0
                        : this.dbase.ScanBackward(mainStream, from, to, Log<TRecord, TKey>.countNotDeletedRecords, predicate: predicate, max: 1) > 0;
                }
                finally { this.dbase.ExitIndexReadLock(); }
            }
        }

        /// <summary>
        /// Close this client's readers after any active query finishes.
        /// Calling Dispose from any query or record callback on this log is not supported.
        /// </summary>
        public void Dispose()
        {
            this.dbase.RejectCallbackReentry();
            if (Monitor.IsEntered(this.syncRoot))
                throw new InvalidOperationException("A log client cannot be disposed from its own query callback.");
            lock (this.syncRoot)
            {
                if (this.disposed) return;
                this.disposed = true;
                Exception? failure = null;
                try { this.main?.Dispose(); }
                catch (Exception error) { failure = error; }
                try { this.ext?.Dispose(); }
                catch (Exception error) { failure ??= error; }
                this.main = null;
                this.ext = null;
                if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
    }
}
