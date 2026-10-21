namespace RtTools.Logger
{
    /// <summary>
    /// Supplies an optional bucket hint for the existing log range index.
    /// Query semantics and the persisted index format always use the full key.
    /// </summary>
    public interface ILogKeyProjection<TKey> where TKey : Key, new()
    {
        /// <summary>
        /// Returns a stable bucket identifier without modifying the key. Identifiers
        /// must be nondecreasing in full-key order. Newly written sorted ranges are
        /// split at bucket boundaries in addition to their ordinary size limit.
        /// </summary>
        /// <remarks>
        /// The hint does not create a separate index file. If it throws or violates
        /// bucket ordering, that Log instance stops using it and writes ordinary
        /// ranges instead. Existing ranges are not rewritten when a hint changes.
        /// </remarks>
        uint GetBucketId(TKey key);

        /// <summary>
        /// Retained for API compatibility. The primary range index does not call
        /// this method; searches compare complete keys through their comparer.
        /// </summary>
        long GetOrderValue(TKey key);
    }
}
