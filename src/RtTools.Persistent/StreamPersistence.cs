using System;
using System.ComponentModel;
using System.IO;

namespace RtTools;

/// <summary>
/// Explicitly saves mutable objects through their store-local PersistId. Property changes alone are not saved.
/// The default serializer stores supported runtime object graphs and type metadata; use trusted storage only.
/// Persistence callbacks must not reenter mutations or disposal of the same wrapper.
/// </summary>
public class StreamPersistence : IDisposable
{
    private bool disposed;
    private bool operationActive;

    /// <summary>Idle time in seconds before the underlying storage requests an automatic flush.</summary>
    [Browsable(false)]
    [Description("Idle time /seconds/ to auto flush file"), Category("Storage")]
    public int FlushIdleSeconds
    {
        get => this.Storage.FlushIdleSeconds;
        set => this.Storage.FlushIdleSeconds = value;
    }

    /// <summary>The synchronized storage owned by this wrapper.</summary>
    public SynchronizedDataStorage Storage { get; }

    /// <summary>
    /// Opens storage, taking ownership of stream once argument validation succeeds.
    /// Loaded objects receive their current PersistId before onLoad runs during construction.
    /// Default serialization is for trusted object graphs; binary payload control is available through StreamPersistence&lt;T&gt;.
    /// Explicit recovery/clear policies may repeat callbacks or discard data; the default failure policy closes and throws.
    /// </summary>
    public StreamPersistence(Stream stream, ushort headerSize = 0, int minDataChunk = 256, byte moveFactor = 90,
        uint minUnusedTruncate = 1000000, IDataSerializer? serializer = null,
        DataStorage.OnException onException = DataStorage.OnException.Default,
        DataStorage.OpenStateDelegate? listener = null, Action<IPersistent>? onLoad = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var sourceCleanup = new DataStorage.SourceCleanup(false);
        try
        {
            void Scan(long position, object? value)
            {
                this.OnScanObject(position, value);
                if (value is IPersistent persistent)
                {
                    persistent.PersistId = position;
                    onLoad?.Invoke(persistent);
                }
            }
            this.Storage = new SynchronizedDataStorage(new DataStorage(sourceCleanup, stream, null, Scan, headerSize,
                minDataChunk, moveFactor, minUnusedTruncate, serializer, onException, listener));
        }
        catch (Exception error)
        {
            if (sourceCleanup.TryBeginClose())
            {
                try { stream.Dispose(); }
                catch (Exception cleanupError) { throw new AggregateException("Stream persistence initialization and cleanup failed.", error, cleanupError); }
            }
            throw;
        }
    }

    /// <summary>
    /// Called during the initial scan, including from the base constructor before derived initialization completes.
    /// Use the onLoad constructor callback to collect loaded objects without overriding this hook.
    /// </summary>
    protected virtual void OnScanObject(long pos, object? obj)
    {
        if (obj is IPersistent persistent) persistent.PersistId = pos;
    }

    /// <summary>Adds an unpersisted object or explicitly resaves its current payload.</summary>
    public long AddOrUpdate(IPersistent persistent)
    {
        ArgumentNullException.ThrowIfNull(persistent);
        lock (this.Storage.SynchronizationRoot)
        {
            this.BeginOperation();
            try
            {
                bool isPersistent = persistent.IsPersistent;
                this.Storage.ThrowIfUnavailable();
                return persistent.PersistId = isPersistent
                    ? this.Storage.Update(persistent.PersistId, persistent) : this.Storage.Add(persistent);
            }
            finally { this.operationActive = false; }
        }
    }

    /// <summary>Adds an unpersisted object and assigns its store-local record location.</summary>
    public long Add(IPersistent persistent)
    {
        ArgumentNullException.ThrowIfNull(persistent);
        lock (this.Storage.SynchronizationRoot)
        {
            this.BeginOperation();
            try
            {
                bool isPersistent = persistent.IsPersistent;
                this.Storage.ThrowIfUnavailable();
                if (isPersistent) throw new ArgumentException("Object is already persisted.", nameof(persistent));
                return persistent.PersistId = this.Storage.Add(persistent);
            }
            finally { this.operationActive = false; }
        }
    }

    /// <summary>Explicitly resaves an existing object; the returned PersistId may differ after relocation.</summary>
    public long Update(IPersistent persistent)
    {
        ArgumentNullException.ThrowIfNull(persistent);
        lock (this.Storage.SynchronizationRoot)
        {
            this.BeginOperation();
            try
            {
                bool isPersistent = persistent.IsPersistent;
                this.Storage.ThrowIfUnavailable();
                if (!isPersistent) throw new ArgumentException("Object must be persisted before update.", nameof(persistent));
                return persistent.PersistId = this.Storage.Update(persistent.PersistId, persistent);
            }
            finally { this.operationActive = false; }
        }
    }

    /// <summary>Removes a stored object, resets PersistId, and returns false for an unpersisted object.</summary>
    public bool Remove(IPersistent persistent)
    {
        ArgumentNullException.ThrowIfNull(persistent);
        lock (this.Storage.SynchronizationRoot)
        {
            this.BeginOperation();
            try
            {
                bool isPersistent = persistent.IsPersistent;
                this.Storage.ThrowIfUnavailable();
                if (!isPersistent) return false;
                persistent.PersistId = this.Storage.Remove(persistent.PersistId);
                return true;
            }
            finally { this.operationActive = false; }
        }
    }

    private void BeginOperation()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        this.Storage.ThrowIfUnavailable();
        if (this.operationActive) throw new InvalidOperationException("Persistence callbacks cannot reenter mutations of the same wrapper.");
        this.operationActive = true;
    }

    /// <summary>Disposes the owned storage. No storage lock is held while its timer is joined.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing) return;
        lock (this.Storage.SynchronizationRoot)
        {
            if (this.operationActive) throw new InvalidOperationException("Persistence callbacks cannot dispose the same wrapper.");
            if (this.disposed) return;
            this.disposed = true;
        }
        this.Storage.Dispose();
    }

    /// <summary>Disposes the owned storage and its stream.</summary>
    public void Dispose()
    {
        this.Dispose(true);
        GC.SuppressFinalize(this);
    }
}
