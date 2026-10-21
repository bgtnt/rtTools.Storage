using System;
using System.Collections.Generic;

namespace RtTools.Logger
{
    /// <summary>
    /// Record storage type options
    /// </summary>
    [Flags]
    public enum StoreType : byte
    {
        /// <summary>
        /// Default Option
        /// </summary>
        None = 0,

        /// <summary>
        /// Record is deleted
        /// </summary>
        Deleted = 0x01,

        /// <summary>
        /// Compressed record, should deflate
        /// </summary>
        Compressed = 0x02,

        /// <summary>
        /// Data Record has extension
        /// </summary>
        Extended = 0x80,
    }
}
