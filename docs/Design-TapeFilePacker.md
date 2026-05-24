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

The read path is structurally simpler than the write path in its agent-facing contract: there is no commit decoupling, and the implementation overlaps tape reads with agent consumption via a worker-thread prefetch ring. The interface is shaped to admit future enhancements (e.g. true OVERLAPPED I/O) without reshaping the agents.

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
    ReadResult ReadOneBlock(byte[] buffer, int offset);
}
```

`WorkerThreadTapeReadBackend` (production) routes reads/seeks through the sinks supplied by `TapeStreamManager.PackerReadSink` and a `MoveToBlock` lambda, serializing all drive I/O on a dedicated background thread. Like the write backend, it packages ordinary tape conditions (tapemark, EOF) in `ReadResult` rather than throwing; only true I/O exceptions surface as `Exception`.

### 6.2 High layer — `TapeFilePipelinedReader`

Owns:

- a ring buffer of `slotCount × BlockSize` bytes rented from `ArrayPool<byte>.Shared` (default `slotCount = 16`, matching the write packer's `blockMultiplier`),
- per-slot metadata: `{ State, Block, ValidBytes, EndOfStream, Error }` where `State ∈ { Empty, Prefetching, Ready }`,
- a dedicated background worker `Thread` that greedily prefetches into `Empty` slots up to `_prefetchEndBlockExcl`,
- `_prefetchEndBlockExcl` — per-file exclusive upper block bound enforced by the worker to prevent consuming post-file setmarks on partitioned media.

API:

```csharp
internal sealed class TapeFilePipelinedReader : ITapeFileReader, ITapeReadStreamHost
{
    public bool IsFileOpen { get; }
    public int  BlockSize  { get; }

    public TapeReadStreamFacade BeginRead(TapeAddress addr, long length);
    public void                 EndRead();        // retains ring cache for the next caller
}
```

Read strategy:

- **Seek-and-resume (cache hit).** If `addr.Block` is within the already-prefetched window, the consumer advances past intervening slots and re-arms the prefetch bound for the new file. No tape I/O.
- **Seek-and-restart (cache miss).** Backward or non-monotonic address: the ring is flushed, the worker is given a seek request, and prefetch restarts from `addr.Block`.
- **`_prefetchEndBlockExcl` bound.** `BeginRead` arms the worker with an exclusive upper block (`((startAbs + length - 1) / BlockSize) + 1`). The worker parks once the bound is reached, leaving any post-file setmarks intact for the navigator.
- **Ring retention on `EndRead`.** The ring contents are retained (not flushed) so the next `BeginRead` for the immediately following packed file can hit cache without tape I/O.

`TapeReadStreamFacade` forwards `Read(...)` to `TapeFilePipelinedReader.ReadIntoOpenFile`, tracks logical `Position`, ignores `Flush()`, and calls `EndRead()` on disposal.

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

- `BeginReadContent()` transitions to `ReadingContent` and, when crossing a set boundary while already in that state, **disposes the read packer first** via `DisposeReadPacker()`. The prefetch ring and worker thread are tied to the prior set's position state; without this reset, stale prefetched blocks could feed into the new set's reads.
- `BeginPackedFileRead(addr, length)` calls `EnsureReadPackerCreated()` (lazy construction of `WorkerThreadTapeReadBackend` + `TapeFilePipelinedReader`), then delegates to `TapeFilePipelinedReader.BeginRead`.
- `EndPackedFileRead()` calls `TapeFilePipelinedReader.EndRead()` (retains ring cache for the next caller).
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

## 9. Future-Work Hook: Read-Side Parallelization *(realized — see §12)*

The write path already overlaps source ingest with tape writes via the worker-thread backend. The read path is now pipelined via `TapeFilePipelinedReader` (§12), which overlaps tape read-ahead with agent consumption through a worker-thread ring buffer. The agent-facing contract (`BeginRead(addr, length)` → `Stream`) is unchanged.

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

## 11. Notes on §9 and §12

Section §9 originally described the *shape* of a prefetching backend at the interface level. §12 below is the concrete design that supersedes it. The pipelined read path described in §12 has been fully implemented as of Step 8 of the implementation plan (§13); `TapeFilePipelinedReader` and `WorkerThreadTapeReadBackend` are the current production components on the packed restore path.

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

`SyncTapeReadBackend` is updated in place to implement the new interface; its `ReadOneBlock` is a thin wrapper around the existing `TapeReadSink` delegate (one block per call). `SyncTapeReadBackend` is retained through the transition so the legacy `TapeFileReadPacker` keeps working until step 8 of §13 removes both.

### 12.5 `TapeFilePipelinedReader` (new class)

Replaces `TapeFileReadPacker` on the packed restore path. Implements `ITapeFileReader` and `ITapeReadStreamHost` (the consumer hook used by `TapeReadStreamFacade`). Owns:

- a ring buffer of `slotCount × BlockSize` bytes rented from `ArrayPool<byte>.Shared` at construction (default `slotCount = PackerBlockMultiplier = 16`, mirroring the write packer); slot *i* lives at `[i*BlockSize .. +BlockSize)`;
- per-slot metadata: `{ SlotState State, long Block, int ValidBytes, bool EndOfStream, Exception? Error }` where `SlotState ∈ { Empty, Prefetching, Ready }`. Consumed slots are reset straight back to `Empty` — there is no separate `Consumed` state;
- a dedicated worker `Thread` ("TapeFilePipelinedReader") that loops: reserve `Empty` slot → `backend.ReadOneBlock` outside the lock → mark `Ready` → pulse waiters;
- `_producerIndex` / `_consumerIndex` — the strict-FIFO ring cursors;
- `_prefetchBlock` — the next absolute block the worker will fetch (`-1` = idle);
- `_prefetchEndBlockExcl` — the exclusive upper bound the worker may prefetch *for the currently armed file*. Critical: without this the worker would happily read the block that follows the file payload, which on the partitioned layout is the trailing content setmark — consuming it would leave the drive past EOM and break the next `EndReadContentSet` / `MoveToTargetContentSet`;
- `_prefetchHalted` — set when the worker should pause until the next `BeginRead` re-arms (after EOS, error, short read, or `EndRead`);
- `_seekPending` / `_pendingSeekBlock` — request channel telling the worker to seek before its next read;
- a single `Monitor` on `_lock` plus `Monitor.Wait` / `Monitor.PulseAll` for all coordination (no `SemaphoreSlim`, no `CancellationTokenSource`); shutdown is signalled by the `_shutdown` / `_disposed` flags under the same lock.

Public API (same surface as `TapeFileReadPacker`, declared by `ITapeFileReader`):

```csharp
internal sealed class TapeFilePipelinedReader : ITapeFileReader, ITapeReadStreamHost
{
    public bool IsFileOpen { get; }
    public int  BlockSize  { get; }

    // addr must be the start address of the file (Block:Offset).
    // If addr.Block is outside the current prefetch window the worker is signalled
    // to seek; otherwise the consumer simply advances past already-buffered blocks.
    public TapeReadStreamFacade BeginRead(TapeAddress addr, long length);
    public void                 EndRead();
}
```

`BeginRead` computes `endBlockExcl = ((startAbs + length - 1) / BlockSize) + 1` (zero-length collapses to `addr.Block`) and arms the worker with `[addr.Block .. endBlockExcl)`. The worker always reads forward; the facade skips the first `addr.Offset` bytes on the initial read to honour packed (non-aligned) start positions.

### 12.6 Worker thread loop

All waits use `Monitor.Wait(_lock)` and all wake-ups use `Monitor.PulseAll(_lock)`; pseudocode condensed from `WorkerLoop`:

```
loop:
    lock (_lock):
        wait until any of:
            _shutdown                                       → exit
            _seekPending                                    → handle seek
            not _prefetchHalted AND _prefetchBlock >= 0
               AND _prefetchBlock < _prefetchEndBlockExcl
               AND head producer slot is Empty              → handle read

        if _seekPending:
            consume seek request (read it under the lock, clear the flag)
        else:
            reserve producer slot:
                slot.State = Prefetching
                slot.Block = _prefetchBlock
                advance _producerIndex

    // --- outside the lock ---
    if seek:
        ok = backend.MoveToBlock(seekTarget)
        lock (_lock):
            if !ok: publish an error slot at seekTarget; _prefetchHalted = true
            PulseAll
        continue

    result = backend.ReadOneBlock(_ringBuffer, slotIdx * BlockSize)
    lock (_lock):
        // Race guard: ring may have been flushed by a concurrent BeginRead miss.
        if slot was reassigned (state != Prefetching or Block changed):
            discard this read; continue
        slot.ValidBytes  = clamp(result.BytesRead, 0..BlockSize)
        slot.EndOfStream = result.TapemarkEncountered || result.EofEncountered
        slot.Error       = result.Exception
        slot.State       = Ready

        if Exception or Tapemark or EOF or BytesRead < BlockSize:
            _prefetchHalted = true        // wait for next BeginRead to re-arm
        else:
            _prefetchBlock = slot.Block + 1
        PulseAll
```

The `_prefetchEndBlockExcl` predicate is what enforces the per-file upper bound — once the worker has queued every block the open file may need it parks until the next `BeginRead` reopens the gate. Without it, sequential restore on the Partitions profile would silently consume the content-setmark following the last file and the next `BeginReadContent` / `EndReadContentSet` transition would fail.

On hard error (`result.Exception != null`), the exception is stored on the slot and `_prefetchHalted` is set; the agent surfaces it as `IOException` on the next consumer read. Failed seeks publish a synthetic error slot at the requested block (same surfacing path).

If the worker itself throws unexpectedly, the outer `catch` logs at error level, sets `_prefetchHalted`, and pulses — disposal still completes cleanly via the `_shutdown` flag.

### 12.7 Consumer (agent) read path

`TapeReadStreamFacade.Read(buf, offset, count)` calls `TapeFilePipelinedReader.ReadIntoOpenFile` (the `ITapeReadStreamHost` hook), which loops until the requested span is satisfied or the file ends:

1. Derive `block = _readCurrentAbsByte / BlockSize`, `offsetInBlock = _readCurrentAbsByte % BlockSize`.
2. Under `_lock`, `Monitor.Wait` until the head slot (`_slots[_consumerIndex]`) is `Ready` (or shutdown). If the head slot's `Block` does not match the requested `block`, a seek-and-restart is in flight — return whatever bytes are already accumulated; the next `Read` call will park on the new head.
3. If the slot carries an `Error`, release it and throw `IOException` wrapping the original exception.
4. If `offsetInBlock >= ValidBytes`, the slot is a short/EOS block — return what's been read so far.
5. Copy `min(count, ValidBytes - offsetInBlock)` bytes from `_ringBuffer[slot * BlockSize + offsetInBlock]` into the caller's buffer, advance `_readCurrentAbsByte`.
6. When the slot is fully consumed, reset it to `Empty`, advance `_consumerIndex`, and `Monitor.PulseAll` so the worker can refill that slot.

`EndRead` marks the open facade closed, drops `_prefetchEndBlockExcl`, sets `_prefetchHalted`, and pulses — the prefetch ring contents are *retained* so an adjacent `BeginRead` for the next packed file can hit the cache without any tape I/O.

### 12.8 `TapeStreamManager` integration

Changes to `TapeStreamManager`:

- A new internal interface `ITapeFileReader` exposes the existing read-packer surface: `BlockSize`, `IsFileOpen`, `BeginRead(TapeAddress, long)`, `EndRead()`, plus `IDisposable`.
- The field `m_readPacker` is retyped from `TapeFileReadPacker?` to `ITapeFileReader?`.
- `EnsureReadPackerCreated()` constructs `WorkerThreadTapeReadBackend` + `TapeFilePipelinedReader` instead of `SyncTapeReadBackend` + `TapeFileReadPacker`.
- `BeginPackedFileRead`, `EndPackedFileRead`, `DisposeReadPacker`, and `BeginReadContent` call sites are unchanged — they already go through the field.
- `ITapeFileReader` is retained after the legacy classes are removed so the in-memory test fake (`MemoryTapeReadBackend` and a pipelined reader bound to it) can be substituted in tests just like `MemoryTapeWriteBackend` is on the write side.

### 12.9 `TapeNavigator` changes

No API changes. The drive-head position tracking that `TapeFileReadPacker` performed via its private `_drivePositionBlock` moves into `WorkerThreadTapeReadBackend` — it now tracks the worker's read-ahead position, not the agent's. The navigator's address-ordering helpers (`CompareAddresses`, restore-order sorting) are unchanged.

`BeginRead` distinguishes two paths inside `ArmReadSession_NoLock`:

- **Seek-and-resume (cache hit).** If `addr.Block` is at or after the consumer head and either is currently `Ready` or lies strictly below `_prefetchBlock` (i.e. the worker has already queued it), the consumer simply advances past intervening `Ready` slots, re-arms `_prefetchEndBlockExcl` for the new file, reopens `_prefetchHalted` if needed, and pulses. No tape I/O — the seamless packed `file N → file N+1` transition (where both share a block) is the common case.
- **Seek-and-restart (cache miss).** Otherwise — typically `addr.Block < headBlock` (backward selective restore) or `addr.Block` far ahead of the window — the reader first waits for any `Prefetching` slot to settle (so the worker's in-flight write does not bleed into a flushed slot), flushes the ring, records a seek request to `addr.Block`, and pulses the worker.

### 12.10 Restore agent changes

The agent loop in `TapeRestoreAgent.RestoreNextFile` is **unchanged** — it already calls `Manager.BeginPackedFileRead(addr, length)` and reads through a `Stream`. The pipelined reader is fully transparent behind that `Stream`.

Internal changes only:
- The agent's sequential-file loop naturally benefits from prefetch without any code change: by the time the agent finishes processing file *N*, the worker has already prefetched the first few blocks of file *N+1*.
- The `[Obsolete]` private members `RestoreNextFileAligned`, `RestoreFilesFromCurrentSetAligned`, and `RestoreFileCoreAligned` are deleted in step 8 of §13 along with the legacy read backend they depend on.

### 12.11 Legacy `TapeFileReadPacker` — removed

`TapeFileReadPacker` and `SyncTapeReadBackend` (old synchronous read path) have been removed from the active codebase (Step 8 of §13). Their source files are preserved in `TapeLibNET/Excluded Files/` as `TapeFileReadPacker.cs.txt` and `SyncTapeReadBackend.cs.txt`.

The aligned path for TOC reads (`ProduceReadTOCStream`) is independent and goes through the legacy `TapeStream` provisioning, not through the packer or the pipelined reader. It is unaffected by this change.

---

## 13. Implementation Plan — Pipelined Read Path

The steps below are ordered by dependency. **Every step leaves the solution buildable and the existing `TapeLibNET.Tests` suite green**, and each step that adds production code also adds focused unit tests for the new surface. The legacy synchronous read path stays live until step 8; only then is it deleted.

### Step 1 — Extract `ITapeFileReader` interface (no behavior change) ✅

- Introduced `internal interface ITapeFileReader : IDisposable` in `TapeLibNET.TapeFilePacker` exposing `BlockSize`, `IsFileOpen`, `BeginRead(TapeAddress, long)`, `EndRead()`.
- Made the existing `TapeFileReadPacker` implement `ITapeFileReader` (its public surface already matched).
- Retyped `TapeStreamManager.m_readPacker` from `TapeFileReadPacker?` to `ITapeFileReader?`.
- **Build:** green. **Tests:** all existing tests pass unchanged — no behavior change, only a type change at one field.

**Implementation notes:** No deviations from the plan. The interface surface matched `TapeFileReadPacker` exactly, so the `implements` declaration and field retype were the only changes. `TapeReadStreamFacade` still holds a concrete `TapeFileReadPacker` reference internally and is not yet widened — that coupling is intentional at this stage; the facade is updated in Step 5 when `TapeFilePipelinedReader` introduces its own consumer hook.

---

### Step 2 — Reshape `ITapeReadBackend` to single-block transport ✅

- Replaced `ReadBlocks(byte[] buffer, int bytesRequested)` with `ReadOneBlock(byte[] buffer, int offset)`.
- Updated `SyncTapeReadBackend` to implement the new method (body shrinks to a single sink call for one block).
- Updated `TapeFileReadPacker.EnsureBlockCached` to call `ReadOneBlock` directly into the ring buffer at the victim slot's offset (`victim * _blockSize`), eliminating the temporary `ArrayPool` rental that the old `ReadBlocks` path required.
- Updated `TapeStreamManager.PackerReadSink` signature to match `TapeReadSink(byte[] buffer, int offset)`.
- **Build:** green. **Tests:** all existing read-path tests pass unchanged.

**Key design decision — `offset` parameter instead of a separate buffer:**
The original plan said only "one block per call"; the concrete API shape was left open. The `offset` parameter was chosen deliberately so that the *caller* (pipelined reader worker) can write directly into a slot in its ring buffer — a single large `ArrayPool`-rented array — without any intermediate copy. The backend fills `buffer[offset .. offset+BlockSize]` in-place. `SyncTapeReadBackend` validates that `offset + BlockSize ≤ buffer.Length` and forwards to the sink unchanged. `TapeFileReadPacker.EnsureBlockCached` exploits this to fill `_ringBuffer[victim * _blockSize]` in one call, which also removed the only remaining per-block `ArrayPool` rental in the legacy read packer.

---

### Step 3 — Add `MemoryTapeReadBackend` test fake ✅

- New class `MemoryTapeReadBackend : ITapeReadBackend` in `TapeLibNET/TapeFilePacker/` (accessible to tests via `InternalsVisibleTo`), analogous to `MemoryTapeWriteBackend`:
  - Primary constructor takes `(uint blockSize, byte[][] blocks)` — caller supplies pre-split block arrays.
  - `FromWrittenBuffers(uint blockSize, IReadOnlyList<byte[]> writtenBuffers)` convenience factory splits the concatenated output of `MemoryTapeWriteBackend.WrittenBuffers` into individual blocks, enabling direct write→read round-trip tests.
  - Scripted conditions: `ScriptTapemarkBefore(long block)`, `ScriptEofAfterBlock(long block)`, `ScriptHardErrorAtBlock(long block, string message)`.
  - `MoveToBlock` records every call into `SeekHistory` (a `List<long>`) for assertion, in addition to repositioning the internal read cursor.
- New test file `TapeLibNET.Tests/TapeFilePacker/TapeReadBackendTests.cs`: **21 tests** covering sequential reads, the `offset` parameter, `FromWrittenBuffers`, seek + read, seek history, tapemarks, EOF, hard-error injection, validation (zero block size, null buffer, negative offset, buffer-too-small), and disposal.
- **Build:** green. **Tests:** 21/21 new tests pass. No production wiring change.

**Implementation notes — placement in production assembly:**
`MemoryTapeReadBackend` lives in `TapeLibNET` (not in the test project) so it can be used in future integration tests and in Step 5's `TapeFilePipelinedReaderTests`. The class is `internal`; the `InternalsVisibleTo` attribute on `TapeLibNET` already grants access to `TapeLibNET.Tests`, matching the pattern established by `MemoryTapeWriteBackend`.

**Expansion beyond the plan — richer scripting API:**
The plan mentioned only "scriptable tapemark / EOF positions and hard-error injection". The implementation adds `SeekHistory` (ordered list of every `MoveToBlock` argument) as a first-class assertion surface, which proved essential for verifying the seek-skip optimisation in Step 4 and will be equally critical in Step 5.

---

### Step 4 — Implement `WorkerThreadTapeReadBackend` (not yet wired) ✅

- New class `WorkerThreadTapeReadBackend : ITapeReadBackend` in `TapeLibNET/TapeFilePacker/`, mirroring `WorkerThreadTapeWriteBackend`.
- Single dedicated background `Thread` ("TapeReadBackend"), two `ManualResetEventSlim` gates (`_readRequested` / `_readComplete`), one in-flight request slot protected by a lock.
- Constructor takes `TapeReadSink`, `TapeReadSeek`, `uint blockSize`, optional `ILogger`.
- `ReadOneBlock` dispatches to the worker and blocks until `_readComplete`; `MoveToBlock` does the same via a seek-kind request — both go through the shared `DispatchAndWait` helper, so reads and seeks are always serialised on the drive thread.
- Owns `_drivePositionBlock` for skip-seek optimisation.
- New test file `TapeLibNET.Tests/TapeFilePacker/WorkerThreadTapeReadBackendTests.cs`: **25 tests** covering construction, single-block read, sequential reads, offset parameter, buffer-too-small / null / negative-offset guards, EOF, tapemark forwarding, sink-throws-exception, sink-returns-hard-error, `MoveToBlock` (success, then-read, out-of-range, backward seek), seek-skip optimisation (skip and non-skip paths, tapemark invalidation), 20-block sequential run, and disposal (double-dispose, post-dispose throws).
- **Build:** green. **Tests:** 25/25 pass. No production call sites use the new class yet.

**Key design decision — consumer-driven pull, not producer-push:**
The original plan said "mirrors `WorkerThreadTapeWriteBackend`". The write backend is producer-driven (`StartWriting` hands off a buffer and returns immediately; `AwaitCompletion` harvests later). For the read side, a symmetric producer-push design would require the backend to hold a buffer and signal "data ready" asynchronously — adding complexity with no benefit at this layer, because `TapeFilePipelinedReader` (Step 5) is the component that actually overlaps I/O with consumption. The backend therefore uses a simpler **consumer-driven pull**: `ReadOneBlock` blocks the caller until the worker delivers. The worker still isolates all drive I/O on one OS thread, which is the invariant `TapeFilePipelinedReader` depends on.

**Key design decision — `RequestKind` discriminator for a unified request channel:**
Both `ReadOneBlock` and `MoveToBlock` route through the same `DispatchAndWait` path using a `RequestKind { None, Read, Seek }` tag in the shared request slot. This ensures that a seek issued from `MoveToBlock` can never interleave with a concurrent read on the drive thread — there is no separate seek channel that could race. The single slot also keeps the lock surface minimal; there is never more than one in-flight request.

**Nuance — `_drivePositionBlock` seeding requirement:**
`_drivePositionBlock` is initialised to `-1` (unknown). After a successful full-block read, the worker increments it. After a tapemark, EOF, hard error, or failed seek, it resets to `-1`. Because the initial value is unknown, `MoveToBlock` always issues a physical seek until the position is first seeded by an explicit `MoveToBlock` call — a plain sequential read from position 0 without a preceding seek does *not* advance `_drivePositionBlock` to a known value (the backend doesn't know it started at block 0). Callers (`TapeFilePipelinedReader`) must call `MoveToBlock` before the first read of a new session or after any seek-and-restart to seed the tracked position. This was confirmed by the `MoveToBlock_SamePositionAsAfterRead_SkipsPhysicalSeek` test, which required an explicit `MoveToBlock(0)` before the first read in order for the subsequent skip to be observable.

**Exception containment on the worker thread:**
If `_readSink` or `_seekSink` throws, the exception is caught on the worker thread, wrapped into a `ReadResult(0, false, false, ex)` (for reads) or recorded as `_seekResult = false` (for seeks), and the worker continues its loop. The worker thread never crashes on a sink exception. The consumer observes the failure via the returned `ReadResult.Exception` or `MoveToBlock` returning `false`.

### Step 5 — Implement `TapeFilePipelinedReader` (not yet wired) ✅

- New class `TapeFilePipelinedReader : ITapeFileReader, ITapeReadStreamHost` in `TapeLibNET/TapeFilePacker/`. Ring buffer of `slotCount × BlockSize` rented from `ArrayPool<byte>.Shared` (default `slotCount = 16`), per-slot metadata `{ State, Block, ValidBytes, EndOfStream, Error }`, dedicated background `Thread` ("TapeFilePipelinedReader").
- `BeginRead(addr, length)` discriminates seek-and-resume (in-window hit) vs. seek-and-restart (out-of-window) via `ArmReadSession_NoLock`; honours intra-block start offset in the returned `TapeReadStreamFacade`.
- `EndRead` marks the facade closed, drops the per-file prefetch bound, and parks the worker (`_prefetchHalted = true`) while *retaining* the ring so the next `BeginRead` can hit cache.
- Worker loop and error-slot propagation per §12.6 / §12.7.
- New tests `TapeLibNET.Tests/TapeFilePacker/TapeFilePipelinedReaderTests.cs`: **27 tests** covering construction, sequential reads, intra-block offset, file boundary across blocks, packed back-to-back files sharing a block, backward seek-and-restart, tapemark/EOF surfacing, hard-error propagation, ring back-pressure with a slow consumer, double-`BeginRead` rejection, and disposal under concurrency.
- **Build:** green. **Tests:** 27/27 new tests pass; production restore path still used `TapeFileReadPacker` at this point.

**Key design decision — single `Monitor` lock, no `SemaphoreSlim` / `CancellationTokenSource`:**
The original plan called for two `SemaphoreSlim` gates (`_slotsAvailable`, `_dataAvailable`) and a `CancellationTokenSource`. The implementation collapses both gates into one `Monitor.Wait`/`PulseAll` discipline on a single `_lock`. Every condition the worker or consumer may wait on (slot Ready, slot Empty, seek pending, shutdown, prefetch re-armed) is part of the same predicate evaluation, so a single broadcast pulse correctly wakes whichever side needs to proceed. Shutdown rides on the `_shutdown` / `_disposed` flags read under the same lock, eliminating the need for a separate cancellation token and the registration plumbing that would otherwise pair with it.

**Key design decision — `_prefetchEndBlockExcl` per-file upper bound (added beyond the plan):**
The plan let the worker prefetch greedily until tapemark/EOF. That works on the synchronous read path but breaks the partitioned multi-set layout: the worker would consume the content-setmark immediately following the open file's last block, leaving the drive past EOM. The next `BeginReadContent` / `EndReadContentSet` transition then fails because the setmark is gone from the drive's perspective.
`BeginRead` therefore arms the worker with both a start block and an exclusive end block (`endBlockExcl = ((startAbs + length - 1) / BlockSize) + 1`). The worker's main-loop predicate checks `_prefetchBlock < _prefetchEndBlockExcl` and parks once the bound is reached, leaving the post-file setmark intact for the navigator. This was the root cause of a multi-volume restore regression discovered during Step 6 integration and is the single biggest deviation from §12's original sketch.

**Race guard against concurrent seek-flush:**
When `ArmReadSession_NoLock` takes the cache-miss path it flushes the ring; the worker's in-flight `ReadOneBlock` (running outside the lock) may complete *after* the flush. The worker's post-read critical section therefore re-checks the slot's `State` and `Block` and, if either was reassigned, discards the read silently. Symmetrically, `ReadIntoOpenFile` checks `slot.Block == block` after waiting for `Ready` and returns short if a seek-and-restart raced ahead of it; the consumer's next call will park on the new head.

**Slot states reduced from 4 → 3:**
The plan listed `{ Empty, Prefetching, Ready, Consumed }`. The implementation drops `Consumed`: when the consumer has drained a slot, `ReleaseHeadSlot` resets it straight to `Empty` and pulses. There is no observable state between "fully drained" and "available for the worker again".

---

### Step 6 — Wire `TapeStreamManager` to the pipelined reader ✅

- `EnsureReadPackerCreated()` now constructs `WorkerThreadTapeReadBackend(PackerReadSink, b => Drive.MoveToBlock(b), Drive.BlockSize, m_logger)` paired with `TapeFilePipelinedReader(backend, slotCount: PackerBlockMultiplier, logger: m_logger)`.
- `m_readBackend` keeps its `ITapeReadBackend?` typing; only the constructed concrete type changed.
- `DisposeReadPacker`, `BeginPackedFileRead`, `EndPackedFileRead`, and the set-boundary teardown in `BeginReadContent` are unchanged — they already go through the interface.
- **Build:** green. **Tests:** the full `TapeLibNET.Tests` suite (packed round-trip, multi-set, multi-volume, selective restore, large file, file edge cases) passes.

**Issue surfaced during this step — multi-volume `Partitions` regression:**
The initial wiring failed only on the partitioned-media multi-volume restore path. Trace logs showed the navigator's post-set transition encountering `ERROR_NO_DATA_DETECTED` (later mis-attributed to backend state from the prior volume). Root cause: the greedy prefetcher had consumed the trailing content setmark of the *open file's last block + 1*, so by the time `EndReadContentSet` tried to position past the setmark, it was already gone. The fix was the `_prefetchEndBlockExcl` bound documented under Step 5. A secondary cleanup — resetting the backend's stale error state in `VirtualTapeDriveBackend.LoadMedia` so a previous-volume `ERROR_NO_DATA_DETECTED` is not inherited by the next volume's load — was applied to silence the cascading symptom. Both fixes were validated by the full suite turning green.

**No `MoveToBlock` seeding workaround needed at the manager:**
Step 4's nuance about `_drivePositionBlock` needing an explicit `MoveToBlock` to seed was naturally satisfied here: every `BeginRead(addr, length)` that produces a cache miss issues `backend.MoveToBlock(addr.Block)` from the worker, so the position becomes known before the first read of any session. No call-site changes were needed at `TapeStreamManager`.

---

### Step 7 — Selective-restore and multi-set integration coverage ✅

- New integration test class `TapeLibNET.Tests/TapeRestoreAgentPipelinedTests.cs` alongside `TapeRestoreAgentPackedTests`. **8 scenarios × 4 drive profiles (`FilemarksOnly`, `Setmarks`, `SeqFilemarks`, `Partitions`) = 32 tests, all passing.**
- Scenarios go beyond the minimum plan:
  - `Pipelined_BackwardSeek_SelectiveRestore_AllFilesByteForByte` — every-other file restored in reverse TOC order, stressing `ArmReadSession`'s out-of-window branch.
  - `Pipelined_RestoreFirstFileLast_SeekToBlockZero_Succeeds` — the most aggressive backward seek (selective restore of file #0 last).
  - `Pipelined_TwoSets_OneAgentSession_ResetsRingBetweenSets`, `Pipelined_TwoSets_ReverseOrder_SameSession_Succeeds` — verify `DisposeReadPacker` on the set boundary still flushes the prefetch ring.
  - `Pipelined_RestoreFromMultipleSets_PackerResetsBetweenSets` — exercises the cross-set restore through `RestoreFilesFromSets`.
  - `Pipelined_SingleLargeFile_RingBackPressure_RoundTrip` — 64-block single file where the ring saturates ahead of the consumer.
  - `Pipelined_MixedLargeAndTiny_RoundTrip` — sustained-stream large files interleaved with rapid `BeginRead`/`EndRead` cycles on tiny ones.
  - `Pipelined_SelectiveLargeFile_FromMixedSet_Succeeds` — mid-set seek to a large file with ring saturation under selective restore.
- A small private `NameSetFilter` was added in the test file to express explicit name-based selective restore without depending on `FclNET` from the test assembly.
- **Build:** green. **Tests:** 32/32 new integration tests pass; full `TapeLibNET.Tests` suite (1689 active tests) remains green.

**Implementation notes — coverage beyond the minimum plan:**
The plan asked for backward-seek selective restore, cross-set restore in one session, and large-file back-pressure. The implementation parameterises each scenario across all four `DriveProfile` values so any profile-specific edge case (notably `Partitions`, which exposed the §12.6 `_prefetchEndBlockExcl` bug) is exercised end-to-end. This is the recommended git checkpoint before the Step 9 benchmark.

### Step 8 — Remove the legacy non-pipelined packer path ✅

- Confirmed by `find_symbol` / grep that `TapeFileReadPacker` and `SyncTapeReadBackend` have no non-test callers; test files also had zero references (already supplanted by Step 5/7 tests).
- Moved `TapeLibNET/TapeFilePacker/TapeFileReadPacker.cs` and `TapeLibNET/TapeFilePacker/SyncTapeReadBackend.cs` to `TapeLibNET/Excluded Files/TapeFileReadPacker.cs.txt` and `SyncTapeReadBackend.cs.txt` — files preserved, not deleted.
- Updated doc comments in `ITapeFileReader.cs`, `ITapeReadBackend.cs`, `TapeReadStreamFacade.cs`, and `TapeRestoreAgent.cs` to remove `<see cref>` references to the deleted classes.
- Kept the `[Obsolete]` "Aligned" members (`RestoreNextFileAligned`, `RestoreFilesFromCurrentSetAligned`, `RestoreFileCoreAligned`) and their tests untouched.
- Updated §6, §7.2, §9, §11, and §12.11 of this document to describe the pipelined reader as the current production path.
- **Build:** green. **Tests:** full suite passes.

### Step 9 — Performance validation

- Benchmark sequential restore of a large small-file archive (≥ 10 000 files) before (git checkpoint at end of step 7) and after (post step 8). Target ≥ 1.5× throughput improvement on the dominant case.
- Confirm that ring-full back-pressure does not stall the agent on large-file workloads (single-file restore should match or exceed legacy throughput).
- Verify peak read-session memory: `slotCount × BlockSize` = 16 × 512 KB = 8 MB (acceptable).
- Record results in a short note appended to §12 or in a sibling `Bench-PipelinedRead.md`.

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
| **Pipelined reader** | `TapeFilePipelinedReader` — the worker-thread prefetching read component (current production path for packed restore). Replaced the legacy `TapeFileReadPacker`. |
| **Ring slot** | One `BlockSize`-byte entry in the pipelined reader's prefetch ring buffer; transitions through Empty → Prefetching → Ready → Consumed. |
| **Seek-and-restart** | When `BeginRead` is called with a backward or non-monotonic address, the worker cancels the current prefetch window, seeks, and restarts read-ahead from the new block. |
