using System.Text;
using RtTools;
using RtTools.Logger;

namespace ResidentState;

internal static class PersistenceLifecycle
{
    public static void Run(string dataRoot)
    {
        string directory = Path.Combine(dataRoot, "persistence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string entityFile = Path.Combine(directory, "entities.bin");
        using (var entities = new StreamPersistence<SavedEntity>(entityFile, SavedEntity.Read))
        {
            var entity = new SavedEntity { Name = "Запис", Revision = 1, TransientMessage = "current connection" };
            entities.Add(entity);
            entity.Revision = 2;
            entities.Update(entity); // Explicitly save selected fields; PersistId may change.
            entities.Storage.Flush();
        }
        var loaded = new List<SavedEntity>();
        using (var entities = new StreamPersistence<SavedEntity>(entityFile, SavedEntity.Read, loaded.Add))
        {
            Require(loaded.Count == 1 && loaded[0].IsPersistent && loaded[0].Name == "Запис" &&
                loaded[0].Revision == 2 && loaded[0].TransientMessage == null,
                "Entity factory/load callback differs after reopen.");
        }

        string queueFile = Path.Combine(directory, "queue.bin");
        using (var queue = new BufferedQueuePersistent<string>(queueFile, maxBuffer: 1))
        {
            queue.Enqueue("first");
            queue.Enqueue("second");
            queue.Enqueue("third");
            Require(queue.Count == 3 && queue.BufferedCount <= 1 && queue.Dequeue() == "first", "Queue head/cache differs.");
            queue.Storage.Flush();
        }
        using (var queue = new BufferedQueuePersistent<string>(queueFile, maxBuffer: 1))
            Require(queue.Count == 2 && queue.Dequeue() == "second" && queue.Dequeue() == "third" && queue.Count == 0,
                "Queue FIFO differs after reopen/refill.");

        string stateFile = Path.Combine(directory, "snapshot-state.bin");
        string historyMask = Path.Combine(directory, "snapshots");
        using (var snapshots = new DictionaryLogger<string, int>(stateFile, historyMask))
        {
            Require(!snapshots.Update("worker", value => value + 1, 1), "First dictionary update must insert.");
            Require(snapshots.Update("worker", value => value + 1, 1), "Second dictionary update must update.");
            Require(snapshots.LogValues() is { Length: 1 }, "Dictionary snapshot was rejected.");
            snapshots.Values.Storage.Flush();
            snapshots.Log.Flush(); // Explicitly publish bytes for GetLogged/independent readers.
        }
        using (var snapshots = new DictionaryLogger<string, int>(stateFile, historyMask))
        {
            var history = snapshots.GetLogged(DateTime.UnixEpoch, DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc));
            Require(snapshots.Values["worker"] == 2 && history.Length == 1 &&
                history[0].Value is [{ Key: "worker", Value: 2 }], "Dictionary state/snapshot differ after reopen.");
        }
        Console.WriteLine("Persistence package consumer PASS: entity=1, FIFO=3, dictionary snapshots=1, directory=" + directory);
    }

    private sealed class SavedEntity : IPersistentBinary
    {
        public long PersistId { get; set; } = long.MinValue;
        public bool IsPersistent => this.PersistId != long.MinValue;
        public string Name { get; set; } = "";
        public int Revision { get; set; }
        public string? TransientMessage { get; set; }

        public byte[] ToBinary()
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write((byte)1);
                writer.Write(this.Name);
                writer.Write(this.Revision);
            }
            return stream.ToArray();
        }

        public static SavedEntity Read(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (reader.ReadByte() != 1) throw new InvalidDataException("Unsupported entity payload version.");
            var entity = new SavedEntity { Name = reader.ReadString(), Revision = reader.ReadInt32() };
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing entity payload bytes.");
            return entity;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
