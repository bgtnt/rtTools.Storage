using System;
using System.Collections.Generic;

namespace RtTools.Logger
{
    /// <summary>
    /// Default DataRecord Implementation
    /// Data is stored as byte array
    /// </summary>
    /// <typeparam name="TKey">Record Key</typeparam>
    public sealed class Record<TKey> : DataRecord<TKey> where TKey : Key, new()
    {
        /// <summary>
        /// get/set Record key
        /// </summary>
        public override TKey Key { get; set; } = new();

        /// <summary>
        /// Get/set record data
        /// </summary>
        public override byte[] Data { get; set; } = [];
    }
}
