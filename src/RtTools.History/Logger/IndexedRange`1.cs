using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace RtTools.Logger
{
    // Shared by copies of one physical range. Never serialized into .index.
    internal sealed class RangeDeletionState(long deletedCount)
    {
        private long deletedCount = deletedCount;
        internal long DeletedCount => System.Threading.Volatile.Read(ref this.deletedCount);
        internal void SetKnown(long count) => System.Threading.Volatile.Write(ref this.deletedCount, count);
        // Caller holds the Log index write lock; concurrent summary scans are excluded.
        internal void RecordDeletion()
        {
            long count = this.DeletedCount;
            if (count >= 0) this.SetKnown(count + 1);
        }
    }

    internal struct IndexedRange<TKey>: IComparable<IndexedRange<TKey>> where TKey : Key, new()
    {
        /// <summary>
        /// Record Key size
        /// </summary>
        public static readonly int KeySize = LogRecord<TKey>.KeySize;

        /// <summary>
        /// Range record size
        /// </summary>
        public static readonly int Size = sizeof(long) + sizeof(long) + 2 * KeySize;

        public long Start { get; set; }
        public long End { get; set; }
        public TKey StartKey { get; set; }
        public TKey EndKey { get; set; }
        internal RangeDeletionState? DeletionState;

        public readonly long Count => this.End - this.Start;

        public readonly bool IsEmpty => this.Start >= this.End;
        //public bool IsStarted { get { return 0 <= this.Start; } }
        //public bool IsFinished { get { return 0 <= this.Start && this.Start <= this.End; } }

        public IndexedRange(Stream stream, byte[] buffer)
        {
            this.DeletionState = new RangeDeletionState(-1);
            int i = 0;
            int read = stream.Read(buffer, 0, Size);
            if (read != Size)
            {
                if (read <= 0)
                    throw new EndOfStreamException();

                int offset = read;
                while (offset < Size)
                {
                    read = stream.Read(buffer, offset, Size - offset);
                    if (read <= 0)
                        throw new EndOfStreamException();
                    offset += read;
                }
            }
            this.Start = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(i)); i += sizeof(long);
            this.End = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(i)); i += sizeof(long);
            this.StartKey = new TKey(); this.StartKey.Read(buffer, i); i += KeySize;
            this.EndKey = new TKey(); this.EndKey.Read(buffer, i); //i += KeySize;
        }

        public IndexedRange(byte[] buffer, int offset)
        {
            this.DeletionState = new RangeDeletionState(-1);
            this.Start = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset)); offset += sizeof(long);
            this.End = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset)); offset += sizeof(long);
            this.StartKey = new TKey(); this.StartKey.Read(buffer, offset); offset += KeySize;
            this.EndKey = new TKey(); this.EndKey.Read(buffer, offset);
        }

        public void Write(Stream stream, bool flush = true)
        {
            byte[] buffer = new byte[Size];
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(0, sizeof(long)), this.Start);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(sizeof(long), sizeof(long)), this.End);
            byte[] startKey = new byte[this.StartKey.Size];
            this.StartKey.Write(startKey, 0);
            Buffer.BlockCopy(startKey, 0, buffer, 2 * sizeof(long), startKey.Length);
            byte[] endKey = new byte[this.EndKey.Size];
            this.EndKey.Write(endKey, 0);
            Buffer.BlockCopy(endKey, 0, buffer, 2 * sizeof(long) + startKey.Length, endKey.Length);
            stream.Write(buffer, 0, buffer.Length);
            if (flush)
                stream.Flush();
        }

        public bool InRange(TKey key) => this.StartKey.CompareTo(key) <= 0 && key.CompareTo(this.EndKey) <= 0;

        public bool Intersects(TKey from, TKey to) => !(this.StartKey.CompareTo(to) >= 0 || this.EndKey.CompareTo(from) < 0); // [from, to)

        public void MergeWith(IndexedRange<TKey> ir)
        {
            if (this.IsEmpty)
            { // replace
                this.Start = ir.Start;
                this.End = ir.End;
                this.StartKey = ir.StartKey;
                this.EndKey = ir.EndKey;
            }
            else
            {
                if (ir.Start < this.Start) this.Start = ir.Start;
                if (this.End < ir.End) this.End = ir.End;
                this.StartKey.Merge(ir.StartKey, true);
                this.EndKey.Merge(ir.EndKey, false);
            }
        }

        public int CompareTo(IndexedRange<TKey> other) => this.EndKey.CompareTo(other.EndKey);
    }
}
