using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace RtTools.Logger
{
    /// <summary>
    /// Combines persistent current values with explicitly recorded timestamped snapshots.
    /// Updating <see cref="Values"/> does not automatically append history.
    /// </summary>
    public class DictionaryLogger<TKey, TValue> : IDisposable where TKey : notnull
    {
        /// <summary>Current values, persisted through dictionary mutations.</summary>
        public readonly DictionaryPersistent<TKey, TValue> Values;

        /// <summary>Explicit timestamped snapshots. Flush this log before querying pending writes.</summary>
        public readonly TimeLog<SerializableKeyValuePair<TKey, TValue>[]> Log;

        private int disposed;
        private readonly object operationGate = new();
        private bool operationActive;

        /// <summary>Opens current values and snapshot history with the default serializers.</summary>
        public DictionaryLogger(string persistent, string logger)
            : this(persistent, logger, null, null, null, null) { }

        /// <summary>
        /// Opens current values and snapshot history. Supply each reader/writer pair together;
        /// omitted pairs use the default serializer. State and history files are separate stores.
        /// Wrapper operations serialize; their callbacks must not re-enter the same wrapper.
        /// Direct access through Values or Log bypasses this gate and requires caller coordination.
        /// </summary>
        public DictionaryLogger(string persistent, string logger,
            Converter<byte[], KeyValuePair<TKey, TValue>>? stateReader = null,
            Converter<KeyValuePair<TKey, TValue>, byte[]>? stateWriter = null,
            Converter<byte[], SerializableKeyValuePair<TKey, TValue>[]>? historyReader = null,
            Converter<SerializableKeyValuePair<TKey, TValue>[], byte[]>? historyWriter = null)
        {
            if ((stateReader == null) != (stateWriter == null))
                throw new ArgumentException("stateReader and stateWriter must be supplied together.");
            if ((historyReader == null) != (historyWriter == null))
                throw new ArgumentException("historyReader and historyWriter must be supplied together.");

            FileStream stateStream = File.Open(persistent, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            DictionaryPersistent<TKey, TValue>? values = null;
            try
            {
                values = new DictionaryPersistent<TKey, TValue>(stateStream, reader: stateReader, writer: stateWriter);
                var log = new TimeLog<SerializableKeyValuePair<TKey, TValue>[]>(logger, reader: historyReader, writer: historyWriter);
                this.Values = values;
                this.Log = log;
            }
            catch
            {
                // Preserve the construction error while releasing every resource acquired here.
                try { values?.Dispose(); } catch { }
                try { stateStream.Dispose(); } catch { }
                throw;
            }
        }

        /// <summary>
        /// Updates an existing value and returns true, or inserts <paramref name="ifNotFound"/>
        /// and returns false. The read/convert/write operation holds the dictionary lock.
        /// This does not record a history snapshot.
        /// </summary>
        public bool Update(TKey key, Converter<TValue, TValue> updateIfFound, TValue ifNotFound)
        {
            this.enterOperation();
            try
            {
                lock (this.Values.Storage.SynchronizationRoot)
                {
                    if (this.Values.TryGetValue(key, out TValue? value))
                    {
                        this.Values[key] = updateIfFound(value!);
                        return true;
                    }
                    this.Values[key] = ifNotFound;
                    return false;
                }
            }
            finally { this.exitOperation(); }
        }

        /// <summary>
        /// Copies current key/value entries and appends an explicit snapshot, optionally transformed.
        /// Returns null when the converter suppresses it or the TimeLog timestamp is rejected.
        /// Values are shallow references; callers must synchronize mutations of referenced objects.
        /// This method does not flush either store or create a transaction between them.
        /// </summary>
        public SerializableKeyValuePair<TKey, TValue>[]? LogValues(
            Converter<SerializableKeyValuePair<TKey, TValue>[], SerializableKeyValuePair<TKey, TValue>[]>? convert = null)
        {
            this.enterOperation();
            try
            {
                KeyValuePair<TKey, TValue>[] values;
                lock (this.Values.Storage.SynchronizationRoot)
                {
                    values = new KeyValuePair<TKey, TValue>[this.Values.Count];
                    ((ICollection<KeyValuePair<TKey, TValue>>)this.Values).CopyTo(values, 0);
                }

                SerializableKeyValuePair<TKey, TValue>[] snapshot = Array.ConvertAll(values,
                    pair => new SerializableKeyValuePair<TKey, TValue> { Key = pair.Key, Value = pair.Value });
                if (convert != null)
                {
                    // Application conversion runs outside the dictionary storage lock.
                    snapshot = convert(snapshot);
                }
                lock (this.Values.Storage.SynchronizationRoot)
                    this.Values.Storage.ThrowIfUnavailable();
                if (snapshot == null) return null;
                return this.Log.Append(snapshot) ? snapshot : null;
            }
            finally { this.exitOperation(); }
        }

        /// <summary>
        /// Queries recorded snapshots over an inclusive time interval. This does not flush pending
        /// writes; call <see cref="Log"/>.Flush explicitly when immediate visibility is required.
        /// </summary>
        public KeyValuePair<DateTime, SerializableKeyValuePair<TKey, TValue>[]>[] GetLogged(DateTime from, DateTime to)
        {
            this.enterOperation();
            try
            {
                using TimeLogClient<SerializableKeyValuePair<TKey, TValue>[]> client = this.Log.CreateClient();
                return client.FindAll(from, to);
            }
            finally { this.exitOperation(); }
        }

        private void throwIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposed) != 0, this);

        private void enterOperation()
        {
            Monitor.Enter(this.operationGate);
            try
            {
                this.throwIfDisposed();
                this.rejectReentry();
                lock (this.Values.Storage.SynchronizationRoot)
                    this.Values.Storage.ThrowIfUnavailable();
                this.operationActive = true;
            }
            catch { Monitor.Exit(this.operationGate); throw; }
        }

        private void exitOperation()
        {
            this.operationActive = false;
            Monitor.Exit(this.operationGate);
        }

        private void rejectReentry()
        {
            if (this.operationActive)
                throw new InvalidOperationException("A dictionary logger callback cannot re-enter the same wrapper.");
        }

        /// <summary>
        /// Attempts to dispose both stores once. A single failure is rethrown unchanged;
        /// failures from both stores are aggregated in Values, Log order.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;
            lock (this.operationGate)
            {
                this.rejectReentry();
                if (Interlocked.Exchange(ref this.disposed, 1) != 0) return;
            }
            // Release the operation gate before either component waits for its timer callbacks.
            List<Exception>? failures = null;
            try { this.Values.Dispose(); } catch (Exception error) { (failures ??= []).Add(error); }
            try { this.Log.Dispose(); } catch (Exception error) { (failures ??= []).Add(error); }
            if (failures?.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures?.Count > 1) throw new AggregateException("Dictionary logger disposal failed.", failures);
        }

        /// <summary>Disposes both stores.</summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
