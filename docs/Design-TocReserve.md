# Design-TocReserve — a TOC reserve that follows the TOC

_Little sister of Design-TocLocation. Status: revision 2 — §4 redesigned after implementation (Group D)._

## 0. Problem and goal

On TOC-in-set layouts the backup arms a logical early-warning (EW) reserve before each set, so content stops while
room for both TOC copies remains. Before this design the reserve was a fixed guess, `TapeNavigator.DefaultTOCCapacity` =
capacity / 400 clamped to 32 MB … 1 GB:

- **Too large, almost always:** 1 GB per LTO volume, 400 MB on DAT-320, for a TOC that is usually a few MB.
- **Too small, rarely — and then fatal:** the TOC grows ~150 B per file. Several million entries across a series exceed
  1 GB; copy 2 is lost, possibly copy 1. The F4 check only warns _after_ the save.

**Goal:** the reserve covers what the TOC will need for exactly the files that reach the tape — no more than a few
entries over — and never falls behind it. Primarily a **safety** feature; the capacity gain is a bonus.

**Out of scope:** TOC-in-partition (no reserve). A pre-backup "will it fit?" forecast (§7).

### Invariants

| # | Invariant |
|---|---|
| TR-1 | **Safe.** Every write of a set leaves room ahead of EOM for the TOC as it would be written if the set ended right after that write: every committed entry, plus every entry the write itself commits. |
| TR-2 | **Monotone.** Within a set the reserve never shrinks; a set starts at a fresh value. |
| TR-3 | The drive's logical-EW state (anchors, tail byte count, poll pacing, sticky latch) survives a reserve change. Only `SetEarlyWarning` (set start) re-selects the mechanism. |
| TR-4 | A reserve change takes effect on the **writing thread**, inside the write it is made for. |
| TR-5 | Estimates are upper bounds: an estimate never undercounts the serialized size. |
| TR-6 | Floor: 16 MiB on LTO drives, 4 MiB otherwise — capped at 1/32 of the medium (test-sized media). An explicit override wins. |
| TR-7 | **Live.** A volume takes every file that fits together with its TOC entry; a volume ends empty only when not even one file and its entry fit. A fresh continuation volume that takes nothing ends the backup with an error — never a loop. |
| TR-8 | **Tight.** The reserve exceeds the TOC's real needs only by: the estimate's upper-bound slack (TR-5), at most one entry for a file cut at the end of the volume, and entries of files that failed after being started. |

## 1. Step 1 — Estimate the TOC's size

`TapeFileInfo.EstimateSerializedSize()` (120 B fixed + name + hash; no front-coding credit), and upward:

```csharp
// TapeSetTOC — upper bound of this set's 2.1 TOC footprint (TR-5); EstimateEntryGrowth for one more entry
internal long EstimateSerializedSize(int padUnit);
internal long EstimateEntryGrowth(long entryBytes);
// TapeTOC — upper bound of one TOC copy: header + every set + end record + CRC envelope (TR-5)
public long EstimateSerializedSize();
```

| Part | Estimate |
|---|---|
| TOC header | 128 B + clamped description + clamped WrittenBy |
| Plain set | 160 B + description + Σ entries (each carries its share of a batch prologue) |
| **Locked** sealed set | exact: stub + stored chunks + chunk records (re-emitted verbatim, E7) |
| **Unlocked** sealed set | plain content + 10 B framing + one whole pad unit + one tag and one record per MiB chunk; never below its last seal |
| End + envelope | 64 B |

One pass over in-memory entries, no I/O. During a set the TOC is not re-summed: entry estimates travel with the files
through the packer (§4).

**Tests (`TocSizeEstimateTests`):** upper bound for every TOC shape (empty, many sets, long/Unicode/ill-formed names,
16-byte hashes, > 1 batch, encrypted unlocked and locked); ≤ 4× the real size for a realistic deep tree; growth additive.

## 2. Step 2 — The navigator says how much to reserve

```csharp
// TapeNavigator
public virtual long TocReserveFor(long tocBytes);       // 0 with a TOC partition; else max(floor, footprint + marks)
public long? TocReserveOverride { get; set; }           // tests, diagnostics: a fixed reserve
internal virtual Func<long, long> CaptureTocReserve();  // the same function, snapshotted for the writing thread (§4)
public static long PredictTocReserve(TapeDrive drive, long tocBytes);   // the service's view, no navigator
```

| Navigator | `TocReserveFor(b)` |
|---|---|
| TOCInPartition | 0, whatever the override |
| TOCInSet (all three) | `max(floor, TOCFootprint(b) + MarkAllowance)`; `TOCFootprint` = two copies rounded to 16 KiB blocks, plus the gap file on the TOC-mark layout |

- `MarkAllowance` = one 16 KiB block (the 5d parity test ran with exactly this).
- floor = 16 MiB if `Drive.LtoGeneration >= 1`, else 4 MiB; capped at 1/32 of `Drive.Capacity`.
- `CaptureTocReserve` reads the floor and the override **once**: `Drive.Capacity` refreshes the media parameters after
  every write — on the writing thread, the planner (§4.3) would pay a device query per call.
- `TOCCapacity` remains as the reserve for an empty TOC (reading) and as an alias of the override (setting).
  `DefaultTOCCapacity` remains for what it still means: the TOC partition at format, and the driver's EOT-zone hint.

## 3. Step 3 — The drive accepts a growing reserve mid-set

Two ways to grow the reserve within a set, both applied on the writing thread and both keeping every runtime state of
the EW mapping (TR-3):

```csharp
public void RaiseEarlyWarning(long bytesBeforeEom);                  // any thread; applied at the next WriteDirect
internal int WriteDirect(byte[] buffer, int offset, int count, bool honorEwReserve,
    out bool tapemark, out bool ew, out bool eom,
    Func<int, long>? reserveForBlocks = null);                       // §4.3: plan the write against a growing reserve
```

- **`RaiseEarlyWarning`** queues the larger value (several raises collapse into the largest); `WriteDirect` applies it as
  its first statement and **invalidates** the cached headroom, so the clamp takes a fresh estimate on that very write.
  (Shifting a possibly stale cache by the delta could let one large write slip past the new reserve.) Dropped by
  `SetEarlyWarning` and on media (un)load. Since Group D the backup no longer uses it; it stays for callers that only
  know the target value.
- **`reserveForBlocks`** (§4.3) is the backup's way: the reserve is chosen inside the write, as a function of how much
  of the buffer is written.

| Regime | What reads the reserve | Effect of a larger reserve |
|---|---|---|
| No reserve (calibration run) | — | ignored (never arms one) |
| Before physical EW | `est ≤ desired` after the write; the clamp before it | fires earlier, never later |
| After physical EW | byte count `EwToEomDistance − bytesSinceEw` | same; anchor untouched |
| Latched | sticky `IsEarlyWarning` | stays latched |
| Overwrite notification | `EstimatedContentRemainingForEw` | unaffected |

**Tests (`LogicalEwReserveRaiseTests`):** both regimes, clamp right after a raise, lower/no-reserve ignored, 1 000 raises
from a `Parallel.For`, lifetime. **Regression gate:** every early-warning test passes unchanged.

## 4. Step 4 — The TOC reserve follows the files through the pipeline  _(revised)_

### 4.1 Why the first design failed

Revision 1 raised the reserve at **hand-off** to the packer, i.e. for every file handed off, committed or not. With small
files, one packer buffer holds thousands of them: on a 256 KiB test volume ~656 files (~200 KB of TOC estimate) were
handed off before the first write, the reserve grew past the whole volume, the first write was clamped to 0 B, and all
656 files rolled back. Every equally sized volume would have ended the same way — **not live**. The hand-off count is
safe but unbounded relative to the volume, because it counts files whose data is not on tape.

The fix: the **only** moment content and TOC compete is the write itself, and it competes only with the entries of files
that write puts on tape. So the decision moves into the write.

### 4.2 One file through the pipeline

| # | When, where (thread) | What happens to the reserve |
|---|---|---|
| 0 | **Set start** — agent, `BeginWriteContentForCurrentSet` | `committed := TOC.EstimateSerializedSize()` (the whole series, the new empty set included). `Drive.SetEarlyWarning(TocReserveFor(committed))`. `Manager.TrackTocReserve(committed)` snapshots the reserve function (§2) for the writing thread. |
| 1 | **Begin file** — agent, `BackupFile` → `Manager.BeginPackedFile(entry)` → `Packer.BeginFile(entry)` | `entry := set.EstimateEntryGrowth(template.EstimateSerializedSize() + 16)`: the file's TOC entry, with the largest hash any algorithm writes. The packer stores it on the file's pending record. Nothing reserved yet. |
| 2 | **File bytes** — agent → packer fill buffer | nothing |
| 3 | **Buffer hand-off** — packer, `DoFlushFillBuffer` (agent thread) | Builds the buffer's **commit plan**: every file not yet counted whose **first byte** lies in or before this buffer, keyed by the block of the buffer that holds that byte (0 when it began in an earlier buffer). Marks them counted. Hands the plan to the backend with the buffer. |
| 4 | **Write** — manager's sink → `Drive.WriteDirect(…, reserveForBlocks)` (writing thread) | `reserveForBlocks(b) = TocReserveFor(committed + plan.TocBytesFor(b))`. The drive writes the largest prefix of `b` blocks with `b × blockSize + reserveForBlocks(b) ≤ estimated remaining`, and the reserve becomes `reserveForBlocks(b)` (never lower than before, TR-2). |
| 5 | **After the write** — sink | `committed += plan.TocBytesFor(blocksWritten)`. |
| 6 | **Harvest** — packer | Files whose bytes all reached the tape are committed (unchanged). |
| 7 | **Set end** — manager, `EndWriteContent` | Tracking ends; the TOC is written into the reserve (it bypasses the clamp — Design-TocLocation TL-8). |

Counting a file at its **first byte**, not at its commit, is what keeps it simple: the first byte is known when the
buffer is planned, a commit is not; and a file that starts in the written part but ends beyond it is at most one entry
(TR-8). A file whose bytes span several buffers is counted in the first and never again.

### 4.3 The write planner (drive, writing thread)

```text
blocks  = bytes to write / blockSize
rFull   = max(desired, reserveForBlocks(blocks))
if no EW latched, a headroom is cached, and bytes + (rFull − desired) < cachedHeadroom / 4:
    desired = rFull; cachedHeadroom −= (rFull − desired)        // cheap path: no device query
else:
    avail = estimated remaining (one query)
    best  = largest b ≤ blocks with b × blockSize + max(desired, reserveForBlocks(b)) ≤ avail
    desired = max(desired, reserveForBlocks(best)); cachedHeadroom = avail − desired
    bytes  = best × blockSize; trimmed = best < blocks
write; a trimmed write reports early warning
```

- **The cheap path is safe:** the clamp's own skip rule rests on "actual headroom ≥ ¾ of the cached one". With
  `bytes + delta < cached/4`, the actual headroom after the raise is ≥ `¾·cached − delta > cached/2 > bytes`.
- **`reserveForBlocks` is a step function** of `b` (it changes only where a file's first byte lies), monotone, and
  cheap: a binary search in the plan and the snapshotted reserve function. The descending scan stops at the first
  fit; at most one scan per buffer, and only near the end of the volume.
- The existing clamp and the post-write evaluation run unchanged after the planner.

### 4.4 Roles

| Component | Owns | Does |
|---|---|---|
| **Agent** | the TOC, the entry estimate | arms the reserve at set start; passes each file's entry estimate at `BeginPackedFile`; decides what an end-of-media means (§4.6) |
| **Packer** | which file's bytes are in which buffer | records the entry per pending file; builds each buffer's commit plan; counts each file once |
| **Write backend** | the hand-off | carries the plan with its buffer to the writing thread — no interpretation |
| **Manager** (sink) | `committed` (writing thread only) | turns the plan into `reserveForBlocks`; adds what the write committed |
| **Drive** | the EW state, the estimate | plans the write (§4.3), applies the reserve, clamps, reports early warning |
| **Navigator** | the layout's footprint | `TocReserveFor` / `CaptureTocReserve` |

### 4.5 Why it is safe, live and tight

- **Safe (TR-1).** A file's entry is in `reserveForBlocks(b)` as soon as the write reaches the block holding its first
  byte — so before any of its bytes is on tape, and a fortiori before it commits. The planner never writes more than
  leaves the room the reserve says. The estimate is an upper bound (TR-5). The TOC written at set end therefore fits.
- **Live (TR-7).** Only entries of files the write actually starts are reserved. On a fresh volume a write is refused
  entirely only if not even the first file's start fits with `committed + its entry` — i.e. the volume genuinely cannot
  take a single file. That case ends the backup with an error instead of asking for yet another volume (§4.6).
- **Tight (TR-8).** Overcount sources: the estimate (upper bound by construction); at most one entry for the file cut
  at the end of the volume — its first byte was written, its last was not, it rolls back to the next volume; and files
  that failed after their first byte reached the tape (source errors) — one entry each.

### 4.6 Scenarios

| Scenario | What happens | Reserve afterwards |
|---|---|---|
| Write fits entirely | written in full, no early warning | `TocReserveFor(committed)` with the buffer's files |
| Write fits partly | planner trims to `best` blocks and reports **early warning** — the sink reports it as EOM, the packer rolls back the files not completely written, the set closes, the TOC is written, the backup continues on the next volume | grown by the files started in the written blocks; never lowered |
| Nothing fits (`best = 0`) | 0 B written, early warning → same EOM path; all files of the buffer roll back | unchanged (nothing started) |
| Drive reports EOM / a write fault mid-write | as before: rollback / lost files | `committed` grows only by the blocks actually written |
| Early warning from the drive (estimate crossed the reserve) after a full write | EOM path; files spanning into the next buffer roll back | includes their entries if their first byte was written — the one-entry overcount |
| A source error discards an open file | `DiscardOpenFile` as before | its entry stays counted if its first byte went out — harmless overcount |
| Retry of a file | a new `BeginFile`, a new entry | the discarded attempt as above; the retry counted once |
| Incremental, file up to date | never reaches `BeginFile` | not counted |
| **Fresh continuation volume takes no file** | the first write refused (`best = 0`) → EOM with an empty continuation set → **the agent fails the backup** with `ERROR_DISK_FULL`: "volume #n holds not a single file of the set together with its table of contents". Without this guard, equally sized volumes would be requested forever. | — |
| TOC in partition | the reserve function is 0, `desired` stays 0 → no planning at all | 0 |
| Fixed override (5d fixture) | `reserveForBlocks` is the constant → behaves exactly as a fixed reserve | the override |

**When can "adjusting the reserve" fail?** It cannot fail as an operation — it is a number on the writing thread. What
can happen is that the grown reserve leaves no room for the write: that is the trimmed/empty write above, reported as
early warning and handled by the ordinary end-of-volume path. The reserve is never lowered to make room (TR-2); a
volume ends instead.

### 4.7 Tests

| Test | Proves |
|---|---|
| `TocCommitPlanTests` | `TocBytesFor` sums exactly the entries due by `b` blocks; empty and unsorted input |
| `TocReservePlannedWriteTests` (drive) | a write that fits raises the reserve to `reserveForBlocks(all)`, no EW; a write that does not fit is trimmed to the largest fitting prefix, reports EW, reserve = `reserveForBlocks(best)`; `best = 0` writes nothing and reports EW; the reserve never drops; no reserve armed → the function is ignored |
| `TocReserveAgentTests` | **safety:** 800 long-named files on 256 KiB volumes, live reserve — every volume's TOC far beyond one block, both copies everywhere, no copy anomaly, the series restores byte for byte; **liveness:** every volume of that series takes files; **no loop:** a reserve larger than any volume ends the backup with the "not a single file" error; **encrypted series:** both copies everywhere; full set raises past the floor, unchanged incremental arms at the set-start value and does not grow; Partitions arms none |
| Service | the reserve reported equals the floor rule; `WritableRemaining = estimated − reserve`; the property row |

## 5. Step 5 — Service reports the reserve it will use

- `TocSizeEstimate` (cached per TOC state) and `TocReserve` = `PredictTocReserve(drive, TocSizeEstimate)` — the reserve
  the **next** backup starts with.
- `Used` and `ComputeWritableRemaining` deduct `TocReserve`.
- One property row, "TOC Reserve" — e.g. `4 MiB (TOC ~1.2 MiB)`; WPF and CLI render it with no code change.

## 6. Files

| File | Change |
|---|---|
| `Toc/TapeTOC.Format.cs` | estimates (§1) |
| `Media/TapeNavigator.cs` | `TocReserveFor`, override, `CaptureTocReserve`, `PredictTocReserve` (§2) |
| `Drive/TapeDrive.cs` | `RaiseEarlyWarning` (§3); the write planner (§4.3) |
| `Packer/ITapeWriteBackend.cs`, `WorkerThreadTapeWriteBackend.cs`, `MemoryTapeWriteBackend.cs` | the plan travels with the buffer |
| `Packer/TocCommitPlan.cs` | new |
| `Packer/TapeFileWritePacker.cs` | entry per pending file; plan per buffer |
| `Media/TapeStreamManager.cs` | `TrackTocReserve`; the sink plans |
| `Agents/TapeFileBackupAgent.cs` | arm at set start; entry at `BeginPackedFile`; the continuation guard |
| `Services/TapeServiceBase*.cs` | §5 |

## 7. Deliberately not done

- **Aggregator-based forecast** before the backup — advisory only, a later phase.
- **Front-coding credit in the estimate.** Sound only with an aggregate bound on batch restarts (the real writer
  restarts sharing at 4096 files or 1 MiB of batch body, which the estimate cannot place per entry). With §4 the
  estimate's slack costs capacity only, never liveness or safety. A later phase.
- **Shrinking mid-set** (TR-2) and **per-copy layout tuning** (both copies identical by design).

## 8. Changes during implementation

- **Slack and mark allowance (Group C).** One 16 KiB block of mark allowance (not 6): on a 256 KiB test volume a larger
  allowance would have reserved most of it. The hand-off slack of revision 1 is gone with §4.
- **Floor cap 1/32 of the medium** (was ¼). Only test-sized media are affected.
- **Raise handling (Group B).** A raise invalidates the cached headroom instead of shifting it (§3).
- **No WPF or CLI change** for the service figures: both render the property sheets row by row.
- **Revision 2 (Group D): the reserve is planned in the write**, not raised at hand-off (§4.1). `RaiseEarlyWarning`
  stays in the drive, unused by the backup.
