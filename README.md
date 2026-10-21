# rtTools.Storage

Embedded .NET storage for application state and indexed history. One BCL-only
package targets .NET 10 with no external runtime dependencies.

First preview: `0.1.0-preview.1`. APIs and file formats may change before a stable
release. Namespaces remain `RtTools` and `RtTools.Logger`.

| Need | Type | Model |
| --- | --- | --- |
| Resident application state | `DictionaryPersistent<TKey,TValue>`, `ListPersistent<T>` | Complete collection in RAM; explicit mutations persist selected fields. |
| Chronological history | `TimeLog<TRecord>` | Strictly increasing timestamps; inclusive `[from,to]` indexed queries. |
| History indexed by fixed-size keys | `Log<TRecord,TKey>` | Buffered appends, publication, duplicate keys; half-open `[from,to)` queries. |

## Install

Download `rtTools.Storage.0.1.0-preview.1.nupkg` from the
[GitHub release](https://github.com/bgtnt/rtTools.Storage/releases/tag/v0.1.0-preview.1)
into a local `packages` directory, then run:

```sh
dotnet add package rtTools.Storage --version 0.1.0-preview.1 --source ./packages
```

Packages are distributed through GitHub Releases. This release does not require
NuGet.org or a private package source.

## Persist application state

```csharp
using RtTools;
using ResidentState;

using var state = new DictionaryPersistent<string, StateRecord>(
    "state.bin", reader: RecordCodec.Read, writer: RecordCodec.Write);
state["worker-1"] = new StateRecord { Name = "Worker", Revision = 1 };
state.Storage.Flush();
```

The runnable [sample](samples/ResidentState/Program.cs) and its
[versioned codec](samples/ResidentState/RecordCodec.cs) demonstrate write, close,
reopen and read. The codec selects persisted fields and omits transient state.
Property mutation alone is not saved: assign the changed record through the
collection setter again. `Storage.Flush()` flushes bytes already written.

Updates may overwrite or relocate records. DataStorage tracks free regions,
reuses suitable holes and truncates sufficiently large unused tails. It does not
rewrite the entire collection on every mutation. List ordering uses finite
floating labels; repeated insertion into a tight gap can exhaust them and throw.

## Write and query history

```csharp
using System.Text;
using RtTools.Logger;

DateTime at = DateTime.UtcNow;
using var history = new TimeLog<string>("events",
    reader: Encoding.UTF8.GetString, writer: Encoding.UTF8.GetBytes);
if (!history.Append(at, "started"))
    throw new InvalidOperationException("Timestamp was rejected.");
history.Flush();
using var client = history.CreateClient();
var rows = client.FindAll(at, at); // Both bounds are inclusive.
```

TimeLog converts both query bounds to UTC. Local and Unspecified values follow
.NET's local-time convention; prefer UTC. Append returns false when a timestamp
is not newer than the last accepted one. Optional `shiftEquals` tolerance can
move a close timestamp to the next tick. The overload without a timestamp uses
append time. With the default serializer an eligible null throws
`ArgumentNullException`; explicit codecs can encode null if their reader returns
a non-null record.

Log accepts out-of-order keys and duplicates. Append buffers; Publish, its timer,
Flush or disposal publishes pending records. Queries see published records.
FindAll returns key-sorted results; ForEach does not promise globally sorted
traversal across runs. Keys need consistent fixed binary size, ordering and
serialization. Opt-in deletion creates tombstones; physical Count includes
removed records and deletion does not reclaim history files. Retention and
segmentation belong to the application.

The [history sample](samples/ResidentState/HistoryLifecycle.cs) demonstrates both
engines with explicit codecs, publication boundaries and exact clean-reopen reads.

Log retains the first publication/storage failure in `WriteError` and rejects
further operations. Dispose it and inspect the files before reopening. Timer
diagnostics contain Trace listener exceptions. Mutation or disposal of the same
Log from one of its query/diagnostic callbacks is rejected to avoid reentry.

## Flush, errors and lifetime

TimeLog explicit Flush attempts both files. One failure is rethrown as the
original exception; multiple failures produce AggregateException. Success means
the managed flush attempts completed, without an atomic two-file commit or
power-loss durability promise.

The idle timer flushes after 1000 ms. Automatic failures are observable through
LastAutoFlushException and best-effort Trace reporting. The property describes
the latest completed automatic attempt: success clears it; explicit Flush does
not change it. Exceptions from Trace listeners are contained.

TimeLog disposal prevents further operations, serializes cleanup with stream
access and attempts both final flushes and both closes even after failure. It
then throws collected errors using the same single/multiple rule. Repeated
Dispose is inert. Already-running diagnostic delivery may finish after Dispose;
disposal does not wait for Trace listeners. Append, Flush, Count and CreateClient
after disposal throw ObjectDisposedException.

Use one owning writer/store per file set. Collection locks do not synchronize
mutable application objects or multi-step workflows. There are no multi-operation
transactions, rollback, atomic multi-file commits or crash-recovery guarantees.
State Storage.Flush uses Stream.Flush; TimeLog WriteThrough/idle flush and Log's
optional per-file Flush(true) do not establish equivalent durability. Explicitly
flush at application checkpoints and handle errors there.

Use trusted files and codecs that validate input. The optional default serializer
records assembly-qualified runtime types and loads them when reading. It is
unsuitable for untrusted input. Persisted type identity and key layouts need
validation when changing applications or platforms.

## Build and verify

Install the .NET 10 SDK and PowerShell 7.2+, then run from a clean checkout:

```sh
pwsh ./verify.ps1
```

Verification builds and packs the library, then restores checks and the sample
from that package alone using a fresh local feed/cache. No original repository
is required. Outputs go to unique artifacts/verify-* directories and application
data to run-data/verify-*. History checks cover TimeLog boundaries, UTC/null
regressions, synthetic Flush failures, Log publication, overlapping runs, payload
layouts, tombstones and clean reopen. The UTC regression requires a nonzero local
timezone offset; CI uses Europe/Sofia (Windows: FLE Standard Time).

Fault probes establish control flow for injected failures, not real disk-full,
permissions, power loss or concurrent production workloads. Performance depends
on workload and codecs; no universal speed claim is made.

## License and source

[MIT](LICENSE), copyright RtTools contributors. The selected code is extracted
from the owner's RtTools source; [provenance](source-provenance.json) records
relative origin paths, original/current hashes, helper spans and corrections.
Original import history includes the contributor identity `himchev`; attribution
does not imply exclusive authorship. DictionaryLogger, unrelated RtTools helpers
and application-specific consumers are outside this package.
See [release notes](CHANGELOG.md).
