using System.Globalization;
using RtTools.Logger;
using IntKey = RtTools.Logger.Key.KeyInt64;
using ByteRecord = RtTools.Logger.Record<RtTools.Logger.Key.KeyInt64>;
using ByteLog = RtTools.Logger.Log<RtTools.Logger.Record<RtTools.Logger.Key.KeyInt64>, RtTools.Logger.Key.KeyInt64>;

namespace HistoryAcceptance;

internal static class LogChecks
{
    private const int RecordSize = 64;

    public static void Contracts(CheckContext ctx)
    {
        ctx.Run("log.publication-and-range-contract", () =>
        {
            string mask = NewMask(ctx, "publication");
            ExpectedRow[] expected = [Row(20, 1), Row(10, 2), Row(20, 3), Row(30, 4)];
            using var log = Open(mask);
            using var client = log.CreateClient();
            Append(log, expected);
            Check.Equal(0L, log.Count, "Pending appends must be excluded from physical Count.");
            Check.Equal(0L, log.CountAll(K(0), K(40)), "Pending appends must be excluded from range counts.");
            Check.Equal(0, log.FindAll(K(0), K(40)).Length, "Pending appends must be invisible to direct queries.");
            Check.Equal(0, client.FindAll(K(0), K(40)).Length, "Pending appends must be invisible to an existing client.");
            Check.That(!log.Exists(K(10), K(31)), "Pending appends must not satisfy Exists.");

            log.Publish();
            Check.Equal(4L, log.Count, "Publish must expose every accepted record.");
            AssertRows(log.FindAll(K(0), K(40)), expected, true, "Published direct query");
            AssertRows(client.FindAll(K(0), K(40)), expected, true, "Existing client after Publish");
            ExpectedRow[] middle = [expected[0], expected[1], expected[2]];
            AssertRows(log.FindAll(K(10), K(30)), middle, true, "Half-open range");
            AssertRows(client.FindAll(K(20), K(30)), [expected[0], expected[2]], true, "Duplicate key range");
            Check.Equal(3L, log.CountAll(K(10), K(30)), "Upper boundary must be excluded from CountAll.");
            Check.Equal(2L, client.CountAll(K(20), K(30)), "Client count must preserve duplicates.");
            Check.Equal(0L, log.CountAll(K(20), K(20)), "Equal range bounds must be empty.");
            Check.Equal(0, client.FindAll(K(30), K(30)).Length, "Equal range bounds must read no records.");
            Check.That(log.Exists(K(30), K(31)) && client.ExistsBackward(K(10), K(11)), "Inclusive lower bounds must be searchable.");
            Check.That(!client.Exists(K(21), K(30)) && !log.ExistsBackward(K(31), K(40)), "Absent ranges must not satisfy Exists.");
            Check.Equal(2L, log.CountAll(K(0), K(40), key => key.Value == 20), "Key predicate must preserve both matching records.");
            Check.Throws<NotSupportedException>(() => log.DeleteAll(K(0), K(40)), "Deletion must remain opt-in.");
            return "4 accepted records invisible before Publish; existing client sees all afterward; duplicates and [from,to) count/find/exists verified.";
        });

        ctx.Run("log.overlapping-runs-scan-order", () =>
        {
            string mask = NewMask(ctx, "runs");
            ExpectedRow[] first = Enumerable.Range(0, 12).Select(i => Row(i, 100 + i)).ToArray();
            ExpectedRow[] second = Enumerable.Range(6, 12).Select(i => Row(i, 200 + i)).ToArray();
            ExpectedRow[] memory = [Row(5, 305)];
            ExpectedRow[] expected = [.. first, .. second, .. memory];
            using var log = Open(mask);
            Append(log, first.Reverse());
            log.Publish();
            Check.Equal(12L * RecordSize, Length(mask, ".data"), "The first run must cross the minimum ten-record promotion threshold.");
            Append(log, second.Reverse());
            log.Publish();
            Append(log, memory);
            log.Publish();
            Check.Equal(24L * RecordSize, Length(mask, ".data"), "Both independently sorted runs must be promoted.");
            Check.Equal((long)RecordSize, Length(mask, ".buffer"), "A separate published memory run must remain.");
            AssertRows(log.FindAll(K(0), K(18)), expected, true, "Merged overlapping runs");
            using var client = log.CreateClient();
            AssertRows(client.FindAll(K(0), K(18)), expected, true, "Client merged overlapping runs");
            var visited = new List<ByteRecord>();
            Check.Equal(25L, client.ForEach(K(0), K(18), visited.Add), "Streaming scan must visit every matching record.");
            AssertRows(visited, expected, false, "ForEach membership");
            Check.Equal(12L, log.CountAll(K(6), K(12)), "Overlapping runs must retain duplicate keys across the half-open range.");
            Check.That(log.Exists(K(17), K(18)) && !client.Exists(K(18), K(19)), "Existence must span promoted and memory runs.");
            // Characterize observed callback order without treating that order as a public guarantee.
            string prefix = string.Join(",", visited.Take(14).Select(record => record.Key.Value));
            return $"25 exact rows, two overlapping promoted runs plus memory; FindAll sorted; ForEach globally sorted={IsSorted(visited)}, first keys={prefix}; equal-key arrival order unconstrained.";
        }, characterization: true);

        ctx.Run("log.payload-layout-and-clean-reopen", () =>
        {
            string mask = NewMask(ctx, "payloads");
            ExpectedRow[] expected =
            [
                new(1, [0, 1, 2, 127, 128, 254, 255]),
                new(2, Noise(300, 0x1234)),
                new(3, Enumerable.Repeat((byte)0xA5, 4096).ToArray()),
                new(4, RepeatedNoise())
            ];
            using (var log = Open(mask))
            {
                Append(log, expected);
                log.Publish();
                AssertRows(log.FindAll(K(0), K(5)), expected, true, "Exact payload round trip");
                log.Flush();
            }

            // Read the public binary format only while the writer's exclusive buffer handle is closed.
            var headers = ReadHeaders(mask);
            Check.Equal(StoreType.None, headers.Single(item => item.Key == 1).Options, "Small payload must use an uncompressed inline record.");
            Check.Equal(StoreType.Extended, headers.Single(item => item.Key == 2).Options, "Sub-512-byte overflow must use an uncompressed extended record.");
            Check.Equal(StoreType.Compressed, headers.Single(item => item.Key == 3).Options, "Compressible bytes must actually take the Deflate inline branch.");
            Check.Equal(StoreType.Compressed | StoreType.Extended, headers.Single(item => item.Key == 4).Options, "Repeated noise must actually take the Deflate extended branch.");
            Check.That(Length(mask, ".extra") > 0, "Overflow payload bytes must exist in the extension file.");
            using (var reopened = Open(mask))
            using (var client = reopened.CreateClient())
            {
                Check.Equal(4L, reopened.Count, "Clean reopen must reconstruct all buffered records.");
                AssertRows(client.FindAll(K(0), K(5)), expected, true, "Reopened exact payloads");
            }
            return "Exact inline, uncompressed overflow, Deflate-inline and Deflate-overflow payloads survive clean reopen; persisted StoreType bytes confirm all four branches.";
        });

        ctx.Run("log.tombstones-and-clean-reopen", () =>
        {
            string mask = NewMask(ctx, "deletion");
            ExpectedRow[] all = Enumerable.Range(0, 15).Select(i => new ExpectedRow(i, Noise(300, 1000 + i))).ToArray();
            ExpectedRow[] live = all.Where(row => row.Key < 4 || row.Key >= 14 || (row.Key % 2) != 0).ToArray();
            Dictionary<string, long> lengths;
            using (var log = Open(mask, deleteSupported: true))
            using (var client = log.CreateClient())
            {
                Append(log, all.Take(12));
                log.Publish();
                Append(log, all.Skip(12));
                log.Publish();
                AssertRows(client.FindAll(K(0), K(15)), all, true, "Client primed before deletion");
                lengths = FileLengths(mask);
                Check.Equal(5L, log.DeleteAll(K(4), K(14), key => key.Value % 2 == 0), "Deletion must cover matching main and memory records.");
                Check.Equal(0L, log.DeleteAll(K(4), K(14), key => key.Value % 2 == 0), "Repeated deletion must be a no-op.");
                Check.Equal(15L, log.Count, "Tombstone deletion must preserve physical Count.");
                Check.Equal(10L, client.CountAll(K(0), K(15)), "Client live count must refresh after deletion.");
                Check.Equal(15L, log.CountAll(K(0), K(15), deleted: true), "Deleted-inclusive count must retain physical records.");
                AssertRows(client.FindAll(K(0), K(15)), live, true, "Existing client refreshed after deletion");
                AssertRows(log.FindAll(K(0), K(15), deleted: true), all, true, "Deleted-inclusive payloads");
                Check.That(!client.Exists(K(4), K(5)) && log.Exists(K(4), K(5), deleted: true), "Existence must respect tombstone filtering.");
                log.Flush();
                AssertLengths(mask, lengths, "Deletion must not physically reclaim main, extension, index or buffer bytes.");
            }

            Check.Equal(5, ReadHeaders(mask).Count(item => (item.Options & StoreType.Deleted) != 0), "Five persisted record headers must contain tombstone flags.");
            using (var reopened = Open(mask, deleteSupported: true))
            {
                Check.Equal(15L, reopened.Count, "Physical count must survive reopen.");
                AssertRows(reopened.FindAll(K(0), K(15)), live, true, "Reopened tombstone filtering");
                AssertRows(reopened.FindAll(K(0), K(15), deleted: true), all, true, "Reopened deleted-inclusive records");
                Check.Equal(10L, reopened.CountAll(K(0), K(15)), "Reconstructed deletion summaries must count only live rows.");
                AssertLengths(mask, lengths, "Clean reopen must preserve physical history sizes.");
            }
            return "5 main/buffer tombstones survive restart; live=10, physical=15; exact deleted-inclusive payloads remain; all four storage lengths unchanged.";
        });
    }

    public static void Seed(CheckContext ctx)
    {
        ctx.Run("log.interop.seed", () =>
        {
            string mask = InteropMask(ctx);
            Check.That(new[] { ".data", ".extra", ".buffer", ".index" }.All(extension => !File.Exists(Path.ChangeExtension(mask, extension))), "Interop seed requires a new fixture; existing files must not be overwritten.");
            ExpectedRow[] expected = OriginalInteropRows();
            using (var log = Open(mask, deleteSupported: true))
            {
                Append(log, expected.Take(12).Reverse());
                log.Publish();
                Append(log, expected.Skip(12));
                log.Publish();
                AssertRows(log.FindAll(K(-1), K(1000)), expected, true, "Baseline seed oracle");
                Check.Equal(12L * RecordSize, Length(mask, ".data"), "Seed must have a promoted main run.");
                Check.Equal(3L * RecordSize, Length(mask, ".buffer"), "Seed must retain published buffered history.");
                log.Flush();
            }
            return "Created 15 explicit-byte fixtures with a promoted run, buffered rows, duplicate key and four payload layouts under log-interop.";
        });
    }

    public static void Exchange(CheckContext ctx)
    {
        ctx.Run("log.interop.exchange", () =>
        {
            string mask = InteropMask(ctx);
            ExpectedRow[] original = OriginalInteropRows();
            ExpectedRow[] appended = AppendedInteropRows();
            using (var log = Open(mask, deleteSupported: true))
            using (var client = log.CreateClient())
            {
                Check.Equal(15L, log.Count, "Isolated source closure must reopen the baseline physical records.");
                AssertRows(client.FindAll(K(-1), K(1000)), original, true, "Baseline-to-isolated fixture");
                Append(log, appended);
                Check.Equal(15L, client.CountAll(K(-1), K(1000)), "Isolated pending appends must remain invisible to an existing client.");
                log.Publish();
                AssertRows(client.FindAll(K(-1), K(1000)), [.. original, .. appended], true, "Isolated append publication");
                Check.Equal(4L, log.DeleteAll(K(40), K(61)), "Interop deletion must cover main records and the duplicate buffered key.");
                AssertRows(client.FindAll(K(-1), K(1000)), FinalInteropRows(), true, "Isolated changes");
                Check.Equal(17L, log.Count, "Interop append/delete must retain seventeen physical rows.");
                log.Flush();
            }
            return "Read all 15 baseline rows exactly; appended 2 explicit-byte rows, published and tombstoned 4 main/buffer rows; live=13, physical=17.";
        });
    }

    public static void Verify(CheckContext ctx)
    {
        ctx.Run("log.interop.verify", () =>
        {
            string mask = InteropMask(ctx);
            ExpectedRow[] physical = [.. OriginalInteropRows(), .. AppendedInteropRows()];
            ExpectedRow[] live = FinalInteropRows();
            using var log = Open(mask, deleteSupported: true);
            using var client = log.CreateClient();
            Check.Equal(17L, log.Count, "Baseline must reopen all isolated physical rows.");
            Check.Equal(13L, client.CountAll(K(-1), K(1000)), "Baseline must respect isolated tombstones.");
            Check.Equal(17L, log.CountAll(K(-1), K(1000), deleted: true), "Baseline deleted-inclusive count must include isolated tombstones.");
            AssertRows(client.FindAll(K(-1), K(1000)), live, true, "Isolated-to-baseline exact live rows");
            AssertRows(log.FindAll(K(-1), K(1000), deleted: true), physical, true, "Isolated-to-baseline exact physical rows");
            AssertRows(client.FindAll(K(20), K(61)), live.Where(row => row.Key >= 20 && row.Key < 61).ToArray(), true, "Interop half-open boundary");
            Check.That(!client.Exists(K(40), K(61)) && log.Exists(K(40), K(61), deleted: true), "Baseline existence must distinguish live from tombstoned history.");
            Check.Equal(12L * RecordSize, Length(mask, ".data"), "Interop deletion must not reclaim the promoted main run.");
            Check.Equal(5L * RecordSize, Length(mask, ".buffer"), "Interop buffer must retain appended and tombstoned records.");
            return "Retained full DLL reopened isolated changes: 13 exact live rows, 17 exact physical rows, duplicate/deletion/overflow/compression data and [from,to) query preserved.";
        });
    }

    private static ByteLog Open(string mask, bool deleteSupported = false) =>
        new(mask, recordSize: RecordSize, autoWriteMilliseconds: Timeout.Infinite, maxBufferSize: 10, deleteSupported: deleteSupported);

    private static IntKey K(long value) => new() { Value = value };

    private static ExpectedRow Row(long key, int marker) => new(key,
        [0x01, (byte)marker, (byte)(marker >> 8), (byte)(marker >> 16), (byte)(marker >> 24)]);

    private static void Append(ByteLog log, IEnumerable<ExpectedRow> rows) =>
        // Keep oracle bytes independent from mutable payloads retained by Append.
        log.Append(rows.Select(row => new ByteRecord { Key = K(row.Key), Data = row.Data.ToArray() }));

    private static string NewMask(CheckContext ctx, string label)
    {
        string directory = Path.Combine(ctx.DataRoot, "log-contracts", label + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "history");
    }

    private static string InteropMask(CheckContext ctx)
    {
        string directory = Path.Combine(ctx.DataRoot, "log-interop");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "history");
    }

    private static void AssertRows(IEnumerable<ByteRecord> actual, IReadOnlyList<ExpectedRow> expected, bool sorted, string label)
    {
        ByteRecord[] records = actual.ToArray();
        Check.Equal(expected.Count, records.Length, label + ": record count.");
        if (sorted) Check.That(IsSorted(records), label + ": keys must be nondecreasing; duplicate arrival order is unconstrained.");
        static string Identity(long key, byte[] data) => key.ToString(CultureInfo.InvariantCulture) + ":" + Convert.ToHexString(data);
        string[] expectedRows = expected.Select(row => Identity(row.Key, row.Data)).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        string[] actualRows = records.Select(row => Identity(row.Key.Value, row.Data)).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        Check.That(expectedRows.SequenceEqual(actualRows, StringComparer.Ordinal), label + ": exact key/payload multiset, including duplicates.");
    }

    private static bool IsSorted(IReadOnlyList<ByteRecord> rows)
    {
        for (int i = 1; i < rows.Count; i++)
            if (rows[i - 1].Key.Value > rows[i].Key.Value) return false;
        return true;
    }

    private static byte[] Noise(int length, int seed)
    {
        var result = new byte[length];
        uint state = (uint)seed;
        for (int i = 0; i < result.Length; i++)
        {
            state = unchecked(state * 1664525u + 1013904223u);
            result[i] = (byte)(state >> 24);
        }
        return result;
    }

    private static byte[] RepeatedNoise()
    {
        byte[] block = Noise(4096, 0x13579);
        var result = new byte[block.Length * 16];
        for (int offset = 0; offset < result.Length; offset += block.Length)
            Buffer.BlockCopy(block, 0, result, offset, block.Length);
        return result;
    }

    private static ExpectedRow[] OriginalInteropRows()
    {
        ExpectedRow[] promoted = Enumerable.Range(0, 12).Select(i => Row(i * 10L, 500 + i)).ToArray();
        promoted[1] = new ExpectedRow(10, Noise(300, 0x1234));
        promoted[2] = new ExpectedRow(20, Enumerable.Repeat((byte)0xA5, 4096).ToArray());
        promoted[3] = new ExpectedRow(30, RepeatedNoise());
        return [.. promoted, Row(5, 605), Row(50, 650), Row(200, 800)];
    }

    private static ExpectedRow[] AppendedInteropRows() => [Row(25, 925), new(210, RepeatedNoise())];

    private static ExpectedRow[] FinalInteropRows() =>
        OriginalInteropRows().Concat(AppendedInteropRows()).Where(row => row.Key < 40 || row.Key >= 61).ToArray();

    private static long Length(string mask, string extension) => new FileInfo(Path.ChangeExtension(mask, extension)).Length;

    private static Dictionary<string, long> FileLengths(string mask) =>
        new[] { ".data", ".extra", ".buffer", ".index" }.ToDictionary(extension => extension, extension => Length(mask, extension));

    private static void AssertLengths(string mask, IReadOnlyDictionary<string, long> expected, string message)
    {
        foreach (var item in expected) Check.Equal(item.Value, Length(mask, item.Key), message + " " + item.Key);
    }

    private static List<(long Key, StoreType Options)> ReadHeaders(string mask)
    {
        var result = new List<(long Key, StoreType Options)>();
        foreach (string extension in new[] { ".data", ".buffer" })
        {
            byte[] bytes = File.ReadAllBytes(Path.ChangeExtension(mask, extension));
            Check.Equal(0, bytes.Length % RecordSize, "Physical fixture must contain whole fixed records.");
            for (int offset = 0; offset < bytes.Length; offset += RecordSize)
                // Key<Int64> persists the native unmanaged representation; BitConverter matches it.
                result.Add((BitConverter.ToInt64(bytes, offset), (StoreType)bytes[offset + sizeof(long)]));
        }
        return result;
    }

    private sealed record ExpectedRow(long Key, byte[] Data);
}
