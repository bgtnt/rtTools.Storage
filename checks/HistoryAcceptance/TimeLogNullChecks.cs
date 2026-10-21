using System;
using System.Collections.Generic;
using System.IO;
using RtTools.Logger;

namespace HistoryAcceptance;

internal static class TimeLogNullChecks
{
    private static readonly DateTime Epoch = new(2020, 1, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Future = new(2099, 1, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime From = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
    private static readonly DateTime To = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
    private readonly record struct ExpectedValue<T>(DateTime Time, T Value);
    private sealed record FileState(long Count, byte[] Main, byte[] Extended);

    public static void Contracts(CheckContext ctx)
    {
        DefaultDirect<string?>(ctx, "reference", "seed", "between", "at-rejected-time");
        DefaultDirect<int?>(ctx, "nullable", 11, 22, 33);
        DefaultConvenience<string?>(ctx, "reference", "after-null", "convenience-value");
        DefaultConvenience<int?>(ctx, "nullable", 44, 55);
        DefaultShift<string?>(ctx, "reference", "seed", "equal-shift", "after-late-null");
        DefaultShift<int?>(ctx, "nullable", 66, 77, 88);
        TimestampPrecedence<string?>(ctx, "reference", "future-seed", "after-rejections");
        TimestampPrecedence<int?>(ctx, "nullable", 101, 102);
        DefaultNonNull<string?>(ctx, "reference", "direct-value", "convenience-value");
        DefaultNonNull<int?>(ctx, "nullable", 201, 202);
        ExplicitNullMapping<string?>(ctx, "reference", "EXPLICIT-NULL-SENTINEL");
        ExplicitNullMapping<int?>(ctx, "nullable", -777);

        ctx.Run("timelog.null.explicit-empty-bytes", () =>
        {
            string mask = Mask(ctx, "explicit-empty-bytes");
            ExpectedValue<byte[]>[] expected;
            using (var log = new TimeLog<byte[]>(mask, reader: Identity, writer: Identity))
            {
                Check.That(log.Append(Epoch, []), "Explicit direct empty byte[] must remain valid.");
                expected = AppendConvenienceAndObserve(log, [], [new(Epoch, [])], []);
                Check.Equal(2L, log.Count, "Explicit empty direct/convenience count.");
                FileState state = Snapshot(log);
                Check.Equal(32, state.Main.Length, "Two empty payloads occupy two fixed time records.");
                Check.Equal(0, state.Extended.Length, "Empty payloads must not occupy extension storage.");
            }
            AssertReopened(mask, expected, Identity);
            return "Control: explicit empty byte[] accepted by both overloads; exact empty values and committed timestamps survive clean reopen; fixed=32 bytes, extension=0.";
        });
    }

    private static void DefaultDirect<T>(CheckContext ctx, string type, T first, T between, T atRejectedTime)
    {
        ctx.Run($"timelog.null.default-{type}-direct", () =>
        {
            string mask = Mask(ctx, "default-" + type + "-direct");
            DateTime rejected = Epoch.AddSeconds(2);
            using (var log = new TimeLog<T>(mask))
            {
                AssertEmpty(Snapshot(log));
                Check.That(log.Append(Epoch, first), "Initial non-null default append.");
                FileState before = Snapshot(log);
                AssertNullRejected(() => log.Append(rejected, default!), "Eligible future null with default writer.");
                AssertUnchanged(before, Snapshot(log), "Rejected eligible default null");
                Check.That(log.Append(Epoch.AddSeconds(1), between),
                    "A valid timestamp between the last accepted and rejected timestamps proves lastLogTime was not advanced.");
                Check.That(log.Append(rejected, atRejectedTime), "A valid value at the exact rejected timestamp must still be accepted.");
                Check.Equal(3L, log.Count, "Only three non-null default records must be present.");
            }
            AssertReopened(mask, new ExpectedValue<T>[]
            {
                new(Epoch, first), new(Epoch.AddSeconds(1), between), new(rejected, atRejectedTime)
            });
            return "Default null throws exact ArgumentNullException(record) before writes; Count and both files byte-identical; valid between/same timestamp appends and all three exact values/timestamps survive reopen.";
        });
    }

    private static void DefaultConvenience<T>(CheckContext ctx, string type, T afterRejection, T convenienceValue)
    {
        ctx.Run($"timelog.null.default-{type}-convenience", () =>
        {
            string mask = Mask(ctx, "default-" + type + "-convenience");
            ExpectedValue<T>[] expected;
            using (var log = new TimeLog<T>(mask))
            {
                FileState before = Snapshot(log);
                AssertEmpty(before);
                Check.That(DateTime.UtcNow > Epoch, "The deterministic recovery timestamp must precede the convenience overload's current time.");
                AssertNullRejected(() => log.Append(default!), "Eligible convenience null with default writer.");
                AssertUnchanged(before, Snapshot(log), "Rejected convenience default null");
                Check.That(log.Append(Epoch, afterRejection),
                    "Appending a valid old timestamp after null rejection proves the convenience overload did not advance lastLogTime.");
                expected = AppendConvenienceAndObserve(log, convenienceValue, [new(Epoch, afterRejection)], convenienceValue);
            }
            AssertReopened(mask, expected);
            return "Convenience default null throws exact ArgumentNullException(record) with empty Count/files unchanged; deterministic older valid append succeeds; later non-null convenience value is clock-bounded and exactly preserved across reopen.";
        });
    }

    private static void DefaultShift<T>(CheckContext ctx, string type, T first, T equalShift, T afterLateRejection)
    {
        ctx.Run($"timelog.null.default-{type}-shift", () =>
        {
            string mask = Mask(ctx, "default-" + type + "-shift");
            using (var log = new TimeLog<T>(mask))
            {
                Check.That(log.Append(Epoch, first), "Initial non-null record for shifted null checks.");
                FileState beforeEqual = Snapshot(log);
                AssertNullRejected(() => log.Append(Epoch, default!, shiftEquals: true), "Eligible equal/shiftable default null.");
                AssertUnchanged(beforeEqual, Snapshot(log), "Rejected equal/shiftable default null");
                Check.That(log.Append(Epoch, equalShift, shiftEquals: true), "Valid equal timestamp must still shift exactly once.");

                FileState beforeLate = Snapshot(log);
                AssertNullRejected(() => log.Append(Epoch.AddTicks(-1), default!, shiftEquals: true), "Eligible slightly-late/shiftable default null.");
                AssertUnchanged(beforeLate, Snapshot(log), "Rejected slightly-late/shiftable default null");
                Check.That(log.Append(Epoch.AddTicks(2), afterLateRejection),
                    "A valid timestamp at last+1 tick proves the late null did not advance the last timestamp.");
                Check.Equal(3L, log.Count, "Shift checks must leave exactly three non-null records.");
            }
            AssertReopened(mask, new ExpectedValue<T>[]
            {
                new(Epoch, first), new(Epoch.AddTicks(1), equalShift), new(Epoch.AddTicks(2), afterLateRejection)
            });
            return "Equal and slightly-late eligible nulls throw ArgumentNullException(record) without Count/file changes; subsequent exact timestamps Epoch,+1,+2 ticks prove shift candidates did not update lastLogTime.";
        });
    }

    private static void TimestampPrecedence<T>(CheckContext ctx, string type, T first, T second)
    {
        ctx.Run($"timelog.null.{type}-timestamp-precedence", () =>
        {
            string mask = Mask(ctx, type + "-precedence");
            Check.That(DateTime.UtcNow < Future.AddDays(-1), "The deterministic 2099 fixture must remain in the future for convenience precedence checks.");
            using (var log = new TimeLog<T>(mask))
            {
                Check.That(log.Append(Future, first), "Future seed record.");
                FileState before = Snapshot(log);
                Check.That(!log.Append(Future, default!), "Equal null without shifting must return false before null validation.");
                DateTime localEqual = Future.ToLocalTime();
                Check.That(!log.Append(localEqual, default!), "Equivalent Local null timestamp must normalize before equal-time rejection.");
                Check.That(!log.Append(DateTime.SpecifyKind(localEqual, DateTimeKind.Unspecified), default!),
                    "Equivalent Unspecified null timestamp must normalize before equal-time rejection.");
                Check.That(!log.Append(Future.AddSeconds(-1), default!), "Expired null without shifting must return false before null validation.");
                Check.Equal(5000L, log.TicksToExpire, "Default timestamp lateness tolerance.");
                Check.That(!log.Append(Future.AddTicks(-5000), default!, shiftEquals: true),
                    "Null at the exact lateness threshold must return false before null validation.");
                log.TicksToExpire = 0;
                Check.That(!log.Append(Future, default!, shiftEquals: true), "Zero tolerance makes equal null unshiftable and returns false.");
                log.TicksToExpire = -1;
                Check.That(!log.Append(Future, default!, shiftEquals: true), "Negative tolerance makes equal null unshiftable and returns false.");
                log.TicksToExpire = 5000;
                Check.That(!log.Append(default!), "Convenience null before future last time must return false.");
                Check.That(!log.Append(default!, shiftEquals: true), "Convenience null years outside tolerance must return false with shifting enabled.");
                AssertUnchanged(before, Snapshot(log), "All timestamp-ineligible null calls");
                Check.That(log.Append(Future.AddTicks(1), second), "A later valid record must remain appendable.");
            }
            AssertReopened(mask, new ExpectedValue<T>[] { new(Future, first), new(Future.AddTicks(1), second) });
            return "Control: equivalent UTC/Local/Unspecified equal timestamps normalize before rejection; equal/expired/tolerance-boundary/disabled-shift nulls return false without exception or Count/byte changes; both convenience forms return false against deterministic 2099 last time; exact two-value reopen verified.";
        });
    }

    private static void DefaultNonNull<T>(CheckContext ctx, string type, T directValue, T convenienceValue)
    {
        ctx.Run($"timelog.null.default-{type}-nonnull-control", () =>
        {
            string mask = Mask(ctx, "default-" + type + "-nonnull");
            ExpectedValue<T>[] expected;
            using (var log = new TimeLog<T>(mask))
            {
                Check.That(log.Append(Epoch, directValue), "Non-null direct default value.");
                expected = AppendConvenienceAndObserve(log, convenienceValue, [new(Epoch, directValue)], convenienceValue);
            }
            AssertReopened(mask, expected);
            return "Control: non-null default values work through both overloads; direct time exact, convenience time independently clock-bounded, then both exact committed values/timestamps survive clean reopen.";
        });
    }

    private static void ExplicitNullMapping<T>(CheckContext ctx, string type, T sentinel)
    {
        ctx.Run($"timelog.null.explicit-{type}-null-mapping", () =>
        {
            string mask = Mask(ctx, "explicit-" + type + "-null");
            T Read(byte[] data)
            {
                Check.Equal(0, data.Length, "Explicit null encoding must persist as an empty payload.");
                return sentinel;
            }
            byte[] Write(T record)
            {
                Check.That(record is null, "This control intentionally maps null through an explicit writer.");
                return [];
            }

            ExpectedValue<T>[] expected;
            using (var log = new TimeLog<T>(mask, reader: Read, writer: Write))
            {
                Check.That(log.Append(Epoch, default!), "Explicit direct null mapping must remain valid.");
                expected = AppendConvenienceAndObserve(log, default!, [new(Epoch, sentinel)], sentinel);
                FileState state = Snapshot(log);
                Check.Equal(2L, state.Count, "Explicit null mapping record count.");
                Check.Equal(32, state.Main.Length, "Two explicit empty null encodings occupy two fixed time records.");
                Check.Equal(0, state.Extended.Length, "Explicit empty null encodings must not occupy extension storage.");
            }
            AssertReopened(mask, expected, Read);
            return "Control: explicit writer accepts null via both overloads, encodes empty bytes, reader returns the exact non-null sentinel; fixed=32 bytes/extension=0 and exact committed timestamps survive clean reopen.";
        });
    }

    private static ExpectedValue<T>[] AppendConvenienceAndObserve<T>(TimeLog<T> log, T input,
        IReadOnlyList<ExpectedValue<T>> prefix, T decodedValue)
    {
        DateTime lower = DateTime.UtcNow;
        Check.That(prefix.Count == 0 || lower > prefix[prefix.Count - 1].Time,
            "The convenience append must be timestamp-eligible without shifting.");
        Check.That(log.Append(input), "Eligible convenience append must succeed.");
        DateTime upper = DateTime.UtcNow;
        Check.That(upper >= lower, "The UTC observation window must not run backwards.");
        log.Flush();
        using TimeLogClient<T> client = log.CreateClient();
        KeyValuePair<DateTime, T>[] actual = client.FindAll(From, To);
        Check.Equal(prefix.Count + 1, actual.Length, "Convenience append count.");
        for (int index = 0; index < prefix.Count; index++)
            AssertRecord(prefix[index], actual[index], "Convenience prefix record " + index);
        KeyValuePair<DateTime, T> appended = actual[actual.Length - 1];
        Check.Equal(DateTimeKind.Utc, appended.Key.Kind, "Convenience timestamp kind.");
        Check.That(appended.Key >= lower && appended.Key <= upper, "Convenience timestamp must fall inside the independent UTC observation window.");
        AssertValue(decodedValue, appended.Value, "Convenience decoded scalar/bytes.");

        // Values come from independent scalar/byte oracles. The convenience overload owns its clock;
        // first bound that timestamp independently, then require exact preservation at clean reopen.
        ExpectedValue<T>[] expected = new ExpectedValue<T>[prefix.Count + 1];
        for (int index = 0; index < prefix.Count; index++) expected[index] = prefix[index];
        expected[expected.Length - 1] = new(appended.Key, decodedValue);
        return expected;
    }

    private static void AssertNullRejected(Action action, string description)
    {
        ArgumentNullException error = Check.Throws<ArgumentNullException>(action, description);
        Check.Equal(typeof(ArgumentNullException), error.GetType(), description + " Exact exception type.");
        Check.Equal("record", error.ParamName, description + " Parameter name.");
    }

    private static FileState Snapshot<T>(TimeLog<T> log)
    {
        log.Flush();
        return new(log.Count, ReadShared(log.MainFileName), ReadShared(log.ExtendedFileName));
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static void AssertEmpty(FileState state)
    {
        Check.Equal(0L, state.Count, "New writer count after normal Flush.");
        Check.Equal(0, state.Main.Length, "New fixed file length after normal Flush.");
        Check.Equal(0, state.Extended.Length, "New extension file length after normal Flush.");
    }

    private static void AssertUnchanged(FileState before, FileState after, string description)
    {
        Check.Equal(before.Count, after.Count, description + ": Count.");
        Check.Equal(before.Main.Length, after.Main.Length, description + ": fixed file length.");
        Check.Equal(before.Extended.Length, after.Extended.Length, description + ": extension file length.");
        Check.That(before.Main.AsSpan().SequenceEqual(after.Main), description + ": fixed file must remain byte-identical.");
        Check.That(before.Extended.AsSpan().SequenceEqual(after.Extended), description + ": extension file must remain byte-identical.");
    }

    private static void AssertReopened<T>(string mask, IReadOnlyList<ExpectedValue<T>> expected, Converter<byte[], T>? reader = null)
    {
        using var client = new TimeLogClient<T>(Path.ChangeExtension(mask, ".tlog"), Path.ChangeExtension(mask, ".tldata"), reader: reader);
        KeyValuePair<DateTime, T>[] actual = client.FindAll(From, To);
        Check.Equal(expected.Count, actual.Length, "Clean reopen record count.");
        for (int index = 0; index < expected.Count; index++)
            AssertRecord(expected[index], actual[index], "Clean reopen record " + index);
    }

    private static void AssertRecord<T>(ExpectedValue<T> expected, KeyValuePair<DateTime, T> actual, string description)
    {
        Check.Equal(expected.Time.Ticks, actual.Key.Ticks, description + ": exact timestamp ticks.");
        Check.Equal(DateTimeKind.Utc, actual.Key.Kind, description + ": UTC kind.");
        AssertValue(expected.Value, actual.Value, description + ": decoded scalar/bytes.");
    }

    private static void AssertValue<T>(T expected, T actual, string description)
    {
        if (expected is byte[] expectedBytes && actual is byte[] actualBytes)
            Check.Equal(Convert.ToHexString(expectedBytes), Convert.ToHexString(actualBytes), description);
        else
            Check.Equal(expected, actual, description);
    }

    private static byte[] Identity(byte[] data) => data;

    private static string Mask(CheckContext ctx, string name)
    {
        string directory = Path.Combine(ctx.DataRoot, "timelog-default-null", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "history");
    }
}
