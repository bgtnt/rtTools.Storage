# rtTools.Storage

Embedded .NET storage for application state and indexed history. One BCL-only
package targets .NET 10 with no external runtime dependencies.

Current preview: `0.1.0-preview.3`. APIs and file formats may change before a stable
release. Namespaces remain `RtTools` and `RtTools.Logger`.

| Need | Type | Model |
| --- | --- | --- |
| Resident application state | `DictionaryPersistent<TKey,TValue>`, `ListPersistent<T>` | Complete collection in RAM; explicit mutations persist selected fields. |
| Object persistence by record position | `StreamPersistence`, `StreamPersistence<T>` | Explicit save/update/remove; objects carry a store-local PersistId. |
| Persistent FIFO | `BufferedQueuePersistent<T>` | Disk backlog and a bounded decoded head cache. |
| Current dictionary plus snapshots | `DictionaryLogger<TKey,TValue>` | Update state and explicitly capture timestamped shallow snapshots. |
| Chronological history | `TimeLog<TRecord>` | Strictly increasing timestamps; inclusive `[from,to]` indexed queries. |
| History indexed by fixed-size keys | `Log<TRecord,TKey>` | Buffered appends, publication, duplicate keys; half-open `[from,to)` queries. |

## Install

Download `rtTools.Storage.0.1.0-preview.3.nupkg` from the
[GitHub release](https://github.com/bgtnt/rtTools.Storage/releases/tag/v0.1.0-preview.3)
into a local `packages` directory, then run:

```sh
dotnet add package rtTools.Storage --version 0.1.0-preview.3 --source ./packages
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

DictionaryPersistent/ListPersistent require each codec reader/writer pair
together, validated before opening a filename or accepting a supplied stream.
Rejected stream arguments leave ownership with the caller. Once initialization
begins, the collection owns the stream and attempts to close it if initialization
fails. The initialization error is preserved if cleanup itself throws.
DataStorage checks complete record headers, nonnegative representable record
lengths and payload bounds before allocation or acceptance, even without scan
callbacks. The default CloseAndThrow policy preserves malformed input bytes;
explicit recovery/clear policies have their separate destructive behavior.

## Public API overview

All public type families are listed below. Source links include overloads,
configuration parameters and XML documentation. Nonpersistent helpers do not
create a store on their own.

### Storage, objects and collections — `RtTools`

| Public types | Construction and principal operations | Ownership, codecs and limits |
| --- | --- | --- |
| [DataStorage](src/RtTools.Persistent/DataStorage.cs) | Seekable stream, scan callbacks, header size, serializer and error policy. Add, Update, Remove, GetData, object indexer, ReadPositions/ReadData/ReadObjects, Clear, CopyTo, Flush; Get/Set/UpdateHeaderValue and ClearHeader. | Base record allocator and reusable-space engine. Owns its stream once initialized; direct access needs caller synchronization. Update may relocate a record: use its returned position. |
| DataStorage.DataScan, ObjectScan, OpenStateDelegate, UpdateStructureDelegate&lt;T&gt; | Byte/object scanning, open-state notifications and header update callbacks. | Initial scan callbacks run during construction, under the selected error policy. |
| [DataStorage.DataSourceException, OnException, OpenState](src/RtTools.Persistent/DataStorage%2Brecover.cs) | Storage-format errors, open/recovery policies and reported states. | Default resolves to CloseAndThrow. Explicit Clear policies may erase data; recovery controls do not establish crash durability. |
| [SynchronizedDataStorage](src/RtTools.Persistent/SynchronizedDataStorage.cs) | File/stream constructors or wrap DataStorage; locked record/header operations and reads, SynchronizationRoot, FlushUpdates, FlushIdleMilliseconds/Seconds. | Owns the wrapped store. Defaults: flush after 256 updates or 1000 ms idle. Explicit Flush is an observable checkpoint; its background errors differ from TimeLog's LastAutoFlushException. |
| [DictionaryPersistent&lt;TKey,TValue&gt;](src/RtTools.Persistent/DictionaryPersistent%602.cs) | File/stream, optional paired key/value reader/writer; IDictionary operations and Storage. | Complete resident dictionary; setters serialize. Property mutation alone is not saved. |
| [ListPersistent&lt;T&gt;](src/RtTools.Persistent/ListPersistent%601.cs) | File/stream, optional paired reader/writer; IList operations and Storage. | Complete resident ordered list; finite order labels can be exhausted. |
| [IPersistent](src/RtTools.Persistent/IPersistent.cs), [IPersistentBinary](src/RtTools.Persistent/IPersistentBinary.cs) | PersistId and IsPersistent; binary variant adds ToBinary(). | Unpersisted/removed ID is long.MinValue. IDs are physical, relocatable and store-local; no cross-store validation is implied. |
| [StreamPersistence](src/RtTools.Persistent/StreamPersistence.cs) | Stream, IDataSerializer and optional onLoad; Add, Update, AddOrUpdate, Remove, Storage. | Trusted object serialization. Initial scan sets IDs before load callbacks; the wrapper does not retain a resident object collection. |
| [StreamPersistence&lt;T&gt;](src/RtTools.Persistent/StreamPersistence%601.cs) | File/stream, explicit Func&lt;byte[],T&gt; factory, optional onLoad; same mutation operations. Constructor-based option uses an instance .ctor(byte[]). | T implements IPersistentBinary; ToBinary/factory control payloads, not the serializer parameter. PersistId is refreshed after relocation. Null binary payloads are rejected before writes. |
| [BufferedQueuePersistent&lt;T&gt;](src/RtTools.Persistent/BufferedQueuePersistent%601.cs) | File/stream, positive maxBuffer, optional paired reader/writer; Enqueue, Peek, Dequeue, Clear, Count, BufferedCount, FIFO enumeration and Storage. | Disk backlog with a bounded decoded head cache. Opening decodes all records; enumeration materializes all values. Dequeue removes immediately, without acknowledgement/retry delivery semantics. |
| [IDataSerializer, DataContractDataSerializer](src/RtTools.Persistent/DataSerializer.cs) | Stream serialization/deserialization; default serializer adds array helpers and Default. | Default payloads carry runtime type identity; use trusted files. Explicit codecs define application-controlled bytes. |
| [SerializableKeyValuePair&lt;TKey,TValue&gt;](src/RtTools.Persistent/SerializableKeyValuePair%602.cs) | Settable Key/Value and conversions to/from KeyValuePair. | Serializable carrier used by snapshots; not a store. |
| [BufferBuilder](src/RtTools.Persistent/BufferBuilder.cs) | Capacity/byte-array constructors, typed Append/Read operations, position/remaining-byte controls and ToArray. | Nonpersistent binary-codec helper; version and validate application payloads. |
| [Statics](src/RtTools.Persistent/Statics.Storage.cs) | TryChangeType, default ToBytes/FromBytes; [History helpers](src/RtTools.History/Statics.History.cs) merge sorted ranges and read streams to the end. | Selected helpers, not the complete original RtTools utility class. |
| [Interval&lt;T&gt;, Int16Interval, Int32Interval, Int64Interval, UInt16Interval, UInt32Interval, UInt64Interval, SingleInterval, DoubleInterval](src/RtTools.Persistent/Interval.cs) | Numeric interval construction, comparison, containment and interval operations. | Nonpersistent range helpers; they do not change Log's half-open query contract. |
| [SortedArrayCollection&lt;T&gt;](src/RtTools.History/SortedArrayCollection%601.cs), [ComparableSortedArrayCollection&lt;T&gt;](src/RtTools.History/ComparableSortedArrayCollection%601.cs), SortedArrayCollection&lt;T&gt;.EqualFound | Sorted collection constructors, search/insertion/removal and equal-key policy; comparable variant uses T's comparison. | Nonpersistent index helpers, not additional durable collections. |
| [LimitedList&lt;T&gt;](src/RtTools.Persistent/LimitedList.cs) | Positive Limit, indexer, Add, Insert/InsertFirstOut/InsertLastOut, removal, resizing, conversions and BinarySearch. | Nonpersistent bounded circular list. At capacity insertion evicts an end according to the operation; BinarySearch requires sorted contents. |

### Indexed history — `RtTools.Logger`

| Public types | Construction and principal operations | Publication, codecs and limits |
| --- | --- | --- |
| [TimeLog&lt;TRecord&gt;](src/RtTools.History/Logger/TimeLog%601.cs) | File mask, optional paired reader/writer, timestamped/append-time Append, Count, TicksToExpire, CreateClient, Flush, LastAutoFlushException. | Increasing UTC timestamps; inclusive [from,to] client queries. Owns .tlog/.tldata. |
| [TimeLogClient&lt;TRecord&gt;](src/RtTools.History/Logger/TimeLogClient%601.cs) | File names plus reader, or TimeLog.CreateClient; ForEach, FindAll, CountAll, Exists. | Owns reader handles. Separate readers see flushed bytes; bounds normalize to UTC. |
| [Log&lt;TRecord,TKey&gt;](src/RtTools.History/Logger/Log%602.cs) | File mask, record size, publication/buffer options; Append, Publish, Flush, CreateClient, queries, opt-in DeleteAll, WriteError. | Out-of-order/duplicate keys; queries see published records, [from,to) ranges, sorted FindAll and physical Count including tombstones. |
| [LogClient&lt;TRecord,TKey&gt;](src/RtTools.History/Logger/LogClient%602.cs) | Log.CreateClient with reader buffer sizes; ForEach, FindAll, CountAll, Exists, ExistsBackward. | Bound to its owning Log/published generations. Dispose readers before the owner; ForEach has no global ordering promise. |
| [Key, Key&lt;T&gt;, Key.KeyInt32, Key.KeyInt64, Key.KeySingle, Key.KeyDouble, Key.KeyDateTime](src/RtTools.History/Logger/Key.cs) | Fixed bytes/size, comparison, extrema and merge/range operations; [Key&lt;T&gt;](src/RtTools.History/Logger/Key%601.cs) supports unmanaged comparable values. | Layout and comparison must agree. Validate custom layouts across platforms. Key.InRange is inclusive; Log queries remain half-open. |
| [DataRecord&lt;TKey&gt;](src/RtTools.History/Logger/DataRecord.cs), [Record&lt;TKey&gt;](src/RtTools.History/Logger/Record%601.cs), [Record&lt;T,TKey&gt;](src/RtTools.History/Logger/Record%602.cs) | Base Key/Data contract, raw byte record or typed Source convenience record. | Raw/custom DataRecord supplies explicit bytes. Typed Record uses its getter-only default serializer, without per-record codec injection. |
| [ILogKeyProjection&lt;TKey&gt;](src/RtTools.History/Logger/ILogKeyProjection.cs) | Optional monotonic GetBucketId hint; GetOrderValue is unused by the primary range index. | Grouping optimization; stored/query semantics compare complete keys. |
| [StoreType](src/RtTools.History/Logger/StoreType.cs) | Inline/extended, compressed and deleted record flags. | Payload-layout metadata, not a store. |
| [DictionaryLogger&lt;TKey,TValue&gt;](src/RtTools.History/Logger/DictionaryLogger%602.cs) | Current Values plus TimeLog snapshots, optional separate paired codecs; Update, LogValues, GetLogged, public Values and Log. | Explicit shallow snapshots; Update alone records no history. GetLogged reads flushed history; no atomic state/history transaction. |

## Persist objects and queued work

StreamPersistence is for application-owned objects saved explicitly. IPersistent
tracks the current record position; IPersistentBinary adds selected bytes. Prefer
the typed wrapper's explicit factory and collect reconstructed objects through
onLoad if needed. Add/Update/AddOrUpdate refresh PersistId after relocation;
Remove resets it. Property mutation alone remains an in-memory change.
Implement PersistId/IsPersistent as simple nonthrowing accessors, and do not
change persistence metadata inside codecs. ID assignment follows the storage
write; a throwing metadata setter can therefore fail after bytes were saved.

StreamPersistence filename constructors own files they open. For supplied streams, argument
validation before initialization leaves ownership with the caller; once storage
initialization begins, wrappers own the stream and close it on failure/disposal.
Load callbacks run during construction. The nongeneric OnScanObject virtual hook
also runs before derived construction completes. Default open errors close and
throw. Explicit recovery policies can repeat callbacks; Clear/RecoverOrClear can
clear a valid file after callback failure. These callbacks are not exactly-once
notifications. The generic serializer parameter does not replace ToBinary/factory.
The typed wrapper's Storage.GetData/ReadData expose raw payload bytes; its
Storage object indexer/ReadObjects use IDataSerializer, not the binary factory.
Use onLoad at open, or explicitly apply the factory to raw bytes, to reconstruct T.
Payload callbacks must not reenter mutation or disposal of the same wrapper.

BufferedQueuePersistent is a disk-backed FIFO. Enqueue saves; Peek retains;
Dequeue removes. maxBuffer limits decoded head items, not total backlog. Refill
scans bounded ordinal/position metadata before decoding selected payloads;
opening decodes all records, and enumeration takes a full FIFO snapshot when
iteration starts. Mutating a cached object does not resave it. Use the same
codec pair when reopening; the default codec follows the trusted-type rules below.
Only Count/BufferedCount may be called on the same queue from a codec; mutation,
Peek/refill, enumeration and disposal are rejected.
Direct mutation through queue.Storage bypasses its FIFO/cache invariants and is
unsupported. Public Storage properties expose low-level operations, not an
additional coordinated object API.

## Capture dictionary snapshots

DictionaryLogger.Update performs a locked read/modify/write and reports whether
the key existed; it does not create history. LogValues creates a shallow Key/Value
array and optionally transforms it outside the state lock. It returns null when
the converter suppresses the snapshot or TimeLog rejects its append timestamp.
Reference-valued entries remain references in the returned array, not deep copies;
accepted snapshots are serialized during Append.

Flush logger.Values.Storage for a state checkpoint and logger.Log before
GetLogged/a separate reader must see newly appended snapshots. These are
independent operations. Optional stateReader/stateWriter encode KeyValuePair
payloads; historyReader/historyWriter encode SerializableKeyValuePair[] payloads.
Cleanup attempts both resources even after failure and reports multiple failures
together. This is composition of state and history, not an atomic transaction.
Callbacks must not reenter the same DictionaryLogger wrapper. Direct access to
Values/Log bypasses its operation guards and requires caller coordination.

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

TimeLogClient propagates application decoder, action and predicate exceptions,
including EndOfStreamException. Physical EOF has a separate existing contract:
an incomplete final main record is ignored; EOF while sequentially reading a
payload stops decoded queries at the available prefix. ForEach increments its
header count before reading that payload, so its return value can exceed the
number of completed callbacks. Unfiltered CountAll/Exists inspect matching main
headers without validating payloads. EOF during the initial binary search still
propagates. These reader behaviors do not repair or validate damaged history.

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
diagnostics contain Trace listener exceptions. Publish, Flush, DeleteAll and
Dispose are rejected from query/record callbacks. Mutation and disposal of the
same Log from timer diagnostic callbacks are rejected to avoid reentry.

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

The [persistence sample](samples/ResidentState/PersistenceLifecycle.cs) demonstrates
typed entity factories, a queue with a one-item cache and dictionary snapshots,
each through explicit saves and clean reopen.

Verification builds and packs the library, then restores checks and the sample
from that package alone using a fresh local feed/cache. No original repository
is required. Outputs go to unique artifacts/verify-* directories and application
data to run-data/verify-*. History checks cover TimeLog boundaries, UTC/null
regressions, synthetic Flush failures, Log publication, overlapping runs, payload
layouts, tombstones and clean reopen. Object-persistence, bounded-queue and
dictionary-snapshot suites test the new APIs through the package as well.
Additional regressions cover application exceptions versus physical EOF,
collection constructor ownership/validation and malformed DataStorage bounds.
The UTC regression requires a nonzero local
timezone offset; CI uses Europe/Sofia (Windows: FLE Standard Time).

Fault probes establish control flow for injected failures, not real disk-full,
permissions, power loss or concurrent production workloads. Performance depends
on workload and codecs; no universal speed claim is made.

## License and source

[MIT](LICENSE), copyright RtTools contributors. The selected code is extracted
from the owner's RtTools source; [provenance](source-provenance.json) records
relative origin paths, original/current hashes, helper spans and corrections.
Original import history includes the contributor identity `himchev`; attribution
does not imply exclusive authorship. Unrelated RtTools helpers
and application-specific consumers are outside this package.
See [release notes](CHANGELOG.md).
