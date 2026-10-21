using System.Reflection;
using System.Text;
using RtTools;
using RtTools.Logger;
using Pair = RtTools.SerializableKeyValuePair<string, int>;
using Logger = RtTools.Logger.DictionaryLogger<string, int>;

namespace HistoryAcceptance;

internal static class DictionaryLoggerChecks
{
    private static readonly DateTime From = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
    private static readonly DateTime To = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
    private static readonly FieldInfo MainField = typeof(TimeLog<Pair[]>).GetField("mainStreamW", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException("TimeLog.mainStreamW");
    private static readonly FieldInfo ExtendedField = typeof(TimeLog<Pair[]>).GetField("extendedStreamW", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException("TimeLog.extendedStreamW");

    public static void Contracts(CheckContext ctx)
    {
        ctx.Run("dictionary-logger.serializable-pair", () =>
        {
            KeyValuePair<string, int> original = new("ключ", 7);
            Pair serializable = original;
            KeyValuePair<string, int> restored = serializable;
            Check.Equal(original, restored, "Implicit pair conversion must preserve key and value.");
            Check.Equal("[ключ,7]", serializable.ToString(), "Serializable pair text.");
            Check.Equal("[,]", default(SerializableKeyValuePair<string?, string?>).ToString(), "Default null pair text.");
            return "Both implicit conversions and populated/default pair text preserve the original helper contract.";
        });

        Lifecycle(ctx, explicitCodecs: false);
        Lifecycle(ctx, explicitCodecs: true);

        ctx.Run("dictionary-logger.transform-and-suppress", () =>
        {
            var paths = Paths(ctx, "transform");
            using (var logger = new Logger(paths.State, paths.History))
            {
                logger.Values.Add("x", 1);
                logger.Values.Add("y", 2);
                Check.That(logger.LogValues(_ => null!) is null, "A null conversion must suppress recording.");
                Check.Equal(0L, logger.Log.Count, "Suppression must not append a snapshot.");
                Pair[] transformed = [new() { Key = "history/z", Value = 99 }, new() { Key = "history/a", Value = 44 }];
                Pair[]? returned = logger.LogValues(snapshot =>
                {
                    AssertMap(snapshot, ("x", 1), ("y", 2));
                    return transformed;
                });
                Check.That(ReferenceEquals(transformed, returned), "LogValues must return the transformed snapshot it accepted.");
                transformed[0] = new Pair { Key = "mutated-after-append", Value = -1 };
                logger.Log.Flush();
                var rows = logger.GetLogged(From, To);
                Check.Equal(1, rows.Length, "Only the transformed snapshot must be present.");
                AssertTransformed(rows[0].Value);
                AssertCurrent(logger, ("x", 1), ("y", 2));
                logger.Values.Storage.Flush();
            }
            using (var logger = new Logger(paths.State, paths.History))
            {
                AssertCurrent(logger, ("x", 1), ("y", 2));
                var rows = logger.GetLogged(From, To);
                Check.Equal(1, rows.Length, "Converted snapshot count after reopen.");
                AssertTransformed(rows[0].Value);
            }
            return "Converter suppression records nothing; transformed sequence/order is encoded during append, survives later array mutation and clean reopen, and does not replace current values.";
        });

        ctx.Run("dictionary-logger.empty-snapshot", () =>
        {
            var paths = Paths(ctx, "empty");
            using (var logger = new Logger(paths.State, paths.History))
            {
                var recorded = Record(logger);
                Check.Equal(0, recorded.Snapshot.Length, "An empty dictionary is a valid non-null snapshot.");
                logger.Values.Add("later", 3);
                Check.Equal(1L, logger.Log.Count, "Adding a current value must not append another snapshot.");
                logger.Values.Storage.Flush();
            }
            using (var logger = new Logger(paths.State, paths.History))
            {
                AssertCurrent(logger, ("later", 3));
                var rows = logger.GetLogged(From, To);
                Check.Equal(1, rows.Length, "Empty snapshot count after reopen.");
                Check.Equal(0, rows[0].Value.Length, "Empty snapshot payload after reopen.");
            }
            return "Empty snapshots remain distinct from suppressed/rejected snapshots and survive default-serializer close/reopen.";
        });

        ctx.Run("dictionary-logger.timestamp-rejection", () =>
        {
            var paths = Paths(ctx, "rejection");
            DateTime future = new(2099, 1, 15, 12, 0, 0, DateTimeKind.Utc);
            Check.That(DateTime.UtcNow < future.AddDays(-1), "The timestamp rejection control must remain in the future.");
            using var logger = new Logger(paths.State, paths.History);
            logger.Values.Add("current", 9);
            Check.That(logger.Log.Append(future, []), "Future control snapshot append.");
            logger.Log.Flush();
            byte[] main = ReadShared(logger.Log.MainFileName);
            byte[] extended = ReadShared(logger.Log.ExtendedFileName);
            int conversions = 0;
            Pair[]? rejected = logger.LogValues(snapshot => { conversions++; return snapshot; });
            Check.That(rejected is null, "TimeLog timestamp rejection must return null rather than claim the snapshot was recorded.");
            Check.Equal(1, conversions, "Snapshot conversion precedes TimeLog timestamp eligibility.");
            logger.Log.Flush();
            Check.Equal(1L, logger.Log.Count, "Rejected snapshot must not advance history count.");
            Check.That(main.AsSpan().SequenceEqual(ReadShared(logger.Log.MainFileName)) &&
                extended.AsSpan().SequenceEqual(ReadShared(logger.Log.ExtendedFileName)), "Rejected snapshot must not change either history file.");
            AssertCurrent(logger, ("current", 9));
            return "A future control timestamp makes convenience append reject; LogValues returns null, Count/files remain unchanged and current values remain intact.";
        });

        ctx.Run("dictionary-logger.query-does-not-flush", () =>
        {
            var paths = Paths(ctx, "query");
            using var logger = new Logger(paths.State, paths.History);
            FileStream original = (FileStream)MainField.GetValue(logger.Log)!;
            using var probe = new CloseProbe(Path.Combine(paths.Directory, "query.probe"), null);
            try
            {
                MainField.SetValue(logger.Log, probe);
                Check.That(ReferenceEquals(probe, MainField.GetValue(logger.Log)), "Query probe installation must take effect.");
                Check.Equal(0, logger.GetLogged(From, To).Length, "Empty query control.");
                Check.Equal(0, probe.FlushAttempts, "GetLogged must not implicitly flush history.");
            }
            finally { MainField.SetValue(logger.Log, original); }
            return "An empty-store test-only Flush counter confirms GetLogged queries without an implicit Log.Flush; original stream is restored before disposal.";
        });

        ctx.Run("dictionary-logger.codec-pair-validation", () =>
        {
            for (int variant = 0; variant < 4; variant++)
            {
                var paths = Paths(ctx, "codec-pair-" + variant);
                Check.Throws<ArgumentException>(() =>
                {
                    using var ignored = variant switch
                    {
                        0 => new Logger(paths.State, paths.History, stateReader: ReadState),
                        1 => new Logger(paths.State, paths.History, stateWriter: WriteState),
                        2 => new Logger(paths.State, paths.History, ReadState, WriteState, historyReader: ReadHistory),
                        _ => new Logger(paths.State, paths.History, ReadState, WriteState, historyWriter: WriteHistory)
                    };
                }, "Both codec pairs must be complete before either store opens.");
                Check.That(!File.Exists(paths.State) && !File.Exists(Path.ChangeExtension(paths.History, ".tlog")) &&
                    !File.Exists(Path.ChangeExtension(paths.History, ".tldata")), "Invalid codec pairs must create no state/history files.");
            }
            return "All four half-specified codec combinations reject before any state or history file is created.";
        });

        ctx.Run("dictionary-logger.failed-state-decoder-cleanup", () =>
        {
            var paths = Paths(ctx, "state-failure");
            using (var values = new DictionaryPersistent<string, int>(paths.State, reader: ReadState, writer: WriteState))
            { values.Add("seed", 5); values.Storage.Flush(); }
            byte[] before = File.ReadAllBytes(paths.State);
            var injected = new FormatException("dictionary-state-decoder-control");
            FormatException actual = Check.Throws<FormatException>(() =>
            {
                using var ignored = new Logger(paths.State, paths.History, _ => throw injected, WriteState);
            }, "State decoder construction error.");
            Check.That(ReferenceEquals(injected, actual), "Constructor must preserve the exact state decoder error.");
            AssertExclusive(paths.State);
            Check.That(before.AsSpan().SequenceEqual(File.ReadAllBytes(paths.State)), "Failed state decode must not rewrite the valid state file.");
            Check.That(!File.Exists(Path.ChangeExtension(paths.History, ".tlog")), "History must not open after state construction fails.");
            return "A failing state reader preserves its original exception and bytes; the owned state handle is released before the caller reopens it exclusively.";
        });

        ctx.Run("dictionary-logger.failed-history-constructor-cleanup", () =>
        {
            var paths = Paths(ctx, "history-failure");
            string extended = Path.ChangeExtension(paths.History, ".tldata");
            using (var barrier = new FileStream(extended, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                Check.Throws<IOException>(() =>
                {
                    using var ignored = new Logger(paths.State, paths.History);
                }, "Locked history extension must fail construction after state opens.");
                Check.That(barrier.CanWrite, "The wrapper must not dispose a caller-owned lock barrier.");
                AssertExclusive(paths.State);
                AssertExclusive(Path.ChangeExtension(paths.History, ".tlog"));
            }
            using (var logger = new Logger(paths.State, paths.History))
            { Check.Equal(0, logger.Values.Count, "Failed constructor leaves an empty reusable state."); Check.Equal(0L, logger.Log.Count, "History can open after the barrier is released."); }
            return "A controlled locked history extension releases the already-created Values resource and the partial TimeLog main handle; the same files subsequently open normally.";
        });

        Disposal(ctx, stateFailure: true, historyFailure: false);
        Disposal(ctx, stateFailure: false, historyFailure: true);
        Disposal(ctx, stateFailure: true, historyFailure: true);

        foreach (string callback in new[] { "update", "state-writer", "snapshot-converter", "history-writer", "history-reader" })
            Reentry(ctx, callback);
    }

    private static void Reentry(CheckContext ctx, string callback)
    {
        ctx.Run("dictionary-logger.reentry-" + callback, () =>
        {
            var paths = Paths(ctx, "reentry-" + callback);
            Logger? target = null;
            bool armed = false;
            void AttemptDispose() { if (armed) target!.Dispose(); }
            var logger = new Logger(paths.State, paths.History, ReadState,
                value => { if (callback == "state-writer") AttemptDispose(); return WriteState(value); },
                bytes => { if (callback == "history-reader") AttemptDispose(); return ReadHistory(bytes); },
                values => { if (callback == "history-writer") AttemptDispose(); return WriteHistory(values); });
            target = logger;
            using var calls = new BoundedCalls(logger);
            logger.Update("seed", value => value + 1, 10);
            logger.Values.Storage.Flush();
            if (callback == "history-reader") Record(logger);
            logger.Log.Flush();
            byte[] state = ReadOwnedState(logger);
            byte[] main = ReadShared(logger.Log.MainFileName);
            byte[] extended = ReadShared(logger.Log.ExtendedFileName);
            long historyCount = logger.Log.Count;

            armed = true;
            Exception? error = calls.Capture(() =>
            {
                if (callback == "update")
                    logger.Update("seed", value => { AttemptDispose(); return value + 1; }, -1);
                else if (callback == "state-writer")
                    logger.Update("seed", value => value + 1, -1);
                else if (callback == "snapshot-converter")
                    logger.LogValues(values =>
                    {
                        Check.That(!Monitor.IsEntered(logger.Values.Storage.SynchronizationRoot),
                            "The snapshot converter must run outside the dictionary lock.");
                        AttemptDispose();
                        return values;
                    });
                else if (callback == "history-writer") logger.LogValues();
                else logger.GetLogged(From, To);
            });
            armed = false;
            Check.That(error is InvalidOperationException && error.Message.Contains("cannot re-enter", StringComparison.Ordinal),
                "Callback Dispose must reject wrapper reentry before disposed state changes or timer joins.");
            Check.Equal(historyCount, logger.Log.Count, "Rejected callback must not change history count.");
            AssertCurrent(logger, ("seed", 10));
            Check.That(state.AsSpan().SequenceEqual(ReadOwnedState(logger)) &&
                main.AsSpan().SequenceEqual(ReadShared(logger.Log.MainFileName)) &&
                extended.AsSpan().SequenceEqual(ReadShared(logger.Log.ExtendedFileName)),
                "Rejected callback must leave current-state and both history files unchanged.");
            Exception? recoveryError = calls.Capture(() =>
            {
                Check.That(logger.Update("seed", value => value + 1, -1), "Wrapper remains open after callback rejection.");
                logger.Values.Storage.Flush();
                AssertMap(Record(logger).Snapshot, ("seed", 11));
                AssertMap(logger.GetLogged(From, To).Last().Value, ("seed", 11));
            });
            Check.That(recoveryError == null, "Update, snapshot and query must remain usable after reentry rejection: " + recoveryError);
            return callback + " -> Dispose rejects within five seconds, preserves all store bytes/count/current value, then permits Update, append, Flush and query; snapshot conversion stays outside the dictionary lock.";
        });
    }

    private static void Lifecycle(CheckContext ctx, bool explicitCodecs)
    {
        string name = explicitCodecs ? "explicit-codecs" : "default-primitives";
        ctx.Run("dictionary-logger." + name + "-lifecycle", () =>
        {
            var paths = Paths(ctx, name);
            Logger Open() => explicitCodecs
                ? new Logger(paths.State, paths.History, ReadState, WriteState, ReadHistory, WriteHistory)
                : new Logger(paths.State, paths.History);
            DateTime first;
            DateTime second;
            using (var logger = Open())
            {
                int conversions = 0;
                Check.That(!logger.Update("alpha", value => { conversions++; return value + 10; }, 1), "Missing key insertion returns false.");
                Check.Equal(0, conversions, "Missing-key insertion must not invoke the existing-value converter.");
                Check.That(logger.Update("alpha", value => { conversions++; return value + 10; }, -1), "Existing key update returns true.");
                Check.Equal(1, conversions, "Existing-key converter count.");
                logger.Values.Add("beta", 2);
                Check.Equal(0L, logger.Log.Count, "Update and direct Values mutations do not record history.");
                var recorded = Record(logger);
                first = recorded.Time;
                AssertMap(recorded.Snapshot, ("alpha", 11), ("beta", 2));
                AssertMap(logger.GetLogged(first, first).Single().Value, ("alpha", 11), ("beta", 2));
                logger.Update("alpha", value => value + 1, -1);
                Check.That(logger.Values.Remove("beta"), "Remove current beta.");
                logger.Values.Add("gamma", 3);
                Check.Equal(1L, logger.Log.Count, "Later current-value changes do not add snapshots.");
                logger.Values.Storage.Flush();
            }
            using (var logger = Open())
            {
                AssertCurrent(logger, ("alpha", 12), ("gamma", 3));
                var previous = logger.GetLogged(From, To);
                Check.Equal(1, previous.Length, "First snapshot count after reopen.");
                Check.Equal(first.ToBinary(), previous[0].Key.ToBinary(), "First committed timestamp after reopen.");
                AssertMap(previous[0].Value, ("alpha", 11), ("beta", 2));
                second = Record(logger).Time;
                Check.That(second > first, "A second accepted snapshot has a strictly later timestamp.");
                var afterFirst = logger.GetLogged(first.AddTicks(1), second);
                Check.Equal(1, afterFirst.Length, "Inclusive upper and exclusive-by-tick lower query control.");
                AssertMap(afterFirst[0].Value, ("alpha", 12), ("gamma", 3));
            }
            using (var logger = Open())
            {
                AssertCurrent(logger, ("alpha", 12), ("gamma", 3));
                var rows = logger.GetLogged(first, second);
                Check.Equal(2, rows.Length, "Both snapshot endpoints survive second reopen.");
                Check.Equal(first.ToBinary(), rows[0].Key.ToBinary(), "First exact snapshot timestamp.");
                Check.Equal(second.ToBinary(), rows[1].Key.ToBinary(), "Second exact snapshot timestamp.");
                AssertMap(rows[0].Value, ("alpha", 11), ("beta", 2));
                AssertMap(rows[1].Value, ("alpha", 12), ("gamma", 3));
            }
            return name + ": Update/current state and two explicit snapshots remain distinct; exact values, clock-bounded committed UTC timestamps, inclusive queries and clean close/reopen verified for both stores.";
        });
    }

    private static void Disposal(CheckContext ctx, bool stateFailure, bool historyFailure)
    {
        string name = stateFailure && historyFailure ? "both" : stateFailure ? "state" : "history";
        ctx.Run("dictionary-logger.dispose-" + name + "-failure", () =>
        {
            var paths = Paths(ctx, "dispose-" + name);
            var stateError = new IOException("state-dispose-control");
            var historyError = new IOException("history-dispose-control");
            using var fixture = new DisposalFixture(paths, stateFailure ? stateError : null, historyFailure ? historyError : null);
            if (stateFailure && historyFailure)
            {
                AggregateException failure = Check.Throws<AggregateException>(() => fixture.Logger.Dispose(), "Both resource failures must be reported.");
                Check.Equal(2, failure.InnerExceptions.Count, "Resource failure count.");
                Check.That(ReferenceEquals(stateError, failure.InnerExceptions[0]) && ReferenceEquals(historyError, failure.InnerExceptions[1]),
                    "Disposal aggregation must retain original exception objects in Values, Log order.");
            }
            else
            {
                IOException failure = Check.Throws<IOException>(() => fixture.Logger.Dispose(), "Single resource failure must propagate.");
                Check.That(ReferenceEquals(stateFailure ? stateError : historyError, failure), "A single resource exception must be rethrown unchanged.");
            }
            fixture.AssertClosedOnce();
            fixture.Logger.Dispose();
            fixture.AssertClosedOnce();
            Check.Throws<ObjectDisposedException>(() => fixture.Logger.LogValues(), "Disposed wrapper snapshot must be rejected.");
            Check.Throws<ObjectDisposedException>(() => fixture.Logger.GetLogged(From, To), "Disposed wrapper query must be rejected.");
            Check.Throws<ObjectDisposedException>(() => fixture.Logger.Update("after", value => value + 1, 1), "Disposed wrapper Update must be rejected.");
            return "Synthetic " + name + " resource Dispose error preserves single/ordered aggregate identity; both stores are attempted and closed once, repeat Dispose is inert and wrapper operations reject disposed use.";
        });
    }

    private static (Pair[] Snapshot, DateTime Time) Record(Logger logger)
    {
        DateTime lower = DateTime.UtcNow;
        Pair[]? snapshot = logger.LogValues();
        DateTime upper = DateTime.UtcNow;
        Check.That(snapshot != null, "A timestamp-eligible snapshot must be accepted.");
        logger.Log.Flush();
        var last = logger.GetLogged(From, To).Last();
        Check.Equal(DateTimeKind.Utc, last.Key.Kind, "Snapshot timestamp kind.");
        Check.That(last.Key >= lower && last.Key <= upper, "Snapshot timestamp must lie in the independent UTC observation window.");
        return (snapshot!, last.Key);
    }

    private static void AssertTransformed(Pair[] actual)
    {
        Check.Equal(2, actual.Length, "Transformed snapshot length.");
        Check.Equal("history/z", actual[0].Key, "Converted first key/order."); Check.Equal(99, actual[0].Value, "Converted first value.");
        Check.Equal("history/a", actual[1].Key, "Converted second key/order."); Check.Equal(44, actual[1].Value, "Converted second value.");
    }

    private static void AssertCurrent(Logger logger, params (string Key, int Value)[] expected) =>
        AssertMap(logger.Values.Select(pair => (Pair)pair).ToArray(), expected);

    private static void AssertMap(Pair[] actual, params (string Key, int Value)[] expected)
    {
        Check.Equal(expected.Length, actual.Length, "Snapshot/current value count.");
        Pair[] ordered = actual.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        var wanted = expected.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        for (int i = 0; i < wanted.Length; i++)
        { Check.Equal(wanted[i].Key, ordered[i].Key, "Exact key."); Check.Equal(wanted[i].Value, ordered[i].Value, "Exact value."); }
    }

    private static byte[] WriteState(KeyValuePair<string, int> value)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        { writer.Write((byte)1); writer.Write(value.Key); writer.Write(value.Value); }
        return stream.ToArray();
    }

    private static KeyValuePair<string, int> ReadState(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        Check.Equal((byte)1, reader.ReadByte(), "State codec version.");
        var result = new KeyValuePair<string, int>(reader.ReadString(), reader.ReadInt32());
        Check.Equal(stream.Length, stream.Position, "State codec input exhaustion.");
        return result;
    }

    private static byte[] WriteHistory(Pair[] values)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        { writer.Write((byte)2); writer.Write(values.Length); foreach (Pair value in values) { writer.Write(value.Key); writer.Write(value.Value); } }
        return stream.ToArray();
    }

    private static Pair[] ReadHistory(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        Check.Equal((byte)2, reader.ReadByte(), "History codec version.");
        int count = reader.ReadInt32();
        Check.That(count >= 0 && count <= 1024, "Small history fixture item count.");
        Pair[] result = new Pair[count];
        for (int i = 0; i < count; i++) result[i] = new Pair { Key = reader.ReadString(), Value = reader.ReadInt32() };
        Check.Equal(stream.Length, stream.Position, "History codec input exhaustion.");
        return result;
    }

    private static byte[] ReadShared(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray();
    }

    private static byte[] ReadOwnedState(Logger logger)
    {
        lock (logger.Values.Storage.SynchronizationRoot)
        {
            Stream input = logger.Values.Storage.Storage.Stream;
            long position = input.Position;
            try
            {
                input.Position = 0;
                using var output = new MemoryStream();
                input.CopyTo(output);
                return output.ToArray();
            }
            finally { input.Position = position; }
        }
    }

    private static void AssertExclusive(string path) { using var ignored = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }

    private sealed record StorePaths(string Directory, string State, string History);
    private static StorePaths Paths(CheckContext ctx, string name)
    {
        string directory = Path.Combine(ctx.DataRoot, "dictionary-logger", name + "-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        return new(directory, Path.Combine(directory, "state.bin"), Path.Combine(directory, "history"));
    }

    private sealed class CloseProbe(string path, IOException? injected) : FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite)
    {
        public int FlushAttempts { get; private set; }
        public int DisposeAttempts { get; private set; }
        public IOException? Injected => injected;
        public override void Flush() { this.FlushAttempts++; base.Flush(); }
        protected override void Dispose(bool disposing)
        {
            if (disposing) this.DisposeAttempts++;
            base.Dispose(disposing);
            if (disposing && this.DisposeAttempts == 1 && injected != null) throw injected;
        }
    }

    private sealed class BoundedCalls(Logger logger) : IDisposable
    {
        private Task<Exception?>? pending;

        public Exception? Capture(Action action)
        {
            this.pending = Task.Run<Exception?>(() =>
            {
                try { action(); return null; }
                catch (Exception error) { return error; }
            });
            Check.That(this.pending.Wait(TimeSpan.FromSeconds(5)), "Wrapper callback operation exceeded five seconds (possible deadlock).");
            return this.pending.Result;
        }

        public void Dispose()
        {
            if (this.pending?.IsCompleted != false) logger.Dispose();
            else
            {
                // A regression must fail the check, not deadlock the runner in its cleanup.
                // Keep the store alive while the background operation still owns its locks.
                _ = this.pending.ContinueWith(_ => logger.Dispose(), TaskScheduler.Default);
            }
        }
    }

    private sealed class DisposalFixture : IDisposable
    {
        private static readonly PropertyInfo StateStream = typeof(DataStorage).GetProperty(nameof(DataStorage.Stream))!;
        public Logger Logger { get; }
        private readonly Stream originalState;
        private readonly FileStream originalMain;
        private readonly FileStream originalExtended;
        private readonly CloseProbe stateProbe;
        private readonly CloseProbe historyProbe;

        public DisposalFixture(StorePaths paths, IOException? stateError, IOException? historyError)
        {
            this.Logger = new Logger(paths.State, paths.History);
            this.originalState = this.Logger.Values.Storage.Storage.Stream;
            this.originalMain = (FileStream)MainField.GetValue(this.Logger.Log)!;
            this.originalExtended = (FileStream)ExtendedField.GetValue(this.Logger.Log)!;
            this.stateProbe = new CloseProbe(Path.Combine(paths.Directory, "state-close.probe"), stateError);
            this.historyProbe = new CloseProbe(Path.Combine(paths.Directory, "history-close.probe"), historyError);
            // Empty stores, before any Update/LogValues: neither component timer has been armed.
            StateStream.SetValue(this.Logger.Values.Storage.Storage, this.stateProbe);
            MainField.SetValue(this.Logger.Log, this.historyProbe);
            Check.That(ReferenceEquals(this.stateProbe, this.Logger.Values.Storage.Storage.Stream) &&
                ReferenceEquals(this.historyProbe, MainField.GetValue(this.Logger.Log)), "Both disposal probes must be installed.");
        }

        public void AssertClosedOnce()
        {
            Check.Equal(1, this.stateProbe.DisposeAttempts, "Values disposal attempt count.");
            Check.Equal(1, this.historyProbe.DisposeAttempts, "History disposal attempt count.");
            Check.That(!this.stateProbe.CanWrite && !this.historyProbe.CanWrite && !this.originalExtended.CanWrite,
                "Both tested stores must close their streams despite the injected errors.");
        }

        public void Dispose()
        {
            try
            {
                StateStream.SetValue(this.Logger.Values.Storage.Storage, this.originalState);
                MainField.SetValue(this.Logger.Log, this.originalMain);
                this.Logger.Dispose();
            }
            finally
            {
                try { this.originalState.Dispose(); }
                finally
                {
                    try { this.originalMain.Dispose(); }
                    finally
                    {
                        try { this.originalExtended.Dispose(); }
                        finally { try { CloseIfNeeded(this.stateProbe); } finally { CloseIfNeeded(this.historyProbe); } }
                    }
                }
            }
        }

        private static void CloseIfNeeded(CloseProbe probe)
        {
            if (probe.DisposeAttempts != 0) return;
            try { probe.Dispose(); }
            catch (IOException error) when (ReferenceEquals(error, probe.Injected)) { }
        }
    }
}
