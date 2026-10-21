# Release notes

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
