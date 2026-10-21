using System.Buffers.Binary;
using RtTools;

namespace HistoryAcceptance;

internal static class BufferedQueueChecks
{
    public static void Contracts(CheckContext ctx)
    {
        ctx.Run("queue.arguments-before-open", () =>
        {
            string path = PathFor(ctx, "arguments");
            Check.Throws<ArgumentOutOfRangeException>(() => new BufferedQueuePersistent<int>(path, 0), "Zero buffer must be rejected.");
            Check.That(!File.Exists(path), "An invalid buffer must not create a file.");
            byte[] sentinel = [9, 8, 7, 6];
            File.WriteAllBytes(path, sentinel);
            Check.Throws<ArgumentOutOfRangeException>(() => new BufferedQueuePersistent<int>(path, -1, FileMode.Create), "Negative buffer must be rejected before truncation.");
            Check.Throws<ArgumentException>(() => new BufferedQueuePersistent<int>(path, 2, FileMode.Create, reader: ReadInt), "An unpaired reader must be rejected before opening.");
            Check.Throws<ArgumentException>(() => new BufferedQueuePersistent<int>(path, 2, FileMode.Create, writer: WriteInt), "An unpaired writer must be rejected before opening.");
            Same(sentinel, File.ReadAllBytes(path), "Invalid file arguments must preserve existing bytes.");
            Exclusive(path);
            using var stream = new MemoryStream();
            Check.Throws<ArgumentOutOfRangeException>(() => new BufferedQueuePersistent<int>(stream, 0), "Invalid stream buffer must be rejected.");
            Check.Throws<ArgumentException>(() => new BufferedQueuePersistent<int>(stream, 1, reader: ReadInt), "Invalid stream codec pair must be rejected.");
            Check.That(stream.CanWrite, "Rejected arguments must leave the caller's stream open.");
            using var empty = Open(stream, 1);
            Check.Equal(0, empty.Count, "New queue must be empty.");
            Check.Throws<InvalidOperationException>(() => empty.Peek(), "Empty Peek must throw.");
            Check.Throws<InvalidOperationException>(() => empty.Dequeue(), "Empty Dequeue must throw.");
            return "Buffer/pair validation precedes file creation or truncation; invalid stream arguments retain caller ownership; empty Peek/Dequeue reject.";
        });

        ctx.Run("queue.fifo-reuse-reopen", () =>
        {
            foreach (int capacity in new[] { 1, 2 })
            {
                string path = PathFor(ctx, "fifo-" + capacity);
                using (var queue = Open(path, capacity))
                {
                    for (int value = 1; value <= 6; value++) queue.Enqueue(value);
                    Check.Equal(1, queue.Peek(), "Peek must return the earliest item.");
                    Check.Equal(1, queue.Dequeue(), "First dequeue must be FIFO.");
                    Check.Equal(2, queue.Dequeue(), "Second dequeue must be FIFO.");
                    queue.Enqueue(7);
                    queue.Enqueue(8);
                    ulong[] physical = Ordinals(queue);
                    Check.That(!physical.SequenceEqual(physical.Order()), "The fixture must actually reuse a hole before older records.");
                    Same(Enumerable.Range(3, 6), queue, "Enumeration must sort by persisted FIFO ordinal after physical reuse.");
                    Check.That(queue.BufferedCount <= capacity, "The resident cache must stay bounded.");
                }
                using (var queue = Open(path, capacity))
                {
                    Check.Equal(capacity, queue.BufferedCount, "Reopen must cache the earliest bounded prefix.");
                    Check.Equal(3, queue.Peek(), "Reopen must reconstruct the earliest item.");
                    queue.Enqueue(9);
                    Same(Enumerable.Range(3, 7), queue, "Reconstructed max ordinal must place a new item last.");
                    foreach (int value in Enumerable.Range(3, 7))
                    {
                        Check.Equal(value, queue.Peek(), "Refill Peek must stay FIFO.");
                        Check.Equal(value, queue.Dequeue(), "Refill Dequeue must stay FIFO.");
                        Check.That(queue.BufferedCount <= capacity, "Refill must respect capacity.");
                    }
                    Check.Equal(0, queue.Count, "Draining must remove every persisted record.");
                    Check.Throws<InvalidOperationException>(() => queue.Dequeue(), "Drained queue must reject Dequeue.");
                    queue.Enqueue(10);
                    queue.Clear();
                    queue.Enqueue(11);
                    Same(new ulong[] { 1 }, Ordinals(queue), "Clear must reset the ordinal counter.");
                    Check.Equal(11, queue.Dequeue(), "The cleared queue must remain usable.");
                }
                using (var queue = Open(path, capacity)) Check.Equal(0, queue.Count, "An empty queue must reopen empty.");
                Exclusive(path);
            }
            return "Buffers 1 and 2 preserve FIFO through confirmed physical hole reuse, sorted enumeration, clean reopen, counter reconstruction, repeated refill, drain and Clear.";
        });

        ctx.Run("queue.bounded-refill-payload-reads", () =>
        {
            using var stream = new RecordingStream();
            List<int> decoded = [];
            using var queue = Open(stream, 2, bytes => { int value = ReadPadded(bytes); decoded.Add(value); return value; }, WritePadded);
            for (int value = 1; value <= 6; value++) queue.Enqueue(value);
            Check.Equal(0, decoded.Count, "Enqueue must not invoke the reader.");
            Check.Equal(1, queue.Dequeue(), "The first cached item must be available.");
            Check.Equal(2, queue.Dequeue(), "The second cached item must be available.");
            Check.Equal(0, queue.BufferedCount, "The fixture must require refill.");
            var positions = queue.Storage.Storage.ReadData.ToDictionary(pair => ReadPadded(pair.Value[12..]), pair => pair.Key);
            stream.Reads.Clear();
            decoded.Clear();
            Check.Equal(3, queue.Peek(), "Refill must select the earliest remaining ordinal.");
            Same(new[] { 3, 4 }, decoded, "Refill must decode exactly the selected bounded prefix.");
            Check.Equal(2, queue.BufferedCount, "Refill must populate exactly two cache slots.");
            foreach (int value in new[] { 3, 4, 5, 6 })
            {
                // DataStorage header is 24 bytes; queue envelope is ordinal(8) + length(4).
                long start = positions[value] + 24 + 12;
                bool payloadRead = stream.Reads.Any(read => read.Position < start + 512 && read.Position + read.Count > start);
                Check.Equal(value <= 4, payloadRead, "Only selected records may have their value payload read during refill: " + value);
            }
            return "A tracked stream with 512-byte payloads proves refill scans metadata for the backlog but reads/decodes full payloads only for the earliest two selected records.";
        });

        ctx.Run("queue.codec-failures-preserve-state", () =>
        {
            using (var stream = new MemoryStream())
            {
                var failure = new IOException("controlled queue encoder failure");
                using var queue = Open(stream, 2, ReadInt, value => value == 99 ? throw failure : value == 100 ? null! : WriteInt(value));
                queue.Enqueue(1);
                byte[] before = stream.ToArray();
                Check.That(ReferenceEquals(failure, Check.Throws<IOException>(() => queue.Enqueue(99), "Encoder failure must escape.")), "The encoder exception must be preserved.");
                Check.Throws<InvalidDataException>(() => queue.Enqueue(100), "A null payload must be rejected.");
                Same(before, stream.ToArray(), "Failed encodes must not change persisted bytes.");
                Check.Equal(1, queue.Count, "Failed encodes must not change count.");
                Check.Equal(1, queue.BufferedCount, "Failed encodes must not change the cache.");
                queue.Enqueue(2);
                Same(new ulong[] { 1, 2 }, Ordinals(queue), "Failed encodes must not consume an ordinal.");
            }
            using (var stream = new MemoryStream())
            {
                bool fail = false;
                var failure = new IOException("controlled queue decoder failure");
                using var queue = Open(stream, 2, bytes => { int value = ReadInt(bytes); return fail && value == 4 ? throw failure : value; }, WriteInt);
                for (int value = 1; value <= 6; value++) queue.Enqueue(value);
                queue.Dequeue(); queue.Dequeue();
                byte[] before = stream.ToArray();
                fail = true;
                Check.That(ReferenceEquals(failure, Check.Throws<IOException>(() => queue.Peek(), "A selected decoder failure must escape.")), "The decoder exception must be preserved.");
                Check.Equal(4, queue.Count, "Failed refill must not remove persisted items.");
                Check.Equal(0, queue.BufferedCount, "Failed refill must not expose a partially decoded cache.");
                Same(before, stream.ToArray(), "Failed refill must preserve persisted bytes.");
                fail = false;
                foreach (int value in new[] { 3, 4, 5, 6 }) Check.Equal(value, queue.Dequeue(), "A corrected reader must be able to retry refill.");
            }
            return "Throwing/null encoders leave bytes, count, cache and next ordinal unchanged; a decoder failing on the second selected item leaves no partial cache and permits a successful retry.";
        });

        ctx.Run("queue.codec-reentry-rejected", () =>
        {
            using var stream = new MemoryStream();
            BufferedQueuePersistent<int>? queue = null;
            Action? reentry = null;
            bool readerReentry = false;
            queue = Open(stream, 1,
                bytes => { if (readerReentry) queue!.Peek(); return ReadInt(bytes); },
                value =>
                {
                    if (reentry != null)
                    {
                        Check.Equal(1, queue!.Count, "Read-only Count is allowed inside a codec.");
                        Check.Equal(1, queue.BufferedCount, "Read-only BufferedCount is allowed inside a codec.");
                        reentry();
                    }
                    return WriteInt(value);
                });
            using (queue)
            {
                queue.Enqueue(1);
                Action[] attempts = [() => queue.Enqueue(9), () => queue.Dequeue(), () => queue.Peek(), () => queue.Clear(), () => queue.Dispose(), () => queue.ToArray()];
                foreach (Action attempt in attempts)
                {
                    byte[] before = stream.ToArray();
                    reentry = attempt;
                    Check.Throws<InvalidOperationException>(() => queue.Enqueue(2), "A codec must not reenter queue operations.");
                    reentry = null;
                    Same(before, stream.ToArray(), "Rejected callback reentry must not change persisted bytes.");
                    Check.Equal(1, queue.Count, "Rejected callback reentry must not dispose or mutate the queue.");
                }
                queue.Enqueue(2);
                Same(new ulong[] { 1, 2 }, Ordinals(queue), "Rejected reentry must not consume the outer ordinal.");
                Check.Equal(1, queue.Dequeue(), "The original cached value must survive rejected reentry.");
                readerReentry = true;
                Check.Throws<InvalidOperationException>(() => queue.Peek(), "A decoder must not recursively refill the queue.");
                Check.Equal(0, queue.BufferedCount, "Rejected recursive refill must leave an empty cache.");
                readerReentry = false;
                Check.Equal(2, queue.Dequeue(), "The codec guard must be restored after an exception.");
            }
            return "Same-queue mutation, Peek, enumeration and Dispose from an encoder reject before mutation; reader recursion rejects without partial refill; finally-restored guard permits subsequent operations.";
        });

        ctx.Run("queue.default-codec-and-snapshot", () =>
        {
            string path = PathFor(ctx, "default");
            using (var queue = new BufferedQueuePersistent<string?>(path, 1))
            {
                Quiet(queue);
                queue.Enqueue(null);
                queue.Enqueue("second");
                Check.That(queue.Peek() == null, "The default codec must retain a null reference value.");
            }
            using (var queue = new BufferedQueuePersistent<string?>(path, 1))
            {
                Quiet(queue);
                using var snapshot = queue.GetEnumerator();
                Check.That(snapshot.MoveNext() && snapshot.Current == null, "Default null must survive reopen.");
                queue.Enqueue("third");
                List<string?> remaining = [];
                while (snapshot.MoveNext()) remaining.Add(snapshot.Current);
                Same(new string?[] { "second" }, remaining, "An enumerator started before mutation must retain its complete FIFO snapshot.");
                Same(new string?[] { null, "second", "third" }, queue, "A later enumeration must see the new value.");
                Check.That(queue.Dequeue() == null, "Null is a queued value, not an empty marker.");
                Check.Equal("second", queue.Dequeue(), "Default decoded values must remain FIFO.");
                Check.Equal("third", queue.Dequeue(), "Default codec refill must work.");
            }
            return "The existing default serializer supports null/string values across reopen; enumeration captures a full sorted snapshot at first MoveNext and excludes later enqueues.";
        });

        ctx.Run("queue.invalid-frame-and-open-cleanup", () =>
        {
            byte[][] invalid = [new byte[11], Frame(1, WriteInt(1), -1), Frame(1, WriteInt(1), 3), Frame(1, WriteInt(1), 5)];
            int number = 0;
            foreach (byte[] frame in invalid)
            {
                string path = PathFor(ctx, "bad-frame-" + number++);
                SeedFrame(path, frame);
                byte[] before = File.ReadAllBytes(path);
                int readerCalls = 0;
                Check.Throws<InvalidDataException>(() =>
                {
                    using var ignored = new BufferedQueuePersistent<int>(path, 1, reader: bytes => { readerCalls++; return ReadInt(bytes); }, writer: WriteInt);
                }, "Malformed queue envelopes must reject construction.");
                Check.Equal(0, readerCalls, "Envelope validation must precede the user decoder.");
                Exclusive(path);
                Same(before, File.ReadAllBytes(path), "Failed construction must preserve malformed input bytes.");
            }
            string validPath = PathFor(ctx, "open-decoder-failure");
            SeedFrame(validPath, Frame(1, WriteInt(7)));
            var failure = new IOException("controlled opening decoder failure");
            Check.That(ReferenceEquals(failure, Check.Throws<IOException>(() =>
            {
                using var ignored = new BufferedQueuePersistent<int>(validPath, 1, reader: _ => throw failure, writer: WriteInt);
            }, "Opening decoder failure must escape.")), "Opening must preserve the decoder failure.");
            Exclusive(validPath);
            var stream = new MemoryStream(File.ReadAllBytes(validPath));
            Check.Throws<IOException>(() => new BufferedQueuePersistent<int>(stream, 1, reader: _ => throw failure, writer: WriteInt), "Failed stream construction must close the owned stream.");
            Check.That(!stream.CanRead, "An opening failure after validation must release the owned stream.");
            return "Short/negative/mismatched frames reject before decoder invocation and preserve bytes; malformed-file and decoder failures immediately release owned files/streams without GC.";
        });

        ctx.Run("queue.ordinal-exhaustion", () =>
        {
            string path = PathFor(ctx, "ordinal-limit");
            SeedFrame(path, Frame(ulong.MaxValue, WriteInt(42)));
            int writes = 0;
            using var queue = Open(path, 1, ReadInt, value => { writes++; return WriteInt(value); });
            byte[] before = ((FileStream)queue.Storage.Storage.Stream).ReadSnapshot();
            Check.Throws<OverflowException>(() => queue.Enqueue(43), "The persisted maximum ordinal must reject wraparound.");
            Check.Equal(0, writes, "Ordinal exhaustion must be detected before invoking the encoder.");
            Check.Equal(1, queue.Count, "Overflow must preserve count.");
            Check.Equal(42, queue.Peek(), "Overflow must preserve the cached value.");
            Same(before, ((FileStream)queue.Storage.Storage.Stream).ReadSnapshot(), "Overflow must preserve persisted bytes.");
            queue.Clear();
            queue.Enqueue(43);
            Same(new ulong[] { 1 }, Ordinals(queue), "Clear must recover from ordinal exhaustion by resetting the counter.");
            Check.Equal(1, writes, "Only the successful enqueue may call the encoder.");
            return "A raw persisted ulong.MaxValue ordinal rejects enqueue before encoding or writing; count/cache/bytes stay unchanged and Clear restarts at ordinal 1.";
        });

        ctx.Run("queue.disposal", () =>
        {
            string path = PathFor(ctx, "dispose");
            var queue = Open(path, 2);
            queue.Enqueue(1);
            using var deferred = queue.GetEnumerator();
            queue.Dispose();
            queue.Dispose();
            Action[] operations = [() => _ = queue.Count, () => _ = queue.BufferedCount, () => queue.Peek(), () => queue.Dequeue(), () => queue.Enqueue(2), () => queue.Clear(), () => queue.ToArray(), () => deferred.MoveNext()];
            foreach (Action operation in operations) Check.Throws<ObjectDisposedException>(operation, "Disposed queue operations must reject even when values were cached.");
            Check.That(!queue.Storage.Storage.Stream.CanRead, "Disposal must close the owned file stream.");
            Exclusive(path);
            return "Repeated Dispose is safe; cached Peek/counts/mutations/enumeration reject after disposal, including an enumerator first advanced after disposal; the file immediately reopens exclusively.";
        });

        ctx.Run("queue.limited-list-dependency", () =>
        {
            Check.Throws<ArgumentOutOfRangeException>(() => new LimitedList<int>(0), "LimitedList must reject zero capacity.");
            var ring = new LimitedList<int>(3);
            foreach (int value in Enumerable.Range(1, 5)) ring.Add(value);
            Same(new[] { 3, 4, 5 }, ring, "Full Add must evict the oldest value through ring wrap.");
            ring.RemoveAt(0);
            Same(new[] { 4, 5 }, ring, "Removing the front must preserve order.");
            var selected = new LimitedList<int>(2);
            foreach (int value in new[] { 8, 2, 6, 1, 7, 3 })
            {
                int index = selected.BinarySearch(value);
                selected.InsertLastOut(index < 0 ? ~index : index, value);
            }
            Same(new[] { 1, 2 }, selected, "Sorted bounded insertion must retain the earliest two values.");
            Check.Equal(-1, selected.InsertLastOut(selected.Count, 9), "Insertion beyond a full selected prefix must be ignored.");
            selected.RemoveAt(0);
            selected.Add(9);
            Same(new[] { 2, 9 }, selected.ToArray(), "Selection list must support front removal and later append.");
            selected.Clear();
            Check.Equal(0, selected.Count, "Clear must empty the dependency.");
            return "The unmodified LimitedList dependency preserves bounded ring Add, front removal, binary-search/top-k insertion, ignored full tail insertion and Clear used by the queue.";
        });
    }

    private static BufferedQueuePersistent<int> Open(string path, int capacity, Converter<byte[], int>? reader = null, Converter<int, byte[]>? writer = null)
    {
        var queue = new BufferedQueuePersistent<int>(path, capacity, reader: reader ?? ReadInt, writer: writer ?? WriteInt);
        Quiet(queue);
        return queue;
    }

    private static BufferedQueuePersistent<int> Open(Stream stream, int capacity, Converter<byte[], int>? reader = null, Converter<int, byte[]>? writer = null)
    {
        var queue = new BufferedQueuePersistent<int>(stream, capacity, reader: reader ?? ReadInt, writer: writer ?? WriteInt);
        Quiet(queue);
        return queue;
    }

    private static void Quiet<T>(BufferedQueuePersistent<T> queue)
    {
        queue.Storage.FlushIdleMilliseconds = -1;
        queue.Storage.FlushUpdates = ushort.MaxValue;
    }

    private static string PathFor(CheckContext ctx, string name)
    {
        string directory = Path.Combine(ctx.DataRoot, "queue-contracts", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "queue.bin");
    }

    private static byte[] WriteInt(int value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static int ReadInt(byte[] bytes)
    {
        Check.Equal(4, bytes.Length, "The integer codec requires an exact four-byte payload.");
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static byte[] WritePadded(int value)
    {
        byte[] bytes = Enumerable.Repeat((byte)0xA5, 512).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static int ReadPadded(byte[] bytes)
    {
        Check.Equal(512, bytes.Length, "The padded codec requires an exact 512-byte payload.");
        Check.That(bytes.AsSpan(4).ToArray().All(value => value == 0xA5), "The padded codec must receive exact payload bytes.");
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static byte[] Frame(ulong ordinal, byte[] payload, int? declaredLength = null)
    {
        byte[] frame = new byte[12 + payload.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(frame, ordinal);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8), declaredLength ?? payload.Length);
        payload.CopyTo(frame, 12);
        return frame;
    }

    private static void SeedFrame(string path, byte[] frame)
    {
        using var storage = new DataStorage(File.Open(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None));
        storage.Add(frame);
    }

    private static ulong[] Ordinals(BufferedQueuePersistent<int> queue) => queue.Storage.Storage.ReadData
        .Select(pair => BinaryPrimitives.ReadUInt64LittleEndian(pair.Value)).ToArray();

    private static void Same<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message) => Check.That(expected.SequenceEqual(actual), message);

    private static void Exclusive(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Check.That(stream.CanWrite, "The owned file handle must have been released.");
    }

    private static byte[] ReadSnapshot(this FileStream stream)
    {
        long position = stream.Position;
        try
        {
            stream.Position = 0;
            byte[] bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            return bytes;
        }
        finally { stream.Position = position; }
    }

    private sealed class RecordingStream : MemoryStream
    {
        public List<(long Position, int Count)> Reads { get; } = [];

        public override int Read(byte[] buffer, int offset, int count)
        {
            long position = Position;
            int read = base.Read(buffer, offset, count);
            Reads.Add((position, read));
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            long position = Position;
            int read = base.Read(buffer);
            Reads.Add((position, read));
            return read;
        }
    }
}
