using System.Buffers.Binary;
using System.Reflection;
using RtTools;
using RtTools.Logger;

namespace HistoryAcceptance;

internal static class WrapperStorageFailureChecks
{
    private enum WrapperKind { Dictionary, List, Queue, ObjectPersistence, BinaryPersistence }

    // Only disposable memory-backed probes and unique bridge scratch files are used.
    // New diagnostics are accessed by string-name reflection so this source also compiles against preview.3.
    public static void BaselineContracts(CheckContext ctx)
    {
        foreach (WrapperKind kind in Enum.GetValues<WrapperKind>())
        {
            ctx.Run("wrapper-storage.pre-codec-guard." + Name(kind), () =>
            {
                using var fixture = new Fixture(kind);
                fixture.Add(1);
                var failure = new IOException("controlled prior stream failure");
                Fault(fixture.Storage, fixture.Probe, failure);
                int encodes = fixture.EncoderCalls;
                int stateCallbacks = fixture.StateCallbacks;
                foreach (Action mutation in fixture.Mutations()) Blocked(mutation, failure);
                Check.Equal(encodes, fixture.EncoderCalls, "Faulted mutation entry must reject before invoking an encoder.");
                Check.Equal(stateCallbacks, fixture.StateCallbacks, "Faulted mutation entry must reject before IPersistent state callbacks.");
                Check.Equal(0, fixture.Enumerations, "Faulted list range mutations must reject before enumerating user input.");
                AssertFault(fixture.Storage, failure);
                return "After a synthetic stream Flush failure, mutations and missing-item/empty-range no-ops reject with the original cause before codecs, state callbacks or user enumeration; disposal remains available.";
            });

            ctx.Run("wrapper-storage.codec-catches-stream-fault." + Name(kind), () =>
            {
                using var fixture = new Fixture(kind);
                fixture.Add(1);
                byte[] before = fixture.Probe.Bytes;
                long seedId = fixture.SeedEntity?.PersistId ?? long.MinValue;
                var failure = new IOException("controlled stream failure caught inside encoder");
                fixture.BeforeEncode = () => Fault(fixture.Storage, fixture.Probe, failure);
                Blocked(() => fixture.Add(2), failure);
                Same(before, fixture.Probe.Bytes, "A codec that catches the physical failure must not enable a later write.");
                Check.Equal(1, fixture.Storage.Storage.Count, "Caught encoder-side stream failure must not publish another storage record.");
                fixture.AssertResident(1, 1);
                fixture.AssertRejectedEntity(seedId);
                AssertFault(fixture.Storage, failure);
                return "An encoder that catches a real injected stream exception and returns bytes cannot hide the sticky storage fault or publish a second record/resident item.";
            });
        }

        ctx.Run("wrapper-storage.bridge-faulted-entry", () =>
        {
            using var fixture = new BridgeFixture(ctx, "entry");
            var failure = new IOException("controlled bridge state stream failure");
            Fault(fixture.Logger.Values.Storage, fixture.Probe, failure);
            int callbacks = 0;
            Blocked(() => fixture.Logger.LogValues(snapshot => { callbacks++; return snapshot; }), failure);
            Blocked(() => fixture.Logger.Update(1, value => { callbacks++; return value + 1; }, 0), failure);
            Blocked(() => fixture.Logger.GetLogged(DateTime.UnixEpoch, DateTime.UtcNow.AddDays(1)), failure);
            Check.Equal(0, callbacks, "Faulted state must reject bridge callbacks before invoking application code.");
            Check.Equal(0L, fixture.Logger.Log.Count, "The bridge must not append stale state to its separate healthy history.");
            Check.Equal(0, fixture.HistoryEncodes, "The history encoder must not run for a faulted bridge.");
            return "Faulted current-state storage blocks bridge Update, LogValues and GetLogged before callbacks/history append; direct history remains a separate accessible store.";
        });

        foreach (bool suppress in new[] { false, true })
        {
            ctx.Run("wrapper-storage.bridge-converter-catches-fault." + (suppress ? "null" : "snapshot"), () =>
            {
                using var fixture = new BridgeFixture(ctx, "converter-" + suppress);
                var failure = new IOException("controlled state stream failure caught inside snapshot converter");
                int calls = 0;
                Blocked(() => fixture.Logger.LogValues(snapshot =>
                {
                    calls++;
                    Fault(fixture.Logger.Values.Storage, fixture.Probe, failure);
                    return suppress ? null! : snapshot;
                }), failure);
                Check.Equal(1, calls, "The initially healthy converter must run once.");
                Check.Equal(0L, fixture.Logger.Log.Count, "A converter must not append a stale snapshot after catching a state-storage fault.");
                Check.Equal(0, fixture.HistoryEncodes, "The post-converter availability check must precede history encoding.");
                return "A snapshot converter that catches its state stream failure cannot append the snapshot or mask the fault with null suppression.";
            });
        }

        ctx.Run("wrapper-storage.list-input-catches-fault-empty", () =>
        {
            using var fixture = new Fixture(WrapperKind.List);
            var failure = new IOException("controlled stream failure caught by empty range input");
            IEnumerable<int> Input()
            {
                Fault(fixture.Storage, fixture.Probe, failure);
                yield break;
            }
            Blocked(() => ((ListPersistent<int>)fixture.Owner).AddRange(Input()), failure);
            Check.Equal(0, fixture.EncoderCalls, "No codec may run after user input catches a storage fault.");
            return "User enumeration that catches a physical stream failure and yields no values cannot turn a faulted range mutation into a successful empty no-op.";
        });
    }

    public static void Contracts(CheckContext ctx)
    {
        BaselineContracts(ctx);
        ConstructorFailureContracts(ctx);
        FormatClosePolicyContracts(ctx);
        foreach (WrapperKind kind in Enum.GetValues<WrapperKind>())
        {
            ctx.Run("wrapper-storage.inline-auto-flush-publication." + Name(kind), () =>
            {
                string[] operations = kind == WrapperKind.Queue ? ["add", "remove", "clear"]
                    : kind is WrapperKind.ObjectPersistence or WrapperKind.BinaryPersistence ? ["add", "update", "remove"]
                    : ["add", "update", "remove", "clear"];
                foreach (string operation in operations)
                {
                    using var fixture = new Fixture(kind);
                    if (operation != "add") fixture.Add(1);
                    fixture.Storage.FlushIdleMilliseconds = 0;
                    var failure = new IOException("controlled inline Flush failure after " + operation);
                    fixture.Probe.FlushFailure = failure;
                    fixture.Commit(operation);
                    Check.Equal(1, fixture.Probe.FlushCalls, "One inline automatic checkpoint must actually reach the stream.");
                    Check.That(ReferenceEquals(failure, Diagnostic(fixture.Storage, "LastAutoFlushException")), "Inline automatic error must be observable without replacing the successful mutation result.");
                    AssertFault(fixture.Storage, failure);
                    if (operation is "add" or "update") fixture.AssertResident(1, 2, successfulPublication: true);
                    else fixture.AssertResident(0, 0);
                    if (operation == "remove" && fixture.SeedEntity != null)
                        Check.Equal(long.MinValue, fixture.SeedEntity.PersistId, "A committed removal must publish the cleared PersistId despite the subsequent automatic Flush failure.");
                    Blocked(() => fixture.Add(3), failure);
                }
                return "Committed " + string.Join('/', operations) + " operations publish their resident cache/PersistId changes and return normally when the following inline auto-flush fails; the exact error is observable and later mutations reject.";
            });

            ctx.Run("wrapper-storage.application-ioexception-not-poison." + Name(kind), () =>
            {
                using var fixture = new Fixture(kind);
                fixture.Add(1);
                byte[] before = fixture.Probe.Bytes;
                long seedId = fixture.SeedEntity?.PersistId ?? long.MinValue;
                var failure = new IOException("application encoder IOException, not a stream failure");
                fixture.EncoderFailure = failure;
                Check.That(ReferenceEquals(failure, Check.Throws<IOException>(() => fixture.Add(2), "An application encoder error must escape unchanged.")), "The application exception must retain identity.");
                Check.Equal(false, Diagnostic(fixture.Storage, "IsFaulted"), "An IOException from application code must not poison storage.");
                Check.That(Diagnostic(fixture.Storage, "StorageException") == null, "Application encoding failure must not become the storage fault.");
                Same(before, fixture.Probe.Bytes, "A pre-write application encoding failure must preserve bytes.");
                fixture.AssertResident(1, 1);
                fixture.AssertRejectedEntity(seedId);
                fixture.EncoderFailure = null;
                fixture.Add(2);
                Check.Equal(2, fixture.Storage.Storage.Count, "A later healthy mutation must remain possible after an encoder error.");
                return "A deliberate application encoder/ToBinary IOException preserves bytes/resident state, leaves storage healthy and permits a later successful write.";
            });
        }

        ctx.Run("wrapper-storage.list-codec-prefix-remains-healthy", () =>
        {
            using var fixture = new Fixture(WrapperKind.List);
            fixture.Storage.FlushIdleMilliseconds = 0;
            var failure = new IOException("application codec failure on second range item");
            fixture.BeforeEncode = () => { if (fixture.EncoderCalls == 2) throw failure; };
            var list = (ListPersistent<int>)fixture.Owner;
            Check.That(ReferenceEquals(failure, Check.Throws<IOException>(() => list.AddRange(new[] { 1, 2, 3 }), "A range codec failure must escape.")), "The range codec exception must be preserved.");
            Same(new[] { 1 }, list.ToArray(), "The successful range prefix must be published.");
            Check.Equal(1, fixture.Storage.Storage.Count, "Only the successful prefix is stored.");
            Check.Equal(1, fixture.Probe.FlushCalls, "A healthy successful prefix must still receive the configured automatic checkpoint.");
            Check.Equal(false, Diagnostic(fixture.Storage, "IsFaulted"), "Application codec failure after a prefix must not poison storage.");
            fixture.BeforeEncode = null;
            list.Add(3);
            Same(new[] { 1, 3 }, list.ToArray(), "The healthy list must remain usable after a prefix codec failure.");
            return "A range application IOException retains and checkpoints its successful prefix while storage stays healthy and later writes succeed.";
        });

        ctx.Run("wrapper-storage.list-physical-prefix-no-auto-flush", () =>
        {
            using var fixture = new Fixture(WrapperKind.List);
            fixture.Storage.FlushIdleMilliseconds = 0;
            var failure = new IOException("controlled second range write failure");
            fixture.BeforeEncode = () => { if (fixture.EncoderCalls == 2) fixture.Probe.WriteFailure = failure; };
            var list = (ListPersistent<int>)fixture.Owner;
            Check.That(ReferenceEquals(failure, Check.Throws<IOException>(() => list.AddRange(new[] { 1, 2, 3 }), "The physical second write failure must escape.")), "A finally checkpoint must not replace the physical write failure.");
            Same(new[] { 1 }, list.ToArray(), "The already successful resident prefix must still publish in finally.");
            Check.Equal(2, fixture.EncoderCalls, "No later range item may be encoded after the physical failure.");
            Check.Equal(0, fixture.Probe.FlushCalls, "Faulted prefix cleanup must not attempt a new automatic stream flush.");
            Check.That(Diagnostic(fixture.Storage, "LastAutoFlushException") == null, "A skipped checkpoint must not report an automatic attempt.");
            AssertFault(fixture.Storage, failure);
            Blocked(() => list.Add(4), failure);
            return "A physical range write failure preserves publication of the already successful resident prefix, retains its original cause, skips automatic flushing and rejects later encoding/writing; no rollback or reopen-success claim is made.";
        });
    }

    private static void ConstructorFailureContracts(CheckContext ctx)
    {
        foreach (WrapperKind kind in new[] { WrapperKind.Queue, WrapperKind.ObjectPersistence, WrapperKind.BinaryPersistence })
        foreach (bool cleanupThrows in new[] { false, true })
        {
            ctx.Run("wrapper-storage.constructor-length-cleanup." + Name(kind) + (cleanupThrows ? ".cleanup-throws" : ".cleanup-ok"), () =>
            {
                byte[] before = [7, 6, 5, 4];
                var initial = new IOException("controlled constructor source Length failure");
                var cleanup = new IOException("controlled constructor cleanup failure after closing source");
                var source = new ConstructorFailureStream(before.ToArray(), initial, cleanupThrows ? cleanup : null);
                int callbacks = 0;
                IDisposable Open() => kind switch
                {
                    WrapperKind.Queue => new BufferedQueuePersistent<int>(source, 2,
                        reader: bytes => { callbacks++; return ReadInt(bytes); },
                        writer: value => { callbacks++; return IntBytes(value); }),
                    WrapperKind.ObjectPersistence => new StreamPersistence(source, serializer: new EntitySerializer(),
                        onLoad: _ => callbacks++),
                    _ => new StreamPersistence<Entity>(source, bytes => { callbacks++; return new Entity(ReadInt(bytes)); },
                        onLoad: _ => callbacks++)
                };
                Action construct = () => { using IDisposable unexpected = Open(); };
                if (kind == WrapperKind.Queue || !cleanupThrows)
                {
                    IOException actual = Check.Throws<IOException>(construct, "Source initialization failure must escape.");
                    Check.That(ReferenceEquals(initial, actual), "Queue cleanup never masks the original error; successful stream-wrapper cleanup preserves it too.");
                }
                else
                {
                    AggregateException actual = Check.Throws<AggregateException>(construct,
                        "StreamPersistence preserves its established initialization-plus-cleanup failure aggregate.");
                    Check.Equal(2, actual.InnerExceptions.Count, "Exactly the initialization and one cleanup error are aggregated.");
                    Check.That(ReferenceEquals(initial, actual.InnerExceptions[0]), "Aggregate first member is the exact initialization error.");
                    Check.That(ReferenceEquals(cleanup, actual.InnerExceptions[1]), "Aggregate second member is the exact single cleanup error.");
                }
                Check.Equal(1, source.DisposeCalls, "Exactly one owner must dispose the accepted source after constructor I/O failure.");
                Check.That(!source.CanRead && !source.CanWrite, "Cleanup error is injected only after the actual memory resource closes.");
                Check.Equal(1, source.LengthCalls, "Physical constructor failure is not retried through recovery.");
                Check.Equal(0, source.ReadCalls, "Initial Length failure precedes scanning any record bytes.");
                Check.Equal(0, source.WriteCalls, "Initialization and cleanup leave initial source bytes untouched.");
                Check.Equal(0, callbacks, "No queue codec, binary factory or onLoad callback runs after the initial physical failure.");
                Same(before, source.ToArray(), "Exact initial source bytes survive constructor failure and cleanup.");
                return "Synthetic initial Length failure closes the accepted source exactly once with unchanged bytes and no scan callbacks; "
                    + (cleanupThrows && kind != WrapperKind.Queue ? "ordered initialization/cleanup exception identities preserved in the existing StreamPersistence aggregate."
                        : "the original initialization exception retains exact identity, including Queue's throwing-cleanup path.");
            });
        }
    }

    private static void FormatClosePolicyContracts(CheckContext ctx)
    {
        foreach (bool cleanupThrows in new[] { false, true })
        {
            ctx.Run("wrapper-storage.constructor-format-close-policy." + (cleanupThrows ? "cleanup-throws" : "cleanup-ok"), () =>
            {
                foreach (WrapperKind kind in Enum.GetValues<WrapperKind>())
                {
                    byte[] before = [7, 6, 5, 4];
                    var cleanup = new IOException("controlled malformed-format cleanup error for " + Name(kind));
                    var source = new ConstructorFailureStream(before.ToArray(), initial: null, cleanup: cleanupThrows ? cleanup : null);
                    int callbacks = 0;
                    IDisposable Open() => kind switch
                    {
                        WrapperKind.Dictionary => new DictionaryPersistent<int, int>(source,
                            reader: bytes => { callbacks++; return ReadPair(bytes); },
                            writer: pair => { callbacks++; return PairBytes(pair); }),
                        WrapperKind.List => new ListPersistent<int>(source,
                            itemReader: bytes => { callbacks++; return ReadInt(bytes); },
                            itemWriter: value => { callbacks++; return IntBytes(value); }),
                        WrapperKind.Queue => new BufferedQueuePersistent<int>(source, 2,
                            reader: bytes => { callbacks++; return ReadInt(bytes); },
                            writer: value => { callbacks++; return IntBytes(value); }),
                        WrapperKind.ObjectPersistence => new StreamPersistence(source, serializer: new EntitySerializer(),
                            onLoad: _ => callbacks++),
                        _ => new StreamPersistence<Entity>(source, bytes => { callbacks++; return new Entity(ReadInt(bytes)); },
                            onLoad: _ => callbacks++)
                    };
                    Action construct = () => { using IDisposable unexpected = Open(); };
                    if (cleanupThrows)
                    {
                        IOException actual = Check.Throws<IOException>(construct,
                            Name(kind) + ": physical source Close failure under the default format policy must escape.");
                        Check.That(ReferenceEquals(cleanup, actual), Name(kind) + ": exact first physical Close error overrides the format error; wrapper must not add a second cleanup aggregate.");
                    }
                    else
                    {
                        Check.Throws<DataStorage.DataSourceException>(construct,
                            Name(kind) + ": four-byte malformed storage uses the default close-and-throw format policy.");
                    }
                    Check.Equal(1, source.DisposeCalls, Name(kind) + ": core format-policy Close owns the single cleanup attempt, even when it throws.");
                    Check.That(!source.CanRead && !source.CanWrite, Name(kind) + ": actual memory source is closed before the optional cleanup error.");
                    Check.That(source.LengthCalls > 0, Name(kind) + ": Length is healthy and format validation reached the short source.");
                    Check.Equal(0, source.WriteCalls, Name(kind) + ": default format handling leaves the malformed bytes untouched.");
                    Check.Equal(0, callbacks, Name(kind) + ": no wrapper codecs, factory or onLoad callback runs for the malformed header.");
                    Same(before, source.ToArray(), Name(kind) + ": exact malformed input bytes survive the default format policy and cleanup.");
                }
                return "All five wrappers over a four-byte malformed memory source with healthy Length honor one core-owned default format-policy Close; exact bytes remain unchanged. "
                    + (cleanupThrows ? "The exact physical Close IOException escapes, with no second cleanup or wrapper aggregate."
                        : "DataSourceException escapes after successful closure.");
            });
        }
    }

    private static void Fault(SynchronizedDataStorage storage, ProbeStream stream, IOException failure)
    {
        stream.FlushFailure = failure;
        Check.That(ReferenceEquals(failure, Check.Throws<IOException>(() => storage.Flush(), "The controlled stream Flush must fail.")), "The stream failure must escape unchanged.");
        stream.FlushFailure = null;
    }

    private static void Blocked(Action action, Exception cause)
    {
        InvalidOperationException error = Check.Throws<InvalidOperationException>(action, "A faulted owner must reject the operation.");
        Check.That(ReferenceEquals(cause, error.InnerException), "The later rejection must retain the original physical failure as its cause.");
    }

    private static object? Diagnostic(SynchronizedDataStorage storage, string name)
    {
        PropertyInfo property = typeof(SynchronizedDataStorage).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidDataException("Missing storage diagnostic property: " + name);
        return property.GetValue(storage);
    }

    private static void AssertFault(SynchronizedDataStorage storage, Exception failure)
    {
        Check.Equal(true, Diagnostic(storage, "IsFaulted"), "Storage must retain its terminal physical I/O failure.");
        Check.That(ReferenceEquals(failure, Diagnostic(storage, "StorageException")), "The first physical exception must remain available by identity.");
    }

    private static string Name(WrapperKind kind) => kind.ToString().ToLowerInvariant();
    private static void Same<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message) => Check.That(expected.SequenceEqual(actual), message);
    private static byte[] IntBytes(int value) { byte[] bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value); return bytes; }
    private static int ReadInt(byte[] bytes) { Check.Equal(4, bytes.Length, "Exact int payload length."); return BinaryPrimitives.ReadInt32LittleEndian(bytes); }
    private static byte[] PairBytes(KeyValuePair<int, int> pair) { byte[] bytes = new byte[8]; BinaryPrimitives.WriteInt32LittleEndian(bytes, pair.Key); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), pair.Value); return bytes; }
    private static KeyValuePair<int, int> ReadPair(byte[] bytes) { Check.Equal(8, bytes.Length, "Exact dictionary payload length."); return new(BinaryPrimitives.ReadInt32LittleEndian(bytes), BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4))); }

    private sealed class Fixture : IDisposable
    {
        public ProbeStream Probe { get; } = new();
        public IDisposable Owner { get; }
        public SynchronizedDataStorage Storage { get; }
        public int EncoderCalls { get; private set; }
        public int StateCallbacks { get; private set; }
        public int Enumerations { get; private set; }
        public Action? BeforeEncode { get; set; }
        public Exception? EncoderFailure { get; set; }
        public Entity? LatestEntity { get; private set; }
        public Entity? SeedEntity { get; private set; }

        public Fixture(WrapperKind kind)
        {
            switch (kind)
            {
                case WrapperKind.Dictionary:
                    var dictionary = new DictionaryPersistent<int, int>(Probe, reader: ReadPair, writer: pair => { Encode(); return PairBytes(pair); });
                    Owner = dictionary; Storage = dictionary.Storage; break;
                case WrapperKind.List:
                    var list = new ListPersistent<int>(Probe, itemReader: ReadInt, itemWriter: value => { Encode(); return IntBytes(value); });
                    Owner = list; Storage = list.Storage; break;
                case WrapperKind.Queue:
                    var queue = new BufferedQueuePersistent<int>(Probe, 2, reader: ReadInt, writer: value => { Encode(); return IntBytes(value); });
                    Owner = queue; Storage = queue.Storage; break;
                case WrapperKind.ObjectPersistence:
                    var objects = new StreamPersistence(Probe, serializer: new EntitySerializer());
                    Owner = objects; Storage = objects.Storage; break;
                default:
                    var binary = new StreamPersistence<Entity>(Probe, bytes => new Entity(ReadInt(bytes)));
                    Owner = binary; Storage = binary.Storage; break;
            }
            Storage.FlushIdleMilliseconds = -1;
            Storage.FlushUpdates = ushort.MaxValue;
        }

        private void Encode() { EncoderCalls++; BeforeEncode?.Invoke(); if (EncoderFailure != null) throw EncoderFailure; }
        private Entity NewEntity(int value) => new(value) { Encode = Encode, State = () => StateCallbacks++ };

        public void Add(int value)
        {
            switch (Owner)
            {
                case DictionaryPersistent<int, int> dictionary: dictionary.Add(value, value); break;
                case ListPersistent<int> list: list.Add(value); break;
                case BufferedQueuePersistent<int> queue: queue.Enqueue(value); break;
                case StreamPersistence objects:
                    LatestEntity = NewEntity(value); objects.Add(LatestEntity); SeedEntity ??= LatestEntity; break;
                case StreamPersistence<Entity> binary:
                    LatestEntity = NewEntity(value); binary.Add(LatestEntity); SeedEntity ??= LatestEntity; break;
            }
        }

        private IEnumerable<int> Input() { Enumerations++; yield return 2; }

        public Action[] Mutations() => Owner switch
        {
            DictionaryPersistent<int, int> dictionary => [() => Add(2), () => dictionary[1] = 2, () => dictionary.Remove(999), () => ((ICollection<KeyValuePair<int, int>>)dictionary).Remove(new(999, 999)), () => dictionary.Clear()],
            ListPersistent<int> list => [() => Add(2), () => list[0] = 2, () => list.Insert(0, 2), () => list.Remove(999), () => list.RemoveAt(0), () => list.AddRange(Input()), () => list.InsertRange(0, Input()), () => list.AddRange(Array.Empty<int>()), () => list.Clear()],
            BufferedQueuePersistent<int> queue => [() => Add(2), () => queue.Dequeue(), () => queue.Clear()],
            StreamPersistence objects => [() => Add(2), () => objects.Update(SeedEntity!), () => objects.AddOrUpdate(NewEntity(3)), () => objects.Remove(NewEntity(4))],
            StreamPersistence<Entity> binary => [() => Add(2), () => binary.Update(SeedEntity!), () => binary.AddOrUpdate(NewEntity(3)), () => binary.Remove(NewEntity(4))],
            _ => throw new InvalidDataException("Unexpected wrapper.")
        };

        public void Commit(string operation)
        {
            if (operation == "add") { Add(2); return; }
            switch (Owner)
            {
                case DictionaryPersistent<int, int> dictionary:
                    if (operation == "update") dictionary[1] = 2;
                    else if (operation == "remove") Check.That(dictionary.Remove(1), "The committed dictionary remove must return true.");
                    else dictionary.Clear();
                    break;
                case ListPersistent<int> list:
                    if (operation == "update") list[0] = 2;
                    else if (operation == "remove") list.RemoveAt(0);
                    else list.Clear();
                    break;
                case BufferedQueuePersistent<int> queue:
                    if (operation == "remove") Check.Equal(1, queue.Dequeue(), "A committed queue removal must return its value even when its automatic Flush fails.");
                    else queue.Clear();
                    break;
                case StreamPersistence objects:
                    if (operation == "update") { SeedEntity!.Value = 2; objects.Update(SeedEntity); }
                    else Check.That(objects.Remove(SeedEntity!), "A committed object removal must return true.");
                    break;
                case StreamPersistence<Entity> binary:
                    if (operation == "update") { SeedEntity!.Value = 2; binary.Update(SeedEntity); }
                    else Check.That(binary.Remove(SeedEntity!), "A committed binary removal must return true.");
                    break;
            }
        }

        public void AssertRejectedEntity(long seedId)
        {
            if (SeedEntity == null) return;
            Check.Equal(seedId, SeedEntity.PersistId, "A rejected encode/write must preserve the existing object's saved PersistId.");
            Check.That(LatestEntity != null && !ReferenceEquals(SeedEntity, LatestEntity), "The rejected candidate must be distinct from the already committed object.");
            Check.Equal(long.MinValue, LatestEntity!.PersistId, "The rejected candidate must remain unpersisted.");
        }

        public void AssertResident(int count, int value, bool successfulPublication = false)
        {
            switch (Owner)
            {
                case DictionaryPersistent<int, int> dictionary:
                    Check.Equal(count, dictionary.Count, "Resident dictionary count must publish the completed operation.");
                    if (count != 0) Check.Equal(value, dictionary.GetValues().Single(), "Resident dictionary value.");
                    break;
                case ListPersistent<int> list:
                    Same(count == 0 ? Array.Empty<int>() : new[] { value }, list.ToArray(), "Resident list values must publish the completed operation.");
                    break;
                case BufferedQueuePersistent<int> queue:
                    Check.Equal(count, queue.BufferedCount, "Resident queue buffer must publish the completed operation.");
                    break;
                default:
                    if (count != 0)
                    {
                        Entity? resident = successfulPublication ? LatestEntity : SeedEntity;
                        Check.That((resident?.PersistId ?? long.MinValue) >= DataStorage.HeaderOffset, "A committed stream object must receive its store-local ID before the fault is inspected.");
                        Check.Equal(value, resident!.Value, "The committed object remains available as advisory caller-held state.");
                    }
                    break;
            }
        }

        public void Dispose() => Owner.Dispose();
    }

    private sealed class Entity(int value) : IPersistentBinary
    {
        public int Value { get; set; } = value;
        public long PersistId { get; set; } = long.MinValue;
        public Action? Encode { get; init; }
        public Action? State { get; init; }
        public bool IsPersistent { get { State?.Invoke(); return PersistId >= 0; } }
        public byte[] ToBinary() { Encode?.Invoke(); return IntBytes(Value); }
    }

    private sealed class EntitySerializer : IDataSerializer
    {
        public void Serialize(Stream stream, object? graph) => stream.Write(((Entity)graph!).ToBinary());
        public object Deserialize(Stream stream) { byte[] bytes = new byte[4]; stream.ReadExactly(bytes); return new Entity(ReadInt(bytes)); }
    }

    private sealed class BridgeFixture : IDisposable
    {
        private static readonly PropertyInfo StreamProperty = typeof(DataStorage).GetProperty(nameof(DataStorage.Stream))!;
        private readonly Stream original;
        public DictionaryLogger<int, int> Logger { get; }
        public ProbeStream Probe { get; } = new();
        public int HistoryEncodes { get; private set; }

        public BridgeFixture(CheckContext ctx, string label)
        {
            string directory = Path.Combine(ctx.DataRoot, "wrapper-storage", label + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Logger = new DictionaryLogger<int, int>(Path.Combine(directory, "state.bin"), Path.Combine(directory, "history"),
                ReadPair, PairBytes, _ => [], _ => { HistoryEncodes++; return [1]; });
            Logger.Values.Storage.FlushIdleMilliseconds = -1;
            Logger.Values.Storage.FlushUpdates = ushort.MaxValue;
            Logger.Values.Add(1, 1);
            original = Logger.Values.Storage.Storage.Stream;
            // Test-only replacement injects a Flush exception into a separate empty memory stream.
            // Cached state/history observations never read that probe as a state file.
            StreamProperty.SetValue(Logger.Values.Storage.Storage, Probe);
        }

        public void Dispose()
        {
            try { StreamProperty.SetValue(Logger.Values.Storage.Storage, original); Logger.Dispose(); }
            finally { original.Dispose(); Probe.Dispose(); }
        }
    }

    private sealed class ProbeStream : MemoryStream
    {
        public IOException? FlushFailure { get; set; }
        public IOException? WriteFailure { get; set; }
        public int FlushCalls { get; private set; }
        public byte[] Bytes => ToArray();
        public override void Flush() { FlushCalls++; if (FlushFailure != null) throw FlushFailure; base.Flush(); }
        public override void Write(byte[] buffer, int offset, int count) { if (WriteFailure != null) throw WriteFailure; base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { if (WriteFailure != null) throw WriteFailure; base.Write(buffer); }
    }

    // Test-owned memory: optional initial Length failure, or healthy Length over malformed bytes.
    // The optional physical cleanup failure is thrown only after closing the actual memory resource.
    private sealed class ConstructorFailureStream(byte[] bytes, Exception? initial, Exception? cleanup) : MemoryStream(bytes)
    {
        public int LengthCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public int WriteCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public override long Length { get { LengthCalls++; if (initial != null) throw initial; return base.Length; } }
        public override int Read(byte[] buffer, int offset, int count) { ReadCalls++; return base.Read(buffer, offset, count); }
        public override int Read(Span<byte> buffer) { ReadCalls++; return base.Read(buffer); }
        public override void Write(byte[] buffer, int offset, int count) { WriteCalls++; base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { WriteCalls++; base.Write(buffer); }
        public override void SetLength(long value) { WriteCalls++; base.SetLength(value); }
        protected override void Dispose(bool disposing)
        {
            if (!disposing) { base.Dispose(false); return; }
            DisposeCalls++;
            base.Dispose(true);
            if (cleanup != null) throw cleanup;
        }
    }
}
