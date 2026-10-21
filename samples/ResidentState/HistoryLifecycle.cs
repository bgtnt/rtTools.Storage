using System.Text;
using RtTools.Logger;

namespace ResidentState;

internal static class HistoryLifecycle
{
    public static void Run(string dataRoot)
    {
        string directory = Path.Combine(dataRoot, "history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        DateTime firstTime = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        DateTime secondTime = firstTime.AddSeconds(1);
        HistoryEntry[] expected = [new("Създаден", 1), new("Обновен", 2)];

        string timeMask = Path.Combine(directory, "timeline");
        using (var history = new TimeLog<HistoryEntry>(timeMask, reader: ReadEntry, writer: WriteEntry))
        {
            Require(history.Append(firstTime, expected[0]), "First timeline append was rejected.");
            Require(history.Append(secondTime, expected[1]), "Second timeline append was rejected.");
            history.Flush();
        }
        using (var reader = new TimeLogClient<HistoryEntry>(
            Path.ChangeExtension(timeMask, ".tlog"), Path.ChangeExtension(timeMask, ".tldata"), reader: ReadEntry))
        {
            // TimeLog includes both endpoints of its UTC time interval.
            var rows = reader.FindAll(firstTime, secondTime);
            Require(rows.Length == 2 && rows[0].Key == firstTime && rows[1].Key == secondTime &&
                rows.All(row => row.Key.Kind == DateTimeKind.Utc) &&
                rows.Select(row => row.Value).SequenceEqual(expected), "Timeline differs after clean reopen.");
        }

        string indexMask = Path.Combine(directory, "indexed");
        Log<Record<Key.KeyInt64>, Key.KeyInt64> OpenIndex() => new(indexMask,
            recordSize: 64, autoWriteMilliseconds: Timeout.Infinite);
        using (var history = OpenIndex())
        {
            // Raw Record bytes make the payload codec explicit. Append accepts references;
            // Publish makes the batch query-visible. The timer is disabled for this example.
            history.Append(new Record<Key.KeyInt64> { Key = Index(20), Data = WriteEntry(expected[1]) });
            history.Append(new Record<Key.KeyInt64> { Key = Index(10), Data = WriteEntry(expected[0]) });
            Require(history.FindAll(Index(0), Index(100)).Length == 0, "Unpublished records became visible.");
            history.Publish();
            Require(history.FindAll(Index(10), Index(20)).Length == 1, "The indexed upper endpoint must be excluded.");
            history.Flush();
        }
        using (var history = OpenIndex())
        {
            // Log uses a half-open [from,to) key interval and FindAll sorts by key.
            var rows = history.FindAll(Index(10), Index(21));
            Require(rows.Select(row => row.Key.Value).SequenceEqual(new long[] { 10, 20 }) &&
                rows.Select(row => ReadEntry(row.Data)).SequenceEqual(expected), "Indexed history differs after clean reopen.");
        }

        Console.WriteLine("History package consumer PASS: explicit codecs, TimeLog=2, Log=2, directory=" + directory);
    }

    private sealed record HistoryEntry(string Message, int Revision);

    private static Key.KeyInt64 Index(long value) => new() { Value = value };

    private static byte[] WriteEntry(HistoryEntry entry)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)1);
            writer.Write(entry.Message);
            writer.Write(entry.Revision);
        }
        return stream.ToArray();
    }

    private static HistoryEntry ReadEntry(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        if (reader.ReadByte() != 1) throw new InvalidDataException("Unsupported history payload version.");
        var entry = new HistoryEntry(reader.ReadString(), reader.ReadInt32());
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing history payload bytes.");
        return entry;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
