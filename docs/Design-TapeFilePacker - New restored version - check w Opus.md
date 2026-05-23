# TapeNET Design: Shared-Block File Packing

**Scope:** TapeLibNET core (packer, agents, stream manager) — Services & apps are affected only cosmetically.
**Status:** Current implementation reference. This document describes the architecture as it stands in the codebase; it is not a plan or a history log.

---

## 1. Goals

1. Eliminate intra-block padding so multiple small files share a tape block.
2. Identify every file by a `TapeAddress(Block, Offset)` rather than a bare block number.
3. Overlap source-file reads with tape writes by way of a single-in-flight worker-thread backend, so wall-clock throughput on small-file workloads improves alongside tape footprint.
4. Keep the agent-facing notification contract (`ITapeFileNotifiable`) unchanged; only the *timing* of `PostProcess` shifts to commit-time on the packed path.
5. Preserve a fully working "aligned" (one-file-per-block, no packing) write/read path for:
   - writing and reading the TOC itself (which is never packed),
   - side-by-side test comparisons between the packed and the legacy paths.

### Non-goals
- On-tape backward compatibility with pre-packer TOC formats.
- Parallelized / prefetched **read** path (see §12 for the pipelined design).
- Encryption, compression, or other changes to the on-tape file body format beyond removing per-file alignment.

---

## 2. Method Naming Convention

The packed path is the normal API:

| Concern | Default (packed) method | Legacy (aligned) method |
|---|---|---|
| Backup one file | `TapeFileBackupAgent.BackupFile` | `BackupFileAligned` `[Obsolete]` |
| Backup current set | `BackupFilesToCurrentSet` | `BackupFilesToCurrentSetAligned` `[Obsolete]` |
| Backup file list | `BackupFileListToCurrentSet` | `BackupFileListToCurrentSetAligned` `[Obsolete]` |
| Restore one file | `RestoreNextFile` | `RestoreNextFileAligned` `[Obsolete]` |
| Restore file core | `RestoreFileCore(FileInfo, Stream, hasher)` | `RestoreFileCoreAligned(FileInfo, TapeReadStream, hasher)` `[Obsolete]` |
| Manager: open per-file stream | `BeginPackedFile` / `EndPackedFile`, `BeginPackedFileRead` / `EndPackedFileRead` | `ProduceWriteContentStream` / `ProduceReadContentStream` |

The aligned methods are retained for two reasons: the TOC itself is still written and read through the aligned `TapeStream` provisioning path (TOC is never packed), and some unit tests deliberately exercise both paths to compare them.

---

## 3. `TapeAddress` — the canonical position type

```csharp
public readonly record struct TapeAddress(long Block, uint Offset)
{
    public static readonly TapeAddress Zero;     // (0, 0)
    public static readonly TapeAddress Invalid;  // (-1, 0)
    public bool IsValid   => Block >= 0;
    public bool IsAligned => Offset == 0;
    public override string ToString();           // "block" if Offset==0, else "block:offset"
    public static TapeAddress Parse(string s);
}
```

- `Offset` is `uint`, not `long`: all in-memory buffers and `Stream` APIs are `int`-indexed, and LTO block sizes are well below the 4 GiB ceiling on any realistic horizon. Saves 4 bytes per TOC entry on the wire.
- `Invalid` is an internal sentinel; production code stamps real addresses only via the commit handler.
- Ordering compares `Block` first, then `Offset` (used for the rare backward-seek case on selective restore).
- `ToString()` collapses to a bare block number when offset is zero, so aligned addresses (TOC entries, legacy backups) display identically to the pre-packer formatting.

`TapeFileInfo.Address` is the only place a `TapeAddress` is persisted; the TOC serializer reads/writes both fields per entry.

---

## 4. Architectural Layers

Two layers per direction, plus a thin per-file `Stream` façade exposed to agents:

```
                    write path                            read path
                    ──────────                            ─────────
agent ────────────► TapeWriteStreamFacade  ◄────────────── TapeReadStreamFacade
                          │                                       │
                          │                                       │
TapeStreamManager ──► TapeFileWritePacker   ───    ───  TapeFileReadPacker
                          │                   │    │              │
                          │                   │    │              │
                  ITapeWriteBackend           │    │      ITapeReadBackend
                  (worker-thread,             │    │      (synchronous,
                   single in-flight)          │    │       LRU block cache
                          │                   │    │       in the packer)
                          │                   │    │              │
                  Drive.WriteDirect           │    │      Drive.ReadDirect
                                              │    │
                  TapeStreamManager exposes FilesCommitted (write-only),
                  forwarding it from the packer to the agent.
```

There is no shared base class between the two packers — they share too little.
`TapeStreamManager` owns the lifecycle of whichever packer matches the current content mode.

---

## 5. Write Path

### 5.1 Low layer — `ITapeWriteBackend`

```csharp
internal enum WriteBackendStatus { Idle, Busy }

internal readonly record struct WriteResult(
    int  BlocksWritten,     // block-aligned; rounded down from the drive's byte count
    bool EomEncountered,    // ERROR_END_OF_MEDIA seen during this write
    Exception? Exception);  // null on success / pure EOM; non-null on hard error

internal interface ITapeWriteBackend : IDisposable
{
    uint BlockSize { get; }
    void StartWriting(byte[] buffer, int validBytes);          // blocks if prev write still in flight
    WriteBackendStatus PollStatus();                            // non-blocking
    (WriteResult Result, byte[]? Buffer) AwaitCompletion();    // idempotent; returns prior in-flight buffer
}
```

Contract highlights, observed across all callers:

- **EOM is a status, not an exception.** A write that hits end-of-media may have committed some blocks before the boundary; the backend reports `(BlocksWritten = K, Eom = true, Exception = null)`. The high layer treats partial-EOM commit as durable.
- **Hard errors are exceptions on the result.** `Exception != null` (with `Eom = false`) means the buffer's unwritten suffix is gone.
- **`BlocksWritten` is authoritative** — the backend interprets the drive's byte count, rounds down to a block boundary, and the packer trusts the value as-is.
- **Cancellation granularity is one write.** The worker checks shutdown between writes; an in-flight `WriteDirect` runs to completion.
- **The backend is never poisoned by errors.** Error policy lives entirely in the high layer; the backend stays usable for the next `StartWriting`.
- **Buffer ownership.** After `StartWriting(buffer, …)` the caller must not touch `buffer` until it is returned via `AwaitCompletion()`.

The production implementation `WorkerThreadTapeWriteBackend` uses a single dedicated `Thread` and a pair of `ManualResetEventSlim` gates (`workAvailable` / `workComplete`). The sink it invokes is supplied at construction by `TapeStreamManager` (`PackerWriteSink`), so the backend itself stays independent of `TapeDrive` and is unit-tested in isolation via a `MemoryTapeWriteBackend` fake.

### 5.2 High layer — `TapeFileWritePacker`

Owns:

- a pool-rented **fill buffer** (`blockMultiplier × BlockSize`, default 16),
- an **in-flight buffer** handed to the backend (ownership returns on the next harvest),
- a **file registry** keyed by `CommitToken`, recording `{ StartAbsByte, Length, IsOpen }`,
- three position counters: `_committedTapeBlock` (durably on tape), `_baseAbsByteOfFill` (absolute byte corresponding to `_fillBuffer[0]`), and `_inflightValidBytes`.

Construction takes `initialAbsBlock`, which production code (`TapeStreamManager.EnsurePackerCreated`) sets to `Drive.BlockCounter` so the `TapeAddress` values surfaced via `FilesCommitted` are absolute on-tape coordinates. This matches the legacy backup's TOC convention and is required for correct packed restore on multi-set tapes.

Public API:

```csharp
internal sealed class TapeFileWritePacker : IDisposable
{
    public bool IsFileOpen { get; }
    public int  BlockSize  { get; }

    public TapeWriteStreamFacade BeginFile();      // may throw TapePackerEndOfMediaException
    public CommitToken           EndFile();
    public void                  DiscardOpenFile();
    public IReadOnlyList<CommitToken> RollbackPending();
    public void                  Flush();
    public event Action<IReadOnlyList<CommittedFile>>? FilesCommitted;
    public void                  Dispose();        // calls Flush(); rethrows EOM from the final flush
}

internal readonly record struct CommitToken(ulong Sequence);
internal sealed record CommittedFile(CommitToken Token, TapeAddress StartAddress, long Length);
```

Notable design decisions:

- **`BeginFile()` returns a stream, not an address.** The final `TapeAddress` is reported only at commit time inside `FilesCommitted`, so the agent never sees a provisional value that could move on rollback.
- **`CommitToken` is opaque** and surfaces via `TapeWriteStreamFacade.CommitToken` after the stream closes, letting the agent correlate pending entries with the eventual commit notification.
- **Single-open-file invariant.** At most one file is open between `BeginFile` and `EndFile`. This makes `DiscardOpenFile` a pure in-memory truncation in the common case.
- **`Flush()` drains everything.** There is no per-token granularity — no use case ever required partial flush.
- **`BeginFile()` does an opportunistic proactive harvest** via `PollStatus()`. EOM/hard errors therefore surface one-file-late rather than one-buffer-late, without per-`Write` polling overhead.
- **Final flush from `Dispose()` may rethrow `TapePackerEndOfMediaException`.** Disposal completes resource cleanup before rethrowing so the agent can recover and continue on the next volume.

### 5.3 Buffer handoff (double buffering)

```
fill        → currently being filled by the agent's Write() calls
in-flight   → currently being written to tape by the backend
```

When `fill` reaches a full block multiple, the packer:

1. Computes `validBytes = (fillPos / BlockSize) * BlockSize` and holds back the trailing sub-block remainder.
2. Calls `backend.StartWriting(fillBuffer, validBytes)` (blocks if the previous write hasn't finished).
3. Calls `HarvestNow()` to collect the previous write's result, advance `_committedTapeBlock`, fire `FilesCommitted` for newly-promoted files, and return the previous buffer to `ArrayPool<byte>.Shared`.
4. Rents a fresh `fillBuffer`, copies the held-back leftover into the new buffer's prefix, and resumes filling.

This realizes the throughput win on small-file workloads: source ingest into the new fill buffer overlaps with the prior buffer's tape write.

### 5.4 Source-side error handling: `SourceErrorMode`

Selects how `DiscardOpenFile()` reacts when the open file's bytes have already been partially flushed:

| Mode | Behavior | When to choose |
|---|---|---|
| `NoRollback` (default) | Truncate the still-buffered tail; any already-flushed bytes remain as on-tape garbage (no TOC entry, no token). Never repositions the tape head. | Recommended default. In the typical small-file workload the open file hasn't flushed yet, so behavior is identical to `Rollback`. |
| `Rollback` | Drain the backend, `MoveToBlock(committedTapeBlock + 1)`, discard the entire fill buffer. Recovers all of the open file's on-tape bytes. | Archives dominated by very large files where one mid-stream failure would otherwise waste many MiB. |

The mode is per packer instance, configured via `TapeStreamManager.PackerSourceErrorMode`.

### 5.5 EOM and hard-error semantics

| Condition | Backend reports | Packer reaction |
|---|---|---|
| Clean success | `(blocks, Eom=false, null)` | Advance `_committedTapeBlock`; promote any newly-complete files via `FilesCommitted`. |
| EOM (partial commit allowed) | `(blocks, Eom=true, null)` | Advance committed pointer, promote, then collect rolled-back tokens, reset fill to the committed boundary, and throw `TapePackerEndOfMediaException(rolledBackTokens)`. |
| Hard error | `(blocks, false, ex)` | Advance committed pointer for whatever did go through, roll back pending entries, then throw `IOException` wrapping the underlying exception. |

`TapePackerEndOfMediaException.RolledBackTokens` contains pending closed-but-uncommitted files in their original order. The open file (if any) is **not** in this list — the agent calls `DiscardOpenFile()` for it separately.

After EOM, the packer does **not** attempt to re-pack onto the last committed block. The next write target is `committedTapeBlock + 1`, accepting at most one wasted block per EOM event. This avoids a tape-read-during-write hazard and keeps the rollback state machine tractable.

### 5.6 Per-file write façade

`TapeWriteStreamFacade` is a thin `Stream` that:

- forwards `Write(byte[], int, int)` to `TapeFileWritePacker.WriteFromOpenFile`,
- treats `Flush()` as a no-op (the packer decides when to flush blocks),
- exposes `CommitToken` so the agent can read it after the stream closes,
- becomes inert after `EndFile`, `DiscardOpenFile`, or packer disposal.

---

## 6. Read Path

The read path is structurally simpler than the write path: there is no commit decoupling, and the current implementation is fully synchronous (no worker thread, no prefetch). The interface is shaped to admit a prefetching/async backend later without reshaping the agents (see §12).

### 6.1 Low layer — `ITapeReadBackend`

```csharp
internal readonly record struct ReadResult(
    int  BytesRead,
    bool TapemarkEncountered,
    bool EofEncountered,
    Exception? Exception);

internal interface ITapeReadBackend : IDisposable
{
    uint BlockSize { get; }
    bool MoveToBlock(long blockNumber);
    ReadResult ReadBlocks(byte[] buffer, int bytesRequested);
}
```

`SyncTapeReadBackend` (production) routes reads/seeks through the sinks supplied by `TapeStreamManager.PackerReadSink` and a `MoveToBlock` lambda. Like the write backend, it packages ordinary tape conditions (tapemark, EOF) in `ReadResult` rather than throwing; only true I/O exceptions surface as `Exception`.

### 6.2 High layer — `TapeFileReadPacker`

Owns:

- a contiguous ring buffer of `slotCount × BlockSize` (default 16 slots, matching the write packer's `blockMultiplier`),
- per-slot metadata: `{ block, validBytes, lastAccessTick }`,
- the single open-read slot's logical state: `{ currentAbsByte, endAbsByte }`,
- `_drivePositionBlock` — the drive head's last known block, used to skip `MoveToBlock` on adjacent forward reads.

API:

```csharp
internal sealed class TapeFileReadPacker : IDisposable
{
    public bool IsFileOpen { get; }
    public int  BlockSize  { get; }

    public TapeReadStreamFacade BeginRead(TapeAddress addr, long length);
    public void                 EndRead();        // retains cache for the next caller
}
```

Cache policy:

- **Lookup is linear** across `slotCount` slots; `slotCount` is small (16), so the cost is negligible.
- **Eviction is LRU.** Never evicts the slot currently being served.
- **Sequential extension is free.** Crossing into `block + 1` checks the cache first; on miss, the next read happens without a `MoveToBlock` because the drive head is already there.
- **No prefetch.** The cache fills strictly on demand.
- **Backward / non-monotonic reads** (e.g. selective restore) are supported: `BeginRead` will seek backward when needed; cache hit/miss logic is identical.

`TapeReadStreamFacade` forwards `Read(...)` to `TapeFileReadPacker.ReadIntoOpenFile`, tracks logical `Position`, ignores `Flush()`, and calls `EndRead()` on disposal.

---

## 7. `TapeStreamManager` Integration

`TapeStreamManager` owns the packer/backend lifetimes; agents never construct or dispose them directly.

### 7.1 Write path lifecycle

- `BeginWriteContent(remainingCapacity)` transitions the state machine to `WritingContent`, calls `BeginWriteContentSet`, then `EnsurePackerCreated()`.
- `EnsurePackerCreated()` constructs:
  - `WorkerThreadTapeWriteBackend(PackerWriteSink, Drive.BlockSize, logger)`,
  - `TapeFileWritePacker(backend, rewindToBlock: b => Drive.MoveToBlock(b), blockMultiplier: PackerBlockMultiplier, sourceErrorMode: PackerSourceErrorMode, initialAbsBlock: Drive.BlockCounter)`,
  - subscribes `OnPackerFilesCommitted` to the packer's `FilesCommitted` event, which the manager re-exposes as its own `FilesCommitted` event so subscriptions persist across `BeginWriteContent` / `EndWriteContent` cycles.
- `BeginPackedFile()` / `EndPackedFile()` are the agent-facing entry points for per-file write slots; they simply delegate to `Packer.BeginFile()` / `Packer.EndFile()` after the packer is ensured.
- `EndWriteContent()` first calls `FlushAndDisposePacker()`:
  1. Disposes the packer (which calls `Flush()` internally, firing `FilesCommitted` for tail commits).
  2. **Then** unsubscribes from `FilesCommitted` — this order is critical. Inverting it silently drops tail commits.
  3. Disposes the backend.
  4. Rethrows any `TapePackerEndOfMediaException` captured from the final flush so the agent can roll back uncommitted files and continue on a new volume.

`PackerWriteSink` bridges the backend to `Drive.WriteDirect`. It also enforces the reserved-capacity constraint: when the TOC is co-located with content (no Initiator partition), it synthesizes EOM once `CapacityForCurrentSet` would be exceeded, so the agent rolls back only the tail pending tokens that didn't fit. When an Initiator partition is present, the drive's real EOM is authoritative.

### 7.2 Read path lifecycle

- `BeginReadContent()` transitions to `ReadingContent` and, when crossing a set boundary while already in that state, **disposes the read packer first** via `DisposeReadPacker()`. The cached blocks and `_drivePositionBlock` are tied to the prior set's position state; without this reset, the cache feeds stale data into the new set's reads.
- `BeginPackedFileRead(addr, length)` calls `EnsureReadPackerCreated()` (lazy construction of `SyncTapeReadBackend` + `TapeFileReadPacker`), then delegates to `TapeFileReadPacker.BeginRead`.
- `EndPackedFileRead()` calls `TapeFileReadPacker.EndRead()` (retains cache for the next caller).
- `EndReadContent()` calls `DisposeReadPacker()` before transitioning state.

---

## 8. Agent Flows

### 8.1 Backup — `TapeFileBackupAgent`

`BeginWriteContentForCurrentSet(bool newSet)` does the following, in order:

1. Ends any prior read/write via `Manager.EndReadWrite()`.
2. Sets `Navigator.TargetContentSet`.
3. Computes `remainingCapacity`.
4. **Calls `Drive.SetBlockSize(TOC.CurrentSetTOC.BlockSize)` before `Manager.BeginWriteContent(...)`**. This ordering is non-negotiable: `EnsurePackerCreated()` captures `Drive.BlockSize` at packer-creation time. If the block size were set afterwards, the packer would cache a stale value (e.g. the TOC's 16 KB) and the sink's `blocks = bytes / Drive.BlockSize` arithmetic would round to zero — no `FilesCommitted` events, pending files never promoted.
5. Calls `Manager.BeginWriteContent(remainingCapacity)`.

The per-file write helper `BackupFile(template, out hash)`:

1. Opens `Manager.BeginPackedFile()` → `TapeWriteStreamFacade`.
2. Serializes the file header via `template.SerializeHeaderTo(serializer)`.
3. Copies the source file body through an optional `HashingStream`.
4. Calls `Manager.EndPackedFile()` to obtain the `CommitToken`.
5. On any exception, calls `Manager.Packer?.DiscardOpenFile()` — **never** `Drive.MoveToBlock` (earlier files queued in the same fill buffer must remain intact to commit later).

The `BackupFilesToCurrentSet` main loop uses `PackedCommitTracker` to consolidate three otherwise-juggled collections:

- the **pending** map keyed by `CommitToken`,
- the **TOC promotion** (constructing real `TapeFileInfo` with the resolved `TapeAddress` from `FilesCommitted` and appending to `CurrentSetTOC`),
- the **awaiting-post-process** queue, drained on the main loop thread between iterations and once more after `EndWriteContent`.

The tracker exposes four verbs: `Register`, `OnCommitted`, `RemoveRolledBack`, `DrainPostProcess`. The agent subscribes `Manager.FilesCommitted += tracker.OnCommitted` *before* the loop and unsubscribes in `finally` *after* `Manager.EndWriteContent()`, so tail commits emitted by the manager's final flush still reach the tracker.

#### Notification timing on the packed path

| Event | Fires when |
|---|---|
| `NotifyBatchStart` | Once at agent entry, unchanged. |
| `NotifyPreProcessFile` | At `BeginFile()` time. May fire **multiple times** for the same file across retries / EOM rollback. |
| `NotifyFileSkipped` | Pre-processor returns false, or incremental skip. Unchanged. |
| `NotifyPostProcessFile` | **Deferred** — fires only when the file's commit is observed via `FilesCommitted` and the awaiting-post-process queue is drained. May lag the corresponding `BeginFile` by many other files. |
| `NotifyFileFailed` | Source error, hard tape error, read error. **Not** for EOM rollback (the rolled-back files are re-attempted on the next volume). |
| `NotifyBatchEnd` | Once after `EndWriteContent` and final drain. |

Statistics:

- **Bytes processed** moves with the agent's `BytesBackedup += wstream.Length` after each successful per-file body copy.
- **Files processed (committed)** increments when `FilesCommitted` fires (via the tracker).
- **Files failed** increments on `NotifyFileFailed`. On EOM rollback the agent calls `StatsUndoSkips` to back out any skips that occurred between the earliest rolled-back index and the current file index, since those files will be re-processed on the next volume.

#### EOM rollback and multi-volume continuation

When `TapePackerEndOfMediaException` surfaces (either from inside the per-file loop or from `Manager.EndWriteContent()`'s final flush), the shared `HandleEom` handler:

1. Calls `tracker.RemoveRolledBack(eomEx.RolledBackTokens, currentFileIndex)` to find the earliest rolled-back `FileIndex` and the corresponding template.
2. Undoes skip counts for files that will be re-attempted.
3. Snapshots `bc.continuationSetParams = TOC.CurrentSetTOC.ToParams()` and `bc.prevVolumeHasFiles = TOC.CurrentSetTOC.Count > 0`.
4. Sets `bc.fileIndex` to the earliest rolled-back index so the next volume re-attempts every uncommitted file.
5. Always calls `Manager.EndWriteContent()` to close the write session cleanly (a second EOM here is tolerated and logged).
6. Fires `NotifyBatchEnd`, sets `TOC.ContinuedOnNextVolume = true`, and returns false.

The user is then expected to call `ResumeBackupToNextVolume()` after loading new media, which renews the navigator, creates a fresh continuation set from `continuationSetParams`, and dispatches to either `BackupFilesToCurrentSet` (packed) or `BackupFilesToCurrentSetAligned` (legacy) according to `TapeBackupContext.packed`.

```csharp
private struct TapeBackupContext(List<string> fileList, bool ignoreFailures,
    ITapeFileNotifiable? fileNotify, bool incremental, bool packed)
{
    internal readonly List<string> fileList;
    internal readonly bool ignoreFailures;
    internal readonly ITapeFileNotifiable? fileNotify;
    internal readonly bool incremental;
    internal readonly bool packed;          // dispatches Resume to packed or aligned path
    internal int fileIndex;
    internal bool overallSuccess;
    internal bool prevVolumeHasFiles;
    internal TapeSetTOCParams? continuationSetParams;
}
```

### 8.2 Restore — `TapeFileRestoreBaseAgent` & subclasses

`RestoreNextFile(tfi, ...)`:

1. Computes `totalBytes = TapeFileInfo.EstimateSerializedHeaderSize() + tfi.FileDescr.Length`.
2. Opens `Manager.BeginPackedFileRead(tfi.Address, totalBytes)` → `TapeReadStreamFacade`.
3. Validates the header via `TapeDeserializer`.
4. Calls the virtual `RestoreFileCore(FileInfo, Stream, hasher)` — concrete subclasses (`TapeFileRestoreAgent`, `TapeFileValidateAgent`, `TapeFileVerifyAgent`) implement this over a generic `Stream` so block boundaries remain invisible.
5. Verifies hash, fires post-process notifications, applies file attributes.

Cross-path compatibility:

- **Legacy (aligned) backup output restores cleanly through the packed path** because every `Address.Offset == 0` is just a special case of `BeginRead(addr, length)`.
- **Packed-backup output does NOT restore through the legacy aligned path** in general — packed backup may place files at non-zero intra-block offsets even when files exceed one block (the tail of one file shares a block with the head of the next).

---

## 9. Future-Work Hook: Read-Side Parallelization

The write path already overlaps source ingest with tape writes via the worker-thread backend. The read path is currently synchronous. The shape of `ITapeReadBackend` plus the `TapeFileReadPacker`'s LRU cache deliberately admits a prefetching backend without touching agents:

- A prefetcher could read ahead by up to `slotCount - 1` blocks while the consumer reads, populating the LRU ring opportunistically.
- The agent-facing contract (`BeginRead(addr, length)` → `Stream`) does not change.
- Prefetch must be disabled when the agent enqueues non-monotonic addresses (e.g. selective restore with backward seeks); the existing `_drivePositionBlock` tracking is the natural decision input.
- A further step would extract `ITapeReadBackend` into a worker-thread implementation analogous to `WorkerThreadTapeWriteBackend`, with a bounded prefetch queue. Cancellation would plumb through a `CancellationToken` on `BeginRead`.

Other deferred refinements:

- True Win32 `OVERLAPPED` IO for the write backend, if profiling shows the worker thread is the bottleneck.
- Per-token granular `Flush` on the write path (no use case identified yet).
- An aggregated `OnBatchFailed` notification for hard tape errors instead of per-file `OnFileFailed` (current behavior fires per-file `OnFileFailed` for each pending file in the failed flush).

---

## 10. Invariants & Critical Implementation Notes

These are the load-bearing constraints that have been confirmed by both code review and the test suite. Future changes should preserve them.

1. **Absolute addressing.** `TapeFileWritePacker` is anchored at construction to `initialAbsBlock = Drive.BlockCounter`. All `TapeAddress` values surfaced through `FilesCommitted` are absolute on-tape coordinates, matching the legacy TOC convention. Without this, multi-set packed restore would land inside the wrong set.
2. **Block size before begin-write.** `Drive.SetBlockSize(...)` must run **before** `Manager.BeginWriteContent(...)` because `EnsurePackerCreated()` captures `Drive.BlockSize` at construction.
3. **Subscription order at teardown.** `FlushAndDisposePacker` disposes the packer **before** detaching `OnPackerFilesCommitted`, so tail commits emitted during the final flush are still observed.
4. **Read packer reset on set boundary.** Crossing into a different content set while already in `ReadingContent` disposes the read packer first, clearing the cache and `_drivePositionBlock`.
5. **No `MoveToBlock` on per-file packed failure.** The packed `BackupFile` failure path calls `Manager.Packer?.DiscardOpenFile()` only — earlier pending files in the same fill buffer must commit later. Rewinding would clobber them.
6. **Single open file at all times** on both the write and read packers. Multiple simultaneous open files are not supported and the API actively rejects them.
7. **`PreProcessFile` may fire repeatedly** for the same file across retries and EOM rollback. Notifiables must tolerate this.
8. **`PostProcessFile` fires only after commit** on the packed path. An abort thrown from `PostProcessFile` breaks the loop without rewinding (the file is already durable and in the TOC).
9. **One wasted block per EOM event** is accepted (no re-pack onto the last committed block) to avoid a tape-read-during-write hazard.
10. **Filemarks.** No filemarks are written between content files on the packed path. Filemarks are still written after each TOC copy — TOC storage is not subject to the packer.
11. **TOC always uses the aligned path.** `ProduceWriteTOCStream` / `ProduceReadTOCStream` go through the legacy `TapeStream` provisioning regardless of whether content uses the packer.

---

---

## 11. Notes on §9 Future-Work Hook

Section §9 describes the *shape* of a prefetching backend at the interface level. §12 below is the concrete design that supersedes it with a full pipelined read architecture and an explicit implementation plan.

---

## 12. Pipelined Read Path (Design)

### 12.1 Motivation

The write path already overlaps source-file reads with tape writes via `WorkerThreadTapeWriteBackend`. The read path is fully synchronous: each `Drive.ReadDirect` call blocks the restore agent until the tape hardware delivers the block. On LTO hardware, sequential read throughput is limited not by the drive's sustained transfer rate but by the round-trip latency of `ReadDirect` calls when the buffer drains between files.

The pipelined design mirrors the write path: a worker thread reads ahead into a ring buffer while the agent consumes already-delivered blocks. The agent-facing contract (`BeginRead(addr, length)` → `Stream`) is unchanged, keeping all three restore subclasses (`TapeFileRestoreAgent`, `TapeFileValidateAgent`, `TapeFileVerifyAgent`) untouched.

### 12.2 Write-path / read-path mirror

| Write path | Read path (pipelined) |
|---|---|
| `ITapeWriteBackend` | `ITapeReadBackend` (reshaped — see §12.4) |
| `WorkerThreadTapeWriteBackend` | `WorkerThreadTapeReadBackend` (new) |
| `TapeFileWritePacker` | `TapeFilePipelinedReader` (new, replaces `TapeFileReadPacker`) |
| `TapeWriteStreamFacade` | `TapeReadStreamFacade` (existing, unchanged) |
| `FilesCommitted` event | — (no equivalent; read side is pull-based) |
| Fill buffer → in-flight buffer swap | Read-ahead ring: slots prefilled by worker, consumed by agent |
| `BeginFile` / `EndFile` | `BeginRead(addr, length)` / `EndRead` |
| `PackerWriteSink` | `PackerReadSink` (existing) |

### 12.3 Concept: greedy read-ahead

The worker thread reads greedily forward from the current tape position, filling ring slots until either:
- all `slotCount` slots are full (ring saturated), or
- it encounters a tapemark or EOF (end of the current content set).

The agent consumes slots sequentially. When it reaches the last byte of a file and calls `EndRead()`, the next `BeginRead(addr, length)` checks whether `addr` is within the already-prefetched window. On a cache hit the agent proceeds without any tape I/O. On a miss (backward seek / non-monotonic address), the worker is signalled to seek and restart prefetch from the new address.

Greedy reads to buffer end are acceptable because sequential multi-file restore is the dominant case. The cost of prefetching one extra block that turns out to be unneeded is negligible against the latency saved on every sequential file transition.

### 12.4 `ITapeReadBackend` reshaping

The existing synchronous `ITapeReadBackend` is replaced with an async-capable variant:

```csharp
internal interface ITapeReadBackend : IDisposable
{
    uint BlockSize { get; }

    // Seek to block; returns false if the seek itself fails (drive error).
    bool MoveToBlock(long blockNumber);

    // Read exactly one block into buffer[offset..offset+BlockSize].
    // Returns the result; never throws for tape conditions.
    ReadResult ReadOneBlock(byte[] buffer, int offset);
}
```

`ReadOneBlock` replaces the old `ReadBlocks(buffer, bytesRequested)`. The packer (or pipelined reader) now decides how many blocks to read and in what loop; the backend is a pure single-block transport.

`SyncTapeReadBackend` (production) is updated to implement the new interface. Its implementation of `ReadOneBlock` is a thin wrapper around `PackerReadSink`.

### 12.5 `TapeFilePipelinedReader` (new class)

Replaces `TapeFileReadPacker`. Owns:

- a ring buffer of `slotCount` slots, each `BlockSize` bytes, rented from `ArrayPool<byte>.Shared` at construction,
- per-slot metadata: `{ long Block, int ValidBytes, SlotState State }` where `SlotState ∈ { Empty, Prefetching, Ready, Consumed }`,
- a dedicated worker `Thread` that loops: acquire empty slot → `backend.ReadOneBlock` → mark Ready → signal consumer,
- `_prefetchBlock` — next block the worker will read,
- `_consumeSlot` — next slot the agent will read from,
- two `SemaphoreSlim` gates: `_slotsAvailable` (worker waits when ring full) and `_dataAvailable` (agent waits when ring empty),
- a `CancellationTokenSource` for clean shutdown.

Public API (same surface as `TapeFileReadPacker`):

```csharp
internal sealed class TapeFilePipelinedReader : IDisposable
{
    public bool IsFileOpen { get; }
    public int  BlockSize  { get; }

    // addr must be the start address of the file (Block:Offset).
    // If addr.Block is not the next expected prefetch block, the worker seeks first.
    public TapeReadStreamFacade BeginRead(TapeAddress addr, long length);
    public void                 EndRead();
}
```

`BeginRead` signals the worker if it is idle or if a seek is required. The worker always reads forward from `addr.Block`; the facade skips the first `addr.Offset` bytes on the initial read to honour packed (non-aligned) start positions.

### 12.6 Worker thread loop

```
loop:
    wait _slotsAvailable (ring not full)
    if shutdown requested → exit
    if seek pending:
        backend.MoveToBlock(seekTarget)
        _prefetchBlock = seekTarget
        clear seek-pending flag
    result = backend.ReadOneBlock(ring[nextSlot], offset: 0)
    ring[nextSlot].Block      = _prefetchBlock
    ring[nextSlot].ValidBytes = result.BytesRead
    mark slot Ready
    _prefetchBlock++
    signal _dataAvailable
    if result.TapemarkEncountered or result.EofEncountered:
        mark end-of-stream; exit prefetch loop (worker waits for next BeginRead)
```

On hard error (`result.Exception != null`): stash the exception in the slot, mark it as an error slot, signal `_dataAvailable`. The agent will surface it as an `IOException` on the next `Read()` call.

### 12.7 Consumer (agent) read path

`TapeReadStreamFacade.Read(buf, offset, count)` calls `TapeFilePipelinedReader.ReadIntoOpenFile`:

1. If no bytes remain in `_currentSlot`, wait `_dataAvailable`, dequeue next Ready slot.
2. If slot is an error slot, rethrow its exception.
3. Copy `min(count, remaining-in-slot)` bytes into `buf`.
4. Advance logical position.
5. When slot is fully consumed, mark it Empty and signal `_slotsAvailable`.

### 12.8 `TapeStreamManager` integration

Changes to `TapeStreamManager`:

- `EnsureReadPackerCreated()` constructs `WorkerThreadTapeReadBackend` + `TapeFilePipelinedReader` instead of `SyncTapeReadBackend` + `TapeFileReadPacker`.
- `BeginPackedFileRead`, `EndPackedFileRead`, `DisposeReadPacker`, `BeginReadContent` call sites are unchanged — they already go through the `_readPacker` field typed as the packer interface.
- The read packer interface (`IDisposable` + `BeginRead` / `EndRead` / `IsFileOpen` / `BlockSize`) is extracted into a small `ITapeFileReader` internal interface to allow both `TapeFileReadPacker` (kept for aligned/TOC path) and `TapeFilePipelinedReader` (packed path) to be stored in the same field.

### 12.9 `TapeNavigator` changes

No API changes. The existing `_drivePositionBlock` tracking inside `TapeFileReadPacker` moves to `WorkerThreadTapeReadBackend` (it tracks the worker's position, not the agent's). The navigator's address-ordering helpers (`CompareAddresses`, restore-order sorting) are unchanged.

For selective restore (non-monotonic address sequence): `BeginRead` detects when `addr.Block < _prefetchBlock` (backward seek), cancels the current prefetch window, issues `backend.MoveToBlock`, and restarts the worker from the new position.

### 12.10 `TapeFileRestoreBaseAgent` changes

The agent loop in `RestoreNextFile` is **unchanged** — it already calls `Manager.BeginPackedFileRead(addr, length)` and reads through a `Stream`. The pipelined reader is fully transparent behind that `Stream`.

Internal changes only:
- Remove the `[Obsolete]` `RestoreNextFileAligned` path and its `SyncTapeReadBackend` dependency once all callers are confirmed migrated (see §13 step 5).
- The restore agent's sequential-file loop naturally benefits from prefetch without any code change: by the time the agent finishes processing file *N*, the worker has already prefetched the first few blocks of file *N+1*.

### 12.11 Legacy `TapeFileReadPacker` — removal plan

`TapeFileReadPacker` + `SyncTapeReadBackend` (old synchronous read path) are removed once:

1. `TapeFilePipelinedReader` passes the full read-path test suite (all `TapeLibNET.Tests` read-path cases).
2. The aligned (TOC) read path is confirmed to use `ProduceReadTOCStream`, not `BeginPackedFileRead`, so it is unaffected.
3. `RestoreNextFileAligned` is confirmed unused by any non-test caller.

The aligned path for TOC reads (`ProduceReadTOCStream`) is independent and goes through the legacy `TapeStream` provisioning, not through the packer or the pipelined reader. It is unaffected by this change.

---

## 13. Implementation Plan — Pipelined Read Path

The following steps are ordered by dependency. Each step is independently buildable and testable.

### Step 1 — Reshape `ITapeReadBackend` and update `SyncTapeReadBackend`

- Replace `ReadBlocks(byte[] buffer, int bytesRequested)` with `ReadOneBlock(byte[] buffer, int offset)`.
- Update `SyncTapeReadBackend` to implement the new interface.
- Update `TapeFileReadPacker` to call `ReadOneBlock` in a loop (no behavioral change, just adapts to the new interface).
- **All existing tests pass** — no observable behavior change.

### Step 2 — Implement `WorkerThreadTapeReadBackend`

- Single `Thread`, two `ManualResetEventSlim` gates (`workAvailable` / `workComplete`), mirroring `WorkerThreadTapeWriteBackend`.
- Accepts a `PackerReadSink` and `MoveToBlock` lambda from `TapeStreamManager` at construction (same pattern as write backend).
- Unit-tested in isolation via a `MemoryTapeReadBackend` fake (analogous to `MemoryTapeWriteBackend`).

### Step 3 — Implement `TapeFilePipelinedReader`

- Ring buffer, worker loop, seek detection, error-slot propagation (§12.5 – §12.7).
- Extract `ITapeFileReader` interface and make both `TapeFileReadPacker` and `TapeFilePipelinedReader` implement it.
- Unit tests: sequential read, backward seek, tapemark at end of set, hard-error propagation, ring-full back-pressure.

### Step 4 — Wire `TapeStreamManager` to use `TapeFilePipelinedReader`

- `EnsureReadPackerCreated()` constructs `WorkerThreadTapeReadBackend` + `TapeFilePipelinedReader`.
- `_readPacker` field typed as `ITapeFileReader`.
- Integration tests: packed backup → pipelined restore round-trip, multi-set tape, selective restore with backward seek.

### Step 5 — Remove legacy `TapeFileReadPacker` and `SyncTapeReadBackend`

- Confirm no production caller uses `RestoreNextFileAligned` or `TapeFileReadPacker` directly.
- Delete `TapeFileReadPacker.cs`, `SyncTapeReadBackend.cs`.
- Remove `RestoreNextFileAligned` from `TapeFileRestoreBaseAgent` (mark `[Obsolete]` first, then delete after one commit).
- Update §6 and §9 of this document to reflect the removal.

### Step 6 — Performance validation

- Benchmark sequential restore of a large small-file archive (≥ 10 000 files) before and after.
- Confirm that ring-full back-pressure does not cause agent stalls on large-file workloads.
- Verify peak memory: `slotCount × BlockSize` = 16 × 512 KB = 8 MB per restore session (acceptable).

---

## 14. Glossary

| Term | Meaning |
|---|---|
| **Block** | Fixed-size unit of tape I/O. Set per content set (`TapeSetTOC.BlockSize`). |
| **Address** | `(Block, Offset)` pair locating a byte on tape. |
| **Pack / Packing** | Placing file *N+1* immediately after file *N* within the same block. |
| **Open file** | The single file currently between `BeginFile` and `EndFile`. At most one at any time. |
| **Pending commit** | File fully written to the packer (`EndFile` called) but not yet durably on tape. |
| **Committed** | File whose bytes are on tape AND whose `FilesCommitted` notification has fired. |
| **Token** | Opaque `CommitToken` returned by `EndFile`, used to match pending files to their commit notification. |
| **Rollback (open)** | Discard the open file's in-buffer bytes; in-memory only unless `SourceErrorMode.Rollback` is selected and the file already partially flushed. |
| **Rollback (pending)** | Discard all pending-commit files; reposition drive to last committed block. Used on EOM. |
| **Aligned path** | Legacy one-file-per-block write/read methods, suffixed `Aligned` and marked `[Obsolete]`. Still used for TOC I/O and side-by-side tests. |
| **Packed path** | Default write/read methods routed through `TapeFileWritePacker` / `TapeFilePipelinedReader`. |
| **Pipelined reader** | `TapeFilePipelinedReader` — the worker-thread prefetching read component that replaces `TapeFileReadPacker` on the packed restore path. |
| **Ring slot** | One `BlockSize`-byte entry in the pipelined reader's prefetch ring buffer; transitions through Empty → Prefetching → Ready → Consumed. |
| **Seek-and-restart** | When `BeginRead` is called with a backward or non-monotonic address, the worker cancels the current prefetch window, seeks, and restarts read-ahead from the new block. |
