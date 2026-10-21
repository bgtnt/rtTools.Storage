using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using RtTools;

namespace HistoryAcceptance;

internal static class StreamPersistenceChecks
{
    public static void Contracts(CheckContext ctx)
    {
        ctx.Run("stream-persistence.binary-lifecycle", () =>
        {
            string file = FilePath(ctx, "binary-lifecycle");
            using (var values = OpenBinary(file))
            {
                var item = new BinaryEntity(7, "alpha") { Transient = "live-only" };
                Check.Equal(values.Add(item), item.PersistId, "Add assigns the returned record location.");
                Check.That(item.IsPersistent, "Added object is persistent.");
                AssertHex("0107000000616C706861", values.Storage.GetData(item.PersistId), "Initial binary payload.");
                item.Value = 999;
                item.Name = "not-resaved";
                values.Storage.Flush();
            }

            List<BinaryEntity> loaded = [];
            long relocated;
            string grown = new('x', 300);
            using (var values = OpenBinary(file, item =>
            {
                Check.That(item.IsPersistent && item.PersistId >= DataStorage.HeaderOffset, "onLoad sees current record metadata during construction.");
                loaded.Add(item);
            }))
            {
                Check.Equal(1, loaded.Count, "Factory/onLoad load before construction returns.");
                BinaryEntity item = loaded[0];
                Check.Equal(7, item.Value, "Property mutation plus Flush did not implicitly save.");
                Check.Equal("alpha", item.Name, "Only explicit payload was saved.");
                Check.That(item.Transient == null, "Transient field was omitted.");
                Check.Equal(values.Storage.ReadPositions.Single(), item.PersistId, "Loaded metadata identifies the actual record.");
                long oldPosition = item.PersistId;
                var guard = new BinaryEntity(8, "beta");
                values.Add(guard);
                item.Value = 70;
                item.Name = grown;
                relocated = values.Update(item);
                Check.That(relocated != oldPosition, "Growing a non-tail record forces relocation in this fixture.");
                Check.Equal(relocated, item.PersistId, "Update writes the relocated ID back to the live object.");
                AssertHex("0146000000" + string.Concat(Enumerable.Repeat("78", 300)), values.Storage.GetData(relocated), "Relocated binary payload.");
                Check.That(values.Remove(guard), "Remove persisted guard.");
                Check.Equal(long.MinValue, guard.PersistId, "Remove resets ID.");
                Check.That(!guard.IsPersistent && !values.Remove(guard), "Repeated removal is false.");
                Check.Equal(1, values.Storage.Count, "Only updated entity remains.");
            }
            loaded.Clear();
            using (var values = OpenBinary(file, loaded.Add))
            {
                Check.Equal(1, loaded.Count, "Reopen record count after relocation/removal.");
                Check.Equal(relocated, loaded[0].PersistId, "Relocated ID survives reopen.");
                Check.Equal(70, loaded[0].Value, "Explicit updated scalar survives reopen.");
                Check.Equal(grown, loaded[0].Name, "Grown payload survives reopen.");
                Check.That(values.Remove(loaded[0]), "Remove remaining entity.");
            }
            using (var values = OpenBinary(file, _ => throw new InvalidDataException("Deleted entity reloaded.")))
                Check.Equal(0, values.Storage.Count, "Deletion survives final reopen.");
            return "Explicit binary factory, independent payload bytes, transient omission, explicit-save boundary, forced relocation/PersistId reassignment, removal and clean reopen verified.";
        });

        ctx.Run("stream-persistence.binary-preconditions-null-payload", () =>
        {
            using var stream = new MemoryStream();
            using var values = new StreamPersistence<BinaryEntity>(stream, BinaryEntity.Read);
            values.FlushIdleSeconds = -1;
            var item = new BinaryEntity(1, "a");
            Check.That(!values.Remove(item), "Unpersisted Remove is false.");
            Check.Throws<ArgumentException>(() => values.Update(item), "Update requires a persisted object.");
            Check.Equal(0, item.EncodeCalls, "State preconditions are checked before encoding.");
            AssertArgumentNull(() => values.Add(null!));
            AssertArgumentNull(() => values.Update(null!));
            AssertArgumentNull(() => values.Remove(null!));
            AssertArgumentNull(() => values.AddOrUpdate(null!));
            long original = values.AddOrUpdate(item);
            Check.Equal(original, item.PersistId, "AddOrUpdate add branch assigns ID.");
            item.Value = 2;
            Check.Equal(values.AddOrUpdate(item), item.PersistId, "AddOrUpdate update branch assigns current ID.");
            AssertHex("010200000061", values.Storage.GetData(item.PersistId), "AddOrUpdate persisted new scalar.");
            byte[] before = Snapshot(values, stream);
            long beforeId = item.PersistId;
            int beforeCalls = item.EncodeCalls;
            Check.Throws<ArgumentException>(() => values.Add(item), "Duplicate Add is rejected.");
            Check.Equal(beforeCalls, item.EncodeCalls, "Duplicate Add does not invoke encoder.");
            AssertUnchanged(before, Snapshot(values, stream), "Duplicate Add");
            item.ReturnNull = true;
            Check.Throws<InvalidDataException>(() => values.Update(item), "Null ToBinary update is rejected.");
            Check.Throws<InvalidDataException>(() => values.AddOrUpdate(item), "Null ToBinary existing AddOrUpdate is rejected.");
            var invalid = new BinaryEntity(3, "invalid") { ReturnNull = true };
            Check.Throws<InvalidDataException>(() => values.Add(invalid), "Null ToBinary Add is rejected.");
            Check.Throws<InvalidDataException>(() => values.AddOrUpdate(invalid), "Null ToBinary new AddOrUpdate is rejected.");
            Check.Equal(beforeId, item.PersistId, "Failed encodes preserve existing ID.");
            Check.Equal(long.MinValue, invalid.PersistId, "Failed new encodes do not assign an ID.");
            Check.Equal(1, values.Storage.Count, "Rejected mutations preserve record count.");
            AssertUnchanged(before, Snapshot(values, stream), "Null payload rejections");
            beforeCalls = item.EncodeCalls;
            Check.That(values.Remove(item), "Removal does not require serializing the current object.");
            Check.Equal(beforeCalls, item.EncodeCalls, "Remove never invokes the encoder.");
            return "Null arguments and object-state preconditions checked; AddOrUpdate both branches work; null payload errors preserve exact stream bytes/count/IDs; Remove skips encoding.";
        });

        ctx.Run("stream-persistence.constructor-factory-overloads", () =>
        {
            string file = FilePath(ctx, "private-constructor");
            using (var values = new StreamPersistence<ConstructorEntity>(file, ConstructorEntity.Read))
            {
                values.FlushIdleSeconds = -1;
                var item = new ConstructorEntity(321);
                values.Add(item);
                AssertHex("41010000", values.Storage.GetData(item.PersistId), "Independent private-constructor fixture bytes.");
            }
            List<ConstructorEntity> loaded = [];
            using (var values = new StreamPersistence<ConstructorEntity>(file, onLoad: loaded.Add))
            {
                Check.Equal(1, loaded.Count, "Non-public byte[] constructor is supported.");
                Check.Equal(321, loaded[0].Value, "Constructor-based reopen scalar.");
                Check.Equal(values.Storage.ReadPositions.Single(), loaded[0].PersistId, "Constructor-based load assigns real ID.");
            }
            using (var stream = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (var values = new StreamPersistence<ConstructorEntity>(stream, onLoad: item => Check.Equal(321, item.Value, "Stream convenience constructor.")))
                Check.Equal(1, values.Storage.Count, "Stream convenience constructor count.");
            return "Explicit-factory filename constructor and private byte[] constructor convenience overloads load the same exact payload through file and stream paths.";
        });

        ctx.Run("stream-persistence.factory-validation-before-ownership", () =>
        {
            string nullFile = FilePath(ctx, "null-factory");
            ArgumentNullException nullError = Check.Throws<ArgumentNullException>(() =>
            {
                using var failed = new StreamPersistence<BinaryEntity>(nullFile, persistentFactory: null!);
            }, "Null factory must be validated before file opening.");
            Check.Equal("persistentFactory", nullError.ParamName, "Factory argument name.");
            Check.That(!File.Exists(nullFile), "Null factory must not create a file.");
            string missingFile = FilePath(ctx, "missing-constructor");
            Check.Throws<MissingMethodException>(() =>
            {
                using var failed = new StreamPersistence<BinaryEntity>(missingFile);
            }, "Missing byte[] constructor must be validated before file opening.");
            Check.That(!File.Exists(missingFile), "Missing constructor must not create a file.");
            using var provided = new TrackingStream();
            Check.Throws<ArgumentNullException>(() =>
            {
                using var failed = new StreamPersistence<BinaryEntity>(provided, persistentFactory: null!);
            }, "Null factory on caller-provided stream.");
            Check.Throws<MissingMethodException>(() =>
            {
                using var failed = new StreamPersistence<BinaryEntity>(provided);
            }, "Missing byte[] constructor on caller-provided stream.");
            Check.That(provided.CanRead && provided.CanWrite && provided.Length == 0, "Argument validation must not take ownership or mutate caller stream.");
            Check.Equal(0, provided.DisposeCalls, "Invalid factory arguments leave caller ownership intact.");
            return "Factories/constructor existence are validated before owned file open or caller-stream ownership transfer; invalid arguments create no files and keep supplied stream open/unchanged.";
        });

        ctx.Run("stream-persistence.failed-scan-file-cleanup", () =>
        {
            string file = SeedBinaryFile(ctx, "failed-scan");
            byte[] before = File.ReadAllBytes(file);
            Check.Throws<InvalidDataException>(() =>
            {
                using var failed = new StreamPersistence<BinaryEntity>(file, _ => null!);
            }, "Factory returning null during scan.");
            AssertClosedAndUnchanged(file, before);
            var factoryError = new InvalidOperationException("factory sentinel");
            Exception actual = Check.Throws<InvalidOperationException>(() =>
            {
                using var failed = new StreamPersistence<BinaryEntity>(file, persistentFactory: _ => throw factoryError);
            }, "Factory exception during scan.");
            Check.That(ReferenceEquals(factoryError, actual), "Primary factory exception is preserved.");
            AssertClosedAndUnchanged(file, before);
            var loadError = new InvalidOperationException("onLoad sentinel");
            actual = Check.Throws<InvalidOperationException>(() =>
            {
                using var failed = new StreamPersistence<BinaryEntity>(file, BinaryEntity.Read, _ => throw loadError);
            }, "onLoad exception during scan.");
            Check.That(ReferenceEquals(loadError, actual), "Primary onLoad exception is preserved.");
            AssertClosedAndUnchanged(file, before);
            return "Default CloseAndThrow scan failures (null factory result, factory throw, onLoad throw) preserve exact valid-file bytes and release exclusive file handle immediately, without GC.";
        });

        ctx.Run("stream-persistence.failed-scan-owned-stream-cleanup", () =>
        {
            byte[] valid = File.ReadAllBytes(SeedBinaryFile(ctx, "owned-stream-failure"));
            var stream = new TrackingStream(valid);
            var failure = new InvalidOperationException("owned stream scan sentinel");
            try
            {
                Exception actual = Check.Throws<InvalidOperationException>(() =>
                {
                    using var failed = new StreamPersistence<BinaryEntity>(stream, BinaryEntity.Read, _ => throw failure,
                        onException: DataStorage.OnException.Throw);
                }, "Initialization failure owns/cleans stream even when raw DataStorage policy does not close it.");
                Check.That(ReferenceEquals(failure, actual), "Owned-stream failure identity.");
                Check.That(!stream.CanRead && !stream.CanWrite, "Accepted stream must be closed after failed initialization.");
                Check.Equal(1, stream.DisposeCalls, "Wrapper closes stream under Throw-without-CloseSource policy.");
                AssertUnchanged(valid, stream.ToArray(), "Owned-stream scan failure");
            }
            finally { stream.Dispose(); }
            return "After validation ownership transfers: scan failure closes supplied stream even with DataStorage.OnException.Throw; primary error and exact bytes preserved.";
        });

        ctx.Run("stream-persistence.object-lifecycle-onload", () =>
        {
            string file = FilePath(ctx, "object-lifecycle");
            using (var values = OpenObjects(file))
            {
                var item = new ObjectEntity { Name = "trusted", Value = 41, Transient = "not stored" };
                values.AddOrUpdate(item);
                Check.That(item.IsPersistent, "Object AddOrUpdate assigns ID.");
                item.Value = 42;
                values.Update(item);
                item.Value = 99;
                values.Storage.Flush();
            }
            List<ObjectEntity> loaded = [];
            using (var values = OpenObjects(file, persistent =>
            {
                Check.That(persistent.IsPersistent && persistent.PersistId >= DataStorage.HeaderOffset, "Non-generic onLoad observes assigned current ID.");
                loaded.Add((ObjectEntity)persistent);
            }))
            {
                Check.Equal(1, loaded.Count, "Non-generic callback collects objects during construction.");
                ObjectEntity item = loaded[0];
                Check.Equal(42, item.Value, "Explicit updated object scalar survives reopen; later RAM mutation did not resave.");
                Check.Equal("trusted", item.Name, "Default trusted serializer object field.");
                Check.That(item.Transient == null, "DataContract omits transient field.");
                Check.Equal(values.Storage.ReadPositions.Single(), item.PersistId, "Non-generic current storage ID.");
                Check.That(values.Remove(item) && !values.Remove(item), "Non-generic removal and repeated removal.");
                Check.Equal(long.MinValue, item.PersistId, "Non-generic Remove resets ID.");
            }
            using (var values = OpenObjects(file, _ => throw new InvalidDataException("Deleted object reloaded.")))
                Check.Equal(0, values.Storage.Count, "Object deletion survives reopen.");
            return "Trusted DataContract object path supports explicit save, additive onLoad with assigned ID, transient omission, removal and clean reopen; no derived-constructor hook required.";
        });

        ctx.Run("stream-persistence.object-preconditions", () =>
        {
            using var stream = new MemoryStream();
            using var values = new StreamPersistence(stream);
            values.FlushIdleSeconds = -1;
            AssertArgumentNull(() => values.Add(null!));
            AssertArgumentNull(() => values.Update(null!));
            AssertArgumentNull(() => values.Remove(null!));
            AssertArgumentNull(() => values.AddOrUpdate(null!));
            var item = new ObjectEntity { Name = "object", Value = 11 };
            Check.That(!values.Remove(item), "Non-generic unpersisted Remove.");
            Check.Throws<ArgumentException>(() => values.Update(item), "Non-generic Update precondition.");
            values.Add(item);
            long current = item.PersistId;
            values.Storage.Flush();
            byte[] before = stream.ToArray();
            Check.Throws<ArgumentException>(() => values.Add(item), "Non-generic duplicate Add.");
            values.Storage.Flush();
            AssertUnchanged(before, stream.ToArray(), "Non-generic duplicate Add");
            Check.Equal(current, item.PersistId, "Rejected object mutation preserves metadata.");
            Check.Equal(1, values.Storage.Count, "Rejected object mutation preserves count.");
            return "Non-generic null guards (including AddOrUpdate), persisted/unpersisted preconditions and byte-identical rejection verified.";
        });

        ctx.Run("stream-persistence.object-failed-scan-cleanup", () =>
        {
            string file = FilePath(ctx, "object-scan-failure");
            using (var values = OpenObjects(file)) values.Add(new ObjectEntity { Name = "seed", Value = 12 });
            byte[] valid = File.ReadAllBytes(file);
            var stream = new TrackingStream(valid);
            var failure = new InvalidOperationException("object onLoad sentinel");
            try
            {
                Exception actual = Check.Throws<InvalidOperationException>(() =>
                {
                    using var failed = new StreamPersistence(stream, onException: DataStorage.OnException.Throw, onLoad: _ => throw failure);
                }, "Non-generic onLoad failure closes owned stream.");
                Check.That(ReferenceEquals(failure, actual), "Non-generic primary callback error.");
                Check.That(!stream.CanRead && !stream.CanWrite, "Non-generic failed construction releases stream.");
                AssertUnchanged(valid, stream.ToArray(), "Non-generic failed construction");
            }
            finally { stream.Dispose(); }
            return "Non-generic onLoad failure preserves its original exception and valid bytes while closing accepted caller stream under Throw-only policy.";
        });

        ctx.Run("stream-persistence.empty-binary-payload", () =>
        {
            string file = FilePath(ctx, "empty-payload");
            static EmptyEntity Read(byte[] data)
            {
                Check.Equal(0, data.Length, "Empty binary payload remains a valid non-null byte array.");
                return new EmptyEntity();
            }
            using (var values = new StreamPersistence<EmptyEntity>(file, Read))
            {
                values.FlushIdleSeconds = -1;
                var item = new EmptyEntity();
                values.Add(item);
                Check.Equal(0, values.Storage.GetData(item.PersistId).Length, "Stored empty payload length.");
            }
            List<EmptyEntity> loaded = [];
            using (var values = new StreamPersistence<EmptyEntity>(file, Read, loaded.Add))
            {
                Check.Equal(1, loaded.Count, "Empty-payload entity reloads.");
                Check.Equal(values.Storage.ReadPositions.Single(), loaded[0].PersistId, "Empty-payload current ID.");
            }
            return "Empty ToBinary byte[] is valid and reloads via explicit factory; null payload rejection does not reject zero-length payloads.";
        });

        ctx.Run("stream-persistence.disposal-ownership", () =>
        {
            var binaryStream = new TrackingStream();
            var binary = new StreamPersistence<BinaryEntity>(binaryStream, BinaryEntity.Read);
            binary.FlushIdleSeconds = -1;
            binary.Dispose();
            binary.Dispose();
            Check.That(!binaryStream.CanRead && !binaryStream.CanWrite, "Binary wrapper owns supplied stream after successful construction.");
            Check.Equal(1, binaryStream.DisposeCalls, "Repeated binary wrapper Dispose is inert.");
            var item = new BinaryEntity(1, "x");
            Check.Throws<ObjectDisposedException>(() => binary.Add(item), "Disposed binary Add.");
            Check.Throws<ObjectDisposedException>(() => binary.Update(item), "Disposed binary Update.");
            Check.Throws<ObjectDisposedException>(() => binary.Remove(item), "Disposed binary Remove.");
            Check.Throws<ObjectDisposedException>(() => binary.AddOrUpdate(item), "Disposed binary AddOrUpdate.");
            var objectStream = new TrackingStream();
            var objects = new StreamPersistence(objectStream);
            objects.Dispose();
            objects.Dispose();
            Check.That(!objectStream.CanRead && !objectStream.CanWrite, "Object wrapper owns supplied stream.");
            Check.Equal(1, objectStream.DisposeCalls, "Repeated object wrapper Dispose is inert.");
            var obj = new ObjectEntity();
            Check.Throws<ObjectDisposedException>(() => objects.Add(obj), "Disposed object Add.");
            Check.Throws<ObjectDisposedException>(() => objects.Update(obj), "Disposed object Update.");
            Check.Throws<ObjectDisposedException>(() => objects.Remove(obj), "Disposed object Remove.");
            Check.Throws<ObjectDisposedException>(() => objects.AddOrUpdate(obj), "Disposed object AddOrUpdate.");
            return "Both wrappers own accepted streams, dispose once, and reject all mutating APIs after disposal; ownership is distinct from argument-validation failure.";
        });

        ctx.Run("stream-persistence.binary-callback-reentrancy", () =>
        {
            using var stream = new MemoryStream();
            using var values = new StreamPersistence<BinaryEntity>(stream, BinaryEntity.Read);
            values.FlushIdleSeconds = -1;
            var item = new BinaryEntity(7, "alpha");
            values.Add(item);
            long id = item.PersistId;
            byte[] before = Snapshot(values, stream);
            Action[] nested = [() => values.Add(item), () => values.Update(item), () => values.AddOrUpdate(item),
                () => values.Remove(item), values.Dispose];
            foreach (Action callback in nested)
            {
                item.BeforeEncode = callback;
                try { AssertReentrantRejected(() => values.Update(item)); }
                finally { item.BeforeEncode = null; }
                Check.Equal(id, item.PersistId, "Rejected encoder reentry preserves live metadata.");
                Check.Equal(1, values.Storage.Count, "Rejected encoder reentry preserves record count.");
                AssertUnchanged(before, Snapshot(values, stream), "Rejected encoder reentry");
            }
            item.Value = 8;
            values.Update(item);
            AssertHex("0108000000616C706861", values.Storage.GetData(item.PersistId), "A later valid mutation succeeds after every guard rejection.");
            return "ToBinary cannot reenter Add/Update/AddOrUpdate/Remove or Dispose on the same wrapper; exact InvalidOperationException precedes byte/ID/count changes; wrapper remains usable afterward.";
        });

        ctx.Run("stream-persistence.object-serializer-reentrancy", () =>
        {
            using var stream = new MemoryStream();
            var serializer = new CallbackSerializer();
            using var values = new StreamPersistence(stream, serializer: serializer);
            values.FlushIdleSeconds = -1;
            var item = new ObjectEntity { Name = "guarded", Value = 21 };
            values.Add(item);
            long id = item.PersistId;
            values.Storage.Flush();
            byte[] before = stream.ToArray();
            Action[] nested = [() => values.Add(item), () => values.Update(item), () => values.AddOrUpdate(item),
                () => values.Remove(item), values.Dispose];
            foreach (Action callback in nested)
            {
                serializer.BeforeSerialize = callback;
                try { AssertReentrantRejected(() => values.Update(item)); }
                finally { serializer.BeforeSerialize = null; }
                Check.Equal(id, item.PersistId, "Rejected serializer reentry preserves live metadata.");
                Check.Equal(1, values.Storage.Count, "Rejected serializer reentry preserves record count.");
                values.Storage.Flush();
                AssertUnchanged(before, stream.ToArray(), "Rejected serializer reentry");
            }
            item.Value = 22;
            values.Update(item);
            var loaded = (ObjectEntity)(values.Storage[item.PersistId] ?? throw new InvalidDataException("Missing valid post-reentry object."));
            Check.Equal(22, loaded.Value, "A later valid object mutation succeeds after guard rejection.");
            return "Supplied object serializer cannot reenter mutation/disposal of its wrapper; exact InvalidOperationException preserves stream bytes/count/ID, and later normal serialization succeeds.";
        });
    }

    private static StreamPersistence<BinaryEntity> OpenBinary(string file, Action<BinaryEntity>? onLoad = null)
    {
        var result = new StreamPersistence<BinaryEntity>(file, BinaryEntity.Read, onLoad, minDataChunk: 32, moveFactor: 100, minUnusedTruncate: 64);
        result.FlushIdleSeconds = -1;
        return result;
    }

    private static StreamPersistence OpenObjects(string file, Action<IPersistent>? onLoad = null)
    {
        var result = new StreamPersistence(File.Open(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), onLoad: onLoad);
        result.FlushIdleSeconds = -1;
        return result;
    }

    private static string SeedBinaryFile(CheckContext ctx, string name)
    {
        string file = FilePath(ctx, name);
        using (var values = OpenBinary(file)) values.Add(new BinaryEntity(9, "seed"));
        return file;
    }

    private static string FilePath(CheckContext ctx, string name)
    {
        string directory = Path.Combine(ctx.DataRoot, "stream-persistence", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "state.bin");
    }

    private static byte[] Snapshot(StreamPersistence<BinaryEntity> values, MemoryStream stream)
    {
        values.Storage.Flush();
        return stream.ToArray();
    }

    private static void AssertClosedAndUnchanged(string file, byte[] expected)
    {
        using (var exclusive = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check.Equal((long)expected.Length, exclusive.Length, "Failed constructor releases file immediately.");
        AssertUnchanged(expected, File.ReadAllBytes(file), "Default-policy failed scan");
    }

    private static void AssertArgumentNull(Action action)
    {
        ArgumentNullException error = Check.Throws<ArgumentNullException>(action, "Null persistent argument.");
        Check.Equal("persistent", error.ParamName, "Persistent argument name.");
    }

    private static void AssertReentrantRejected(Action action)
    {
        InvalidOperationException error = Check.Throws<InvalidOperationException>(action, "Same-wrapper persistence callback reentry must be rejected.");
        Check.Equal(typeof(InvalidOperationException), error.GetType(), "Reentry rejection is the exact guard exception, not a later disposed-stream error.");
    }

    private static void AssertHex(string expected, byte[] actual, string message) => Check.Equal(expected, Convert.ToHexString(actual), message);
    private static void AssertUnchanged(byte[] expected, byte[] actual, string message) => Check.That(expected.AsSpan().SequenceEqual(actual), message + ": exact bytes unchanged.");

    private sealed class BinaryEntity(int value, string name) : IPersistentBinary
    {
        public int Value { get; set; } = value;
        public string Name { get; set; } = name;
        public string? Transient { get; set; }
        public bool ReturnNull { get; set; }
        public Action? BeforeEncode { get; set; }
        public int EncodeCalls { get; private set; }
        public long PersistId { get; set; } = long.MinValue;
        public bool IsPersistent => this.PersistId >= 0;
        public byte[] ToBinary()
        {
            this.EncodeCalls++;
            this.BeforeEncode?.Invoke();
            if (this.ReturnNull) return null!;
            byte[] text = Encoding.UTF8.GetBytes(this.Name);
            byte[] bytes = new byte[5 + text.Length];
            bytes[0] = 1;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), this.Value);
            text.CopyTo(bytes, 5);
            return bytes;
        }
        public static BinaryEntity Read(byte[] bytes)
        {
            if (bytes.Length < 5 || bytes[0] != 1) throw new InvalidDataException("Unsupported test binary payload.");
            return new(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(1)), Encoding.UTF8.GetString(bytes, 5, bytes.Length - 5));
        }
    }

    private sealed class ConstructorEntity : IPersistentBinary
    {
        public int Value { get; }
        public long PersistId { get; set; } = long.MinValue;
        public bool IsPersistent => this.PersistId >= 0;
        public ConstructorEntity(int value) => this.Value = value;
        private ConstructorEntity(byte[] bytes)
        {
            if (bytes.Length != 4) throw new InvalidDataException("Invalid constructor payload.");
            this.Value = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        }
        public static ConstructorEntity Read(byte[] bytes) => new(bytes);
        public byte[] ToBinary()
        {
            byte[] bytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, this.Value);
            return bytes;
        }
    }

    [DataContract]
    private sealed class ObjectEntity : IPersistent
    {
        [DataMember(Order = 1)] public string Name { get; set; } = "";
        [DataMember(Order = 2)] public int Value { get; set; }
        [IgnoreDataMember] public string? Transient { get; set; }
        [IgnoreDataMember] public long PersistId { get; set; } = long.MinValue;
        [IgnoreDataMember] public bool IsPersistent => this.PersistId >= 0;
    }

    private sealed class EmptyEntity : IPersistentBinary
    {
        public long PersistId { get; set; } = long.MinValue;
        public bool IsPersistent => this.PersistId >= 0;
        public byte[] ToBinary() => [];
    }

    private sealed class TrackingStream : MemoryStream
    {
        public int DisposeCalls { get; private set; }
        public TrackingStream() { }
        public TrackingStream(byte[] bytes) : base((byte[])bytes.Clone()) { }
        protected override void Dispose(bool disposing)
        {
            if (disposing) this.DisposeCalls++;
            base.Dispose(disposing);
        }
    }

    private sealed class CallbackSerializer : IDataSerializer
    {
        public Action? BeforeSerialize { get; set; }
        public void Serialize(Stream serializationStream, object? graph)
        {
            this.BeforeSerialize?.Invoke();
            DataContractDataSerializer.Default.Serialize(serializationStream, graph);
        }
        public object? Deserialize(Stream serializationStream) => DataContractDataSerializer.Default.Deserialize(serializationStream);
    }
}
