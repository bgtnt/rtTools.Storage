using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.ExceptionServices;
using RtTools;

namespace HistoryAcceptance;

internal static class DataStorageFailureChecks
{
    private static readonly PropertyInfo? ErrorProperty = typeof(DataStorage).GetProperty("StorageException");
    private static readonly PropertyInfo? FaultProperty = typeof(DataStorage).GetProperty("IsFaulted");
    private static readonly MethodInfo Prefix = typeof(DataStorage).GetMethod("ReadDataPrefix", BindingFlags.Instance | BindingFlags.NonPublic)!;

    // Safe on preview.3: tiny buffers, one controlled physical failure, then a real
    // continued-operation oracle before optional new status properties are inspected.
    public static void BaselineContracts(CheckContext ctx)
    {
        PhysicalFailure(ctx, "Flush");
    }

    public static void Contracts(CheckContext ctx)
    {
        BaselineContracts(ctx);
        PartialWrite(ctx, payload: false);
        PartialWrite(ctx, payload: true);
        PartialMutationValidation(ctx);
        UnexpectedEof(ctx);
        foreach (string operation in new[] { "Length", "Seek", "Read", "SetLength" }) PhysicalFailure(ctx, operation);

        ctx.Run("data-storage.failure.payload-array-read", () =>
        {
            using var stream = new FaultStream();
            using var storage = new DataStorage(stream);
            long id = storage.Add(new byte[] { 1, 2, 3 });
            var error = new IOException("payload-array-read-control");
            int spans = stream.SpanReadCalls;
            int arrays = stream.ArrayReadCalls;
            stream.Arm("Read", error, match: 2);
            Check.That(ReferenceEquals(error, Check.Throws<IOException>(() => storage.GetData(id), "Payload read fails after its complete header.")), "Payload read exception identity.");
            Check.Equal(spans + 1, stream.SpanReadCalls, "One complete span header read precedes the failure.");
            Check.Equal(arrays + 1, stream.ArrayReadCalls, "The failing payload request uses the array Read overload.");
            stream.Disarm();
            AssertBlocked(storage, stream, error);
            return "The span header read completes before a controlled payload byte[] Read failure; that array-boundary exception is retained and later storage operations perform no I/O.";
        });

        ctx.Run("data-storage.failure.update-after-nested-remove", () =>
        {
            using var stream = new FaultStream(Records(3));
            using var storage = new DataStorage(stream);
            stream.PatchInt32(62 + 20, -1);
            var error = Check.Throws<DataStorage.DataSourceException>(() => storage.Update(12, new byte[] { 8, 9 }),
                "Growing the first record removes it before the last header fails validation during append.");
            Check.Equal(37L, storage.FirstPosition, "Nested remove changed the advisory first-position snapshot.");
            Check.Equal(37L, BinaryPrimitives.ReadInt64LittleEndian(stream.ToArray().AsSpan(4)), "Nested remove physically published the new first pointer.");
            Check.Equal(long.MinValue, BinaryPrimitives.ReadInt64LittleEndian(stream.ToArray().AsSpan(37)), "Nested remove also repaired the second record's Prev pointer.");
            AssertBlocked(storage, stream, error);
            return "Three one-byte records at 12/37/62 force a completed inner remove, then last-header validation fails during outer Update append; the outer epoch preserves sticky failure across the successful nested mutation, with no rollback claim.";
        });

        ctx.Run("data-storage.failure.non-io-source-exception", () =>
        {
            using var stream = new FaultStream();
            using var storage = new DataStorage(stream);
            var error = new NotSupportedException("owned-source-write-control");
            stream.Arm("Write", error);
            Check.That(ReferenceEquals(error, Check.Throws<NotSupportedException>(() => storage.Add(new byte[] { 3 }), "Valid source request can fail with a non-IOException.")), "Actual source exception is unchanged.");
            stream.Disarm();
            AssertBlocked(storage, stream, error);
            return "Classification follows the owned-source operation boundary, including NotSupportedException, rather than broadly catching application exceptions or filtering only IOException.";
        });

        ctx.Run("data-storage.failure.destination-is-independent", () =>
        {
            using var source = new FaultStream();
            using var storage = new DataStorage(source);
            long id = storage.Add(new byte[] { 4, 5, 6 });
            using var destination = new FaultStream();
            var error = new IOException("destination-control");
            destination.Arm("Write", error);
            Check.That(ReferenceEquals(error, Check.Throws<IOException>(() => storage.CopyTo(destination), "Destination failure propagates.")), "Destination error identity.");
            AssertHealthy(storage);
            Check.That(storage.GetData(id).AsSpan().SequenceEqual(new byte[] { 4, 5, 6 }), "Source remains readable after destination failure.");
            using var copy = new MemoryStream();
            storage.CopyTo(copy);
            Check.That(source.ToArray().AsSpan().SequenceEqual(copy.ToArray()), "Successful CopyTo includes normal EOF without faulting the source.");
            AssertHealthy(storage);
            return "CopyTo destination Write failure is outside source failure tracking; source remains usable and normal copy EOF remains healthy.";
        });

        ctx.Run("data-storage.failure.codec-and-callback-controls", () =>
        {
            using var stream = new FaultStream();
            var codec = new Codec();
            using var storage = new DataStorage(stream, headerSize: 8, serializer: codec);
            var error = new IOException("application-codec-control");
            codec.WriteError = error;
            byte[] before = stream.ToArray();
            Check.That(ReferenceEquals(error, Check.Throws<IOException>(() => storage.Add((object)7), "Application writer failure.")), "Writer identity.");
            AssertHealthy(storage);
            Check.That(before.AsSpan().SequenceEqual(stream.ToArray()), "Codec failure precedes persistence.");
            codec.WriteError = null;
            long id = storage.Add((object)7);
            codec.ReadError = error;
            var decoding = Check.Throws<DataStorage.DataSourceException>(() => _ = storage[id], "Application decoder error retains its existing wrapper.");
            Check.That(ReferenceEquals(error, decoding.InnerException), "Decoder cause identity.");
            AssertHealthy(storage);
            codec.ReadError = null;
            Check.Equal(7, (int)storage[id]!, "Decoder retry remains possible.");
            storage.SetHeaderValue(12, 5);
            before = stream.ToArray();
            Check.That(ReferenceEquals(error, Check.Throws<IOException>(() => storage.UpdateHeaderValue<int>(12, _ => throw error), "Application header callback error.")), "Callback identity.");
            AssertHealthy(storage);
            Check.That(before.AsSpan().SequenceEqual(stream.ToArray()), "Callback rejection writes nothing.");
            Check.Equal(6, storage.UpdateHeaderValue<int>(12, value => value + 1), "Callback retry succeeds.");
            return "Serializer and header callbacks throwing IOException do not poison storage; writer bytes remain unchanged and controlled decode/update retries succeed.";
        });

        ctx.Run("data-storage.failure.argument-format-controls", () =>
        {
            using var stream = new FaultStream();
            using var storage = new DataStorage(stream);
            long id = storage.Add(new byte[] { 1 });
            Check.Throws<ArgumentNullException>(() => storage.Add((byte[])null!), "Null data argument.");
            Check.Throws<DataStorage.DataSourceException>(() => storage.GetData(-1), "Invalid position.");
            Check.Throws<DataStorage.DataSourceException>(() => storage.GetHeaderValue<int>(0), "Invalid reserved header offset.");
            stream.PatchInt32(id + 20, -1);
            Check.Throws<DataStorage.DataSourceException>(() => storage.GetData(id), "Detected malformed length before mutation.");
            AssertHealthy(storage);
            stream.PatchInt32(id + 20, 1);
            Check.Equal((byte)1, storage.GetData(id).Single(), "Explicitly repaired test fixture remains readable.");
            return "Argument/bounds and pre-mutation record-format errors remain distinct from physical source failure; raw Stream patching is an explicit test escape.";
        });

        ctx.Run("data-storage.failure.constructor-policy", () =>
        {
            foreach (var policy in new[] { DataStorage.OnException.Default, DataStorage.OnException.Clear, DataStorage.OnException.RecoverOrClear })
            {
                using var stream = new FaultStream(TwoRecords());
                var first = new IOException("initial-length-control");
                stream.Arm("Length", first);
                stream.CloseError = new IOException("secondary-constructor-close");
                Check.That(ReferenceEquals(first, Check.Throws<IOException>(() => new DataStorage(stream, onException: policy), "Initial source failure overrides format policies.")), "Initial failure retained over cleanup failure.");
                Check.Equal(1, stream.CloseCalls, "Constructor cleanup closes once.");
                Check.Equal(0, stream.WriteCalls, "No Clear/recovery writes after source failure.");
                Check.Equal(0, stream.ResizeCalls, "No Clear/recovery resize after source failure.");
                Check.That(!stream.CanRead, "Constructor cleanup closes the owned stream.");
                stream.CloseError = null;
            }
            return "Initial Length source failure bypasses Default/Clear/RecoverOrClear and closes once; a secondary close error cannot replace the original exception.";
        });

        ctx.Run("data-storage.failure.constructor-caught-listener-fault", () =>
        {
            using var stream = new FaultStream(TwoRecords());
            var first = new IOException("constructor-listener-flush");
            Check.That(ReferenceEquals(first, Check.Throws<IOException>(() => new DataStorage(stream,
                onException: DataStorage.OnException.RecoverOrClear, listener: (state, data) =>
                {
                    if (state != DataStorage.OpenState.Opened) return;
                    stream.Arm("Flush", first);
                    Check.Throws<IOException>(((DataStorage)data).Flush, "Listener catches the underlying source error.");
                }), "Construction cannot succeed after the listener catches a source failure.")), "Constructor preserves the original source failure.");
            Check.Equal(1, stream.CloseCalls, "Caught listener failure still closes owned source.");
            Check.Equal(0, stream.WriteCalls, "No Clear/recovery after a caught source fault.");
            Check.Equal(0, stream.ResizeCalls, "No resize after a caught source fault.");
            return "An Opened listener catches its own source Flush failure, but the constructor rechecks and closes/rethrows the first failure instead of returning a faulted writer or applying Clear.";
        });

        ctx.Run("data-storage.failure.constructor-format-close-once", () =>
        {
            byte[] image = new byte[12];
            BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(4), 12); // first header is absent
            using var stream = new FaultStream(image);
            var closing = new IOException("format-policy-close-control");
            stream.CloseError = closing;
            try
            {
                Check.That(ReferenceEquals(closing, Check.Throws<IOException>(() => new DataStorage(stream), "Format-policy Close itself can fail.")),
                    "The first actual source failure is the close error.");
                Check.Equal(1, stream.CloseCalls, "Outer constructor cleanup must not retry a format-policy Close that already failed.");
            }
            finally { stream.CloseError = null; }
            return "A format error reaches CloseAndThrow, whose controlled Close failure becomes the first source fault; constructor cleanup preserves it and performs exactly one physical close attempt.";
        });

        ctx.Run("data-storage.failure.recovery-write", () =>
        {
            byte[] image = TwoRecords();
            BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(12), 0); // corrupt first Prev; second record remains recoverable
            using var stream = new FaultStream(image);
            var error = new IOException("recovery-partial-write");
            stream.Arm("Write", error, prefixBytes: 3);
            Check.That(ReferenceEquals(error, Check.Throws<IOException>(() => new DataStorage(stream, onException: DataStorage.OnException.RecoverOrClear), "Recovery source write failure.")), "Recovery first failure identity.");
            Check.Equal(1, stream.WriteCalls, "Recovery stops at its first failed repair write.");
            Check.Equal(0, stream.ResizeCalls, "Recovery failure cannot fall through to Clear.");
            Check.Equal(1, stream.CloseCalls, "Recovery source failure still closes.");
            return "A recoverable candidate with a controlled prefix-write failure aborts recovery and suppresses Clear; original exception survives cleanup. No general recovery guarantee is claimed.";
        });

        ctx.Run("data-storage.failure.dispose-first-and-secondary", () =>
        {
            using var stream = new FaultStream();
            var storage = new DataStorage(stream);
            var first = new IOException("first-flush-control");
            stream.Arm("Flush", first);
            Check.Throws<IOException>(storage.Flush, "First source failure.");
            var closing = new IOException("secondary-close-control");
            stream.CloseError = closing;
            Check.That(ReferenceEquals(closing, Check.Throws<IOException>(storage.Dispose, "Cleanup failure remains visible.")), "Dispose reports its actual cleanup error.");
            AssertFault(storage, first);
            Check.That(!stream.CanRead, "Dispose closes despite a prior failure.");
            int calls = stream.CloseCalls;
            storage.Dispose();
            Check.Equal(calls, stream.CloseCalls, "Repeated Dispose is inert.");
            stream.CloseError = null;
            using var closeOnly = new FaultStream();
            var secondStorage = new DataStorage(closeOnly);
            closeOnly.CloseError = closing;
            Check.Throws<IOException>(secondStorage.Dispose, "Close itself can be the first failure.");
            AssertFault(secondStorage, closing);
            secondStorage.Dispose();
            closeOnly.CloseError = null;
            return "Dispose remains available after failure, attempts close once, reports close errors without replacing the first latch, and is idempotent even when its first attempt throws.";
        });

        ctx.Run("data-storage.failure.source-callback-reentry", () =>
        {
            using var stream = new FaultStream();
            using var storage = new DataStorage(stream);
            long id = storage.Add(new byte[] { 7 });
            foreach (string operation in new[] { "Read", "Flush" })
            {
                stream.Callback = actual =>
                {
                    if (actual != operation) return;
                    stream.Callback = null;
                    int calls = stream.IOCalls;
                    Check.Throws<InvalidOperationException>(() => storage.Add(new byte[] { 9 }), "Source callback cannot mutate owner.");
                    Check.Throws<InvalidOperationException>(storage.Dispose, "Source callback cannot dispose owner.");
                    Check.Throws<InvalidOperationException>(storage.Flush, "Source callback cannot recursively flush owner.");
                    Check.Equal(calls, stream.IOCalls, "Reentry rejection precedes further source I/O.");
                };
                if (operation == "Read") Check.Equal((byte)7, storage.GetData(id).Single(), "Outer source read succeeds.");
                else storage.Flush();
                AssertHealthy(storage);
            }
            Check.Equal((byte)7, storage.GetData(id).Single(), "Rejected Dispose did not mark owner disposed.");
            return "Caught owner mutation/Flush/Dispose reentry from Read/Flush callbacks rejects before I/O or disposed mutation; the outer operation and later owner use remain healthy.";
        });

        ctx.Run("data-storage.failure.caught-callback-fault", () =>
        {
            using var stream = new FaultStream();
            var codec = new Codec();
            using var storage = new DataStorage(stream, headerSize: 8, serializer: codec);
            var first = new IOException("callback-owned-flush");
            stream.Arm("Flush", first);
            byte[] before = stream.ToArray();
            Check.Throws<InvalidOperationException>(() => storage.UpdateHeaderValue<int>(12, value =>
            { Check.Throws<IOException>(storage.Flush, "Callback catches physical source failure."); return value + 1; }), "Header update rechecks after callback.");
            Check.That(before.AsSpan().SequenceEqual(stream.ToArray()), "Caught callback fault cannot publish a header value.");
            AssertFault(storage, first);
            stream.Disarm();
            AssertBlocked(storage, stream, first, codec);
            return "A callback catching its own source failure cannot resume the outer mutation; the original physical error remains latched and all later operations stop before codecs/I/O.";
        });

        ctx.Run("data-storage.failure.deferred-iterators", () =>
        {
            using var stream = new FaultStream();
            using var storage = new DataStorage(stream);
            storage.Add(new byte[] { 7 });
            using var delayed = storage.ReadPositions.GetEnumerator();
            using var active = storage.ReadData.GetEnumerator();
            using var objects = storage.ReadObjects.GetEnumerator();
            Check.That(active.MoveNext(), "Active iterator has yielded its only row before the fault.");
            var first = new IOException("iterator-flush-control");
            stream.Arm("Flush", first);
            Check.Throws<IOException>(storage.Flush, "Fault while iterators are retained.");
            stream.Disarm();
            int calls = stream.IOCalls;
            foreach (Action action in new Action[] { () => delayed.MoveNext(), () => active.MoveNext(), () => objects.MoveNext() })
            {
                var blocked = Check.Throws<InvalidOperationException>(action, "Both first and terminal MoveNext must check owner availability.");
                Check.That(ReferenceEquals(first, blocked.InnerException), "Deferred iterator retains first cause.");
            }
            Check.Equal(calls, stream.IOCalls, "Deferred iterator checks perform no further source I/O.");
            return "Iterators created before the fault reject on deferred first MoveNext and after their last yielded row; failure is observable even when no more records remain.";
        });
    }

    private static void PartialWrite(CheckContext ctx, bool payload)
    {
        ctx.Run("data-storage.failure.partial-" + (payload ? "payload" : "header"), () =>
        {
            using var stream = new FaultStream();
            using var storage = new DataStorage(stream);
            var error = new IOException("partial-write-control");
            stream.Arm("Write", error, match: payload ? 2 : 1, prefixBytes: 2);
            Check.That(ReferenceEquals(error, Check.Throws<IOException>(() => storage.Add(new byte[] { 1, 2, 3, 4 }), "Prefix-write failure.")), "First write exception identity.");
            Check.That(stream.ToArray().Length > DataStorage.HeaderOffset, "The probe really changed bytes before throwing.");
            stream.Disarm();
            AssertBlocked(storage, stream, error);
            return "Controlled " + (payload ? "payload" : "header") + " prefix is physically written before IOException; first failure is retained and subsequent operations do not perform I/O or allocator mutation. Partial bytes are not rolled back.";
        });
    }

    private static void PhysicalFailure(CheckContext ctx, string operation)
    {
        ctx.Run("data-storage.failure.physical-" + operation.ToLowerInvariant(), () =>
        {
            using var stream = new FaultStream();
            using var storage = new DataStorage(stream);
            long id = storage.Add(new byte[] { 1, 2, 3 });
            var error = new IOException("physical-" + operation);
            stream.Arm(operation, error, resizeBeforeThrow: operation == "SetLength");
            Action action = operation == "Flush" ? storage.Flush : operation == "SetLength" ? storage.Clear : () => storage.GetData(id);
            Check.That(ReferenceEquals(error, Check.Throws<IOException>(action, "Owned source operation fails.")), "Physical failure identity.");
            if (operation == "SetLength") Check.Equal(DataStorage.HeaderOffset, stream.ToArray().Length, "Resize actually changed the source before failure.");
            stream.Disarm();
            AssertBlocked(storage, stream, error);
            return operation + " failure latches the original exception; all later operations reject before source access. SetLength control changes length before throwing.";
        });
    }

    private static void PartialMutationValidation(CheckContext ctx)
    {
        ctx.Run("data-storage.failure.partial-mutation-format", () =>
        {
            using var stream = new FaultStream(TwoRecords());
            using var storage = new DataStorage(stream);
            stream.PatchInt32(37 + 20, -1);
            var error = Check.Throws<DataStorage.DataSourceException>(() => storage.Remove(12), "Malformed next header is detected after first-pointer write.");
            Check.Equal(37L, BinaryPrimitives.ReadInt64LittleEndian(stream.ToArray().AsSpan(4)), "The first pointer really changed before next-header validation failed.");
            AssertBlocked(storage, stream, error);
            return "Remove writes the new first pointer before detecting a malformed neighbor; that post-mutation library exception becomes sticky, prevents continued allocator use and makes no rollback claim.";
        });
    }

    private static void UnexpectedEof(CheckContext ctx)
    {
        ctx.Run("data-storage.failure.exact-read-eof", () =>
        {
            using var stream = new FaultStream(TwoRecords());
            using var storage = new DataStorage(stream);
            stream.ReturnEof = true;
            var error = Check.Throws<DataStorage.DataSourceException>(() => storage.GetData(12), "Exact header read unexpectedly returns zero despite reported length.");
            stream.ReturnEof = false;
            AssertBlocked(storage, stream, error);
            return "An exact read returning zero before its declared boundary latches a controlled storage exception; ordinary partial reads and CopyTo EOF remain separate valid cases.";
        });
    }

    private static void AssertBlocked(DataStorage storage, FaultStream stream, Exception first, Codec? codec = null)
    {
        int calls = stream.IOCalls;
        byte[] before = stream.ToArray();
        int callbacks = 0;
        int writes = codec?.SerializeCalls ?? 0;
        Action[] operations =
        [
            () => storage.Add(new byte[] { 8 }), () => storage.Update(12, new byte[] { 8 }),
            () => storage.Add((object)8), () => storage.Update(12, (object)8),
            () => storage.Remove(12), storage.Clear, () => storage.Clear(0), storage.ClearHeader, storage.Flush,
            () => storage.GetData(12), () => _ = storage[12], () => storage.ReadPositions.ToArray(),
            () => storage.ReadData.ToArray(), () => storage.ReadObjects.ToArray(),
            () => storage.GetHeaderValue<int>(12), () => storage.SetHeaderValue(12, 1),
            () => storage.UpdateHeaderValue<int>(12, value => { callbacks++; return value + 1; }),
            () => storage.CopyTo(Stream.Null), () => ((ICollection<byte[]>)storage).Contains(null!),
            () => ((ICollection<byte[]>)storage).Remove(null!), () => ReadPrefix(storage)
        ];
        foreach (Action action in operations)
        {
            InvalidOperationException blocked = Check.Throws<InvalidOperationException>(action, "Faulted storage must reject continued use.");
            Check.That(ReferenceEquals(first, blocked.InnerException), "Later rejection retains the exact first exception as cause.");
        }
        Check.Equal(calls, stream.IOCalls, "Unavailable operations perform no further physical I/O.");
        Check.Equal(0, callbacks, "Unavailable operations do not invoke callbacks.");
        Check.Equal(writes, codec?.SerializeCalls ?? 0, "Unavailable object operations do not invoke serializer.");
        Check.That(before.AsSpan().SequenceEqual(stream.ToArray()), "Unavailable operations do not change bytes.");
        _ = storage.Count; _ = storage.FirstPosition; _ = storage.LastPosition; _ = storage.Version;
        AssertFault(storage, first);
    }

    private static void AssertFault(DataStorage storage, Exception first)
    {
        Check.That(ErrorProperty != null && FaultProperty != null, "Failure diagnostics must be public.");
        Check.That(ReferenceEquals(first, ErrorProperty!.GetValue(storage)), "StorageException preserves the first failure object.");
        Check.Equal(true, (bool)FaultProperty!.GetValue(storage)!, "IsFaulted.");
    }
    private static void AssertHealthy(DataStorage storage)
    {
        Check.That(ErrorProperty != null && FaultProperty != null, "Failure diagnostics must be public.");
        Check.That(ErrorProperty!.GetValue(storage) == null && !(bool)FaultProperty!.GetValue(storage)!, "Application-only rejection must not poison storage.");
    }
    private static void ReadPrefix(DataStorage storage)
    {
        try { Prefix.Invoke(storage, [12L, new byte[1]]); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static byte[] TwoRecords() => Records(2);
    private static byte[] Records(int count)
    {
        byte[] bytes = new byte[12 + count * 25];
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(4), 12);
        for (int i = 0; i < count; i++)
        {
            int position = 12 + i * 25;
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(position), i == 0 ? long.MinValue : position - 25);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(position + 8), i == count - 1 ? long.MinValue : position + 25);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(position + 20), 1);
            bytes[position + 24] = (byte)position;
        }
        return bytes;
    }

    private sealed class Codec : IDataSerializer
    {
        public Exception? WriteError { get; set; }
        public Exception? ReadError { get; set; }
        public int SerializeCalls { get; private set; }
        public void Serialize(Stream stream, object? value) { this.SerializeCalls++; if (this.WriteError != null) throw this.WriteError; stream.WriteByte((byte)(int)value!); }
        public object Deserialize(Stream stream) { if (this.ReadError != null) throw this.ReadError; return stream.ReadByte(); }
    }

    private sealed class FaultStream : MemoryStream
    {
        private string? operation;
        private Exception? error;
        private int match;
        private int seen;
        private int prefixBytes;
        private bool resizeBeforeThrow;
        public bool ReturnEof { get; set; }
        public Action<string>? Callback { get; set; }
        public Exception? CloseError { get; set; }
        public int IOCalls { get; private set; }
        public int WriteCalls { get; private set; }
        public int ResizeCalls { get; private set; }
        public int CloseCalls { get; private set; }
        public int ArrayReadCalls { get; private set; }
        public int SpanReadCalls { get; private set; }
        public FaultStream(byte[]? bytes = null) { if (bytes != null) { base.Write(bytes, 0, bytes.Length); base.Position = 0; } }
        public void Arm(string name, Exception failure, int match = 1, int prefixBytes = 0, bool resizeBeforeThrow = false)
        { this.operation = name; this.error = failure; this.match = match; this.seen = 0; this.prefixBytes = prefixBytes; this.resizeBeforeThrow = resizeBeforeThrow; this.WriteCalls = this.ResizeCalls = 0; }
        public void Disarm() { this.operation = null; }
        private bool Before(string name)
        { this.IOCalls++; this.Callback?.Invoke(name); return this.operation == name && ++this.seen == this.match; }
        public override long Length { get { if (this.Before("Length")) throw this.error!; return base.Length; } }
        public override long Position { get { if (this.Before("Position")) throw this.error!; return base.Position; } set { if (this.Before("Position")) throw this.error!; base.Position = value; } }
        public override long Seek(long offset, SeekOrigin origin) { if (this.Before("Seek")) throw this.error!; return base.Seek(offset, origin); }
        public override int Read(byte[] buffer, int offset, int count) { this.ArrayReadCalls++; if (this.Before("Read")) throw this.error!; return this.ReturnEof ? 0 : base.Read(buffer, offset, count); }
        public override int Read(Span<byte> buffer)
        { this.SpanReadCalls++; if (this.Before("Read")) throw this.error!; if (this.ReturnEof) return 0; byte[] data = new byte[buffer.Length]; int count = base.Read(data, 0, data.Length); data.AsSpan(0, count).CopyTo(buffer); return count; }
        public override void Write(byte[] buffer, int offset, int count)
        { this.WriteCalls++; if (this.Before("Write")) { base.Write(buffer, offset, Math.Min(count, this.prefixBytes)); throw this.error!; } base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { byte[] data = buffer.ToArray(); this.Write(data, 0, data.Length); }
        public override void SetLength(long value)
        { this.ResizeCalls++; if (this.Before("SetLength")) { if (this.resizeBeforeThrow) base.SetLength(value); throw this.error!; } base.SetLength(value); }
        public override void Flush() { if (this.Before("Flush")) throw this.error!; base.Flush(); }
        protected override void Dispose(bool disposing)
        { if (disposing) { this.CloseCalls++; this.IOCalls++; } base.Dispose(disposing); if (disposing && this.CloseError != null) throw this.CloseError; }
        public void PatchInt32(long offset, int value)
        { long saved = base.Position; base.Position = offset; byte[] data = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(data, value); base.Write(data, 0, data.Length); base.Position = saved; }
    }
}
