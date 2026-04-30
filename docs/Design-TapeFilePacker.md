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
- Not pooled across sessions — sessions are long-lived; pooling adds complexity with no measurable benefit.

### 4.3 `TapeFilePacker` API surface

The API is shaped now to admit a future async backend without reshaping callers. Phase 2 implementations are synchronous; method names are `Async`-suffixed where Phase 3 will plausibly need to await, returning `ValueTask` even when the implementation is currently synchronous.

#### 4.3.1 Lifecycle

```text
internal sealed class TapeFilePacker : IDisposable
{
    internal TapeFilePacker(TapeStreamManager mgr, uint blockSize, int blockMultiplier);

    // Mode switching — mutually exclusive. Must be called before any Begin* call.
    internal void EnterWriteMode(long startingBlockOnTape);
    internal void EnterReadMode();

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
// re-queue them. Used on EOM.
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
- **Out-of-order reads (e.g. selective restore):** supported — `BeginReadAsync` will seek backward if needed. Cache hit/miss logic is identical. Phase 3 prefetcher will be disabled when the agent enqueues non-monotonic addresses.

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
5. The rolled-back files are re-queued at the head of the multi-volume context's pending list. Their `PreProcess` will be re-fired when the new volume processes them. Per your spec, double `PreProcess` is acceptable.
6. The currently-open file is also re-queued. Its previous `PreProcess` already fired; the second one on the new volume is harmless.
7. Set `MultiVolumeContext` and return — caller asks user to load a new volume, then calls `ResumeBackupToNextVolume`.

**No `OnFileFailed` is fired for EOM rollback.** It's expected, recoverable, and unambiguous.

#### 4.6.2 Source file read error (or any error *before* `EndFile`)
**Symptom:** `BackupFile` is mid-CopyTo and the source `FileStream.Read` throws (corrupt file, network share dropped, etc.). The packer is healthy.

**Packer action:** none yet — packer doesn't know.

**Agent action:**
1. Catch the exception.
2. Call `packer.RollbackOpenFile()`. Cheap — just truncates the buffer to `openFileStartAddress`.
3. Tape head is unchanged; previously-committed files are unaffected.
4. Fire `OnFileFailed(tfi, ex)` and ask for the action.
5. **Action `Abort`:** stop the loop, return failure. Pending-commit files (if any) remain pending — they will be flushed by the eventual `EndWriteMode` triggered by leaving content-write mode. They get committed and `PostProcess`-notified during that flush. (This matches today's "abort writes what's already buffered to disk" semantics.)
6. **Action `Skip`:** continue to next file. The skipped file leaves no trace on tape and no TOC entry. Other pending files keep accumulating.
7. **Action `Retry`:** call `BeginFile` again with the same `tfi` (yields the same `TapeAddress` since `openFileStartAddress` was reset). Re-fire `PreProcess` (acceptable per your note). Re-stream the source file. The packer is in a clean Idle state — there is no tape rewind needed, because nothing was written to tape for this file.

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

---

## 5. Updated Implementation Plan

### Phase 1 — combined foundation (estimated 2–3 dev-days)

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
10. **Tag** as `phase1-complete` for rollback safety.

### Phase 2 — packing (estimated 5–8 dev-days)

1. **Spike `TapeFilePacker` write side** with an in-memory backend (no `TapeDrive`). Validate:
   - `BeginFile` → `WriteAsync` × N → `EndFile` → eventual `FilesCommitted`.
   - `RollbackOpenFile` truncates correctly.
   - `RollbackPending` returns exactly the right token set.
   - Cross-block file boundaries land at the correct `(Block, Offset)`.
2. **Wire the packer into `TapeStreamManager`** in place of the current per-file write path. `TapeWriteStream` becomes the façade described in §4.3.4.
3. **Implement Phase 2 filemark policy (F1)** in `TapeStreamManager`'s flush path.
4. **Read side:** implement `BeginReadAsync` / `ReadAsync` / `EndRead` with the LRU block cache from §4.4.
5. **Restore agent migration** — straightforward, no commit decoupling.
6. **Backup agent migration:**
   - Introduce `pendingByToken` map and `OnPackerCommit` handler.
   - Move `TOC.Append` and `NotifyPostProcessFile` into the commit handler.
   - Update stats helpers to support backwards movement on rollback.
   - Reshape `MultiVolumeContext` per §4.8.
   - Implement the three error-handling paths from §4.6.
7. **Remove `BufferedTapeWriteStream`** wrapping in `BackupFile`. Delete the class once no callers remain.
8. **Tests:**
   - Unit tests for `TapeFilePacker` against an in-memory drive: pack many small files, verify addresses are contiguous (offsets follow lengths), verify `RollbackOpenFile` and `RollbackPending` invariants, verify cache hit/miss on read.
   - Integration tests on `VirtualTapeDriveBackend`:
     - 10 000 × 1 KiB files round-trip; verify total tape footprint is `≈ 10 MiB` (not `10 GiB`).
     - Mixed sizes (1 KiB / 1 MiB / 100 MiB) round-trip.
     - EOM injected mid-flush — verify rollback, multi-volume continuation succeeds, no file lost.
     - Source-read error mid-file — Skip / Retry / Abort each cover.
     - Abort during pending-commit window — verify pending files still get committed and `PostProcess`-notified.
   - Manual real-LTO: full backup/restore of a representative small-file directory; compare elapsed time vs. Phase 1 baseline.
9. **Doc updates:** README sections on tape footprint, agent contract, notification timing.
10. **Tag** as `phase2-complete`.

### Phase 3 (deferred, sketched only)
- Extract `IPackerIoBackend`.
- Implement `ThreadPoolPackerBackend` (bounded write queue + read prefetcher).
- Wire cancellation through `TapePackerCancellation` (already plumbed in Phase 2 via `CancellationToken`).
- Optional `OverlappedPackerBackend` if profiling shows the worker is the bottleneck.

---

## 6. Open Questions for Review

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
