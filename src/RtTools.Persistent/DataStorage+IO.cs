using System;
using System.IO;
using System.Threading;

namespace RtTools
{
    partial class DataStorage
    {
        private readonly Stream ioStream;
        private readonly SourceCleanup sourceCleanup;
        private Exception? storageException;
        private int disposed;
        private int ioThread;
        private long mutationEpoch;

        // A construction owner and this core share one close-attempt claim, including
        // format-policy closes and cleanup after a source operation itself fails.
        internal sealed class SourceCleanup(bool closeOnFailure)
        {
            private int closeAttempted;
            internal bool CloseOnFailure { get; } = closeOnFailure;
            internal bool TryBeginClose() => Interlocked.Exchange(ref this.closeAttempted, 1) == 0;
        }

        /// <summary>The first source I/O or partial-mutation failure, retained for this instance's lifetime.</summary>
        public Exception? StorageException => Volatile.Read(ref this.storageException);

        /// <summary>Whether this instance must be disposed and its files inspected before reopening.</summary>
        public bool IsFaulted => this.StorageException != null;

        internal void ThrowIfInIoCallback()
        {
            if (Volatile.Read(ref this.ioThread) == Environment.CurrentManagedThreadId)
                throw new InvalidOperationException("Storage operations cannot re-enter the same owner from its source I/O callback.");
        }

        internal void ThrowIfUnavailable()
        {
            this.ThrowIfInIoCallback();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposed) != 0, this);
            Exception? error = this.StorageException;
            if (error != null)
                throw new InvalidOperationException("The storage has failed. Dispose it and inspect/recover its files before reopening.", error);
        }

        private void RememberFailure(Exception error) => Interlocked.CompareExchange(ref this.storageException, error, null);
        private void MutationStarting() => Interlocked.Increment(ref this.mutationEpoch);
        private void RememberMutationFailure(long epoch, Exception error)
        {
            if (Volatile.Read(ref this.mutationEpoch) != epoch) this.RememberFailure(error);
        }

        private static void UnexpectedEnd(Stream stream)
        {
            var error = new DataSourceException("unexpected end of stream");
            if (stream is SourceIO source) source.owner.RememberFailure(error);
            throw error;
        }

        private void CloseSource()
        {
            this.ThrowIfInIoCallback();
            if (!this.sourceCleanup.TryBeginClose()) return;
            Volatile.Write(ref this.ioThread, Environment.CurrentManagedThreadId);
            try { this.Stream.Close(); }
            catch (Exception error) { this.RememberFailure(error); throw; }
            finally { Volatile.Write(ref this.ioThread, 0); }
        }

        // The public Stream remains the original escape hatch. Only engine I/O passes
        // through this adapter; codecs, callbacks and CopyTo destination writes do not.
        private sealed class SourceIO(DataStorage storage) : Stream
        {
            internal readonly DataStorage owner = storage;

            private T ReadSource<T>(Func<Stream, T> operation)
            {
                this.owner.ThrowIfUnavailable();
                Volatile.Write(ref this.owner.ioThread, Environment.CurrentManagedThreadId);
                try { return operation(this.owner.Stream); }
                catch (Exception error) { this.owner.RememberFailure(error); throw; }
                finally { Volatile.Write(ref this.owner.ioThread, 0); }
            }

            private void WriteSource(Action<Stream> operation)
            {
                this.owner.ThrowIfUnavailable();
                this.owner.MutationStarting();
                Volatile.Write(ref this.owner.ioThread, Environment.CurrentManagedThreadId);
                try { operation(this.owner.Stream); }
                catch (Exception error) { this.owner.RememberFailure(error); throw; }
                finally { Volatile.Write(ref this.owner.ioThread, 0); }
            }

            public override bool CanRead => this.owner.Stream.CanRead;
            public override bool CanSeek => this.owner.Stream.CanSeek;
            public override bool CanWrite => this.owner.Stream.CanWrite;
            public override long Length => this.ReadSource(stream => stream.Length);
            public override long Position
            {
                get => this.ReadSource(stream => stream.Position);
                set => this.ReadSource(stream => { stream.Position = value; return 0; });
            }
            public override long Seek(long offset, SeekOrigin origin) => this.ReadSource(stream => stream.Seek(offset, origin));
            public override int Read(byte[] buffer, int offset, int count) => this.ReadSource(stream => stream.Read(buffer, offset, count));
            public override int Read(Span<byte> buffer)
            {
                this.owner.ThrowIfUnavailable();
                Volatile.Write(ref this.owner.ioThread, Environment.CurrentManagedThreadId);
                try { return this.owner.Stream.Read(buffer); }
                catch (Exception error) { this.owner.RememberFailure(error); throw; }
                finally { Volatile.Write(ref this.owner.ioThread, 0); }
            }
            public override void Write(byte[] buffer, int offset, int count) => this.WriteSource(stream => stream.Write(buffer, offset, count));
            public override void Write(ReadOnlySpan<byte> buffer)
            {
                this.owner.ThrowIfUnavailable();
                this.owner.MutationStarting();
                Volatile.Write(ref this.owner.ioThread, Environment.CurrentManagedThreadId);
                try { this.owner.Stream.Write(buffer); }
                catch (Exception error) { this.owner.RememberFailure(error); throw; }
                finally { Volatile.Write(ref this.owner.ioThread, 0); }
            }
            public override void SetLength(long value) => this.WriteSource(stream => stream.SetLength(value));
            public override void Flush() => this.ReadSource(stream => { stream.Flush(); return 0; });
            protected override void Dispose(bool disposing) { if (disposing) this.owner.CloseSource(); base.Dispose(disposing); }
        }
    }
}
