using System;
using System.Collections.Generic;
using System.IO;
using RtTools.Logger;

namespace HistoryAcceptance;

internal static class TimeLogChecks
{
    private static readonly DateTime Epoch = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    // Expected payloads are immutable hex scalars, independent of objects passed to the writer.
    private readonly record struct ExpectedRecord(DateTime Time, string Hex);

    private static ExpectedRecord[] SeedRecords() =>
    [
        new(Epoch, ""),
        new(Epoch.AddSeconds(1), "01020304050607"),
        new(Epoch.AddSeconds(2), "1011121314151617"),
        new(Epoch.AddSeconds(3), "202122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F")
    ];

    private static ExpectedRecord[] ExchangeRecords() =>
    [
        new(Epoch.AddSeconds(4), "FF"),
        new(Epoch.AddSeconds(5), "F0F1F2F3F4F5F6"),
        new(Epoch.AddSeconds(6), "808182838485868788898A8B8C8D8E8F90")
    ];

    public static void Contracts(CheckContext ctx)
    {
        ctx.Run("timelog.empty", () =>
        {
            string mask = ContractMask(ctx, "empty");
            using (TimeLog<byte[]> log = Open(mask))
                Check.Equal(0L, log.Count, "A new TimeLog must be empty.");

            using TimeLogClient<byte[]> client = OpenClient(mask);
            int callbacks = 0;
            Check.Equal(0L, client.ForEach(Epoch, Epoch.AddDays(1), _ => callbacks++), "Empty ForEach count.");
            Check.Equal(0, callbacks, "Empty ForEach must not invoke its action.");
            Check.Equal(0, client.FindAll(Epoch, Epoch.AddDays(1)).Length, "Empty FindAll.");
            Check.Equal(0L, client.CountAll(Epoch, Epoch.AddDays(1)), "Empty CountAll.");
            Check.Equal(0L, client.CountAll(Epoch, Epoch.AddDays(1), _ => true), "Empty filtered CountAll.");
            Check.That(!client.Exists(Epoch, Epoch.AddDays(1)), "Empty Exists.");
            Check.That(!client.Exists(Epoch, Epoch.AddDays(1), _ => true), "Empty filtered Exists.");
            return "0 records after clean close; all read/count/existence APIs agree.";
        });

        ctx.Run("timelog.explicit-payload-boundaries", () =>
        {
            string mask = ContractMask(ctx, "payloads");
            string largeHex = new('A', 2048);
            ExpectedRecord[] expected =
            [
                new(Epoch, ""),
                new(Epoch.AddSeconds(1), "FF"),
                new(Epoch.AddSeconds(2), "00010203040506"),
                new(Epoch.AddSeconds(3), "1011121314151617"),
                new(Epoch.AddSeconds(4), "202122232425262728"),
                new(Epoch.AddSeconds(5), largeHex)
            ];
            WriteFresh(mask, expected);

            Check.Equal(96L, new FileInfo(Path.ChangeExtension(mask, ".tlog")).Length,
                "Six fixed time records must occupy 96 bytes.");
            Check.Equal(1053L, new FileInfo(Path.ChangeExtension(mask, ".tldata")).Length,
                "Only 8/9/1024-byte payloads should occupy extension storage, including their length prefixes.");
            using TimeLogClient<byte[]> client = OpenClient(mask);
            AssertRecords(expected, client.FindAll(Epoch, Epoch.AddSeconds(5)), "payload roundtrip");
            List<KeyValuePair<DateTime, byte[]>> visited = [];
            Check.Equal(6L, client.ForEach(Epoch, Epoch.AddSeconds(5), visited.Add), "ForEach payload count.");
            AssertRecords(expected, visited, "ForEach payload roundtrip");
            return "Exact UTC timestamps and bytes survive close/reopen for payload sizes 0,1,7,8,9,1024; fixed=96 bytes, extension=1053 bytes.";
        });

        ctx.Run("timelog.timestamp-monotonicity", () =>
        {
            string mask = ContractMask(ctx, "ordering");
            using (TimeLog<byte[]> log = Open(mask))
            {
                Check.Equal(5000L, log.TicksToExpire, "Default lateness tolerance is 0.5 ms.");
                Check.That(log.Append(Epoch, [0x10]), "First timestamp must be accepted.");
                Check.That(!log.Append(Epoch, [0xEE]), "Equal timestamp without shifting must be rejected.");
                Check.That(!log.Append(Epoch.AddTicks(-1), [0xEE]), "Older timestamp without shifting must be rejected.");
                Check.Equal(1L, log.Count, "Rejected appends must not add records.");
                Check.That(log.Append(Epoch, [0x11], shiftEquals: true), "Equal timestamp inside tolerance must shift.");
                Check.That(log.Append(Epoch.AddTicks(-10), [0x12], shiftEquals: true), "Slightly late timestamp inside tolerance must shift.");

                log.TicksToExpire = 5;
                Check.That(!log.Append(Epoch.AddTicks(-3), [0xEE], shiftEquals: true),
                    "Lateness exactly equal to tolerance must be rejected (last=Epoch+2 ticks, input=Epoch-3).");
                Check.That(log.Append(Epoch.AddTicks(-2), [0x13], shiftEquals: true),
                    "Lateness one tick below tolerance must shift to last+1 tick.");
                log.TicksToExpire = 0;
                Check.That(!log.Append(Epoch.AddTicks(3), [0xEE], shiftEquals: true), "Zero tolerance must disable shifting.");
                log.TicksToExpire = -1;
                Check.That(!log.Append(Epoch.AddTicks(3), [0xEE], shiftEquals: true), "Negative tolerance must disable shifting.");
                Check.That(log.Append(Epoch.AddSeconds(1), [0x14]), "A later timestamp must still be accepted.");
                Check.Equal(5L, log.Count, "Only the five accepted records must remain.");
            }

            ExpectedRecord[] expected =
            [
                new(Epoch, "10"),
                new(Epoch.AddTicks(1), "11"),
                new(Epoch.AddTicks(2), "12"),
                new(Epoch.AddTicks(3), "13"),
                new(Epoch.AddSeconds(1), "14")
            ];
            using TimeLogClient<byte[]> client = OpenClient(mask);
            AssertRecords(expected, client.FindAll(Epoch, Epoch.AddSeconds(1)), "timestamp ordering");
            return "5 accepted timestamps exactly match Epoch,+1,+2,+3 ticks,+1 second; equal/late rejection and strict tolerance boundary verified.";
        });

        ctx.Run("timelog.utc-query-contract", () =>
        {
            string mask = ContractMask(ctx, "utc-queries");
            ExpectedRecord[] expected = SeedRecords();
            WriteFresh(mask, expected);
            using TimeLogClient<byte[]> client = OpenClient(mask);
            DateTime first = Epoch.AddSeconds(1);
            DateTime last = Epoch.AddSeconds(3);
            ExpectedRecord[] interval = [expected[1], expected[2], expected[3]];
            AssertRecords(interval, client.FindAll(first, last), "inclusive UTC interval");
            AssertRecords([expected[2]], client.FindAll(Epoch.AddSeconds(2), Epoch.AddSeconds(2)), "UTC singleton");
            AssertRecords(interval, client.FindAll(Epoch.AddTicks(TimeSpan.TicksPerSecond / 2), last), "nonexact lower bound");
            Check.Equal(3L, client.CountAll(first, last), "Inclusive interval count.");

            Predicate<KeyValuePair<DateTime, byte[]>> extended = record => record.Value.Length >= 8;
            AssertRecords([expected[2], expected[3]], client.FindAll(first, last, extended), "payload filter");
            Check.Equal(2L, client.CountAll(first, last, extended), "Filtered interval count.");
            Check.That(client.Exists(first, last), "Nonempty interval must exist.");
            Check.That(client.Exists(first, last, record => Convert.ToHexString(record.Value) == expected[2].Hex),
                "Exact payload predicate must find the middle record.");
            Check.That(!client.Exists(first, last, record => record.Value.Length == 9), "Absent payload predicate must be false.");

            DateTime gapFrom = first.AddTicks(1);
            DateTime gapTo = Epoch.AddSeconds(2).AddTicks(-1);
            Check.Equal(0L, client.CountAll(gapFrom, gapTo), "Gap count.");
            Check.Equal(0, client.FindAll(gapFrom, gapTo).Length, "Gap records.");
            Check.That(!client.Exists(gapFrom, gapTo), "A gap must not exist.");
            Check.Equal(0L, client.CountAll(last.AddTicks(1), last.AddSeconds(1)), "After-last count.");
            Check.Equal(0L, client.CountAll(last, first), "Inverted UTC range count.");
            Check.That(!client.Exists(last, first), "Inverted UTC range must not exist.");

            List<KeyValuePair<DateTime, byte[]>> visited = [];
            Check.Equal(3L, client.ForEach(first, last, visited.Add), "ForEach inclusive interval count.");
            AssertRecords(interval, visited, "ForEach inclusive interval");
            return "Inclusive UTC exact/nonexact bounds, singleton, gap, after-last and inverted ranges; exact payload filtering, ForEach, CountAll and Exists verified.";
        });

        ctx.Run("timelog.codec-pair-validation", () =>
        {
            string mask = ContractMask(ctx, "codec-pair");
            Check.Throws<ArgumentException>(() =>
            {
                using var ignored = new TimeLog<byte[]>(mask, reader: Identity);
            }, "A reader without writer must be rejected.");
            Check.Throws<ArgumentException>(() =>
            {
                using var ignored = new TimeLog<byte[]>(mask, writer: Identity);
            }, "A writer without reader must be rejected.");
            Check.That(!File.Exists(Path.ChangeExtension(mask, ".tlog")), "Invalid codec pairs must not create the fixed file.");
            Check.That(!File.Exists(Path.ChangeExtension(mask, ".tldata")), "Invalid codec pairs must not create the payload file.");
            return "Both half-specified codec pairs throw ArgumentException before opening files.";
        });

        ctx.Run("timelog.clean-reopen", () =>
        {
            string mask = ContractMask(ctx, "reopen");
            ExpectedRecord[] initial =
            [
                new(Epoch, ""),
                new(Epoch.AddSeconds(1), "01020304050607"),
                new(Epoch.AddSeconds(2), "1011121314151617")
            ];
            WriteFresh(mask, initial);
            using (TimeLogClient<byte[]> client = OpenClient(mask))
                AssertRecords(initial, client.FindAll(Epoch, Epoch.AddDays(1)), "standalone client after close");

            ExpectedRecord[] expected =
            [
                initial[0], initial[1], initial[2],
                new(Epoch.AddSeconds(2).AddTicks(1), "AA"),
                new(Epoch.AddSeconds(3), "B0B1B2B3B4B5B6B7B8")
            ];
            using (TimeLog<byte[]> log = Open(mask))
            {
                Check.Equal(3L, log.Count, "Reopened writer count.");
                Check.That(!log.Append(initial[2].Time, [0xEE]), "Reopen must restore the last timestamp for equal rejection.");
                Check.That(!log.Append(initial[1].Time, [0xEE]), "Reopen must reject older timestamps.");
                Check.That(log.Append(initial[2].Time, [0xAA], shiftEquals: true), "Equal timestamp after reopen must shift exactly one tick.");
                Check.That(log.Append(expected[4].Time, Convert.FromHexString(expected[4].Hex)), "Later extension payload after reopen.");
                Check.Equal(5L, log.Count, "Reopened append count.");
                log.Flush();
                using TimeLogClient<byte[]> client = log.CreateClient();
                AssertRecords(expected, client.FindAll(Epoch, Epoch.AddDays(1)), "CreateClient after explicit clean flush");
            }

            using (TimeLogClient<byte[]> client = OpenClient(mask))
                AssertRecords(expected, client.FindAll(Epoch, Epoch.AddDays(1)), "standalone client after second close");
            return "3 records reopened, equal/older timestamps rejected, 2 appended; CreateClient after normal Flush and independent post-close reader agree on all 5 records.";
        });

        if (ctx.TimeLogVariant == "Original") ctx.Run("timelog.characterization.local-unspecified-end", () =>
        {
            string mask = ContractMask(ctx, "local-end");
            ExpectedRecord[] expected = [new(Epoch, "01"), new(Epoch.AddTicks(1), "02")];
            WriteFresh(mask, expected);
            using TimeLogClient<byte[]> client = OpenClient(mask);
            Check.Equal(1L, client.CountAll(Epoch, Epoch), "UTC control singleton count.");

            DateTime local = Epoch.ToLocalTime();
            TimeSpan offset = local - Epoch;
            string timezone = TimeZoneInfo.Local.Id;
            if (offset == TimeSpan.Zero)
                return $"INCONCLUSIVE for end-bound asymmetry: timezone={timezone}, offset={offset}; local and UTC wall clocks coincide at the fixture date. UTC control count=1.";

            // The lower bound is normalized to UTC, while the current end comparison uses wall-clock ticks.
            long observedCount = offset > TimeSpan.Zero ? 2L : 0L;
            DateTime unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            Check.Equal(Epoch, local.ToUniversalTime(), "Local lower bound represents the UTC control instant.");
            Check.Equal(Epoch, unspecified.ToUniversalTime(), "Unspecified lower bound uses the local timezone.");
            foreach (DateTime bound in new[] { local, unspecified })
            {
                string kind = bound.Kind.ToString();
                Check.Equal(observedCount, client.CountAll(bound, bound), $"Current {kind} end-bound count.");
                Check.Equal((int)observedCount, client.FindAll(bound, bound).Length, $"Current {kind} end-bound FindAll.");
                Check.Equal(observedCount, client.ForEach(bound, bound), $"Current {kind} end-bound ForEach.");
                Check.Equal(observedCount > 0, client.Exists(bound, bound), $"Current {kind} end-bound Exists.");
            }
            return $"CHARACTERIZED: timezone={timezone}, offset={offset}; equivalent UTC singleton count=1, Local/Unspecified end-bound count={observedCount}. This records current behavior, not a normalized-bound guarantee.";
        }, characterization: true);

        if (ctx.TimeLogVariant == "Original") ctx.Run("timelog.characterization.default-null", () =>
        {
            string mask = ContractMask(ctx, "default-null");
            using (var log = new TimeLog<string?>(mask))
            {
                Check.That(log.Append(Epoch, null), "The current default writer accepts a null payload.");
                Check.Equal(1L, log.Count, "Default null append adds one fixed record.");
            }

            using var client = new TimeLogClient<string?>(Path.ChangeExtension(mask, ".tlog"), Path.ChangeExtension(mask, ".tldata"));
            Check.Equal(1L, client.CountAll(Epoch, Epoch), "Counting without decode sees the null record.");
            Check.That(client.Exists(Epoch, Epoch), "Exists without predicate sees the null record.");
            Check.Throws<InvalidDataException>(() => client.FindAll(Epoch, Epoch), "FindAll must reject the decoded null.");
            Check.Throws<InvalidDataException>(() => client.ForEach(Epoch, Epoch, _ => { }), "ForEach with action must reject the decoded null.");
            Check.Throws<InvalidDataException>(() => client.CountAll(Epoch, Epoch, _ => true), "Filtered count must reject the decoded null.");
            Check.Throws<InvalidDataException>(() => client.Exists(Epoch, Epoch, _ => true), "Filtered Exists must reject the decoded null.");
            return "CHARACTERIZED: default null Append succeeds; undecoded count=1/Exists=true; all four decoding paths throw InvalidDataException after clean reopen.";
        }, characterization: true);
    }

    public static void Seed(CheckContext ctx)
    {
        ctx.Run("timelog.interop.seed", () =>
        {
            string mask = InteropMask(ctx);
            Check.That(!File.Exists(Path.ChangeExtension(mask, ".tlog")) && !File.Exists(Path.ChangeExtension(mask, ".tldata")),
                "Interop seed requires a fresh file pair.");
            ExpectedRecord[] expected = SeedRecords();
            WriteFresh(mask, expected);
            using TimeLogClient<byte[]> client = OpenClient(mask);
            AssertRecords(expected, client.FindAll(Epoch, Epoch.AddDays(1)), "seed records");
            return "Seeded and reopened 4 exact records (payload sizes 0,7,8,32) in timelog-interop/history.tlog + .tldata.";
        });
    }

    public static void Exchange(CheckContext ctx)
    {
        ctx.Run("timelog.interop.exchange", () =>
        {
            string mask = InteropMask(ctx);
            ExpectedRecord[] seed = SeedRecords();
            using (TimeLogClient<byte[]> client = OpenClient(mask))
                AssertRecords(seed, client.FindAll(Epoch, Epoch.AddDays(1)), "baseline seed read by closure");

            ExpectedRecord[] additions = ExchangeRecords();
            using (TimeLog<byte[]> log = Open(mask))
            {
                Check.Equal(4L, log.Count, "Seed writer reopen count.");
                foreach (ExpectedRecord record in additions)
                    Check.That(log.Append(record.Time, Convert.FromHexString(record.Hex)), "Exchange append must be accepted.");
                Check.Equal(7L, log.Count, "Exchange writer count.");
            }

            using (TimeLogClient<byte[]> client = OpenClient(mask))
                AssertRecords(AllInteropRecords(), client.FindAll(Epoch, Epoch.AddDays(1)), "exchange clean reopen");
            return "Verified all 4 seed records, appended 3 exact records (payload sizes 1,7,17), reopened and verified total=7.";
        });
    }

    public static void Verify(CheckContext ctx)
    {
        ctx.Run("timelog.interop.verify", () =>
        {
            using TimeLogClient<byte[]> client = OpenClient(InteropMask(ctx));
            ExpectedRecord[] expected = AllInteropRecords();
            AssertRecords(expected, client.FindAll(Epoch, Epoch.AddDays(1)), "closure additions read by baseline");
            Check.Equal(7L, client.CountAll(Epoch, Epoch.AddDays(1)), "Final interop record count.");
            AssertRecords([expected[6]], client.FindAll(Epoch.AddSeconds(6), Epoch.AddSeconds(6)), "exchange endpoint singleton");
            return "Baseline verified all 7 exact timestamp/payload records, including the closure-written 17-byte final payload and inclusive final endpoint.";
        });
    }

    private static byte[] Identity(byte[] data) => data;

    private static TimeLog<byte[]> Open(string mask) => new(mask, reader: Identity, writer: Identity);

    private static TimeLogClient<byte[]> OpenClient(string mask) =>
        new(Path.ChangeExtension(mask, ".tlog"), Path.ChangeExtension(mask, ".tldata"), reader: Identity);

    private static void WriteFresh(string mask, IReadOnlyList<ExpectedRecord> expected)
    {
        using TimeLog<byte[]> log = Open(mask);
        Check.Equal(0L, log.Count, "Fixture writer must start empty.");
        foreach (ExpectedRecord record in expected)
            Check.That(log.Append(record.Time, Convert.FromHexString(record.Hex)), "Fixture append must be accepted.");
        Check.Equal((long)expected.Count, log.Count, "Fixture writer count.");
    }

    private static void AssertRecords(IReadOnlyList<ExpectedRecord> expected,
        IReadOnlyList<KeyValuePair<DateTime, byte[]>> actual, string description)
    {
        Check.Equal(expected.Count, actual.Count, description + ": record count.");
        for (int index = 0; index < expected.Count; index++)
        {
            Check.Equal(expected[index].Time.Ticks, actual[index].Key.Ticks, description + $": record {index} timestamp ticks.");
            Check.Equal(DateTimeKind.Utc, actual[index].Key.Kind, description + $": record {index} UTC kind.");
            Check.Equal(expected[index].Hex, Convert.ToHexString(actual[index].Value), description + $": record {index} payload bytes.");
        }
    }

    private static ExpectedRecord[] AllInteropRecords() => [.. SeedRecords(), .. ExchangeRecords()];

    private static string ContractMask(CheckContext ctx, string name)
    {
        string directory = Path.Combine(ctx.DataRoot, "timelog-contracts", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "history");
    }

    private static string InteropMask(CheckContext ctx)
    {
        string directory = Path.Combine(ctx.DataRoot, "timelog-interop");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "history");
    }
}
