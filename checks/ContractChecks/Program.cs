using ResidentState;
using RtTools;

if (args.Length != 1) throw new ArgumentException("Usage: ContractChecks <run-data root>");
string runRoot = Path.GetFullPath(args[0]);
Directory.CreateDirectory(runRoot);
string directory = Path.Combine(runRoot, "checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
DictionaryContract();
ListContract();
LimitedListContract();
SortedArrayContract();
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

void LimitedListContract()
{
    var shared = new List<int> { 1 };
    var partial = new LimitedList<List<int>>(5);
    partial.Add(shared);
    partial.Add(new List<int> { 2 });
    var partialCopy = partial.Copy();
    Assert(partialCopy.Limit == 5 && partialCopy.SequenceEqual(partial), "Partial ring copy preserves limit and order.");
    partialCopy[1] = new List<int> { 3 };
    shared.Add(4);
    Assert(partial[1][0] == 2 && ReferenceEquals(partialCopy[0], shared) && partialCopy[0].SequenceEqual([1, 4]), "Ring copy owns its storage and shares element references.");
    var referencesSeen = new List<List<int>?>();
    partialCopy.ForEach(value =>
    {
        referencesSeen.Add(value);
        if (referencesSeen.Count == 1) partialCopy.Clear();
    });
    Assert(referencesSeen.Count == 2 && ReferenceEquals(referencesSeen[0], shared) && referencesSeen[1] is null && partialCopy.Count == 0 && partial.Count == 2,
        "Reentrant Clear releases reference slots, preserves captured ForEach count, and leaves the original intact.");

    var wrapped = new LimitedList<int>(4);
    for (int value = 1; value <= 7; value++) wrapped.Add(value);
    wrapped.RemoveAt(3);
    var wrappedCopy = wrapped.Copy();
    Assert(wrappedCopy.Limit == 4 && wrappedCopy.SequenceEqual([4, 5, 6]), "Wrapped partial ring copy preserves order.");
    wrappedCopy[0] = 40;
    Assert(wrapped[0] == 4, "Wrapped ring copy has independent storage.");
    var valuesSeen = new List<int>();
    wrapped.ForEach(value =>
    {
        valuesSeen.Add(value);
        if (valuesSeen.Count == 1) wrapped.Clear();
    });
    Assert(valuesSeen.SequenceEqual([4, 0, 0]) && wrapped.Count == 0 && wrapped.Limit == 4, "Reentrant Clear resets wrapped value slots without changing ForEach iteration.");
    wrapped.Add(8);
    Assert(wrapped.SequenceEqual([8]) && wrappedCopy.SequenceEqual([40, 5, 6]), "Cleared ring restarts at the beginning and leaves its copy intact.");
    wrapped.Clear();
    wrapped.Clear();

    var single = new LimitedList<int>(1);
    Assert(single.Copy().Count == 0 && single.Copy().Limit == 1, "Empty ring copy preserves the minimum limit.");
    single.Clear();
    single.Add(1);
    single.Add(2);
    var singleCopy = single.Copy();
    single.Clear();
    Assert(single.Count == 0 && single.Limit == 1 && singleCopy.SequenceEqual([2]), "Minimum-limit full ring copy survives Clear.");
    Console.WriteLine("LimitedList copy/clear contract PASS");
}

void SortedArrayContract()
{
    int comparisons = 0;
    var comparer = Comparer<int>.Create((x, y) => { comparisons++; return x.CompareTo(y); });
    void Check(SortedArrayCollection<int> values, int item, int first, int end, int boundaryBudget = 160)
    {
        comparisons = 0;
        values.BinarySearch(item);
        int binaryComparisons = comparisons;
        comparisons = 0;
        Int32Interval range = values.FindEqualIndices(item);
        Assert(range.Start == first && range.End == end && values.FindFirstEqualIndex(item) == first && values.FindLastEqualIndex(item) == (first < 0 ? first : end - 1), $"Equal boundaries for {item}.");
        Assert(comparisons <= 3 * binaryComparisons + boundaryBudget, $"Adaptive equal-boundary budget for {item}: {comparisons} comparisons.");
    }
    Check(new SortedArrayCollection<int>(16, comparer), 5, ~0, ~0);
    Check(new SortedArrayCollection<int>([4], comparer), 4, 0, 1);
    const int run = 65536;
    int[] data = [-4, .. Enumerable.Repeat(7, run), 20];
    var repeated = new SortedArrayCollection<int>(data, comparer);
    Check(repeated, 7, 1, run + 1);
    Check(repeated, -4, 0, 1);
    Check(repeated, 20, run + 1, run + 2);
    Check(repeated, -5, ~0, ~0);
    Check(repeated, 8, ~(run + 1), ~(run + 1));
    Check(repeated, 21, ~repeated.Count, ~repeated.Count);
    Check(new SortedArrayCollection<int>(Enumerable.Repeat(7, run), comparer), 7, 0, run);
    foreach (int length in new[] { 2, 3 })
    {
        int[] shortRun = Enumerable.Range(0, run).ToArray();
        Array.Fill(shortRun, run / 2, run / 2, length);
        Check(new SortedArrayCollection<int>(shortRun, comparer), run / 2, run / 2, run / 2 + length, 12);
    }
    var unique = new SortedArrayCollection<int>(Enumerable.Range(0, run), comparer);
    comparisons = 0;
    int uniqueIndex = unique.BinarySearch(run / 2);
    int uniqueBinaryComparisons = comparisons;
    comparisons = 0;
    Int32Interval uniqueRange = unique.FindEqualIndices(run / 2);
    Assert(uniqueRange.Start == uniqueIndex && uniqueRange.End == uniqueIndex + 1 && comparisons <= uniqueBinaryComparisons + 2, "Unique search keeps one neighboring comparison per boundary.");
    var descendingComparer = Comparer<int>.Create((x, y) => { comparisons++; return y.CompareTo(x); });
    var descending = new SortedArrayCollection<int>(data, descendingComparer);
    Check(descending, 7, 1, run + 1);
    Check(descending, 20, 0, 1);
    Check(descending, -4, run + 1, run + 2);
    Check(descending, 21, ~0, ~0);
    Check(descending, 8, ~1, ~1);
    Check(descending, -5, ~descending.Count, ~descending.Count);
    var overridden = new DescendingSortedArrayCollection();
    overridden.SetItems([9, 9, 7, 7, 7, 3, 3], false);
    Check(overridden, 7, 2, 5);
    var keyed = new SortedArrayCollection<(int Key, string Label)>([(0, "before"), (2, "a"), (2, "b"), (2, "c"), (3, "after")],
        Comparer<(int Key, string Label)>.Create((x, y) => x.Key.CompareTo(y.Key)));
    var expected = keyed.Where(item => item.Key == 2).ToArray();
    var copy = keyed.FindEqual((2, "query"));
    Assert(copy.SequenceEqual(expected), "Equal copy preserves distinct values and their order.");
    copy[0] = (2, "changed");
    Assert(keyed.Where(item => item.Key == 2).SequenceEqual(expected), "Equal copy does not alias collection storage.");
    Assert(repeated.FindEqual(8).Length == 0, "Missing equal copy is empty.");
    Console.WriteLine("SortedArrayCollection duplicate-boundary contract PASS");
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

sealed class DescendingSortedArrayCollection : SortedArrayCollection<int>
{
    public override int Compare(int x, int y) => y.CompareTo(x);
}
