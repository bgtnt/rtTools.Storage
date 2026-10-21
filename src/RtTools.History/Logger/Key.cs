using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace RtTools.Logger
{
    /// <summary>
    /// Log record Key abstraction
    /// </summary>
    public abstract class Key : IComparable<Key>
    {
        #region properties

        /// <summary>
        /// Key Size in byte
        /// Should be fixed
        /// </summary>
        public abstract int Size { get; }

        /// <summary>
        /// Max possible value
        /// </summary>
        public abstract Key MaxValue { get; }

        /// <summary>
        /// Min possible value
        /// </summary>
        public abstract Key MinValue { get; }

        #endregion

        #region methods
        /// <summary>
        /// Merge 2 keys as start, end
        /// </summary>
        /// <param name="key"></param>
        /// <param name="startOrEnd"></param>
        public abstract void Merge(Key key, bool startOrEnd);

        /// <summary>
        /// Read key from byte array
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        internal protected abstract int Read(byte[] buffer, int offset);

        /// <summary>
        /// Read key from stream
        /// </summary>
        /// <param name="stream"></param>
        /// <returns></returns>
        internal protected bool Read(Stream stream)
        {
            int len = this.Size;
            byte[] buffer = new byte[len];
            try
            {
                stream.ReadExactly(buffer, 0, len);
                this.Read(buffer, 0);
                return true;
            }
            catch (EndOfStreamException)
            {
                return false;
            }
        }

        /// <summary>
        /// Write Key to byte array
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        internal protected abstract int Write(byte[] buffer, int offset);

        /// <summary>
        /// Write key to stream
        /// </summary>
        /// <param name="stream"></param>
        internal protected void Write(Stream stream)
        {
            int len = this.Size;
            byte[] buffer = new byte[len];
            this.Write(buffer, 0);
            stream.Write(buffer, 0, len);
        }

        /// <summary>
        /// Check if key is contained in range of keys
        /// </summary>
        /// <param name="start"></param>
        /// <param name="end"></param>
        /// <returns></returns>
        public bool InRange(Key start, Key end) => 0 <= this.CompareTo(start) && this.CompareTo(end) <= 0;

        /// <summary>
        /// Compare 2 records
        /// </summary>
        /// <param name="other"></param>
        /// <returns></returns>
        public abstract int CompareTo(Key? other);

        #endregion

        #region Base types

        /// <summary>
        /// Predefined Int32 Key <see cref="Key&lt;T&gt;"/>
        /// </summary>
        [System.Runtime.InteropServices.ComVisible(false)]
        public sealed class KeyInt32 : Key<Int32>
        {
            /// <summary>
            /// overridden
            /// </summary>
            public override Key MaxValue => new KeyInt32() { Value = Int32.MaxValue };
            /// <summary>
            /// overridden
            /// </summary>
            public override Key MinValue => new KeyInt32() { Value = Int32.MinValue };

            public static implicit operator KeyInt32(Int32 value) => new() { Value = value };
        }
        /// <summary>
        /// Predefined Int64 Key <see cref="Key&lt;T&gt;"/>
        /// </summary>
        [System.Runtime.InteropServices.ComVisible(false)]
        public sealed class KeyInt64 : Key<Int64>
        {
            /// <summary>
            /// overridden
            /// </summary>
            public override Key MaxValue => new KeyInt64() { Value = Int64.MaxValue };
            /// <summary>
            /// overridden
            /// </summary>
            public override Key MinValue => new KeyInt64() { Value = Int64.MinValue };

            public static implicit operator KeyInt64(Int64 value) => new() { Value = value };
        }
        /// <summary>
        /// Predefined Single Key <see cref="Key&lt;T&gt;"/>
        /// </summary>
        [System.Runtime.InteropServices.ComVisible(false)]
        public sealed class KeySingle : Key<Single>
        {
            /// <summary>
            /// overridden
            /// </summary>
            public override Key MaxValue => new KeySingle() { Value = Single.MaxValue };
            /// <summary>
            /// overridden
            /// </summary>
            public override Key MinValue => new KeySingle() { Value = Single.MinValue };

            public static implicit operator KeySingle(Single value) => new() { Value = value };
        }
        /// <summary>
        /// Predefined Double Key <see cref="Key&lt;T&gt;"/>
        /// </summary>
        [System.Runtime.InteropServices.ComVisible(false)]
        public sealed class KeyDouble : Key<Double>
        {
            /// <summary>
            /// overridden
            /// </summary>
            public override Key MaxValue => new KeyDouble() { Value = Double.MaxValue };
            /// <summary>
            /// overridden
            /// </summary>
            public override Key MinValue => new KeyDouble() { Value = Double.MinValue };

            public static implicit operator KeyDouble(Double value) => new() { Value = value };
        }

        /// <summary>
        /// Special implementation (does not support SizeOf)
        /// </summary>
        [System.Runtime.InteropServices.ComVisible(false)]
        public sealed class KeyDateTime : Key
        {
            /// <summary>
            /// Key Value
            /// </summary>
            public DateTime Value { get; set; }
            /// <summary>
            /// Key size in bytes
            /// </summary>
            public override int Size => sizeof(long);
            /// <summary>
            /// Max value
            /// </summary>
            public override Key MaxValue => new KeyDateTime() { Value = DateTime.MaxValue };
            /// <summary>
            /// Min value
            /// </summary>
            public override Key MinValue => new KeyDateTime() { Value = DateTime.MinValue };
            /// <summary>
            /// Merge with another one
            /// </summary>
            /// <param name="key"></param>
            /// <param name="startOrEnd"></param>
            public override void Merge(Key key, bool startOrEnd)
            {
                KeyDateTime other = (KeyDateTime)key;
                if (startOrEnd)
                {
                    if (this.Value > other.Value) this.Value = other.Value;
                }
                else
                {
                    if (this.Value < other.Value) this.Value = other.Value;
                }
            }
            /// <summary>
            /// Read from buffer
            /// </summary>
            /// <param name="buffer"></param>
            /// <param name="offset"></param>
            /// <returns></returns>
            protected internal override int Read(byte[] buffer, int offset)
            {
                this.Value = DateTime.FromBinary(BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset)));
                return offset + sizeof(long);
            }
            /// <summary>
            /// Write to buffer
            /// </summary>
            /// <param name="buffer"></param>
            /// <param name="offset"></param>
            /// <returns></returns>
            protected internal override int Write(byte[] buffer, int offset)
            {
                BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset), this.Value.ToBinary());
                return offset + sizeof(long);
            }
            /// <summary>
            /// Compare to another
            /// </summary>
            /// <param name="other"></param>
            /// <returns></returns>
            public override int CompareTo(Key? other) => other is null
                ? 1
                : this.Value.CompareTo(((KeyDateTime)other).Value);
            /// <summary>
            /// overridden
            /// </summary>
            /// <returns></returns>
            public override string ToString() => this.Value.ToString();

            public static implicit operator KeyDateTime(DateTime value) => new() { Value = value };
        }

        #endregion
    }
}
