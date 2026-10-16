# Design: Shared-Block File Packing and the Content Pipeline

**Scope:** TapeLibNET core — `Packer/`, `Drive/TapeWriteBuffer`, `Media/TapeStreamManager`, the backup and restore agents.
Services and apps see it only through `ITapeFileNotifiable`.
**Status:** v2 — implemented. Describes the code as it stands; the code is the authority.
**Supersedes:** v1, including its aligned (one-file-per-block) path, now removed, and its pipelined-read implementation
plan, now done.

Rules are cited as **P1–P16** (§11). Format rules (`F…`) refer to Design-FormatV21, set-header rules (`SH-…`) to
Design-SetHeader.

---

## 1. What the pipeline does

All file content is written and read through one pipeline, in both directions:

- **Packing.** Files follow each other with no padding; several small files share one tape block. Every file is located
  by a `TapeAddress(Block, Offset)`.
- **Overlap.** On write, the agent fills the next buffer while a worker thread writes the previous one. On read, a worker
  thread prefetches the file's blocks into a ring while the agent consumes earlier ones.
- **Commit decoupling.** A written file is reported — added to the TOC, post-processed — only once its last byte is on
  tape. A file that never reaches the tape is rolled back and reported for what happened to it: continued on the next
  volume (EOM) or lost (write fault).

There is one content path. The aligned one-file-per-block path is gone; the TOC is written through the stream provisioning
of `TapeStreamManager` (`ProduceWriteTOCStream` / `ProduceReadTOCStream`), never through the packer (§7.3).

---

## 2. TapeAddress

```csharp
public readonly record struct TapeAddress(long Block, uint Offset)
{
    public static readonly TapeAddress Zero;     // (0, 0)
    public static readonly TapeAddress Invalid;  // (-1, 0)
    public bool IsValid   => Block >= 0;
    public bool IsAligned => Offset == 0;
}
```

- **Absolute.** `Block` is the drive's logical block number in the content partition — not a set-relative index (P1).
- `Offset` is a `uint`: every buffer and stream API is `int`-indexed, and block sizes stay far below 4 GiB.
- Persisted in exactly one place: the TOC file entry (`AddressBlock`, `AddressOffset`; Design-FormatV21 §5.1).

---

## 3. Layers

```
                 write path                                     read path
                 ──────────                                     ─────────
agent ─────────► TapeWriteStreamFacade                          TapeReadStreamFacade ◄──────── agent
                       │                                                │
TapeStreamManager ─► TapeFileWritePacker                        TapeFilePipelinedReader ◄─ TapeStreamManager
                       │   fill buffer ⇄ in-flight buffer               │   ring of slotCount blocks,
                       │   (TapeWriteBuffer, page-aligned POH)          │   worker thread prefetches
                 ITapeWriteBackend                              ITapeReadBackend
                 WorkerThreadTapeWriteBackend                   WorkerThreadTapeReadBackend
                 (producer push: hand off, harvest later)       (consumer pull: one block per call)
                       │                                                │
                 PackerWriteSink → Drive.WriteDirect            PackerReadSink → Drive.ReadDirect
```

- The two directions share no base class — they share too little.
- **All drive I/O of a content session runs on the backend's worker thread** (P10).
- `TapeStreamManager` owns every packer, backend and pool; agents never construct or dispose them.

---

## 4. Write path

### 4.1 Buffers — `Drive/TapeWriteBuffer`, `TapeWriteBufferPool`

Fill buffers are page-aligned windows over Pinned-Object-Heap arrays:

- **Why page-aligned.** The SPTD write path DMAs straight from a page-aligned buffer, skipping the copy into a native
  aligned scratch buffer. The pool over-allocates by one page and exposes an aligned window of `Capacity` bytes; the
  window's start offset stays private.
- **Why POH.** A POH array never moves, so its address is stable through the worker-thread handoff, and the GC reclaims
  it — no manual free on an exception path. The pool is bucketed by page-rounded capacity and holds a stack per bucket,
  so the packer's double buffering keeps two rentals of one size live.
- **Why a handle, not a `Span`.** `TapeWriteBuffer` must survive buffer rotation and the thread handoff; a ref struct
  cannot be stored. Window operations (`CopyFrom`, `Clear`, `CopyRegionTo`) take 0-based window positions. The write
  glue reaches the backing array and offset through `internal` members only; the SCSI layer detects alignment from the
  pinned pointer, so no flag crosses that boundary.
- Located in `TapeLibNET.Drive`, beside the code whose copy it saves. One pool per content session, owned by
  `TapeStreamManager`; keep the pooled count small — POH is not compacted.

### 4.2 Low layer — `ITapeWriteBackend`

```csharp
internal enum WriteBackendStatus { Idle, Busy }

internal readonly record struct WriteResult(int BlocksWritten, bool EomEncountered, Exception? Exception)
{
    public static WriteResult Empty => default;
    public bool Succeeded => Exception is null;
}

internal delegate WriteResult TapeWriteSink(TapeWriteBuffer buffer, int validBytes);

internal interface ITapeWriteBackend : IDisposable
{
    uint BlockSize { get; }
    void StartWriting(TapeWriteBuffer buffer, int validBytes);   // blocks until the previous write has finished
    WriteBackendStatus PollStatus();                             // non-blocking
    (WriteResult Result, TapeWriteBuffer? Buffer) AwaitCompletion();   // idempotent
}
```

- **One write in flight.** `StartWriting` hands the buffer over and returns; the caller must not touch it until a
  harvest returns it (P9).
- **EOM is a status, not an exception.** A write that reaches end-of-media may have written some blocks first:
  `(K, true, null)`. Those `K` blocks are durable.
- **A hard error rides on the result.** `Exception != null` means the buffer's unwritten suffix is gone. The backend
  classifies nothing and stays usable — error policy lives in the packer and above.
- **`BlocksWritten` is authoritative**, always whole blocks.
- `WorkerThreadTapeWriteBackend` runs the sink on one dedicated thread, gated by two `ManualResetEventSlim`s; a sink that
  throws becomes a result with `Exception`, and a crashed worker posts one final error result so no producer deadlocks.
  `MemoryTapeWriteBackend` wraps the same worker class around an in-memory sink with scripted EOM and hard errors, so
  tests exercise the real concurrency.

### 4.3 High layer — `TapeFileWritePacker`

```csharp
internal sealed class TapeFileWritePacker : IDisposable
{
    public bool IsFileOpen { get; }
    public int  BlockSize  { get; }
    public TapeWriteStreamFacade BeginFile();           // may throw TapePackerException (proactive harvest)
    public CommitToken           EndFile();             // never harvests, never throws a packer exception
    public void                  DiscardOpenFile();     // source error: drop the open file
    public IReadOnlyList<CommitToken> RollbackPending();
    public void                  Flush();               // throws TapePackerException
    public void                  Dispose();             // flushes; rethrows EOM only
    public event Action<IReadOnlyList<CommittedFile>>? FilesCommitted;
}

internal readonly record struct CommitToken(ulong Sequence);
internal sealed record CommittedFile(CommitToken Token, TapeAddress StartAddress, long Length);
```

State:

| Field | Meaning |
|---|---|
| `_fillBuffer`, `_fillPos` | the buffer being filled, and its fill level (may carry a sub-block leftover) |
| `_baseAbsByteOfFill` | absolute tape byte of `_fillBuffer[0]` |
| `_committedTapeBlock` | blocks the backend has reported durable — the commit boundary |
| `_inflightValidBytes` | bytes currently being written (0 when idle) |
| `_pending` | insertion-ordered `{Token, StartAbsByte, Length, IsOpen}` entries not yet committed |

- **Anchored to the head.** `initialAbsBlock` is `Drive.CurrentBlock` at construction, so every address is absolute (P1).
- **The address comes at commit, never before.** `BeginFile` returns a stream; the `TapeAddress` arrives in
  `FilesCommitted` once the file's last byte lies below the commit boundary. No provisional address can move under
  rollback.
- **One open file** between `BeginFile` and `EndFile` (P8).
- **Promotion.** After each harvest, every closed entry with `StartAbsByte + Length ≤ committedBlock × BlockSize` is
  removed and reported, in order.
- **Proactive harvest.** `BeginFile` polls the backend and harvests if it is idle, so an EOM or fault surfaces one file
  late rather than one buffer late, without polling on every `Write`.
- **`Flush`** zero-pads the tail to a block boundary, hands it off, and harvests. No per-file flush — no use case needs
  one.

### 4.4 Buffer handoff

```
fill       ← the agent's Write() calls land here
in-flight  ← the backend writes this one
```

When the fill buffer is full (`WriteFromOpenFile`) or flushed:

1. `validBytes` = the whole-block prefix; the sub-block remainder is held back.
2. **Harvest first** — collect the previous write's result, advance the commit boundary, promote, return its buffer to
   the pool, and surface EOM or a fault.
3. Rent a fresh buffer, copy the held-back remainder to its start.
4. `StartWriting(old fill, validBytes)`; the fresh buffer becomes the fill buffer.

Source ingest into the new buffer overlaps the tape write of the old one — the throughput gain on small-file workloads.

### 4.5 Outcomes of a write — `HarvestNow`

| Backend result | Packer reaction |
|---|---|
| success `(K, false, null)` | advance the boundary by `K`; promote |
| EOM `(K, true, null)` | advance and promote; roll back every closed uncommitted entry; reset the fill buffer to the boundary; throw `TapePackerEndOfMediaException(rolled)` |
| fault `(K, _, ex)` | advance and promote; roll back every closed uncommitted entry; reset the fill buffer; throw `TapePackerWriteFaultException(lost, ex)` |

Files already promoted stay committed whatever follows (P3). After an EOM the packer does not try to re-pack onto the
last committed block: the next volume starts fresh, at the cost of at most one block (P12).

### 4.6 Packer exceptions

```csharp
internal abstract class TapePackerException : TapeIOException
{
    public IReadOnlyList<CommitToken> RolledBackTokens { get; }   // closed, uncommitted; in write order
}
internal sealed class TapePackerEndOfMediaException : TapePackerException;   // ERROR_END_OF_MEDIA
internal sealed class TapePackerWriteFaultException : TapePackerException    // the drive's own code and message
{
    public Exception Cause { get; }
}
```

- **Two classes, one base.** The reactions are opposite — continue on the next volume, or end the set — so they are
  caught in separate clauses (P5). The base serves cleanup paths that treat both alike.
- **`TapeIOException`s.** The Win32 code travels in `HResult`, so `SetError(ex)`, `TapeResult.Fail(ex)` and the service
  read the real cause off the exception. A write fault takes the code of its cause: a `TapeIOException` from the sink keeps
  its own; anything else is classified as everywhere else in the solution.
- **The open file is never in `RolledBackTokens`** — its writer discards it.

### 4.7 Source errors — `DiscardOpenFile`, `SourceErrorMode`

A source file that fails mid-copy (locked, vanished, unreadable) is discarded; the files before it in the fill buffer stay.

| Mode | Open file's bytes still in the fill buffer | Some already flushed |
|---|---|---|
| `NoRollback` (default) | truncate the fill buffer to the file's start | truncate what is buffered; flushed bytes stay on tape as anonymous garbage (no TOC entry, no token); no head movement |
| `Rollback` | same | drain the backend, locate to the first block after the last committed byte, discard the fill buffer |

In the common small-file case both behave identically. `Rollback` suits archives of very large files, where one failure
would otherwise waste many MiB. The mode is per packer, set through `TapeStreamManager.PackerSourceErrorMode`.

### 4.8 Write façade

`TapeWriteStreamFacade` forwards `Write` to the packer, counts `Length`, treats `Flush` as a no-op (the packer decides when
blocks go out), exposes `CommitToken`, and becomes inert after `EndFile`, `DiscardOpenFile` or disposal.

---

## 5. Read path

### 5.1 Low layer — `ITapeReadBackend`

```csharp
internal readonly record struct ReadResult(int BytesRead, bool TapemarkEncountered, bool EofEncountered,
    Exception? Exception);

internal delegate ReadResult TapeReadSink(byte[] buffer, int offset);   // fills one block at buffer[offset..]
internal delegate bool       TapeReadSeek(long blockNumber);

internal interface ITapeReadBackend : IDisposable
{
    uint BlockSize { get; }
    bool       MoveToBlock(long blockNumber);
    ReadResult ReadOneBlock(byte[] buffer, int offset);
}
```

- **A single-block transport.** The reader above decides how many blocks to read and when; `offset` lets the backend fill
  a ring slot in place, with no intermediate copy.
- **Tape conditions are flags, never exceptions** — tapemark, end of data; only a hard error sets `Exception`.

`WorkerThreadTapeReadBackend` serializes reads and seeks on one dedicated thread through a single request slot with a
`Read`/`Seek` discriminator, so a seek can never interleave with a read. The model is **consumer pull**: the call blocks
until the worker delivers; the overlap happens one layer up.

**Seek skipping.** The backend tracks the head as `_drivePositionBlock`, advanced by every full-block read and reset to
unknown (−1) by a tapemark, end of data, error or failed seek. `MoveToBlock` to the tracked block skips the physical
locate. The position starts unknown, so the first read of a session always follows a real seek — which the reader issues
on every cache miss (§5.3).

### 5.2 High layer — `TapeFilePipelinedReader`

```csharp
internal interface ITapeFileReader : IDisposable
{
    int  BlockSize  { get; }
    bool IsFileOpen { get; }
    TapeReadStreamFacade BeginRead(TapeAddress addr, long length);
    void                 EndRead();     // keeps the ring for the next file
}
```

- A ring of `slotCount` blocks (default 16 = `PackerBlockMultiplier`, at least 2), one contiguous array rented from
  `ArrayPool<byte>.Shared`; slot *i* at `[i × BlockSize …)`.
- Per slot: `State ∈ {Empty, Prefetching, Ready}`, `Block`, `ValidBytes`, `EndOfStream`, `Error`. A consumed slot goes
  straight back to `Empty`.
- Strict FIFO: `_producerIndex` (next slot to fill), `_consumerIndex` (next slot to read).
- One dedicated worker thread; all coordination through one `Monitor` on `_lock` with `Wait`/`PulseAll` — no semaphores,
  no cancellation token. Shutdown is a flag under the same lock.
- The worker calls `ReadOneBlock` **outside** the lock, so the consumer drains Ready slots meanwhile.

### 5.3 Arming a file — `BeginRead`

`BeginRead(addr, length)` computes the file's byte range and an exclusive end block,
`((startAbs + length − 1) / BlockSize) + 1` (a zero-length file needs no block), then:

- **Seek-and-resume (cache hit).** The start block is at or after the consumer head and either already Ready in the ring
  or below the worker's next block. The consumer skips Ready slots before it, the end bound is re-armed, prefetch
  resumes if halted. No tape I/O — the common case of consecutive packed files sharing a block.
- **Seek-and-restart (miss).** Otherwise — a backward selective restore, or a jump far ahead: wait until no slot is
  `Prefetching`, flush the ring, post a seek to the start block, re-arm.

### 5.4 The per-file prefetch bound (P13)

The worker never reads at or beyond the armed file's end block; it parks there until the next `BeginRead`. `EndRead`
drops the bound and halts prefetch while **keeping** the ring, so the next file can hit it.

The bound exists because the block after a file's payload may be the set's closing setmark. Reading it moves the head past
the mark; on partitioned media the next set transition then fails. Greedy prefetch to the next tapemark is not an option.

### 5.5 Worker and consumer

Worker loop, under the lock: wait for shutdown, a pending seek, or (prefetch armed, below the bound, producer slot Empty).
A seek runs outside the lock; a failed one publishes an **error slot** and halts. A read reserves the slot as
`Prefetching`, reads outside the lock, then publishes it `Ready` — unless the ring was flushed meanwhile, in which case
the result is discarded. A tapemark, end of data, short read or error halts prefetch until the next `BeginRead`.

Consumer (`ReadIntoOpenFile`, called by the façade): wait until the head slot is Ready; if its block is not the one
expected, a restart is in flight — return what has been read so far. An error slot throws `IOException` wrapping the
cause. A short or end-of-stream block ends the read. A fully consumed slot is released and the worker pulsed.

`TapeReadStreamFacade` honours the start offset through the absolute byte cursor, tracks `Position`, ignores `Flush`, and
calls `EndRead` on disposal.

---

## 6. `TapeStreamManager` integration

### 6.1 Write session

`BeginWriteContent(remainingCapacity)` transitions to `WritingContent`, then `EnsurePackerCreated`:

- `WorkerThreadTapeWriteBackend(PackerWriteSink, Drive.BlockSize)`, a `TapeWriteBufferPool`, and
  `TapeFileWritePacker(backend, rewindToBlock: Drive.MoveToBlock, PackerBlockMultiplier, PackerSourceErrorMode,
  initialAbsBlock: Drive.CurrentBlock, pool)`.
- The block size is captured **here** — the agent must set it first (P2).
- `OnPackerFilesCommitted` is subscribed; the manager re-exposes it as its own `FilesCommitted`, so subscriptions survive
  session cycles.

`BeginPackedFile` / `EndPackedFile` delegate to the packer. `BeginPackedFile` rethrows a packer EOM (with
`ERROR_END_OF_MEDIA`) and a write fault (with the drive's error) — the agent needs the tokens; any other failure returns
`null` with `ERROR_INVALID_STATE`.

**`PackerWriteSink`** bridges to `Drive.WriteDirect`:

- **Early-warning stop on TOC-in-set media.** The drive's logical early warning is armed by the agent with the TOC
  reserve; the sink reports it as EOM, so the packer rolls back the tail and the TOC still fits. With the TOC in a
  partition, early warning is not a stop condition — only real EOM is.
- `ContentCapacityLimit` (a test knob) writes what still fits, block-aligned, then reports EOM.
- A drive error other than EOM is returned as `WriteResult.Exception`, a `TapeIOException` carrying the Win32 code.

### 6.2 Ending a write session — `EndWriteContent`

```
FlushAndDisposePacker   → packer.Flush()   — the one place a write fault can be taken (P6)
                          packer.Dispose() — nothing left to flush; releases buffers
                          unsubscribe FilesCommitted (after the final flush, P7); dispose backend and pool
EndWriteContentSet      → the closing setmark — or, on TOC-in-partition media, end-of-media itself (F32)
State → MediaPrepared   — always (P4)
then: rethrow a write fault (error set to the drive's cause) or an EOM; else return whether the set is closed
```

- **Why flush before dispose.** `TapeFileWritePacker.Dispose` must not throw, so it swallows every non-EOM error of its
  internal flush. Calling `Flush` first is the only way to take a write fault. After a fault or an EOM the packer has
  rolled back and reset its fill buffer, so the flush inside `Dispose` writes nothing.
- **The set is closed before anything is rethrown.** Files committed before an EOM or a fault are in the TOC; on a
  TOC-in-set layout the closing mark keeps end-of-content — and so the TOC — from landing on top of them. A faulting
  drive may refuse the mark too; nothing is then lost that was not lost already.
- **The fault is carried as a value.** The close resets the error channel (the drive's included), so the fault must not
  depend on shared error state — it travels in the exception, and its error is set after the close.
- **The navigator is not reset on a fault.** `EndWriteContentSet` has already settled it: past the mark if one was
  written, reset if not. A fault in the data says nothing against a mark written afterwards.
- **`LastSetClosedByEom`** reports a set closed by end-of-media; the agent records it in the TOC (F32,
  Design-FormatV21 §9.7). Only a new write session clears it, so it survives the idempotent second `EndWriteContent` of the
  EOM path.

### 6.3 Read session

- `BeginReadContent` transitions to `ReadingContent`. Crossing to another set while already reading **disposes the read
  packer first** — its ring belongs to the previous set (P14).
- `BeginPackedFileRead(addr, length)` creates the reader lazily — `WorkerThreadTapeReadBackend(PackerReadSink,
  Drive.MoveToBlock, Drive.BlockSize)` plus `TapeFilePipelinedReader(backend, PackerBlockMultiplier)` — and delegates.
  Lazy creation is what opens the set-header window: `ReadSetHeaderBlock` refuses while any packer exists (SH-7, SH-17).
- `EndReadContent` disposes the reader, moves past the set's closing mark, and always leaves `ReadingContent`. A set with
  no mark ahead (closed by end-of-media, or a tail a backup never closed) runs into end of data: the read is complete, the
  position unknown, and that is not an error.

---

## 7. Agents

### 7.1 Backup — `TapeFileBackupAgent`

**Preparing a set (`BeginWriteContentForCurrentSet`).** Order is load-bearing: media-header presence; end any session;
target set; **block size before the session** (P2); hardware-compression interlock; early-warning reserve (TOC-in-set
only); positioning, set-header verification and header writes (SH-4, SH-13, SH-15, SH-20); then
`Manager.BeginWriteContent`, which creates the packer anchored after both headers.

**One file (`BackupFile`).** `Manager.BeginPackedFile()` → the per-file header through the agent's own emitter (`RecordEmitter.WriteFileHeader`) - no allocation per file
(2.1 frame, or the legacy header on the test emitter) → the codec byte (software compression: `ProbingCompressionStream`
decides and writes it; otherwise `Stored`) → the body via `TapeBackupSourceStream` (all NTFS streams, through `BackupRead`),
hashed uncompressed → `Manager.EndPackedFile()` returns the token. On any exception: `Packer.DiscardOpenFile()` — never a
`MoveToBlock`, which would clobber earlier files still in the fill buffer (P11).

**The set loop (`BackupFilesToCurrentSet`)** keeps its books in `PackedCommitTracker`:

| Verb | Use |
|---|---|
| `Register` | a file handed to the packer: template, file index, hash, codec |
| `OnCommitted` | wired to `Manager.FilesCommitted`: builds the real `TapeFileInfo` (address, `SizeOnTape`, codec), appends it to the TOC, queues it |
| `DrainPostProcess` | `NotifyPostProcessFile` for queued files, on the loop thread; `false` only on an abort |
| `RemoveRolledBack` | EOM: drop the tokens, return the earliest file index to resume from |
| `TakeRolledBack` | write fault: drop the tokens, return their templates in file order to report |

The subscription spans the loop **and** the final `EndWriteContent`, whose flush commits the tail; it ends in `finally`.

**Three ways a packed file can fail:**

| Cause | Surfaces as | Reaction |
|---|---|---|
| **Source error** — the source file | any exception inside `BackupFile` | `DiscardOpenFile`; `OnFileFailed` asks Retry / Skip / Abort; `ignoreFailures` decides whether the set goes on |
| **End of media** | `TapePackerEndOfMediaException` — in the loop, or from `EndWriteContent` | `HandleEom`: drop the rolled-back tokens, undo skips in the range, snapshot `continuationSetParams`, resume index = the earliest rolled-back file; close the session; `ContinuedOnNextVolume`; no file is reported failed |
| **Media write fault** | `TapePackerWriteFaultException` — in the loop, or from `EndWriteContent` | `HandleWriteFault`: latch the drive's error; report every rolled-back file, plus the file being written, through `OnFileLost` — no prompt, counted failed; end the set |

- **A write fault ends the set (P5).** The drive has retried before reporting it; writing on behind the failed region
  would scatter the next files over damaged tape. `ignoreFailures` governs source errors only.
- **Every file started is accounted for (P15):** committed (in the TOC, post-processed) or reported — failed, skipped,
  lost, or rolled back to the next volume.
- In `HandleEom`, a second packer exception during its own `EndWriteContent` is caught by the base class and ignored: the
  files are already rolled back and continue on the next volume.

**Full volume before the set (F32).** On TOC-in-partition media, when the previous set on the volume ran to end-of-media,
the new set goes straight to the next volume — appending would overwrite that set.

### 7.2 Notifications

| Callback | When |
|---|---|
| `SetStart` / `SetEnd` | once per set — a continuation is a new set on the new volume |
| `PreProcessFile` | before a file is written; may repeat for one file across Retry and EOM rollback |
| `OnFileSkipped` | pre-processor, incremental up-to-date |
| `PostProcessFile` | **deferred** until the file is committed and the queue drained — may lag its start by many files (P16) |
| `OnFileFailed` | source error — asks |
| `OnFileLost` | media write fault — reports; default no-op, so existing implementers compile |

`TapeFileStatistics` counts a lost file as failed. `BytesBackedup` grows by the per-file stream length after each body;
`FileBytesProcessed` and `BytesOnTapeProcessed` grow at commit.

### 7.3 The TOC

`BackupTOC` / `RestoreTOC` use `ProduceWriteTOCStream` / `ProduceReadTOCStream`: a `TapeWriteStream`/`TapeReadStream`
over the TOC area, at the fixed 16 KiB TOC block, one filemark after each copy. The TOC is never packed and never passes
through a backend thread. `ProduceWriteContentStream` / `ProduceReadContentStream` remain for tests only.

### 7.4 Restore — `TapeFileRestoreBaseAgent`

`BeginReadContentForCurrentSet` navigates explicitly (SH-20), opens the read session, verifies the set header before the
first `BeginPackedFileRead` (SH-7, SH-8), then applies block size and the compression interlock. Per file
(`RestoreNextFile`):

- **Length:** `SizeOnTape` (header + codec + body) when recorded; else the set's `EstimateFileOverhead` plus the file
  length.
- `Manager.BeginPackedFileRead(address, length)` → check the per-file header in the set's data format (2.1 frame: CRC,
  `SetId`, `FileId`; legacy: the 12-byte header against the `FileId`) → read the codec byte (2.1; it must equal the TOC's)
  → `RestoreFileCore` over a plain `Stream` (`DecompressionFilterStream` for Zstd) → hash check against the TOC.
- `TapeFileRestoreAgent`, `TapeFileValidateAgent` and `TapeFileVerifyAgent` implement only `RestoreFileCore`; block
  boundaries stay invisible to them.
- Restore order is set by set, oldest first, so the tape moves forward; a backward selective restore is the reader's
  seek-and-restart.

---

## 8. What the pipeline does not do

- **No retry of tape writes** at file level (P5).
- **No re-packing after EOM** — at most one block lost per EOM (P12).
- **No file-end markers between files** — files follow back to back; locating them needs the TOC, or Scan Media for a
  set's first file (Design-ScanMedia §4.2).
- **No read prefetch past a file's last block** (P13).
- **No OVERLAPPED I/O** — each backend runs one synchronous call at a time on its own thread.

---

## 9. Tests

| Area | Suites |
|---|---|
| Backends | `TapeWriteBackendTests`, `TapeReadBackendTests`, `WorkerThreadTapeReadBackendTests` |
| Packer / reader | `TapeFileWritePackerTests`, `TapeFileWritePackerWriteFaultTests`, `TapeFilePipelinedReaderTests`; pooled-buffer helper `PooledTestBuffers` |
| Buffers | `Drive/TapeStreamBufferTests` |
| Manager | `Media/TapeStreamManagerTests` |
| Agents | `TapeBackupAgentPackedTests`, `TapeRestoreAgentPackedTests`, `TapeRestoreAgentPipelinedTests` (selective, backward-seek, cross-set and back-pressure scenarios × four profiles), `ErrorHandlingTests` (incl. media write faults: diagnosis, every file accounted for), `StatisticsTests` |
| End to end | `BackupRestoreRoundTripTests`, `LargeFileTests`, `FileEdgeCaseTests`, `MultiVolumeBackupRestoreTests`, `EomClosedSetTests` |

Write faults are injected at the medium (`VirtualMediaFaultInjector`, `ContentWriteFaults`), so the whole chain
medium → drive → sink → packer → manager → agent is exercised.

---

## 10. Open items

| Item | Note |
|---|---|
| Benchmark of the pipelined read path | sequential restore of ≥ 10 000 small files against the pre-pipeline code, and large-file throughput under ring back-pressure; peak ring memory is `slotCount × BlockSize` (16 × 512 KiB = 8 MiB). Not recorded yet. |
| OVERLAPPED I/O for the write backend | only if profiling shows the worker thread as the bottleneck |
| Read ring on POH | the read ring comes from `ArrayPool`; a page-aligned ring would let the read path skip the native copy as the write path does |

---

## 11. Rules

| Rule | Statement |
|---|---|
| **P1** | Addresses are absolute: the write packer is anchored at `Drive.CurrentBlock` when the session opens, after any headers. |
| **P2** | The set's block size is applied before `BeginWriteContent`; the packer captures it at construction. |
| **P3** | A file is committed only when its last byte lies below the backend-reported boundary; committed files stay committed through any later EOM or fault. |
| **P4** | `EndWriteContent` always leaves `WritingContent`, and closes the set before rethrowing. |
| **P5** | A media write fault ends the set; every file it took is reported lost, without a prompt. `ignoreFailures` governs source errors only. |
| **P6** | The final flush is explicit (`Flush` before `Dispose`); a packer exception is carried as a value, never left in shared drive error state. |
| **P7** | `FilesCommitted` stays subscribed through the final flush; the manager unsubscribes only after it. |
| **P8** | At most one open file, on both the write packer and the reader. |
| **P9** | A buffer handed to the write backend is not touched until a harvest returns it. |
| **P10** | All drive I/O of a content session runs on the backend's worker thread; set-header I/O happens only while no packer exists (SH-7, SH-17). |
| **P11** | A per-file source failure discards the open file only; it never moves the head. |
| **P12** | No re-packing onto the last committed block after an EOM. |
| **P13** | The reader never prefetches past the armed file's last block. |
| **P14** | Crossing to another set disposes the read packer first. |
| **P15** | Every file the agent starts is accounted for: committed, failed, skipped, lost, or rolled back to the next volume. |
| **P16** | `PostProcessFile` fires only after commit; `PreProcessFile` may fire more than once per file. |

---

## 12. Glossary

| Term | Meaning |
|---|---|
| **Address** | `(Block, Offset)` of a file's first header byte on tape |
| **Packing** | file *N+1* starts right after file *N*, inside the same block |
| **Open file** | the one file between `BeginFile` and `EndFile` |
| **Pending** | closed in the packer, not yet below the commit boundary |
| **Committed** | below the boundary and reported through `FilesCommitted` |
| **Commit boundary** | `_committedTapeBlock` — blocks the backend reported durable |
| **Rolled back** | pending files dropped by EOM or a write fault, reported through the exception's tokens |
| **Lost** | a rolled-back file of a write fault — reported via `OnFileLost` |
| **Discard** | dropping the open file after a source error |
| **Harvest** | collecting the previous write's result: advance, promote, return the buffer, surface EOM or a fault |
| **Ring slot** | one block of the reader's prefetch ring: `Empty → Prefetching → Ready → Empty` |
| **Seek-and-resume / -restart** | `BeginRead` served from the ring, or by flushing it and seeking |
