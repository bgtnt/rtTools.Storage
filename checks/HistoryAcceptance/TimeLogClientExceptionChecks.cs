using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using RtTools.Logger;

namespace HistoryAcceptance;

internal static class TimeLogClientExceptionChecks
{
    private static readonly DateTime Epoch = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
    private readonly record struct ExpectedRecord(DateTime Time, string Hex);
    private static ExpectedRecord[] Records() =>
    [
        new(Epoch, ""),
        new(Epoch.AddSeconds(1), "01020304050607"),
        new(Epoch.AddSeconds(2), "1011121314151617"),
        new(Epoch.AddSeconds(3), "202122232425262728292A2B2C2D2E2F30")
    ];

    public static void Contracts(CheckContext ctx)
    {
        BaselineContracts(ctx);
        PhysicalContracts(ctx);
    }

    // Safe to run against preview.2: all fifteen cases should FAIL its swallowed-decoder/callback-EOF behavior.
    // These cases use the same desired assertions for the old and fixed packages.
    public static void BaselineContracts(CheckContext ctx)
    {
        foreach ((string label, int target) in new[] { ("inline", 1), ("extended", 2) })
        foreach (string api in new[] { "foreach", "findall", "findall-filtered", "count-filtered", "exists-filtered" })
        {
            ctx.Run("timelog.client-eos.decoder-" + label + "-" + api, () =>
            {
                ExpectedRecord[] expected = Records();
                string mask = Fixture(ctx, "decoder-" + label + "-" + api, expected);
                var sentinel = new EndOfStreamException("application decoder sentinel " + label + "/" + api);
                bool failing = true;
                int decoderCalls = 0;
                byte[] Read(byte[] data)
                {
                    decoderCalls++;
                    if (failing && Convert.ToHexString(data) == expected[target].Hex) throw sentinel;
                    return data;
                }

                using TimeLogClient<byte[]> client = Open(mask, Read);
                Check.Equal(4L, client.CountAll(Epoch, Epoch.AddSeconds(3)), "Header-only count does not require decoding.");
                Check.That(client.Exists(Epoch, Epoch.AddSeconds(3)), "Header-only Exists does not require decoding.");
                Check.Equal(0, decoderCalls, "Unfiltered count/existence must not invoke the decoder.");
                AssertSameException(sentinel, () => Invoke(client, api), "Decoder " + label + "/" + api);
                failing = false;
                AssertValid(client, expected);
                return "Exact application decoder EndOfStreamException propagated from " + label + " payload via " + api
                    + "; header-only queries skip decoding and all subsequent exact UTC/payload queries succeed.";
            });
        }

        ctx.Run("timelog.client-eos.action-foreach", () =>
        {
            ExpectedRecord[] expected = Records();
            string mask = Fixture(ctx, "action", expected);
            var sentinel = new EndOfStreamException("application action sentinel");
            using TimeLogClient<byte[]> client = Open(mask);
            List<KeyValuePair<DateTime, byte[]>> visited = [];
            AssertSameException(sentinel, () => client.ForEach(Epoch, Epoch.AddSeconds(3), row =>
            {
                if (row.Key == expected[2].Time) throw sentinel;
                visited.Add(row);
            }), "ForEach action");
            AssertRecords([expected[0], expected[1]], visited, "Action side effects before its failure remain visible");
            AssertValid(client, expected);
            return "Exact application action EndOfStreamException propagates; two preceding callbacks remain visible (no rollback), and later complete query succeeds.";
        });

        foreach (string api in new[] { "findall-filtered", "count-filtered", "exists-filtered" })
        {
            ctx.Run("timelog.client-eos.predicate-" + api, () =>
            {
                ExpectedRecord[] expected = Records();
                string mask = Fixture(ctx, "predicate-" + api, expected);
                var sentinel = new EndOfStreamException("application predicate sentinel " + api);
                using TimeLogClient<byte[]> client = Open(mask);
                List<KeyValuePair<DateTime, byte[]>> visited = [];
                bool Predicate(KeyValuePair<DateTime, byte[]> row)
                {
                    if (row.Key == expected[2].Time) throw sentinel;
                    visited.Add(row);
                    return false; // Exists must reach the throwing record rather than return early.
                }
                AssertSameException(sentinel, () => Invoke(client, api, Predicate), "Predicate " + api);
                AssertRecords([expected[0], expected[1]], visited, "Predicates before failure");
                AssertValid(client, expected);
                return "Exact application predicate EndOfStreamException propagates from " + api + "; same reader remains usable for full exact queries.";
            });
        }

        ctx.Run("timelog.client-eos.default-decoder", () =>
        {
            ExpectedRecord[] expected = [new(Epoch, "05")];
            string mask = Fixture(ctx, "default-decoder", expected);
            byte[] mainBefore = File.ReadAllBytes(MainFile(mask));
            byte[] extendedBefore = File.ReadAllBytes(Extended(mask));
            Check.Equal(16, mainBefore.Length, "Malformed serializer data occupies one physically complete fixed record.");
            Check.Equal(0, extendedBefore.Length, "One-byte payload is inline; no extension data is required.");
            // No reader argument: 05 is a complete TimeLog payload, but is only the default
            // serializer's string-length prefix and lacks the five requested string bytes.
            using (var client = new TimeLogClient<byte[]>(MainFile(mask), Extended(mask)))
            {
                foreach (string api in new[] { "foreach", "findall", "findall-filtered", "count-filtered", "exists-filtered" })
                {
                    EndOfStreamException error = Check.Throws<EndOfStreamException>(() => Invoke(client, api),
                        "Default decoder EOF must escape " + api + " for physically complete payload 05.");
                    Check.Equal(typeof(EndOfStreamException), error.GetType(), "Default decoder propagates its exact EOF type.");
                    Check.Equal(1L, client.ForEach(Epoch, Epoch), "Header-only ForEach remains usable after default decoding fails.");
                    Check.Equal(1L, client.CountAll(Epoch, Epoch), "Header-only CountAll skips malformed serializer data.");
                    Check.That(client.Exists(Epoch, Epoch), "Header-only Exists skips malformed serializer data.");
                }
            }
            Check.Equal(Convert.ToHexString(mainBefore), Convert.ToHexString(File.ReadAllBytes(MainFile(mask))), "Default decoding failures preserve exact main-file bytes.");
            Check.Equal(Convert.ToHexString(extendedBefore), Convert.ToHexString(File.ReadAllBytes(Extended(mask))), "Default decoding failures preserve exact extension-file bytes.");
            using TimeLogClient<byte[]> raw = Open(mask);
            AssertValid(raw, expected);
            return "Default reader (no supplied codec) propagates exact EndOfStreamException type through all five decoding APIs for complete inline payload 05; header-only queries succeed and exact physical bytes/UTC/payload oracle remain unchanged.";
        });
    }

    // Unchanged physical-read contract: safe PASS controls against both preview.2 and the fixed package.
    public static void PhysicalContracts(CheckContext ctx)
    {
        ctx.Run("timelog.client-eos.physical-partial-fixed-tail", () =>
        {
            ExpectedRecord[] expected = Records();
            string mask = Fixture(ctx, "partial-fixed-tail", expected);
            using (var tail = new FileStream(MainFile(mask), FileMode.Append, FileAccess.Write, FileShare.None))
                tail.Write([0xFF, 0x00, 0x7F]);
            Check.Equal(67L, new FileInfo(MainFile(mask)).Length, "Four complete fixed records plus a three-byte physical tail.");
            using TimeLogClient<byte[]> client = Open(mask);
            AssertValid(client, expected);
            return "A static three-byte main-file tail is ignored by complete-record Length/16 snapshot; four exact records remain queryable through all APIs.";
        });

        PhysicalPayloadCase(ctx, "extension-length", 2);
        PhysicalPayloadCase(ctx, "extension-payload", sizeof(int) + 3);

        ctx.Run("timelog.client-eos.physical-main-snapshot-eof", () =>
        {
            ExpectedRecord[] expected = [new(Epoch, "01020304050607"), new(Epoch.AddSeconds(1), "1011121314151617")];
            string mask = Fixture(ctx, "main-snapshot-eof", expected);
            byte[] complete = File.ReadAllBytes(MainFile(mask));
            // Deterministic test-only I/O seam: models a two-record length snapshot followed by a
            // shortened physical main stream. No scheduling race, decoder exception or OS-failure claim.
            using var shortened = new ReportedLengthStream(complete.AsSpan(0, 19).ToArray(), complete.Length);
            using TimeLogClient<byte[]> client = Open(mask);
            FieldInfo field = typeof(TimeLogClient<byte[]>).GetField("main", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException("TimeLogClient.main");
            var original = (Stream)(field.GetValue(client) ?? throw new InvalidDataException("Missing main reader."));
            try
            {
                field.SetValue(client, shortened);
                Check.That(ReferenceEquals(shortened, field.GetValue(client)), "Physical-main seam must replace the actual reader.");
                List<KeyValuePair<DateTime, byte[]>> visited = [];
                Check.Equal(1L, client.ForEach(Epoch, expected[1].Time, visited.Add), "Sequential fixed-read EOF counts only its complete prefix.");
                AssertRecords([expected[0]], visited, "Sequential fixed EOF callback prefix");
                AssertRecords([expected[0]], client.FindAll(Epoch, expected[1].Time), "Sequential fixed EOF FindAll prefix");
                Check.Equal(1L, client.CountAll(Epoch, expected[1].Time), "Header-only sequential fixed EOF count.");
                Check.Equal(1L, client.CountAll(Epoch, expected[1].Time, _ => true), "Filtered sequential fixed EOF count.");
                Check.That(!client.Exists(Epoch, expected[1].Time, row => row.Key == expected[1].Time), "Sequential fixed EOF must not invent a second match.");

                DateTime second = expected[1].Time;
                Check.Throws<EndOfStreamException>(() => client.ForEach(second, second), "Physical EOF during initial BinarySearch remains observable in ForEach.");
                Check.Throws<EndOfStreamException>(() => client.FindAll(second, second), "Physical EOF during initial BinarySearch remains observable in FindAll.");
                Check.Throws<EndOfStreamException>(() => client.CountAll(second, second), "Physical EOF during initial BinarySearch remains observable in CountAll.");
                Check.Throws<EndOfStreamException>(() => client.Exists(second, second), "Physical EOF during initial BinarySearch remains observable in Exists.");
                AssertRecords([expected[0]], client.FindAll(Epoch, Epoch), "Valid prefix remains queryable after initial-search EOF");
            }
            finally
            {
                field.SetValue(client, original);
                Check.That(ReferenceEquals(original, field.GetValue(client)), "Original main reader must be restored before disposal.");
            }
            AssertValid(client, expected);
            return "Synthetic shortened-main seam preserves sequential-prefix stop and initial BinarySearch EOF propagation; original reader restored and full valid query succeeds. No concurrent-file or crash guarantee.";
        });
    }

    private static void PhysicalPayloadCase(CheckContext ctx, string name, int extensionLength)
    {
        ctx.Run("timelog.client-eos.physical-" + name, () =>
        {
            ExpectedRecord[] expected = [new(Epoch, "01020304050607"), new(Epoch.AddSeconds(1), "1011121314151617")];
            string mask = Fixture(ctx, name, expected);
            using (var extension = new FileStream(Extended(mask), FileMode.Open, FileAccess.Write, FileShare.None))
                extension.SetLength(extensionLength);
            using TimeLogClient<byte[]> client = Open(mask);
            List<KeyValuePair<DateTime, byte[]>> visited = [];
            Check.Equal(2L, client.ForEach(Epoch, expected[1].Time, visited.Add), "ForEach counts the second header before its physical payload EOF.");
            AssertRecords([expected[0]], visited, "Physical payload EOF callback prefix");
            AssertRecords([expected[0]], client.FindAll(Epoch, expected[1].Time), "Physical payload EOF FindAll prefix");
            AssertRecords([expected[0]], client.FindAll(Epoch, expected[1].Time, _ => true), "Physical payload EOF filtered FindAll prefix");
            Check.Equal(1L, client.CountAll(Epoch, expected[1].Time, _ => true), "Filtered CountAll sees only the decoded prefix.");
            Check.Equal(2L, client.CountAll(Epoch, expected[1].Time), "Unfiltered CountAll sees both complete headers without payload reads.");
            Check.Equal(2L, client.ForEach(Epoch, expected[1].Time), "ForEach without action reads headers only.");
            Check.That(!client.Exists(Epoch, expected[1].Time, row => row.Key == expected[1].Time), "Filtered Exists stops on physical payload EOF.");
            Check.That(client.Exists(expected[1].Time, expected[1].Time), "Unfiltered Exists sees the second fixed header even with an incomplete payload.");
            AssertRecords([expected[0]], client.FindAll(Epoch, Epoch), "Valid first record remains queryable after physical payload EOF");
            return "Physical " + name + " EOF preserved: ForEach(action)=2 headers/1 callback, decoded prefix/filtered count=1, header-only count=2/second Exists=true, second filtered Exists=false.";
        });
    }

    private static void Invoke(TimeLogClient<byte[]> client, string api, Predicate<KeyValuePair<DateTime, byte[]>>? predicate = null)
    {
        predicate ??= _ => false;
        DateTime to = Epoch.AddSeconds(3);
        switch (api)
        {
            case "foreach": client.ForEach(Epoch, to, _ => { }); break;
            case "findall": client.FindAll(Epoch, to); break;
            case "findall-filtered": client.FindAll(Epoch, to, predicate); break;
            case "count-filtered": client.CountAll(Epoch, to, predicate); break;
            case "exists-filtered": client.Exists(Epoch, to, predicate); break;
            default: throw new ArgumentException("Unknown query API.", nameof(api));
        }
    }

    private static void AssertSameException(EndOfStreamException expected, Action action, string description)
    {
        EndOfStreamException actual = Check.Throws<EndOfStreamException>(action, description + " must propagate application EOF.");
        Check.That(ReferenceEquals(expected, actual), description + ": exact application exception object must escape unchanged.");
    }

    private static void AssertValid(TimeLogClient<byte[]> client, ExpectedRecord[] expected)
    {
        DateTime from = expected[0].Time, to = expected[expected.Length - 1].Time;
        List<KeyValuePair<DateTime, byte[]>> visited = [];
        Check.Equal((long)expected.Length, client.ForEach(from, to, visited.Add), "Subsequent valid ForEach count.");
        AssertRecords(expected, visited, "Subsequent valid ForEach");
        Check.Equal((long)expected.Length, client.ForEach(from, to), "Subsequent valid header-only ForEach.");
        AssertRecords(expected, client.FindAll(from, to), "Subsequent valid FindAll");
        ExpectedRecord[] extended = expected.Where(row => row.Hex.Length >= 16).ToArray();
        AssertRecords(extended, client.FindAll(from, to, row => row.Value.Length >= 8), "Subsequent valid filtered FindAll");
        Check.Equal((long)expected.Length, client.CountAll(from, to), "Subsequent valid CountAll.");
        Check.Equal((long)extended.Length, client.CountAll(from, to, row => row.Value.Length >= 8), "Subsequent valid filtered CountAll.");
        Check.That(client.Exists(from, to), "Subsequent valid Exists.");
        Check.That(client.Exists(from, to, row => row.Key == to), "Subsequent valid filtered Exists reaches exact last timestamp.");
        Check.That(!client.Exists(from, to, _ => false), "Subsequent valid filtered Exists no match.");
    }

    private static void AssertRecords(IReadOnlyList<ExpectedRecord> expected,
        IReadOnlyList<KeyValuePair<DateTime, byte[]>> actual, string description)
    {
        Check.Equal(expected.Count, actual.Count, description + ": row count.");
        for (int index = 0; index < expected.Count; index++)
        {
            Check.Equal(expected[index].Time.Ticks, actual[index].Key.Ticks, description + ": exact UTC timestamp ticks.");
            Check.Equal(DateTimeKind.Utc, actual[index].Key.Kind, description + ": UTC kind.");
            Check.Equal(expected[index].Hex, Convert.ToHexString(actual[index].Value), description + ": independent payload bytes.");
        }
    }

    private static string Fixture(CheckContext ctx, string name, ExpectedRecord[] expected)
    {
        string directory = Path.Combine(ctx.DataRoot, "timelog-client-eos", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string mask = Path.Combine(directory, "history");
        using (var writer = new TimeLog<byte[]>(mask, reader: Identity, writer: Identity))
        {
            foreach (ExpectedRecord row in expected)
                Check.That(writer.Append(row.Time, Convert.FromHexString(row.Hex)), "Fixture timestamp must be accepted.");
            writer.Flush();
        }
        return mask;
    }

    private static byte[] Identity(byte[] bytes) => bytes;
    private static string MainFile(string mask) => Path.ChangeExtension(mask, ".tlog");
    private static string Extended(string mask) => Path.ChangeExtension(mask, ".tldata");
    private static TimeLogClient<byte[]> Open(string mask, Converter<byte[], byte[]>? reader = null) => new(MainFile(mask), Extended(mask), reader: reader ?? Identity);

    private sealed class ReportedLengthStream(byte[] readablePrefix, long reportedLength) : MemoryStream(readablePrefix, writable: false)
    {
        public override long Length => reportedLength;
    }
}
