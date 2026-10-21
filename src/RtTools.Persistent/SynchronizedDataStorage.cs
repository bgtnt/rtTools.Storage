using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.ComponentModel;
using System.Diagnostics;

namespace RtTools
{
    /// <summary>
    /// Persistent storage wrapper around <see cref="DataStorage"/> with a shared synchronization object
    /// and idle auto-flush support.
    /// </summary>
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
                    return this.Storage.Count;
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
                    return [.. this.Storage.ReadPositions];
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
                    return [.. this.Storage.ReadData];
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
                    return [.. this.Storage.ReadObjects];
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
            this.flushTimer = new Timer(this.FlushTimerCallback);
            this.Storage = storage;
        }

        internal void FlushNeeded()
        {
            if (this.FlushIdleMilliseconds == 0 || Interlocked.Increment(ref this.unflushedUpdates) >= this.FlushUpdates)
                this.FlushTimerCallback(null);
            else
                try { this.flushTimer.Change(this.FlushIdleMilliseconds > 0 ? this.FlushIdleMilliseconds : Timeout.Infinite, Timeout.Infinite); }
                catch (ObjectDisposedException) { } // if timer is disposed - it is OK
        }
        private int unflushedUpdates = 0;

        private void FlushTimerCallback(object? stateInfo)
        {
            try
            {
                this.Flush();
                Interlocked.Exchange(ref this.unflushedUpdates, 0);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        /// <summary>
        /// Flush internal stream
        /// </summary>
        public void Flush() { lock (this.SynchronizationRoot) this.Storage.Flush(); }

        /// <summary>
        /// Get element at position
        /// </summary>
        /// <param name="pos"></param>
        /// <returns></returns>
        public object? this[long pos] => this.Storage.FromBytes(this.GetData(pos));

        /// <summary>
        /// Get data at position
        /// </summary>
        /// <param name="pos"></param>
        /// <returns></returns>
        public byte[] GetData(long pos)
        {
            lock (this.SynchronizationRoot)
                return this.Storage.GetData(pos);
        }
        /// <summary>
        /// add/store new data
        /// </summary>
        /// <param name="data">data to store</param>
        /// <returns>stream position where data was stored</returns>
        public long Add(byte[] data)
        {
            try { lock (this.SynchronizationRoot) return this.Storage.Add(data); }
            finally { this.FlushNeeded(); }
        }
        /// <summary>
        /// add/store new object
        /// </summary>
        /// <param name="obj">object to store</param>
        /// <returns>stream position where object was stored</returns>
        public long Add(object? obj) => this.Add(this.Storage.ToBytes(obj));

        /// <summary>
        /// update data at position
        /// </summary>
        /// <param name="pos">stream position</param>
        /// <param name="data">data to set</param>
        /// <returns></returns>
        public long Update(long pos, byte[] data)
        {
            try { lock (this.SynchronizationRoot) return this.Storage.Update(pos, data); }
            finally { this.FlushNeeded(); }
        }
        /// <summary>
        /// Update object at position
        /// </summary>
        /// <param name="pos">stream position</param>
        /// <param name="obj">object to store</param>
        /// <returns></returns>
        public long Update(long pos, object? obj) => this.Update(pos, this.Storage.ToBytes(obj));

        /// <summary>
        /// remove data at position
        /// </summary>
        /// <param name="pos"></param>
        /// <returns></returns>
        public long Remove(long pos)
        {
            try { lock (this.SynchronizationRoot) return this.Storage.Remove(pos); }
            finally { this.FlushNeeded(); }
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
                return this.Storage.GetHeaderValue<T>(offset);
        }

        /// <summary>
        /// Set header value
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="offset"></param>
        /// <param name="value"></param>
        public void SetHeaderValue<T>(int offset, T value) where T : unmanaged
        {
            try { lock (this.SynchronizationRoot) this.Storage.SetHeaderValue(offset, value); }
            finally { this.FlushNeeded(); }
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
            try { lock (this.SynchronizationRoot) return this.Storage.UpdateHeaderValue(offset, callback); }
            finally { this.FlushNeeded(); }
        }

        /// <summary>
        /// Copy entire stream into destination stream
        /// </summary>
        /// <param name="destination"></param>
        public void CopyTo(Stream destination)
        {
            lock (this.SynchronizationRoot)
                this.Storage.CopyTo(destination);
        }

        #region Dispose / finalize methods

        /// <summary>
        /// Disposes storage resources
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) != 0)
                return;

            this.flushTimer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            lock (this.SynchronizationRoot)
                this.Storage.Dispose();
        }

        #endregion

        /// <summary>
        /// Enumerates DataStorage objects data 
        /// </summary>
        /// <returns></returns>
        public IEnumerator<object?> GetEnumerator()
        {
            lock (this.SynchronizationRoot)
                foreach (KeyValuePair<long, object?> item in this.Storage.ReadObjects)
                    yield return item.Value;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.GetEnumerator();

        #region System.Collections.Generic.ICollection<object> implementation

        void ICollection<object?>.Add(object? item) => this.Add(item);

        /// <summary>
        /// Clears all data storage
        /// </summary>
        public void Clear()
        {
            try { lock (this.SynchronizationRoot) this.Storage.Clear(); }
            finally { this.FlushNeeded(); }
        }

        /// <summary>
        /// Returns if has some object (Search data file)
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public bool Contains(object? item)
        {
            lock (this.SynchronizationRoot)
                foreach (KeyValuePair<long, object?> data in this.Storage.ReadObjects)
                    if (object.Equals(item, data.Value))
                        return true;
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
                foreach (KeyValuePair<long, object?> data in this.Storage.ReadObjects)
                    array[arrayIndex++] = data.Value;
        }

        bool ICollection<object?>.IsReadOnly => false;

        bool ICollection<object?>.Remove(object? item)
        {
            lock (this.SynchronizationRoot)
                foreach (KeyValuePair<long, object?> data in this.Storage.ReadObjects)
                    if (object.Equals(item, data.Value))
                    {
                        this.Remove(data.Key);
                        return true;
                    }
            return false;
        }

        #endregion
    }
}

