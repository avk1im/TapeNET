# Design-TocReserve — a TOC reserve that follows the TOC

_Little sister of Design-TocLocation. Status: **implemented** (toc-reserve branch, Groups A–D). This document describes
the design as built; §8 records how it got there._

## 0. Problem and goal

On TOC-in-set layouts (Setmarks, SeqFilemarks, FilemarksOnly) both TOC copies are written behind the content on the same
partition. A backup therefore stops content early enough to leave room for them: the drive's logical early warning (EW)
is armed with a **TOC reserve**, and content writes stop when the estimated remaining capacity reaches it.

The reserve used to be a fixed guess (`TapeNavigator.DefaultTOCCapacity`: capacity / 400, clamped to 32 MB … 1 GB):

- **Too large, almost always:** 1 GB per LTO volume for a TOC that is usually a few MB.
- **Too small, rarely — and then fatal:** the TOC grows ~150 B per file; millions of entries outgrow 1 GB, copy 2 is
  lost, possibly copy 1.

**The reserve now follows the TOC.** It is armed at set start from the TOC as it stands, and every content write grows it
by the TOC entries of exactly the files that write puts on tape. It is primarily a **safety** feature; the capacity gain is
a bonus.

**Out of scope:** TOC-in-partition (no reserve: the TOC has its own partition). A pre-backup "will it fit?" forecast (§7).

### Invariants

| # | Invariant |
|---|---|
| TR-1 | **Safe.** Every content write leaves room ahead of EOM for the TOC as it would be written if the set ended right after that write: every committed entry, plus every entry the write itself commits. |
| TR-2 | **Monotone.** Within a set the reserve never shrinks; a set starts at a fresh value. |
| TR-3 | The drive's logical-EW state (physical-EW anchor, tail byte count, poll pacing, sticky latch) survives a reserve change. Only `SetEarlyWarning` (set start) re-selects the mechanism. |
| TR-4 | A reserve change takes effect on the **writing thread**, inside the write it is made for. |
| TR-5 | Estimates are upper bounds: an estimate never undercounts the serialized size. |
| TR-6 | Floor: 16 MiB on LTO drives, 4 MiB otherwise — capped at 1/32 of the medium. An explicit override replaces the computed reserve. |
| TR-7 | **Live.** A volume takes every file that fits together with its TOC entry; it ends empty only when not even one file and its entry fit. A fresh continuation volume that takes nothing ends the backup with an error — never a loop. |
| TR-8 | **Tight.** The reserve exceeds the TOC's real needs only by the estimate's upper-bound slack (TR-5), at most one entry for a file cut at the end of the volume, and the entries of files that failed after being started. |

## 1. Estimating the TOC's size

`TapeTOC.EstimateSerializedSize()` gives an upper bound of one TOC copy; `TapeSetTOC.EstimateSerializedSize` and
`TapeSetTOC.EstimateEntryGrowth` do the same per set and per added entry (`Toc/TapeTOC.Format.cs`). The per-entry base
is `TapeFileInfo.EstimateSerializedSize()`: a fixed part, the full name and the hash, **without front-coding credit**.

| Part | Estimate |
|---|---|
| TOC header | fixed part + clamped description + clamped WrittenBy |
| Plain set | fixed part + description + Σ entries (each carries its share of a batch prologue) |
| **Locked** sealed set | exact: stub + stored chunks + chunk records (re-emitted verbatim, Design-Encryption E7) |
| **Unlocked** sealed set | plain content + framing + one whole pad unit + one tag and one record per chunk; never below its last seal |
| End + envelope | fixed |

One pass over in-memory entries, no I/O. During a set the TOC is not re-summed: the entry estimates travel with the files
through the packer (§4).

The estimate overcounts a realistic tree by roughly 2× (no shared-prefix credit) — capacity, never safety or liveness.

**Tests:** `TocSizeEstimateTests` — upper bound for every TOC shape (empty, many sets, long/Unicode/ill-formed names,
largest hash, more than one batch, locked and unlocked encrypted sets); within 4× of the real size for a realistic deep
tree; growth additive.

## 2. The navigator says how much to reserve

`TapeNavigator` (`Media/TapeNavigator.cs`), region *TOC reserve*:

| Member | Role |
|---|---|
| `TocReserveFor(long tocBytes)` | the reserve this layout needs for a TOC of `tocBytes` (one copy) |
| `TocReserveOverride` | a fixed reserve replacing the computed one (tests, diagnostics); copied by `TapeStreamManager.RenewNavigator` |
| `CaptureTocReserve()` | the same function, snapshotted for the writing thread (floor and override read once) |
| `PredictTocReserve(TapeDrive, long)` | the service's view without a navigator, by the same layout decision as `ProduceNavigator` |
| `TOCFootprint(long)` | two copies rounded to whole 16 KiB blocks; the TOC-mark layout adds the gap file |
| `TocReserveFloor`, `TocReserveFloorLto`, `TocMarkAllowance` | 4 MiB, 16 MiB, one header block |

| Navigator | `TocReserveFor(b)` |
|---|---|
| `TapeNavigatorTOCInPartition` | 0, whatever the override |
| the three TOC-in-set navigators | `max(floor, TOCFootprint(b) + TocMarkAllowance)` |

The floor is `TocReserveFloorFor`: 16 MiB on LTO drives, 4 MiB otherwise, never more than 1/32 of `Drive.Capacity` — so
small (virtual) media lose little to it; from 128 MiB up the cap has no effect.

`CaptureTocReserve` exists because `Drive.Capacity` refreshes the media parameters after every write: the planner (§4.3)
evaluates the function many times per buffer and must not query the device for it.

`TOCCapacity` remains as the reserve for an empty TOC (get) and an alias of the override (set). `DefaultTOCCapacity`
remains for what it still means: the TOC partition size at format, and the driver's end-of-tape zone hint.

**Tests:** `TocReserveForTests` — per layout; floor and 1/32 cap; override wins; the gap file on the TOC-mark layout;
the override survives a navigator renewal, a computed reserve is not frozen into one.

## 3. The drive accepts a growing reserve mid-set

`TapeDrive` (`Drive/TapeDrive.cs`), region *Early Warning*. Two ways to grow the reserve within a set; both are applied on
the writing thread and keep every runtime state of the EW mapping (TR-3):

- **`RaiseEarlyWarning(long)`** — callable from any thread. The value is queued (several raises collapse into the largest)
  and applied by `ApplyPendingEarlyWarning`, the first statement of `WriteDirect`. Applying a raise **invalidates** the
  cached headroom (`m_writableHeadroomAtLastPoll`), so the clamp takes a fresh estimate on that very write: shifting a
  possibly stale cache by the raise could let one large write slip past the new reserve. A pending raise is dropped by
  `SetEarlyWarning` and `ResetEarlyWarningRuntime` (media (un)load, calibration). A raise never arms a reserve and
  never lowers one. The backup no longer uses it (§8); it stays for callers that only know a target value.
- **The planned write** — `WriteDirect(…, honorEwReserve, …, Func<int, long>? reserveForBlocks)`, the backup's way (§4.3).

| Regime | What reads the reserve | Effect of a larger reserve |
|---|---|---|
| No reserve (calibration run, TOC partition) | — | ignored |
| Before physical EW | the clamp before the write; the estimate after it | fires earlier, never later |
| After physical EW | byte count `EwToEomDistance − bytesSinceEw` | the same; anchor untouched |
| Latched | sticky `IsEarlyWarning` | stays latched |
| Overwrite notification | `EstimatedContentRemainingForEw` | unaffected |

**Tests:** `LogicalEwReserveRaiseTests` — both regimes; the clamp right after a raise; lower and no-reserve raises
ignored; 1 000 raises from a `Parallel.For` while writing; lifetime of a pending raise. Every existing early-warning test
(`CalibrationAndLogicalEwTests`, `VirtualDriveEarlyWarningTests`) passes unchanged.

## 4. The TOC reserve follows the files through the pipeline

### 4.1 Why the decision sits in the write

The reserve could grow as files are handed to the packer — but one packer buffer can hold thousands of small files, whose
entries would then be reserved before any of their data reaches the tape. On a small volume that reserve outgrows the
volume, the first write is clamped to nothing, and every equally sized volume ends the same way. (This was revision 1;
§8.)

Content and TOC compete only in a write, and only for the entries of the files that write puts on tape. So the decision is
made **in the write**.

### 4.2 One file through the pipeline

| # | When, where (thread) | What happens to the reserve |
|---|---|---|
| 0 | **Set start** — `TapeFileBackupAgent.ArmTocReserve`, from `BeginWriteContentForCurrentSet` (agent thread) | the series TOC's estimate (every set of every volume, the new empty set included) arms `Drive.SetEarlyWarning(Navigator.TocReserveFor(toc))`; `TapeStreamManager.TrackTocReserve(toc)` snapshots the reserve function for the writing thread. TOC partition: 0, nothing tracked. |
| 1 | **Begin file** — `TapeFileBackupAgent.BackupFile` → `TapeStreamManager.BeginPackedFile(tocEntryBytes)` → `TapeFileWritePacker.BeginFile(tocEntryBytes)` | the file's entry estimate, `EstimateEntryGrowth(template.EstimateSerializedSize() + c_maxHashBytes)` (the hash does not exist yet: the largest is counted), is stored on the file's pending record. Nothing is reserved yet. |
| 2 | **File bytes** — into the packer's fill buffer | nothing |
| 3 | **Buffer hand-off** — `TapeFileWritePacker.DoFlushFillBuffer` → `BuildTocCommitPlan` (agent thread) | every file not yet counted whose **first byte** lies in or before this buffer enters the buffer's `TocCommitPlan`, keyed by the number of the buffer's blocks needed to reach that byte (0 when it began in an earlier buffer). Each file is counted once. The plan travels with the buffer (`ITapeWriteBackend.StartWriting(…, plan)`, `WorkerThreadTapeWriteBackend`). |
| 4 | **Write** — `TapeStreamManager.PackerWriteSink` → `TapeDrive.WriteDirect(…, reserveForBlocks)` (writing thread) | `reserveForBlocks(b) = reserveFor(committed + plan.TocBytesFor(b))`. The drive writes the largest prefix `b` with `b × blockSize + reserveForBlocks(b) ≤ estimated remaining`; the reserve becomes `reserveForBlocks(b)` (§4.3). |
| 5 | **After the write** — the sink | `committed += plan.TocBytesFor(blocks written)` |
| 6 | **Harvest** — the packer | files whose bytes all reached the tape are committed (unchanged) |
| 7 | **Set end** — `TapeStreamManager.EndWriteContent` | tracking ends; the TOC is written into the reserve, which TOC writes bypass (Design-TocLocation TL-8) |

Counting a file at its **first byte** rather than at its commit keeps it simple: the first byte is known when the buffer is
planned, a commit is not; and a file that starts in the written part but ends beyond it costs at most one entry (TR-8).

`TocCommitPlan` (`Packer/TocCommitPlan.cs`) is immutable, built on the agent thread and read on the writing thread:
`TocBytesFor(blocks)` is a binary search over cumulative sums.

### 4.3 The write planner

`TapeDrive.PlanWriteAgainstTocReserve`, called from `WriteDirect` before the clamp (`ClampWriteToEarlyWarning`), only
for content writes (`honorEwReserve`) with a reserve armed:

- **Cheap path** — no device query — when the write plus the reserve's growth is well inside the headroom cached at the
  last poll (below a quarter of it). The clamp's own skip rule ("actual headroom ≥ ¾ of the cached one") then still leaves
  more than the write; the reserve is raised and the cached headroom reduced by the growth.
- **Slow path** — one estimate (`EstimatedContentRemainingForEw`), then a descending scan for the largest fitting prefix.
  `reserveForBlocks` is a monotone step function, cheap to evaluate, so the scan costs no I/O. The chosen reserve is
  applied, the cached headroom reset to `available − reserve`.
- A **trimmed** write (fewer blocks than offered, possibly none) reports early warning: the set must end there.
- The existing clamp and the post-write early-warning evaluation run unchanged after the planner.

### 4.4 Roles

| Component | Owns | Does |
|---|---|---|
| **Agent** (`TapeFileBackupAgent`) | the TOC, the entry estimate | arms the reserve at set start; passes each file's entry estimate to `BeginPackedFile`; decides what an end-of-media means (§4.6) |
| **Packer** (`TapeFileWritePacker`) | which file's bytes are in which buffer | records the entry per pending file; builds each buffer's commit plan; counts each file once |
| **Write backend** (`WorkerThreadTapeWriteBackend`) | the hand-off | carries the plan with its buffer to the writing thread, uninterpreted |
| **Stream manager** (`TapeStreamManager`) | the committed TOC estimate (writing thread), the snapshotted reserve function | turns the plan into `reserveForBlocks`; adds what the write committed |
| **Drive** (`TapeDrive`) | the EW state, the estimate | plans the write, applies the reserve, clamps, reports early warning |
| **Navigator** | the layout's footprint | `TocReserveFor` / `CaptureTocReserve` |

### 4.5 Why it is safe, live and tight

- **Safe (TR-1).** A file's entry is in `reserveForBlocks(b)` as soon as the write reaches the block holding its first
  byte — before any of its bytes is on tape, and so before it can commit. The planner never writes more than leaves room
  for that reserve, and the estimate is an upper bound (TR-5). The TOC written at set end therefore fits.
- **Live (TR-7).** Only entries of files a write actually starts are reserved. A write is refused entirely only when not
  even the first file's start fits next to the committed TOC plus its entry — the volume genuinely cannot take a file.
- **Tight (TR-8).** Overcount sources: the estimate; at most one entry for the file cut at the end of the volume (its first
  byte was written, it rolls back to the next volume); one entry per file that failed after its first byte went out.

### 4.6 Scenarios

| Scenario | What happens | Reserve afterwards |
|---|---|---|
| Write fits entirely | written in full, no early warning | grown by the buffer's started files |
| Write fits partly | trimmed, early warning → the sink reports end-of-media; the packer rolls back the files not completely written; the set closes; the TOC is written; the backup continues on the next volume | grown by the files started in the written blocks |
| Nothing fits | 0 B written, early warning → the same path; every file of the buffer rolls back | unchanged |
| End-of-media or write fault from the drive | as before: rollback, or lost files | `committed` grows only by the blocks written |
| **Final flush crosses early warning with every file committed** | the set is complete on this volume; **no continuation** (the agent's final-flush handler) | — |
| **Set header does not fit ahead of the reserve** (new set on a full volume) | the volume is full before the set; continue on the next volume, as for a trimmed first content write (`ContinueOnNextVolumeBeforeSet`) | — |
| **Fresh continuation volume takes no file** | the backup ends with `ERROR_DISK_FULL` ("holds not a single file … together with its table of contents") instead of asking for volumes forever | — |
| A source error discards an open file | `DiscardOpenFile`, as before | its entry stays counted if its first byte went out |
| Retry of a file | a new `BeginFile`, a new entry | the discarded attempt as above |
| Incremental, file up to date | never reaches `BeginFile` | not counted |
| TOC in partition | reserve 0; no planning | 0 |
| Fixed override | `reserveForBlocks` is a constant: a fixed reserve | the override |

Adjusting the reserve cannot fail as an operation — it is a number on the writing thread. What can happen is that the
grown reserve leaves no room for the write; that is the trimmed or empty write above, handled by the ordinary end-of-volume
path. The reserve is never lowered to make room (TR-2): a volume ends instead.

### 4.7 Tests

| Suite | Proves |
|---|---|
| `TocCommitPlanTests` | `TocBytesFor` sums exactly the entries due by `b` blocks; empty and unsorted input |
| `TocReservePlannedWriteTests` | a fitting write grows the reserve without early warning; an overlong one is trimmed to the largest fitting prefix and reports it; nothing fits → nothing written; never lowered; no reserve armed → the function is ignored; writing to the end leaves room for every written entry |
| `TocReserveAgentTests` | **safe and live** on the three in-set layouts: 800 long-named files on equal 1 MiB volumes — both TOC copies on every volume, every volume takes files, the series restores; an **encrypted** series; **no loop** — a reserve beyond every volume ends with the "not a single file" error; a full set raises past the floor, an unchanged incremental keeps exactly its armed reserve; Partitions arms none; **mixed volume sizes** (2 MiB / 1 MiB, both orders, all four layouts) — each volume used to its own capacity, nothing stale from the previous cartridge |
| `ServiceTocReserveTests` | the reserve reported equals the floor rule; `WritableRemaining = estimated − reserve`; the property row |
| `MultiVolumeBackupRestoreTests` | `TwoSets_DifferentVolumes_…`, `SecondSet_OnAVolumeFullToTheReserve_…`, the parity guard `Fixture_EveryVolumeHoldsBothTocCopies` — the boundary cases of §4.6 on every layout and header mode |

The multi-volume fixture gives every volume its own capacity (`VolumeCapacity` / `CapacityOfVolume`; default all equal)
and runs either the live reserve (`UseLiveTocReserve`) or the fixed 5d parity reserve (`TOCCapacityOverride` / layout
default). After every backup it checks the saved TOC's structure (`TocInvariants.AssertWellFormed`; opt out with
`ValidatesTocAfterBackup = false`).

## 5. The service reports the reserve it will use

`TapeServiceBase` (`Services/TapeServiceBase.cs`):

- `TocSizeEstimate` — the loaded TOC's estimate, cached per TOC state (instance, last save time, set count).
- `TocReserve` — `TapeNavigator.PredictTocReserve(drive, TocSizeEstimate)`: the reserve the **next** backup starts with;
  0 without media or with a TOC partition.
- `Used` and `ComputeWritableRemaining` deduct `TocReserve`; `WritableRemaining` follows.
- One property row, "TOC Reserve". WPF and CLI render the property lists row by row, so they need no change.

## 6. Files

| Area | Files |
|---|---|
| Estimate | `Toc/TapeTOC.Format.cs` |
| Navigator | `Media/TapeNavigator.cs` |
| Drive | `Drive/TapeDrive.cs` |
| Packer pipeline | `Packer/TocCommitPlan.cs` (new), `Packer/TapeFileWritePacker.cs`, `Packer/ITapeWriteBackend.cs`, `Packer/WorkerThreadTapeWriteBackend.cs`, `Packer/MemoryTapeWriteBackend.cs` |
| Stream manager | `Media/TapeStreamManager.cs` |
| Agent | `Agents/TapeFileBackupAgent.cs` |
| Service | `Services/TapeServiceBase.cs`, property sheets |
| Tests | `TocSizeEstimateTests`, `TocReserveForTests`, `LogicalEwReserveRaiseTests`, `TocCommitPlanTests`, `TocReservePlannedWriteTests`, `TocReserveAgentTests`, `ServiceTocReserveTests`; fixtures `MultiVolumeVirtualTapeFixture`, `TocInvariants` |

## 7. Deliberately not done

- **Pre-backup forecast.** Estimating the TOC after the backup before starting (Σ path lengths from the size aggregator)
  could warn "this volume will not hold the TOC". Advisory only; a later phase.
- **Front-coding credit in the estimate.** Sound only with an aggregate bound on batch restarts (the writer restarts the
  shared prefix at 4096 files or 1 MiB of batch body, which a per-entry estimate cannot place). It would recover the ~2×
  overcount; it affects capacity only.
- **Shrinking mid-set** (TR-2), **per-copy layout tuning** (both copies are identical by design).

## 8. History

| Step | Change |
|---|---|
| Group A | Estimate (§1) and navigator (§2). Floor capped at 1/32 of the medium — first ¼, which on tiny test volumes cost a quarter of the capacity for a TOC of a few KiB. Mark allowance one block. |
| Group B | `RaiseEarlyWarning` (§3). A raise invalidates the cached headroom rather than shifting it by the raise: the cache can be up to one poll interval stale. |
| Group C | **Revision 1**: the agent raised the reserve at hand-off to the packer, with a slack. Failed on small files — the reserve outgrew a small volume before a single file committed (§4.1). |
| Group D | **Revision 2**: the reserve is planned in the write (§4). `RaiseEarlyWarning` stays in the drive, unused by the backup. |
| Follow-ups | The final-flush, set-header and fresh-continuation cases of §4.6; the multi-volume fixture without padded continuation volumes, with per-volume capacities; a reload clears the drive's cached content capacity (`TapeDrive.ReloadMedia` → `InvalidateContentCache`). |
