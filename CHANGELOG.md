# Release notes

## 0.1.0-preview.2 — 2026-10-21

Completes the original object-persistence, persistent FIFO and dictionary-snapshot
families in the same BCL-only .NET 10 package.

- StreamPersistence, StreamPersistence<T>, IPersistent and IPersistentBinary:
  explicit entity persistence, store-local record IDs, load callbacks and binary
  factories, with prewrite validation and constructor resource cleanup.
- BufferedQueuePersistent<T> with paired codecs, a bounded head cache and FIFO
  refill after space reuse; LimitedList<T> is its nonpersistent circular helper.
- DictionaryLogger<TKey,TValue> and SerializableKeyValuePair<TKey,TValue>:
  independently encoded current state and explicit shallow history snapshots;
  timestamp rejection is reported and both resources are cleaned up on failure.
- A complete public type-family overview in README, including DataStorage and
  synchronization, serializers, collection helpers, keys, records and readers.
- New package-only contracts for all three additions; existing State/History
  suites remain part of verification.

This is still a preview, with no API/file-format stability or transactional,
crash-recovery or power-loss durability promise.

## 0.1.0-preview.1 — 2026-10-21

First public preview of rtTools.Storage for .NET 10, distributed as source and
a NuGet package through GitHub Releases. No external runtime packages.

- Resident dictionary/list state, explicit payload codecs and reusable DataStorage.
- Indexed chronological TimeLog and fixed-key Log history, including explicit
  publication, payload overflow/compression, duplicate keys and opt-in tombstones.
- TimeLog query upper bounds normalize to UTC.
- Eligible default-serializer null TimeLog records are rejected before persistence.
- TimeLog explicit Flush and Dispose report storage failures; automatic failures
  are observable through LastAutoFlushException and safe Trace reporting.
- Constructor-failure cleanup for TimeLog and its reader; safe Log timer diagnostics.
- Package-only verification and runnable State/History examples.

This preview has no stable API/file-format compatibility promise and no
transactional, crash-recovery or power-loss durability guarantee. Historical
files containing default-serializer null records are not repaired automatically.
