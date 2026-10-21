using System;
using System.Buffers.Binary;
using System.IO;

namespace RtTools
{
    partial class DataStorage
    {
        /// <summary>
        /// Record header and the shared little-endian record codec.
        /// Position is runtime state; only the other four fields are stored.
        /// </summary>
        private struct RecordHeader
        {
            public const int HeaderSize = 8 + 8 + 4 + 4;
            public long Position;
            public long Prev;
            public long Next;
            public int Info;
            public int DataLength;
            public readonly int RecordLength => HeaderSize + this.DataLength;

            public RecordHeader()
            {
                this.Position = 0;
                this.Prev = long.MinValue;
                this.Next = long.MinValue;
                this.Info = 0;
                this.DataLength = 0;
            }

            public RecordHeader(Stream stream, long position, Span<byte> buffer) : this()
            {
                stream.Seek(this.Position = position, SeekOrigin.Begin);
                ReadExactly(stream, buffer[..HeaderSize]);
                this.Read(buffer);
            }

            public void Read(ReadOnlySpan<byte> source)
            {
                this.Prev = BinaryPrimitives.ReadInt64LittleEndian(source);
                this.Next = BinaryPrimitives.ReadInt64LittleEndian(source[8..]);
                this.Info = BinaryPrimitives.ReadInt32LittleEndian(source[16..]);
                this.DataLength = BinaryPrimitives.ReadInt32LittleEndian(source[20..]);
            }

            public readonly void Write(Span<byte> destination)
            {
                BinaryPrimitives.WriteInt64LittleEndian(destination, this.Prev);
                BinaryPrimitives.WriteInt64LittleEndian(destination[8..], this.Next);
                BinaryPrimitives.WriteInt32LittleEndian(destination[16..], this.Info);
                BinaryPrimitives.WriteInt32LittleEndian(destination[20..], this.DataLength);
            }

            public readonly void Write(Stream stream, Span<byte> buffer)
            {
                this.Write(buffer);
                stream.Seek(this.Position, SeekOrigin.Begin);
                stream.Write(buffer[..HeaderSize]);
            }

            // Recovery retains array I/O for streams that override those operations.
            public readonly void Write(Stream stream, byte[] buffer)
            {
                this.Write(buffer.AsSpan(0, HeaderSize));
                stream.Seek(this.Position, SeekOrigin.Begin);
                stream.Write(buffer, 0, HeaderSize);
            }

            public override readonly string ToString() => this.Position + ": " + this.Prev + "-" + this.Next + "/" + this.DataLength;
        }
    }
}
