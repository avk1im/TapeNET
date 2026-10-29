# Design-TocReserve — a TOC reserve that follows the TOC

*Little sister of Design-TocLocation. Status: design, pre-implementation.*

## 0. Problem and goal

On TOC-in-set layouts the backup arms a logical early-warning (EW) reserve before each set, so content stops while
room for both TOC copies remains. Today the reserve is a fixed guess, `TapeNavigator.DefaultTOCCapacity` =
`capacity / 400` clamped to 32 MB … 1 GB.

- **Too large, almost always:** 1 GB per LTO volume, 400 MB on DAT-320, for a TOC that is usually a few MB.
- **Too small, rarely — and then fatal:** the TOC grows ~150 B per file. Several million entries across a series exceed
  1 GB; copy 2 is lost, possibly copy 1. The F4 check only warns *after* the save.

**Goal:** the reserve equals what the TOC needs *right now*, grows as files are added, and never falls behind it.
Primarily a **safety** feature; the capacity gain is a bonus.

**Out of scope:** TOC-in-partition (no reserve). A pre-backup "will it fit?" forecast (§6).

### Invariants

| # | Invariant |
|---|---|
| TR-1 | The reserve armed for a set is ≥ the footprint of the TOC as it would be written at any write of that set. |
| TR-2 | **Within a set the reserve never shrinks.** It only grows; a set starts at a fresh value. |
| TR-3 | The drive's logical-EW state (anchors, tail byte count, poll pacing, sticky latch) survives a reserve change. Only `SetEarlyWarning` (set start) re-selects the mechanism. |
| TR-4 | A reserve change takes effect on the **writing thread**, at the start of the next `WriteDirect`. |
| TR-5 | Estimates are upper bounds: an estimate never undercounts the serialized size. |
| TR-6 | Floor: 16 MiB on LTO drives (one SCSI direct-IO content buffer), 4 MiB otherwise. An explicit override wins (tests). |

## 1. Step 1 — Estimate the TOC's size

`TapeFileInfo.EstimateSerializedSize()` exists (120 B fixed + name + hash; no front-coding credit). Extend upward:

```csharp
// TapeSetTOC
/// <summary>Upper bound of this set's 2.1 TOC footprint: set record + batch prologues + every entry (TR-5).</summary>
internal long EstimateSerializedSize();

// TapeTOC
/// <summary>Upper bound of one TOC copy: header + every set + end record + CRC envelope (TR-5).</summary>
public long EstimateSerializedSize();
```

| Part | Estimate |
|---|---|
| TOC header | fixed 128 B + clamped description + clamped `WrittenBy` |
| Plain set | fixed 128 B + description + Σ entries + one batch prologue (16 B) per `MaxFilesPerBatch` / `MaxBatchBodyBytes` |
| **Locked** sealed set | exact: its stored chunks + stub (they are re-emitted verbatim, E7) |
| **Unlocked** sealed set | plain estimate + AEAD overhead per chunk, rounded up to `SealPadUnit` |
| End + envelope | fixed 64 B |

Cheap: one pass over in-memory entries, no I/O. For a running set the agent keeps a **running total** instead of
re-summing (§4).

**Tests (`TocSizeEstimateTests`):**
- Upper bound holds — `EstimateSerializedSize() ≥ SaveTo(...).Length` for: empty TOC; one/many sets; long, Unicode
  and ill-formed (UTF-16 fallback) names; hashes of every algorithm; > `MaxFilesPerBatch` files; locked and unlocked
  encrypted sets; a legacy-loaded TOC.
- Not absurd — estimate ≤ 2 × actual for a realistic corpus (deep shared paths, 10 k files).
- Incremental — `set.EstimateSerializedSize()` grows by exactly `entry.EstimateSerializedSize()` per `Append`.

## 2. Step 2 — The navigator says how much to reserve

Replace `TOCCapacity` as the reserve source with one virtual:

```csharp
// TapeNavigator
/// <summary>
/// The EW reserve this layout needs for a TOC of <paramref name="tocBytes"/> (one copy): 0 where the TOC has its own
///  partition; otherwise both copies, the layout's extra structure and the closing marks — floored (TR-6), or the
///  override when set.
/// </summary>
public virtual long TocReserveFor(long tocBytes);

/// <summary>Fixed reserve replacing the computed one (tests, diagnostics). Null = computed.</summary>
public long? TocReserveOverride { get; set; }
```

| Navigator | `TocReserveFor(b)` |
|---|---|
| `TOCInPartition` | 0 |
| `TOCInSet` (all three) | `max(floor, TOCFootprint(b) + MarkAllowance)`; `TOCFootprint` already adds the gap file on the TOC-mark layout |

- `MarkAllowance` = 16 KiB (closing marks may cost a block on some media — cf. 5d).
- `floor` = 16 MiB if `Drive.LtoGeneration >= 1`, else 4 MiB; never more than ¼ of `Drive.Capacity` (tiny virtual media).
- `TOCCapacity` / `DefaultTOCCapacity` remain for one release as `[Obsolete]` wrappers; `SetOptimalDriveParams`'
  `eotZone` keeps using `DefaultTOCCapacity` (driver hint, not our reserve).
- The multi-volume fixture's 5d reserve maps onto `TocReserveOverride`.

**Tests (`TocReserveForTests`):** per layout — partition 0; in-set ≥ footprint + marks; floors 16 MiB (LTO backend
stub) / 4 MiB (virtual); ¼-capacity cap; override wins.

## 3. Step 3 — The drive accepts a growing reserve mid-set  *(the load-bearing step)*

> **Implementation update Step 3:** The design shifted the cached headroom by the raise's delta. That is not safe: the cache is up to one poll interval stale (the clamp's skip test relies on "actual ≥ ¾ of cached"), and subtracting a large delta from a stale value can still leave the clamp skipped for a write that crosses the new reserve. Instead a raise invalidates the cache (long.MaxValue = "not polled"): the clamp then takes one fresh estimate on the very next write, whatever the delta. Cost: one ReportedRemaining query per raise. Update the design's §3 snippet accordingly.

New member next to `SetEarlyWarning`; **nothing else in the EW machinery changes**:

```csharp
/// <summary>
/// Raises the logical-EW reserve of the running set to <paramref name="bytesBeforeEom"/> (TR-2: never lowers it).
///  Safe from any thread; applied at the start of the next WriteDirect, on the writing thread (TR-4). Keeps every
///  runtime state of the EW mapping (TR-3) — unlike SetEarlyWarning, which starts a set.
/// </summary>
public void RaiseEarlyWarning(long bytesBeforeEom);
```

Mechanics:

```csharp
private long m_pendingEarlyWarning = -1L;           // -1 = none; written by any thread, read by the writer

public void RaiseEarlyWarning(long bytes)           // any thread
{
    long cur;
    do { cur = Volatile.Read(ref m_pendingEarlyWarning); }
    while (bytes > cur && Interlocked.CompareExchange(ref m_pendingEarlyWarning, bytes, cur) != cur);
}

private void ApplyPendingEarlyWarning()             // first statement of WriteDirect, writing thread
{
    long pending = Interlocked.Exchange(ref m_pendingEarlyWarning, -1L);
    if (pending <= m_desiredEarlyWarning || m_desiredEarlyWarning <= 0L)
        return;                                     // TR-2; and never arms a reserve SetEarlyWarning didn't
    long delta = pending - m_desiredEarlyWarning;
    m_desiredEarlyWarning = pending;
    if (m_writableHeadroomAtLastPoll != long.MaxValue)
        m_writableHeadroomAtLastPoll -= delta;      // the clamp sees the smaller headroom immediately
    m_bytesSinceRemainingPoll = long.MaxValue / 2;  // force a fresh estimate on this write (pre-EW regime)
    m_logger.LogTrace("{Prefix}: Early-warning reserve raised to {Reserve} B", LogPrefix, pending);
}
```

Why this is safe, regime by regime (Design-RemainingAndEw Part 2, 7.2.1):

| Regime | What reads the reserve | Effect of a raise |
|---|---|---|
| No reserve (calibration run) | — | ignored (`desired ≤ 0`) |
| Before physical EW | `est ≤ desired` after the forced poll; the clamp via shifted headroom | fires earlier, never later |
| After physical EW | byte-count `EwToEomDistance − bytesSinceEw ≤ desired` | same; anchor untouched |
| Latched | sticky `IsEarlyWarning` | stays latched (a raise can't clear it) |
| Overwrite notification | `EstimatedContentRemainingForEw` | unaffected |

- `SetEarlyWarning` and `ResetEarlyWarningRuntime` also clear `m_pendingEarlyWarning`, so a raise never leaks into the
  next set, the next medium, or a calibration run.
- `EarlyWarning` (getter) reads `Volatile.Read(ref m_desiredEarlyWarning)` — readers on other threads see a whole value.
- The forced poll costs one `ReportedRemaining` query per raise; raises are rare (§4: per ≥ 64 KiB of TOC growth).

**Tests (`LogicalEwReserveRaiseTests`, over the existing `CalibrationAndLogicalEwTests` harness):**
1. Raise before physical EW → logical EW fires at ≥ the larger reserve (true remaining at fire ≥ new reserve).
2. Raise after physical EW (measured calibration, small reserve) → fires at the larger reserve; `IsPhysicalEarlyWarningSeen` stays true.
3. A single large write right after a raise is **clamped** to the new reserve (headroom shift).
4. Lowering is ignored mid-set (`RaiseEarlyWarning(smaller)` → reserve unchanged).
5. Raise with no reserve armed is ignored; a calibration run is unaffected.
6. Raise from another thread while writes run (≥ 1 000 raises, `Parallel.For`) → final reserve = max requested, no lost update, no exception.
7. Pending raise is dropped by `SetEarlyWarning` and by `ReloadMedia`.
8. **Regression gate:** every existing `CalibrationAndLogicalEwTests` / `VirtualDriveEarlyWarningTests` passes unchanged.

## 4. Step 4 — The agent tracks the TOC while the set grows

In `TapeFileBackupAgent`:

- **Set start** (`BeginWriteContentForCurrentSet`, replacing `SetEarlyWarning(HasInitiatorPartition ? 0 : TOCCapacity)`):
  ```csharp
  m_tocBytes = TOC.EstimateSerializedSize();                      // whole series TOC, current set included
  m_tocBytesArmed = m_tocBytes;
  Drive.SetEarlyWarning(Navigator.TocReserveFor(m_tocBytes));
  ```
- **Per file — counted at HAND-OFF to the packer / stream, not at commit:**
  ```csharp
  m_tocBytes += fileInfo.EstimateSerializedSize();
  if (m_tocBytes - m_tocBytesArmed >= TocReserveSlack)            // 64 KiB
  {
      m_tocBytesArmed = m_tocBytes;
      Drive.RaiseEarlyWarning(Navigator.TocReserveFor(m_tocBytes));
  }
  ```
  - *Why hand-off, not commit:* the packer keeps up to two 16 MiB buffers in flight; with 1 KiB files that is ~32 k
    entries ≈ 6 MB of TOC the commit count would lag. Counting at hand-off never lags; files rolled back at EOM stay
    counted — an overcount, i.e. a safe (TR-1) error.
  - *Retries* re-hand-off the same file: count once per `FileId`, not per attempt (a retry burns a FileId, but the
    TOC holds only the committed one).
- **Incremental:** files skipped as up to date never reach hand-off — never counted.
- **Continuation volume:** `ResumeBackupToNextVolume` → `BeginWriteContentForCurrentSet` re-arms from the series TOC
  as it stands; nothing carries over.
- **Packed and aligned paths alike:** both pass through the same hand-off point in `BackupFile`.
- F4's after-save warning stays as a backstop, now comparing against `TocReserveFor(LastTOCCopyBytes)`.

**Tests (`TocReserveAgentTests`, single-volume fixture with an EW profile; multi-volume fixture):**
1. **The safety case:** a set of many small files with long names whose TOC exceeds the old fixed reserve → both
   copies written, no `CopyWriteFailed`, TOC reloads (fails before this phase).
2. **Content is not starved:** same capacity, few files → content stops within `floor + slack` of true EOM.
3. **Incremental:** second, unchanged-files incremental → reserve stays at the set-start value (no raise logged).
4. **Retry / rollback:** injected file failure with Retry → TOC estimate counts the file once; EOM rollback → reserve
   not lowered.
5. **Multi-volume:** each volume re-arms from the series TOC; volume 2's reserve ≥ volume 1's TOC estimate.
6. **Encrypted set:** sealed TOC with padding → both copies fit.
7. All four layouts; Partitions never calls `SetEarlyWarning` with a non-zero reserve.

## 5. Step 5 — Service reports the reserve it will use

- `WritableRemaining` / `ComputeWritableRemaining(estimated)`: deduct `Navigator.TocReserveFor(TOC.EstimateSerializedSize())`
  — the reserve the *next* backup would start with — instead of `TOCCapacity`.
- Property pane / CLI `--verbose`: one new row, *TOC reserve*, e.g. `16 MiB (TOC 1.2 MiB)`.
- No new API for apps; `WritableRemaining` simply becomes truthful.

**Tests:** `WritableRemaining` grows by `old − new` reserve on an LTO-like virtual profile; equals estimated remaining
on Partitions; the media-usage bar's what-if equals the live figure.

## 6. Deliberately not done

- **Aggregator-based forecast.** `FileSizeAggregator` stays statistics-only. A pre-backup "the TOC will need ~X; this
  volume holds it / it won't fit" warning would add `Σ path lengths` to it (known at enumeration, no file access) —
  overcounts incremental, advisory only. Candidate for a later phase.
- **Shrinking mid-set.** Never needed: the TOC only grows within a set (TR-2).
- **Per-copy layout tuning** (e.g. a smaller copy 2). Both copies are identical by design.

## 7. Files

| File | Change |
|---|---|
| `Toc/TapeTOC.Format.cs`, `Toc/TapeTOC.Encryption.cs` | `EstimateSerializedSize` on set and TOC (§1) |
| `Media/TapeNavigator.cs` | `TocReserveFor`, `TocReserveOverride`; `TOCCapacity` obsolete (§2) |
| `Drive/TapeDrive.cs` | `RaiseEarlyWarning`, `ApplyPendingEarlyWarning`, clears in `SetEarlyWarning` / `ResetEarlyWarningRuntime` (§3) |
| `Agents/TapeFileBackupAgent.cs` | arm at set start, raise at hand-off (§4) |
| `Agents/TapeAgentBase.cs` | F4 check against `TocReserveFor` |
| `Services/TapeServiceBase*.cs` | `WritableRemaining`, `ComputeWritableRemaining`, reserve row (§5) |
| Tests | `TocSizeEstimateTests`, `TocReserveForTests`, `LogicalEwReserveRaiseTests`, `TocReserveAgentTests`; multi-volume fixture → `TocReserveOverride` |

**Sequence:** 1 → 2 → 3 (land with its regression gate green before 4) → 4 → 5.

## 8. Changes during implementation

**The slack** is 16 KiB (not 64 KiB) and **the mark allowance** one block (Group A) — on a
256 KiB test volume 64 KiB of slack plus 96 KiB of marks would have reserved most of it. The slack only has to cover the
entries handed off since the last raise: the raise collapses into the next write, and that write can only commit files
handed off before it.

**No WPF or CLI change.** Both render `DescribeDrive` / `DescribeMedia` row by row (`ShowProperties`, `LogProperties`), so the
new row appears in the property pane, `tapecon info` and `tapecon list` by itself; the status bar and the usage bar pick
up the new `WritableRemaining` / `ComputeWritableRemaining` likewise.

**Updated raise handling.** The design shifted the cached headroom by the raise's delta. That is not safe: the cache is up to one poll interval stale (the clamp's skip test relies on "actual ≥ ¾ of cached"), and subtracting a large delta from a stale value can still leave the clamp skipped for a write that crosses the new reserve. Instead a raise invalidates the cache (long.MaxValue = "not polled"): the clamp then takes one fresh estimate on the very next write, whatever the delta. Cost: one ReportedRemaining query per raise. Update the design's §3 snippet accordingly.

**Updated the TOC capacity reserve floor `TapeNavigator.TocReserveCore`** to 1/32nd of the cpacity vs. previously a quarter. This is only relevant for the small test virtual drives.
A quarter of the medium proved far too big as a cap. On tiny test volumes it costs a quarter of the capacity for a TOC of a few KiB, which goes against the whole point of the live reserve.
On a 3 MiB volume 1/32nd gives 96 KiB. That's still above the computed size (two one-block copies plus one block for marks is 48 KiB) and the slack. Real media aren't affected: from 128 MiB up, a 32nd is already more than the 4 MiB floor. The cap only matters on test-sized media.
