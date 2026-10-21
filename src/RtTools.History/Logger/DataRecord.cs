using System;
using System.Collections.Generic;

namespace RtTools.Logger
{
    /// <summary>
    /// Data Record abstraction
    /// </summary>
    /// <typeparam name="TKey"></typeparam>
    public abstract class DataRecord<TKey> : IComparable<DataRecord<TKey>>, IComparable<TKey> where TKey : Key, new()
    {
        /// <summary>
        /// Get/Set record Key
        /// </summary>
        public abstract TKey Key { get; set; }
        /// <summary>
        /// Get Set Record Data
        /// </summary>
        public abstract byte[] Data { get; set; }

        /// <summary>
        /// Compare record to other by key
        /// </summary>
        /// <param name="other"></param>
        /// <returns></returns>
        public int CompareTo(DataRecord<TKey>? other) => other is null ? 1 : this.Key.CompareTo(other.Key);

        /// <summary>
        /// Compare record to Key
        /// </summary>
        /// <param name="other"></param>
        /// <returns></returns>
        public int CompareTo(TKey? other) => this.Key.CompareTo(other);

        /// <summary>
        /// overridden
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            string ret = this.Key.ToString() ?? string.Empty;
            byte[] data = this.Data;
            if (data != null) 
                return ret + ": " + BitConverter.ToString(data);
            return ret;
        }
    }
}
