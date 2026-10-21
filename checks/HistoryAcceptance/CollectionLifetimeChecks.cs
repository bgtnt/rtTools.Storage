using System.Buffers.Binary;
using RtTools;

namespace HistoryAcceptance;

internal static class CollectionLifetimeChecks
{
    private enum CollectionKind { Dictionary, List }

    // Safe to run against preview.2: each failing constructor gets a unique fixture,
    // shared reads can observe its destructive behavior, and no fixture is deleted.
    public static void BaselineContracts(CheckContext ctx) => PreOpenArguments(ctx);

    public static void Contracts(CheckContext ctx)
    {
        PreOpenArguments(ctx);
        foreach (CollectionKind kind in Enum.GetValues<CollectionKind>())
        {
            string prefix = "collections.constructor." + Name(kind);
            ctx.Run(prefix + ".caller-stream-validation", () =>
            {
                byte[] sentinel = [9, 8, 7, 6, 5];
                foreach (bool readerOnly in new[] { true, false })
                {
                    using var inner = new MemoryStream(sentinel.ToArray());
                    inner.Position = 2;
                    using var stream = new ObservedStream(inner);
                    Check.Throws<ArgumentException>(() =>
                    {
                        using var ignored = Open(kind, stream, reader: readerOnly, writer: !readerOnly);
                    }, "A half codec pair must be rejected before stream ownership begins.");
                    Check.Equal(0, stream.AccessCount, "Argument rejection must not inspect, seek, read or write the caller stream.");
                    Check.Equal(0, stream.DisposeCount, "Argument rejection must not dispose the caller stream.");
                    Check.That(inner.CanRead && inner.CanWrite, "The caller stream must remain usable.");
                    Check.Equal(2L, inner.Position, "Argument rejection must preserve stream position.");
                    Same(sentinel, inner.ToArray(), "Argument rejection must preserve every caller-stream byte.");
                }
                Check.Throws<ArgumentNullException>(() =>
                {
                    using var ignored = Open(kind, (Stream)null!);
                }, "A null caller stream must be rejected explicitly.");
                return "Both half-pair combinations reject with zero stream accesses/disposals and unchanged bytes/position; null stream rejects explicitly.";
            });

            ctx.Run(prefix + ".decoder-failure-cleanup", () =>
            {
                foreach (DataStorage.OnException policy in new[] { DataStorage.OnException.Default, DataStorage.OnException.CloseAndThrow, DataStorage.OnException.Throw })
                {
                    string path = FilePath(ctx, Name(kind) + "-decoder-" + policy);
                    Seed(kind, path);
                    byte[] before = File.ReadAllBytes(path);
                    var failure = new IOException("controlled " + Name(kind) + " decoder failure " + policy);
                    Exception actual = Check.Throws<IOException>(() =>
                    {
                        using var ignored = Open(kind, path, policy: policy, decodeError: failure);
                    }, "An owned-file decoder failure must escape.");
                    Check.That(ReferenceEquals(failure, actual), "The original owned-file decoder exception must be preserved.");
                    Exclusive(path);
                    Same(before, File.ReadAllBytes(path), "Failed file decoding must not clear, recover or rewrite the fixture.");

                    using var stream = new ObservedStream(File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
                    actual = Check.Throws<IOException>(() =>
                    {
                        using var ignored = Open(kind, stream, policy: policy, decodeError: failure);
                    }, "A caller-stream decoder failure after ownership begins must escape.");
                    Check.That(ReferenceEquals(failure, actual), "The original stream decoder exception must be preserved.");
                    Check.That(!stream.CanRead, "A failed initialized wrapper must release its owned stream immediately.");
                    if (policy == DataStorage.OnException.Throw)
                        Check.Equal(1, stream.DisposeCount, "Throw-only core policy leaves exactly one cleanup to the collection constructor.");
                    else
                        Check.That(stream.DisposeCount >= 1, "Default/CloseAndThrow may already close before outer cleanup.");
                    Exclusive(path);
                    Same(before, File.ReadAllBytes(path), "Failed stream decoding must preserve exact bytes.");
                }
                return "Custom decoder failure is rethrown unchanged for filename/stream paths under Default, CloseAndThrow and Throw; bytes remain exact and exclusive reopen needs no GC; Throw-only proves one outer cleanup.";
            });

            ctx.Run(prefix + ".invalid-storage-cleanup", () =>
            {
                foreach (DataStorage.OnException policy in new[] { DataStorage.OnException.Default, DataStorage.OnException.CloseAndThrow })
                {
                    string path = FilePath(ctx, Name(kind) + "-invalid-storage-" + policy);
                    byte[] malformed = [0x41, 0x42, 0x43, 0x44, 0x45];
                    File.WriteAllBytes(path, malformed);
                    Check.Throws<DataStorage.DataSourceException>(() =>
                    {
                        using var ignored = Open(kind, path, policy: policy);
                    }, "An incomplete storage header must reject owned-file construction.");
                    Exclusive(path);
                    Same(malformed, File.ReadAllBytes(path), "Default/CloseAndThrow must preserve malformed input bytes.");
                    using var stream = new ObservedStream(File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
                    Check.Throws<DataStorage.DataSourceException>(() =>
                    {
                        using var ignored = Open(kind, stream, policy: policy);
                    }, "An incomplete storage header must reject stream construction.");
                    Check.That(!stream.CanRead, "Invalid storage initialization must release the owned stream.");
                    Exclusive(path);
                    Same(malformed, File.ReadAllBytes(path), "Malformed-stream failure must preserve every byte.");
                }
                return "Incomplete headers reject under Default and explicit CloseAndThrow through file and stream constructors, preserving all bytes and releasing handles immediately.";
            });

            ctx.Run(prefix + ".listener-failure-cleanup", () =>
            {
                string path = FilePath(ctx, Name(kind) + "-listener");
                Seed(kind, path);
                byte[] before = File.ReadAllBytes(path);
                var failure = new IOException("controlled " + Name(kind) + " opening listener failure");
                DataStorage.OpenStateDelegate listener = (_, _) => throw failure;
                Check.That(ReferenceEquals(failure, Check.Throws<IOException>(() =>
                {
                    using var ignored = Open(kind, path, listener: listener);
                }, "An opening listener failure must escape.")), "The original file listener failure must survive cleanup.");
                Exclusive(path);
                Same(before, File.ReadAllBytes(path), "Opening listener failure must not change valid fixture bytes.");
                using var stream = new ObservedStream(File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
                Check.That(ReferenceEquals(failure, Check.Throws<IOException>(() =>
                {
                    using var ignored = Open(kind, stream, listener: listener);
                }, "A stream opening listener failure must escape.")), "The original stream listener failure must survive cleanup.");
                Check.Equal(1, stream.DisposeCount, "A throwing failure notification must still result in one owned cleanup attempt.");
                Check.That(!stream.CanRead, "Listener failure must close the owned stream.");
                Exclusive(path);
                Same(before, File.ReadAllBytes(path), "Listener stream failure must preserve all persisted bytes.");
                return "A listener throwing during successful open and again during failure handling cannot bypass closing-policy cleanup; its original error and exact fixture bytes survive for both constructor paths.";
            });

            ctx.Run(prefix + ".early-length-and-cleanup-failure", () =>
            {
                foreach (bool cleanupThrows in new[] { false, true })
                {
                    byte[] bytes = [7, 6, 5, 4];
                    using var inner = new MemoryStream(bytes.ToArray());
                    var failure = new IOException("controlled Length failure");
                    var cleanupFailure = new IOException("controlled cleanup failure after closing inner stream");
                    var stream = new ObservedStream(inner) { LengthFailure = failure, DisposeFailure = cleanupThrows ? cleanupFailure : null };
                    Exception actual = Check.Throws<IOException>(() =>
                    {
                        using var ignored = Open(kind, stream);
                    }, "Length fails before record initialization.");
                    Check.That(ReferenceEquals(failure, actual), "Even a throwing cleanup must not replace the original initialization error.");
                    Check.Equal(1, stream.DisposeCount, "Early Length failure must trigger exactly one owned cleanup attempt.");
                    Check.That(!inner.CanRead, "The synthetic cleanup failure occurs after releasing the actual resource.");
                    Same(bytes, inner.ToArray(), "Early initialization failure must preserve bytes.");
                }
                return "Initial Length failure is covered; one owned disposal releases the resource, and a secondary disposal exception cannot mask the original failure.";
            });

            foreach (bool custom in new[] { true, false })
            {
                ctx.Run(prefix + ".valid-" + (custom ? "custom" : "default") + "-lifecycle", () =>
                {
                    string path = FilePath(ctx, Name(kind) + "-lifecycle-" + custom);
                    using (IDisposable values = Open(kind, path, reader: custom, writer: custom))
                    {
                        Quiet(values);
                        AddValues(values);
                        AssertValues(values, changed: false);
                        if (custom)
                        {
                            byte[] first = Storage(values).Storage.ReadData.First().Value;
                            byte[] expected = kind == CollectionKind.Dictionary ? [1, 0, 0, 0, 11, 0, 0, 0] : [0, 0, 0, 0, 11, 0, 0, 0];
                            Same(expected, first, "Custom-codec record bytes must retain the existing dictionary/list envelope.");
                        }
                    }
                    Exclusive(path);
                    using (IDisposable values = Open(kind, path, reader: custom, writer: custom))
                    {
                        Quiet(values);
                        AssertValues(values, changed: false);
                        ChangeValues(values);
                        AssertValues(values, changed: true);
                    }
                    using (IDisposable values = Open(kind, path, reader: custom, writer: custom)) AssertValues(values, changed: true);
                    Exclusive(path);
                    var inner = new MemoryStream();
                    using var stream = new ObservedStream(inner);
                    using (IDisposable values = Open(kind, stream, reader: custom, writer: custom))
                    {
                        Quiet(values);
                        AddValues(values);
                        AssertValues(values, changed: false);
                    }
                    Check.That(!inner.CanRead && stream.DisposeCount >= 1, "A successfully initialized collection must own its caller stream until disposal.");
                    return "Valid " + (custom ? "paired custom codec with independent exact record bytes" : "default serializer") + " supports add, update, remove, repeated clean file reopen and successful caller-stream ownership/disposal.";
                });
            }
        }

        ctx.Run("collections.constructor.list.post-scan-validation-cleanup", () =>
        {
            string path = FilePath(ctx, "list-nonfinite-label");
            byte[] record = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(record, BitConverter.SingleToInt32Bits(float.NaN));
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), 11);
            using (var storage = new DataStorage(File.Open(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))) storage.Add(record);
            byte[] before = File.ReadAllBytes(path);
            Check.Throws<InvalidDataException>(() =>
            {
                using var ignored = Open(CollectionKind.List, path);
            }, "A non-finite label must reject after storage scanning completes.");
            Exclusive(path);
            Same(before, File.ReadAllBytes(path), "Post-scan validation must preserve the invalid persisted label.");
            bool coreOpened = false;
            using var stream = new ObservedStream(File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            Check.Throws<InvalidDataException>(() =>
            {
                using var ignored = Open(CollectionKind.List, stream, listener: (state, _) => coreOpened |= (state & DataStorage.OpenState.Opened) != 0);
            }, "Stream post-scan validation must also release its owner.");
            Check.That(coreOpened, "The fixture must reach the successful core-open callback before collection validation fails.");
            Check.Equal(1, stream.DisposeCount, "Post-scan failure is outside core error handling and needs one outer disposal.");
            Exclusive(path);
            Same(before, File.ReadAllBytes(path), "Post-scan stream failure must preserve exact bytes.");
            return "NaN list label reaches successful DataStorage open then fails collection validation; file and stream handles release immediately and input bytes remain exact.";
        });
    }

    private static void PreOpenArguments(CheckContext ctx)
    {
        foreach (CollectionKind kind in Enum.GetValues<CollectionKind>())
        foreach (bool readerOnly in new[] { true, false })
        foreach (bool existing in new[] { true, false })
        {
            string label = Name(kind) + "." + (readerOnly ? "reader-only" : "writer-only") + "." + (existing ? "existing-create" : "missing-open-or-create");
            ctx.Run("collections.constructor.pre-open." + label, () =>
            {
                string path = FilePath(ctx, "pre-open-" + label);
                byte[] sentinel = [0x51, 0x75, 0x65, 0x75, 0x65, 0x21];
                if (existing) File.WriteAllBytes(path, sentinel);
                Check.Throws<ArgumentException>(() =>
                {
                    using var ignored = Open(kind, path, existing ? FileMode.Create : FileMode.OpenOrCreate,
                        FileShare.ReadWrite | FileShare.Delete, readerOnly, !readerOnly);
                }, "A half codec pair must reject before opening any owned file.");
                if (existing)
                {
                    byte[] actual = ReadShared(path);
                    Check.That(sentinel.SequenceEqual(actual), "Invalid codec pair must not truncate the sentinel. ActualHex=" + Convert.ToHexString(actual));
                    Exclusive(path);
                }
                else
                    Check.That(!File.Exists(path), "Invalid codec pair must not create the previously missing file. FileExists=" + File.Exists(path));
                return "Invalid " + (readerOnly ? "reader-only" : "writer-only") + " pair rejected before " + (existing ? "Create could truncate the sentinel; exact bytes and exclusive reopen verified." : "OpenOrCreate could create a file; path remains absent.");
            });
        }
    }

    private static IDisposable Open(CollectionKind kind, string path, FileMode mode = FileMode.OpenOrCreate, FileShare share = FileShare.None,
        bool reader = true, bool writer = true, DataStorage.OnException policy = DataStorage.OnException.Default,
        DataStorage.OpenStateDelegate? listener = null, Exception? decodeError = null) => kind == CollectionKind.Dictionary
        ? new DictionaryPersistent<int, int>(path, mode, FileAccess.ReadWrite, share,
            reader: reader ? bytes => decodeError == null ? ReadPair(bytes) : throw decodeError : null,
            writer: writer ? WritePair : null, onException: policy, listener: listener)
        : new ListPersistent<int>(path, mode, FileAccess.ReadWrite, share,
            itemReader: reader ? bytes => decodeError == null ? ReadInt(bytes) : throw decodeError : null,
            itemWriter: writer ? WriteInt : null, onException: policy, listener: listener);

    private static IDisposable Open(CollectionKind kind, Stream stream, bool reader = true, bool writer = true,
        DataStorage.OnException policy = DataStorage.OnException.Default, DataStorage.OpenStateDelegate? listener = null, Exception? decodeError = null) => kind == CollectionKind.Dictionary
        ? new DictionaryPersistent<int, int>(stream,
            reader: reader ? bytes => decodeError == null ? ReadPair(bytes) : throw decodeError : null,
            writer: writer ? WritePair : null, onException: policy, listener: listener)
        : new ListPersistent<int>(stream,
            itemReader: reader ? bytes => decodeError == null ? ReadInt(bytes) : throw decodeError : null,
            itemWriter: writer ? WriteInt : null, onException: policy, listener: listener);

    private static void Seed(CollectionKind kind, string path)
    {
        using IDisposable values = Open(kind, path);
        Quiet(values);
        AddValues(values);
    }

    private static SynchronizedDataStorage Storage(IDisposable values) => values switch
    {
        DictionaryPersistent<int, int> dictionary => dictionary.Storage,
        ListPersistent<int> list => list.Storage,
        _ => throw new InvalidDataException("Unexpected collection type.")
    };

    private static void Quiet(IDisposable values)
    {
        Storage(values).FlushIdleMilliseconds = -1;
        Storage(values).FlushUpdates = ushort.MaxValue;
    }

    private static void AddValues(IDisposable values)
    {
        if (values is DictionaryPersistent<int, int> dictionary) { dictionary.Add(1, 11); dictionary.Add(2, 22); }
        else if (values is ListPersistent<int> list) { list.Add(11); list.Add(22); }
    }

    private static void ChangeValues(IDisposable values)
    {
        if (values is DictionaryPersistent<int, int> dictionary) { dictionary[1] = 111; Check.That(dictionary.Remove(2), "The dictionary control must remove its second entry."); }
        else if (values is ListPersistent<int> list) { list[0] = 111; list.RemoveAt(1); }
    }

    private static void AssertValues(IDisposable values, bool changed)
    {
        if (values is DictionaryPersistent<int, int> dictionary)
        {
            Check.Equal(changed ? 1 : 2, dictionary.Count, "Dictionary lifecycle count.");
            Check.Equal(changed ? 111 : 11, dictionary[1], "Dictionary lifecycle first value.");
            if (!changed) Check.Equal(22, dictionary[2], "Dictionary lifecycle second value.");
            else Check.That(!dictionary.ContainsKey(2), "Dictionary removal must survive reopen.");
        }
        else if (values is ListPersistent<int> list)
            Same(changed ? new[] { 111 } : new[] { 11, 22 }, list.ToArray(), "List lifecycle values/order must survive reopen.");
    }

    private static byte[] WritePair(KeyValuePair<int, int> pair)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, pair.Key);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), pair.Value);
        return bytes;
    }

    private static KeyValuePair<int, int> ReadPair(byte[] bytes)
    {
        Check.Equal(8, bytes.Length, "The dictionary test codec requires an exact eight-byte payload.");
        return new(BinaryPrimitives.ReadInt32LittleEndian(bytes), BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)));
    }

    private static byte[] WriteInt(int value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static int ReadInt(byte[] bytes)
    {
        Check.Equal(4, bytes.Length, "The list test codec requires an exact four-byte payload.");
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static string Name(CollectionKind kind) => kind.ToString().ToLowerInvariant();

    private static string FilePath(CheckContext ctx, string label)
    {
        string directory = Path.Combine(ctx.DataRoot, "collection-lifetime", label + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "collection.bin");
    }

    private static void Same<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message) => Check.That(expected.SequenceEqual(actual), message);

    private static void Exclusive(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Check.That(stream.CanRead && stream.CanWrite, "An acquired owned handle must be released without GC.");
    }

    private static byte[] ReadShared(string path)
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        file.CopyTo(copy);
        return copy.ToArray();
    }

    private sealed class ObservedStream(Stream inner) : Stream
    {
        public int AccessCount { get; private set; }
        public int DisposeCount { get; private set; }
        public Exception? LengthFailure { get; init; }
        public Exception? DisposeFailure { get; init; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length { get { AccessCount++; return LengthFailure == null ? inner.Length : throw LengthFailure; } }
        public override long Position { get { AccessCount++; return inner.Position; } set { AccessCount++; inner.Position = value; } }
        public override void Flush() { AccessCount++; inner.Flush(); }
        public override int Read(byte[] buffer, int offset, int count) { AccessCount++; return inner.Read(buffer, offset, count); }
        public override int Read(Span<byte> buffer) { AccessCount++; return inner.Read(buffer); }
        public override long Seek(long offset, SeekOrigin origin) { AccessCount++; return inner.Seek(offset, origin); }
        public override void SetLength(long value) { AccessCount++; inner.SetLength(value); }
        public override void Write(byte[] buffer, int offset, int count) { AccessCount++; inner.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { AccessCount++; inner.Write(buffer); }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
                inner.Dispose();
                if (DisposeFailure != null) throw DisposeFailure;
            }
            base.Dispose(disposing);
        }
    }
}
