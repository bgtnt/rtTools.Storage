using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.ComponentModel;
using System.Runtime.ExceptionServices;

namespace RtTools
{
    /// <summary>
    /// Persistent storage wrapper around <see cref="DataStorage"/> with a shared synchronization object
    /// and idle auto-flush support.
    /// </summary>
    /// <remarks>
    /// Automatic flush failures are contained and exposed through LastAutoFlushException and the owned storage's
    /// fault state. A mutation which completed before an inline automatic flush failed still returns its result,
    /// so callers can publish their record position or resident state. Subsequent storage operations reject the
    /// faulted owner. Use explicit Flush as an observable checkpoint; automatic telemetry alone is not a health check.
    /// </remarks>
    public sealed class SynchronizedDataStorage : IDisposable, ICollection<object?>
    {
        /// <summary>
        /// Internal DataStorage
        /// </summary>
        public DataStorage Storage { get; }

        /// <summary>
        /// Object used to coordinate storage access.
        /// </summary>
        public object SynchronizationRoot { get; } = new();
        private readonly Timer flushTimer; // timer to manage auto flush
        private int disposed;
        private int unflushedUpdates;
        private Exception? lastAutoFlushException;

        /// <summary>The first failure recorded by the owned storage, or null if none has been recorded.</summary>
        public Exception? StorageException => this.Storage.StorageException;

        /// <summary>Whether the owned storage has entered its terminal faulted state.</summary>
        public bool IsFaulted => this.Storage.IsFaulted;

        /// <summary>
        /// The exact failure from the latest completed automatic flush, or null after a successful automatic flush.
        /// Explicit Flush and Dispose do not change this value. Skipped callbacks do not represent attempts.
        /// This value is not a health check; use IsFaulted and StorageException to inspect the owned storage.
        /// </summary>
        public Exception? LastAutoFlushException => Volatile.Read(ref this.lastAutoFlushException);

        /// <summary>
        /// Time in seconds to autoFlush if idle
        /// </summary>
        [Browsable(false)]
        [Description("Idle time /seconds/ to auto flush file"), Category("Storage")]
        public int FlushIdleSeconds
        {
            get => this.FlushIdleMilliseconds / 1000;
            set => this.FlushIdleMilliseconds = value * 1000;
        }

        /// <summary>
        /// Time in milliseconds to autoFlush if idle
        /// </summary>
        [Browsable(false)]
        [Description("Idle time /seconds/ to auto flush file"), Category("Storage")]
        public int FlushIdleMilliseconds { get; set; } = 1000;

        /// <summary>
        /// Updates count to flush file
        /// </summary>
        [Browsable(false)]
        [Description("Idle time /seconds/ to auto flush file"), Category("Storage")]
        public ushort FlushUpdates { get; set; } = 256; // auto flush on 256 updates

        /// <summary>
        /// Get Elements count
        /// </summary>
        public int Count
        {
            get
            {
                lock (this.SynchronizationRoot)
                {
                    this.ThrowIfUnavailable();
                    return this.Storage.Count;
                }
            }
        }

        /// <summary>
        /// Enumerate all positions in storage
        /// </summary>
        public IEnumerable<long> ReadPositions
        {
            get
            {
                lock (this.SynchronizationRoot)
                {
                    this.ThrowIfUnavailable();
                    return [.. this.Storage.ReadPositions];
                }
            }
        }

        /// <summary>
        /// Enumerates stored objects as Position, Data
        /// </summary>
        public IEnumerable<KeyValuePair<long, byte[]>> ReadData
        {
            get
            {
                lock (this.SynchronizationRoot)
                {
                    this.ThrowIfUnavailable();
                    return [.. this.Storage.ReadData];
                }
            }
        }

        /// <summary>
        /// Enumerates stored objects as Position, Object
        /// </summary>
        public IEnumerable<KeyValuePair<long, object?>> ReadObjects
        {
            get
            {
                lock (this.SynchronizationRoot)
                {
                    this.ThrowIfUnavailable();
                    return [.. this.Storage.ReadObjects];
                }
            }
        }

        /// <summary>
        /// open new file data storage
        /// </summary>
        /// <param name="fileName">data file name</param>
        /// <param name="mode">data file mode</param>
        /// <param name="access">data file access</param>
        /// <param name="share">data file share</param>
        /// <param name="headerSize">stream header size</param>
        /// <param name="scanBytesCallback">load data callback</param>
        /// <param name="scanObjectsCallback">load deserialized data callback</param>
        public SynchronizedDataStorage(
            string fileName, FileMode mode = FileMode.OpenOrCreate, FileAccess access = FileAccess.ReadWrite, FileShare share = FileShare.None,
            ushort headerSize = 0,
            DataStorage.DataScan? scanBytesCallback = null, DataStorage.ObjectScan? scanObjectsCallback = null) :
            this(File.Open(fileName, mode, access, share), headerSize, scanBytesCallback, scanObjectsCallback)
        { }

        /// <summary>
        /// new persistent dictionary
        /// </summary>
        /// <param name="stream">stream to open</param>
        /// <param name="headerSize">stream header size</param>
        /// <param name="scanBytesCallback">load data callback</param>
        /// <param name="scanObjectsCallback">load deserialized data callback</param>
        public SynchronizedDataStorage(Stream stream,
            ushort headerSize = 0,
            DataStorage.DataScan? scanBytesCallback = null, DataStorage.ObjectScan? scanObjectsCallback = null,
            DataStorage.OnException onException = DataStorage.OnException.Default,
            DataStorage.OpenStateDelegate? listener = null)
            : this(new DataStorage(stream, scanBytesCallback, scanObjectsCallback, headerSize, serializer: DataContractDataSerializer.Default, onException: onException, listener: listener)) { }

        /// <summary>
        /// Constructs new object for data storage
        /// </summary>
        /// <param name="storage">data storage to open</param>
        public SynchronizedDataStorage(DataStorage storage)
        {
            ArgumentNullException.ThrowIfNull(storage);
            this.Storage = storage;
            this.flushTimer = new Timer(this.FlushTimerCallback);
        }

        internal void FlushNeeded()
        {
            lock (this.SynchronizationRoot)
            {
                if (this.disposed != 0 || this.Storage.IsFaulted) return;
                this.unflushedUpdates++;
                if (this.FlushIdleMilliseconds == 0 || this.unflushedUpdates >= this.FlushUpdates)
                    this.AutoFlush();
                else
                    this.flushTimer.Change(this.FlushIdleMilliseconds > 0 ? this.FlushIdleMilliseconds : Timeout.Infinite, Timeout.Infinite);
            }
        }

        private void FlushTimerCallback(object? stateInfo)
        {
            lock (this.SynchronizationRoot)
            {
                if (this.disposed != 0 || this.Storage.IsFaulted) return;
                this.AutoFlush();
            }
        }

        // Called under the shared gate, which may also be held by a parent collection.
        // Never throw a post-commit flush failure before that collection publishes its cache or PersistId.
        private void AutoFlush()
        {
            try
            {
                this.Storage.Flush();
                this.unflushedUpdates = 0;
                Volatile.Write(ref this.lastAutoFlushException, null);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref this.lastAutoFlushException, ex);
            }
        }

        // Callers coordinate entry using SynchronizationRoot, including collection operations which
        // write through Storage directly and must reject unavailable state before invoking user code.
        internal void ThrowIfUnavailable()
        {
            ObjectDisposedException.ThrowIf(this.disposed != 0, this);
            this.Storage.ThrowIfUnavailable();
        }

        /// <summary>
        /// Flushes the internal stream and clears pending-update accounting only on success.
        /// Failures propagate to the caller; automatic-flush diagnostics remain unchanged.
        /// </summary>
        public void Flush()
        {
            lock (this.SynchronizationRoot)
            {
                this.ThrowIfUnavailable();
                this.Storage.Flush();
                this.unflushedUpdates = 0;
            }
        }

        /// <summary>
        /// Get element at position
        /// </summary>
        /// <param name="pos"></param>
        /// <returns></returns>
        public object? this[long pos]
        {
            get
            {
                lock (this.SynchronizationRoot)
                {
                    this.ThrowIfUnavailable();
                    return this.Storage.FromBytes(this.Storage.GetData(pos));
                }
            }
        }

        /// <summary>
        /// Get data at position
        /// </summary>
        /// <param name="pos"></param>
        /// <returns></returns>
        public byte[] GetData(long pos)
        {
            lock (this.SynchronizationRoot)
            {
                this.ThrowIfUnavailable();
                return this.Storage.GetData(pos);
            }
        }
        /// <summary>
        /// add/store new data
        /// </summary>
        /// <param name="data">data to store</param>
        /// <returns>stream position where data was stored</returns>
        public long Add(byte[] data)
        {
            lock (this.SynchronizationRoot)
            {
                this.ThrowIfUnavailable();
                long position = this.Storage.Add(data);
                this.FlushNeeded();
                return position;
            }
        }
        /// <summary>
        /// add/store new object
        /// </summary>
        /// <param name="obj">object to store</param>
        /// <returns>stream position where object was stored</returns>
        public long Add(object? obj)
        {
            lock (this.SynchronizationRoot)
            {
                this.ThrowIfUnavailable();
                return this.Add(this.Storage.ToBytes(obj));
            }
        }

        /// <summary>
        /// update data at position
        /// </summary>
        /// <param name="pos">stream position</param>
        /// <param name="data">data to set</param>
        /// <returns></returns>
        public long Update(long pos, byte[] data)
        {
            lock (this.SynchronizationRoot)
            {
                this.ThrowIfUnavailable();
                long position = this.Storage.Update(pos, data);
                this.FlushNeeded();
                return position;
            }
        }
        /// <summary>
        /// Update object at position
        /// </summary>
        /// <param name="pos">stream position</param>
        /// <param name="obj">object to store</param>
        /// <returns></returns>
        public long Update(long pos, object? obj)
        {
            lock (this.SynchronizationRoot)
            {
                this.ThrowIfUnavailable();
                return this.Update(pos, this.Storage.ToBytes(obj));
            }
        }

        /// <summary>
        /// remove data at position
        /// </summary>
        /// <param name="pos"></param>
        /// <returns></returns>
        public long Remove(long pos)
        {
            lock (this.SynchronizationRoot)
            {
                this.ThrowIfUnavailable();
                long position = this.Storage.Remove(pos);
                this.FlushNeeded();
                return position;
            }
        }

        /// <summary>
        /// Get header value structure
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="offset"></param>
        /// <returns></returns>
        public T GetHeaderValue<T>(int offset) where T : unmanaged
        {
            lock (this.SynchronizationRoot)
            {
                this.ThrowIfUnavailable();
                return this.Storage.GetHeaderValue<T>(offset);
            }
        }

        /// <summary>
        /// Set header value
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="offset"></param>
        /// <param name="value"></param>
        public void SetHeaderValue<T>(int offset, T value) where T : unmanaged
        {
            lock (this.SynchronizationRoot)
            {
                this.ThrowIfUnavailable();
                this.Storage.SetHeaderValue(offset, value);
                this.FlushNeeded();
            }
        }

        /// <summary>
        /// Operation when direct update persistent structure
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="offset"></param>
        /// <param name="callback">callback to run</param>
        /// <returns></returns>
        public T UpdateHeaderValue<T>(int offset, DataStorage.UpdateStructureDelegate<T> callback) where T : unmanaged
        {
            lock (this.SynchronizationRoot)
            {
                this.ThrowIfUnavailable();
                T value = this.Storage.UpdateHeaderValue(offset, callback);
                this.FlushNeeded();
                return value;
            }
        }

        /// <summary>
        /// Copy entire stream into destination stream
        /// </summary>
        /// <param name="destination"></param>
        public void CopyTo(Stream destination)
        {
            lock (this.SynchronizationRoot)
            {
                this.ThrowIfUnavailable();
                this.Storage.CopyTo(destination);
            }
        }

        #region Dispose / finalize methods

        /// <summary>
        /// Stops automatic flushing and attempts to dispose the owned storage, even if it is faulted.
        /// Does not wait for timer callbacks; queued callbacks inspect disposed state under the shared gate.
        /// </summary>
        public void Dispose()
        {
            lock (this.SynchronizationRoot)
            {
                if (this.disposed != 0) return;
                this.Storage.ThrowIfInIoCallback();
                this.disposed = 1;
                List<Exception>? failures = null;
                try { this.flushTimer.Dispose(); }
                catch (Exception error) { (failures ??= []).Add(error); }
                try { this.Storage.Dispose(); }
                catch (Exception error) { (failures ??= []).Add(error); }
                if (failures?.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
                if (failures?.Count > 1) throw new AggregateException("Synchronized storage disposal failed.", failures);
            }
        }

        #endregion

        /// <summary>
        /// Enumerates DataStorage objects data 
        /// </summary>
        /// <returns></returns>
        public IEnumerator<object?> GetEnumerator()
        {
            lock (this.SynchronizationRoot)
            {
                this.ThrowIfUnavailable();
                using IEnumerator<KeyValuePair<long, object?>> items = this.Storage.ReadObjects.GetEnumerator();
                while (true)
                {
                    this.ThrowIfUnavailable();
                    if (!items.MoveNext()) yield break;
                    yield return items.Current.Value;
                }
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.GetEnumerator();

        #region System.Collections.Generic.ICollection<object> implementation

        void ICollection<object?>.Add(object? item) => this.Add(item);

        /// <summary>
        /// Clears all data storage
        /// </summary>
        public void Clear()
        {
            lock (this.SynchronizationRoot)
            {
                this.ThrowIfUnavailable();
                this.Storage.Clear();
                this.FlushNeeded();
            }
        }

        /// <summary>
        /// Returns if has some object (Search data file)
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public bool Contains(object? item)
        {
            lock (this.SynchronizationRoot)
            {
                this.ThrowIfUnavailable();
                foreach (KeyValuePair<long, object?> data in this.Storage.ReadObjects)
                    if (object.Equals(item, data.Value))
                        return true;
            }
            return false;
        }

        /// <summary>
        /// Copy all elements into an array
        /// </summary>
        /// <param name="array"></param>
        /// <param name="arrayIndex"></param>
        public void CopyTo(object?[] array, int arrayIndex)
        {
            lock (this.SynchronizationRoot)
            {
                this.ThrowIfUnavailable();
                foreach (KeyValuePair<long, object?> data in this.Storage.ReadObjects)
                    array[arrayIndex++] = data.Value;
            }
        }

        bool ICollection<object?>.IsReadOnly => false;

        bool ICollection<object?>.Remove(object? item)
        {
            lock (this.SynchronizationRoot)
            {
                this.ThrowIfUnavailable();
                foreach (KeyValuePair<long, object?> data in this.Storage.ReadObjects)
                    if (object.Equals(item, data.Value))
                    {
                        this.Remove(data.Key);
                        return true;
                    }
            }
            return false;
        }

        #endregion
    }
}

