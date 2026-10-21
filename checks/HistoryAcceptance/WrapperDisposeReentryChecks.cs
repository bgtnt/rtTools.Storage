using System.Buffers.Binary;
using System.Reflection;
using RtTools;

namespace HistoryAcceptance;

internal static class WrapperDisposeReentryChecks
{
    private enum WrapperKind { Queue, ObjectPersistence, BinaryPersistence }
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    // All six cases use owned memory streams, bounded tasks and direct storage fallback cleanup.
    // On exact published preview.4 the explicit cases and inline Queue case fail; the two inline StreamPersistence controls pass.
    public static void BaselineContracts(CheckContext ctx)
    {
        foreach (WrapperKind kind in Enum.GetValues<WrapperKind>())
        foreach (bool inline in new[] { false, true })
        {
            string mode = inline ? "inline-auto" : "explicit";
            ctx.Run("wrapper-dispose-reentry." + Name(kind) + "." + mode, () => Bounded(() => CallbackContract(kind, inline)));
        }
    }

    public static void Contracts(CheckContext ctx)
    {
        BaselineContracts(ctx);
        foreach (WrapperKind kind in Enum.GetValues<WrapperKind>())
        {
            ctx.Run("wrapper-dispose-reentry." + Name(kind) + ".faulted-cleanup", () => Bounded(() =>
            {
                using var fixture = new Fixture(kind);
                fixture.AddInitial();
                var failure = new IOException("controlled source Flush failure before wrapper cleanup");
                fixture.Source.FlushFailure = failure;
                Check.That(ReferenceEquals(failure, Check.Throws<IOException>(() => fixture.Storage.Flush(), "The controlled source Flush must fail.")), "The first physical exception must retain identity.");
                fixture.Source.FlushFailure = null;
                Check.That(fixture.Storage.IsFaulted && fixture.Storage.Storage.IsFaulted, "The core must enter its existing terminal fault state.");
                Check.That(ReferenceEquals(failure, fixture.Storage.StorageException), "The wrapper must expose the first source failure.");
                Check.Equal(0, fixture.Source.CloseCount, "A source operation failure must leave cleanup available to the owner.");
                fixture.Owner.Dispose();
                Check.Equal(1, fixture.Source.CloseCount, "Faulted storage must still permit one actual source close through its wrapper.");
                Check.That(!fixture.Source.CanRead && !fixture.Source.CanWrite, "Wrapper cleanup must close the owned source after a storage fault.");
                Check.That(ReferenceEquals(failure, fixture.Storage.StorageException), "Cleanup must preserve the first failure diagnostic.");
                fixture.Owner.Dispose();
                Check.Equal(1, fixture.Source.CloseCount, "Repeated wrapper cleanup must not close twice.");
                fixture.Storage.Dispose();
                Check.Equal(1, fixture.Source.CloseCount, "Direct underlying cleanup must also remain idempotent.");
                Check.That(ReferenceEquals(failure, fixture.Storage.Storage.StorageException), "The first failure remains inspectable after all cleanup calls.");
                return "A prior synthetic source failure does not block wrapper disposal: one actual close, repeated wrapper/SDS disposal inert, original fault retained by identity.";
            }));
        }
    }

    private static string CallbackContract(WrapperKind kind, bool inline)
    {
        using var fixture = new Fixture(kind);
        if (!inline) fixture.AddInitial();
        InvalidOperationException? callbackError = null;
        bool callbackDisposeReturned = false;
        int callbackThread = 0;
        int callerThread = Environment.CurrentManagedThreadId;
        fixture.Source.Callback = () =>
        {
            callbackThread = Environment.CurrentManagedThreadId;
            try { fixture.Owner.Dispose(); callbackDisposeReturned = true; }
            catch (InvalidOperationException error) { callbackError = error; }
        };
        fixture.Source.Armed = true;
        if (inline)
        {
            fixture.Storage.FlushIdleMilliseconds = 0;
            fixture.AddInitial();
        }
        else fixture.Storage.Flush();

        Check.Equal(1, fixture.Source.CallbackCalls, "Exactly one armed source Flush callback must run.");
        Check.Equal(callerThread, callbackThread, "The controlled explicit/inline automatic callback must run synchronously.");
        Check.That(callbackError != null && !callbackDisposeReturned, "Dispose from the source callback must reject before ownership changes.");
        Check.Equal(0, fixture.Source.CloseCount, "Rejected callback disposal must not close the source.");
        Check.That(fixture.Source.CanRead && fixture.Source.CanWrite, "The rejected callback must leave the source usable.");
        Check.That(!fixture.Storage.IsFaulted && fixture.Storage.StorageException == null, "A callback that catches the reentry rejection and returns success must not fault storage.");
        Check.That(fixture.Storage.LastAutoFlushException == null, "A successful controlled Flush must not report an automatic error.");
        // Optional observation only; public write/read and normal disposal below establish the contract.
        FieldInfo? disposed = fixture.Owner.GetType().GetField("disposed", BindingFlags.NonPublic | BindingFlags.Instance);
        if (disposed?.GetValue(fixture.Owner) is bool markedDisposed)
            Check.That(!markedDisposed, "Rejected source callback disposal must not mark the outer wrapper disposed.");

        fixture.Storage.FlushIdleMilliseconds = -1;
        fixture.AssertHealthyWriteRead();
        Check.That(!fixture.Storage.IsFaulted && fixture.Storage.StorageException == null, "Subsequent wrapper writes and actual source reads must remain healthy.");
        fixture.Owner.Dispose();
        Check.Equal(1, fixture.Source.CloseCount, "The first normal wrapper Dispose must perform the actual close.");
        Check.That(!fixture.Source.CanRead && !fixture.Source.CanWrite, "Normal wrapper disposal must release its source.");
        fixture.Owner.Dispose();
        Check.Equal(1, fixture.Source.CloseCount, "Repeated normal wrapper disposal must be inert.");
        fixture.Storage.Dispose();
        Check.Equal(1, fixture.Source.CloseCount, "Underlying fallback disposal must not double close.");
        return (inline ? "Inline automatic" : "Explicit SDS") + " Flush callback catches the rejected wrapper Dispose without changing lifetime/fault state; subsequent exact payload write/read succeeds and normal disposal closes once.";
    }

    private static string Bounded(Func<string> body) => Task.Run(body).WaitAsync(WaitLimit).GetAwaiter().GetResult();
    private static string Name(WrapperKind kind) => kind.ToString().ToLowerInvariant();
    private static byte[] WriteInt(int value) { byte[] bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value); return bytes; }
    private static int ReadInt(byte[] bytes) { Check.Equal(4, bytes.Length, "The codec requires an exact four-byte payload."); return BinaryPrimitives.ReadInt32LittleEndian(bytes); }

    private sealed class Fixture : IDisposable
    {
        public CallbackStream Source { get; } = new();
        public IDisposable Owner { get; }
        public SynchronizedDataStorage Storage { get; }
        private Entity? entity;

        public Fixture(WrapperKind kind)
        {
            if (kind == WrapperKind.Queue)
            {
                var queue = new BufferedQueuePersistent<int>(Source, 2, reader: ReadInt, writer: WriteInt);
                Owner = queue; Storage = queue.Storage;
            }
            else if (kind == WrapperKind.ObjectPersistence)
            {
                var objects = new StreamPersistence(Source, serializer: new EntitySerializer());
                Owner = objects; Storage = objects.Storage;
            }
            else
            {
                var binary = new StreamPersistence<Entity>(Source, bytes => new Entity(ReadInt(bytes)));
                Owner = binary; Storage = binary.Storage;
            }
            Storage.FlushIdleMilliseconds = -1;
            Storage.FlushUpdates = ushort.MaxValue;
        }

        public void AddInitial()
        {
            if (Owner is BufferedQueuePersistent<int> queue) { queue.Enqueue(1); return; }
            entity = new Entity(1);
            long position = Owner is StreamPersistence objects ? objects.Add(entity) : ((StreamPersistence<Entity>)Owner).Add(entity);
            Check.Equal(position, entity.PersistId, "Initial stream mutation must publish its returned ID.");
        }

        public void AssertHealthyWriteRead()
        {
            if (Owner is BufferedQueuePersistent<int> queue)
            {
                queue.Enqueue(2);
                int[] payloads = Storage.ReadData.Select(pair => ReadInt(pair.Value[12..])).Order().ToArray();
                Check.That(payloads.SequenceEqual(new[] { 1, 2 }), "Actual queue source reads must contain both exact encoded payloads.");
                Check.Equal(1, queue.Dequeue(), "The queue must remain usable in FIFO order after the rejected callback.");
                Check.Equal(2, queue.Dequeue(), "The second queue value must also remain usable.");
            }
            else
            {
                entity!.Value = 42;
                long position = Owner is StreamPersistence objects ? objects.Update(entity) : ((StreamPersistence<Entity>)Owner).Update(entity);
                Check.Equal(position, entity.PersistId, "The healthy updated object must receive its current record position.");
                Check.Equal(42, ReadInt(Storage.GetData(position)), "The subsequent wrapper write must round-trip through an actual source read.");
            }
        }

        public void Dispose()
        {
            // Baseline preview.4 can mark the wrapper disposed while leaving its SDS open.
            // Always attempt fallback disposal outside the source callback, including assertion failures.
            try { Owner.Dispose(); }
            finally { Storage.Dispose(); }
        }
    }

    private sealed class Entity(int value) : IPersistentBinary
    {
        public int Value { get; set; } = value;
        public long PersistId { get; set; } = long.MinValue;
        public bool IsPersistent => PersistId >= 0;
        public byte[] ToBinary() => WriteInt(Value);
    }
    private sealed class EntitySerializer : IDataSerializer
    {
        public void Serialize(Stream stream, object? graph) => stream.Write(((Entity)graph!).ToBinary());
        public object Deserialize(Stream stream) { byte[] bytes = new byte[4]; stream.ReadExactly(bytes); return new Entity(ReadInt(bytes)); }
    }
    private sealed class CallbackStream : MemoryStream
    {
        public Action? Callback { get; set; }
        public bool Armed { get; set; }
        public IOException? FlushFailure { get; set; }
        public int CallbackCalls { get; private set; }
        public int CloseCount { get; private set; }
        public override void Flush()
        {
            if (Armed) { Armed = false; CallbackCalls++; Callback?.Invoke(); }
            if (FlushFailure != null) throw FlushFailure;
            base.Flush();
        }
        protected override void Dispose(bool disposing) { if (disposing) CloseCount++; base.Dispose(disposing); }
    }
}
