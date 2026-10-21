# Release notes

## 0.1.0-preview.5 — 2026-10-21

- Queue and both StreamPersistence wrappers reject source-I/O callback Dispose
  before marking themselves disposed. A caught rejection leaves the owner usable
  and later normal disposal closes once; cleanup after a source fault stays allowed.
- Direct DataStorage and SynchronizedDataStorage construction honors closing
  policies even when an error notification throws. Secondary application errors
  preserve the opening error; actual source failures retain first-source precedence.
- Package-only regressions cover wrapper reentry, faulted cleanup, distinct
  notification errors, close policies and resource release. Public signatures,
  record layouts and runtime dependencies are unchanged.

## 0.1.0-preview.4 — 2026-10-21

- DataStorage exposes its first StorageException and IsFaulted. A source-stream
  failure or failure after a possible core mutation makes the instance unusable
  for further storage operations; disposal and advisory metadata remain available.
- SynchronizedDataStorage exposes LastAutoFlushException and forwards storage
  fault state. Automatic failures are contained, pending changes remain pending,
  and an already completed mutation still returns its result before fail-stop.
- Pending-update accounting and timer shutdown share the storage gate; rejected
  calls without completed changes do not trigger a flush that could mask their error.
- Mutation entry guards distinguish application codec/callback exceptions from
  storage failures. Dictionary snapshots reject a faulted current-state store.
- New package-only synthetic failure regressions cover automatic/explicit flush,
  partial writes, truncation, post-mutation failures, reentry and disposal.

Record layouts and runtime dependencies are unchanged. Faulted owners must be
disposed; reopening and validating files is a separate action, with no rollback,
automatic repair or crash/power-loss durability guarantee.

## 0.1.0-preview.3 — 2026-10-21

Corrects three storage/error-path defects without changing public APIs, record
layouts or runtime dependencies.

- TimeLogClient propagates decoder, action and predicate EndOfStreamException
  failures. Only physical sequential reads retain the existing EOF handling;
  partial-main-tail and header-count behavior are documented separately.
- DictionaryPersistent/ListPersistent validate paired codecs before opening a
  filename, preserving existing bytes even with FileMode.Create. Failed
  initialization attempts to release accepted owned streams and preserves the primary error.
- DataStorage validates record header/payload bounds and integer lengths before
  allocation, callbacks or record acceptance, including opens without a scanner.
- Package-only regression suites join all existing checks. A safe before/after
  harness demonstrates the defects against preview.2 and the corrected behavior.

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
