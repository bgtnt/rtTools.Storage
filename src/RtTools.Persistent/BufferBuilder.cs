using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace RtTools
{
    /// <summary>
    /// Facilitates building buffer of values (similar to <see cref="StringBuilder"/>)
    /// </summary>
    public sealed class BufferBuilder
    {
        /// <summary>
        /// Returns size Of (with Byte, Char exceptions resolved)
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public static int SizeOf(Type type)
        {
            ArgumentNullException.ThrowIfNull(type);

            if (type.IsEnum)
                type = Enum.GetUnderlyingType(type);

            return Type.GetTypeCode(type) switch
            {
                TypeCode.Boolean => sizeof(bool),
                TypeCode.SByte => sizeof(sbyte),
                TypeCode.Byte => sizeof(byte),
                TypeCode.Char => sizeof(char),
                TypeCode.Int16 => sizeof(short),
                TypeCode.UInt16 => sizeof(ushort),
                TypeCode.Int32 => sizeof(int),
                TypeCode.UInt32 => sizeof(uint),
                TypeCode.Single => sizeof(float),
                TypeCode.Int64 => sizeof(long),
                TypeCode.UInt64 => sizeof(ulong),
                TypeCode.Double => sizeof(double),
                _ => throw new NotSupportedException($"BufferBuilder supports only primitive array element types. '{type.FullName}' is not supported.")
            };
        }

        private int length;


        /// <summary>
        /// Get Buffer data
        /// </summary>
        public byte[] Buffer { get; private set; }

        /// <summary>
        /// Gets the written part of <see cref="Buffer"/> without allocating.
        /// </summary>
        public ReadOnlySpan<byte> WrittenSpan => this.Buffer.AsSpan(0, this.length);

        /// <summary>
        /// Gets the unread part of <see cref="Buffer"/> without allocating.
        /// </summary>
        public ReadOnlySpan<byte> UnreadSpan => this.Buffer.AsSpan(this.Offset, this.Left);

        /// <summary>
        /// Get/Set buffer length (to be consistent with <see cref="StringBuilder"/>)
        /// </summary>
        public int Length
        {
            get => this.length;
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException(nameof(value));

                int diff = value - this.length;
                if (diff != 0)
                { // different 
                    if (diff > 0) this.EnsureBuffer(diff);
                    this.length = value;
                }
            }
        }

        /// <summary>
        /// Get/Set Read offset
        /// </summary>
        public int Offset { get; set; }

        /// <summary>
        /// Get/Set bytes left to read
        /// </summary>
        public int Left
        {
            get => this.length - this.Offset;
            set => this.Length = this.Offset + value;
        }

        /// <summary>
        /// Get/Set Capacity (to add without reallocation)
        /// </summary>
        public int Capacity
        {
            get => this.Buffer.Length;
            set
            {
                if (value < this.length)
                    throw new ArgumentOutOfRangeException(nameof(value), "Capacity should be >= length");

                if (value != this.Buffer.Length)
                {
                    if (value == 0)
                        this.Buffer = EmptyBuffer;
                    else
                    {
                        byte[] temp = new byte[value];
                        System.Buffer.BlockCopy(this.Buffer, 0, temp, 0, this.length);
                        this.Buffer = temp;
                    }
                }
            }
        }

        private static readonly byte[] EmptyBuffer = [];
        /// <summary>
        /// Constructs empty buffer
        /// </summary>
        public BufferBuilder() => this.Buffer = EmptyBuffer;

        /// <summary>
        /// Constructs buffer with initial capacity
        /// </summary>
        /// <param name="capacity"></param>
        public BufferBuilder(int capacity) => this.Buffer = new byte[capacity];

        /// <summary>
        /// Constructs buffer with initial capacity
        /// </summary>
        /// <param name="data">initial data</param>
        public BufferBuilder(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            this.length = (this.Buffer = data).Length;
        }

        /// <summary>
        /// Ensures buffer has enough bytes count for data (if not - increases buffer length)
        /// </summary>
        /// <param name="count">needed bytes</param>
        private void EnsureBuffer(int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            int blen = this.Buffer.Length;
            if (count > (blen - this.length))
            { // not enough room
                long required = (long)this.length + count;
                if (required > int.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(count));

                blen = blen == 0 ? 16 : blen;
                while (blen < required)
                    blen = blen <= (int.MaxValue / 2) ? blen * 2 : int.MaxValue;

                if (this.length > 0)
                { // if there is something to copy
                    byte[] dst = new byte[blen];
                    System.Buffer.BlockCopy(this.Buffer, 0, dst, 0, this.length);
                    this.Buffer = dst;
                }
                else
                    this.Buffer = new byte[blen];
            }
        }

        /// <summary>
        /// Additional data provider - fired when no enough space to on <see cref="ReadByte"/> etc. methods
        /// TRUE - data was provided
        /// FALSE - no more data, but do not throw an exception
        /// </summary>
        /// <example>
        /// <para><code>Reader = (needed) => false</code></para>
        /// <para><code>Reader = (needed) => <see cref="Append(byte[])"/> != null</code></para>
        /// <para><code>Reader = (needed) => throw new <see cref="Exception"/>()</code></para>
        /// </example>
        public Action<int>? Reader { get; set; }

        /// <summary>
        /// Check if has space to put data
        /// If no enough data - rise an exception
        /// </summary>
        /// <param name="count"></param>
        /// <exception cref="IndexOutOfRangeException">if no enough data and no <see cref="Reader"/> was set (to read more data)</exception>
        /// <returns></returns>
        public void CheckSpace(int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            if (this.Offset < 0)
                throw new ArgumentOutOfRangeException(nameof(this.Offset));

            long required = (long)this.Offset + count;
            if (required > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(count));

            while (required > this.length)
            {
                Action<int> reader = this.Reader ?? throw new IndexOutOfRangeException();
                int lengthBefore = this.length;
                int offsetBefore = this.Offset;

                reader((int)(required - this.length));

                required = (long)this.Offset + count;
                if (required > int.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(count));

                if (required > this.length &&
                    this.length == lengthBefore &&
                    this.Offset == offsetBefore)
                    throw new IndexOutOfRangeException("Reader did not provide enough data.");
            }
        }

        /// <summary>
        /// Skip bytes
        /// </summary>
        /// <param name="len">bytes to skip</param>
        public void ReadSkip(int len)
        {
            if (len > 0)
            {
                this.CheckSpace(len);
                this.Offset += len;
            }
        }

        /// <summary>
        /// Append SByte value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private BufferBuilder AppendUnchecked(SByte value)
        {
            this.Buffer[this.length++] = (Byte)value;
            return this;
        }

        /// <summary>
        /// Append SByte value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public BufferBuilder Append(SByte value)
        {
            this.EnsureBuffer(1);
            return this.AppendUnchecked(value);
        }

        /// <summary>
        /// Read next Value as SByte  (no space check)
        /// </summary>
        /// <exception cref="IndexOutOfRangeException">if no enough data</exception>
        /// <returns></returns>
        private SByte ReadSByteUnchecked() => (sbyte)this.Buffer[this.Offset++];

        /// <summary>
        /// Read next Value as SByte
        /// </summary>
        /// <exception cref="IndexOutOfRangeException">if no enough data and no <see cref="Reader"/> was set (to read more data)</exception>
        /// <returns></returns>
        public SByte ReadSByte()
        {
            this.CheckSpace(sizeof(SByte));
            return this.ReadSByteUnchecked();
        }

        /// <summary>
        /// Append Byte value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private BufferBuilder AppendUnchecked(Byte value)
        {
            this.Buffer[this.length++] = value;
            return this;
        }

        /// <summary>
        /// Append Byte value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public BufferBuilder Append(Byte value)
        {
            this.EnsureBuffer(1);
            return this.AppendUnchecked(value);
        }

        /// <summary>
        /// Read next Value as Byte  (no space check)
        /// </summary>
        /// <exception cref="IndexOutOfRangeException">if no enough data</exception>
        /// <returns>next byte</returns>
        private Byte ReadByteUnchecked() => this.Buffer[this.Offset++];

        /// <summary>
        /// Read next Value as Byte
        /// </summary>
        /// <exception cref="IndexOutOfRangeException">if no enough data and no <see cref="Reader"/> was set (to read more data)</exception>
        /// <returns>next byte</returns>
        public Byte ReadByte()
        {
            this.CheckSpace(sizeof(Byte));
            return this.ReadByteUnchecked();
        }

        /// <summary>
        /// Append Int16 value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private BufferBuilder AppendUnchecked(Int16 value)
        {
            BinaryPrimitives.WriteInt16LittleEndian(this.Buffer.AsSpan(this.length), value);
            this.length += sizeof(Int16);
            return this;
        }

        /// <summary>
        /// Append Int16 value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public BufferBuilder Append(Int16 value)
        {
            this.EnsureBuffer(sizeof(Int16));
            return this.AppendUnchecked(value);
        }

        /// <summary>
        /// Read next Value as Int16 (no space check)
        /// </summary>
        /// <returns></returns>
        private Int16 ReadInt16Unchecked()
        {
            short value = BinaryPrimitives.ReadInt16LittleEndian(this.Buffer.AsSpan(this.Offset));
            this.Offset += sizeof(Int16);
            return value;
        }

        /// <summary>
        /// Read next Value as Int16
        /// </summary>
        /// <exception cref="IndexOutOfRangeException">if no enough data and no <see cref="Reader"/> was set (to read more data)</exception>
        /// <returns></returns>
        public Int16 ReadInt16()
        {
            this.CheckSpace(sizeof(Int16));
            return this.ReadInt16Unchecked();
        }

        /// <summary>
        /// Append UInt16 value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private BufferBuilder AppendUnchecked(UInt16 value) => this.AppendUnchecked(unchecked((Int16)value));

        /// <summary>
        /// Append UInt16 value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public BufferBuilder Append(UInt16 value) => this.Append(unchecked((Int16)value));

        /// <summary>
        /// Read next Value as UInt16 (no space check)
        /// </summary>
        /// <returns></returns>
        private UInt16 ReadUInt16Unchecked() => unchecked((UInt16)this.ReadInt16Unchecked());

        /// <summary>
        /// Read next Value as UInt16
        /// </summary>
        /// <returns></returns>
        public UInt16 ReadUInt16() => unchecked((UInt16)this.ReadInt16());

        /// <summary>
        /// Append Int32 value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private BufferBuilder AppendUnchecked(Int32 value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(this.Buffer.AsSpan(this.length), value);
            this.length += sizeof(Int32);
            return this;
        }

        /// <summary>
        /// Append Int32 value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public BufferBuilder Append(Int32 value)
        {
            this.EnsureBuffer(sizeof(Int32));
            return this.AppendUnchecked(value);
        }

        /// <summary>
        /// Read next Value as Int32 (no space check)
        /// </summary>
        /// <returns></returns>
        private Int32 ReadInt32Unchecked()
        {
            int value = BinaryPrimitives.ReadInt32LittleEndian(this.Buffer.AsSpan(this.Offset));
            this.Offset += sizeof(Int32);
            return value;
        }

        /// <summary>
        /// Read next Value as Int32
        /// </summary>
        /// <exception cref="IndexOutOfRangeException">if no enough data and no <see cref="Reader"/> was set (to read more data)</exception>
        /// <returns></returns>
        public Int32 ReadInt32()
        {
            this.CheckSpace(sizeof(Int32));
            return this.ReadInt32Unchecked();
        }

        /// <summary>
        /// Append UInt32 value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private BufferBuilder AppendUnchecked(UInt32 value) => this.AppendUnchecked(unchecked((Int32)value));

        /// <summary>
        /// Append UInt32 value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public BufferBuilder Append(UInt32 value) => this.Append(unchecked((Int32)value));

        /// <summary>
        /// Read next Value as UInt32 (no space check)
        /// </summary>
        /// <returns></returns>
        private UInt32 ReadUInt32Unchecked() => unchecked((UInt32)this.ReadInt32Unchecked());

        /// <summary>
        /// Read next Value as UInt32
        /// </summary>
        /// <exception cref="IndexOutOfRangeException">if no enough data and no <see cref="Reader"/> was set (to read more data)</exception>
        /// <returns></returns>
        public UInt32 ReadUInt32() => unchecked((UInt32)this.ReadInt32());

        /// <summary>
        /// Append Int64 value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private BufferBuilder AppendUnchecked(Int64 value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(this.Buffer.AsSpan(this.length), value);
            this.length += sizeof(Int64);
            return this;
        }

        /// <summary>
        /// Append Int64 value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public BufferBuilder Append(Int64 value)
        {
            this.EnsureBuffer(sizeof(Int64));
            return this.AppendUnchecked(value);
        }

        /// <summary>
        /// Read next Value as Int64 (no space check)
        /// </summary>
        /// <returns></returns>
        private Int64 ReadInt64Unchecked()
        {
            long value = BinaryPrimitives.ReadInt64LittleEndian(this.Buffer.AsSpan(this.Offset));
            this.Offset += sizeof(Int64);
            return value;
        }

        /// <summary>
        /// Read next Value as Int64
        /// </summary>
        /// <exception cref="IndexOutOfRangeException">if no enough data and no <see cref="Reader"/> was set (to read more data)</exception>
        /// <returns></returns>
        public Int64 ReadInt64()
        {
            this.CheckSpace(sizeof(Int64));
            return this.ReadInt64Unchecked();
        }

        /// <summary>
        /// Append UInt64 value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private BufferBuilder AppendUnchecked(UInt64 value) => this.AppendUnchecked(unchecked((Int64)value));

        /// <summary>
        /// Append UInt64 value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public BufferBuilder Append(UInt64 value) => this.Append(unchecked((Int64)value));

        /// <summary>
        /// Read next Value as UInt64 (no space check)
        /// </summary>
        /// <returns></returns>
        private UInt64 ReadUInt64Unchecked() => unchecked((UInt64)this.ReadInt64Unchecked());

        /// <summary>
        /// Read next Value as UInt64
        /// </summary>
        /// <exception cref="IndexOutOfRangeException">if no enough data and no <see cref="Reader"/> was set (to read more data)</exception>
        /// <returns></returns>
        public UInt64 ReadUInt64() => unchecked((UInt64)this.ReadInt64());

        /// <summary>
        /// Append float value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private BufferBuilder AppendUnchecked(Single value) => this.AppendUnchecked(BitConverter.SingleToInt32Bits(value));

        /// <summary>
        /// Append float value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public BufferBuilder Append(Single value) => this.Append(BitConverter.SingleToInt32Bits(value));

        /// <summary>
        /// Read next Value as float (no space check)
        /// </summary>
        /// <returns></returns>
        private Single ReadSingleUnchecked()
        {
            int value = this.ReadInt32Unchecked();
            return BitConverter.Int32BitsToSingle(value);
        }

        /// <summary>
        /// Read next Value as float
        /// </summary>
        /// <exception cref="IndexOutOfRangeException">if no enough data and no <see cref="Reader"/> was set (to read more data)</exception>
        /// <returns></returns>
        public Single ReadSingle()
        {
            int value = this.ReadInt32();
            return BitConverter.Int32BitsToSingle(value);
        }

        /// <summary>
        /// Append double value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private BufferBuilder AppendUnchecked(Double value) => this.AppendUnchecked(BitConverter.DoubleToInt64Bits(value));

        /// <summary>
        /// Append double value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public BufferBuilder Append(Double value) => this.Append(BitConverter.DoubleToInt64Bits(value));

        /// <summary>
        /// Read next Value as double (no space check)
        /// </summary>
        /// <returns></returns>
        private Double ReadDoubleUnchecked()
        {
            long value = this.ReadInt64Unchecked();
            return BitConverter.Int64BitsToDouble(value);
        }

        /// <summary>
        /// Read next Value as double
        /// </summary>
        /// <exception cref="IndexOutOfRangeException">if no enough data and no <see cref="Reader"/> was set (to read more data)</exception>
        /// <returns></returns>
        public Double ReadDouble()
        {
            long value = this.ReadInt64();
            return BitConverter.Int64BitsToDouble(value);
        }

        /// <summary>
        /// Append decimal value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private BufferBuilder AppendUnchecked(Decimal value)
        {
            MemoryMarshal.Write(this.Buffer.AsSpan(this.length, sizeof(Decimal)), in value);
            this.length += sizeof(Decimal);
            return this;
        }

        /// <summary>
        /// Append decimal value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public BufferBuilder Append(Decimal value)
        {
            this.EnsureBuffer(sizeof(Decimal));
            return this.AppendUnchecked(value);
        }

        /// <summary>
        /// Read next Value as decimal (no space check)
        /// </summary>
        /// <returns></returns>
        private Decimal ReadDecimalUnchecked()
        {
            Decimal value = MemoryMarshal.Read<Decimal>(this.Buffer.AsSpan(this.Offset, sizeof(Decimal)));
            this.Offset += sizeof(Decimal);
            return value;
        }

        /// <summary>
        /// Read next Value as decimal
        /// </summary>
        /// <exception cref="IndexOutOfRangeException">if no enough data and no <see cref="Reader"/> was set (to read more data)</exception>
        /// <returns></returns>
        public Decimal ReadDecimal()
        {
            this.CheckSpace(sizeof(Decimal));
            return this.ReadDecimalUnchecked();
        }

        /// <summary>
        /// Append boolean value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private BufferBuilder AppendUnchecked(Boolean value)
        {
            this.Buffer[this.length++] = value ? (byte)1 : (byte)0;
            return this;
        }

        /// <summary>
        /// Append boolean value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public BufferBuilder Append(Boolean value)
        {
            this.EnsureBuffer(1);
            return this.AppendUnchecked(value);
        }

        /// <summary>
        /// Read next Value as boolean (no space check)
        /// </summary>
        /// <exception cref="IndexOutOfRangeException">if no enough data and no <see cref="Reader"/> was set (to read more data)</exception>
        /// <returns></returns>
        private Boolean ReadBooleanUnchecked() => this.Buffer[this.Offset++] != 0;

        /// <summary>
        /// Read next Value as boolean
        /// </summary>
        /// <returns></returns>
        public Boolean ReadBoolean()
        {
            this.CheckSpace(sizeof(Boolean));
            return this.ReadBooleanUnchecked();
        }

        /// <summary>
        /// Append char value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private BufferBuilder AppendUnchecked(char value) => this.AppendUnchecked((UInt16)value);

        /// <summary>
        /// Append char value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public BufferBuilder Append(char value) => this.Append((UInt16)value);

        /// <summary>
        /// Read next Value as Char (no space check)
        /// </summary>
        /// <returns></returns>
        private Char ReadCharUnchecked() => (Char)this.ReadInt16Unchecked();

        /// <summary>
        /// Read next Value as Char
        /// </summary>
        /// <exception cref="IndexOutOfRangeException">if no enough data and no <see cref="Reader"/> was set (to read more data)</exception>
        /// <returns></returns>
        public Char ReadChar() => (Char)this.ReadInt16();

        /// <summary>
        /// Append date-time value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private BufferBuilder AppendUnchecked(DateTime value) => this.AppendUnchecked(value.ToBinary());

        /// <summary>
        /// Append date-time value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public BufferBuilder Append(DateTime value) => this.Append(value.ToBinary());

        /// <summary>
        /// Read next Value as DateTime (no space check)
        /// </summary>
        /// <returns></returns>
        private DateTime ReadDateTimeUnchecked() => DateTime.FromBinary(this.ReadInt64Unchecked());

        /// <summary>
        /// Read next Value as DateTime
        /// </summary>
        /// <returns></returns>
        public DateTime ReadDateTime() => DateTime.FromBinary(this.ReadInt64());

        /// <summary>
        /// Append timespan value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private BufferBuilder AppendUnchecked(TimeSpan value) => this.AppendUnchecked(value.Ticks);

        /// <summary>
        /// Append timespan value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public BufferBuilder Append(TimeSpan value) => this.Append(value.Ticks);

        /// <summary>
        /// Read next Value as TimeSpan (no space check)
        /// </summary>
        /// <returns></returns>
        private TimeSpan ReadTimeSpanUnchecked() => new(this.ReadInt64Unchecked());

        /// <summary>
        /// Read next Value as TimeSpan
        /// </summary>
        /// <returns></returns>
        public TimeSpan ReadTimeSpan() => new(this.ReadInt64());

        /// <summary>
        /// Append array
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public BufferBuilder Append(byte[] value)
        {
            ArgumentNullException.ThrowIfNull(value);
            return this.Append(value.AsSpan());
        }

        /// <summary>
        /// Append bytes without forcing callers to allocate a byte[] slice.
        /// </summary>
        public BufferBuilder Append(ReadOnlySpan<byte> value)
        {
            if (!value.IsEmpty)
            {
                this.EnsureBuffer(value.Length);
                value.CopyTo(this.Buffer.AsSpan(this.length));
                this.length += value.Length;
            }
            return this;
        }

        /// <summary>
        /// Append unmanaged values as their raw in-memory bytes.
        /// </summary>
        public BufferBuilder Append<T>(ReadOnlySpan<T> value) where T : unmanaged
            => this.Append(MemoryMarshal.AsBytes(value));

        /// <summary>
        /// Append array
        /// </summary>
        /// <param name="value"></param>
        /// <param name="startIndex">start Index</param>
        /// <param name="length">add count</param>
        /// <returns></returns>
        public BufferBuilder Append(byte[] value, int startIndex, int length)
        {
            ArgumentNullException.ThrowIfNull(value);
            return this.Append(value.AsSpan(startIndex, length));
        }

        /// <summary>
        /// Insert bytes in the Buffer at position <see cref="Offset"/>
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public BufferBuilder Insert(byte[] value)
        {
            ArgumentNullException.ThrowIfNull(value);
            return this.Insert(value.AsSpan());
        }

        /// <summary>
        /// Insert bytes in the Buffer at position <see cref="Offset"/> without allocating.
        /// </summary>
        public BufferBuilder Insert(ReadOnlySpan<byte> value)
        {
            if (!value.IsEmpty)
            {
                this.EnsureBuffer(value.Length);
                if (this.Offset < this.length)
                    System.Buffer.BlockCopy(this.Buffer, this.Offset, this.Buffer, this.Offset + value.Length, this.length - this.Offset);
                value.CopyTo(this.Buffer.AsSpan(this.Offset));
                this.length += value.Length;
            }
            return this;
        }

        /// <summary>
        /// Insert bytes in the Buffer at position <see cref="Offset"/>
        /// </summary>
        /// <param name="value"></param>
        /// <param name="start"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        public BufferBuilder Insert(byte[] value, int start, int length)
        {
            ArgumentNullException.ThrowIfNull(value);
            return this.Insert(value.AsSpan(start, length));
        }

        /// <summary>
        /// Removes length bytes at current<see cref="Offset"/>
        /// </summary>
        /// <param name="length"> (if length is negative - do insert)</param>
        /// <returns></returns>
        public BufferBuilder Remove(int length)
        {
            if (length > 0)
            {
                int left = this.Left - length;
                if (left < 0) throw new ArgumentOutOfRangeException(nameof(length), "No enough data to remove");
                System.Buffer.BlockCopy(this.Buffer, this.Offset + length, this.Buffer, this.Offset, left);
                this.length -= length;
            }
            return this;
        }

        #region Array Append/Read methods

        /// <summary>
        /// Append array of different type
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="value"></param>
        /// <param name="startIndex"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        public BufferBuilder Append<T>(T[] value) where T : struct => this.Append(value, 0, value.Length);

        /// <summary>
        /// Append array of different type
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="value"></param>
        /// <param name="startIndex"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        public BufferBuilder Append<T>(T[] value, int startIndex, int length) where T : struct => this.Append(value, startIndex, length, typeof(T));

        /// <summary>
        /// Append array as byte
        /// </summary>
        /// <param name="value"></param>
        /// <param name="elementType"></param>
        /// <returns></returns>
        public BufferBuilder Append(Array value, Type? elementType = null)
        {
            ArgumentNullException.ThrowIfNull(value);
            return this.Append(value, 0, value.Length, elementType);
        }

        /// <summary>
        /// Append Array as byte
        /// </summary>
        /// <param name="value"></param>
        /// <param name="startIndex"></param>
        /// <param name="length"></param>
        /// <param name="elementType"></param>
        /// <returns></returns>
        public BufferBuilder Append(Array value, int startIndex, int length, Type? elementType = null)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (length > 0)
            {
                Type actualElementType = elementType ?? value.GetType().GetElementType() ?? throw new ArgumentException("Array element type cannot be resolved.", nameof(value));
                int size = BufferBuilder.SizeOf(actualElementType);
                startIndex *= size;
                length *= size;

                this.EnsureBuffer(length);
                System.Buffer.BlockCopy(value, startIndex, this.Buffer, this.length, length);
                this.length += length;
            }
            return this;
        }

        /// <summary>
        /// read buffer into typed array (no space check)
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="len"></param>
        /// <returns></returns>
        private T[]? ReadArrayUnchecked<T>(int len, int tsize = 0) where T : struct
        {
            if (len >= 0)
            {
                T[] ret = new T[len];
                System.Buffer.BlockCopy(this.Buffer, this.Offset, ret, 0, len *= (tsize > 0) ? tsize : BufferBuilder.SizeOf(typeof(T)));
                this.Offset += len;
                return ret;
            }
            return null;
        }

        /// <summary>
        /// read buffer into typed array 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="len"></param>
        /// <returns></returns>
        public T[]? ReadArray<T>(int len, int tsize = 0) where T : struct
        {
            if (len >= 0)
            {
                T[] ret = new T[len];
                len *= (tsize > 0) ? tsize : BufferBuilder.SizeOf(typeof(T));

                this.CheckSpace(len);
                System.Buffer.BlockCopy(this.Buffer, this.Offset, ret, 0, len);
                this.Offset += len;
                return ret;
            }
            return null;
        }

        /// <summary>
        /// Read unmanaged values from the buffer without using <see cref="System.Buffer.BlockCopy"/>.
        /// </summary>
        public T[]? ReadUnmanagedArray<T>(int len) where T : unmanaged
        {
            if (len < 0)
                return null;

            T[] ret = new T[len];
            Span<byte> destination = MemoryMarshal.AsBytes(ret.AsSpan());
            this.CheckSpace(destination.Length);
            this.Buffer.AsSpan(this.Offset, destination.Length).CopyTo(destination);
            this.Offset += destination.Length;
            return ret;
        }

        /// <summary>
        /// Read a span over the internal buffer and advance <see cref="Offset"/>.
        /// </summary>
        public ReadOnlySpan<byte> ReadSpan(int len)
        {
            if (len < 0)
                throw new ArgumentOutOfRangeException(nameof(len));

            this.CheckSpace(len);
            ReadOnlySpan<byte> ret = this.Buffer.AsSpan(this.Offset, len);
            this.Offset += len;
            return ret;
        }

        /// <summary>
        /// Read buffer into array of type
        /// </summary>
        /// <param name="elType"></param>
        /// <param name="len"></param>
        /// <returns></returns>
        private Array? ReadArrayUnchecked(int len, Type elType)
        {
            if (len >= 0)
            {
                Array ret = Array.CreateInstance(elType, len);
                System.Buffer.BlockCopy(this.Buffer, this.Offset, ret, 0, len *= BufferBuilder.SizeOf(elType));
                this.Offset += len;
                return ret;
            }
            return null;
        }

        /// <summary>
        /// Read buffer into array of type
        /// </summary>
        /// <param name="elType"></param>
        /// <param name="len"></param>
        /// <returns></returns>
        public Array? ReadArray(int len, Type elType)
        {
            if (len >= 0)
            {
                Array ret = Array.CreateInstance(elType, len);
                len *= BufferBuilder.SizeOf(elType);

                this.CheckSpace(len);
                System.Buffer.BlockCopy(this.Buffer, this.Offset, ret, 0, len);
                this.Offset += len;
                return ret;
            }
            return null;
        }

        #endregion

        #region String Append/Read methods

        /// <summary>
        /// Append string value
        /// </summary>
        /// <param name="value"></param>
        /// <param name="lenType"></param>
        /// <param name="encoding"></param>
        /// <returns></returns>
        public BufferBuilder Append(string? value, TypeCode lenType = TypeCode.Int32, Encoding? encoding = null)
            => this.Append(value, 0, value == null ? -1 : value.Length, lenType, encoding);

        private static void ValidateStringSlice(string value, int startIndex, int length)
        {
            ArgumentNullException.ThrowIfNull(value);
            if ((uint)startIndex > (uint)value.Length || (uint)length > (uint)(value.Length - startIndex))
                throw new ArgumentOutOfRangeException(nameof(length));
        }

        private static int GetStringByteCount(string value, int startIndex, int length, Encoding encoding)
        {
            ValidateStringSlice(value, startIndex, length);
            return encoding.GetByteCount(value.AsSpan(startIndex, length));
        }

        private static int GetStringBytes(string value, int startIndex, int length, Encoding encoding, byte[] buffer, int byteIndex)
        {
            ValidateStringSlice(value, startIndex, length);
            return encoding.GetBytes(value.AsSpan(startIndex, length), buffer.AsSpan(byteIndex));
        }


        /// <summary>
        /// Append string
        /// </summary>
        /// <param name="value"></param>
        /// <param name="startIndex"></param>
        /// <param name="length"></param>
        /// <param name="lenType"></param>
        /// <param name="encoding"></param>
        /// <returns></returns>
        public BufferBuilder Append(string? value, int startIndex, int length, TypeCode lenType = TypeCode.Int32, Encoding? encoding = null)
        {
            int byteCount = length;
            Encoding enc = encoding ?? Encoding.UTF8;

            if (length > 0)
            { // not null
                ArgumentNullException.ThrowIfNull(value);
                byteCount = GetStringByteCount(value, startIndex, length, enc);
            }

            switch (lenType)
            {
                case TypeCode.SByte:
                    if (SByte.MaxValue < byteCount)
                        throw new ArgumentException("unsupported string length", "count");
                    this.Append((SByte)byteCount);
                    break;
                case TypeCode.Byte:
                    if (Byte.MaxValue < byteCount || byteCount < 0)
                        throw new ArgumentException("unsupported string length", "count");
                    this.Append((Byte)byteCount);
                    break;
                case TypeCode.Int16:
                    if (Int16.MaxValue < byteCount)
                        throw new ArgumentException("unsupported string length", "count");
                    this.Append((Int16)byteCount);
                    break;
                case TypeCode.UInt16:
                    if (UInt16.MaxValue < byteCount || byteCount < 0)
                        throw new ArgumentException("unsupported string length", "count");
                    this.Append((UInt16)byteCount);
                    break;
                case TypeCode.Int32:
                    this.Append(byteCount);
                    break;
                case TypeCode.UInt32:
                    if (byteCount < 0)
                        throw new ArgumentException("unsupported string length", "count");
                    this.Append((UInt32)byteCount);
                    break;
                case TypeCode.Int64:
                    this.Append((Int64)byteCount);
                    break;
                case TypeCode.UInt64:
                    if (byteCount < 0)
                        throw new ArgumentException("unsupported string length", "count");
                    this.Append((UInt64)byteCount);
                    break;
                case TypeCode.Object:
                    this.AppendVarInt32(byteCount);
                    break;
                case TypeCode.DBNull:
                    if (byteCount < 0)
                        throw new ArgumentException("unsupported string length", "count");
                    this.AppendVarUInt32((uint)byteCount);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(lenType));
            }

            if (byteCount > 0)
            {
                this.EnsureBuffer(byteCount);
                this.length += GetStringBytes(value!, startIndex, length, enc, this.Buffer, this.length);
            }

            return this;
        }

        /// <summary>
        /// Get string length in bytes
        /// </summary>
        /// <param name="value"></param>
        /// <param name="lenType"></param>
        /// <param name="encoding"></param>
        /// <returns></returns>
        public static int GetStringLength(string? value, TypeCode lenType = TypeCode.Int32, Encoding? encoding = null)
            => GetStringLength(value, 0, value == null ? -1 : value.Length, lenType, encoding);

        /// <summary>
        /// Get string length in bytes
        /// </summary>
        /// <param name="value"></param>
        /// <param name="startIndex"></param>
        /// <param name="length"></param>
        /// <param name="lenType"></param>
        /// <param name="encoding"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public static int GetStringLength(string? value, int startIndex, int length, TypeCode lenType = TypeCode.Int32, Encoding? encoding = null)
        {
            int ret = 0;
            int byteCount = length;

            if (length > 0)
            {
                ArgumentNullException.ThrowIfNull(value);
                ret += byteCount = GetStringByteCount(value, startIndex, length, encoding ?? Encoding.UTF8);
            }

            switch (lenType)
            {
                case TypeCode.SByte:
                    if (SByte.MaxValue < byteCount)
                        throw new ArgumentException("unsupported string length", "count");
                    ret += sizeof(SByte);
                    break;
                case TypeCode.Byte:
                    if (Byte.MaxValue < byteCount || byteCount < 0)
                        throw new ArgumentException("unsupported string length", "count");
                    ret += sizeof(Byte);
                    break;
                case TypeCode.Int16:
                    if (Int16.MaxValue < byteCount)
                        throw new ArgumentException("unsupported string length", "count");
                    ret += sizeof(Int16);
                    break;
                case TypeCode.UInt16:
                    if (UInt16.MaxValue < byteCount || byteCount < 0)
                        throw new ArgumentException("unsupported string length", "count");
                    ret += sizeof(UInt16);
                    break;
                case TypeCode.Int32:
                    ret += sizeof(Int32);
                    break;
                case TypeCode.UInt32:
                    if (byteCount < 0)
                        throw new ArgumentException("unsupported string length", "count");
                    ret += sizeof(UInt32);
                    break;
                case TypeCode.Int64:
                    ret += sizeof(Int64);
                    break;
                case TypeCode.UInt64:
                    if (byteCount < 0)
                        throw new ArgumentException("unsupported string length", "count");
                    ret += sizeof(UInt64);
                    break;
                case TypeCode.Object:
                    Int32 zigzag = (byteCount << 1) ^ (byteCount >> (8 * sizeof(Int32) - 1));
                    ret += GetVarUIntLen((UInt32)zigzag);
                    break;
                case TypeCode.DBNull:
                    if (byteCount < 0)
                        throw new ArgumentException("unsupported string length", "count");
                    ret += GetVarUIntLen((UInt32)byteCount);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(lenType));
            }

            return ret;
        }

        private int ReadStringByteCountUnchecked(TypeCode lenType)
        {
            switch (lenType)
            {
                case TypeCode.SByte: return this.ReadSByteUnchecked();
                case TypeCode.Byte: return this.ReadByteUnchecked();
                case TypeCode.Int16: return this.ReadInt16Unchecked();
                case TypeCode.UInt16: return this.ReadUInt16Unchecked();
                case TypeCode.Int32: return this.ReadInt32Unchecked();
                case TypeCode.UInt32: return (Int32)this.ReadUInt32Unchecked();
                case TypeCode.Int64: return (Int32)this.ReadInt64Unchecked();
                case TypeCode.UInt64: return (Int32)this.ReadUInt64Unchecked();
                case TypeCode.Object: return this.ReadVarInt32Unchecked();
                case TypeCode.DBNull: return (Int32)this.ReadVarUInt32Unchecked();
                default:
                    throw new ArgumentOutOfRangeException(nameof(lenType));
            }
        }

        public int ReadStringByteCount(TypeCode lenType)
        {
            switch (lenType)
            {
                case TypeCode.SByte: return this.ReadSByte();
                case TypeCode.Byte: return this.ReadByte();
                case TypeCode.Int16: return this.ReadInt16();
                case TypeCode.UInt16: return this.ReadUInt16();
                case TypeCode.Int32: return this.ReadInt32();
                case TypeCode.UInt32: return (Int32)this.ReadUInt32();
                case TypeCode.Int64: return (Int32)this.ReadInt64();
                case TypeCode.UInt64: return (Int32)this.ReadUInt64();
                case TypeCode.Object: return this.ReadVarInt32();
                case TypeCode.DBNull: return (Int32)this.ReadVarUInt32();
                default:
                    throw new ArgumentOutOfRangeException(nameof(lenType));
            }
        }

        /// <summary>
        /// Read next Value as String 
        /// </summary>
        /// <returns></returns>
        private string? ReadStringUnchecked(TypeCode lenType = TypeCode.Int32, Encoding? encoding = null)
        {
            int length = this.ReadStringByteCountUnchecked(lenType);

            if (length < 0) return null; // null string
            if (length == 0) return string.Empty; // string with zero length

            int offset = this.Offset;
            this.Offset += length;
            return (encoding ?? Encoding.UTF8).GetString(this.Buffer, offset, length);
        }

        /// <summary>
        /// Read next Value as String
        /// </summary>
        /// <returns></returns>
        public string? ReadString(TypeCode lenType = TypeCode.Int32, Encoding? encoding = null)
        {
            int length = this.ReadStringByteCount(lenType);

            if (length < 0) return null; // null string
            if (length == 0) return string.Empty; // string with zero length

            this.CheckSpace(length);
            int offset = this.Offset;
            this.Offset += length;
            return (encoding ?? Encoding.UTF8).GetString(this.Buffer, offset, length);
        }

        #endregion

        #region VarInt16/32/64 + zig-zag encoding methods

        /// <summary>
        /// Maximum size of VarUInt16 value
        /// </summary>
        public const int MaxVarUInt16Size = 3;

        /// <summary>
        /// Append UInt16 as variable length int:
        /// 1b: 0x0..7F (..127)
        /// 2b: 0x80..3FFF (..16383)
        /// 3b: 0x4000..FFFF (..65535)
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private BufferBuilder AppendVarUInt16Unchecked(UInt16 value)
        {
            /* !!! improved by once EnsureBuffer
            while (value >= 0x80) { this.Append((byte)(0x80 | value)); value >>= 7; }
            return this.Append((byte)value);*/

            if (value >= 0x80) // must shift
                do this.Buffer[this.length++] = (byte)(0x80 | value);
                while ((value >>= 7) >= 0x80);
            this.Buffer[this.length++] = (byte)value; // this.Append((byte)value);
            return this;
        }

        /// <summary>
        /// Append UInt16 as variable length int:
        /// 1b: 0x0..7F (..127)
        /// 2b: 0x80..3FFF (..16383)
        /// 3b: 0x4000..FFFF (..65535)
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public BufferBuilder AppendVarUInt16(UInt16 value)
        {
            this.EnsureBuffer(value < 0x80 ? 1 : value < 0x4000 ? 2 : 3);
            return this.AppendVarUInt16Unchecked(value);
        }

        /// <summary>
        /// Read next Value as variable length UInt16
        /// </summary>
        /// <returns></returns>
        private UInt16 ReadVarUInt16Unchecked()
        {
            UInt16 b, ret = 0;
            int shift = 0;
            while (((b = this.ReadByteUnchecked()) & 0x80) != 0)
            {
                b &= 0x7F;
                b <<= shift;
                ret |= b;
                if ((shift += 7) >= 8 * sizeof(UInt16)) throw new OverflowException();
            }
            if (b > (UInt16.MaxValue >> shift)) throw new OverflowException();
            b <<= shift;
            return ret |= b;
        }

        /// <summary>
        /// Read next Value as variable length UInt16
        /// </summary>
        /// <returns></returns>
        public UInt16 ReadVarUInt16()
        {
            UInt16 b, ret = 0;
            int shift = 0;
            while (((b = this.ReadByte()) & 0x80) != 0)
            {
                b &= 0x7F;
                b <<= shift;
                ret |= b;
                if ((shift += 7) >= 8 * sizeof(UInt16)) throw new OverflowException();
            }
            if (b > (UInt16.MaxValue >> shift)) throw new OverflowException();
            b <<= shift;
            return ret |= b;
        }

        /// <summary>
        /// Maximum size of VarUInt32 value
        /// </summary>
        public const int MaxVarUInt32Size = 5;

        /// <summary>
        /// Append UInt32 as variable length int:
        /// 1b: 0x0..7F (..127)
        /// 2b: 0x80..3FFF (..16383)
        /// 3b: 0x4000..1FFFFF (..2097151)
        /// 4b: 0x200000..FFFFFFF (..268435455)
        /// 5b: 0x10000000..FFFFFFFF (..4294967295)
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private BufferBuilder AppendVarUInt32Unchecked(UInt32 value)
        {
            if (value >= 0x80)  // must shift
                do this.Buffer[this.length++] = (byte)(0x80 | value);
                while ((value >>= 7) >= 0x80);
            this.Buffer[this.length++] = (byte)value; // this.Append((byte)value);
            return this;
        }

        /// <summary>
        /// Append UInt32 as variable length int:
        /// 1b: 0x0..7F (..127)
        /// 2b: 0x80..3FFF (..16383)
        /// 3b: 0x4000..1FFFFF (..2097151)
        /// 4b: 0x200000..FFFFFFF (..268435455)
        /// 5b: 0x10000000..FFFFFFFF (..4294967295)
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public BufferBuilder AppendVarUInt32(UInt32 value)
        {
            this.EnsureBuffer(
                value < 0x80 ? 1 :
                (value < 0x4000 ? 2 :
                (value < 0x200000 ? 3 :
                (value < 0x10000000 ? 4 : 5))));
            return this.AppendVarUInt32Unchecked(value);
        }

        /// <summary>
        /// Read next Value as variable length UInt32 0..268435455(0xFFFFFFF) - 4 bytes)
        /// </summary>
        /// <returns></returns>
        private UInt32 ReadVarUInt32Unchecked()
        {
            UInt32 b, ret = 0;
            int shift = 0;
            while (((b = this.ReadByteUnchecked()) & 0x80) != 0)
            {
                ret |= (b & 0x7F) << shift;
                if ((shift += 7) >= 8 * sizeof(UInt32)) throw new OverflowException();
            }
            if (b > (UInt32.MaxValue >> shift)) throw new OverflowException();
            return ret | (b << shift);
        }

        /// <summary>
        /// Read next Value as variable length UInt32 0..268435455(0xFFFFFFF) - 4 bytes)
        /// </summary>
        /// <returns></returns>
        public UInt32 ReadVarUInt32()
        {
            UInt32 b, ret = 0;
            int shift = 0;
            while (((b = this.ReadByte()) & 0x80) != 0)
            {
                ret |= (b & 0x7F) << shift;
                if ((shift += 7) >= 8 * sizeof(UInt32)) throw new OverflowException();
            }
            if (b > (UInt32.MaxValue >> shift)) throw new OverflowException();
            return ret | (b << shift);
        }

        /// <summary>
        /// Append ZigZag encoded int value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private BufferBuilder AppendVarInt32Unchecked(Int32 value)
        {
            Int32 zigzag = (value << 1) ^ (value >> (8 * sizeof(Int32) - 1));
            return this.AppendVarUInt32Unchecked(unchecked((UInt32)zigzag));
        }

        /// <summary>
        /// Append ZigZag encoded int value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public BufferBuilder AppendVarInt32(Int32 value)
        {
            Int32 zigzag = (value << 1) ^ (value >> (8 * sizeof(Int32) - 1));
            return this.AppendVarUInt32(unchecked((UInt32)zigzag));
        }

        /// <summary>
        /// Read zigzag int value
        /// </summary>
        /// <returns></returns>
        private Int32 ReadVarInt32Unchecked()
        {
            UInt32 zigzag = this.ReadVarUInt32Unchecked();
            return unchecked((Int32)(zigzag >> 1) ^ -(Int32)(zigzag & 1));
        }

        /// <summary>
        /// Read zigzag int value
        /// </summary>
        /// <returns></returns>
        public Int32 ReadVarInt32()
        {
            UInt32 zigzag = this.ReadVarUInt32();
            return unchecked((Int32)(zigzag >> 1) ^ -(Int32)(zigzag & 1));
        }

        /// <summary>
        /// Get bytes length if write value as VarUInt
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public static int GetVarUIntLen(UInt64 value) =>
            value < 0x80 ? 1 :
            (value < 0x4000 ? 2 :
            (value < 0x200000 ? 3 :
            (value < 0x10000000 ? 4 :
            (value < 0x800000000 ? 5 :
            (value < 0x40000000000 ? 6 :
            (value < 0x2000000000000 ? 7 :
            (value < 0x100000000000000 ? 8 :
            (value < 0x8000000000000000 ? 9 : 10))))))));

        /// <summary>
        /// Maximum size of VarUInt32 value
        /// </summary>
        public const int MaxVarUInt64Size = 10;

        /// <summary>
        /// Append UInt64 as variable length int:
        /// 1b: 0x0..7F (..127)
        /// 2b: 0x80..3FFF (..16383)
        /// 3b: 0x4000..1FFFFF (..2097151)
        /// 4b: 0x200000..FFFFFFF (..268435455)
        /// 5b: 0x10000000..7FFFFFFFF (..34359738367)
        /// 6b: 0x800000000..3FFFFFFFFFF (..4398046511103)
        /// 7b: 0x40000000000..1FFFFFFFFFFFF (..562949953421311)
        /// 8b: 0x2000000000000..FFFFFFFFFFFFFF (..72057594037927935)
        /// 9b: 0x100000000000000..7FFFFFFFFFFFFFFF (..9223372036854775807)
        /// 10b: 0x8000000000000000..FFFFFFFFFFFFFFFF (..18446744073709551615)
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private BufferBuilder AppendVarUInt64Unchecked(UInt64 value)
        {
            if (value >= 0x80)
                do this.Buffer[this.length++] = (byte)(0x80 | value);
                while ((value >>= 7) >= 0x80);
            this.Buffer[this.length++] = (byte)value; // this.Append((byte)value);
            return this;
        }

        /// <summary>
        /// Append UInt64 as variable length int:
        /// 1b: 0x0..7F (..127)
        /// 2b: 0x80..3FFF (..16383)
        /// 3b: 0x4000..1FFFFF (..2097151)
        /// 4b: 0x200000..FFFFFFF (..268435455)
        /// 5b: 0x10000000..7FFFFFFFF (..34359738367)
        /// 6b: 0x800000000..3FFFFFFFFFF (..4398046511103)
        /// 7b: 0x40000000000..1FFFFFFFFFFFF (..562949953421311)
        /// 8b: 0x2000000000000..FFFFFFFFFFFFFF (..72057594037927935)
        /// 9b: 0x100000000000000..7FFFFFFFFFFFFFFF (..9223372036854775807)
        /// 10b: 0x8000000000000000..FFFFFFFFFFFFFFFF (..18446744073709551615)
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public BufferBuilder AppendVarUInt64(UInt64 value)
        {
            this.EnsureBuffer(
                value < 0x80 ? 1 :
                (value < 0x4000 ? 2 :
                (value < 0x200000 ? 3 :
                (value < 0x10000000 ? 4 :
                (value < 0x800000000 ? 5 :
                (value < 0x40000000000 ? 6 :
                (value < 0x2000000000000 ? 7 :
                (value < 0x100000000000000 ? 8 :
                (value < 0x8000000000000000 ? 9 : 10)))))))));
            return this.AppendVarUInt64Unchecked(value);
        }

        /// <summary>
        /// Read next Value as variable length
        /// </summary>
        /// <returns></returns>
        private UInt64 ReadVarUInt64Unchecked()
        {
            UInt64 b, ret = 0;
            int shift = 0;
            while (((b = this.ReadByteUnchecked()) & 0x80) != 0)
            {
                ret |= (b & 0x7F) << shift;
                if ((shift += 7) >= 8 * sizeof(UInt64)) throw new OverflowException();
            }
            if (b > (UInt64.MaxValue >> shift)) throw new OverflowException();
            return ret | (b << shift);
        }

        /// <summary>
        /// Read next Value as variable length
        /// </summary>
        /// <returns></returns>
        public UInt64 ReadVarUInt64()
        {
            UInt64 b, ret = 0;
            int shift = 0;
            while (((b = this.ReadByte()) & 0x80) != 0)
            {
                ret |= (b & 0x7F) << shift;
                if ((shift += 7) >= 8 * sizeof(UInt64)) throw new OverflowException();
            }
            if (b > (UInt64.MaxValue >> shift)) throw new OverflowException();
            return ret | (b << shift);
        }

        /// <summary>
        /// Append ZigZag encoded int value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private BufferBuilder AppendVarInt64Unchecked(Int64 value)
        {
            Int64 zigzag = (value << 1) ^ (value >> (8 * sizeof(Int64) - 1));
            return this.AppendVarUInt64Unchecked(unchecked((UInt64)zigzag));
        }

        /// <summary>
        /// Append ZigZag encoded int value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public BufferBuilder AppendVarInt64(Int64 value)
        {
            Int64 zigzag = (value << 1) ^ (value >> (8 * sizeof(Int64) - 1));
            return this.AppendVarUInt64(unchecked((UInt64)zigzag));
        }

        /// <summary>
        /// Read zigzag int value
        /// </summary>
        /// <returns></returns>
        private Int64 ReadVarInt64Unchecked()
        {
            UInt64 zigzag = this.ReadVarUInt64Unchecked();
            //return unchecked((zigzag & 1) == 0 ? (Int64)(zigzag >> 1) : ((Int64)(zigzag >> 1) ^ -1));
            return unchecked((Int64)(zigzag >> 1) ^ -(Int64)(zigzag & 1));
        }

        /// <summary>
        /// Read zigzag int value
        /// </summary>
        /// <returns></returns>
        public Int64 ReadVarInt64()
        {
            UInt64 zigzag = this.ReadVarUInt64();
            //return unchecked((zigzag & 1) == 0 ? (Int64)(zigzag >> 1) : ((Int64)(zigzag >> 1) ^ -1));
            return unchecked((Int64)(zigzag >> 1) ^ -(Int64)(zigzag & 1));
        }

        #endregion

        /// <summary>
        /// Shift <see cref="Buffer"/> to the start of the buffer start - make <see cref="Offset"/> = 0
        /// <see cref="Length"/> is decreased by <see cref="Offset"/>.
        /// </summary>
        /// <returns>new <see cref="Length"/>( == <see cref="Left"/>)</returns>
        public void ShiftToStart()
        {
            int offset = this.Offset;
            if ((uint)offset > (uint)this.length)
                throw new ArgumentOutOfRangeException(nameof(this.Offset));

            if (0 < offset)
            {
                int left = this.length - offset;
                if (left > 0)
                    System.Buffer.BlockCopy(this.Buffer, offset, this.Buffer, 0, left);
                this.length = left;
            }
            this.Offset = 0;
        }

        /// <summary>
        /// Reallocate <see cref="Buffer"/> to all bytes left: between <see cref="Offset"/>..<see cref="Length"/>
        /// <see cref="Length"/> is set to <see cref="Left"/>;
        /// <see cref="Offset"/> is set to 0;
        /// </summary>
        /// <returns>new <see cref="Length"/>( == <see cref="Left"/>)</returns>
        public int TrimToLeft()
        {
            int left = this.Left;
            if (left == 0) this.Buffer = EmptyBuffer;
            else if (this.Offset != 0 || this.Buffer.Length != left)
            {
                byte[] temp = new byte[left];
                System.Buffer.BlockCopy(this.Buffer, this.Offset, temp, 0, left);
                this.Buffer = temp;
            }
            this.Offset = 0;
            return this.length = left;
        }

        /// <summary>
        /// Get data as array
        /// </summary>
        /// <returns></returns>
        public byte[] ToArray()
        {
            if (this.length == 0) return EmptyBuffer;
            byte[] ret = new byte[this.length];
            this.WrittenSpan.CopyTo(ret);
            return ret;
        }

        public override string ToString() => string.Format("[{0}]+{1}: {2}", this.length, this.Offset,
                this.Buffer == null ? null : (PersistenceBinary.ToHexString(this.Buffer, 0, Math.Min(this.length, 16)) + (this.length > 16 ? "..." : "")));
    }
}

