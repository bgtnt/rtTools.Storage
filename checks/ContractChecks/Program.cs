using ResidentState;
using RtTools;

if (args.Length != 1) throw new ArgumentException("Usage: ContractChecks <run-data root>");
string runRoot = Path.GetFullPath(args[0]);
Directory.CreateDirectory(runRoot);
string directory = Path.Combine(runRoot, "checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
DictionaryContract();
ListContract();
StorageContract();
DefaultPrimitiveSmoke();
Console.WriteLine("All contract checks PASS: " + directory);

void DictionaryContract()
{
    string file = Path.Combine(directory, "dictionary.bin");
    DateTime created = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
    StateRecord Record(int revision) => new() { Name = "Състояние", Revision = revision, CreatedUtc = created, TransientMessage = "not persisted" };
    DictionaryPersistent<string, StateRecord> Open(FileMode mode) => new(file, mode, reader: RecordCodec.Read, writer: RecordCodec.Write);
    using (var values = Open(FileMode.CreateNew))
    {
        values.Add("main", Record(1));
        values["main"] = Record(2);
        values.Add("removed", Record(3));
        Assert(values.Remove("removed") && !values.Remove("absent"), "Dictionary remove contract.");
        bool duplicateRejected = false;
        try { values.Add("main", Record(99)); } catch (ArgumentException) { duplicateRejected = true; }
        Assert(duplicateRejected && values.Count == 1, "Duplicate Add must preserve dictionary.");
        values.Storage.Flush();
    }
    using (var values = Open(FileMode.Open))
    {
        StateRecord record = values["main"];
        Assert(values.Count == 1 && record.Revision == 2 && record.Name == "Състояние", "Dictionary persisted state.");
        Assert(record.CreatedUtc.ToBinary() == created.ToBinary() && record.TransientMessage is null, "Custom codec omits transient field.");
        record.Revision = 9;
        record.TransientMessage = "live only";
        values.Storage.Flush(); // Flush never invokes the collection writer for resident property changes.
    }
    using (var values = Open(FileMode.Open))
    {
        Assert(values["main"].Revision == 2 && values["main"].TransientMessage is null, "Property mutation + Flush must not resave.");
        StateRecord record = values["main"];
        record.Revision = 10;
        values["main"] = record;
        values.Add("", Record(4));
        values.Storage.Flush();
    }
    using (var values = Open(FileMode.Open))
    {
        Assert(values.Count == 2 && values["main"].Revision == 10 && values[""].Revision == 4 && !values.ContainsKey("removed"), "Setter resaves, empty key and deletion survive reopen.");
    }
    byte[] valid = RecordCodec.Write(new("codec", Record(1)));
    AssertThrows(() => RecordCodec.Read([.. valid, (byte)0]), "Codec rejects trailing input.");
    valid[0] = 2;
    AssertThrows(() => RecordCodec.Read(valid), "Codec rejects unknown version.");
    Console.WriteLine("Dictionary/custom-codec contract PASS");
}

void ListContract()
{
    string file = Path.Combine(directory, "list.bin");
    ListPersistent<string> Open(FileMode mode) => new(file, mode, itemReader: ReadString, itemWriter: WriteString);
    string grown = new('Ж', 512);
    string[] expected = ["bravo", grown, "charlie", "delta"];
    using (var values = Open(FileMode.CreateNew))
    {
        values.Add("alpha"); values.Add("charlie"); values.Insert(1, "bravo");
        values.InsertRange(2, ["b2", "b3"]);
        values[3] = grown;
        values.RemoveAt(0);
        Assert(values.Remove("b2"), "List Remove existing value.");
        values.AddRange(["delta"]);
        Assert(values.SequenceEqual(expected), "List resident order.");
        values.Storage.Flush();
    }
    using (var values = Open(FileMode.Open)) Assert(values.SequenceEqual(expected), "List order through relocating update/insert/remove/reopen.");
    Console.WriteLine("List contract PASS");
}

void StorageContract()
{
    string file = Path.Combine(directory, "storage.bin");
    const long marker = 0x102030405060708;
    const ushort headerSize = 16;
    DataStorage Open(FileMode mode) => new(File.Open(file, mode, FileAccess.ReadWrite, FileShare.None), headerSize: headerSize,
        minDataChunk: 32, moveFactorPercent: 100, minUnusedTruncate: 256);
    byte[] a = Enumerable.Repeat((byte)1, 128).ToArray();
    byte[] middle = Enumerable.Repeat((byte)2, 1024).ToArray();
    byte[] c = Enumerable.Repeat((byte)3, 128).ToArray();
    byte[] reused = Enumerable.Repeat((byte)4, 96).ToArray();
    byte[] grown = Enumerable.Repeat((byte)5, 2048).ToArray();
    Dictionary<long, byte[]> expected;
    using (var storage = Open(FileMode.CreateNew))
    {
        storage.SetHeaderValue(DataStorage.HeaderOffset, marker);
        long first = storage.Add(a), hole = storage.Add(middle), last = storage.Add(c);
        long beforeReuse = storage.Stream.Length;
        storage.Remove(hole);
        long replacement = storage.Add(reused);
        Assert(replacement == hole && storage.Stream.Length <= beforeReuse, "Interior free region is reused.");
        first = storage.Update(first, grown);
        long tail = storage.Add(new byte[8192]);
        long withTail = storage.Stream.Length;
        storage.Remove(tail);
        Assert(storage.Stream.Length < withTail, "Unused tail beyond configured threshold is truncated.");
        Assert(storage.GetHeaderValue<long>(DataStorage.HeaderOffset) == marker, "Header survives reuse/relocation/truncate.");
        expected = new() { [first] = grown, [replacement] = reused, [last] = c };
        CompareRaw(storage, expected);
        storage.Flush();
    }
    using (var storage = Open(FileMode.Open))
    {
        Assert(storage.GetHeaderValue<long>(DataStorage.HeaderOffset) == marker, "Header survives reopen.");
        CompareRaw(storage, expected);
    }
    Console.WriteLine("DataStorage reuse/truncate/header contract PASS");
}

void DefaultPrimitiveSmoke()
{
    string dictionaryFile = Path.Combine(directory, "default-dictionary.bin");
    using (var values = new DictionaryPersistent<int, string>(dictionaryFile, FileMode.CreateNew)) { values[1] = "primitive"; values.Storage.Flush(); }
    using (var values = new DictionaryPersistent<int, string>(dictionaryFile, FileMode.Open)) Assert(values[1] == "primitive", "Default BCL dictionary serialization.");
    string listFile = Path.Combine(directory, "default-list.bin");
    using (var values = new ListPersistent<int>(listFile, FileMode.CreateNew)) { values.AddRange([2, 4]); values.Insert(1, 3); values.Storage.Flush(); }
    using (var values = new ListPersistent<int>(listFile, FileMode.Open)) Assert(values.SequenceEqual([2, 3, 4]), "Default BCL list serialization.");
    Assert(Statics.FromBytes(Statics.ToBytes(42)) is int number && number == 42, "Extracted default serializer helper.");
    Assert(Statics.TryChangeType("7", out int converted) && converted == 7, "Extracted conversion helper.");
    Console.WriteLine("Default primitive API-preservation smoke PASS");
}

static byte[] WriteString(string value)
{
    var buffer = new BufferBuilder(32); buffer.Append((byte)1); buffer.Append(value, TypeCode.Object); return buffer.ToArray();
}
static string ReadString(byte[] bytes)
{
    var buffer = new BufferBuilder(bytes);
    if (buffer.ReadByte() != 1) throw new InvalidDataException("Unknown string version.");
    string value = buffer.ReadString(TypeCode.Object) ?? throw new InvalidDataException("Null string.");
    if (buffer.Left != 0) throw new InvalidDataException("Trailing string bytes.");
    return value;
}
static void CompareRaw(DataStorage storage, Dictionary<long, byte[]> expected)
{
    var actual = storage.ReadData.ToDictionary();
    Assert(storage.Count == expected.Count && actual.Count == expected.Count, "Raw record count.");
    foreach (var entry in expected) Assert(actual.TryGetValue(entry.Key, out var bytes) && bytes.SequenceEqual(entry.Value), "Raw record payload.");
}
static void Assert(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
static void AssertThrows(Action action, string message)
{
    try { action(); } catch (InvalidDataException) { return; }
    throw new InvalidDataException(message);
}
