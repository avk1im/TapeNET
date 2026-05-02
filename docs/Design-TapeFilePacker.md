# TapeNET Design: Shared-Block File Packing (`TapeFilePacker`)

**Status:** Draft for review · **Branch:** `dev` · **Author:** design dialogue with Copilot
**Scope:** TapeLibNET core + agents + Services + apps · **No on-tape compatibility required**

---

## 1. Goals & Non-Goals

### Goals
1. Eliminate intra-block padding waste so multiple files can share a tape block.
2. Replace `long Block` with `TapeAddress(long Block, uint Offset)` everywhere a file's tape position is recorded.
3. Remove `FmksMode` (filemarks-per-file mode) — unsupported on LTO, rarely used, slower than BLOB mode, no longer worth its plumbing cost.
4. Introduce a packing/buffering layer (`TapeFilePacker`) between `TapeStreamManager` and `TapeDrive`, designed from day one with an interface shape that admits an asynchronous backend later.
5. Decouple "file written to packer" from "file committed to tape" in the agents, with deterministic rollback on failure.
6. Keep the public agent API and the `ITapeFileNotifiable` contract **unchanged** (only the *timing* of `PostProcess` shifts to commit-time).
7. Minimal blast radius into `TapeLibNET.Services` and apps (cosmetic + FmksMode removal only).

### Non-Goals (this design)
- On-tape backward compatibility (explicitly waived). Increase the serializer version to expictily disallow old TOCs.
- Asynchronous / overlapped I/O — deferred to a follow-up phase, but the API is shaped to allow it.
- Read-ahead and producer/consumer pipelining — deferred (Phase 3); Phase 2 read side is synchronous with a small block cache.
- Encryption, compression, or any change to the on-tape file body format beyond removing per-file alignment.

---

## 2. Phasing Overview

| Phase | Theme | Ships independently? |
|-------|-------|----------------------|
| **1. Foundation** | `TapeAddress` + drop `FmksMode`. No packer yet (every address has `Offset = 0`). All consumers updated: TOC, serializers, agents, services, both apps, existing tests. | ✅ Yes — produces a working release with identical behavior, narrower API. |
| **2. Packing** | Introduce `TapeFilePacker`. Agents move to "write → commit" decoupling. Synchronous backend. New rollback state machine. | ✅ Yes — delivers the user-visible tape-savings win. |
| **3. Async backend** *(future)* | `IPackerIoBackend` extraction; thread-pool backend first; Win32 OVERLAPPED IO only if measurements justify. Optional read-ahead pipeline. | ✅ Yes — pure backend swap behind the Phase 2 interface. |

The rest of this document specifies **Phase 1** and **Phase 2** in detail. Phase 3 appears only as an interface-shape constraint on Phase 2.

---

## 3. Phase 1 — Foundation (`TapeAddress`, drop `FmksMode`)

### 3.1 New type: `TapeAddress`

```text
public readonly record struct TapeAddress(long Block, uint Offset)
{
    public static readonly TapeAddress Zero;        // (0, 0)
    public static readonly TapeAddress Invalid;     // (-1, 0)  -- sentinel for "not set"

    public bool IsValid    => Block >= 0;
    public bool IsAligned  => Offset == 0;          // true for any Phase 1 address

    public override string ToString();              // "1234"          if Offset == 0
                                                    // "1234:5678"     otherwise

    public static TapeAddress Parse(string s);      // for diagnostics / CLI input
}
```

- **`uint Offset` (not `long`)** — see §3.2 rationale.
- `readonly record struct` — value semantics, no allocation, free `Equals`/`GetHashCode`/`ToString`.
- `Invalid` is used internally by the agent (e.g. for files in the pending-commit queue before they have an address — Phase 2 only).
- `ToString` collapses to bare block when offset is zero, so Phase 1 logs/UIs look identical to today.

### 3.2 Why `uint Offset`, not `long`
- All current buffers, `Span<byte>`, `Memory<byte>`, `Stream.Read/Write` count are `int`-indexed. A `long Offset` cannot be used to index any actual buffer.
- LTO-10 maximum logical block size is in the low MiBs; LTO-11/12 roadmap does not project breaking the 4 GiB barrier in any realistic horizon.
- 4 bytes saved per TOC entry × millions of files in large archives = real serialized-TOC size savings.
- If the assumption ever breaks, widening `Offset` is a one-shot TOC-format-version bump — same migration cost as we're paying now.

### 3.3 TOC / serialization changes
- Add `TapeSerailizer.Serialize(TapeAddress)` and `TapeDeserializer.DeserializeTapeAddress()`.
- `TapeFileInfo.Block (long)` → `TapeFileInfo.Address (TapeAddress)`.
- `SerializeTo` writes `Block` (8 B) + `Offset` (4 B) instead of `Block` (8 B). +4 B per entry.
- `EstimateSerializedSize` adjusts by +4.
- `ConstructFrom` reads both fields.
- **TOC format version bumped once for both the `TapeAddress` change and the `FmksMode` removal.** Reading an older TOC is unsupported — agents return a clear "TOC format too old" diagnostic and refuse to mount.
- `EstimateSerializedHeaderSize` unchanged (header carries only signature + UID).

### 3.4 `FmksMode` removal — surface map

| File / area | Action |
|-------------|--------|
| `TapeSetTOC.FmksMode` field, serialization | Remove field, drop from serializer, bump version. |
| `TapeNavigator.FmksMode` | Remove property and all setter logic. |
| `TapeStreamManager` calls referencing `FmksMode` | Remove. The "write trailing filemark per file" path stays (still needed in Phase 1 — one filemark per *file*, as today; Phase 2 changes this — see §4.7). |
| `TapeFileBackupAgent.BeginWriteContentForCurrentSet` | Remove the FmksMode set/log block. |
| `TapeFileRestoreAgent` | Remove any FmksMode-conditional positioning. |
| `TapeLibNET.Services` (`TapeServiceBase.Backup.cs` etc.) | Remove FmksMode parameter from `ServiceOperationRequest` (or whichever DTO carries it). |
| `TapeConNET` CLI | Drop `--fmks` / equivalent flag; update help text. |
| `TapeWinNET` UI | Remove FmksMode option from BackupWindow (or wherever exposed). Remove from settings persistence. |
| `TapeLibNET.Tests`, `TapeConNET.Tests` | Remove tests asserting FmksMode behavior; update tests parameterized over FmksMode. |

Notice: `TapeFileAgent` and `TapeStreamManager` continue using filemarks for storing / reading TOC: Each of the two TOC copies on tape has a filemark written at its end.

### 3.5 Agent reductions in Phase 1
With FmksMode gone, `BeginWriteContentForCurrentSet` simplifies; with `TapeAddress` (offset always 0), there are zero behavioral changes elsewhere — every site that constructed `new TapeFileInfo(uid, blockCounter, fileInfo)` becomes `new TapeFileInfo(uid, new TapeAddress(blockCounter, 0), fileInfo)`. Rewind logic still uses `Drive.MoveToBlock(tfi.Address.Block)`.

### 3.6 App / Services changes in Phase 1
- Replace any displayed `Block` with `Address.ToString()`. Because Offset is always 0 in Phase 1, output is unchanged.
- File-list export formats (CSV/text dumps) gain the `:offset` suffix only when non-zero — Phase 1 listings remain visually identical.
- Remove FmksMode UI (BackupWindow option, backup set displayed property, CLI flag, settings keys).
- WPF tree/info panes that show "Block N" become "Address N" (or "N:0" — pick the column header per UI consistency; recommend "Address" header, value rendered by `TapeAddress.ToString()`).

### 3.7 Phase 1 exit criteria
- All projects build.
- All existing tests pass after FmksMode removal and `Address` renames.
- Backup → Restore round-trip on `VirtualTapeDriveBackend` is byte-exact.
- Backup → Restore round-trip on real LTO (manual smoke test) succeeds.
- TOC files written in Phase 1 are readable by Phase 1 (no cross-version requirement).

---

## 4. Phase 2 — `TapeFilePacker` and Commit Decoupling

### 4.1 Naming & placement
- Class: **`TapeFilePacker`** (per your preference — caller-facing semantics is per-file, not per-block).
- Namespace: `TapeLibNET` (same as `TapeStreamManager`).
- File: `TapeLibNET/TapeFilePacker.cs`.
- Owned by `TapeStreamManager` as a private member, instantiated when the manager transitions into content read or content write mode, disposed when leaving that mode.
- **Not** a `Stream` subclass. The packer's lifetime spans many files; `Stream` shape would invert ownership.
- `TapeWriteStream` and `TapeReadStream` become thin façades that forward to the packer for the single file they currently represent.

### 4.2 Buffer sizing policy
- Write buffer: `max(N × BlockSize, MinBufferBytes)` where `N` is configurable (default 16) and `MinBufferBytes` defaults to 16 MiB.
- Read buffer: same formula. (Phase 3 may grow it for read-ahead.)
- Buffer is a single contiguous `byte[]` allocated once per session and reused across all files in the session.
- Employ `ByteBufferCache` for buffer allocation and management via `ArrayPool<byte>`.

### 4.3 `TapeFilePacker` API surface

The API is shaped now to admit a future async backend without reshaping callers. Phase 2 implementations are synchronous; method names are `Async`-suffixed where Phase 3 will plausibly need to await, returning `ValueTask` even when the implementation is currently synchronous.

#### 4.3.1 Lifecycle

```text
internal sealed class TapeFilePacker : IDisposable
{
    internal TapeFilePacker(TapeStreamManager mgr, uint blockSize, int blockMultiplier);

    // Mode switching — mutually exclusive. Must be called before any Begin* call.
    // The tape is assumed in the right position to start the respective mode.
    // The blockSize remains valid for the whole session.
    internal void EnterWriteMode(uint blockSize);
    internal void EnterReadMode(uint blockSize);

    // Returns the drive to a clean state. For write mode: flushes the trailing
    // partial block (zero-padded), drains pending commits, returns the final list.
    internal IReadOnlyList<CommittedFile> EndWriteMode();
    internal void EndReadMode();

    public void Dispose();
}
```

#### 4.3.2 Write side

```text
// Open a logical write slot for one file. Returns the TapeAddress where
// the file's first byte will land once committed.
internal TapeAddress BeginFile(TapeFileInfo tfi);

// Append bytes to the currently-open file. May trigger zero or more
// internal block flushes to the drive; flushes never split a file's
// in-buffer data — they only emit fully-written prefix blocks.
internal ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

// Mark the currently-open file complete. Adds it to the pending-commit
// queue. Does NOT necessarily flush — the file's tail may sit in the
// buffer until the next file (or EndWriteMode) fills the block.
// Returns a token the agent stores alongside the file for correlation
// with the eventual CommittedFile entry.
internal CommitToken EndFile();

// Discard the currently-open file's bytes from the buffer. Used when
// a file fails *before* EndFile is called (source read error, etc.).
// Does NOT touch already-committed files. Cheap — no tape I/O.
// Resets the open-file write head to the address returned by BeginFile.
internal void RollbackOpenFile();

// Roll back ALL pending-commit files (those EndFile'd but not yet on tape).
// Drops their bytes from the buffer and repositions the drive to the last
// committed block. Returns the list of rolled-back tokens so the agent can
// re-queue them. Used on EOM (end-of-media).
internal IReadOnlyList<CommitToken> RollbackPending();

// Force-flush the buffer up to and including the file identified by the
// token (zero-padding the trailing partial block of the LAST file in the
// flush, if any). After this returns, all files up to `upTo` are
// committed and reported via the CommittedFiles event.
// Used when the agent needs a hard durability point (e.g. set close).
internal ValueTask FlushAsync(CommitToken upTo, CancellationToken ct);

// Event fired (synchronously, on the calling thread in Phase 2) whenever
// one or more files cross the commit boundary as a side effect of WriteAsync,
// EndFile, FlushAsync, or EndWriteMode.
internal event Action<IReadOnlyList<CommittedFile>>? FilesCommitted;
```

```text
internal readonly record struct CommitToken(ulong Sequence);

internal sealed record CommittedFile(
    CommitToken Token,
    TapeFileInfo File,                   // carries the resolved Address
    long BytesOnTape);                   // = file body length; padding excluded
```

**Invariant:** `BeginFile`'s returned address is *provisional* in the sense that the file is not on tape yet, but it is **stable** — the address will be the same when the file is later reported in `FilesCommitted`. The agent may stamp `tfi.Address` immediately at `BeginFile`-time. This works because the packer never reorders files and never changes the starting position of an already-opened file, except via `RollbackOpenFile` / `RollbackPending` (which by definition discard the address).

#### 4.3.3 Read side

```text
// Open a logical read slot for one file at the given address & length.
// Triggers a drive seek + read into the block cache only if the block
// at addr.Block is not currently buffered.
internal ValueTask BeginReadAsync(TapeAddress addr, long length, CancellationToken ct);

// Read up to `buffer.Length` bytes from the current file. Returns 0 on EOF
// (i.e. when `length` bytes have been consumed). May trigger sequential
// block reads from the drive when crossing block boundaries.
internal ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);

// Close the current read slot. Bytes still in the cache are retained for
// the next BeginReadAsync (the next file likely starts in the same or the
// next block).
internal void EndRead();
```

#### 4.3.4 What the façade streams expose
- `TapeWriteStream`: holds `(packer, currentToken)`. `Write` → `packer.WriteAsync(...).AsTask().GetAwaiter().GetResult()` in Phase 2 (sync today, awaitable when Phase 3 lands). `Dispose` → `packer.EndFile()`.
- `TapeReadStream`: holds `(packer, expectedLength)`. `Read` → `packer.ReadAsync(...)`. `Dispose` → `packer.EndRead()`.

### 4.4 Read-side caching policy (Phase 2)

Minimal but principled — designed to extend cleanly to Phase 3 read-ahead.

- **Cache structure:** a small ring of `K` block-sized slots, where `K` matches the write buffer's `blockMultiplier` (default 16). Each slot tracks `{ blockNumber, validBytes, lastAccessTick }`.
- **Lookup:** `BeginReadAsync(addr, len)` checks if `addr.Block` is in any slot. Hit → serve from buffer, bumping `lastAccessTick`. Miss → seek drive to `addr.Block`, read into LRU slot, set `validBytes` from drive.
- **Sequential extension:** when `ReadAsync` crosses into `addr.Block + 1`, check cache; if miss, read into LRU, **assuming forward sequential** (no seek needed because the drive head is already there).
- **No prefetch in Phase 2.** The cache only fills on demand. Phase 3 adds a background prefetcher that reads ahead by up to `K-1` blocks while consumer reads.
- **Eviction:** LRU. Never evict the slot currently being served from.
- **Cross-block files:** handled entirely inside the packer; the agent does not see block boundaries on read.
- **Out-of-order reads (e.g. selective restore):** supported, even if not desired: the caller is encouraged to sort the accesses — `BeginReadAsync` will seek backward if needed. Cache hit/miss logic is identical. Phase 3 prefetcher will be disabled when the agent enqueues non-monotonic addresses.

### 4.5 Rollback state machine

The packer maintains three logical positions:

| Position | Meaning |
|----------|---------|
| `committedTapeBlock` | Highest block number known to be physically on tape. Drive head is at or after this block. |
| `bufferedThruAddress` | Address one byte past the last byte sitting in the write buffer (committed-or-not). |
| `openFileStartAddress` | Address where the currently-open file's `BeginFile` returned. `Invalid` when no file is open. |

State transitions (write mode):

```text
              ┌──────────────────────────────────────┐
              │              Idle                    │
              │  (no open file, buffer may be        │
              │   non-empty with pending commits)    │
              └──────────────────────────────────────┘
                  │   │              ▲           ▲
       BeginFile  │   │ EndFile      │           │ FlushAsync
                  ▼   │              │           │ (drains pending)
              ┌──────────────────────────────────────┐
              │           OpenFile                   │
              │  WriteAsync may flush full blocks.   │
              │  RollbackOpenFile -> Idle (truncate  │
              │  buffer to openFileStartAddress).    │
              └──────────────────────────────────────┘
                                ▲
                                │ RollbackPending (Idle):
                                │   truncate buffer to last
                                │   committed boundary,
                                │   drive seek to committedTapeBlock,
                                │   return rolled-back tokens.
```

**Critical invariants:**
1. A file can be in exactly one of: *open*, *pending commit*, *committed*, *rolled back*, *never existed*.
2. Once `FilesCommitted` fires for a token, that file is durably on tape and cannot be rolled back.
3. `RollbackOpenFile` is only valid when a file is open. It does not touch pending-commit files.
4. `RollbackPending` is only valid when no file is open. It rolls back every pending file at once.
5. The packer never partially flushes a file's tail past its `EndFile` boundary unless either (a) a subsequent `WriteAsync` from the next file fills the block, or (b) `FlushAsync`/`EndWriteMode` is called. Otherwise, a tail sits in the buffer indefinitely.
6. After `RollbackPending`, the drive head sits at `committedTapeBlock`. The next `WriteAsync` will resume packing into that block at offset 0 — meaning the packer must re-read the last partial block from tape if `committedTapeBlock` itself contained part of a committed file *and* we want subsequent files to share it. **Phase 2 simplification:** we *do not* re-pack onto the last committed block after a rollback. After `RollbackPending`, the packer treats `committedTapeBlock + 1` as the next write target, which means the last committed block is zero-padded. This costs at most one block per EOM event (negligible) and avoids a tape-read-during-write hazard. Document this explicitly.

### 4.6 Error handling — the algorithmic specification

This is the section we most need to lock down. Three error classes; each maps to a specific packer + agent action.

#### 4.6.1 EOM (end-of-media) during a `WriteAsync`-driven flush
**Symptom:** the drive returns EOM while the packer is flushing a full block.

**Packer action:**
1. The block whose write returned EOM may or may not be partially on tape. The packer treats it as **not** on tape (i.e. `committedTapeBlock` is *not* advanced past it).
2. The drive head is repositioned to `committedTapeBlock + 1` (i.e. to the first block after the last fully-committed file's last block). **Reposition is the packer's responsibility, not the agent's.**
3. All pending-commit files become candidates for rollback.
4. Throw a `TapePackerEomException` containing `IReadOnlyList<CommitToken> pendingTokens` (the to-be-rolled-back files) and `CommitToken? openFileToken` (the file currently being written, if any).
5. **The packer does NOT auto-rollback.** It hands control to the agent, which decides whether to `RollbackPending` + `RollbackOpenFile` (normal case) or `FlushAsync(lastSafeToken)` first (impossible at EOM, but the door is open for future error classes).

**Agent action (in `BackupFile` / `BackupFilesToCurrentSet`):**
1. Catch `TapePackerEomException`.
2. Call `packer.RollbackOpenFile()` — discards the file currently being written.
3. Call `packer.RollbackPending()` — returns rolled-back tokens.
4. **Files already reported via `FilesCommitted` are kept in the TOC. No notifications retracted.** Pending-but-uncommitted files have *not* yet had `PostProcess` fired (per option (b), `PostProcess` only fires on commit), so no notification retraction is needed.
5. The rolled-back files are re-queued at the head of the multi-volume context's pending list. Their `PreProcess` will be re-fired when the new volume processes them. For this reason, firing multiple `PreProcess` for the same file is acceptable.
6. The currently-open file is also re-queued. Its previous `PreProcess` already fired; the second one on the new volume is likewise acceptable.
7. Set `MultiVolumeContext` and return — caller asks user to load a new volume, then calls `ResumeBackupToNextVolume`.

**No `OnFileFailed` is fired for EOM rollback.** It's expected, recoverable, and unambiguous.

#### 4.6.2 Source file read error (or any other error *before* `EndFile`)
**Symptom:** `BackupFile` is mid-CopyTo and the source `FileStream.Read` throws (corrupt file, network share dropped, etc.). The packer is healthy.

**Packer action:** none yet — packer doesn't know.

**Agent action:**
1. Catch the exception.
2. Call `packer.RollbackOpenFile()`. Cheap — just truncates the buffer to `openFileStartAddress`.
3. Tape head is unchanged; previously-committed files are unaffected.
4. Fire `OnFileFailed(tfi, ex)` to ask the caller for the action.
5. **Action `Abort`:** stop the loop, return failure. Pending-commit files (if any) remain pending — they will be flushed by the eventual `EndWriteMode` triggered by leaving content-write mode. They get committed and `PostProcess`-notified during that flush. (This matches today's "abort writes what's already buffered" semantics.)
6. **Action `Skip`:** continue to next file. The skipped file leaves no trace on tape and no TOC entry. Other pending files keep accumulating.
7. **Action `Retry`:** call `BeginFile` again with the same `tfi` (yields the same `TapeAddress` since `openFileStartAddress` was reset). Re-fire `PreProcess` (acceptable). Re-stream the source file. The packer is in a clean Idle state — there is no tape rewind needed, because nothing was written to tape for this file.

**Important distinction:** in today's code, retry requires `Drive.MoveToBlock(tfi.Block)` because each file already started a fresh tape block. In Phase 2, retry of an open-file failure requires **no drive movement at all** — the rollback is purely in-memory. This is a significant simplification.

#### 4.6.3 Tape write error that is *not* EOM
**Symptom:** drive returns a non-EOM error during flush (media defect, hardware error, write-protect tripped mid-job).

**Packer action:**
1. Treat the block as not on tape.
2. **Do not auto-reposition** — for non-EOM errors, the drive's position is uncertain. The packer surfaces a `TapePackerWriteException` with the underlying error and the pending tokens.
3. Mark the packer as *poisoned* — no further writes accepted until `EndWriteMode` (which becomes a no-op flush + cleanup).

**Agent action:**
1. Catch the exception.
2. The currently-open file (if any) has not been `EndFile`'d → call `RollbackOpenFile`. Since the buffer is in-memory only, this succeeds.
3. Pending-commit files are lost. Fire `OnFileFailed(tfi, ex)` for each pending file *and* for the currently-open file. (This is a real durability loss — the user needs to know.)
4. The action returned for the *currently-open* file determines loop continuation; pending files' returned actions are advisory only (their content is gone). Recommended: surface a single aggregated `OnBatchFailed`-style notification later (Phase 2.1 polish), but for Phase 2 just fire `OnFileFailed` for each in order and treat any `Abort` as abort.
5. Leave content-write mode. The set is left with whatever files committed before the error; the agent's caller decides whether to discard or keep.

**Open question for review:** should non-EOM tape errors trigger an automatic attempt at `Drive.MoveToEndOfData` followed by a re-attempt? My recommendation: **no**, this is the agent's policy, not the packer's. The packer just reports.

#### 4.6.4 Abort request (`TapeAbortRequestedException`)
- Raised inside `WriteAsync` (via the packer's cancellation token wired to the agent's abort flag) **or** inside `BackupFile` between writes.
- Packer treatment: identical to non-EOM tape error from the *packer's* perspective — open file rolled back, pending files left in the buffer.
- Agent treatment: identical to today — break loop, return failure. **Crucially**, on abort, pending-commit files still get flushed by `EndWriteMode` and `PostProcess`-notified. The user "aborted at file 47" but files 40-46 still made it to tape and to the TOC. This is consistent with today's behavior (abort doesn't undo already-written files).

#### 4.6.5 Read errors (restore side)
- **CRC / hash mismatch:** detected in the agent layer (HashingStream), unchanged from today. Packer is unaffected.
- **Drive read error mid-file:** packer surfaces `TapePackerReadException`. Agent fires `OnFileFailed(tfi, ex)`; user chooses Skip/Retry/Abort. Retry calls `BeginReadAsync` again with the same address — packer invalidates the affected cache slot and re-reads.
- **Address out of range / block not found:** logical error → throw immediately, not via `OnFileFailed`. Indicates TOC/tape inconsistency.

### 4.7 Filemark policy under packing
Today, ONLY in FmksMode every file ends with a trailing filemark. Hence the problem with filemark support in packer will NOT arise.

Notice: for TOC storing, we'll continue writing a filemark after each TOC copy, as today. But TOC storing / reading is NOT subject to packer.

### 4.8 `MultiVolumeContext` reshape

Today's `TapeBackupContext` carries `fileList`, `fileIndex`, `overallSuccess`, `prevVolumeHasFiles`, `incremental`, etc. With the packer, the unit of "what to redo on next volume" is no longer "the file at `fileIndex`" but "the files at `fileIndex` plus any pending-commit files that got rolled back."

```text
private struct TapeBackupContext
{
    // unchanged
    internal readonly List<string> fileList;
    internal readonly bool ignoreFailures;
    internal readonly ITapeFileNotifiable? fileNotify;
    internal readonly bool incremental;
    internal int fileIndex;
    internal bool overallSuccess;
    internal bool prevVolumeHasFiles;

    // NEW: files that were rolled back from the packer's pending queue
    // due to EOM. They MUST be re-attempted before resuming the main
    // iteration through fileList. Stored as TapeFileInfo (already constructed)
    // so the agent doesn't re-stat the source file.
    internal List<TapeFileInfo> rolledBackPending;
}
```

`BackupFilesToCurrentSet`'s loop becomes:

```text
1. Drain rolledBackPending first (one-by-one, same per-file try/catch).
2. Then continue from fileList[fileIndex] forward.
```

The `prevVolumeHasFiles` flag now means "the previous volume committed at least one file via the packer." Determined by inspecting the `FilesCommitted`-driven TOC append count, not by `TOC.CurrentSetTOC.Count > 0` directly (though those should agree).

Subtle point: `rolledBackPending` files have already had `PreProcess` fired on the previous volume. Re-firing is acceptable (per your spec). They have **not** had `PostProcess` fired (because they never committed). They have **not** had `OnFileFailed` fired either (EOM is not a per-file failure event).

### 4.9 Notification timing summary (option (b), confirmed)

| Event | Fires when |
|-------|------------|
| `NotifyBatchStart` | Once at agent entry, unchanged. |
| `NotifyPreProcessFile` | At `BeginFile`-time. May fire **multiple times** for the same file across retries / EOM rollback. |
| `NotifyFileSkipped` | Pre-processor returns false, or incremental skip. Unchanged. |
| `NotifyPostProcessFile` | **Only** when `FilesCommitted` reports the file's token. May lag the corresponding `BeginFile` by an arbitrary amount of time / by many other files. |
| `NotifyFileFailed` | Per §4.6 — source error, non-EOM tape error, read error. **Not** for EOM rollback. |
| `NotifyBatchEnd` | Once after `EndWriteMode` returns. Final stats include all committed files. |

Stats updates:
- **Bytes processed:** updated incrementally as `WriteAsync` accepts bytes (so progress moves smoothly with throughput, not in per-file jumps). On rollback, bytes are *decremented* (your spec explicitly allows backwards stats movement).
- **Files processed (committed):** incremented when `FilesCommitted` fires.
- **Files failed:** incremented on `NotifyFileFailed`.

### 4.10 Agent flow — write path (Phase 2)

```text
BackupFileListToCurrentSet:
  manager.BeginWriteContent(...)
  packer = manager.GetPacker()  // already in write mode after BeginWriteContent
  packer.FilesCommitted += OnPackerCommit

  loop until done:
    if rolledBackPending non-empty: take next tfi from there
    else:                            take next from fileList[fileIndex++]

    try:
      ThrowIfAbortRequested()
      if !PreProcessFile(tfi): notify Skip; continue
      if not exists: throw FileNotFound
      if incremental & up-to-date: notify Skip; continue

      addr = packer.BeginFile(tfi)
      tfi.Address = addr
      token = ...                     // packer.EndFile returns it
      using stream = TapeWriteStream(packer)
      tfi.SerializeHeaderTo(serializer)
      copy source -> stream (with hashing)
      stream.Dispose() -> packer.EndFile() -> token

      pendingByToken[token] = tfi
      // PostProcess fires later, from OnPackerCommit

    catch TapePackerEomException eom:
      packer.RollbackOpenFile()
      tokens = packer.RollbackPending()
      foreach token in tokens (in original order):
        rolledBackPending.Add(pendingByToken.Remove(token))
      rolledBackPending.Insert(0, tfi)  // also re-queue current
      MultiVolumeContext = bc with rolledBackPending
      NotifyBatchEnd()
      return false

    catch source/read error:
      packer.RollbackOpenFile()
      action = NotifyFileFailed(tfi, ex)
      handle Retry/Skip/Abort

    catch TapePackerWriteException wex:
      packer.RollbackOpenFile()
      foreach pending tfi: NotifyFileFailed(...)
      break

  packer.EndWriteMode() -> commits remaining pending; OnPackerCommit fires for each
  manager.EndWriteContent()
  NotifyBatchEnd()

OnPackerCommit(committedFiles):
  foreach cf in committedFiles:
    tfi = pendingByToken.Remove(cf.Token)
    TOC.CurrentSetTOC.Append(tfi)
    NotifyPostProcessFile(tfi)
```

### 4.11 Agent flow — read path (Phase 2)

Restore is structurally simpler since there is no commit decoupling:

```text
RestoreFile(tfi):
  using rstream = packer.OpenReadStream(tfi.Address, tfi.FileDescr.Length)
  validate header via TapeDeserializer
  copy rstream -> destination file (with hashing)
  verify hash
```

Cross-block reads are invisible to the agent. Errors propagate directly; `OnFileFailed` policy is unchanged.

### 4.12 What the existing `BufferedTapeWriteStream` becomes
- Phase 1: unchanged (still useful for source-side double-buffering).
- Phase 2: redundant — the packer's write buffer subsumes its role. Remove the wrapping in `BackupFile` and delete the class (verify no external callers in `Services`/apps first).

### 4.13 Phase 2 Refinements (supersedes parts of §4.1–§4.12)

This section consolidates the design adjustments agreed during Phase 2 implementation
planning. Where it conflicts with earlier sections, this section wins.

#### 4.13.1 Layered architecture

Two layers per direction, no "middle layer" class. The middle is just a few
methods on the high layer that swap the active fill-buffer with the in-flight
buffer.

**Write side:**

- **Low layer — `ITapeWriteBackend`** (interface) with default implementation
  `WorkerThreadTapeWriteBackend`. Owns ONE in-flight write; serializes writes to
  `TapeDrive.WriteDirect` on a dedicated worker thread. Buffers are caller-owned
  and handed off explicitly across the worker boundary.
- **High layer — `TapeFileWritePacker`** (sealed class). Owns the file registry,
  token machinery, double-buffered fill, commit-promotion logic, and rollback
  state. Produces `TapeWriteStream` façade instances via `BeginFile()`.

**Read side (mirror, simpler — no async needed in Phase 2):**

- **Low layer — `ITapeReadBackend`** (interface) with default
  `SyncTapeReadBackend`. Reads N blocks at a time into a caller-owned buffer.
- **High layer — `TapeFileReadPacker`** (sealed class). Owns LRU block cache and
  produces `TapeReadStream` façades via `BeginRead()`.

No common base class between write and read packers — they share too little.
`TapeStreamManager` owns the lifecycle of the appropriate packer for the current
content mode (mirroring how it owns `TapeWriteStream`/`TapeReadStream` today).

#### 4.13.2 Why async is in Phase 2 (not Phase 3)

The original plan parked async behind Phase 3. We bring **single-write-in-flight**
asynchrony forward into Phase 2 because:

- Cost is low — one dedicated worker thread, no OVERLAPPED, no thread-pool tuning.
- Benefit is the *primary* throughput win: source-file ingest overlaps with the
  previous buffer's tape write. Without it, we save tape footprint but burn the
  same wall-clock time on small-file backups.
- Risk is bounded — exactly two pool-rented buffers alternate; one is being
  filled, one is being written. The state machine remains tractable.

True OVERLAPPED IO and read-ahead pipelining stay deferred to Phase 3.

#### 4.13.3 Low-layer write backend API

```text
internal enum WriteBackendStatus { Idle, Busy }

internal readonly record struct WriteResult(
    int BlocksWritten,         // always block-aligned (backend rounds down)
    bool EomEncountered,       // ERROR_END_OF_MEDIA seen during this write
    Exception? Exception);     // null on success / pure EOM; non-null on hard error

internal interface ITapeWriteBackend : IDisposable
{
    uint BlockSize { get; }
    int  MaxBlocksPerWrite { get; }   // capacity hint for the high layer

    // Hand off `validBytes` of `buffer` to the worker. Blocks until the previous
    // write (if any) has completed. After the call, the high layer MUST NOT touch
    // `buffer` until it is returned via the WriteResult of the NEXT call to
    // StartWriting or AwaitCompletion.
    void StartWriting(byte[] buffer, int validBytes);

    // Non-blocking snapshot. Returns Idle if no write is in flight.
    WriteBackendStatus PollStatus();

    // Blocks until any in-flight write finishes. Returns the result + the
    // buffer that was last in flight (caller may now reuse/return-to-pool).
    // If no write is in flight, returns a sentinel "nothing to await" result
    // with buffer == null. Idempotent.
    (WriteResult Result, byte[]? Buffer) AwaitCompletion();
}
```

Notes on the contract:

- **`BlocksWritten` is reliable as-is** — the backend interprets the byte count
  returned by `TapeDrive.WriteDirect` and rounds down to block boundary. The high
  layer treats `committedTapeBlock` as advancing by exactly `BlocksWritten`. (We
  do **not** apply the "minus one to be safe" precaution suggested earlier — the
  drive's count is authoritative.)
- **EOM as a non-fatal status, not an exception.** A write that hits EOM may still
  have committed some blocks; the backend reports `(BlocksWritten = K, Eom = true,
  Exception = null)`. Hard errors (media defect, hardware failure) come back with
  `Exception != null` and `Eom = false`.
- **Cancellation:** the worker observes the agent's abort flag between writes
  (i.e. on the next `StartWriting`). It does NOT interrupt an in-flight
  `WriteDirect` — that is the unit of cancellability.
- **Disposal** drains any in-flight write before returning. Disposing while a
  write is in flight is legal and blocks; aborting cleanly is the high layer's
  responsibility.

#### 4.13.4 High-layer write packer — adjusted API

```text
internal enum SourceErrorMode
{
    /// Discard the open file's in-buffer bytes, including any already-flushed
    /// blocks; reposition tape to last committed block. Recovers tape space at
    /// the cost of one MoveToBlock when a multi-buffer file fails mid-stream.
    /// Recommended for archives dominated by very large files.
    Rollback,

    /// Leave whatever was already flushed for the open file as on-tape garbage;
    /// only truncate the still-buffered tail. Never repositions the tape.
    /// Recommended default — for the typical small-file workload, no flush has
    /// happened yet for the open file, so this is identical to Rollback but
    /// without the failure-mode complexity.
    NoRollback
}

internal sealed class TapeFileWritePacker : IDisposable
{
    internal TapeFileWritePacker(
        TapeStreamManager mgr,
        ITapeWriteBackend backend,
        int blockMultiplier = 16,
        SourceErrorMode sourceErrorMode = SourceErrorMode.NoRollback);

    // Open a logical write slot for one file. Returns a stream the caller writes
    // source bytes into. The file's TapeAddress is NOT known yet; it surfaces in
    // the FilesCommitted event when the file's tail block is on tape.
    internal TapeWriteStream BeginFile();

    // Close the open file. Returns a CommitToken for correlation with the
    // eventual FilesCommitted event. Also assigned to TapeWriteStream.CommitToken
    // before this returns, so the agent can read it from the stream after Dispose.
    internal CommitToken EndFile();

    // Discard the open file according to SourceErrorMode. Both modes truncate the
    // still-buffered tail. Rollback mode additionally repositions the tape if any
    // of the open file's content has already flushed.
    internal void DiscardOpenFile();

    // Discard ALL pending-commit files; reposition tape to last committed block.
    // Used on EOM. Returns the rolled-back tokens in original order.
    internal IReadOnlyList<CommitToken> RollbackPending();

    // Drain everything: zero-pads the trailing partial block, hands off the final
    // buffer, awaits backend completion, fires FilesCommitted for all newly
    // committed tokens. Idempotent; safe to call after errors.
    internal void Flush();

    // Fired (synchronously, on the calling thread) whenever one or more files
    // cross the commit boundary as a side effect of writes triggered by
    // TapeWriteStream.Write, EndFile, Flush, or Dispose.
    internal event Action<IReadOnlyList<CommittedFile>>? FilesCommitted;

    public void Dispose();   // ensures Flush() is attempted; releases buffers
}

internal readonly record struct CommitToken(ulong Sequence);

internal sealed record CommittedFile(
    CommitToken Token,
    TapeAddress StartAddress,
    long Length);            // body length; padding excluded
```

Key changes vs. §4.3:

- **`BeginFile()` takes no `TapeFileInfo`** — the packer doesn't know about the
  TOC and shouldn't.
- **`BeginFile()` returns `TapeWriteStream`, not `TapeAddress`.** The address is
  reported only at commit time, in the `CommittedFile` payload. The agent stamps
  `TapeFileInfo.Address` inside its commit handler. No "provisional address" is
  ever exposed.
- **No `WriteAsync` on the packer.** Bytes flow exclusively through the stream's
  synchronous `Write`. The asynchrony lives in the backend, behind the buffer
  swap — invisible to the agent.
- **`Flush()` drains all pending** (no per-token `upTo` granularity). Simpler,
  and we have no use case for partial flush.
- **`DiscardOpenFile()` replaces `RollbackOpenFile()`** and is governed by
  `SourceErrorMode`. Both Rollback and NoRollback modes are implemented in
  Phase 2 — selectable per backup operation, defaulting to NoRollback.
- The packer maintains a **file registry** keyed by `CommitToken`, holding
  `{ startAddress, length-so-far, isOpen }`. Entries are removed at commit time
  (or at rollback/discard time). The agent maintains its own
  `Dictionary<CommitToken, TapeFileInfo>` for TOC stamping.

#### 4.13.5 SourceErrorMode — semantics in detail

For a file that fails mid-ingest (source `Read` throws):

- **Common case (open file's bytes never flushed):** both modes truncate the
  fill-buffer back to the open file's start offset. No tape I/O. Identical
  outcome.
- **Open file already triggered ≥1 buffer flushes:**
  - **NoRollback:** leave already-flushed bytes on tape as anonymous garbage
    (no TOC entry, no token). Truncate only the still-buffered tail. Subsequent
    files start at `bufferedThruOffset` of the current buffer. Tape head is not
    moved.
  - **Rollback:** await backend completion, then `Drive.MoveToBlock(committedTapeBlock + 1)`
    where `committedTapeBlock` is the last block belonging to a committed file.
    The current fill-buffer is discarded entirely (any pending-commit files in it
    must have started after the open file, which is impossible — at most one open
    file exists — so there is nothing to lose). All bytes of the open file are
    reclaimed.

The mode is set per packer instance (i.e. per backup operation) at construction.

#### 4.13.6 Buffer-handoff sketch (Option A, double-buffered)

```text
Two pool-rented byte[] buffers: `fill` (high layer writes into) and `inflight`
(backend writing from). Initially `fill = pool.Rent(N*BlockSize)`,
`inflight = null`.

When `fill` becomes full (or Flush is called):
  1. validBytes = (fill.PositionAfterLastFullBlock)   [trailing partial held back]
  2. backend.StartWriting(fill, validBytes)           [blocks if previous still busy]
  3. (result, returnedBuffer) = packer.harvest()      [collect the just-finished write]
     - Promotes pending tokens whose end-block ≤ committedTapeBlock + result.BlocksWritten
     - Fires FilesCommitted
     - Returns returnedBuffer to ArrayPool
  4. fill = pool.Rent(N*BlockSize); copy held-back trailing partial into fill[0..]
  5. inflight = (the buffer just handed to backend) — tracked internally
```

`harvest()` calls `backend.AwaitCompletion()`; it is also called from `Flush()`
and from a quick `PollStatus()`-driven check at `BeginFile()` time (proactive
EOM detection without waiting).

#### 4.13.7 EOM detection cadence

Both reactive and proactive, with deliberately coarse proactive cadence:

- **Reactive (mandatory):** every `StartWriting` implicitly awaits the previous
  write. EOM bubbles up here.
- **Proactive (opportunistic):** `BeginFile()` does a non-blocking
  `backend.PollStatus()` and, if `Idle`, harvests the result. This catches EOM
  one-file-late instead of one-buffer-late. Per-`Write`-call polling is too
  fine-grained (a large file produces many Writes); per-file is the right
  cadence.

#### 4.13.8 Notification timing — minor tweak

Per §4.9, but with one clarification reflecting the API change: `PreProcessFile`
fires at `BeginFile()` time. Since `BeginFile()` no longer takes a `TapeFileInfo`,
the agent fires `PreProcessFile` itself **immediately before** calling
`BeginFile()`, and `PostProcessFile` from inside its `FilesCommitted` handler.
The packer is unaware of notifications.

#### 4.13.9 Open questions resolved

- **§6.1 Filemark policy:** closed (no filemarks after files; one after each
  TOC copy retained).
- **§6.3 `FilesCommitted` event threading:** synchronous on the calling thread
  in Phase 2 — confirmed.
- **§6.4 Unknown token in `FilesCommitted`:** throw — confirmed (invariant
  violation).
- **§6.5 Re-pack onto last committed block after `RollbackPending`:** no — one
  block wasted per EOM accepted (Phase 2 simplification).
- **§6.7 `BeginFile` returning provisional address:** revised — `BeginFile`
  returns a stream, address surfaces only at commit. No provisional address
  ever exists.

Open question §6.2 (non-EOM write error coarseness) remains under review;
Phase 2 implements per-file `OnFileFailed` for now.

---

## 5. Updated Implementation Plan

### Phase 1 — combined foundation: COMPLETE

1. **Introduce `TapeAddress`** (new file `TapeLibNET/TapeAddress.cs`).
2. **TOC migration:**
   - `TapeFileInfo.Block (long)` → `Address (TapeAddress)`.
   - Update constructor overloads (keep a `(TypeUID, long block, ...)` convenience overload that wraps in `TapeAddress(block, 0)` to minimize call-site churn during the change, then remove it before Phase 2 begins).
   - Update `SerializeTo` / `ConstructFrom` / `EstimateSerializedSize`.
   - Bump TOC format version constant; refuse older versions with a clear diagnostic.
3. **Drop `FmksMode`:**
   - Remove from `TapeSetTOC` (field + serialization).
   - Remove from `TapeNavigator`: `WriteContentFilemark()` not needed anymore, but we keep `WriteTOCFilemark()`!
   - Remove from `TapeStreamManager`.
   - Remove from `TapeFileBackupAgent.BeginWriteContentForCurrentSet`.
   - Remove from `Services` DTOs (`ServiceOperationRequest` and friends).
4. **Update `TapeFileBackupAgent` & `TapeFileRestoreAgent`** to use `Address` instead of `Block`. Rewind sites become `Drive.MoveToBlock(tfi.Address.Block)`.
5. **Update `TapeLibNET.Services`** — purely cosmetic: `Address` rename, FmksMode parameter removed.
6. **Update `TapeConNET`:**
   - Remove `--fmks` flag and related help text.
   - File-list display formatting goes through `TapeAddress.ToString()`.
   - Update CLI tests.
7. **Update `TapeWinNET`:**
   - Remove FmksMode UI (BackupWindow control + view-model property + settings key).
   - Tree/info panes that show `Block` → use `Address` formatting.
   - Update ViewModel tests if any.
8. **Update `TapeLibNET.Tests` and `TapeConNET.Tests`:**
   - Remove FmksMode-parameterized tests / fixtures.
   - Update assertions touching `tfi.Block` to `tfi.Address.Block`.
   - Add a focused test asserting `TapeAddress.ToString()` formatting and `Parse` round-trip.
   - Add a TOC round-trip test that asserts the new wire format reads back correctly.
9. **Build + run all tests + virtual-drive backup/restore round-trip + manual real-LTO smoke.**

All steps completed.

### Phase 2 — packing (revised; supersedes the earlier Phase 2 list)

Executed in independently-buildable steps. Each step has its own test scope so
regressions are caught at the layer they originate in.

> **Status (Steps A–D): COMPLETE.** The write side of the packer pipeline is
> implemented end-to-end, integrated into `TapeStreamManager` and
> `TapeFileBackupAgent`, and validated by a 65-test focused suite plus the full
> 1393-test regression run (all green). Steps E (read side) and F (large-scale
> integration + real-LTO smoke) remain. The narrative below records what was
> actually built and the key design decisions that shaped each step.

#### Step A — Low-layer write backend [DONE]

**What was built**

- `TapeLibNET/TapeFilePacker/ITapeWriteBackend.cs` defines the contract plus
  `WriteBackendStatus`, `WriteResult`, and the `TapeWriteSink` delegate.
- `WorkerThreadTapeWriteBackend` runs a single dedicated worker `Thread`, with
  a single in-flight write coordinated through `ManualResetEventSlim` pairs.
  Writes are routed through a caller-supplied `TapeWriteSink` (not directly
  through `TapeDrive`), which keeps the backend testable in isolation.
- `MemoryTapeWriteBackend` records all handed-off buffers and supports
  scripted EOM and scripted hard-error injection for unit tests.
- 13 unit tests cover round-trip, blocking handoff, `PollStatus` accuracy,
  `AwaitCompletion` idempotency, scripted EOM, scripted hard error, and clean
  drain on `Dispose` while a write is in flight.

**Key design points**

- **Decoupled from `TapeDrive`** via the `TapeWriteSink` delegate — the manager
  injects the actual `Drive.WriteDirect` call site, so the backend can be unit-
  tested without any drive plumbing.
- **EOM is a status, not an exception.** A write that hits end-of-media still
  reports the partial `BlocksWritten` it managed before the boundary; only
  *hard* errors surface as `Exception`. This was essential to make the high
  layer's commit accounting work in the EOM rollback path.
- **`BlocksWritten` is authoritative** — the backend rounds the drive's byte
  count down to a block boundary, and the high layer trusts the value as-is.
  No "minus one to be safe" precaution.
- **Cancellation granularity is one write.** The worker checks the abort flag
  between writes; an in-flight `WriteDirect` runs to completion. This kept the
  state machine tractable.
- **Backend is not "poisoned" by errors** — error policy lives entirely in the
  high layer. The backend remains usable for the next `StartWriting` call, so
  the packer can decide whether to retry, drain, or dispose.

#### Step B — High-layer write packer [DONE]

**What was built**

- `TapeLibNET/TapeFilePacker/TapeFileWritePacker.cs` (sealed class) implements
  the file registry keyed by `CommitToken`, the double-buffered fill/in-flight
  swap from §4.13.6, both `SourceErrorMode` paths from §4.13.5, and the
  `FilesCommitted` event for commit promotion.
- `TapeWriteStreamFacade` is the per-file `Stream` returned from `BeginFile()`.
  It tracks logical length and carries its `CommitToken`, becoming inert when
  closed.
- `TapePackerEndOfMediaException` carries the rolled-back token list across the
  packer/agent seam.
- `PackerTypes.cs` collects `CommitToken`, `CommittedFile`, and `SourceErrorMode`.
- 14 unit tests cover packed `(Block, Offset)` math, cross-block files, both
  `DiscardOpenFile` modes (with and without prior flush), `RollbackPending`
  token sets, token promotion across buffer boundaries, and scripted EOM mid-
  flush.

**Key design points**

- **`BeginFile()` returns a stream, not an address.** The address is unknown
  until the file commits — exposing a "provisional" address would invite
  callers to depend on a value that can move on rollback. Resolution from
  §4.13.4 / §6.7.
- **`CommitToken` is opaque, surfaces via the stream after `Dispose`.** This
  lets the agent correlate pending entries with `FilesCommitted` payloads
  without ever holding a tentative address.
- **Single-open-file invariant.** At most one file is open at a time; this
  collapses a number of buffer-management corner cases and makes
  `DiscardOpenFile` a pure in-memory truncation in the common case.
- **`SourceErrorMode.NoRollback` is the default.** For typical small-file
  workloads the open file's bytes have not yet flushed, so `Rollback` and
  `NoRollback` are observationally equivalent — but `NoRollback` never
  performs a tape `MoveToBlock`, which keeps the failure path strictly
  in-memory and hazard-free.
- **`Flush()` drains everything; no per-token granularity.** No use case
  required partial flush, and the simpler API removed an entire class of
  "what's on tape after flush(token X)?" reasoning.
- **EOM detection is reactive + opportunistically proactive.** Every
  `StartWriting` implicitly awaits the previous write (reactive); `BeginFile`
  does a non-blocking `PollStatus` harvest (proactive). Per-`Write` polling
  was rejected as too fine-grained.

#### Step C — Manager integration [DONE]

**What was built**

- `TapeStreamManager` owns packer/backend lifecycle: `EnsurePackerCreated()`
  constructs the `WorkerThreadTapeWriteBackend` (bridged to `Drive.WriteDirect`
  via a `PackerWriteSink` lambda) and the `TapeFileWritePacker`.
- `BeginPackedFile()` / `EndPackedFile()` expose per-file packer slots to the
  agent without exposing the packer object's full surface.
- `FlushAndDisposePacker()` zero-pads the trailing partial block, awaits the
  backend, fires the final `FilesCommitted` for tail commits, then disposes.
- `Manager.FilesCommitted` is re-exposed (forwarded from `m_packer.FilesCommitted`)
  so the agent subscribes once at the manager level and never needs a direct
  packer reference.

**Key design points**

- **Manager owns lifecycle; agent owns subscriptions.** The agent never
  constructs or disposes the packer — it just calls `BeginWriteContent` /
  `EndWriteContent` and listens for `FilesCommitted`. This mirrors how the
  manager already owned `TapeWriteStream` / `TapeReadStream` lifetimes.
- **The legacy stream path stayed in parallel.** `BeginWriteContent(...)` still
  serves the existing per-file stream API; the packed path is opt-in via
  `BeginPackedFile()`. This was a safe migration strategy that let us validate
  the packer without destabilizing existing callers.
- **Subscription order matters at teardown.** `FlushAndDisposePacker` disposes
  the packer **before** unsubscribing from `m_packer.FilesCommitted`, so tail
  commits emitted during the final flush are still observed. Inverting this
  order silently dropped tail commits — caught and fixed during Step D
  testing.
- **Packer must observe the *final* drive block size at creation.** Discovered
  during Step D debugging on `DriveProfile.FilemarksOnly`: the agent must call
  `Drive.SetBlockSize(...)` **before** `Manager.BeginWriteContent(...)`,
  otherwise the packer caches the prior set's TOC block size and the in-sink
  arithmetic `blocks = bytes / Drive.BlockSize` rounds to zero. Fix landed in
  `BeginWriteContentForCurrentSet(bool)`; documented inline.

#### Step D — Backup-agent integration [DONE]

**What was built**

- `TapeFileBackupAgent.BackupFilePacked(...)`, `BackupFilesToCurrentSetPacked(bool)`,
  `BackupFileListToCurrentSetPacked(...)`, and the public
  `BackupFilesToCurrentSetPacked(...)` pendant. The packed methods sit alongside
  their legacy counterparts; callers opt in by name.
- `BeginWriteContentForCurrentSet(bool packed)` reorders the per-set startup
  so `Drive.SetBlockSize(...)` runs before `Manager.BeginWriteContent(...)`
  (see Step C key point).
- `TapeLibNET/TapeFilePacker/PackedCommitTracker.cs` consolidates the three
  collections the agent would otherwise hand-roll (pending registry, TOC
  promotion, post-process queue) behind three verbs: `Register`,
  `OnCommitted`, `DrainPostProcess` (plus `RemoveRolledBack` for the EOM
  path).
- `TapeBackupAgentPackedTests` (65 tests over 4 drive profiles × multiple
  hash algorithms) exercises single-file, multi-file, many-small-files
  block-sharing, monotonic addresses, TOC reload of `(Block, Offset)`,
  sequential two-set backup, statistics invariants, deferred post-process
  ordering, pre-process skip, missing-file Skip/Abort, abort-in-pre-process,
  and packed-then-legacy coexistence.

**Key design points**

- **`TapeFileInfo.Address` is stamped at commit time** inside the
  `FilesCommitted` handler — it is the *only* moment the real address is
  known. The per-iteration `template` carries UID + `FileDescr` only and is
  promoted to a real `TapeFileInfo` in the tracker.
- **`NotifyPostProcessFile` is deferred** until the file's commit is observed,
  per §4.9. The drain runs on the main loop thread between iterations (and
  once more after `EndWriteContent`), preserving the legacy abort semantics:
  an abort thrown from `PostProcess` breaks the loop without rewinding the
  tape, because the file is already on tape and in the TOC.
- **No per-file `Drive.MoveToBlock` on the packed failure path.** The legacy
  path rewinds to `tfi.Block` after a per-file failure; the packed path must
  not — earlier files queued in the same fill buffer would be clobbered.
  Instead, `Manager.Packer?.DiscardOpenFile()` truncates the open file's tail
  in memory, leaving prior pending files intact to commit later.
- **`PackedCommitTracker` collapses three ad-hoc collections into one helper.**
  Without it, the packed loop juggled a `Dictionary<CommitToken, TapeFileInfo>`,
  a TOC-append site, and a `Queue<TapeFileInfo>` for deferred post-process.
  With it, the loop's commit handling is `tracker.Register(...)` /
  `tracker.OnCommitted(...)` / `tracker.DrainPostProcess(...)` and nothing
  else.
- **Subscription must remain active through final flush.** The agent
  subscribes to `Manager.FilesCommitted` *before* the loop and unsubscribes
  in a `finally` *after* `Manager.EndWriteContent()` returns, so tail
  commits emitted by the manager's flush still reach the tracker.
- **Overall-success semantics match legacy.** A per-file failure flips
  `bc.overallSuccess = false` even when the chosen action is `Skip` and
  `ignoreFailures` is `true`. The loop continues, but the final `TapeResult`
  reports failure — consistent with `BackupFilesToCurrentSet`.
- **EOM rollback re-queues by earliest rolled-back file index.** On
  `TapePackerEndOfMediaException`, the tracker returns the smallest
  `FileIndex` among the rolled-back tokens; the agent sets `bc.fileIndex` to
  that value so multi-volume continuation re-attempts every uncommitted file
  (per §4.8). No `OnFileFailed` is fired for the rolled-back set; only the
  open file gets a `NotifyFileFailed` (with `StatsUndoFailure` if not Abort).

#### Step E — Read side (mirrors A–D, simpler) [DONE]

**What was built**

- `TapeLibNET/TapeFilePacker/ITapeReadBackend.cs` defines the read contract
  plus `ReadResult` and the `TapeReadSink` / `TapeSeekSink` delegates. The
  read backend is **synchronous** in Phase 2 (no worker thread, no
  prefetch); the surface is shaped to admit a Phase 3 prefetcher behind the
  same interface.
- `SyncTapeReadBackend` issues `Drive.ReadDirect` calls one block at a time
  and reports filemark / EOF / hard-error conditions through `ReadResult`.
  Seeks are routed through the injected `TapeSeekSink` so the backend stays
  testable in isolation from `TapeDrive`.
- `TapeLibNET/TapeFilePacker/TapeFileReadPacker.cs` implements the high
  layer: a small LRU ring of block-sized cache slots (sized by
  `PackerBlockMultiplier`), single-open-file slot state, exact
  `(Block, Offset)` positioning via `BeginRead(TapeAddress, long)`, and
  cross-block reads hidden from the caller. `_drivePositionBlock` tracks
  the drive head so adjacent forward reads skip a `MoveToBlock`.
- `TapeReadStreamFacade` is the per-file `Stream` returned from
  `BeginRead()`. It forwards `Read(...)` to
  `TapeFileReadPacker.ReadIntoOpenFile(...)`, tracks logical position,
  and closes the packer slot on `Dispose` via `EndRead()`.
- `TapeStreamManager` owns the read packer/backend lifecycle:
  `EnsureReadPackerCreated()` constructs the `SyncTapeReadBackend` (bridged
  to `Drive.ReadDirect` via `PackerReadSink` and to `Drive.MoveToBlock`
  via the seek sink) and the `TapeFileReadPacker`.
  `BeginPackedFileRead(addr, length)` is the agent-facing entry point;
  `EndPackedFileRead()` closes the slot.
- `TapeFileRestoreBaseAgent` adds packed pendants: `RestoreNextFilePacked`,
  `RestoreFilesFromCurrentSetPacked` (selected files), and
  `RestoreAllFilesFromCurrentSetPacked` (all files). The three concrete
  agents (`TapeFileRestoreAgent`, `TapeFileValidateAgent`,
  `TapeFileVerifyAgent`) each implement `RestoreFileCorePacked` over a
  generic `Stream` (the façade), keeping block boundaries invisible to the
  restore policy.
- `TapeLibNET.Tests/TapeRestoreAgentPackedTests.cs` covers single-file,
  multi-file, many-small-files (block-sharing), mixed-size, edge-case,
  TOC-reload, selective-restore, two-set independent, validate, verify,
  and the legacy-backup → packed-restore cross-path. All pass on every
  drive profile.

**Key design points**

- **Read packer is reset on every set boundary.** The cached blocks and
  `_drivePositionBlock` are tied to the current set's position state.
  `BeginReadContent()` disposes the read packer when the manager moves
  to a different set within the reading state, and `EndReadContent()`
  disposes it on exit. Without this reset, the cache fed stale blocks
  into the new set's reads, manifesting as a deterministic second-set
  mismatch in `Packed_TwoSets_RoundTripIndependently`.
- **Write packer anchors at the absolute drive block.** The fix that
  made multi-set packed restore work was on the *write* side:
  `TapeFileWritePacker` now takes `initialAbsBlock` and
  `TapeStreamManager.EnsurePackerCreated()` passes
  `Drive.BlockCounter`. The `TapeAddress` values surfaced through
  `FilesCommitted` are absolute and match the legacy aligned backup's
  convention of recording `Drive.BlockCounter`. Without this, set-2
  TOC entries pointed one set-worth of blocks too low, so the packed
  restore's `MoveToBlock` landed inside set 1.
- **No commit decoupling on the read side.** Restore is a direct
  stream-consume loop; failures propagate inline as today, and
  `OnFileFailed` policy is unchanged. The packer only adds transparent
  caching and intra-block positioning.
- **Out-of-order / selective restore is supported but not optimized.**
  `BeginRead` will seek backward when needed; the cache still serves
  hits regardless of direction. Phase 3 prefetch will be disabled for
  non-monotonic access patterns.
- **Cross-path compatibility, asymmetric.** Legacy (aligned) backup
  output restores cleanly through the packed path because
  `Address.Offset == 0` is just a special case of
  `BeginRead(addr, length)`. The reverse — legacy restore over packed
  backup — is **not** supported in general: packed backup may place
  files at non-zero intra-block offsets even when each file is larger
  than one block (the tail of one file shares a block with the head
  of the next). The corresponding cross-path test was removed; only
  the packed restore path is a correct consumer of packed-backup
  output.
- **`BufferedTapeWriteStream` retained for the aligned path.** It is
  still wrapped by `BackupFile` (the aligned write path) to overlap
  source-file reads with tape writes. The packed write path needs no
  such wrapper because the worker-thread backend already overlaps
  source ingest with the previous buffer's tape write. Removal is
  deferred until the aligned path itself is retired.

#### Step F — Integration tests
- In `TapeFileBackupAgent` and `TapeFileRestoreBaseAgent` we retained the legacy methods with "Aligned" suffix and [Obsolete] attribute - to use in the tests for side-by-side comparisons
- Suggestion: `VirtualTapeDriveBackend` integration tests:
    - 10 000 × 1 KiB files round-trip; verify total tape footprint ≈ 10 MiB.
    - Mixed sizes (1 KiB / 1 MiB / 100 MiB) round-trip.
    - Injected EOM mid-flush — multi-volume continuation succeeds, no file lost.
    - Source-read error — Skip / Retry / Abort × NoRollback / Rollback.
    - Abort during pending-commit window — pending files committed and
      `PostProcess`-notified.
- Manual real-LTO smoke test; compare elapsed time vs. Phase 1 baseline.
- Tag `phase2-complete`.

### Phase 3 (deferred, sketched only)
- Extract `IPackerIoBackend`.
- Implement `ThreadPoolPackerBackend` (bounded write queue + read prefetcher).
- Wire cancellation through `TapePackerCancellation` (already plumbed in Phase 2 via `CancellationToken`).
- Optional `OverlappedPackerBackend` if profiling shows the worker is the bottleneck.
- NOTICE we already implemented double-buffered tape writing in Phase 2!

---

## 6. Open Questions for Review [pre-Phase 1 -> potentially outdated]

1. **Filemark policy** [CLOSED] — NO filemarks after files once FmksMode is gone. Filemarks after each stored TOC copy retained.
2. **Non-EOM write error policy** — current proposal: packer reports, agent fires `OnFileFailed` per pending file. Do you want a coarser `OnBatchFailed` semantic instead?
3. **`FilesCommitted` event threading** — Phase 2 fires synchronously on the calling thread (the agent's main loop). Confirm this is acceptable; Phase 3 will need to marshal it back if the backend goes async.
4. **`pendingByToken` failure mode** — if a token reported by `FilesCommitted` is not in the map (logic bug), should we throw or log+drop? Recommend throw (unrecoverable invariant violation).
5. **Re-pack onto last committed block after `RollbackPending`** — Phase 2 simplification says no (one block wasted per EOM). Acceptable, or worth the complexity to reclaim?
6. **TOC format version policy** — single bump for both changes, or two separate bumps? Recommend single (Phase 1 ships once, atomically).
7. **`BeginFile` returning `TapeAddress` vs. returning a token that resolves later** — current proposal: address is known at `BeginFile` time. This is a minor packer design constraint (it must commit to the address before knowing if the file will fit in this volume). Confirm that's acceptable; the alternative is `tfi.Address` becoming `Invalid` until commit, which complicates progress display.

---

## 7. Glossary

| Term | Meaning |
|------|---------|
| **Block** | A fixed-size unit of tape I/O. Set per content-set (`TapeSetTOC.BlockSize`). |
| **Address** | `(Block, Offset)` pair locating a byte on tape. |
| **Pack / Packing** | Placing the start of file *N+1* immediately after the end of file *N* within the same block. |
| **Pending commit** | A file fully written to the packer (`EndFile` called) but not yet flushed to tape. |
| **Committed** | A file whose bytes are durably on tape AND whose `FilesCommitted` notification has fired. |
| **Open file** | The single file currently between `BeginFile` and `EndFile`. At most one at any time. |
| **Token** | Opaque correlation handle returned by `EndFile`, used to match pending files to their commit notification. |
| **Rollback (open)** | Discard the open file's in-buffer bytes; in-memory only, no tape I/O. |
| **Rollback (pending)** | Discard all pending-commit files; reposition drive to last committed block. |
| **F1 / F2** | Filemark policies: per-flush (recommended) / per-set. |
