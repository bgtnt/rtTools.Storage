using ResidentState;
using RtTools;

if (args.Length != 1) throw new ArgumentException("Usage: ResidentState <run-data root>");
if (typeof(DictionaryPersistent<string, StateRecord>).Assembly.GetName().Name != "rtTools.Storage")
    throw new InvalidDataException("The sample must consume the rtTools.Storage package assembly.");
string runRoot = Path.GetFullPath(args[0]);
Directory.CreateDirectory(runRoot);
string directory = Path.Combine(runRoot, "resident-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
string file = Path.Combine(directory, "state.bin");
DateTime created = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

using (var state = Open(FileMode.CreateNew))
{
    state["worker-1"] = new StateRecord
    {
        Name = "Работник", Revision = 1, CreatedUtc = created, TransientMessage = "current connection"
    };
    state.Storage.Flush();
}
using (var state = Open(FileMode.Open))
{
    StateRecord record = state["worker-1"];
    if (record.Revision != 1 || record.CreatedUtc.ToBinary() != created.ToBinary() || record.TransientMessage is not null)
        throw new InvalidDataException("First reopen differs from saved state.");
    record.Revision++;
    record.TransientMessage = "another connection";
    state["worker-1"] = record; // Explicit setter serializes the current persisted fields.
    state.Storage.Flush();
}
using (var state = Open(FileMode.Open))
{
    if (state["worker-1"].Revision != 2 || state["worker-1"].TransientMessage is not null)
        throw new InvalidDataException("Second reopen differs from saved state.");
    Console.WriteLine($"Package consumer PASS: revision={state["worker-1"].Revision}, transient omitted, file={file}");
}

HistoryLifecycle.Run(runRoot);
PersistenceLifecycle.Run(runRoot);

DictionaryPersistent<string, StateRecord> Open(FileMode mode) => new(file, mode, reader: RecordCodec.Read, writer: RecordCodec.Write);
