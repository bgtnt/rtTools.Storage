using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RtTools.Logger;

namespace HistoryAcceptance;

internal static class TimeLogUtcChecks
{
    private const string FirstHex = "01020304050607";
    private const string SecondHex = "1011121314151617";
    private readonly record struct ExpectedRecord(DateTime Time, string Hex);
    private readonly record struct Interval(string Name, DateTime From, DateTime To, ExpectedRecord[] Expected);

    public static void Contracts(CheckContext ctx)
    {
        foreach ((string name, DateTime epoch) in new[]
        {
            ("january", new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc)),
            ("july", new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc))
        })
        {
            ctx.Run($"timelog.utc-normalization.{name}.utc-control", () =>
            {
                Check.That(ctx.TimeLogVariant is "Original" or "Patched", "Unknown TimeLog source variant.");
                string mask = CreateFixture(ctx, name + "-utc", epoch);
                using TimeLogClient<byte[]> client = OpenClient(mask);
                foreach (Interval interval in Intervals(epoch))
                    AssertInterval(client, interval.From, interval.To, interval.Expected, name + "/UTC->UTC/" + interval.Name);
                return Metadata(ctx, epoch) + "; UTC controls: 5 intervals x 8 API forms; exact 7/8-byte payload and timestamp oracles.";
            });

            ctx.Run($"timelog.utc-normalization.{name}.kind-matrix", () =>
            {
                string mask = CreateFixture(ctx, name + "-matrix", epoch);
                using TimeLogClient<byte[]> client = OpenClient(mask);
                DateTimeKind[] kinds = [DateTimeKind.Utc, DateTimeKind.Local, DateTimeKind.Unspecified];
                int checkedIntervals = 0;
                foreach (DateTimeKind fromKind in kinds)
                foreach (DateTimeKind toKind in kinds)
                {
                    if (fromKind == DateTimeKind.Utc && toKind == DateTimeKind.Utc)
                        continue; // The separate UTC control must remain visible when the original source fails here.

                    foreach (Interval interval in Intervals(epoch))
                    {
                        DateTime from = Represent(interval.From, fromKind);
                        DateTime to = Represent(interval.To, toKind);
                        string description = name + "/" + fromKind + "->" + toKind + "/" + interval.Name;
                        Check.Equal(interval.From.Ticks, from.ToUniversalTime().Ticks,
                            description + ": lower-bound representation must preserve the instant.");
                        Check.Equal(interval.To.Ticks, to.ToUniversalTime().Ticks,
                            description + ": upper-bound representation must preserve the instant.");

                        // These assertions intentionally do not depend on Original/Patched. Original must report
                        // an ordinary FAIL for a nonzero-offset upper bound; the local fork must turn it green.
                        AssertInterval(client, from, to, interval.Expected, description);
                        checkedIntervals++;
                    }
                }

                Check.Equal(40, checkedIntervals, "The non-UTC kind matrix must cover 8 kind pairs x 5 intervals.");
                string evidence = Metadata(ctx, epoch) + "; 8 mixed/non-UTC kind pairs x 5 intervals x 8 API forms matched exact UTC oracles.";
                return TimeZoneInfo.Local.GetUtcOffset(epoch) == TimeSpan.Zero
                    ? "INCONCLUSIVE for reproducing the original non-UTC end-bound defect: representations coincide on this UTC-offset host. " + evidence
                    : evidence;
            });
        }
    }

    private static Interval[] Intervals(DateTime epoch)
    {
        ExpectedRecord first = new(epoch, FirstHex);
        ExpectedRecord second = new(epoch.AddSeconds(1), SecondHex);
        return
        [
            new("all-inclusive", first.Time, second.Time, [first, second]),
            new("first-inclusive", first.Time, first.Time, [first]),
            new("second-inclusive", second.Time, second.Time, [second]),
            new("gap", first.Time.AddTicks(1), second.Time.AddTicks(-1), []),
            new("inverted", second.Time, first.Time, [])
        ];
    }

    private static void AssertInterval(TimeLogClient<byte[]> client, DateTime from, DateTime to,
        IReadOnlyList<ExpectedRecord> expected, string description)
    {
        Predicate<KeyValuePair<DateTime, byte[]>> secondPayload = record => Convert.ToHexString(record.Value) == SecondHex;
        ExpectedRecord[] filtered = expected.Where(record => record.Hex == SecondHex).ToArray();

        // In a positive-offset zone the original Exists without a predicate can still return true for
        // a singleton. Targeting the second record exposes the incorrectly widened upper bound.
        Check.Equal(filtered.Length != 0, client.Exists(from, to, secondPayload), description + ": Exists(second payload).");
        Check.Equal(expected.Count != 0, client.Exists(from, to), description + ": Exists without predicate.");
        Check.Equal((long)expected.Count, client.CountAll(from, to), description + ": CountAll without predicate.");
        Check.Equal((long)filtered.Length, client.CountAll(from, to, secondPayload), description + ": CountAll(second payload).");
        Check.Equal((long)expected.Count, client.ForEach(from, to), description + ": ForEach without action.");

        List<KeyValuePair<DateTime, byte[]>> visited = [];
        Check.Equal((long)expected.Count, client.ForEach(from, to, visited.Add), description + ": ForEach action count.");
        AssertRecords(expected, visited, description + ": ForEach action records");
        AssertRecords(expected, client.FindAll(from, to), description + ": FindAll without predicate");
        AssertRecords(filtered, client.FindAll(from, to, secondPayload), description + ": FindAll(second payload)");
    }

    private static void AssertRecords(IReadOnlyList<ExpectedRecord> expected,
        IReadOnlyList<KeyValuePair<DateTime, byte[]>> actual, string description)
    {
        Check.Equal(expected.Count, actual.Count, description + ": count.");
        for (int index = 0; index < expected.Count; index++)
        {
            Check.Equal(expected[index].Time.Ticks, actual[index].Key.Ticks, description + $": record {index} timestamp ticks.");
            Check.Equal(DateTimeKind.Utc, actual[index].Key.Kind, description + $": record {index} UTC kind.");
            Check.Equal(expected[index].Hex, Convert.ToHexString(actual[index].Value), description + $": record {index} payload bytes.");
        }
    }

    private static DateTime Represent(DateTime utc, DateTimeKind kind) => kind switch
    {
        DateTimeKind.Utc => utc,
        DateTimeKind.Local => utc.ToLocalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(utc.ToLocalTime(), DateTimeKind.Unspecified),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string CreateFixture(CheckContext ctx, string name, DateTime epoch)
    {
        string directory = Path.Combine(ctx.DataRoot, "timelog-utc-normalization", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string mask = Path.Combine(directory, "history");
        using (var log = new TimeLog<byte[]>(mask, reader: Identity, writer: Identity))
        {
            Check.That(log.Append(epoch, Convert.FromHexString(FirstHex)), "First UTC fixture append.");
            Check.That(log.Append(epoch.AddSeconds(1), Convert.FromHexString(SecondHex)), "Second UTC fixture append.");
            Check.Equal(2L, log.Count, "The fixture must contain exactly two records.");
        }
        return mask;
    }

    private static byte[] Identity(byte[] data) => data;

    private static TimeLogClient<byte[]> OpenClient(string mask) =>
        new(Path.ChangeExtension(mask, ".tlog"), Path.ChangeExtension(mask, ".tldata"), reader: Identity);

    private static string Metadata(CheckContext ctx, DateTime epoch) =>
        $"variant={ctx.TimeLogVariant}; timezone={TimeZoneInfo.Local.Id}; fixture={epoch:O}; offset={TimeZoneInfo.Local.GetUtcOffset(epoch)}; daylight={TimeZoneInfo.Local.IsDaylightSavingTime(epoch)}";
}
