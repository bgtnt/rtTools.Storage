using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;

namespace RtTools;

/// <summary>
/// Explicitly saves objects using their ToBinary payload and a reconstruction factory.
/// Live property changes are not saved until AddOrUpdate or Update is called.
/// PersistId is an owning-store record location that can change after Update; it is not a stable application identifier.
/// Persistence callbacks must not reenter mutations or disposal of the same wrapper.
/// </summary>
public class StreamPersistence<T> : IDisposable where T : class, IPersistentBinary
{
    private readonly Func<byte[], T> persistentFactory;
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

    /// <summary>The synchronized storage owned by this wrapper. Stored values are binary payloads.</summary>
    public SynchronizedDataStorage Storage { get; }

    /// <summary>Uses a public or non-public instance constructor accepting byte[]; an explicit factory is preferred.</summary>
    public StreamPersistence(Stream stream, Action<T>? onLoad = null, ushort headerSize = 0,
        int minDataChunk = 256, byte moveFactor = 90, uint minUnusedTruncate = 1000000,
        IDataSerializer? serializer = null, DataStorage.OnException onException = DataStorage.OnException.Default,
        DataStorage.OpenStateDelegate? listener = null)
        : this(stream, CreateConstructorFactory(), onLoad, headerSize, minDataChunk, moveFactor,
            minUnusedTruncate, serializer, onException, listener) { }

    /// <summary>
    /// Opens storage with an explicit binary factory. Factory and onLoad run during the initial scan;
    /// PersistId is assigned before onLoad. The wrapper owns stream after argument validation, including on initialization failure.
    /// Explicit recovery/clear policies may repeat callbacks or discard data; the default failure policy closes and throws.
    /// </summary>
    public StreamPersistence(Stream stream, Func<byte[], T> persistentFactory, Action<T>? onLoad = null,
        ushort headerSize = 0, int minDataChunk = 256, byte moveFactor = 90, uint minUnusedTruncate = 1000000,
        IDataSerializer? serializer = null, DataStorage.OnException onException = DataStorage.OnException.Default,
        DataStorage.OpenStateDelegate? listener = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        this.persistentFactory = persistentFactory ?? throw new ArgumentNullException(nameof(persistentFactory));
        try
        {
            void Scan(long position, byte[] data)
            {
                T persistent = this.persistentFactory(data)
                    ?? throw new InvalidDataException($"The persistent factory for '{typeof(T).FullName}' returned null.");
                persistent.PersistId = position;
                onLoad?.Invoke(persistent);
            }
            this.Storage = new SynchronizedDataStorage(new DataStorage(stream, Scan, null, headerSize,
                minDataChunk, moveFactor, minUnusedTruncate, serializer, onException, listener));
        }
        catch (Exception error)
        {
            try { stream.Dispose(); }
            catch (Exception cleanupError) { throw new AggregateException("Stream persistence initialization and cleanup failed.", error, cleanupError); }
            throw;
        }
    }

    /// <summary>Opens an owned file using an instance byte[] constructor, validated before the file is opened.</summary>
    public StreamPersistence(string fileName, Action<T>? onLoad = null, ushort headerSize = 0,
        int minDataChunk = 256, byte moveFactor = 90, uint minUnusedTruncate = 1000000,
        IDataSerializer? serializer = null, DataStorage.OnException onException = DataStorage.OnException.Default)
        : this(fileName, CreateConstructorFactory(), onLoad, headerSize, minDataChunk, moveFactor,
            minUnusedTruncate, serializer, onException) { }

    /// <summary>Opens an owned file with an explicit binary factory, validated before opening the file.</summary>
    public StreamPersistence(string fileName, Func<byte[], T> persistentFactory, Action<T>? onLoad = null,
        ushort headerSize = 0, int minDataChunk = 256, byte moveFactor = 90, uint minUnusedTruncate = 1000000,
        IDataSerializer? serializer = null, DataStorage.OnException onException = DataStorage.OnException.Default)
        : this(OpenFile(fileName, persistentFactory), persistentFactory, onLoad, headerSize, minDataChunk,
            moveFactor, minUnusedTruncate, serializer, onException) { }

    private static FileStream OpenFile(string fileName, Func<byte[], T> factory)
    {
        ArgumentNullException.ThrowIfNull(factory, "persistentFactory");
        return new FileStream(fileName, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 0x10000, FileOptions.RandomAccess);
    }

    private static Func<byte[], T> CreateConstructorFactory()
    {
        ConstructorInfo constructor = typeof(T).GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, [typeof(byte[])], modifiers: null)
            ?? throw new MissingMethodException(typeof(T).FullName, ".ctor(byte[])");
        return data => (T)constructor.Invoke([data]);
    }

    private static byte[] Payload(T persistent) => persistent.ToBinary()
        ?? throw new InvalidDataException($"The binary payload for '{typeof(T).FullName}' is null.");

    /// <summary>Adds an unpersisted object or explicitly resaves its current binary payload.</summary>
    public long AddOrUpdate(T persistent)
    {
        ArgumentNullException.ThrowIfNull(persistent);
        lock (this.Storage.SynchronizationRoot)
        {
            this.BeginOperation();
            try
            {
                byte[] data = Payload(persistent);
                return persistent.PersistId = persistent.IsPersistent
                    ? this.Storage.Update(persistent.PersistId, data) : this.Storage.Add(data);
            }
            finally { this.operationActive = false; }
        }
    }

    /// <summary>Adds an unpersisted object and assigns its store-local record location.</summary>
    public long Add(T persistent)
    {
        ArgumentNullException.ThrowIfNull(persistent);
        lock (this.Storage.SynchronizationRoot)
        {
            this.BeginOperation();
            try
            {
                if (persistent.IsPersistent) throw new ArgumentException("Object is already persisted.", nameof(persistent));
                return persistent.PersistId = this.Storage.Add(Payload(persistent));
            }
            finally { this.operationActive = false; }
        }
    }

    /// <summary>Explicitly resaves an existing object; the returned PersistId may differ after relocation.</summary>
    public long Update(T persistent)
    {
        ArgumentNullException.ThrowIfNull(persistent);
        lock (this.Storage.SynchronizationRoot)
        {
            this.BeginOperation();
            try
            {
                if (!persistent.IsPersistent) throw new ArgumentException("Object must be persisted before update.", nameof(persistent));
                return persistent.PersistId = this.Storage.Update(persistent.PersistId, Payload(persistent));
            }
            finally { this.operationActive = false; }
        }
    }

    /// <summary>Removes a stored object, resets PersistId, and returns false for an unpersisted object.</summary>
    public bool Remove(T persistent)
    {
        ArgumentNullException.ThrowIfNull(persistent);
        lock (this.Storage.SynchronizationRoot)
        {
            this.BeginOperation();
            try
            {
                if (!persistent.IsPersistent) return false;
                persistent.PersistId = this.Storage.Remove(persistent.PersistId);
                return true;
            }
            finally { this.operationActive = false; }
        }
    }

    private void BeginOperation()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
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
