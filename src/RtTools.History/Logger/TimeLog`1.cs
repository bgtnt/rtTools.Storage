using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace RtTools.Logger
{
    /// <summary>
    /// DateTime 
    /// </summary>
    /// <typeparam name="TRecord">Log record type</typeparam>
    public class TimeLog<TRecord> : IDisposable
    {

        /// <summary>
        /// Time Log fixed record size
        /// </summary>
        public const int RecordSize = sizeof(long) + sizeof(long);

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

        private DateTime lastLogTime;
        private readonly object syncRoot = new();

        public const int AutoWriteMilliseconds = 1000;
        private readonly Timer flushTimer; // timer to manage auto flush
        private int disposed;
        private Exception? lastAutoFlushException;

        /// <summary>
        /// The failure from the latest completed automatic flush, or null after a successful automatic flush.
        /// Explicit Flush and Dispose calls do not clear this value.
        /// </summary>
        public Exception? LastAutoFlushException => Volatile.Read(ref this.lastAutoFlushException);

        internal Converter<byte[], TRecord>? ItemReader { get; }
        private readonly Converter<TRecord, byte[]>? itemWriter;

        /// <summary>
        /// Constructs new time log
        /// </summary>
        /// <param name="fileMask"></param>
        public TimeLog(string fileMask,
            Converter<byte[], TRecord>? reader = null,
            Converter<TRecord, byte[]>? writer = null)
        {
            if ((this.ItemReader = reader) == null ^ (this.itemWriter = writer) == null)
                throw new ArgumentException("reader-writer pair should be provided");

            this.flushTimer = new Timer(this.timerCallback);
            this.MainFileName = Path.ChangeExtension(fileMask, ".tlog");
            this.ExtendedFileName = Path.ChangeExtension(fileMask, ".tldata");

            FileStream? openedMain = null;
            FileStream? openedExtended = null;
            try
            {
                this.mainStreamW = openedMain = new FileStream(this.MainFileName, FileMode.Append, FileAccess.Write, FileShare.Read, RecordSize * 0x1000, FileOptions.WriteThrough);
                if (openedMain.Length >= RecordSize)
                { // has records in main stream
                    using (FileStream main = new(this.MainFileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, RecordSize, FileOptions.RandomAccess))
                    { // reopen in random read mode (like in TimeLogClient) to use seek() method
                        main.Seek(-RecordSize, SeekOrigin.End); // seek last record
                        byte[] buffer = new byte[RecordSize];
                        main.ReadExactly(buffer, 0, RecordSize); // read up to the end
                        long dbin = BinaryPrimitives.ReadInt64LittleEndian(buffer);
                        this.lastLogTime = DateTime.FromBinary(dbin >= 0 ? dbin : -dbin);
                    }
                }
                else this.lastLogTime = default;

                this.extendedStreamW = openedExtended = new FileStream(this.ExtendedFileName, FileMode.Append, FileAccess.Write, FileShare.Read, 0x10000, FileOptions.WriteThrough);
            }
            catch (Exception error)
            {
                List<Exception>? failures = [error];
                TryDispose(this.flushTimer, ref failures);
                if (openedMain != null) TryDispose(openedMain, ref failures);
                if (openedExtended != null) TryDispose(openedExtended, ref failures);
                ThrowFailure(CreateFailure(failures, "Time log initialization failed."));
                throw;
            }
        }

        private void timerCallback(object? stateInfo)
        {
            Exception? failure;
            lock (this.syncRoot)
            {
                if (this.disposed != 0) return;
                failure = CreateFailure(this.flushStreams(), "Automatic time log flush failed.");
                Volatile.Write(ref this.lastAutoFlushException, failure);
            }

            if (failure != null)
                try { Trace.TraceError("error on flush buffer {0}", failure); }
                catch (Exception) { } // A diagnostic listener must not turn an automatic flush error into an unhandled callback error.
        }

        /// <summary>
        /// Flushes both files. A single failure is propagated unchanged; multiple failures are aggregated.
        /// A successful flush does not imply atomic writes or power-loss recovery.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The time log has been disposed.</exception>
        public void Flush()
        {
            Exception? failure;
            lock (this.syncRoot)
            {
                this.throwIfDisposed();
                failure = CreateFailure(this.flushStreams(), "Time log flush failed.");
            }
            ThrowFailure(failure);
        }

        private List<Exception>? flushStreams()
        {
            List<Exception>? failures = null;
            TryFlush(this.mainStreamW, ref failures);
            TryFlush(this.extendedStreamW, ref failures);
            return failures;
        }

        private static void TryFlush(FileStream stream, ref List<Exception>? failures)
        {
            try { stream.Flush(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }

        private static void TryDispose(IDisposable resource, ref List<Exception>? failures)
        {
            try { resource.Dispose(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }

        private static Exception? CreateFailure(List<Exception>? failures, string message) => failures == null
            ? null : failures.Count == 1 ? failures[0] : new AggregateException(message, failures);

        private static void ThrowFailure(Exception? failure)
        {
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private void throwIfDisposed() => ObjectDisposedException.ThrowIf(this.disposed != 0, this);

        /// <summary>
        /// Reset timer in order to avoid file flush
        /// </summary>
        protected void resetTimer()
        {
            try { this.flushTimer.Change(AutoWriteMilliseconds, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// Gets logged records count
        /// </summary>
        public long Count
        {
            get
            {
                lock (this.syncRoot)
                {
                    this.throwIfDisposed();
                    return this.mainStreamW.Length / RecordSize;
                }
            }
        }

        /// <summary>
        /// Ticks to assume data is expired (in flag shiftEquals)
        /// </summary>
        public long TicksToExpire { get; set; } = TimeSpan.TicksPerMillisecond / 2;

        /// <summary>
        /// Append new log record
        /// </summary>
        /// <param name="now">record date</param>
        /// <param name="record">record to append</param>
        /// <param name="shiftEquals">if <paramref name="now"/> is before last record, and difference is below <see cref="TicksToExpire"/>, <paramref name="now"/> is set to last + 1 tick</param>
        /// <exception cref="ArgumentNullException">The timestamp is eligible, no custom writer is supplied, and record is null.</exception>
        public bool Append(DateTime now, TRecord record, bool shiftEquals = false)
        {
            if (now.Kind != DateTimeKind.Utc) now = now.ToUniversalTime();

            lock (this.syncRoot)
            {
                this.throwIfDisposed();
                if (now <= this.lastLogTime)
                { // expired date, but in range
                    if (!shiftEquals || this.TicksToExpire <= 0 ||
                        this.lastLogTime.Subtract(now).Ticks >= this.TicksToExpire)
                        return false;

                    now = this.lastLogTime.AddTicks(1);
                }

                if (this.itemWriter == null && record is null)
                    throw new ArgumentNullException(nameof(record));

                byte[] data = (this.itemWriter != null ? this.itemWriter(record) : PersistenceBinary.ToBytes(record))
                    ?? throw new InvalidDataException("Time log writer returned null data.");
                var dlen = data.Length;

                byte[] rec = new byte[RecordSize];
                if (dlen >= sizeof(long))
                { // bug data - no space to store
                    BinaryPrimitives.WriteInt64LittleEndian(rec, now.ToBinary());
                    BinaryPrimitives.WriteInt64LittleEndian(rec.AsSpan(sizeof(long)), this.extendedStreamW.Length);
                    this.mainStreamW.Write(rec, 0, RecordSize); // write time stamp + extended position

                    BinaryPrimitives.WriteInt32LittleEndian(rec, dlen);
                    this.extendedStreamW.Write(rec, 0, sizeof(int)); // write data length
                    this.extendedStreamW.Write(data, 0, dlen); // write data itself
                }
                else
                { // dlen < 8 bytes - could be directly stored
                    BinaryPrimitives.WriteInt64LittleEndian(rec, -now.ToBinary());
                    rec[sizeof(long)] = (byte)dlen;
                    data.CopyTo(rec, sizeof(long) + 1);
                    this.mainStreamW.Write(rec, 0, RecordSize); // write time stamp + data
                }
                this.lastLogTime = now;
                this.resetTimer();
                return true;
            }
        }

        /// <summary>
        /// Append new log record
        /// </summary>
        /// <param name="record"></param>
        public bool Append(TRecord record, bool shiftEquals = false) => this.Append(DateTime.UtcNow, record, shiftEquals);

        /// <summary>
        /// Create Log client/reader
        /// </summary>
        /// <param name="recBufferSize"></param>
        /// <param name="extBufferSize"></param>
        /// <returns></returns>
        public TimeLogClient<TRecord> CreateClient(int recBufferSize = 0x1000, int extBufferSize = 0x10000)
        {
            lock (this.syncRoot)
            {
                this.throwIfDisposed();
                return new(this, recBufferSize, extBufferSize);
            }
        }

        #region Disposing/finalizing

        /// <summary>
        /// Dispose stream
        /// </summary>
        /// <param name="disposing"></param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;

            Exception? failure;
            lock (this.syncRoot)
            {
                if (this.disposed != 0) return;
                this.disposed = 1;
                List<Exception>? failures = null;
                // Do not wait for timer callbacks: a diagnostic listener may call Dispose from the callback itself.
                // Queued callbacks observe disposed under this same lock and never touch closed streams.
                TryDispose(this.flushTimer, ref failures);
                List<Exception>? flushFailures = this.flushStreams();
                if (flushFailures != null) (failures ??= []).AddRange(flushFailures);
                TryDispose(this.mainStreamW, ref failures);
                TryDispose(this.extendedStreamW, ref failures);
                failure = CreateFailure(failures, "Time log disposal failed.");
            }
            ThrowFailure(failure);
        }

        /// <summary>
        /// Attempts both final flushes and both stream disposals even when an operation fails, then propagates the failure(s).
        /// Already-running diagnostic reporting may finish after this method returns.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion

    }
}
