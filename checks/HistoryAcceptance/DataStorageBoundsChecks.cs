using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.ExceptionServices;
using RtTools;

namespace HistoryAcceptance;

internal static class DataStorageBoundsChecks
{
    private const int RecordHeaderSize = 24;
    private static readonly MethodInfo PrefixReader = typeof(DataStorage).GetMethod("ReadDataPrefix", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException("DataStorage.ReadDataPrefix");
    private static readonly string[] ScanModes = ["no-scanner", "bytes-scanner", "objects-scanner"];

    // The only subset intended for published preview.2: a 36-byte fixture declaring one
    // missing payload byte. Never invoke Contracts against preview.2 (near-int-max lengths).
    public static void BaselineContracts(CheckContext ctx)
    {
        foreach (string mode in ScanModes)
        {
            ctx.Run("data-storage.bounds.missing-payload." + mode, () =>
            {
                AssertConstructorRejects(RecordImage(1, []), mode, DataStorage.OnException.Default);
                return "A 36-byte storage+record header declaring one absent payload byte rejects with DataSourceException, no callbacks/record metadata acceptance, no writes and default stream closure.";
            });
        }
    }

    public static void Contracts(CheckContext ctx)
    {
        BaselineContracts(ctx);

        foreach (string mode in ScanModes)
        {
            ctx.Run("data-storage.bounds.malformed-scan." + mode, () =>
            {
                foreach (int length in new[] { -1, int.MinValue, int.MaxValue, int.MaxValue - RecordHeaderSize + 1, int.MaxValue - RecordHeaderSize })
                    AssertConstructorRejects(RecordImage(length, []), mode, DataStorage.OnException.Default);
                for (int available = 0; available < RecordHeaderSize; available++)
                    AssertConstructorRejects(RecordImage(0, [])[..(DataStorage.HeaderOffset + available)], mode, DataStorage.OnException.CloseAndThrow);
                AssertConstructorRejects(RecordImage(9, new byte[8]), mode, DataStorage.OnException.CloseAndThrow);
                return "Small negative/near-int-max/short-payload fixtures and every incomplete record-header width reject before payload allocation/read, callback or record metadata acceptance; Default/CloseAndThrow preserve bytes and close the stream.";
            });
        }

        foreach (string path in new[] { "get-data", "read-data", "read-objects", "read-positions", "read-prefix" })
        {
            ctx.Run("data-storage.bounds.read-path." + path, () =>
            {
                foreach (int length in new[] { -1, int.MinValue, int.MaxValue, int.MaxValue - RecordHeaderSize + 1, int.MaxValue - RecordHeaderSize, 9 })
                    AssertCorruptReadRejects(path, length, truncateTo: null);
                AssertCorruptReadRejects(path, 1, DataStorage.HeaderOffset + RecordHeaderSize);
                AssertCorruptReadRejects(path, 0, DataStorage.HeaderOffset + RecordHeaderSize - 1);
                return path + ": controlled post-open stream corruption rejects invalid/overflowing lengths, missing payload and incomplete header before payload read/deserialization/yield; bytes, runtime metadata and prefix buffer remain unchanged.";
            });
        }

        ctx.Run("data-storage.bounds.record-length-overflow", () =>
        {
            foreach (int length in new[] { int.MaxValue - RecordHeaderSize + 1, int.MaxValue })
            {
                byte[] image = RecordImage(length, []);
                byte[] before = (byte[])image.Clone();
                // The length claims enough space for the payload, isolating int RecordLength
                // overflow from EOF validation. Only the 36-byte header is actually stored.
                using var stream = new SparseProbeStream(image, (long)image.Length + length);
                DataStorage? failedStorage = null;
                Check.Throws<DataStorage.DataSourceException>(() =>
                {
                    using var storage = new DataStorage(stream, listener: (state, data) =>
                    {
                        if ((state & DataStorage.OpenState.Closed) == DataStorage.OpenState.Closed && data is DataStorage failed)
                            failedStorage = failed;
                    });
                }, "RecordLength int overflow must reject even when the reported stream length fits the payload.");
                Check.That(failedStorage != null && !stream.CanRead, "Overflow rejection closes the stream before returning.");
                Check.Equal(0, failedStorage!.Count, "Overflowing record cannot advance Count.");
                Check.Equal(long.MinValue, failedStorage.LastPosition, "Overflowing record cannot become LastPosition.");
                Check.That(!stream.ResizeAttempted && stream.MaximumReadRequest <= RecordHeaderSize,
                    "Overflow rejection must not attempt a virtual resize or payload read.");
                AssertNoWrites(stream, before);
            }
            return "Synthetic sparse-length stream isolates the int RecordLength overflow branch: no-scanner rejection precedes Count/LastPosition changes, payload reads or resizing; only 36 physical bytes and no large allocation are used.";
        });

        ctx.Run("data-storage.bounds.direct-position", () =>
        {
            byte[] image = RecordImage(8, [1, 2, 3, 4, 5, 6, 7, 8], headerSize: 3);
            using var stream = new ProbeStream(image);
            using var storage = new DataStorage(stream, headerSize: 3);
            byte[] before = stream.ToArray();
            stream.ResetActivity();
            foreach (long position in new[] { long.MinValue, -1L, 0L, storage.DataOffset - 1L, stream.Length - RecordHeaderSize + 1, stream.Length, long.MaxValue })
            {
                Check.Throws<DataStorage.DataSourceException>(() => storage.GetData(position), "GetData requires a complete header within the data region.");
                byte[] prefix = [0xCC];
                Check.Throws<DataStorage.DataSourceException>(() => ReadPrefix(storage, position, prefix), "Prefix read requires a complete header within the data region.");
                Check.Equal((byte)0xCC, prefix[0], "Invalid position cannot write the prefix buffer.");
            }
            Check.Equal(0, stream.ReadCalls, "Invalid positions must reject before stream reads/seeks.");
            AssertNoWrites(stream, before);
            return "Negative, storage/reserved-header, incomplete-tail and long.MaxValue positions reject before header I/O; existing public APIs remain unchanged.";
        });

        ctx.Run("data-storage.bounds.valid-controls", () =>
        {
            foreach (string mode in ScanModes)
            foreach (ushort reserved in new ushort[] { 0, 7 })
            foreach (byte[] payload in new byte[][] { [], [1, 2, 3, 4, 5, 6, 7, 8] })
            {
                byte[] image = RecordImage(payload.Length, payload, reserved);
                using var stream = new ProbeStream(image) { MaximumChunk = 1 };
                var serializer = new ProbeSerializer();
                int callbacks = 0;
                using var storage = Open(stream, mode, reserved, serializer, () => callbacks++);
                long position = DataStorage.HeaderOffset + reserved;
                Check.Equal(1, storage.Count, "Valid record count, including a zero-length record ending exactly at EOF.");
                Check.Equal(position, storage.FirstPosition, "First position.");
                Check.Equal(position, storage.LastPosition, "Last position.");
                Check.Equal(mode == "no-scanner" ? 0 : 1, callbacks, "Valid scan callback count.");
                Check.That(payload.AsSpan().SequenceEqual(storage.GetData(position)), "Valid GetData bytes.");
                Check.Equal(position, storage.ReadPositions.Single(), "Valid ReadPositions.");
                var row = storage.ReadData.Single();
                Check.Equal(position, row.Key, "Valid ReadData position.");
                Check.That(payload.AsSpan().SequenceEqual(row.Value), "Valid ReadData bytes.");
                object? decoded = storage.ReadObjects.Single().Value;
                Check.Equal(payload.Length == 0 ? null : Convert.ToHexString(payload), decoded as string, "Valid ReadObjects payload/null convention.");
                byte[] prefix = new byte[payload.Length];
                Check.Equal(long.MinValue, ReadPrefix(storage, position, prefix), "Valid prefix next-record sentinel.");
                Check.That(payload.AsSpan().SequenceEqual(prefix), "Exact valid prefix bytes.");
                Check.Throws<InvalidDataException>(() => ReadPrefix(storage, position, new byte[payload.Length + 1]), "A complete record can still be shorter than the requested prefix.");
                AssertNoWrites(stream, image);
            }
            return "Byte/object/no-scanner controls, zero-length EOF records, reserved header regions, exact payload/prefix bytes and one-byte partial reads all remain valid without rewriting files.";
        });

        ctx.Run("data-storage.bounds.empty-reserved-header", () =>
        {
            foreach (ushort reserved in new ushort[] { 0, 9 })
            {
                byte[] image = new byte[DataStorage.HeaderOffset + reserved];
                BinaryPrimitives.WriteInt32LittleEndian(image, reserved);
                BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(4), long.MinValue);
                image.AsSpan(DataStorage.HeaderOffset).Fill(0x5A);
                using var stream = new ProbeStream(image);
                using var storage = new DataStorage(stream, headerSize: reserved);
                Check.Equal(0, storage.Count, "Valid empty store count.");
                Check.Equal(long.MinValue, storage.FirstPosition, "Empty first position sentinel.");
                Check.Equal(long.MinValue, storage.LastPosition, "Empty last position sentinel.");
                Check.Equal(0, storage.ReadPositions.Count(), "Empty positions.");
                Check.Equal(0, storage.ReadData.Count(), "Empty payloads.");
                AssertNoWrites(stream, image);
            }
            return "A complete empty storage header, with or without reserved bytes, opens unchanged; no record header is required for the empty sentinel.";
        });

        ctx.Run("data-storage.bounds.recovery-policy-boundary", () =>
        {
            AssertConstructorRejects(RecordImage(1, []), "no-scanner", DataStorage.OnException.RecoverOrThrow);
            byte[] image = RecordImage(1, []);
            using var stream = new ProbeStream(image);
            using (var storage = new DataStorage(stream, onException: DataStorage.OnException.Clear))
            {
                Check.Equal(0, storage.Count, "An explicitly selected Clear policy opens an empty store.");
                Check.Equal(long.MinValue, storage.FirstPosition, "Explicitly cleared first position.");
                Check.That(stream.WriteCalls > 0 || stream.SetLengthCalls > 0, "Clear remains an explicitly destructive policy.");
                Check.That(!image.AsSpan().SequenceEqual(stream.ToArray()), "Clear intentionally changes the malformed fixture.");
            }
            return "RecoverOrThrow rejects the unrecoverable missing-payload fixture without writes; explicit Clear intentionally changes it. This does not promise general recovery completeness or non-destructive recovery policies.";
        });
    }

    private static void AssertConstructorRejects(byte[] image, string mode, DataStorage.OnException policy)
    {
        byte[] before = (byte[])image.Clone();
        using var stream = new ProbeStream(image);
        var serializer = new ProbeSerializer();
        int callbacks = 0;
        DataStorage? failedStorage = null;
        Check.Throws<DataStorage.DataSourceException>(() =>
        {
            using var storage = Open(stream, mode, 0, serializer, () => callbacks++, policy,
                (state, data) =>
                {
                    if ((state & DataStorage.OpenState.Closed) == DataStorage.OpenState.Closed && data is DataStorage failed)
                        failedStorage = failed;
                });
        }, "Malformed record must fail with a controlled storage exception.");
        Check.Equal(0, callbacks, "Invalid record cannot reach a scan callback.");
        Check.Equal(0, serializer.DeserializeCalls, "Invalid record cannot reach object deserialization.");
        Check.That(!stream.CanRead, "Default/CloseAndThrow must close the supplied stream on failure.");
        Check.That(failedStorage != null, "Closed listener must expose the failed store for metadata assertions.");
        Check.Equal(0, failedStorage!.Count, "Invalid first record cannot advance Count.");
        Check.Equal(long.MinValue, failedStorage.LastPosition, "Invalid first record cannot become LastPosition.");
        Check.That(stream.MaximumReadRequest <= RecordHeaderSize, "No payload-sized read can precede bounds validation.");
        AssertNoWrites(stream, before);
    }

    private static void AssertCorruptReadRejects(string path, int dataLength, int? truncateTo)
    {
        byte[] image = RecordImage(8, [1, 2, 3, 4, 5, 6, 7, 8]);
        using var stream = new ProbeStream(image);
        var serializer = new ProbeSerializer();
        using var storage = new DataStorage(stream, serializer: serializer);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(DataStorage.HeaderOffset + 20), dataLength);
        if (truncateTo != null) stream.SetLength(truncateTo.Value);
        byte[] before = stream.ToArray();
        stream.ResetActivity();
        byte[] prefix = [0xCC];
        Check.Throws<DataStorage.DataSourceException>(() =>
        {
            switch (path)
            {
                case "get-data": storage.GetData(DataStorage.HeaderOffset); break;
                case "read-data": storage.ReadData.ToArray(); break;
                case "read-objects": storage.ReadObjects.ToArray(); break;
                case "read-positions": storage.ReadPositions.ToArray(); break;
                case "read-prefix": ReadPrefix(storage, DataStorage.HeaderOffset, prefix); break;
                default: throw new InvalidOperationException("Unknown read-path fixture.");
            }
        }, "Corrupted record bounds must reject before consuming its payload.");
        Check.Equal((byte)0xCC, prefix[0], "Rejected read must not overwrite prefix.");
        Check.Equal(0, serializer.DeserializeCalls, "Rejected read must not invoke deserialization.");
        Check.Equal(1, storage.Count, "A failed read cannot alter existing runtime Count.");
        Check.Equal((long)DataStorage.HeaderOffset, storage.LastPosition, "A failed read cannot alter LastPosition.");
        Check.That(stream.MaximumReadRequest <= RecordHeaderSize, "Rejected read cannot request a payload-sized buffer.");
        AssertNoWrites(stream, before);
    }

    private static DataStorage Open(ProbeStream stream, string mode, ushort headerSize, ProbeSerializer serializer, Action scanned,
        DataStorage.OnException policy = DataStorage.OnException.Default, DataStorage.OpenStateDelegate? listener = null) =>
        new(stream,
            scanBytesCallback: mode == "bytes-scanner" ? (_, _) => scanned() : null,
            scanObjectsCallback: mode == "objects-scanner" ? (_, _) => scanned() : null,
            headerSize: headerSize, serializer: serializer, onException: policy, listener: listener);

    private static byte[] RecordImage(int dataLength, byte[] payload, ushort headerSize = 0)
    {
        int position = DataStorage.HeaderOffset + headerSize;
        byte[] image = new byte[position + RecordHeaderSize + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(image, headerSize);
        BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(4), position);
        image.AsSpan(DataStorage.HeaderOffset, headerSize).Fill(0x5A);
        BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(position), long.MinValue);
        BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(position + 8), long.MinValue);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(position + 20), dataLength);
        payload.CopyTo(image, position + RecordHeaderSize);
        return image;
    }

    private static long ReadPrefix(DataStorage storage, long position, byte[] prefix)
    {
        try { return (long)PrefixReader.Invoke(storage, [position, prefix])!; }
        catch (TargetInvocationException error) when (error.InnerException != null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    private static void AssertNoWrites(ProbeStream stream, byte[] before)
    {
        Check.Equal(0, stream.WriteCalls, "Read/validation must not write the fixture.");
        Check.Equal(0, stream.SetLengthCalls, "Read/validation must not resize the fixture.");
        Check.That(before.AsSpan().SequenceEqual(stream.ToArray()), "Read/validation must preserve exact fixture bytes.");
    }

    private sealed class ProbeSerializer : IDataSerializer
    {
        public int DeserializeCalls { get; private set; }
        public void Serialize(Stream stream, object? value) => throw new InvalidOperationException("Read-only fixture serializer.");
        public object Deserialize(Stream stream)
        {
            this.DeserializeCalls++;
            using var data = new MemoryStream();
            stream.CopyTo(data);
            return Convert.ToHexString(data.ToArray());
        }
    }

    private class ProbeStream(byte[] bytes) : MemoryStream(bytes, 0, bytes.Length, writable: true, publiclyVisible: true)
    {
        public int ReadCalls { get; private set; }
        public int MaximumReadRequest { get; private set; }
        public int WriteCalls { get; private set; }
        public int SetLengthCalls { get; private set; }
        public int MaximumChunk { get; init; } = int.MaxValue;
        public void ResetActivity() { this.ReadCalls = this.MaximumReadRequest = this.WriteCalls = this.SetLengthCalls = 0; }
        public override int Read(byte[] buffer, int offset, int count)
        { this.ReadCalls++; this.MaximumReadRequest = Math.Max(this.MaximumReadRequest, count); return base.Read(buffer, offset, Math.Min(count, this.MaximumChunk)); }
        public override int Read(Span<byte> buffer)
        { this.ReadCalls++; this.MaximumReadRequest = Math.Max(this.MaximumReadRequest, buffer.Length); return base.Read(buffer[..Math.Min(buffer.Length, this.MaximumChunk)]); }
        public override void Write(byte[] buffer, int offset, int count) { this.WriteCalls++; base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { this.WriteCalls++; base.Write(buffer); }
        public override void SetLength(long value) { this.SetLengthCalls++; base.SetLength(value); }
    }

    private sealed class SparseProbeStream(byte[] bytes, long logicalLength) : ProbeStream(bytes)
    {
        public bool ResizeAttempted { get; private set; }
        public override long Length => logicalLength;
        public override void SetLength(long value)
        {
            this.ResizeAttempted = true;
            throw new InvalidOperationException("Sparse bounds fixture forbids physical resizing.");
        }
    }
}
