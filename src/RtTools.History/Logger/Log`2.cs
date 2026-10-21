using System;
using System.Collections.Generic;
using System.IO;
using RtTools;
using System.Threading;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace RtTools.Logger
{
    /// <summary>Logger - a simple logging/read-only database with 1 index, optimized for append, and find range
    /// <para>
    /// Logs RECORD with Key, with 2 main functions<br></br>
    /// <list type="number">
    /// <item>
    ///     <term>Append</term>
    ///     <description>method <c>Log.Append(RECORD)</c> appends record into log</description>
    /// </item>
    /// <item>
    ///     <term>Find</term>
    ///     <description>Finds records within the half-open range [fromKey, toKey).
    ///     </description>
    /// </item>
    /// </list>
    /// </para>
    /// <para>
    /// Logger vs file.append() logging:
    ///     - uses 4 files
    ///     - logger initializes much slower (loading memory index)
    ///     - logger uses more memory (for memory index)
    ///     - indexed - findAll functionality (has Key)
    /// </para>
    /// <para>
    /// Logger vs DataBase
    ///     - log has 1 read-only table with 1 index /non unique/
    ///     - log has no sessions, transactions, connections
    ///     - log could contain records with different size (f.e. objects with same base class)
    ///     - Append accepts records in memory; Publish makes accepted records query-visible
    ///     - Flush(true) requests file synchronization; this is not a multi-file transaction
    /// </para>
    /// </summary>
    /// <typeparam name="TRecord"></typeparam>
    /// <typeparam name="TKey"></typeparam>
    /// <example>
    /// <code>
    /// using (Log&lt;Record&lt;byte[], Key.KeyDateTime>, Key.KeyDateTime> log = new Log&lt;Record&lt;byte[], Key.KeyDateTime>, Key.KeyDateTime>("datalog"))
    /// {
    ///     foreach (KeyValuePair&lt;DateTime, byte[]> data in input)  
    ///         log.Append(new Record&lt;byte[], Key.KeyDateTime>() 
    ///         {
    ///             Key = new Key.KeyDateTime() { Value = data.Key }, 
    ///             Source = data.Value 
    ///         });
    ///     ...
    ///     result = log.FindAll(new Key.KeyDateTime() { Value = DateTime.Now.AddDays(-10) }, new Key.KeyDateTime() { Value = DateTime.Now });
    /// }
    /// </code>
    /// </example>
    public partial class Log<TRecord, TKey> : IDisposable
        where TRecord : DataRecord<TKey>, new()
        where TKey : Key, new()
    {
        public const int DefaultRecordDataSize = 128;
        private const int DefaultIndexBlockRecordCount = 4096;

        private static int GetIndexBlockRecordCount(int recordCount) =>
            LogRecord<TKey>.KeySize == 12 && recordCount >= 1024
                ? 1024
                : LogRecord<TKey>.KeySize >= 24 && recordCount >= 1024
                ? 256
                : Math.Min(DefaultIndexBlockRecordCount, recordCount);
        internal sealed class RECComparer : IComparer<TRecord>
        {
            public int Compare(TRecord? x, TRecord? y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (x is null) return -1;
                return y is null ? 1 : x.CompareTo(y);
            }
        }
        internal static readonly RECComparer Comparer = new();

        private static readonly TKey EmptyKey = new();

        private static TKey CopyKey(TKey source)
        {
            byte[] buffer = new byte[source.Size];
            source.Write(buffer, 0);
            TKey copy = new();
            copy.Read(buffer, 0);
            return copy;
        }

        #region Client related methods

        /// <summary>
        /// max main read buffer
        /// </summary>
        private const int MaxExtendedBufferSize = 0x100000;
        internal const int DefaultExtendedBufferSize = 0x10000;

        /// <summary>
        /// Open main file reader
        /// </summary>
        /// <param name="options"></param>
        /// <param name="bufferSize"></param>
        /// <returns></returns>
        internal FileStream GetMainStream(FileOptions options = FileOptions.SequentialScan, long bufferSize = -1)
        {
            if (bufferSize <= 0 || (0x10000 * this.RecordSize < bufferSize))
                bufferSize = 0x10000 * this.RecordSize;
            return new FileStream(this.MainFileName, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite, (int)bufferSize, options);
        }

        /// <summary>
        /// open extended file reader
        /// </summary>
        /// <param name="options"></param>
        /// <param name="bufferSize"></param>
        /// <returns></returns>
        internal FileStream GetExtendedStream(FileOptions options = FileOptions.RandomAccess, long bufferSize = DefaultExtendedBufferSize) => new(this.ExtendedFileName, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite, (int)Math.Min(MaxExtendedBufferSize, bufferSize), options);
        #endregion

        #region public Properties/Fields

        /// <summary>
        /// Record size
        /// </summary>
        public int RecordSize { get; }

        /// <summary>
        /// Automatic write/flush at ms.
        /// </summary>
        public int AutoWriteMilliseconds { get; private set; }

        /// <summary>
        /// File for buffered storage (non-indexed records, waiting to be stored into mainStreamW)
        /// </summary>
        public string BufferFileName { get; }
        private FileStream bufferStreamW;
        // buffer locker object
        private readonly Lock bufferLock = new();

        /// <summary>
        /// File for fixed records storage
        /// </summary>
        public string MainFileName { get; }
        private readonly FileStream mainStreamW;

        /// <summary>
        /// File for records extensions storage
        /// </summary>
        public string ExtendedFileName { get; }
        private readonly FileStream extendedStreamW;

        /// <summary>
        /// File index records
        /// </summary>
        public string IndexFileName { get; }
        private readonly FileStream indexStreamW;
        /// <summary>Legacy cache path retained for API compatibility. This Log neither reads nor writes it.</summary>
        public string ProjectedIndexFileName { get; }
        internal readonly ILogKeyProjection<TKey>? KeyProjection;

        private readonly string deletedStateFileName;
        private bool legacyDeletionMarkerPrepared;
        private int memoryDeletedCount;

        private readonly Timer flushTimer; // timer to manage auto flush
        private int disposed;
        private Exception? writeError;
        private readonly object disposeLock = new();
        // Accessed only under indexLock; invalidates clients' buffered in-place deletion bytes.
        internal long ReaderGeneration { get; private set; }

        /// <summary>
        /// The first publication, flush or deletion failure, including background failures.
        /// Once set, this instance rejects further data operations. Dispose and inspect/recover
        /// the files before opening another instance. This property remains available after Dispose.
        /// </summary>
        public Exception? WriteError => Volatile.Read(ref this.writeError);
        private readonly PendingBufferShard[] pendingBufferShards;
        [ThreadStatic]
        private static Log<TRecord, TKey>? threadPendingBufferOwner;
        [ThreadStatic]
        private static PendingBufferShard? threadPendingBufferShard;
        [ThreadStatic]
        private static Log<TRecord, TKey>? threadDiagnosticOwner;

        /// <summary>
        /// Max buffer after which buffer will be copied into main data file
        /// </summary>
        public int MaxBufferSize { get; }

        /// <summary>
        /// If delete records is supported
        /// </summary>
        public bool DeleteSupported { get; }

        #endregion

        /// <summary>
        /// Create log database
        /// </summary>
        /// <param name="fileMask">file mask to create files (*.buffer, *.data, *.extra, *.index)</param>
        /// <param name="recordSize">size of small record(to write into main data file (the rest is written into extended))</param>
        /// <param name="autoWriteMilliseconds">auto write buffered data in milliseconds</param>
        /// <param name="maxBufferSize">max buffer (before copy into main data file)</param>
        /// <param name="deleteSupported">if Delete to be supported</param>
        public Log(string fileMask, int recordSize = -1, int autoWriteMilliseconds = 1000, int maxBufferSize = -1, bool deleteSupported = false, ILogKeyProjection<TKey>? keyProjection = null)
        {
            if (autoWriteMilliseconds < Timeout.Infinite)
                throw new ArgumentOutOfRangeException(nameof(autoWriteMilliseconds));
            int minSize = EmptyKey.Size + sizeof(StoreType) + sizeof(long);
            if (recordSize < 0) // default size
                recordSize = minSize + DefaultRecordDataSize;
            else
                if (recordSize < minSize) // check min record size ()
                    throw new ArgumentOutOfRangeException(nameof(recordSize), "min recordSize = " + minSize);

            this.RecordSize = recordSize;
            this.KeyProjection = keyProjection;
            this.pendingBufferShards = CreatePendingBufferShards();

            if (maxBufferSize < 0) // by default 16 MB portions
                maxBufferSize = 160000000 / recordSize;
            this.MaxBufferSize = Math.Max(10, maxBufferSize);

            this.DeleteSupported = deleteSupported;

            this.BufferFileName = Path.ChangeExtension(fileMask, ".buffer");
            this.MainFileName = Path.ChangeExtension(fileMask, ".data");
            this.ExtendedFileName = Path.ChangeExtension(fileMask, ".extra");
            this.IndexFileName = Path.ChangeExtension(fileMask, ".index");
            this.ProjectedIndexFileName = Path.ChangeExtension(fileMask, ".pindex");
            this.deletedStateFileName = Path.ChangeExtension(fileMask, ".deleted");
            //Trace.WriteLine(string.Format("log: start load: {0}", fileMask));

            // initialize indexes
            this.IndexRanges = new ComparableSortedArrayCollection<IndexedRange<TKey>>();
            this.IndexRanges.SetItems(LoadIndexRanges(this.IndexFileName), checkSorting: true);
            //Trace.WriteLine(string.Format("log: indices loaded: {0}", this.IndexRanges.Count));

            try
            {
                // open index file
                this.indexStreamW = new FileStream(this.IndexFileName, FileMode.Append, FileAccess.Write, FileShare.None, 0x10 * IndexedRange<TKey>.Size);

                // re-index file
                this.initReindex();

                if (deleteSupported)
                { // Delete is supported
                    //  open main data file for read/append
                    this.mainStreamW = new FileStream(this.MainFileName, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, this.RecordSize * 0x1000);
                    this.mainStreamW.Seek(0, SeekOrigin.End);
                }
                else
                {
                    //  open main data file for append
                    this.mainStreamW = new FileStream(this.MainFileName, FileMode.Append, FileAccess.Write, FileShare.Read, this.RecordSize * 0x1000);
                }

                //  open main extended data file for append
                this.extendedStreamW = new FileStream(this.ExtendedFileName, FileMode.Append, FileAccess.Write, FileShare.Read, 0x10000, FileOptions.WriteThrough);

                // read buffered records into memory index
                this.loadBufferIntoMem();
                // open buffer data stream
                this.bufferStreamW = new FileStream(this.BufferFileName, FileMode.Append, FileAccess.Write, FileShare.None, this.RecordSize * 0x1000, FileOptions.WriteThrough);

                this.AutoWriteMilliseconds = autoWriteMilliseconds;
                this.flushTimer = new Timer(this.timerCallback, null, Timeout.Infinite, Timeout.Infinite);
                this.flushTimer.Change(this.AutoWriteMilliseconds, Timeout.Infinite);
            }
            catch
            {
                // A failed open must release every handle already acquired without hiding
                // the original failure. This is cleanup, not rollback of initialization I/O.
                Volatile.Write(ref this.disposed, 1);
                try
                {
                    if (this.flushTimer is not null)
                        this.flushTimer.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch { }
                CloseAfterFailedOpen(this.bufferStreamW);
                CloseAfterFailedOpen(this.extendedStreamW);
                CloseAfterFailedOpen(this.mainStreamW);
                CloseAfterFailedOpen(this.indexStreamW);
                throw;
            }
        }

        private static void CloseAfterFailedOpen(IDisposable? resource)
        {
            try { resource?.Dispose(); }
            catch { } // Preserve the constructor failure and still close other resources.
        }

        /// <summary>
        /// Gets the number of published physical records, including records marked deleted.
        /// Pending Append calls are excluded. Use CountAll for a filtered range count.
        /// </summary>
        public long Count
        {
            get
            {
                if (this.indexLock.IsReadLockHeld)
                {
                    this.ThrowIfUnavailable();
                    return this.StoredRecordCount;
                }
                this.EnterIndexReadLock();
                try { return this.StoredRecordCount; }
                finally { this.ExitIndexReadLock(); }
            }
        }

        private long StoredRecordCount => (this.mainStreamW.Length + this.bufferStreamW.Length) / this.RecordSize;

        private void ThrowIfWriteFailed()
        {
            Exception? error = this.WriteError;
            if (error is not null)
                throw new InvalidOperationException("The log has failed. Dispose it and inspect/recover its files before reopening.", error);
        }

        internal void ThrowIfUnavailable()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposed) != 0, this);
            this.ThrowIfWriteFailed();
        }

        private void RememberWriteError(Exception error) => Interlocked.CompareExchange(ref this.writeError, error, null);

        internal void RejectCallbackReentry()
        {
            this.RejectDiagnosticReentry();
            if (this.indexLock.IsReadLockHeld || this.indexLock.IsWriteLockHeld)
                throw new InvalidOperationException("This operation cannot run inside a query or record callback on the same log.");
        }

        private void RejectDiagnosticReentry()
        {
            if (ReferenceEquals(threadDiagnosticOwner, this))
                throw new InvalidOperationException("This operation cannot run inside a timer diagnostic callback on the same log.");
        }

        private void timerCallback(object? stateInfo)
        {
            if (Volatile.Read(ref this.disposed) != 0 || this.WriteError is not null)
                return;
            try { this.writeBuffer(); }
            catch (ObjectDisposedException) when (Volatile.Read(ref this.disposed) != 0) { return; }
            catch (Exception e)
            {
                this.RememberWriteError(e);
                Log<TRecord, TKey>? previousDiagnosticOwner = threadDiagnosticOwner;
                threadDiagnosticOwner = this;
                try { Trace.TraceError("error on write buffer {0}", e); }
                catch (Exception) { } // A diagnostic listener must not escape the automatic publication callback.
                finally { threadDiagnosticOwner = previousDiagnosticOwner; }
            }

            if (Volatile.Read(ref this.disposed) != 0 || this.WriteError is not null)
                return;
            try { this.flushTimer.Change(this.AutoWriteMilliseconds, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
        }

        // A marker is no longer authoritative for this reader. If an older reader
        // may reuse an existing marker, conservatively update it BEFORE a tombstone.
        // No new sidecar is created, and a no-op deletion never calls this method.
        private void MarkDeletedStateSome()
        {
            if (this.legacyDeletionMarkerPrepared) return;
            FileStream stream;
            try
            {
                stream = new FileStream(this.deletedStateFileName, FileMode.Open,
                    FileAccess.Write, FileShare.Read, 16, FileOptions.WriteThrough);
            }
            catch (FileNotFoundException) { this.legacyDeletionMarkerPrepared = true; return; }
            catch (DirectoryNotFoundException) { this.legacyDeletionMarkerPrepared = true; return; }
            using (stream)
            {
                stream.WriteByte((byte)'1');
                stream.SetLength(1);
                stream.Flush(true);
            }
            this.legacyDeletionMarkerPrepared = true;
        }

        /// <summary>
        /// Publish all Append calls completed before this call began, unless publication fails.
        /// Concurrent Appends may be included or remain pending for the next publication.
        /// Queries started after return can see these records, including through existing clients.
        /// This does not request a durable sync or make a batch atomic across failures.
        /// </summary>
        public void Publish() => this.writeBuffer();

        /// <summary>
        /// Publishes pending records and flushes the four authoritative storage files.
        /// With flushToDisk=true, requests FileStream.Flush(true) for each file.
        /// The files are not committed atomically and crash recovery semantics are unchanged.
        /// </summary>
        public void Flush(bool flushToDisk = false)
        {
            this.RejectCallbackReentry();
            this.ThrowIfUnavailable();
            lock (this.bufferLock)
            {
                this.ThrowIfUnavailable();
                this.EnterIndexWriteLock();
                try
                {
                    this.WriteBufferCore();
                    this.extendedStreamW.Flush(flushToDisk);
                    this.mainStreamW.Flush(flushToDisk);
                    this.bufferStreamW.Flush(flushToDisk);
                    this.indexStreamW.Flush(flushToDisk);
                }
                catch (Exception error) { this.RememberWriteError(error); throw; }
                finally { this.ExitIndexWriteLock(); }
            }
        }

        internal bool writeBuffer()
        {
            this.RejectCallbackReentry();
            this.ThrowIfUnavailable();
            lock (this.bufferLock)
            {
                this.ThrowIfUnavailable();
                this.EnterIndexWriteLock();
                try { return this.WriteBufferCore(); }
                catch (Exception error) { this.RememberWriteError(error); throw; }
                finally { this.ExitIndexWriteLock(); }
            }
        }

        // Caller holds bufferLock and indexLock(write). Drain belongs inside the publication
        // boundary, even for an empty queue: an earlier publisher may already own a batch.
        private bool WriteBufferCore()
        {
            TRecord[] bdata = this.DrainPendingBuffer();
            if (bdata.Length == 0)
                return false;
            Array.Sort(bdata, Comparer);
            LogRecord<TKey>[] toAdd = new LogRecord<TKey>[bdata.Length];
            long start = this.StoredRecordCount;
            TKey minKey = CopyKey(bdata[0].Key);
            TKey maxKey = CopyKey(bdata[^1].Key);
            for (int i = 0; i < bdata.Length; i++)
            {
                TRecord data = bdata[i];
                LogRecord<TKey> record = new(this.RecordSize) { Key = data.Key };
                record.writeRecordData(this.bufferStreamW, this.extendedStreamW, data.Data);
                toAdd[i] = record;
            }

            // Flush buffers
            this.extendedStreamW.Flush();
            this.bufferStreamW.Flush();

            IndexedRange<TKey> range = new()
            {
                Start = start,
                End = this.StoredRecordCount,
                StartKey = minKey,
                EndKey = maxKey,
            };

            if (this.AddToMemBufferAndCopyToMain(toAdd, range))
            {
                // clear buffer file
                this.bufferStreamW.Dispose();

                // next is better than File.Delete
                using (FileStream stream = new(this.BufferFileName, FileMode.Truncate)) { }

                this.bufferStreamW = new FileStream(this.BufferFileName, FileMode.Append, FileAccess.Write, FileShare.None, this.RecordSize * 0x1000, FileOptions.WriteThrough);
            }
            return true;
        }

        /// <summary>
        /// Accept a record reference in memory. It becomes query-visible at Publish (or the timer).
        /// Do not mutate the record or payload before publication. Keys must remain immutable
        /// while the log uses them, including keys returned by queries. Acceptance is not durable storage.
        /// </summary>
        /// <param name="rec"></param>
        public void Append(TRecord rec)
        {
            ArgumentNullException.ThrowIfNull(rec);
            this.RejectDiagnosticReentry();
            if (this.indexLock.IsWriteLockHeld)
                throw new InvalidOperationException("Append cannot run inside a record publication or deletion callback.");
            PendingBufferShard shard = this.GetPendingBufferShard();
            lock (shard.SyncRoot)
            {
                this.ThrowIfUnavailable();
                shard.Records.Add(rec);
            }
        }

        /// <summary>
        /// Accept record references in memory. Enumeration and null validation complete before
        /// any records are queued; an enumeration failure accepts none of this batch.
        /// </summary>
        /// <param name="records"></param>
        public void Append(IEnumerable<TRecord> records)
        {
            ArgumentNullException.ThrowIfNull(records);
            this.RejectDiagnosticReentry();
            this.ThrowIfUnavailable();
            if (this.indexLock.IsWriteLockHeld)
                throw new InvalidOperationException("Append cannot run inside a record publication or deletion callback.");
            // Caller code must not execute under a shard lock: it could otherwise reverse
            // bufferLock -> shard ordering by calling Publish while it enumerates.
            List<TRecord> batch = new List<TRecord>(records);
            foreach (TRecord record in batch) ArgumentNullException.ThrowIfNull(record);
            PendingBufferShard shard = this.GetPendingBufferShard();
            lock (shard.SyncRoot)
            {
                this.ThrowIfUnavailable();
                shard.Records.AddRange(batch);
            }
        }

        private static PendingBufferShard[] CreatePendingBufferShards()
        {
            int shardCount = Math.Min(Math.Max(1, Environment.ProcessorCount), 16);
            PendingBufferShard[] shards = new PendingBufferShard[shardCount];
            for (int i = 0; i < shards.Length; i++)
                shards[i] = new PendingBufferShard();
            return shards;
        }

        private PendingBufferShard GetPendingBufferShard()
        {
            if (ReferenceEquals(threadPendingBufferOwner, this) && threadPendingBufferShard != null)
                return threadPendingBufferShard;

            PendingBufferShard[] shards = this.pendingBufferShards;
            PendingBufferShard shard = shards[(Environment.CurrentManagedThreadId & int.MaxValue) % shards.Length];
            threadPendingBufferOwner = this;
            threadPendingBufferShard = shard;
            return shard;
        }

        private TRecord[] DrainPendingBuffer()
        {
            TRecord[]? single = null;
            List<TRecord>? merged = null;

            PendingBufferShard[] shards = this.pendingBufferShards;
            for (int i = 0; i < shards.Length; i++)
            {
                PendingBufferShard shard = shards[i];
                lock (shard.SyncRoot)
                {
                    int count = shard.Records.Count;
                    if (count == 0)
                        continue;

                    if (single == null && merged == null)
                    {
                        single = shard.Records.ToArray();
                    }
                    else
                    {
                        if (merged == null)
                        {
                            merged = new List<TRecord>(single!.Length + count);
                            merged.AddRange(single);
                            single = null;
                        }

                        merged!.AddRange(shard.Records);
                    }

                    shard.Records.Clear();
                }
            }

            return merged?.ToArray() ?? single ?? [];
        }

        private sealed class PendingBufferShard
        {
            public Lock SyncRoot { get; } = new();
            public List<TRecord> Records { get; } = [];
        }

        #region Disposing/finalizing

        /// <summary>
        /// Dispose stream
        /// </summary>
        /// <param name="disposing"></param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;
            this.RejectCallbackReentry();
            lock (this.disposeLock) this.DisposeCore();
        }

        private void DisposeCore()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) != 0) return;
            this.AutoWriteMilliseconds = Timeout.Infinite;
            Exception? failure = null;
            try { this.flushTimer.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception error) { failure = error; this.RememberWriteError(error); }
            // Never wait for the timer while holding either writer lock. Existing publications
            // finish first; Appends accepted before closing are then included in the final drain.
            lock (this.bufferLock)
            {
                // Cleanup must wait for current readers, including in Debug builds.
                this.indexLock.EnterWriteLock();
                try
                {
                    try { this.ThrowIfWriteFailed(); this.WriteBufferCore(); }
                    catch (Exception error) { failure ??= error; this.RememberWriteError(error); }
                    Close(this.mainStreamW);
                    Close(this.extendedStreamW);
                    Close(this.indexStreamW);
                    Close(this.bufferStreamW);
                }
                finally { this.ExitIndexWriteLock(); }
            }
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();

            void Close(IDisposable? resource)
            {
                try { resource?.Dispose(); }
                catch (Exception error) { failure ??= error; this.RememberWriteError(error); }
            }
        }

        /// <summary>
        /// Dispose Logger resources
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion

    }
}
