using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RtTools;

namespace HistoryAcceptance;

internal static class SynchronizedStorageFailureChecks
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    public static void Contracts(CheckContext ctx)
    {
        BaselineContracts(ctx);

        ctx.Run("storage.auto.idle-timer-error", () =>
        {
            RequireApi();
            using var fixture = new Fixture();
            var failure = new IOException("idle timer flush sentinel");
            fixture.Source.FlushError = failure;
            fixture.Store.FlushIdleMilliseconds = 10;
            int callerThread = Environment.CurrentManagedThreadId;
            Check.Equal(28L, fixture.Store.Add(new byte[] { 1, 2, 3 }), "Mutation returns its committed record position.");
            Check.That(SpinWait.SpinUntil(() => ReferenceEquals(failure, AutoError(fixture.Store)), WaitLimit),
                "The actual idle timer must expose its completed failure within five seconds.");
            Check.That(fixture.Source.FlushThread != callerThread, "Idle flush must run on a real timer thread.");
            Check.Equal(1, fixture.Source.FlushAttempts, "One actual automatic flush attempt.");
            AssertFault(fixture, failure);
            Check.Equal(1, Pending(fixture.Store), "Failed idle flush preserves its pending update.");
            InvokeSkippedCallback(fixture.Store);
            Check.That(ReferenceEquals(failure, AutoError(fixture.Store)), "A callback skipped after fault must not replace the actual automatic failure.");
            Check.Equal(1, fixture.Source.FlushAttempts, "Faulted callback skips physical flush.");
            AssertBytes(fixture.Source.ToArray(), [(28L, "010203")]);
            Quiesce(fixture.Store);
            return "Real idle timer on another thread exposes the exact flush failure and core first fault; pending update retained, faulted callback skipped, exact committed memory-stream record preserved. Synthetic source failure, no OS/durability claim.";
        });

        ctx.Run("storage.auto.idle-timer-success", () =>
        {
            RequireApi();
            using var fixture = new Fixture();
            fixture.Store.FlushIdleMilliseconds = 10;
            int callerThread = Environment.CurrentManagedThreadId;
            fixture.Store.Add(new byte[] { 4, 5 });
            Check.That(fixture.Source.Flushed.Wait(WaitLimit), "Successful idle timer did not run within five seconds.");
            Check.Equal(0, Pending(fixture.Store), "Successful timer resets pending updates under the gate.");
            Check.That(fixture.Source.FlushThread != callerThread, "Successful automatic flush uses the actual timer thread.");
            Check.That(AutoError(fixture.Store) == null && !Faulted(fixture.Store), "Successful auto flush does not imply a storage fault.");
            Quiesce(fixture.Store);
            Check.Equal(1, fixture.Source.FlushAttempts, "Only one idle automatic attempt.");
            AssertBytes(fixture.Source.ToArray(), [(28L, "0405")]);
            return "Real one-shot idle timer succeeds on another thread, clears pending accounting, and leaves null automatic telemetry with a healthy owner; exact payload verified independently.";
        });

        ctx.Run("storage.auto.source-flush-reentry", () =>
        {
            RequireApi();
            using var fixture = new Fixture();
            fixture.Store.FlushIdleMilliseconds = 0;
            int callbacks = 0;
            fixture.Source.OnFlush = () =>
            {
                callbacks++;
                AssertSourceReentryRejected(fixture);
            };
            fixture.Store.Flush();
            Check.Equal(1, callbacks, "Explicit flush invokes the source hook once.");
            Check.Equal(28L, fixture.Store.Add(new byte[] { 0xA1 }), "Inline auto flush must not dispose or invalidate its committed writer.");
            Check.Equal(2, callbacks, "Inline automatic flush invokes the source hook once.");
            Check.Equal(2, fixture.Source.FlushAttempts, "Recursive flush attempts must not reach the raw source.");
            Check.Equal(0, fixture.Source.CloseAttempts, "Rejected source callback disposal must not close or mark the wrapper disposed.");
            Check.That(!Faulted(fixture.Store) && AutoError(fixture.Store) == null, "Handled callback reentry errors do not poison successful physical I/O.");
            fixture.Source.OnFlush = null;
            Check.Equal(1, fixture.Store.Count, "Wrapper remains usable after rejected source callback disposal.");
            fixture.Store.Add(new byte[] { 0xB2 });
            AssertBytes(fixture.Source.ToArray(), [(28L, "A1"), (53L, "B2")]);
            return "Custom source Flush cannot recursively Dispose, Flush, read or mutate the same owner; rejection precedes wrapper state changes and raw I/O. Explicit/inline paths remain usable when source handles those errors.";
        });

        ctx.Run("storage.auto.timer-source-dispose-reentry", () =>
        {
            RequireApi();
            var fixture = new Fixture();
            bool quiesced = false;
            try
            {
                fixture.Store.FlushIdleMilliseconds = 10;
                int callerThread = Environment.CurrentManagedThreadId;
                fixture.Source.OnFlush = () => AssertSourceReentryRejected(fixture);
                fixture.Store.Add(new byte[] { 0xC3 });
                Check.That(fixture.Source.Flushed.Wait(WaitLimit), "Timer source callback did not finish within five seconds (possible timer self-join).");
                Quiesce(fixture.Store);
                quiesced = true;
                Check.Equal(0, Pending(fixture.Store), "Successful automatic attempt completes after source handles rejected reentry.");
                Check.That(fixture.Source.FlushThread != callerThread, "Reentry checks ran from the actual timer callback.");
                Check.Equal(1, fixture.Source.FlushAttempts, "No physical recursive Flush.");
                Check.Equal(0, fixture.Source.CloseAttempts, "Rejected reentrant Dispose leaves the owner open.");
                Check.That(!Faulted(fixture.Store) && AutoError(fixture.Store) == null, "Source-handled reentry leaves successful automatic state intact.");
                fixture.Source.OnFlush = null;
                Check.Equal(1, fixture.Store.Count, "Timer callback cannot leave wrapper spuriously disposed.");
                return "Real timer source callback rejects synchronous same-owner Dispose before setting disposed, without waiting for itself; recursive reads/writes/flush also reject before raw access, then automatic flush completes normally.";
            }
            finally
            {
                // A regressed callback can own the gate forever. On timeout, deliberately abandon this
                // memory-only fixture to the test process rather than block again in Dispose cleanup.
                if (quiesced) fixture.Dispose();
            }
        });

        ctx.Run("storage.auto.dispose-under-outer-gate", () =>
        {
            RequireApi();
            // No using/finally Dispose: on a worker timeout the regressed worker may still own the gate.
            // The abandoned fixture contains only memory and is reclaimed when the test process exits.
            var fixture = new Fixture();
            Task dispose = Task.Run(() =>
            {
                lock (fixture.Store.SynchronizationRoot)
                {
                    Check.That(Timer(fixture.Store).Change(0, Timeout.Infinite), "Schedule the actual callback while the outer gate is held.");
                    fixture.Store.Dispose();
                }
            });
            Check.That(dispose.Wait(WaitLimit), "Dispose under an outer gate must not join a queued callback waiting for that gate.");
            dispose.GetAwaiter().GetResult();
            Quiesce(fixture.Store);
            Check.Equal(1, fixture.Source.CloseAttempts, "Owned source closes exactly once.");
            Check.Equal(0, fixture.Source.FlushAttempts, "Scheduled callback never flushes disposed resources.");
            InvokeSkippedCallback(fixture.Store);
            fixture.Store.Dispose();
            Check.Equal(1, fixture.Source.CloseAttempts, "Repeated Dispose and skipped callbacks are inert.");
            fixture.Dispose();
            return "Dispose while holding an outer recursive storage gate completes within five seconds after real timer scheduling; callbacks are quiesced outside the gate and never access the closed source. No callback-start scheduling guarantee is claimed.";
        });

        ctx.Run("storage.auto.faulted-dispose-close-error", () =>
        {
            RequireApi();
            using var fixture = new Fixture();
            var flushFailure = new IOException("first auto flush sentinel");
            var closeFailure = new IOException("later close sentinel");
            fixture.Store.FlushIdleMilliseconds = 0;
            fixture.Source.FlushError = flushFailure;
            fixture.Store.Add(new byte[] { 0xD4 });
            fixture.Source.CloseError = closeFailure;
            IOException actual = Check.Throws<IOException>(fixture.Store.Dispose, "Dispose must propagate a close failure after attempting closure of faulted storage.");
            Check.That(ReferenceEquals(closeFailure, actual), "Single close error escapes unchanged.");
            Check.Equal(1, fixture.Source.CloseAttempts, "Close attempted despite prior automatic failure.");
            Check.That(!fixture.Source.CanWrite, "Probe throws its close error only after closing the actual memory stream.");
            Check.That(ReferenceEquals(flushFailure, FirstError(fixture.Store)), "Close error does not replace the core's first failure.");
            Check.That(ReferenceEquals(flushFailure, AutoError(fixture.Store)), "Dispose does not replace automatic failure telemetry.");
            fixture.Store.Dispose();
            Check.Equal(1, fixture.Source.CloseAttempts, "Dispose remains idempotent after close error.");
            return "Faulted storage is still closed; a later close failure propagates unchanged after the source closes, while first-fault and latest-auto identities remain the earlier flush failure. Repeated Dispose does not retry resources.";
        });
    }

    // Safe against preview.3: the first two cases expose behavioral failures using only the old public API.
    // Remaining cases reflect new properties, so missing APIs produce separately identifiable ordinary FAILs.
    // Deliberately excludes real-timer disposal and raw-source recursive callback tests from the old package.
    public static void BaselineContracts(CheckContext ctx)
    {
        ctx.Run("storage.auto.behavior-failed-header-no-flush", () =>
        {
            using var fixture = new Fixture();
            fixture.Store.FlushIdleMilliseconds = 0;
            var failure = new IOException("header write sentinel");
            fixture.Source.WriteError = failure;
            IOException actual = Check.Throws<IOException>(() => fixture.Store.SetHeaderValue(12, 123),
                "Valid reserved-header write failure must propagate unchanged.");
            Check.That(ReferenceEquals(failure, actual), "Original header write error must not be masked.");
            Check.Equal(0, fixture.Source.FlushAttempts, "A failed header mutation must not trigger a finally-driven automatic flush.");
            return "Old-public-API behavioral check: exact reserved-header write error preserved and zero automatic flush calls after failed mutation; no follow-up writes attempted.";
        });

        ctx.Run("storage.auto.behavior-explicit-resets-threshold", () =>
        {
            using var fixture = new Fixture();
            fixture.Store.FlushUpdates = 2;
            fixture.Store.Add(new byte[] { 1 });
            Check.Equal(0, fixture.Source.FlushAttempts, "One pending update is below threshold.");
            fixture.Store.Flush();
            Check.Equal(1, fixture.Source.FlushAttempts, "Explicit flush performs exactly one physical attempt.");
            fixture.Store.Add(new byte[] { 2 });
            Check.Equal(1, fixture.Source.FlushAttempts, "Successful explicit flush resets pending count, so the second Add is again below threshold.");
            AssertBytes(fixture.Source.ToArray(), [(28L, "01"), (53L, "02")]);
            return "Old-public-API behavioral check: Add/explicit Flush/Add with threshold=2 performs only the explicit flush, proving successful explicit reset; exact two-record payload oracle verified.";
        });

        ctx.Run("storage.auto.threshold-control", () =>
        {
            RequireApi();
            using var fixture = new Fixture();
            fixture.Store.FlushUpdates = 2;
            fixture.Store.Add(new byte[] { 1 });
            Check.Equal(1, Pending(fixture.Store), "First mutation is pending.");
            Check.Equal(0, fixture.Source.FlushAttempts, "Below threshold with idle disabled does not flush.");
            fixture.Store.Flush();
            Check.Equal(0, Pending(fixture.Store), "Successful explicit Flush resets pending accounting.");
            Check.That(AutoError(fixture.Store) == null, "Explicit flush leaves initial automatic telemetry unchanged.");
            fixture.Store.Add(new byte[] { 2 });
            Check.Equal(1, fixture.Source.FlushAttempts, "Explicit reset makes the next mutation fall below threshold.");
            fixture.Store.Add(new byte[] { 3 });
            Check.Equal(2, fixture.Source.FlushAttempts, "Second pending mutation reaches the threshold exactly once.");
            Check.Equal(Environment.CurrentManagedThreadId, fixture.Source.FlushThread, "Threshold flush remains synchronous.");
            Check.Equal(0, Pending(fixture.Store), "Successful automatic attempt resets pending accounting.");
            Check.That(AutoError(fixture.Store) == null && !Faulted(fixture.Store), "Successful auto state is null, with no core fault.");
            AssertBytes(fixture.Source.ToArray(), [(28L, "01"), (53L, "02"), (78L, "03")]);
            return "Threshold=2 remains synchronous; explicit success resets pending count without changing auto telemetry, automatic success resets under the gate, exact three-record oracle preserved.";
        });

        InlineFailure(ctx, "threshold-error", 2, -1);
        InlineFailure(ctx, "idle-zero-error", 256, 0);

        ctx.Run("storage.auto.explicit-failure-state", () =>
        {
            RequireApi();
            using var fixture = new Fixture();
            fixture.Store.Add(new byte[] { 7 });
            var failure = new IOException("explicit-only flush sentinel");
            fixture.Source.FlushError = failure;
            IOException actual = Check.Throws<IOException>(fixture.Store.Flush, "Explicit Flush must propagate its physical error.");
            Check.That(ReferenceEquals(failure, actual), "Explicit flush error identity.");
            AssertFault(fixture, failure);
            Check.That(AutoError(fixture.Store) == null, "Null auto telemetry is not a health indicator after an explicit failure.");
            Check.Equal(1, Pending(fixture.Store), "Failed explicit Flush retains pending update accounting.");
            InvokeSkippedCallback(fixture.Store);
            Check.That(AutoError(fixture.Store) == null, "A callback skipped for an existing fault does not fabricate an auto-attempt failure.");
            Check.Equal(1, fixture.Source.FlushAttempts, "Skipped callback performs no physical Flush.");
            return "Explicit flush propagates its exact error and faults the owner while LastAutoFlushException remains null; pending update is retained and later skipped callback does not alter telemetry.";
        });

        ctx.Run("storage.auto.failed-mutation", () =>
        {
            RequireApi();
            using var fixture = new Fixture();
            fixture.Store.FlushIdleMilliseconds = 0;
            var writeFailure = new IOException("mutation write sentinel");
            fixture.Source.WriteError = writeFailure;
            IOException actual = Check.Throws<IOException>(() => fixture.Store.Add(new byte[] { 8 }), "Physical mutation failure must reach the caller.");
            Check.That(ReferenceEquals(writeFailure, actual), "Automatic flush cannot replace the original mutation exception.");
            Check.Equal(0, fixture.Source.FlushAttempts, "Failed mutation does not invoke automatic Flush.");
            Check.Equal(0, Pending(fixture.Store), "Failed mutation adds no pending successful update.");
            Check.That(AutoError(fixture.Store) == null, "No automatic attempt means no automatic error.");
            AssertFault(fixture, writeFailure);
            return "An injected raw write failure propagates unchanged, records the core first fault, and triggers neither automatic flush nor pending-success accounting.";
        });

        ctx.Run("storage.auto.serializer-rejection", () =>
        {
            RequireApi();
            using var fixture = new Fixture();
            fixture.Store.FlushIdleMilliseconds = 0;
            var failure = new IOException("application serializer sentinel");
            fixture.Serializer.Error = failure;
            string before = Convert.ToHexString(fixture.Source.ToArray());
            IOException actual = Check.Throws<IOException>(() => fixture.Store.Add((object)123), "Application serializer error must escape.");
            Check.That(ReferenceEquals(failure, actual), "Application serializer exception identity.");
            Check.That(!Faulted(fixture.Store) && FirstError(fixture.Store) == null, "Application IOException outside owned source I/O does not fault the store.");
            Check.That(AutoError(fixture.Store) == null, "No auto attempt after serialization rejection.");
            Check.Equal(0, fixture.Source.FlushAttempts, "Serialization rejection does not flush.");
            Check.Equal(0, Pending(fixture.Store), "Serialization rejection is not a successful update.");
            Check.Equal(before, Convert.ToHexString(fixture.Source.ToArray()), "Rejected serializer leaves exact bytes unchanged.");
            fixture.Serializer.Error = null;
            Check.Equal(28L, fixture.Store.Add((object)123), "Owner remains usable after serializer rejection.");
            AssertBytes(fixture.Source.ToArray(), [(28L, "7B000000")]);
            return "Application serializer IOException is preserved without poisoning storage, writes, pending updates or automatic attempts; a later valid object is stored with independently specified little-endian bytes.";
        });

        ctx.Run("storage.auto.disposed-entries", () =>
        {
            RequireApi();
            using var fixture = new Fixture();
            using IEnumerator<object?> deferred = fixture.Store.GetEnumerator();
            fixture.Store.Dispose();
            int accesses = fixture.Source.AccessCount;
            int codecs = fixture.Serializer.Calls;
            foreach (Action operation in Operations(fixture.Store))
                Check.Throws<ObjectDisposedException>(operation, "Disposed operation must reject before any raw access or codec, including empty paths.");
            Check.Throws<ObjectDisposedException>(() => deferred.MoveNext(), "Enumerator created before Dispose must guard deferred first MoveNext, even for empty storage.");
            Check.Equal(accesses, fixture.Source.AccessCount, "Disposed entries touch no owned source.");
            Check.Equal(codecs, fixture.Serializer.Calls, "Disposed object overloads run no serializer.");
            Check.That(AutoError(fixture.Store) == null && !Faulted(fixture.Store), "Telemetry is inspectable after clean disposal; disposed is distinct from faulted.");
            InvokeSkippedCallback(fixture.Store);
            fixture.Store.Dispose();
            Check.Equal(1, fixture.Source.CloseAttempts, "Disposed callback/repeated Dispose do not touch resources again.");
            return "All storage operations reject after disposal, including Count, empty collection/search/copy paths, object codecs and deferred iteration; telemetry remains readable and source closes once.";
        });
    }

    private static void InlineFailure(CheckContext ctx, string name, ushort threshold, int idle)
    {
        ctx.Run("storage.auto." + name, () =>
        {
            RequireApi();
            using var fixture = new Fixture();
            fixture.Store.FlushUpdates = threshold;
            fixture.Store.FlushIdleMilliseconds = idle;
            var failure = new IOException(name + " flush sentinel");
            if (threshold == 2) Check.Equal(28L, fixture.Store.Add(new byte[] { 1, 2, 3 }), "First pending mutation position.");
            fixture.Source.FlushError = failure;
            long position;
            lock (fixture.Store.SynchronizationRoot)
                position = fixture.Store.Add(new byte[] { 8, 9, 10, 11 });
            Check.Equal(threshold == 2 ? 55L : 28L, position, "Committed mutation returns its exact position despite inline auto flush failure.");
            Check.That(ReferenceEquals(failure, AutoError(fixture.Store)), "Exact automatic failure is visible before mutation returns.");
            Check.Equal(Environment.CurrentManagedThreadId, fixture.Source.FlushThread, "Inline automatic flush remains on the caller thread, including under an outer gate.");
            Check.Equal(1, fixture.Source.FlushAttempts, "Exactly one inline automatic attempt.");
            Check.Equal(threshold == 2 ? 2 : 1, Pending(fixture.Store), "Failed automatic flush retains every successful pending mutation, including idle=0.");
            AssertFault(fixture, failure);
            int accesses = fixture.Source.AccessCount, codecs = fixture.Serializer.Calls;
            foreach (Action operation in Operations(fixture.Store))
            {
                InvalidOperationException rejected = Check.Throws<InvalidOperationException>(operation, "Next storage entry must fail-stop.");
                Check.That(ReferenceEquals(failure, rejected.InnerException), "Later rejection points to the exact first physical failure.");
            }
            Check.Equal(accesses, fixture.Source.AccessCount, "Faulted entries perform no further source I/O.");
            Check.Equal(codecs, fixture.Serializer.Calls, "Faulted object overloads invoke no codec.");
            Check.That(ReferenceEquals(failure, AutoError(fixture.Store)), "Rejected explicit Flush does not replace auto telemetry.");
            InvokeSkippedCallback(fixture.Store);
            Check.That(ReferenceEquals(failure, AutoError(fixture.Store)), "Skipped faulted callback preserves the latest actual automatic attempt.");
            AssertBytes(fixture.Source.ToArray(), threshold == 2 ? [(28L, "010203"), (55L, "08090A0B")] : [(28L, "08090A0B")]);
            return "Committed ID returned after inline automatic failure; exact error observable immediately, pending count retained, every later storage entry rejects before codecs/I/O, and clean independent memory snapshot reopen verifies exact committed bytes. Synthetic I/O only.";
        });
    }

    private static void AssertSourceReentryRejected(Fixture fixture)
    {
        int accesses = fixture.Source.AccessCount;
        foreach (Action operation in new Action[] { fixture.Store.Dispose, fixture.Store.Flush,
            () => fixture.Store.Add(new byte[] { 0xFF }), () => _ = fixture.Store.Count })
            Check.Throws<InvalidOperationException>(operation, "Same-owner raw-source callback must reject recursive operations before wrapper state change.");
        Check.Equal(accesses, fixture.Source.AccessCount, "Reentrant source callback reached no additional raw operation.");
    }

    private static IEnumerable<Action> Operations(SynchronizedDataStorage store)
    {
        yield return () => _ = store.Count;
        yield return () => _ = store.ReadPositions.ToArray();
        yield return () => _ = store.ReadData.ToArray();
        yield return () => _ = store.ReadObjects.ToArray();
        yield return store.Flush;
        yield return () => store.GetData(28);
        yield return () => _ = store[28];
        yield return () => store.Add(new byte[] { 1 });
        yield return () => store.Add((object)1);
        yield return () => store.Update(28, new byte[] { 1 });
        yield return () => store.Update(28, (object)1);
        yield return () => store.Remove(28);
        yield return () => store.GetHeaderValue<int>(0);
        yield return () => store.SetHeaderValue(0, 1);
        yield return () => store.UpdateHeaderValue<int>(0, value => value + 1);
        yield return () => { using var destination = new MemoryStream(); store.CopyTo(destination); };
        yield return store.Clear;
        yield return () => store.Contains(1);
        yield return () => store.CopyTo(new object?[4], 0);
        yield return () => ((ICollection<object?>)store).Remove(1);
        yield return () => { using IEnumerator<object?> values = store.GetEnumerator(); values.MoveNext(); };
    }

    private static void AssertFault(Fixture fixture, Exception expected)
    {
        Check.That(Faulted(fixture.Store), "Synchronized owner forwards terminal core fault state.");
        Check.That(ReferenceEquals(expected, FirstError(fixture.Store)), "Synchronized owner forwards the exact first core failure.");
        Check.That(ReferenceEquals(expected, Value(fixture.Store.Storage, "StorageException")), "Wrapper and core first-fault properties agree.");
    }

    private static void AssertBytes(byte[] snapshot, (long Position, string Hex)[] expected)
    {
        using var source = new MemoryStream(snapshot, writable: false);
        using var reopened = new DataStorage(source, headerSize: 16);
        KeyValuePair<long, byte[]>[] actual = reopened.ReadData.ToArray();
        Check.Equal(expected.Length, actual.Length, "Independent snapshot reopen record count.");
        for (int index = 0; index < expected.Length; index++)
        {
            Check.Equal(expected[index].Position, actual[index].Key, "Independent exact record position.");
            Check.Equal(expected[index].Hex, Convert.ToHexString(actual[index].Value), "Independent exact raw payload bytes.");
        }
    }

    private static void RequireApi()
    {
        RequireProperty(typeof(SynchronizedDataStorage), "LastAutoFlushException", typeof(Exception));
        RequireProperty(typeof(SynchronizedDataStorage), "StorageException", typeof(Exception));
        RequireProperty(typeof(SynchronizedDataStorage), "IsFaulted", typeof(bool));
    }

    private static PropertyInfo RequireProperty(Type type, string name, Type expected)
    {
        PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException(type.Name, name);
        Check.That(property.PropertyType == expected && property.CanRead && !property.CanWrite, name + " must be a public read-only " + expected.Name + " property.");
        return property;
    }

    private static object? Value(object owner, string name) =>
        (owner.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance) ?? throw new MissingMemberException(owner.GetType().Name, name)).GetValue(owner);
    private static Exception? AutoError(SynchronizedDataStorage store) => (Exception?)Value(store, "LastAutoFlushException");
    private static Exception? FirstError(SynchronizedDataStorage store) => (Exception?)Value(store, "StorageException");
    private static bool Faulted(SynchronizedDataStorage store) => (bool)(Value(store, "IsFaulted") ?? throw new InvalidDataException("Missing fault state."));
    private static int Pending(SynchronizedDataStorage store)
    {
        lock (store.SynchronizationRoot)
            return (int)(Field("unflushedUpdates").GetValue(store) ?? throw new InvalidDataException("Missing pending count."));
    }
    private static FieldInfo Field(string name) => typeof(SynchronizedDataStorage).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(nameof(SynchronizedDataStorage), name);
    private static Timer Timer(SynchronizedDataStorage store) => (Timer)(Field("flushTimer").GetValue(store) ?? throw new InvalidDataException("Missing actual timer."));
    private static void Quiesce(SynchronizedDataStorage store)
    {
        Task completed = Timer(store).DisposeAsync().AsTask();
        Check.That(completed.Wait(WaitLimit), "Actual timer did not quiesce within five seconds.");
        completed.GetAwaiter().GetResult();
    }
    private static void InvokeSkippedCallback(SynchronizedDataStorage store)
    {
        // Deterministic private-callback seam for the skip branch only; real idle timers are covered separately.
        MethodInfo callback = typeof(SynchronizedDataStorage).GetMethod("FlushTimerCallback", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(SynchronizedDataStorage), "FlushTimerCallback");
        callback.Invoke(store, [null]);
    }

    private sealed class Fixture : IDisposable
    {
        public ProbeStream Source { get; } = new();
        public ProbeSerializer Serializer { get; } = new();
        public SynchronizedDataStorage Store { get; }
        public Fixture()
        {
            Store = new SynchronizedDataStorage(new DataStorage(Source, headerSize: 16, serializer: Serializer));
            Store.FlushIdleMilliseconds = -1;
        }
        public void Dispose() => Store.Dispose();
    }

    private sealed class ProbeSerializer : IDataSerializer
    {
        public int Calls { get; private set; }
        public Exception? Error { get; set; }
        public void Serialize(Stream stream, object? value)
        {
            Calls++;
            if (Error != null) throw Error;
            Span<byte> data = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(data, (int)value!);
            stream.Write(data);
        }
        public object Deserialize(Stream stream)
        {
            Calls++;
            Span<byte> data = stackalloc byte[4];
            stream.ReadExactly(data);
            return BinaryPrimitives.ReadInt32LittleEndian(data);
        }
    }

    // A test-owned memory source, toggled only after storage construction. No files, OS-fault or durability claims.
    private sealed class ProbeStream : MemoryStream
    {
        private int accesses, flushAttempts, closeAttempts, flushThread;
        public int AccessCount => Volatile.Read(ref accesses);
        public int FlushAttempts => Volatile.Read(ref flushAttempts);
        public int CloseAttempts => Volatile.Read(ref closeAttempts);
        public int FlushThread => Volatile.Read(ref flushThread);
        public Exception? FlushError { get; set; }
        public Exception? WriteError { get; set; }
        public Exception? CloseError { get; set; }
        public Action? OnFlush { get; set; }
        public ManualResetEventSlim Flushed { get; } = new();
        public override long Length { get { Interlocked.Increment(ref accesses); return base.Length; } }
        public override long Position
        {
            get { Interlocked.Increment(ref accesses); return base.Position; }
            set { Interlocked.Increment(ref accesses); base.Position = value; }
        }
        public override long Seek(long offset, SeekOrigin origin) { Interlocked.Increment(ref accesses); return base.Seek(offset, origin); }
        public override int Read(byte[] buffer, int offset, int count) { Interlocked.Increment(ref accesses); return base.Read(buffer, offset, count); }
        public override int Read(Span<byte> buffer) { Interlocked.Increment(ref accesses); return base.Read(buffer); }
        public override void Write(byte[] buffer, int offset, int count)
        {
            Interlocked.Increment(ref accesses);
            if (WriteError != null) throw WriteError;
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Interlocked.Increment(ref accesses);
            if (WriteError != null) throw WriteError;
            base.Write(buffer);
        }
        public override void SetLength(long value) { Interlocked.Increment(ref accesses); base.SetLength(value); }
        public override void Flush()
        {
            Interlocked.Increment(ref accesses);
            Interlocked.Increment(ref flushAttempts);
            Volatile.Write(ref flushThread, Environment.CurrentManagedThreadId);
            try
            {
                OnFlush?.Invoke();
                if (FlushError != null) throw FlushError;
                base.Flush();
            }
            finally { Flushed.Set(); }
        }
        protected override void Dispose(bool disposing)
        {
            if (!disposing) { base.Dispose(false); return; }
            Interlocked.Increment(ref accesses);
            Interlocked.Increment(ref closeAttempts);
            base.Dispose(true);
            if (CloseError != null) throw CloseError;
        }
    }
}
