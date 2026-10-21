using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RtTools.Logger;

namespace HistoryAcceptance;

internal static class TimeLogFlushChecks
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    public static void Contracts(CheckContext ctx)
    {
        ctx.Run("timelog.flush.manual-control", () =>
        {
            string marker = Marker();
            using var capture = new ErrorCapture(marker);
            using var fixture = new ProbeFixture(ctx, "manual-control", capture, null, null);
            fixture.Log.Flush();
            fixture.AssertAttempts(1, 1, 0, 0, "main.Flush", "extended.Flush");
            capture.AssertNoErrors();
            fixture.AssertWriterActive();
            return "Both streams flushed once in order, no diagnostic error; no synthetic storage or durability claim.";
        });

        ManualFailure(ctx, "main", true, false);
        ManualFailure(ctx, "extended", false, true);
        ManualFailure(ctx, "both", true, true);

        ctx.Run("timelog.flush.timer-state-recovery", () =>
        {
            string marker = Marker();
            var injected = new IOException(marker + ":timer-main");
            using var capture = new ErrorCapture(marker);
            using var fixture = new ProbeFixture(ctx, "timer-main-error", capture, injected, null);
            int callerThread = Environment.CurrentManagedThreadId;
            Check.That(LastAutoError(fixture.Log) == null, "Automatic failure state must initially be empty.");
            Check.That(fixture.Timer.Change(0, Timeout.Infinite), "The actual one-shot timer must accept Change(0, Infinite).");
            Check.That(capture.WaitForError(WaitLimit), "Actual timer callback did not report the unique injected error within five seconds.");
            Check.That(ReferenceEquals(injected, LastAutoError(fixture.Log)), "Automatic failure state must expose the exact flush exception.");
            capture.AssertError("error on flush buffer {0}", injected);
            capture.AssertCallbackThreadDifferentFrom(callerThread);
            ExpectFailure(fixture.Log.Flush, injected);
            Check.That(ReferenceEquals(injected, LastAutoError(fixture.Log)), "A failed explicit Flush must not replace automatic failure state.");
            fixture.MainProbe.FlushError = null;
            fixture.Log.Flush();
            Check.That(ReferenceEquals(injected, LastAutoError(fixture.Log)), "A successful explicit Flush must not clear automatic failure state.");
            Check.That(fixture.Timer.Change(0, Timeout.Infinite), "The same actual timer must accept a later successful automatic attempt.");
            Check.That(SpinWait.SpinUntil(() => LastAutoError(fixture.Log) == null, WaitLimit), "A successful automatic attempt must clear failure state within five seconds.");
            fixture.QuiesceTimer();
            fixture.AssertAttempts(4, 4, 0, 0,
                "main.Flush", "extended.Flush", "main.Flush", "extended.Flush",
                "main.Flush", "extended.Flush", "main.Flush", "extended.Flush");
            fixture.AssertWriterActive();
            capture.AssertError("error on flush buffer {0}", injected);
            return "Actual timer failure exposed and traced on a different thread; both streams attempted. Explicit success/failure preserve the stored automatic error; later successful auto-attempt clears it. Timer quiesced before cleanup.";
        });

        ctx.Run("timelog.flush.timer-both-errors", () =>
        {
            string marker = Marker();
            var main = new IOException(marker + ":main");
            var extended = new IOException(marker + ":extended");
            using var capture = new ErrorCapture(marker);
            using var fixture = new ProbeFixture(ctx, "timer-both-errors", capture, main, extended);
            Check.That(fixture.Timer.Change(0, Timeout.Infinite), "Actual timer scheduling.");
            Check.That(capture.WaitForError(WaitLimit), "Automatic aggregate was not reported within five seconds.");
            fixture.QuiesceTimer();
            Exception failure = LastAutoError(fixture.Log) ?? throw new InvalidDataException("Missing automatic aggregate.");
            AssertAggregate(failure, main, extended);
            capture.AssertError("error on flush buffer {0}", failure);
            fixture.AssertAttempts(1, 1, 0, 0, "main.Flush", "extended.Flush");
            return "Both automatic flush failures retained in ordered AggregateException; same aggregate exposed programmatically and traced once.";
        });

        TimerListenerFailure(ctx);
        TimerReentrantDispose(ctx);
        DisposeFailure(ctx, "main", true, false, false, false);
        DisposeFailure(ctx, "extended", false, true, false, false);
        DisposeFailure(ctx, "both", true, true, false, false);
        DisposeFailure(ctx, "close", false, false, true, true);
        DisposeFailure(ctx, "four", true, true, true, true);

        ctx.Run("timelog.flush.disposed-operations", () =>
        {
            string marker = Marker();
            using var capture = new ErrorCapture(marker);
            using var fixture = new ProbeFixture(ctx, "disposed-operations", capture, null, null);
            fixture.Log.Dispose();
            AssertDisposedOperations(fixture.Log);
            Check.That(LastAutoError(fixture.Log) == null, "Diagnostic property remains readable after disposal.");
            fixture.Log.Dispose();
            fixture.AssertAttempts(1, 1, 1, 1, "main.Flush", "extended.Flush", "main.Dispose", "extended.Dispose");
            capture.AssertNoErrors();
            return "Flush, Count, direct/convenience Append and CreateClient reject disposed instances; repeated Dispose is inert; automatic diagnostic property remains readable.";
        });
    }

    private static void ManualFailure(CheckContext ctx, string target, bool failMain, bool failExtended)
    {
        ctx.Run("timelog.flush.manual-" + target + "-error", () =>
        {
            string marker = Marker();
            var main = new IOException(marker + ":main");
            var extended = new IOException(marker + ":extended");
            using var capture = new ErrorCapture(marker);
            using var fixture = new ProbeFixture(ctx, "manual-" + target + "-error", capture,
                failMain ? main : null, failExtended ? extended : null);
            ExpectFailure(fixture.Log.Flush, Selected(failMain, main, failExtended, extended));
            fixture.AssertAttempts(1, 1, 0, 0, "main.Flush", "extended.Flush");
            capture.AssertNoErrors();
            fixture.AssertWriterActive();
            fixture.RestoreStreams();
            fixture.Log.Flush();
            capture.AssertNoErrors();
            return "Explicit " + target + " failure reaches caller unchanged (ordered aggregate for two); both flushes attempted, writer remains active, no duplicate Trace report; later normal Flush succeeds.";
        });
    }

    private static void DisposeFailure(CheckContext ctx, string target, bool failMainFlush, bool failExtendedFlush,
        bool failMainClose, bool failExtendedClose)
    {
        ctx.Run("timelog.flush.dispose-" + target + "-error", () =>
        {
            string marker = Marker();
            var main = new IOException(marker + ":main-flush");
            var extended = new IOException(marker + ":extended-flush");
            var mainClose = new IOException(marker + ":main-close");
            var extendedClose = new IOException(marker + ":extended-close");
            using var capture = new ErrorCapture(marker);
            using var fixture = new ProbeFixture(ctx, "dispose-" + target + "-error", capture,
                failMainFlush ? main : null, failExtendedFlush ? extended : null);
            fixture.MainProbe.CloseError = failMainClose ? mainClose : null;
            fixture.ExtendedProbe.CloseError = failExtendedClose ? extendedClose : null;
            List<Exception> expected = [.. Selected(failMainFlush, main, failExtendedFlush, extended),
                .. Selected(failMainClose, mainClose, failExtendedClose, extendedClose)];
            ExpectFailure(fixture.Log.Dispose, expected.ToArray());
            fixture.AssertAttempts(1, 1, 1, 1, "main.Flush", "extended.Flush", "main.Dispose", "extended.Dispose");
            capture.AssertNoErrors();
            Check.That(!fixture.MainProbe.CanWrite && !fixture.ExtendedProbe.CanWrite,
                "Both actual probe handles must be closed before errors reach the Dispose caller.");
            Check.Equal(1, fixture.DisposedState, "TimeLog must have entered its disposed state.");
            AssertDisposedOperations(fixture.Log);
            fixture.Log.Dispose();
            fixture.AssertAttempts(1, 1, 1, 1, "main.Flush", "extended.Flush", "main.Dispose", "extended.Dispose");
            return "Dispose propagates all " + expected.Count + " injected " + target + " failure(s) after attempting both flushes and closes; exact exception identities/order preserved; repeated Dispose inert and disposed operations reject. Close probes throw only after their real handles close.";
        });
    }

    private static void TimerListenerFailure(CheckContext ctx)
    {
        ctx.Run("timelog.flush.timer-listener-throws", () =>
        {
            string marker = Marker();
            var injected = new IOException(marker + ":main");
            using var capture = new ErrorCapture(marker, _ => throw new InvalidOperationException(marker + ":listener"));
            using var fixture = new ProbeFixture(ctx, "timer-listener-throws", capture, injected, null);
            Check.That(fixture.Timer.Change(0, Timeout.Infinite), "Actual timer scheduling.");
            Check.That(capture.WaitForError(WaitLimit), "Throwing listener was not invoked within five seconds.");
            fixture.QuiesceTimer();
            capture.AssertError("error on flush buffer {0}", injected);
            Check.That(ReferenceEquals(injected, LastAutoError(fixture.Log)), "A listener exception must not replace the actual automatic flush failure.");
            fixture.AssertAttempts(1, 1, 0, 0, "main.Flush", "extended.Flush");
            fixture.AssertWriterActive();
            return "Actual timer contains a throwing Trace listener; callback completes, writer remains active, original automatic error remains programmatically available.";
        });
    }

    private static void TimerReentrantDispose(CheckContext ctx)
    {
        ctx.Run("timelog.flush.timer-listener-reentrant-dispose", () =>
        {
            string marker = Marker();
            var injected = new IOException(marker + ":main");
            ProbeFixture? current = null;
            Exception? disposeFailure = null;
            using var capture = new ErrorCapture(marker, _ =>
            {
                try { current!.Log.Dispose(); }
                catch (Exception error) { disposeFailure = error; }
            });
            using var fixture = new ProbeFixture(ctx, "timer-reentrant-dispose", capture, injected, null);
            current = fixture;
            Check.That(fixture.Timer.Change(0, Timeout.Infinite), "Actual timer scheduling.");
            Check.That(capture.WaitForError(WaitLimit), "Synchronous listener->Dispose did not complete within five seconds (possible self-deadlock).");
            fixture.QuiesceTimer();
            Check.That(ReferenceEquals(injected, disposeFailure), "Reentrant Dispose must propagate its final flush error after cleanup.");
            fixture.AssertAttempts(2, 2, 1, 1, "main.Flush", "extended.Flush", "main.Flush", "extended.Flush", "main.Dispose", "extended.Dispose");
            Check.That(!fixture.MainProbe.CanWrite && !fixture.ExtendedProbe.CanWrite, "Reentrant Dispose must close both handles.");
            capture.AssertError("error on flush buffer {0}", injected);
            Check.That(ReferenceEquals(injected, LastAutoError(fixture.Log)), "Disposed instance retains its latest automatic failure for inspection.");
            return "A Trace listener synchronously disposes the same writer from its real timer callback without self-deadlock; both files flushed/closed, disposal error propagated to listener, automatic error retained.";
        });
    }

    private static Exception? LastAutoError(TimeLog<byte[]> log)
    {
        PropertyInfo property = typeof(TimeLog<byte[]>).GetProperty("LastAutoFlushException", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException("TimeLog must expose public LastAutoFlushException.");
        Check.That(property.CanRead && !property.CanWrite && property.PropertyType == typeof(Exception), "Automatic flush diagnostic must be a read-only Exception property.");
        return (Exception?)property.GetValue(log);
    }

    private static void AssertDisposedOperations(TimeLog<byte[]> log)
    {
        Check.Throws<ObjectDisposedException>(log.Flush, "Disposed Flush.");
        Check.Throws<ObjectDisposedException>(() => _ = log.Count, "Disposed Count.");
        Check.Throws<ObjectDisposedException>(() => log.Append(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), []), "Disposed direct Append.");
        Check.Throws<ObjectDisposedException>(() => log.Append([]), "Disposed convenience Append.");
        Check.Throws<ObjectDisposedException>(() => { using var ignored = log.CreateClient(); }, "Disposed CreateClient.");
    }

    private static Exception[] Selected(bool firstEnabled, Exception first, bool secondEnabled, Exception second) =>
        firstEnabled ? secondEnabled ? [first, second] : [first] : secondEnabled ? [second] : [];

    private static void ExpectFailure(Action action, params Exception[] expected)
    {
        Check.That(expected.Length > 0, "At least one synthetic failure is required.");
        if (expected.Length == 1)
        {
            IOException actual = Check.Throws<IOException>(action, "Single I/O failure must reach caller unchanged.");
            Check.That(ReferenceEquals(expected[0], actual), "Single exception instance must be preserved.");
        }
        else AssertAggregate(Check.Throws<AggregateException>(action, "Multiple failures must reach caller together."), expected);
    }

    private static void AssertAggregate(Exception failure, params Exception[] expected)
    {
        Check.That(failure is AggregateException, "Multiple failures require AggregateException.");
        var aggregate = (AggregateException)failure;
        Check.Equal(expected.Length, aggregate.InnerExceptions.Count, "Aggregated failure count.");
        for (int index = 0; index < expected.Length; index++)
            Check.That(ReferenceEquals(expected[index], aggregate.InnerExceptions[index]), "Exact ordered inner exception " + index + ".");
    }

    private static string Marker() => "TimeLogFlushProbe-" + Guid.NewGuid().ToString("N");

    private sealed class EventOrder
    {
        private readonly object sync = new();
        private readonly List<string> entries = [];
        public void Add(string entry) { lock (sync) entries.Add(entry); }
        public string[] Snapshot() { lock (sync) return entries.ToArray(); }
    }

    private sealed class FlushProbe(string path, string label, EventOrder order, IOException? injected)
        : FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite)
    {
        private int flushAttempts;
        private int disposeAttempts;
        private int disposingProbe;
        private IOException? flushError = injected;
        public int FlushAttempts => Volatile.Read(ref flushAttempts);
        public int DisposeAttempts => Volatile.Read(ref disposeAttempts);
        public IOException? FlushError { get => Volatile.Read(ref flushError); set => Volatile.Write(ref flushError, value); }
        public IOException? CloseError { get; set; }

        public override void Flush()
        {
            Interlocked.Increment(ref flushAttempts);
            order.Add(label + ".Flush");
            IOException? failure = FlushError;
            if (failure != null && Volatile.Read(ref disposingProbe) == 0) throw failure;
            base.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Exchange(ref disposingProbe, 1);
                Interlocked.Increment(ref disposeAttempts);
                order.Add(label + ".Dispose");
            }
            base.Dispose(disposing);
            if (disposing && CloseError != null) throw CloseError; // The actual scratch handle is closed before simulating a close error.
        }
    }

    private sealed record CapturedError(TraceEventType Type, string? Format, Exception? Error, string Message, int ThreadId);

    private sealed class ErrorCapture : TraceListener
    {
        private readonly string marker;
        private readonly Action<Exception>? onError;
        private readonly object sync = new();
        private readonly List<CapturedError> matched = [];
        private readonly ManualResetEventSlim signal = new(false);
        private readonly bool originalAutoFlush = Trace.AutoFlush;
        private readonly bool originalGlobalLock = Trace.UseGlobalLock;
        private readonly int originalIndentLevel = Trace.IndentLevel;
        private readonly int originalIndentSize = Trace.IndentSize;
        private int errorCount;
        private Task? deferSignalDisposal;
        private int disposed;

        public ErrorCapture(string marker, Action<Exception>? onError = null) : base(marker)
        {
            this.marker = marker;
            this.onError = onError;
            Filter = new EventTypeFilter(SourceLevels.All);
            Trace.Listeners.Add(this); // Leave every preexisting listener and its filter/order intact.
        }

        public override bool IsThreadSafe => true;
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { }

        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType,
            int id, string? format, params object?[]? args)
        {
            string message;
            try { message = args == null ? format ?? "" : string.Format(CultureInfo.InvariantCulture, format ?? "", args); }
            catch (FormatException) { message = format ?? ""; }
            Record(eventType, format, args != null && args.Length > 0 ? args[0] as Exception : null, message);
        }

        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType,
            int id, string? message) => Record(eventType, null, null, message ?? "");

        private void Record(TraceEventType type, string? format, Exception? error, string message)
        {
            if (type == TraceEventType.Error) Interlocked.Increment(ref errorCount);
            if (!message.Contains(marker, StringComparison.Ordinal)) return;
            lock (sync) matched.Add(new(type, format, error, message, Environment.CurrentManagedThreadId));
            try { if (error != null) onError?.Invoke(error); }
            finally { signal.Set(); }
        }

        public bool WaitForError(TimeSpan timeout) => signal.Wait(timeout);
        public void DeferSignalDisposalUntil(Task completion) => deferSignalDisposal = completion;

        public void AssertNoErrors()
        {
            Check.Equal(0, Volatile.Read(ref errorCount), "Successful manual Flush must emit no Trace Error.");
            lock (sync) Check.Equal(0, matched.Count, "Successful Flush must emit no injected-marker trace.");
        }

        public void AssertCallbackThreadDifferentFrom(int callerThread)
        {
            lock (sync)
            {
                Check.Equal(1, matched.Count, "A single actual callback must report the injected error.");
                Check.That(matched[0].ThreadId != callerThread, "The actual Timer callback must execute on a different managed thread from its caller.");
            }
        }

        public void AssertError(string format, Exception expected)
        {
            CapturedError[] actual;
            lock (sync) actual = matched.ToArray();
            Check.Equal(1, actual.Length, "Exactly one unique injected exception must be traced.");
            Check.Equal(TraceEventType.Error, actual[0].Type, "Injected exception trace severity.");
            Check.Equal(format, actual[0].Format, "Exact TraceEvent format/params path.");
            Check.That(ReferenceEquals(expected, actual[0].Error), "TraceEvent args[0] must be the exact automatic failure object.");
            Check.That(actual[0].Message.Contains(expected.GetType().FullName!, StringComparison.Ordinal),
                "Trace text must include the automatic failure type.");
            Check.That(actual[0].Message.Contains(expected.Message, StringComparison.Ordinal),
                "Trace text must preserve the exact injected exception message.");
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing || Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { Trace.Listeners.Remove(this); }
            finally
            {
                Trace.AutoFlush = originalAutoFlush;
                Trace.UseGlobalLock = originalGlobalLock;
                Trace.IndentLevel = originalIndentLevel;
                Trace.IndentSize = originalIndentSize;
                Task? completion = deferSignalDisposal;
                if (completion == null || completion.IsCompleted) signal.Dispose();
                else _ = completion.ContinueWith(_ => signal.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                base.Dispose(disposing);
            }
        }
    }

    private sealed class ProbeFixture : IDisposable
    {
        private static readonly FieldInfo MainField = Field("mainStreamW");
        private static readonly FieldInfo ExtendedField = Field("extendedStreamW");
        private static readonly FieldInfo TimerField = Field("flushTimer");
        private static readonly FieldInfo SyncField = Field("syncRoot");
        private static readonly FieldInfo DisposedField = Field("disposed");
        private readonly EventOrder order = new();
        private readonly FileStream originalMain;
        private readonly FileStream originalExtended;
        private readonly object streamLock;
        private readonly ErrorCapture capture;
        private Task? timerDisposal;
        private bool quiesceTimedOut;
        private bool cleaned;
        public TimeLog<byte[]> Log { get; }
        public Timer Timer { get; }
        public FlushProbe MainProbe { get; }
        public FlushProbe ExtendedProbe { get; }
        public int DisposedState => (int)(DisposedField.GetValue(Log) ?? throw new InvalidDataException("Missing disposed field value."));

        public ProbeFixture(CheckContext ctx, string name, ErrorCapture capture, IOException? mainError, IOException? extendedError)
        {
            this.capture = capture;
            string directory = Path.Combine(ctx.DataRoot, "timelog-flush-probes", name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Log = new TimeLog<byte[]>(Path.Combine(directory, "history"), reader: Identity, writer: Identity);
            originalMain = (FileStream)(MainField.GetValue(Log) ?? throw new InvalidDataException("Missing original main stream."));
            originalExtended = (FileStream)(ExtendedField.GetValue(Log) ?? throw new InvalidDataException("Missing original extended stream."));
            Timer = (Timer)(TimerField.GetValue(Log) ?? throw new InvalidDataException("Missing actual flush timer."));
            streamLock = SyncField.GetValue(Log) ?? throw new InvalidDataException("Missing writer synchronization object.");
            // No Append occurs: the actual timer starts unarmed and both actual history files are empty.
            Log.Flush();
            MainProbe = new FlushProbe(Path.Combine(directory, "main.probe"), "main", order, mainError);
            try
            {
                ExtendedProbe = new FlushProbe(Path.Combine(directory, "extended.probe"), "extended", order, extendedError);
                try
                {
                    lock (streamLock)
                    {
                        Check.That(MainField.IsInitOnly && ExtendedField.IsInitOnly, "This seam must target the actual readonly stream fields.");
                        MainField.SetValue(Log, MainProbe);
                        Check.That(ReferenceEquals(MainField.GetValue(Log), MainProbe), "Readonly main stream injection did not take effect.");
                        ExtendedField.SetValue(Log, ExtendedProbe);
                        Check.That(ReferenceEquals(ExtendedField.GetValue(Log), ExtendedProbe), "Readonly extended stream injection did not take effect.");
                    }
                }
                catch
                {
                    try { RestoreStreams(); }
                    finally { ExtendedProbe.Dispose(); }
                    throw;
                }
            }
            catch
            {
                MainProbe.Dispose();
                Log.Dispose();
                originalMain.Dispose();
                originalExtended.Dispose();
                throw;
            }
        }

        public void AssertAttempts(int mainFlush, int extendedFlush, int mainDispose, int extendedDispose, params string[] expectedOrder)
        {
            Check.Equal(mainFlush, MainProbe.FlushAttempts, "Main Flush attempts.");
            Check.Equal(extendedFlush, ExtendedProbe.FlushAttempts, "Extended Flush attempts.");
            Check.Equal(mainDispose, MainProbe.DisposeAttempts, "Main Dispose attempts.");
            Check.Equal(extendedDispose, ExtendedProbe.DisposeAttempts, "Extended Dispose attempts.");
            Check.Equal(string.Join(" -> ", expectedOrder), string.Join(" -> ", order.Snapshot()), "Exact probe call order.");
        }

        public void AssertWriterActive()
        {
            Check.Equal(0, DisposedState, "Caught Flush error must not dispose the TimeLog.");
            Check.That(MainProbe.CanWrite && ExtendedProbe.CanWrite, "Caught Flush error must leave both probe handles open.");
        }

        public void RestoreStreams()
        {
            Check.That(timerDisposal == null || timerDisposal.IsCompletedSuccessfully,
                "An activated timer must finish DisposeAsync before stream restoration.");
            lock (streamLock)
            {
                MainField.SetValue(Log, originalMain);
                ExtendedField.SetValue(Log, originalExtended);
                Check.That(ReferenceEquals(MainField.GetValue(Log), originalMain) &&
                    ReferenceEquals(ExtendedField.GetValue(Log), originalExtended), "Original handles were not restored.");
            }
        }

        public void QuiesceTimer()
        {
            if (timerDisposal == null)
            {
                try { Timer.Change(Timeout.Infinite, Timeout.Infinite); }
                catch (ObjectDisposedException) { }
                timerDisposal = Timer.DisposeAsync().AsTask();
            }
            if (!timerDisposal.Wait(WaitLimit))
            {
                quiesceTimedOut = true;
                throw new TimeoutException("Timer DisposeAsync did not quiesce callbacks within five seconds; active probes must not be restored/disposed yet.");
            }
            timerDisposal.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            if (cleaned) return;
            if (!quiesceTimedOut)
            {
                try { QuiesceTimer(); }
                catch (TimeoutException) { ScheduleDeferredCleanup(); throw; }
            }
            else
            {
                ScheduleDeferredCleanup();
                return; // The earlier timeout already fails the case; do not race its still-running callback.
            }
            CleanupAfterTimer();
        }

        private void ScheduleDeferredCleanup()
        {
            cleaned = true;
            Task completion = timerDisposal ?? throw new InvalidOperationException("Missing timer-disposal task.");
            capture.DeferSignalDisposalUntil(completion);
            _ = completion.ContinueWith(_ =>
            {
                try { CleanupAfterTimer(); }
                catch (Exception error) { Console.Error.WriteLine("Deferred TimeLog Flush probe cleanup failed: " + error); }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void CleanupAfterTimer()
        {
            cleaned = true;
            try
            {
                RestoreStreams();
                Log.Dispose();
            }
            finally
            {
                // A TimeLog already disposed against probes cannot close its saved original handles itself.
                try { originalMain.Dispose(); }
                finally
                {
                    try { originalExtended.Dispose(); }
                    finally
                    {
                        try { if (MainProbe.DisposeAttempts == 0) MainProbe.Dispose(); }
                        finally { if (ExtendedProbe.DisposeAttempts == 0) ExtendedProbe.Dispose(); }
                    }
                }
            }
        }

        private static FieldInfo Field(string name) => typeof(TimeLog<byte[]>).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(TimeLog<byte[]>).FullName, name);

        private static byte[] Identity(byte[] data) => data;
    }
}
