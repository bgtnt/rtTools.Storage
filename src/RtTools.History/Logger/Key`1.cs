using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RtTools.Logger
{
    /// <summary>
    /// Record Key to be used in Log <see cref="Log&lt;TRecord, TKey&gt;"/>
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public abstract class Key<T> : Key where T : unmanaged, IComparable<T>
    {
        private static readonly int s_size = Unsafe.SizeOf<T>();

        /// <summary>
        /// Key size /bytes/
        /// </summary>
        public override int Size => s_size;

        /// <summary>
        /// Key Value
        /// </summary>
        public T Value { get; set; }

        /// <summary>
        /// Merge key with another one
        /// </summary>
        /// <param name="key"></param>
        /// <param name="startOrEnd"></param>
        public override void Merge(Key key, bool startOrEnd)
        {
            Key<T> other = (Key<T>)key;
            if (startOrEnd)
            {
                if (this.Value.CompareTo(other.Value) > 0)
                    this.Value = other.Value;
            }
            else
            {
                if (this.Value.CompareTo(other.Value) < 0)
                    this.Value = other.Value;
            }
        }

        /// <summary>
        /// Read key from mem buffer
        /// </summary>
        /// <param name="buffer">buffer to read</param>
        /// <param name="offset">read offset</param>
        /// <returns>end offset</returns>
        internal protected override int Read(byte[] buffer, int offset)
        {
            this.Value = MemoryMarshal.Read<T>(buffer.AsSpan(offset, s_size));
            return offset + s_size;
        }

        /// <summary>
        /// Write key to mem buffer
        /// </summary>
        /// <param name="buffer">buffer to write</param>
        /// <param name="offset">buffer offset</param>
        /// <returns>end offset</returns>
        internal protected override int Write(byte[] buffer, int offset)
        {
            T value = this.Value;
            MemoryMarshal.Write(buffer.AsSpan(offset, s_size), in value);
            return offset + s_size;
        }

        /// <summary>
        /// COmpare key to other
        /// </summary>
        /// <param name="other"></param>
        /// <returns></returns>
        public override int CompareTo(Key? other) => other is null
            ? 1
            : this.Value.CompareTo(((Key<T>)other).Value);

        /// <summary>
        /// overridden
        /// </summary>
        /// <returns></returns>
        public override string ToString() => this.Value.ToString() ?? string.Empty;

        public static implicit operator T(Key<T> key) => key.Value;
    }
}

