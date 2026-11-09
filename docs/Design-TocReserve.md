# Design-TocReserve — a TOC reserve that follows the TOC

_Little sister of Design-TocLocation. Status: **implemented** (toc-reserve branch: Groups A–D, then §9 Steps 1–3). This
document describes the design as built; §8 records how it got there._

## 0. Problem and goal

On TOC-in-set layouts (Setmarks, SeqFilemarks, FilemarksOnly) both TOC copies are written behind the content on the same
partition. A backup therefore stops content early enough to leave room for them: the drive's logical early warning (EW)
is armed with a **TOC reserve**, and content writes stop when the estimated remaining capacity reaches it.

The reserve used to be a fixed guess (`TapeNavigator.DefaultTOCCapacity`: capacity / 400, clamped to 32 MB … 1 GB):

- **Too large, almost always:** 1 GB per LTO volume for a TOC that is usually a few MB.
- **Too small, rarely — and then fatal:** the TOC grows ~150 B per file; millions of entries outgrow 1 GB, copy 2 is
  lost, possibly copy 1.

**The reserve now follows the TOC.** It is armed at set start from the TOC as it stands, and every content write grows it
by the TOC entries of exactly the files that write puts on tape. On partitioned media the same tracking keeps the TOC
within its partition (§9). Primarily a **safety** feature; the capacity gain is a bonus.

**Out of scope:** a pre-backup "will it fit?" forecast (§7).

### Invariants

| # | Invariant |
|---|---|
| TR-1 | **Safe.** Every content write leaves room for the TOC as it would be written if the set ended right after that write: every committed entry, plus every entry the write itself commits — ahead of EOM (TOC in set), or within the TOC partition (§9). |
| TR-2 | **Monotone.** Within a set the reserve never shrinks; a set starts at a fresh value. |
| TR-3 | The drive's logical-EW state (physical-EW anchor, tail byte count, poll pacing, sticky latch) survives a reserve change. Only `SetEarlyWarning` (set start) re-selects the mechanism. |
| TR-4 | A reserve change takes effect on the **writing thread**, inside the write it is made for. |
| TR-5 | Estimates are upper bounds: an estimate never undercounts the serialized size. |
| TR-6 | Floor: 16 MiB on LTO drives, 4 MiB otherwise — capped at 1/32 of the medium. An explicit override replaces the computed reserve. |
| TR-7 | **Live.** A volume takes every file that fits together with its TOC entry; it ends empty only when not even one file and its entry fit. A fresh continuation volume that takes nothing ends the backup with an error — never a loop. |
| TR-8 | **Tight.** The reserve exceeds the TOC's real needs only by the estimate's upper-bound slack (TR-5, at most 50 B per entry), at most one entry for a file cut at the end of the volume, and the entries of files that failed after being started. |

## 1. Estimating the TOC's size

`TapeTOC.EstimateSerializedSize()` gives an upper bound of one TOC copy; `TapeSetTOC.EstimateSerializedSize` and
`TapeSetTOC.EstimateEntryGrowth` do the same per set and per added entry (`Toc/TapeTOC.Format.cs`). Each file entry is
bounded by `TocEntrySizing.GroupBound` — by arithmetic, with its real shared-prefix credit (§9.4).

| Part | Estimate |
|---|---|
| TOC header | fixed part + clamped description + clamped WrittenBy |
| Plain set | fixed part + description + Σ `GroupBound` per entry, batch prologues included — the writer's own batching (§9.4) |
| **Locked** sealed set | exact: stub + stored chunks + chunk records (re-emitted verbatim, Design-Encryption E7) |
| **Unlocked** sealed set | plain content + framing + one whole pad unit + one tag and one record per chunk; never below its last seal |
| End + envelope | fixed |

One pass over in-memory entries, no I/O, no serialization. During a set the TOC is not re-summed: the entry bounds travel
with the files through the packer (§4).

`TapeFileInfo.EstimateSerializedSize()` (120 B + name + hash, no front-coding credit) remains as a coarse figure; the TOC
no longer uses it.

**Tests:** `TocSizeEstimateTests` — upper bound for every TOC shape (empty, many sets, long/Unicode/ill-formed names,
largest hash, more than one batch, locked and unlocked encrypted sets); within 4× of the real size for a realistic deep
tree; each entry adds exactly what the meter measured. `TocTightEntryTests` — §9.7.

## 2. The navigator says how much to reserve

`TapeNavigator` (`Media/TapeNavigator.cs`), region *TOC reserve*:

| Member | Role |
|---|---|
| `TocReserveFor(long tocBytes)` | the reserve this layout needs for a TOC of `tocBytes` (one copy) |
| `TocReserveOverride` | a fixed reserve replacing the computed one (tests, diagnostics); copied by `TapeStreamManager.RenewNavigator` |
| `CaptureTocLimits()` | the layout's limits, snapshotted for the writing thread: `TocLimits(ReserveFor, Budget)` — the reserve function (floor and override read once) and the TOC partition budget (§9.2) |
| `PredictTocReserve(TapeDrive, long)` | the service's view without a navigator, by the same layout decision as `ProduceNavigator` |
| `TOCFootprint(long)`, `CopiesFootprint(long)` | two copies rounded to whole 16 KiB blocks; the TOC-mark layout adds the gap file |
| `TocReserveFloor`, `TocReserveFloorLto`, `TocMarkAllowance` | 4 MiB, 16 MiB, one header block |

| Navigator | `TocReserveFor(b)` | Budget |
|---|---|---|
| `TapeNavigatorTOCInPartition` | 0, whatever the override | `InitiatorCapacity − TocMarkAllowance` |
| the three TOC-in-set navigators | `max(floor, TOCFootprint(b) + TocMarkAllowance)` | none (`long.MaxValue`) |

The floor is `TocReserveFloorFor`: 16 MiB on LTO drives, 4 MiB otherwise, never more than 1/32 of `Drive.Capacity` — so
small (virtual) media lose little to it; from 128 MiB up the cap has no effect.

`CaptureTocLimits` exists because `Drive.Capacity` refreshes the media parameters after every write: the planner (§4.3)
evaluates the reserve function many times per buffer and must not query the device for it.

`TOCCapacity` remains as the reserve for an empty TOC (get) and an alias of the override (set). `DefaultTOCCapacity`
remains for what it still means: the TOC partition size at format, and the driver's end-of-tape zone hint.

**Tests:** `TocReserveForTests` — per layout; floor and 1/32 cap; override wins; the gap file on the TOC-mark layout;
the override survives a navigator renewal, a computed reserve is not frozen into one.

## 3. The drive accepts a growing reserve mid-set

`TapeDrive` (`Drive/TapeDrive.cs`), region *Early Warning*. Two ways to grow the reserve within a set; both are applied on
the writing thread and keep every runtime state of the EW mapping (TR-3):

- **`RaiseEarlyWarning(long)`** — callable from any thread. The value is queued (several raises collapse into the largest)
  and applied by `ApplyPendingEarlyWarning`, the first statement of `WriteDirect`. Applying a raise **invalidates** the
  cached headroom (`m_writableHeadroomAtLastPoll`), so the clamp takes a fresh estimate on that very write. A pending raise
  is dropped by `SetEarlyWarning` and `ResetEarlyWarningRuntime` (media (un)load, calibration). A raise never arms a
  reserve and never lowers one. The backup no longer uses it (§8); it stays for callers that only know a target value.
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
| 0 | **Set start** — `TapeFileBackupAgent.ArmTocReserve`, from `BeginWriteContentForCurrentSet` (agent thread) | the meter starts the set (`TocEntryMeter.Reset`); the series TOC's estimate (every set of every volume, the new empty set included) arms `Drive.SetEarlyWarning(Navigator.TocReserveFor(toc))`; `TapeStreamManager.TrackTocReserve(toc)` snapshots the layout's limits for the writing thread. |
| 1 | **Begin file** — `TapeFileBackupAgent.BackupFile` → `TapeStreamManager.BeginPackedFile(tocEntryBytes)` → `TapeFileWritePacker.BeginFile(tocEntryBytes)` | the file's entry bound, `EstimateEntryGrowth(m_tocMeter.Measure(template))`, is stored on the file's pending record; after a successful hand-off the meter accepts it (§9.4). Nothing is reserved yet. |
| 2 | **File bytes** — into the packer's fill buffer | nothing |
| 3 | **Buffer hand-off** — `TapeFileWritePacker.DoFlushFillBuffer` → `BuildTocCommitPlan` (agent thread) | every file not yet counted whose **first byte** lies in or before this buffer enters the buffer's `TocCommitPlan`, keyed by the number of the buffer's blocks needed to reach that byte (0 when it began in an earlier buffer). Each file is counted once. The plan travels with the buffer (`ITapeWriteBackend.StartWriting(…, plan)`). |
| 4 | **Write** — `TapeStreamManager.PackerWriteSink` → `TapeDrive.WriteDirect(…, reserveForBlocks)` (writing thread) | TOC partition: the prefix is first trimmed to the budget (§9.2). Then `reserveForBlocks(b) = ReserveFor(committed + plan.TocBytesFor(b))`; the drive writes the largest prefix `b` with `b × blockSize + reserveForBlocks(b) ≤ estimated remaining`; the reserve becomes `reserveForBlocks(b)` (§4.3). |
| 5 | **After the write** — the sink | `committed += plan.TocBytesFor(blocks written)`; on a stop, `LastContentStopReason` (§9.6) |
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
  `reserveForBlocks` is a monotone step function, cheap to evaluate, so the scan costs no I/O.
- A **trimmed** write (fewer blocks than offered, possibly none) reports early warning: the set must end there.
- The existing clamp and the post-write early-warning evaluation run unchanged after the planner.

### 4.4 Roles

| Component | Owns | Does |
|---|---|---|
| **Agent** (`TapeFileBackupAgent`) | the TOC, the entry meter | arms the reserve at set start; passes each file's entry bound to `BeginPackedFile`; decides what an end-of-media means (§4.6); states why a volume ended (§9.6) |
| **Packer** (`TapeFileWritePacker`) | which file's bytes are in which buffer | records the entry per pending file; builds each buffer's commit plan; counts each file once |
| **Write backend** (`WorkerThreadTapeWriteBackend`) | the hand-off | carries the plan with its buffer to the writing thread, uninterpreted |
| **Stream manager** (`TapeStreamManager`) | the committed TOC estimate (writing thread), the snapshotted limits | turns the plan into `reserveForBlocks`; trims to the budget; adds what the write committed; records the stop reason |
| **Drive** (`TapeDrive`) | the EW state, the estimate, the initiator capacity | plans the write, applies the reserve, clamps, reports early warning |
| **Navigator** | the layout's footprint | `TocReserveFor` / `CaptureTocLimits` |
| **Service** (`TapeServiceBase`) | the advice | words volume ends, advises on volume space (§9.6) |

### 4.5 Why it is safe, live and tight

- **Safe (TR-1).** A file's entry is in `reserveForBlocks(b)` as soon as the write reaches the block holding its first
  byte — before any of its bytes is on tape, and so before it can commit. The planner never writes more than leaves room
  for that reserve, and the bound is an upper bound (TR-5). The TOC written at set end therefore fits.
- **Live (TR-7).** Only entries of files a write actually starts are reserved. A write is refused entirely only when not
  even the first file's start fits next to the committed TOC plus its entry — the volume genuinely cannot take a file.
- **Tight (TR-8).** Overcount sources: the bound (≤ 50 B per entry); at most one entry for the file cut at the end of the
  volume; one entry per file that failed after its first byte went out.

### 4.6 Scenarios

| Scenario | What happens | Reserve afterwards |
|---|---|---|
| Write fits entirely | written in full, no early warning | grown by the buffer's started files |
| Write fits partly | trimmed, early warning → the sink reports end-of-media; the packer rolls back the files not completely written; the set closes; the TOC is written; the backup continues on the next volume | grown by the files started in the written blocks |
| Nothing fits | 0 B written, early warning → the same path; every file of the buffer rolls back | unchanged |
| End-of-media or write fault from the drive | as before: rollback, or lost files | `committed` grows only by the blocks written |
| **Final flush crosses early warning with every file committed** | the set is complete on this volume; **no continuation** | — |
| **Set header does not fit ahead of the reserve** (new set on a full volume) | the volume is full before the set; continue on the next volume (`ContinueOnNextVolumeBeforeSet`) | — |
| **TOC partition budget reached** (§9) | as "write fits partly", reason `TocPartitionFull` | — |
| **Fresh continuation volume takes no file** | the backup ends with `ERROR_DISK_FULL` (`FreshVolumeTookNoFile`) instead of asking for volumes forever; the service reports it as failed, with advice (§9.6) | — |
| A source error discards an open file | `DiscardOpenFile`, as before; the meter does not accept it | its entry stays counted if its first byte went out |
| Retry of a file | a new `BeginFile`, a new entry | the discarded attempt as above |
| Incremental, file up to date | never reaches `BeginFile` | not counted |
| TOC in partition | reserve 0; the budget instead (§9) | 0 |
| Fixed override | `reserveForBlocks` is a constant: a fixed reserve | the override |

Adjusting the reserve cannot fail as an operation — it is a number on the writing thread. What can happen is that the
grown reserve leaves no room for the write; that is the trimmed or empty write above, handled by the ordinary end-of-volume
path. The reserve is never lowered to make room (TR-2): a volume ends instead.

### 4.7 Tests

| Suite | Proves |
|---|---|
| `TocCommitPlanTests` | `TocBytesFor` sums exactly the entries due by `b` blocks; empty and unsorted input |
| `TocReservePlannedWriteTests` | a fitting write grows the reserve without early warning; an overlong one is trimmed to the largest fitting prefix and reports it; nothing fits → nothing written; never lowered; no reserve armed → the function is ignored; writing to the end leaves room for every written entry |
| `TocReserveAgentTests` | **safe and live** on the three in-set layouts: 800 long-named files on equal 1 MiB volumes — both TOC copies on every volume, every volume takes files, the series restores; an **encrypted** series; **no loop**; a full set raises past the floor, an unchanged incremental keeps exactly its armed reserve; Partitions arms none; **mixed volume sizes** (2 MiB / 1 MiB, both orders, all four layouts) |
| `ServiceTocReserveTests` | the reserve reported equals the floor rule; `WritableRemaining = estimated − reserve`; the property row |
| `MultiVolumeBackupRestoreTests` | `TwoSets_DifferentVolumes_…`, `SecondSet_OnAVolumeFullToTheReserve_…`, the parity guard `Fixture_EveryVolumeHoldsBothTocCopies` — the boundary cases of §4.6 on every layout and header mode |

The multi-volume fixture gives every volume its own capacity (`VolumeCapacity` / `CapacityOfVolume`; default all equal)
and TOC partition (`initiatorCapacity`, default 256 KiB), and runs either the live reserve (`UseLiveTocReserve`) or the
fixed 5d parity reserve (`TOCCapacityOverride` / layout default). After every backup it checks the saved TOC's structure
(`TocInvariants.AssertWellFormed`; opt out with `ValidatesTocAfterBackup = false`).

## 5. The service reports the reserve it will use

`TapeServiceBase` (`Services/TapeServiceBase.cs`):

- `TocSizeEstimate` — the loaded TOC's estimate, cached per TOC state (instance, last save time, set count).
- `TocReserve` — `TapeNavigator.PredictTocReserve(drive, TocSizeEstimate)`: the reserve the **next** backup starts with;
  0 without media or with a TOC partition.
- `Used` and `ComputeWritableRemaining` deduct `TocReserve`; `WritableRemaining` follows.
- One property row, "TOC Reserve". WPF and CLI render the property lists row by row, so they need no change.

How a backup reports the end of each volume, and advises on volume space, is §9.6.

## 6. Files

| Area | Files |
|---|---|
| Estimate | `Toc/TapeTOC.Format.cs`, `Toc/TocEntrySizing.cs` (`TocEntrySizing`, `TocEntryMeter`) |
| Navigator | `Media/TapeNavigator.cs` (`TocLimits`) |
| Drive | `Drive/TapeDrive.cs` |
| Packer pipeline | `Packer/TocCommitPlan.cs`, `Packer/TapeFileWritePacker.cs`, `Packer/ITapeWriteBackend.cs`, `Packer/WorkerThreadTapeWriteBackend.cs`, `Packer/MemoryTapeWriteBackend.cs` |
| Stream manager | `Media/TapeStreamManager.cs` (`ContentStopReason`) |
| Agent | `Agents/TapeFileBackupAgent.cs` |
| Service | `Services/TapeServiceBase.cs`, `TapeServiceBase.Backup.cs`, `TapeServiceBase.Outcome.cs`, `ServiceOperationResult.cs`; property sheets |
| Tests | `TocSizeEstimateTests`, `TocTightEntryTests`, `TocReserveForTests`, `LogicalEwReserveRaiseTests`, `TocCommitPlanTests`, `TocReservePlannedWriteTests`, `TocReserveAgentTests`, `ServiceTocReserveTests`, `TocBudgetTests`, `ServiceTocBudgetTests`; fixtures `MultiVolumeVirtualTapeFixture`, `TocInvariants` |

## 7. Deliberately not done

- **Pre-backup forecast.** Estimating the TOC after the backup before starting could warn "this volume will not hold the
  TOC". Advisory at best — what an incremental writes is unknown in advance — and the live tracking ends volumes safely
  anyway.
- **Per-set hash and compression in the bound.** Passing the set's hash size and compression to `GroupBound` (both known
  to the writer too, so batching stays in step) would cut 8–18 B per entry (§9.4). Capacity only.
- **Shrinking mid-set** (TR-2), **per-copy layout tuning** (both copies are identical by design).

## 8. History

| Step | Change |
|---|---|
| Group A | Estimate (§1) and navigator (§2). Floor capped at 1/32 of the medium — first ¼, which on tiny test volumes cost a quarter of the capacity for a TOC of a few KiB. Mark allowance one block. |
| Group B | `RaiseEarlyWarning` (§3). A raise invalidates the cached headroom rather than shifting it by the raise: the cache can be up to one poll interval stale. |
| Group C | **Revision 1**: the agent raised the reserve at hand-off to the packer, with a slack. Failed on small files — the reserve outgrew a small volume before a single file committed (§4.1). |
| Group D | **Revision 2**: the reserve is planned in the write (§4). `RaiseEarlyWarning` stays in the drive, unused by the backup. |
| Follow-ups | The final-flush, set-header and fresh-continuation cases of §4.6; the multi-volume fixture without padded continuation volumes, with per-volume capacities; a reload clears the drive's cached content capacity (`TapeDrive.ReloadMedia` → `InvalidateContentCache`). |
| §9 Step 1 | Entries sized by arithmetic (`TocEntrySizing`, `TocEntryMeter`); the writer's batching by the same rule. The designed exact measurement (a counting stream over the real writer) was dropped: arithmetic is as tight, allocation-free, and has no side effects on sealed sets. |
| §9 Step 2 | The TOC partition budget: `TocLimits`, the initiator-capacity cache, the sink's trim, `ContentStopReason`. |
| §9 Step 3 | Facts in the agent, advice in the service: `BackupResult.VolumeEnds`, the volume-space advisor, `ReportAdvice`. A series stopped by TR-7 no longer reports "completed successfully". |

## 9. The TOC reserve on partitioned media — the TOC partition budget

### 9.0 Problem

With an initiator partition the TOC has its own space, so no early-warning reserve is armed (§2). That space is fixed at
format: `DefaultTOCCapacity` on our own formats (≥ 32 MiB), freely chosen on pre-LTO drives (AIT, DAT), at least one wrap
on LTO-5+ (≈ 37 GB on LTO-5). Before, nothing watched it: a backup of very many small files wrote all its content, then
failed at the TOC save — every file on tape, none reachable without an emergency export.

**Now** a volume takes every file whose TOC entry still fits the TOC partition twice; when the next files' entries would
not, the volume ends exactly as at end-of-media, the backup continues on the next volume, and the user is told why.

**Not a pre-check.** What an incremental will write is unknown before it runs; the live tracking of §4 counts exactly the
files that reach the tape.

### 9.1 Invariants

| # | Invariant |
|---|---|
| TR-1, TR-7, TR-8 | as in §0, against the TOC partition |
| TB-1 | **Budget, not reserve.** The partition limit does not compete with content; content still runs to end-of-media. The check is arithmetic over TOC sizes and the partition's capacity — no drive query, no early-warning state. |
| TB-2 | **The drive's write path is not touched.** Its planner (§4.3) stays the TOC-in-set mechanism; the budget lives in the write sink. |
| TB-3 | **Tight by arithmetic.** Every TOC entry is sized by one rule (§9.4) — in the estimate at set start, and per file at hand-off. Nothing is serialized to measure. |
| TB-4 | **No per-file allocation.** Sizing an entry allocates nothing. |
| TB-5 | **Facts travel with the end of the volume; advice is the service's.** Not a TOC anomaly, not a new notifiable API. |

### 9.2 Where the decision lives

| Component | Role |
|---|---|
| **Navigator** | `CaptureTocLimits()` returns `TocLimits(ReserveFor, Budget)`. `TapeNavigatorTOCInPartition`: reserve 0, `Budget = InitiatorCapacity − TocMarkAllowance`; unknown capacity → no budget, logged. `TocLimits.Fits(toc)`: both copies (`CopiesFootprint`) within the budget. |
| **Drive** | caches the initiator capacity, `TapeDrive.InitiatorCapacity` (§9.2.1). |
| **Manager (sink)** | `PackerWriteSink`: before `WriteDirect`, the largest block prefix whose TOC still `Fits`; a short prefix is written and reported as end-of-media with `LastContentStopReason = TocPartitionFull`. Same plan and `committed` bookkeeping as §4.2. |
| **Agent** | `EnsureTocPartitionCapacityKnown` first (one visit to the partition if needed), then `ArmTocReserve`; `VolumeEndReason`, `FreshVolumeTookNoFile`. |
| **Service** | `BackupResult.VolumeEnds`; the volume-full line; the volume-space advisor (§9.6). |

The navigator supplies the numbers (its layout), the sink applies them (it holds the plan); one branch in the sink is the
only new control flow.

#### 9.2.1 The initiator-capacity cache

During a set the head is on the content partition, and `Capacity` reports the current partition — so the TOC partition's
capacity is cached where it is seen (`TapeDrive.TrackPartition` keeps `m_onContentPartition` / `m_onInitiatorPartition`):

| Opportunity | Action |
|---|---|
| `MoveToPartition(Initiator)` | fill |
| `FastforwardToEnd(Initiator)` | switch partitions: track, refresh, fill |
| `GetCurrentPartition()` answering Initiator | track; fill if empty |
| `EnsureMediaParams()` while on the initiator partition | fill — the hook every TOC read and write passes |
| `ReloadMedia`, `UnloadMedia`, `CloseDrive`, `FormatMedia` | clear (`InvalidateContentCache`) |
| still empty at set start | the agent visits the partition once |

Not filled eagerly at load: a partition switch costs seconds on LTO, and the TOC read that follows fills it for free.

### 9.3 One file through the pipeline (differences to §4.2)

| # | Step | Partitioned media |
|---|---|---|
| 0 | set start | capacity known; tight estimate of the series TOC; `TrackTocReserve` snapshots `TocLimits` |
| 4 | write | the sink trims to the budget; the drive writes as before (no reserve armed) |
| — | budget hit | end-of-media → packer rollback → `HandleEom` → TOC save → next volume; reason `TocPartitionFull` |

The boundary cases of §4.6 apply. The set header is not affected (it goes to the content partition). A fresh continuation
that takes no file ends the backup — on partitioned media the **expected** outcome after a budget end on an equal
cartridge: every volume repeats the whole series' TOC.

### 9.4 Sizing entries by arithmetic

**The rule — `TocEntrySizing.GroupBound(file, previous)`** (`Toc/TocEntrySizing.cs`). Every 2.1 field is
`tag ‖ length ‖ value`, the tag one byte for field numbers ≤ 63; an entry's size is arithmetic over its values — exact for
what is known at hand-off (name and its shared prefix against `previous`, file id, length, attributes, times), maximal for
what is not (address, size on tape, a 16-byte hash, the codec). Never below the entry; at most `MaxSlack` = 50 B above it.

The slack is a function of the unknown fields' actual values, not a statistic: block addresses of 3 bytes, offsets of 2,
sizes of 2, an 8-byte hash and the `Stored` codec give 29 B for every entry. In production it ranges ~15–45 B by the set's
hash algorithm (fixed per set), compression and address width.

**One rule, three users.**
- **The writer** (`TapeSetTOC.WriteBatches`) closes a batch at 4096 entries or when the Σ `GroupBound` reaches 1 MiB — not
  on bytes written — so the meter places every batch break, and with it every front-coding reset, where the writer does.
  Readers accept any batching; the format is unchanged.
- **The estimate** (`TapeSetTOC.EstimateSerializedSize`) sums the rule over the set, batch prologues included.
- **The meter** (`TocEntryMeter`, one per agent): `Reset(set)` at set start, `Measure(file)` before the hand-off,
  `Accept()` after a successful one. A failed or retried file is never accepted; files rolled back at end-of-media are the
  set's last ones. Encrypted sets add `EstimateEntryGrowth`.

**Not re-serialized mid-set:** the TOC grows on the agent thread while the sink runs on the writing thread; a re-measure
would need a lock on the hot path, and the arithmetic is as accurate.

### 9.5 Allocation discipline (TB-4)

Nothing is written, so nothing is reused: `GroupBound` is spans and arithmetic; the meter keeps three scalars and a
reference to the previous name. Pinned by a test: ≤ 256 B added by 10 000 more files (a slope, so one-off runtime allocations cancel).

### 9.6 Telling the user (TB-5)

**The reason travels with the end of the volume:**

| Layer | Carries |
|---|---|
| Manager | `LastContentStopReason` (`None`, `EndOfMedia`, `TocReserve`, `TocPartitionFull`), set on the writing thread; cleared by `BeginWriteContent` |
| Agent | `VolumeEndReason` (from the manager in `HandleEom`; `TocReserve` for a set header that did not fit, F32 → `EndOfMedia`); `FreshVolumeTookNoFile` |
| Service | `BackupResult.VolumeEnds` — one `VolumeEnd(Volume, Reason)` per volume ended before the file list |

**Facts in the agent, advice in the service.** The agent's diagnosis states what happened, with its figures; what to do
is the service's. Three advisors share one shape — headline, details, action (`TapeServiceBase.ReportAdvice`):

| Advisor | Input | Speaks when |
|---|---|---|
| set anomalies (`AdviseOnSetAnomalies`) | `TapeSetStatistics` | drift, refusal |
| TOC anomalies (`AdviseOnTocAnomalies`) | the anomaly trail | lost, unreadable, end of content disputed, one copy, found by searching |
| **volume space** (`AdviseOnVolumeEnds`) | `VolumeEnds`, series stopped | a TOC partition ended a volume (Warning: format future cartridges with a larger partition); a fresh volume took no file — partition too small for the series, or volume too small for the TOC (Failed: a larger partition / cartridge, or a new series) |

Generic file failures get no advice: the verdict line and the per-file failures say all there is.

**The live line** at a budget end: "Volume #n ends early: its TOC partition (X) is full", and — when the series continues —
"Continue on a cartridge with a larger TOC partition — one of the same size cannot take more files of this series".

**A stopped series ends as failed**, naming the files left out ("Backup ended after volume #n — k file(s) not backed up"),
with the agent's diagnosis and the volume-space advice.

**Advice `TocPartitionFull`** (TOC anomalies) keeps its meaning: a TOC copy that still failed to write.

### 9.7 Tests

| Suite | Proves |
|---|---|
| `TocTightEntryTests` | `GroupBound` ≥ the entry, within `MaxSlack`, every name shape and extreme values; the estimate covers the saved TOC with batches closed by count and by bytes; byte-closed batches round-trip; the meter's accept rule; `Reset` on a full set; ≤ 256 B added by 10 000 more files (a slope, so one-off runtime allocations cancel) |
| `TocBudgetTests` | the drive's cache filled where seen, cleared on reload; a 96 KiB TOC partition ends a volume by the budget — both copies stand, the reason is given, the files restore; an equal next volume stops the series with the partition wording, no third volume; within budget no stop; TOC in set has no budget |
| `ServiceTocBudgetTests` | the volume-space policy and wording; a larger next partition completes with warning and advice, everything restores; an equal one ends as failed — files left out named, advice given, no prompt |

### 9.8 Steps

| Step | Content |
|---|---|
| 1 | `TocEntrySizing` + `TocEntryMeter`; writer batching by the bound; tight estimate; the agent passes meter entries |
| 2 | `TocLimits`, initiator-capacity cache, sink budget, `LastContentStopReason`, `VolumeEndReason` |
| 3 | `FreshVolumeTookNoFile`; `VolumeEnds`, volume-space advisor, `ReportAdvice`; a stopped series ends as failed |
