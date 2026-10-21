using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using RtTools.Logger;
using IntKey = RtTools.Logger.Key.KeyInt64;

namespace HistoryAcceptance;

internal static class ResourceLifetimeChecks
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    public static void Contracts(CheckContext ctx)
    {
        ctx.Run("resources.timelog-client-extension-open", () =>
        {
            string mask = Mask(ctx, "client-extension");
            string main = Path.ChangeExtension(mask, ".tlog");
            string extension = Path.ChangeExtension(mask, ".tldata");
            File.WriteAllBytes(main, []);
            Check.Throws<FileNotFoundException>(() =>
            {
                using var ignored = new TimeLogClient<byte[]>(main, extension, reader: Identity);
            }, "Opening an absent extension must fail.");
            AssertExclusiveOpen(main);
            return "Missing extension throws FileNotFoundException; the already-opened main reader is immediately released, proven by exclusive read/write reopen without GC.";
        });

        ctx.Run("resources.timelog-writer-extension-open", () =>
        {
            string mask = Mask(ctx, "writer-extension");
            string main = Path.ChangeExtension(mask, ".tlog");
            string extension = Path.ChangeExtension(mask, ".tldata");
            File.WriteAllBytes(main, []);
            using (var blocker = new FileStream(extension, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                Check.Throws<IOException>(() =>
                {
                    using var ignored = new TimeLog<byte[]>(mask, reader: Identity, writer: Identity);
                }, "The locked extension must reject writer construction.");
                AssertExclusiveOpen(main);
            }
            AssertExclusiveOpen(extension);
            return "Locked extension rejects writer construction; the acquired main handle is immediately released while the extension remains locked; both files reopen exclusively after cleanup.";
        });

        ctx.Run("resources.timelog-writer-invalid-timestamp", () =>
        {
            string mask = Mask(ctx, "writer-timestamp");
            string main = Path.ChangeExtension(mask, ".tlog");
            string extension = Path.ChangeExtension(mask, ".tldata");
            byte[] bytes = new byte[TimeLog<byte[]>.RecordSize];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, long.MaxValue);
            File.WriteAllBytes(main, bytes);
            File.WriteAllBytes(extension, []);
            Check.Throws<ArgumentException>(() =>
            {
                using var ignored = new TimeLog<byte[]>(mask, reader: Identity, writer: Identity);
            }, "An invalid persisted timestamp must reject writer construction.");
            AssertExclusiveOpen(main);
            AssertExclusiveOpen(extension);
            Check.That(File.ReadAllBytes(main).SequenceEqual(bytes), "Failed open must preserve the invalid input bytes.");
            return "Invalid last timestamp throws ArgumentException and preserves input bytes; acquired file handles are immediately released without GC.";
        });

        ctx.Run("resources.log-timer-throwing-diagnostic", () =>
        {
            string mask = Mask(ctx, "log-timer-diagnostic");
            var publicationFailure = new IOException("resource-check-publication-" + Guid.NewGuid().ToString("N"));
            var diagnosticFailure = new InvalidOperationException("resource-check-listener-" + Guid.NewGuid().ToString("N"));
            var log = new Log<FailingRecord, IntKey>(mask, autoWriteMilliseconds: Timeout.Infinite);
            var listener = new ThrowingListener(publicationFailure, diagnosticFailure);
            Timer? timer = null;
            int callerThread = Environment.CurrentManagedThreadId;
            bool originalAutoFlush = Trace.AutoFlush;
            bool originalGlobalLock = Trace.UseGlobalLock;
            try
            {
                FieldInfo timerField = typeof(Log<FailingRecord, IntKey>).GetField("flushTimer", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidDataException("The actual Log timer is missing.");
                timer = (Timer)(timerField.GetValue(log) ?? throw new InvalidDataException("The actual Log timer is null."));
                Trace.Listeners.Add(listener);
                log.Append(new FailingRecord { Key = new IntKey { Value = 1 }, Failure = publicationFailure });
                Check.That(timer.Change(0, Timeout.Infinite), "The actual Log timer must accept an immediate one-shot callback.");
                Check.That(listener.Wait(WaitLimit), "The real timer did not reach the throwing diagnostic listener within five seconds.");
                Task stopped = timer.DisposeAsync().AsTask();
                Check.That(stopped.Wait(WaitLimit), "The real timer callback did not finish after its diagnostic listener threw.");
                Check.Equal(1, listener.CallCount, "The unique publication failure must reach the throwing listener exactly once.");
                Check.That(listener.CallbackThread != callerThread, "The real timer callback must execute on a different managed thread.");
                Check.That(ReferenceEquals(publicationFailure, log.WriteError), "The original storage/codec failure must remain the Log WriteError.");
                InvalidOperationException rejection = Check.Throws<InvalidOperationException>(() => log.Publish(), "The failed Log must retain its fail-stop behavior.");
                Check.That(ReferenceEquals(publicationFailure, rejection.InnerException), "The diagnostic exception must not replace the original failure.");
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Trace.AutoFlush = originalAutoFlush;
                Trace.UseGlobalLock = originalGlobalLock;
                Task stopped = timer?.DisposeAsync().AsTask() ?? Task.CompletedTask;
                if (!stopped.Wait(WaitLimit))
                {
                    // Retain the listener signal and the log until an outstanding callback has finished.
                    _ = stopped.ContinueWith(_ =>
                    {
                        listener.Dispose();
                        try { log.Dispose(); } catch { }
                    }, TaskScheduler.Default);
                    throw new TimeoutException("Log timer did not quiesce; resource cleanup was deferred until callback completion.");
                }
                listener.Dispose();
                try { log.Dispose(); }
                catch (InvalidOperationException error) when (ReferenceEquals(error.InnerException, publicationFailure)) { }
            }
            foreach (string extension in new[] { ".data", ".extra", ".buffer", ".index" })
                AssertExclusiveOpen(Path.ChangeExtension(mask, extension));
            return "Actual Log timer publication preserves its original IOException, contains an intentionally throwing TraceListener, completes on a worker thread, remains fail-stop, and releases all four storage handles on disposal; trace state restored.";
        });

        ctx.Run("resources.log-timer-diagnostic-reentry", () =>
        {
            string mask = Mask(ctx, "log-timer-reentry");
            var publicationFailure = new IOException("resource-check-reentry-" + Guid.NewGuid().ToString("N"));
            var log = new Log<FailingRecord, IntKey>(mask, autoWriteMilliseconds: Timeout.Infinite, deleteSupported: true);
            var rejections = new List<(string Operation, Exception? Error)>();
            bool enumerated = false;
            IEnumerable<FailingRecord> Batch()
            {
                enumerated = true;
                yield return new FailingRecord { Key = new IntKey { Value = 2 }, Failure = publicationFailure };
            }
            void Probe(string operation, Action action)
            {
                Exception? error = null;
                try { action(); }
                catch (Exception failure) { error = failure; }
                rejections.Add((operation, error));
            }
            var listener = new ReentryListener(publicationFailure, () =>
            {
                // Dispose is first: an unguarded call would wait for the timer callback that is currently running it.
                Probe("Dispose", log.Dispose);
                Probe("Append", () => log.Append(new FailingRecord { Key = new IntKey { Value = 2 }, Failure = publicationFailure }));
                Probe("Append batch", () => log.Append(Batch()));
                Probe("Publish", log.Publish);
                Probe("Flush", () => log.Flush());
                Probe("DeleteAll", () => log.DeleteAll(new IntKey { Value = 0 }, new IntKey { Value = 3 }));
            });
            Timer? timer = null;
            bool originalAutoFlush = Trace.AutoFlush;
            bool originalGlobalLock = Trace.UseGlobalLock;
            int callerThread = Environment.CurrentManagedThreadId;
            try
            {
                FieldInfo timerField = typeof(Log<FailingRecord, IntKey>).GetField("flushTimer", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidDataException("The actual Log timer is missing.");
                timer = (Timer)(timerField.GetValue(log) ?? throw new InvalidDataException("The actual Log timer is null."));
                Trace.Listeners.Add(listener);
                log.Append(new FailingRecord { Key = new IntKey { Value = 1 }, Failure = publicationFailure });
                Check.That(timer.Change(0, Timeout.Infinite), "The actual Log timer must accept an immediate one-shot callback.");
                Check.That(listener.Wait(WaitLimit), "Diagnostic reentry did not return within five seconds; Dispose may be waiting on its own timer callback.");
                Check.That(timer.DisposeAsync().AsTask().Wait(WaitLimit), "The actual timer callback must finish after rejecting diagnostic reentry.");
                Check.Equal(1, listener.CallCount, "Exactly one diagnostic callback must attempt reentry.");
                Check.That(listener.CallbackThread != callerThread, "Diagnostic reentry must execute inside the real timer worker callback.");
                Check.That(listener.CallbackFailure is null, "The diagnostic probe must complete all attempted operations.");
                Check.Equal(6, rejections.Count, "All six same-log disposal/mutation operations must be attempted.");
                foreach (var rejection in rejections)
                    Check.That(rejection.Error is InvalidOperationException && rejection.Error.Message.Contains("timer diagnostic callback", StringComparison.Ordinal),
                        rejection.Operation + " must be rejected by the diagnostic reentry guard.");
                Check.That(!enumerated, "Diagnostic batch Append must reject before running caller enumeration.");
                Check.That(ReferenceEquals(publicationFailure, log.WriteError), "Reentry rejection must preserve the original publication failure.");
                InvalidOperationException disposal = Check.Throws<InvalidOperationException>(log.Dispose, "External Dispose must still report the retained storage failure and close resources.");
                Check.That(ReferenceEquals(publicationFailure, disposal.InnerException), "External disposal must preserve the original failure.");
                foreach (string extension in new[] { ".data", ".extra", ".buffer", ".index" })
                    AssertExclusiveOpen(Path.ChangeExtension(mask, extension));
            }
            finally
            {
                Trace.Listeners.Remove(listener);
                Trace.AutoFlush = originalAutoFlush;
                Trace.UseGlobalLock = originalGlobalLock;
                Task stopped = timer?.DisposeAsync().AsTask() ?? Task.CompletedTask;
                if (!stopped.Wait(WaitLimit))
                {
                    _ = stopped.ContinueWith(_ =>
                    {
                        listener.Dispose();
                        try { log.Dispose(); } catch { }
                    }, TaskScheduler.Default);
                    throw new TimeoutException("Reentrant Log timer did not quiesce; cleanup was deferred until callback completion.");
                }
                listener.Dispose();
                try { log.Dispose(); }
                catch (InvalidOperationException error) when (ReferenceEquals(error.InnerException, publicationFailure)) { }
            }
            return "Actual timer diagnostic rejects same-log Dispose, both Append overloads, Publish, Flush and DeleteAll before mutation or batch enumeration; callback completes within five seconds, original WriteError remains, external Dispose closes all four handles, and trace state is restored.";
        });
    }

    private static byte[] Identity(byte[] data) => data;

    private static string Mask(CheckContext ctx, string name)
    {
        string directory = Path.Combine(ctx.DataRoot, "resource-lifetime", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "history");
    }

    private static void AssertExclusiveOpen(string path)
    {
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Check.That(exclusive.CanRead && exclusive.CanWrite, "The file must immediately support an exclusive read/write open: " + path);
    }

    private sealed class FailingRecord : DataRecord<IntKey>
    {
        public FailingRecord() { }
        public IOException? Failure { get; init; }
        public override IntKey Key { get; set; } = new();
        public override byte[] Data
        {
            get => throw ((Exception?)Failure ?? new InvalidOperationException("Missing injected publication failure."));
            set => throw new NotSupportedException();
        }
    }

    private sealed class ThrowingListener(Exception expectedPublicationFailure, Exception diagnosticFailure) : TraceListener
    {
        private readonly ManualResetEventSlim observed = new(false);
        private int callCount;
        private int callbackThread;
        public int CallCount => Volatile.Read(ref this.callCount);
        public int CallbackThread => Volatile.Read(ref this.callbackThread);
        public override bool IsThreadSafe => true;
        public bool Wait(TimeSpan timeout) => this.observed.Wait(timeout);
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { }

        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args)
        {
            if (eventType != TraceEventType.Error || args is not { Length: 1 } || !ReferenceEquals(expectedPublicationFailure, args[0])) return;
            Interlocked.Increment(ref this.callCount);
            Volatile.Write(ref this.callbackThread, Environment.CurrentManagedThreadId);
            this.observed.Set();
            throw diagnosticFailure;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) this.observed.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class ReentryListener(Exception expectedPublicationFailure, Action action) : TraceListener
    {
        private readonly ManualResetEventSlim observed = new(false);
        private int callCount;
        private int callbackThread;
        private Exception? callbackFailure;
        public int CallCount => Volatile.Read(ref this.callCount);
        public int CallbackThread => Volatile.Read(ref this.callbackThread);
        public Exception? CallbackFailure => Volatile.Read(ref this.callbackFailure);
        public override bool IsThreadSafe => true;
        public bool Wait(TimeSpan timeout) => this.observed.Wait(timeout);
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { }

        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args)
        {
            if (eventType != TraceEventType.Error || args is not { Length: 1 } || !ReferenceEquals(expectedPublicationFailure, args[0])) return;
            Interlocked.Increment(ref this.callCount);
            Volatile.Write(ref this.callbackThread, Environment.CurrentManagedThreadId);
            try { action(); }
            catch (Exception failure) { Volatile.Write(ref this.callbackFailure, failure); }
            finally { this.observed.Set(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) this.observed.Dispose();
            base.Dispose(disposing);
        }
    }
}
