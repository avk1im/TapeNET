# Design — Media Reconciliation ("Repair Media") for TapeNET

**Status:** v4 · **specified to the implementable state, with the test plan**
**Scope:** examine a cartridge whose content no longer matches its table of contents, propose a
reconciliation, and — on approval — carry it out. Agent and service layers are specified in full; the WPF
surface is the concluding phase (§14).
**Depends on:** verified destructive navigation (`docs/Design-SetHeader-VerifiedWrite.md`, v4) — SH-13…SH-20,
the verdict ladder, `TapeSetAgent.DeleteSetsFromCurrentSetUp`, the set-level anomaly channel; the set header
(`docs/Design-SetHeader.md`, v3) — record, presence model, `ClassifySetHeader`; remaining capacity
(`docs/Design-RemainingAndEw.md`) — `EstimatedContentRemaining`, the TOC reserve.

*The code is the authority.* Members marked **NEW** do not exist yet and are specified here; everything else
exists today and the description matches it.

---

## 1. What the feature does

The verified-write work gave the library the ability to **refuse** a destructive write on a cartridge whose
tail is damaged, and to **recover its own navigation** across that damage. What it did not give the user is a
way to make the cartridge sound again.

Today that repair is manual: open Delete Backup Sets, guess which sets are still readable, and delete from
there. The guess is the problem. Delete one set too few and the damage remains; one too many and intact
backups are destroyed — by the very verb the user reached for to fix things.

This feature makes the library do the guessing, with evidence:

- **It examines the cartridge** set by set, comparing what is physically on tape against what the TOC
  describes — in **both** directions of disagreement.
- **It proposes a plan** and modifies nothing until the user approves.
- **It reconciles the TOC, not only the tape.** Sets the TOC describes but the tape no longer holds are
  removed from the TOC with **no tape write at all**. Sets the tape holds but the TOC does not describe are
  **kept**, with a placeholder entry minted from the set's own header (§5).
- **It works from whichever TOC the user has** — the one in memory after a botched operation, or one
  imported from a `.tapetoc` file.

### 1.1 Naming

The user-facing verb is **Repair Media** (Media | Repair Media…), subtitled *"reconcile the table of contents
with the tape"*. The internal vocabulary is **reconciliation**: `TapeSetScanEntry`, `MediaReconciliationPlan`,
`AnalyzeMediaAsync`, `RepairMediaAsync`.

*"Set recovery"* was rejected: it undersells a feature whose fix is often entirely in the TOC, and it collides
with the navigation **recovery** of SH-14, which is a different thing at a different layer.

---

## 2. Design principles

| | |
|---|---|
| **Propose, then act** | Analysis and application are separate calls. The plan is a value the user approves; nothing is written until they do. |
| **Two independent axes** | *Structural* — does the set's closing mark exist? *Descriptive* — does its header match what the TOC says? They answer different questions and must not be collapsed (§4.1). |
| **Reachability is evidence, integrity is not** | The scan proves a set can be *located*. It says nothing about whether its files are readable — and the result says so. |
| **The scan observes; it does not repair** | The verdict ladder is BYPASSED during analysis: `ClassifySetHeader` is called directly, no anomaly is raised, no correction attempted. A recovery that quietly repositions would corrupt the measurement being taken. |
| **Truncate as late as possible** | The cut lands at the first set that is structurally broken — never merely unverified. Destroying sound tape to tidy up a bad header would be the opposite of a repair. |
| **Verify the survivor, not the casualty** | The destructive write is authorized by a set that must SURVIVE it (§7.2). Demanding a clean header from the set being deleted would refuse every genuine repair. |
| **TOC set indices mirror physical set positions** | This is what forces placeholders for undescribed sets (§5.1), and it is the invariant the whole navigator rests on. |
| **Policy lives in the service** | The agent reports raw findings; the service turns them into a plan — the split the calibrator/service pair already uses for recalibration verdicts. |
| **Leave a way back** | The pre-repair TOC is exported to a file before anything is applied, and a failed export aborts the repair. |

---

## 3. Bounding the walk — the step that makes the rest honest

The scan walks forward from begin-of-content hopping setmarks. On **every** layout the content area is
followed by something that is not a set, and a naive walk marches straight into it:

| Layout | What follows the last set |
|---|---|
| `TapeNavigatorTOCInSetWithSmks` | `[SM][toc1][FM][toc2][FM]` |
| `TapeNavigatorTOCInSetWithFmks` | `[FM][toc1][FM][toc2][FM]` — the *same mark type* the walk counts |
| `…WithFmksAndTOCMark` | `[FM][gap][FM][FM][FM][toc1]…` |
| `TapeNavigatorTOCInPartition` | EOD in the content partition |

A walk that hops one mark too many reads a TOC block, fails to classify it, and reports a phantom broken set —
on a perfectly healthy cartridge. So the scan establishes its own end marker first:

```
Navigator.MoveToEndOfContent()          // one seek; the head lands exactly where content stops
endOfContentBlock = Drive.CurrentBlock
Navigator.MoveToBeginOfContent()
```

The walk then stops as soon as the pre-read position reaches `endOfContentBlock`. One extra seek buys a hard,
layout-independent boundary and removes an entire class of false findings.

**When the bound is unavailable.** `MoveToEndOfContent` fails on exactly one cartridge: the one whose TOC was
destroyed by a backup that died mid-set — which is also the cartridge where there is no TOC area to walk into.
The fallback is therefore safe: `endOfContentBlock = long.MaxValue`, and the walk ends at EOD
(`ERROR_NO_DATA_DETECTED`). The two cases are complementary by construction, not by luck.

---

## 4. The scan — what the states mean

### 4.1 The two tests, and the grid they produce

For each set, at its start block:

- **Structural** — `Navigator.MoveToNextContentSetmark(1)` succeeds without running into EOD, i.e. the set's
  **closing mark exists**.
- **Descriptive** — read the set's first block and run `ClassifySetHeader` (pure, already tested, reused
  verbatim).

**The structural test is the decisive one.** `WriteSetHeader` runs at the set *start*, before the first file,
so a backup killed mid-set leaves a **perfectly healthy set header** behind. Judged on its header alone, a
half-written set looks rescuable. Its missing closing setmark is what proves it never completed.

| | closing mark **present** | closing mark **missing** (EOD) |
|---|---|---|
| header `Match` | **Complete** — keep | **Partial** — the user's one decision (§7.4) |
| header `Unreadable` / `SetIndexDrift` | **Unverified** — keep, warn | **Damaged** — remove |
| header `WrongMedia` / `WrongVolume` | **Foreign** — remove, stop the walk | **Foreign** |
| *(past the TOC's last set)* header `Match` on identity | **Undescribed** — keep + placeholder (§5) | **Damaged** — remove |
| *(past the TOC's last set)* anything else | — *end of the walk; not a set* | — |

Plus one state that is not a row because it involves no tape at all:

- **Absent** — the set's start lies at or beyond `endOfContentBlock`: the TOC describes sets the tape does not
  hold. Removed from the TOC; **no tape write**.

### 4.2 Why `Unverified` is kept rather than removed

A set whose header will not classify, but whose closing setmark is intact, is **structurally sound**. Its
files restore by absolute `TapeAddress`, which the header has no bearing on; the read path's golden rule —
*a record that cannot be verified never blocks* — applies unchanged. Truncating it would destroy sound tape,
and every set behind it, to tidy up 16 KiB of corrupt metadata.

This is also what answers the v1 draft's open question about stranded sets. A set that genuinely blocks
everything behind it must be missing its **closing mark** — and a missing closing mark means EOD, so there is
nothing behind it to strand. The only state that can strand healthy sets is **Foreign**, where another
series' data has physically overwritten part of this volume. Rare, and named explicitly in the plan when it
happens.

### 4.3 Beyond the TOC's last set — positive identification only

Past the last set the TOC describes, the walk keeps going until `endOfContentBlock`. There the evidential bar
is **higher**, not lower: only a block that positively classifies as a `TapeSetHeader` with matching `MediaId`
and `Volume` counts as a set. Anything else ends the walk.

The asymmetry with §4.2 is deliberate and load-bearing. *Inside* the TOC's range the TOC itself asserts that a
set exists there, so a bad header only removes corroboration. *Outside* it, the header is the **only** evidence
that a set exists at all — and the alternative explanations (a TOC copy, a gap file, garbage) are exactly what
a non-classifying block looks like.

### 4.4 Cost and abortability

One setmark hop plus one 16 KiB read per set, plus the single end-of-content seek. Minutes on LTO for a large
TOC — acceptable for a repair, not acceptable silently: the scan reports each set through `OnSetScanned` (§7.3)
and polls `IsAbortRequested` between sets. An aborted scan has written nothing and simply yields no plan.

### 4.5 Termination

- `Foreign` at the **first** set on the volume terminates the analysis outright: the TOC belongs to a different
  cartridge and there is nothing to reconcile. (§6.2's media-header check catches most of these earlier; this
  catches a TOC carrying no `MediaId`, where `ClassifySetHeader` skips the identity test.)
- `Foreign` anywhere else terminates the walk and becomes the truncation point.
- A **transport** error — anything outside `IsPositionalNavigationError`'s set — aborts the scan with a
  failure. A drive that has gone away produces silence, and a plan built on silence is fiction.

---

## 5. Undescribed sets — keeping what the TOC forgot

An imported or older TOC may describe **fewer** sets than the cartridge holds. Those sets identify themselves
perfectly — their headers carry `MediaId`, `Volume`, `VolumeSetIndex`, `GlobalSetIndex`, `Description` and
`CreatedUtc` — what is missing is the TOC's **description of their contents**. Hence **Undescribed**, not
"unidentifiable": the set knows exactly who it is.

Keeping them is worth real money to the user, because the file list is recoverable from elsewhere: a **later
volume's TOC describes every set of every earlier volume**, and a `.tapetoc` export may surface later. Data
destroyed to tidy an index is gone for good.

### 5.1 Why a placeholder entry is mandatory, not cosmetic

The tempting cheap option — leave them on tape and say nothing in the TOC — silently corrupts the cartridge.
`TapeTOC` set indices are the navigator's contract: `CurrentSetAsNavigatorContentSet` converts
`CurrentSetIndexOnVolume` straight into a mark count. If the TOC describes 3 sets while the tape holds 5, the
next appended set is recorded as index 4 but physically lands at position 6. Every from-begin navigation
afterwards lands two sets short.

**INV: the TOC's set count on a volume equals the physical set count on that volume.** Keeping an undescribed
set therefore *requires* a placeholder — an empty `TapeSetTOC` occupying its slot.

### 5.2 `TapeSetTOC.IsUndescribed` — NEW

A placeholder is an **empty** set, and the library has three places that treat an empty trailing set as
disposable. Each would quietly destroy a placeholder, so each needs the guard:

| Site | Today | With the flag |
|---|---|---|
| `TapeTOC.AddNewSetTOC` | reuses the last set when `Count == 0` | must not reuse an undescribed one — otherwise the next backup's TOC entry **claims** the undescribed set's slot while its data lands after it, putting every later index out by one |
| `TapeTOC.RemoveLastEmptySet` | removes it | refuses when undescribed |
| `TapeTOC.IsEmpty` | `Count == 1 && CurrentSetTOC.Count == 0` | an undescribed set is content, so the medium is not empty |
| `TapeTOC.ReplaceCurrentSetTOC` | — | clears the flag (the slot is being genuinely reused) |

Populated from the header, so the UI can name it: `Description`, `CreationTime`, `BlockSize`, `Volume`. All of
those setters are `internal` or `public`, and the reconciler lives in `TapeLibNET` — no visibility change
needed.

### 5.3 Persisting the flag — a version-gated TOC field

`TapeSetTOC.ConstructFrom` opens with a **strict** `ValidateSignature()` against the library-wide
`TapeSerializer.Version`, so a field cannot be appended there without invalidating every existing tape.
`TapeTOC`, by contrast, already owns a tolerant, independently versioned format (`TocVersionInitial` →
`TocVersionWithMediaId`), and `MediaId` set the precedent exactly.

```csharp
public const ushort TocVersionWithUndescribedSets = 0x0103;     // NEW
public const ushort TocVersion = TocVersionWithUndescribedSets;
```

Serialized **after** `ContinuedOnNextVolume` as a count plus the internal indices of the undescribed sets;
read back only when `version >= TocVersionWithUndescribedSets`, so every older TOC loads unchanged with the
flag false. Forward compatibility is the same as `MediaId`'s: a TOC written by this build does not load in an
older one. That was accepted then and the reasoning has not changed.

### 5.4 The honest cost

`TapeSetTOC.ComputeTotalFileSizeOnTape` returns only the header block for an undescribed set, so `Used` and
the overwrite anchor **under-report** by that set's real size. This is unavoidable — nothing knows the size —
and it is tolerable for one reason: since `Design-RemainingAndEw.md` Phase 3, the real stop signal for a
backup is the drive's **logical early warning**, a physical measurement that owes nothing to TOC arithmetic.
The under-report costs display accuracy, not safety. Stated in the plan, and in the media properties pane.

---

## 6. Workflow

```
1. Choose the TOC source         → in memory / from file
2. Check it against the media    → identity, volume, set-header presence, series warnings
3. SCAN (read-only, abortable)   → bound, then walk; per-set findings + progress
4. BUILD the plan                → keep / seal / remove / placeholder, with the truncation point named
5. PRESENT                       → the user approves (or edits the one partial-set cell)
6. APPLY (on confirmation)       → export old TOC, [seal], [truncate], reconcile the TOC, save it
7. REPORT                        → what was kept, and what to validate next
```

Steps 1–4 are `AnalyzeMediaAsync`; steps 6–7 are `RepairMediaAsync`. Step 5 belongs to the caller — which is
what makes the whole analysis unit-testable without a single tape write.

### 6.1 Step 1 — the TOC source

```csharp
public enum ReconciliationTocSource { Loaded, File }      // NEW
```

**The analysis never mutates the live TOC.** It builds a private working copy (`new TapeTOC(source)`) and
constructs its own `TapeSetAgent` over it. The service's `_toc` is replaced only on a successful apply.
Without this, an aborted analysis would leave the user's TOC quietly re-indexed.

"From a later volume" needs no third source: load that volume's TOC first, swap cartridges, run the repair
against the now-`Loaded` TOC.

### 6.2 Step 2 — does this TOC fit this media?

1. `RefreshLoadedHeader()`, then `EvaluateLoadedHeader(expectedSeriesId: workingToc.MediaId,
   expectedVolume: workingToc.Volume)` presented via `PresentVerdict(…, MediaPromptContext.RepairMedia, …)`
   (**NEW** enum value), `allowProceedAlways: false` — a repair is a one-off.
2. `LoadedMediaHeader?.HasSetHeaders` must be true. A volume declaring no set headers cannot be scanned:
   the descriptive test has nothing to read and the beyond-the-TOC probe has no evidential basis at all.
   Declined with that explanation. (§13.2 records why this may be worth relaxing later.)
3. **Series warnings — warn, never refuse.** Repairing a middle volume is legitimate; the user may simply
   have the damaged cartridge in hand. Two facts are surfaced when they apply, both at Warning, both
   requiring `ConfirmSeriesInterruption` on the repair request:
   - *Later volumes' sets leave this TOC.* `RemoveSetsAfterCurrent` prunes everything after the cut,
     including sets recorded on volumes 2+. The wording must make the limited blast radius plain:
     **"Volumes N+ remain intact and stay fully restorable through their own TOCs — each volume's TOC
     describes its own sets and those of every earlier volume. Only this copy of the index loses them."**
   - *A continuation link breaks.* When `ContinuedOnNextVolume` is set, or the truncated range contains a set
     whose successor carries `ContinuedFromPrevVolume`, the multi-volume chain is cut and a restore spanning
     it will ask for a volume this TOC no longer links to. See §13.4 — the *Skip Volume* work makes exactly
     that situation recoverable.

### 6.3 Step 6 — what "apply" does

1. **Export the pre-repair TOC** to a `.tapetoc` file (`SaveTOCToFile`), into
   `RepairMediaRequest.TocBackupFolder` (mirroring `BackupRequest.EmergencyTocFolder`). This is the only undo,
   so it is automatic rather than offered — and **a failed export aborts the repair**, unless the caller set
   `ProceedWithoutTocBackup`.
2. **Append placeholders** for every kept `Undescribed` set (§5). They can only ever sit at the end of the
   described range — the walk reaches them only after every described set has been passed — so appending is
   always the right operation, and never interacts with the truncation below.
3. **Seal the partial set**, if the plan keeps one (§7.4).
4. **Truncate**, if any *physically present* set is being removed: set `workingToc.CurrentSetIndex` to the
   first removed set and call `DeleteSetsFromCurrentSetUp` with the witness of §7.2. The delete performs the
   TOC pruning and the TOC write itself.
5. **Otherwise — the `Absent`-only or placeholder-only case** — prune the TOC in memory and call
   `BackupTOC()`. No content is written; `TapeUnchanged` is reported `true`.
6. **Adopt** the working TOC as `_toc`; fire `ServiceStateChange.TocChanged`.

Exactly one of steps 4 and 5 runs. Step 4's delete already saves the TOC, so a second `BackupTOC()` afterwards
would be a redundant write at the moment the cartridge has just been altered.

---
## 7. The agent layer

### 7.1 `TapeSetAgent.ScanContentSets` — NEW

```csharp
public TapeResult ScanContentSets(out IReadOnlyList<TapeSetScanEntry> findings,
                                  ITapeFileNotifiable? fileNotify = null);
```

Read-only. It belongs on `TapeSetAgent` rather than `TapeAgentBase` for the same reason
`DeleteSetsFromCurrentSetUp` does: it is a set-level operation, and a restore agent has no business offering
one. Being read-only, `BlocksOnUnverifiableSet` never comes into play.

```csharp
public enum TapeSetScanState                                     // NEW
{ Complete, Unverified, Undescribed, Partial, Damaged, Foreign, Absent }

public readonly record struct TapeSetScanEntry(                  // NEW — TapeSetScanEntry.cs
    int SetIndex,                       // standard (1-based) index; for Undescribed, the slot it would occupy
    TapeSetScanState State,
    TapeSetHeaderVerdict Verdict,       // raw classification, for diagnostics
    bool HasClosingMark,
    long StartBlock,                    // -1 when the set start was never reached
    string ExpectedDescription,         // from the TOC; empty for Undescribed
    string ActualDescription,           // header's DisplayName, or "(unreadable)"
    TapeSetHeader? Header,              // retained for Undescribed — it seeds the placeholder (§5.2)
    TapeResult Diagnosis);              // OK for Complete; the read/navigate fault otherwise
```

**The walk.** Every step below is load-bearing; the ordering is not a matter of taste.

```
EnsureMediaHeaderResolved()                          // presence must be known before any content navigation
if (!Navigator.SetHeadersExpected) → fail ERROR_NOT_SUPPORTED
_stats.Reset();  _setAnomalies.Clear();  ResetLatchedFailure()
Manager.EndReadWrite()

endOfContentBlock = Navigator.MoveToEndOfContent() ? Drive.CurrentBlock : long.MaxValue   // §3
ResetError()                                         // the fallback is a finding, not a failure
Navigator.MoveToBeginOfContent()                     // Present ⇒ lands at block 1, past the media header

setIndex = TOC.FirstSetOnVolume
loop:
    ThrowIfAbortRequested("scanning sets")
    start = Drive.CurrentBlock                       // BEFORE the read — SH-6 may reset the content position
    described = setIndex <= TOC.LastSetOnVolume

    if (start >= endOfContentBlock):
        for each remaining described set → Absent
        break

    header  = ReadSetHeader()                        // one 16 KiB block; advances the head by exactly 1
    if (read failed with a POSITIONAL error):
        if described → Absent for this and every remaining set
        break
    if (read failed otherwise) → transport fault: latch, abort the scan

    verdict = ClassifySetHeader(header)              // PURE; the ladder is NOT entered
    if (!described && verdict != Match) break        // §4.3 — TOC area, gap file, or garbage: not a set

    if (verdict is WrongMedia or WrongVolume):
        if (setIndex == TOC.FirstSetOnVolume)
            → fail ERROR_INVALID_DATA "the TOC belongs to a different cartridge"
        record Foreign;  break

    Drive.MoveToBlock(start + 1)                     // re-anchor if the read path disturbed the position
    hasMark = Navigator.MoveToNextContentSetmark(1)
    if (!hasMark && !IsPositionalNavigationError(LastErrorWin32))
        → transport fault: latch, abort the scan

    state = !hasMark ? (verdict == Match && described ? Partial : Damaged)
          : !described                               ? Undescribed
          : verdict == Match                         ? Complete
          :                                            Unverified

    record entry;  _stats.Sets.SetsProcessed++;  if (state == Complete) _stats.Sets.SetsSucceeded++
    NotifySetScanned(fileNotify, entry)
    if (!hasMark) break                              // EOD: nothing lies beyond
    setIndex++

Navigator.ResetContentSet()                          // raw verbs moved the head — believe nothing
```

Four details an implementation must not skip:

- **The anchor is captured before the read.** SH-6 requires a failed set-header block operation to call
  `ResetContentSet()`, and a successful read advances one block. Deriving the position afterwards from
  `Drive.CurrentBlock` on the failure path names the wrong block.
- **`Unverified` still hops.** A set whose header will not classify may own a perfectly good closing setmark,
  and the walk must continue past it — that is the whole of §4.2.
- **Positional vs. transport errors are different findings.** `IsPositionalNavigationError` (SH-20's existing
  set) means *the tape ends here*. Anything else means the drive went away.
- **Partial and Damaged terminate the loop.** No closing mark means EOD; continuing would read past it.

**Visibility changes required** (all one-word, no behaviour): `TapeAgentBase.ReadSetHeader` →
`private protected`; `TapeAgentBase.IsPositionalNavigationError` → `private protected static`.
`ClassifySetHeader` is already `internal`, `MoveToNextContentSetmark` already `internal`.

**Statistics.** The scan reuses `SetsProcessed` / `SetsSucceeded`, incremented directly, exactly as
`DeleteSetsFromCurrentSetUp` does. The v1 draft's *"[what counters??]"* is answered by not adding any:
`TapeSetStatistics` is nested inside `TapeFileStatistics` and copied by value into **every**
`ITapeFileNotifiable` callback in the library, so each new field is paid for by every notification on every
path. The per-state breakdown lives in the findings, where it belongs.

### 7.2 The write witness — the draft's one real error, generalized

The v1 draft concluded that `DeleteSetsFromCurrentSetUp` needs no changes, and that SH-13 would "re-confirm
the scan's conclusion at the moment of the write". **It would do the opposite.**

The trailing branch navigates to `CurrentSetIndex` — the first set to *delete* — and calls
`VerifyBeforeDestructiveWrite`, which demands `Match` from the header standing there. In a repair that set is
by construction the casualty. The verdict is `Unreadable` or worse, `TapeSetAgent.BlocksOnUnverifiableSet` is
`true`, and the delete **refuses** — on every cartridge the feature exists to fix. Setting
`VerifiesSetHeader = false` would work but throws away the guard entirely, on the single most dangerous write
in the product.

The fix is to notice **which set the verification actually protects**. The trailing delete rewrites the
setmark closing the *last retained* set. The set that must survive is *N*; the set being read for
authorization is *N+1* — the wrong witness, and in a repair the one witness guaranteed unavailable.

```csharp
public readonly record struct SetWriteWitness(int AtSetIndex)              // NEW
{
    /// Legacy: verify the set header standing at the write target itself.
    public static readonly SetWriteWitness TargetSet   = new(0);
    /// No header witness exists (delete-all with a damaged first set) — assert the POSITION only.
    public static readonly SetWriteWitness PositionOnly = new(-1);
    /// Verify at this (preceding) set, then hop forward to the target.
    public static SetWriteWitness PrecedingSet(int stdSetIndex) => new(stdSetIndex);
}

public TapeResult DeleteSetsFromCurrentSetUp(
    bool navigateFromBegin = false,
    ITapeFileNotifiable? fileNotify = null,
    SetWriteWitness witness = default);          // default == TargetSet == today's behaviour
```

**`PrecedingSet(A)` semantics, trailing branch:**

1. Save `TOC.CurrentSetIndex`; set it to `A` (`ClassifySetHeader` reads the TOC's expectation, so the witness
   must be the current set while it runs). Restore in a `finally`.
2. `NavigateToTargetContentSet` to set *A* — SH-20's recovery applies normally, and this count runs over the
   volume's **healthy** region.
3. `VerifyBeforeDestructiveWrite` → `Match` required. Nothing is relaxed: the write is still authorized by a
   framed, CRC-checked record read during the same operation. SH-13 holds verbatim; only *which* record
   satisfies it changes. The scan already proved this set sound, so a disagreement here is a genuine
   late-breaking fault and blocking is correct.
4. The head sits at `setAStart + 1` (SH-15). `Navigator.MoveToNextContentSetmark(firstRemoved − A)` lands just
   past the last kept set's closing mark — the **identical physical position** the `TargetSet` path reaches by
   counting, now derived from a verified anchor. Every intervening set has a confirmed closing mark (the scan
   proved it), so hopping *k* marks is exactly as safe as hopping one.
5. From there the existing code is unchanged: `MoveToNextContentSetmark(-1)`, `WriteContentSetmark()`,
   `OnContentWritten()`, prune the TOC, `BackupTOC()`.

**Why *k* marks matters:** it is what lets the last kept set be `Unverified`. The reconciler simply picks the
nearest preceding `Complete` set as the witness. A design that could only step back one set would have to
refuse whenever the boundary set's header was the corrupt one — the common case.

**`PositionOnly`, delete-all branch:** `MoveToBeginOfContent()` lands at a deterministic block — 1 on headed
media, 0 on legacy — so the fact worth asserting is *"the head cleared the media header"* (INV-4), and
`Drive.CurrentBlock` states that directly. Under `TargetSet` the branch additionally requires the block there
to classify as this volume's first set header; under `PositionOnly` there is no preceding set to witness
anything, and demanding a clean header from set 1 would refuse exactly the cartridge whose set 1 is the
casualty. INV-4 is preserved in full — `BackupInitialTOC(writeHeader: false)` still writes from block 1.

*Why a parameter rather than a second verb:* the paths differ in one navigation step and share the
truncation, the TOC pruning, the anomaly channel and the INV-4 handling. Duplicating that body is how two
copies of a delete drift apart, and only one of them gets the next fix.

### 7.3 `ITapeFileNotifiable.OnSetScanned` — NEW, defaulted

```csharp
void OnSetScanned(in TapeSetScanEntry entry, in TapeFileStatistics stats) { }   // default: no-op
```

Defaulted, so all four existing implementers compile untouched — the mechanism `OnSetAnomaly` /
`OnSetAnomalyRecovered` already used. Its default is a **no-op** rather than `Abort`, and the asymmetry with
SH-18 is deliberate: `OnSetAnomaly` asks permission for a destructive repositioning; this one reports an
observation from a read-only walk. There is nothing to withhold.

Reusing `SetStart` / `SetEnd` was rejected: `SetEnd` renders a file-counter delta, so a scan would emit
*"Set #3 complete: 0 succeeded"* for every set on the cartridge.

`NotifySetScanned` follows the established wrapper discipline: refresh the estimate, call, catch
`TapeAbortRequestedException` → record `IsAbortRequested` and **return without rethrowing** (the walk's own
between-sets poll stops it cleanly at a set boundary), catch everything else → log at Warning.

### 7.4 `TapeSetAgent.SealPartialSet` — NEW, and the draft's algorithm replaced

Salvaging the partial set means writing its missing closing setmark. The v1 draft's algorithm — fast-forward
to EOD, **write a gap file**, watch for an early-warning or EOM error, then backtrack and write the setmark —
should not be implemented. Three reasons, ascending in seriousness:

- It **appends junk to the user's backup set**, inside the one set whose contents are already suspect.
- It **spends the very capacity it is probing for**. The gap file must be large enough to trip EW, and EW
  fires when roughly the TOC reserve remains — so a probe that succeeds has consumed the room the TOC needs.
- "Backtrack to just before the gap file" **cannot be expressed safely**. A backward reposition followed by a
  write truncates; landing one block off destroys the tail of the last file in the set, and no
  `TapeFileInfo.Address` would know.

None of it is necessary, because the library already owns the answer. The estimator exists precisely to answer
*"is there still room for the TOC?"* without writing anything.

```csharp
public TapeResult SealPartialSet(out long sealedAtBlock,                 // NEW
                                 ITapeFileNotifiable? fileNotify = null);
```

```
EnsureMediaHeaderResolved();  Manager.EndReadWrite()
Navigate to the partial set and VERIFY its header (Match required)
    → the one thing we DO know about this set is that its header is sound; refusing to
      seal a set we cannot identify is free and correct.
Navigator.MoveToEndOfContent()          // EOD IS the end of the partial set, by definition
sealedAtBlock = Drive.CurrentBlock
if (!Drive.HasInitiatorPartition):      // TOC co-located ⇒ it needs room
    if (Drive.EstimatedContentRemaining < Navigator.TOCCapacity)
        → fail ERROR_DISK_FULL "not enough room remains for the table of contents"
Navigator.WriteContentSetmark();  Navigator.OnContentWritten()
```

No verification is needed for the setmark write itself: it lands **at EOD**, where nothing stands to be
destroyed — the same reason §3 of the verified-write design charges the append path nothing.

**The default remains: drop the partial set.** Its TOC entries may name files that never reached tape, so
keeping it means keeping a TOC that lies; and keeping it means writing into a region nothing has verified.
Offered as an unchecked advanced option, *"attempt to salvage the last partial backup set"*. When taken, the
result sets `ValidationRecommended` and the summary **mandates** a Validate pass rather than suggesting one.

### 7.5 What the result must not claim

Reachability is not integrity. The kept sets' **files** are unverified — the scan proved the sets can be
located, nothing more. `RepairMediaResult.ValidationRecommended` is true whenever anything was applied, the
summary says so in those terms, and the UI's closing action is **"Validate the repaired sets now"**.

---

## 8. The plan — a reviewable value

```csharp
public sealed record MediaReconciliationPlan                       // NEW
{
    // ── Identity / staleness guard ──────────────────────────────────────────
    public required Guid     MediaId     { get; init; }
    public required int      Volume      { get; init; }
    public required int      TocSetCount { get; init; }
    public required DateTime ScannedUtc  { get; init; }

    // ── Findings ────────────────────────────────────────────────────────────
    public required IReadOnlyList<TapeSetScanEntry> Findings { get; init; }

    // ── Verdict ─────────────────────────────────────────────────────────────
    public required int LastKeptSetIndex     { get; init; }   // 0 ⇒ nothing kept (delete-all)
    public required int FirstRemovedSetIndex { get; init; }   // 0 ⇒ nothing removed
    public required int WitnessSetIndex      { get; init; }   // nearest preceding Complete set; 0 ⇒ PositionOnly
    public IReadOnlyList<int> UndescribedSetIndexes { get; init; } = [];  // placeholders to mint
    public IReadOnlyList<int> UnverifiedSetIndexes  { get; init; } = [];  // kept, header unreadable
    public IReadOnlyList<int> StrandedSetIndexes    { get; init; } = [];  // healthy, behind a Foreign set
    public IReadOnlyList<int> AbsentSetIndexes      { get; init; } = [];  // TOC-only removal
    public bool HasPartialSet     { get; init; }
    public int  PartialSetIndex   { get; init; }
    public bool SalvagePartialSet { get; init; }              // the ONE editable field
    public bool RequiresTapeWrite { get; init; }              // false ⇒ the TOC-only case
    public bool InterruptsSeries  { get; init; }              // §6.2 rule 3
    public int  SetsLeavingTocOnLaterVolumes { get; init; }
    public long EstimatedBytesFreed { get; init; }

    public bool IsNoOp => FirstRemovedSetIndex == 0 && !HasPartialSet
                          && UndescribedSetIndexes.Count == 0 && AbsentSetIndexes.Count == 0;
}
```

**The staleness guard is not decoration.** Between analysis and repair the user may eject the cartridge, run
another operation, or load a different TOC. `RepairMediaCore` re-validates `MediaId`, `Volume` and
`TocSetCount` against the live state before exporting a single byte, and fails with `ERROR_INVALID_STATE` on
any disagreement. A plan is a promise about a cartridge, and promises expire.

**`SalvagePartialSet` is the only field a caller may change.** Everything else is determined by evidence. A
repair dialog that invites per-set fiddling invites the mistake it exists to prevent, so the plan exposes no
per-set disposition setter at all.

---

## 9. The service layer

A new partial, `TapeServiceBase.Repair.cs`, following the established request → operation → result triad.

```csharp
public sealed record AnalyzeMediaRequest(                          // NEW
    ReconciliationTocSource TocSource = ReconciliationTocSource.Loaded,
    string? TocFilePath = null) : ServiceOperationRequest
{
    public bool ProceedOnMediaMismatch { get; init; } = false;     // suppresses the §6.2 prompt only
}

public sealed record RepairMediaRequest(                           // NEW
    MediaReconciliationPlan Plan,
    string? TocBackupFolder = null) : ServiceOperationRequest
{
    public bool ProceedWithoutTocBackup     { get; init; } = false;
    public bool ConfirmSeriesInterruption   { get; init; } = false;  // required when InterruptsSeries
    public bool KeepUndescribedSets         { get; init; } = true;   // §5 — default KEEP
}

public sealed record MediaAnalysisResult : ServiceOperationResult   // NEW
{
    public MediaReconciliationPlan? Plan { get; init; }
    public int SetsScanned { get; init; }
    public int SetsComplete { get; init; }
    public string Summary { get; init; } = string.Empty;
}

public sealed record RepairMediaResult : ServiceOperationResult     // NEW
{
    public int  SetsKept        { get; init; }
    public int  SetsRemoved     { get; init; }
    public int  PlaceholdersAdded { get; init; }
    public bool TapeUnchanged   { get; init; }      // true for the TOC-only reconciliation
    public bool PartialSetSealed{ get; init; }
    public long EstimatedBytesFreed { get; init; }
    public string? TocBackupPath{ get; init; }
    public bool ValidationRecommended { get; init; }
}

public Task<MediaAnalysisResult> AnalyzeMediaAsync(AnalyzeMediaRequest request);
public Task<RepairMediaResult>   RepairMediaAsync(RepairMediaRequest request);
```

Both results derive from `ServiceOperationResult` **directly**, not from `FileOperationResult` — for exactly
the reason `DeleteSetsResult` does: routing a set-level verb through file counters produces the *"completed —
no files processed"* line the error-reporting overhaul exists to eliminate.

### 9.1 Policy lives here

`BuildPlan(findings, workingToc)` is a **pure static** on `TapeServiceBase`, mirroring `JudgeFileOperation`
and `AdviseOnSetAnomalies`. It:

- takes the kept prefix as every entry up to the first `Partial` / `Damaged` / `Foreign`;
- sets `WitnessSetIndex` to the nearest preceding `Complete` entry (0 when none — delete-all,
  `SetWriteWitness.PositionOnly`);
- collects `Undescribed`, `Unverified`, `Absent` and (behind a `Foreign` only) `Stranded` indexes;
- computes `RequiresTapeWrite` = at least one *physically present* set is removed;
- estimates freed bytes from `TapeSetTOC.ComputeTotalFileSizeOnTape(DefaultBlockSize, withSetHeader: true)`
  over the removed described sets, noting that undescribed removals contribute nothing (§5.4);
- computes `InterruptsSeries` and `SetsLeavingTocOnLaterVolumes` from the working TOC.

Pure and static means the whole policy is testable from a hand-built findings list — no drive, no tape, no
fixture. `VerbalizeRepair(plan)` and `VerbalizeRepairResult(result)` sit beside it, keeping the
judge/verbalize split `TapeServiceBase.Outcome.cs` already establishes.

### 9.2 Host surface — nothing new

No new prompt. The plan **is** the prompt. `OnSetAnomalySelect` already exists for anything the underlying
delete raises during the witness verification, and the repair's progress handler is a plain
`ServiceSetProgressHandler`, which already overrides `OnSetAnomaly` correctly. Passing it is mandatory:
without a notifiable, the agent's no-notifiable policy declines every destructive recovery (SH-18) — the
identical defect §9.1 of the verified-write design found in `DeleteBackupSetsAsync`.

`ConfirmSeriesInterruption` is answered by the caller, so a non-interactive host that leaves it false gets a
clear refusal rather than a silent break of a multi-volume chain.

### 9.3 Where the feature is offered

| Trigger | Why |
|---|---|
| Media \| Repair Media… | always, for any loaded media |
| `SetAnomalyAdvice.RepairTrailingSets` banner | **retarget from Delete Backup Sets to here** — this is what that advice always meant, and it removes the *"which sets are lost?"* guesswork. `AdviseText` changes one string. |
| After a failed backup (TOC write failed, or `TOCUnlocated`) | the moment the in-memory TOC is simultaneously most valuable and most perishable |
| After a blocked delete (`Sets.SetWriteBlocked`) | a verification refused the write — precisely the condition reconciliation examines |
| After importing a `.tapetoc` onto a cartridge with more sets than it describes | the natural discovery path for the undescribed-set case |

**Not after a successful operation.** Nothing to repair, and offering it would read as an admission of doubt.

---
## 10. Testing approach

Every claim this document makes is testable without hardware, and most of it without a tape at all. The
suite already contains the machinery; this chapter records **which existing seam serves which claim**, and
the three rules that make the resulting tests worth having.

### 10.1 The three rules, inherited

The set-header suites established these, and they apply unchanged here:

- **Assert the tape, not the return value.** Every refusal test asserts that the pre-existing sets still
  restore **byte-for-byte** (`AssertSetStillRestores` / `FileComparer.AssertFilesMatch`). A repair that
  refused *after* clobbering a set header would pass a `Assert.False(result)` check and lose the user's data.
- **Assert the mechanism where luck could substitute for it.** A scan that reports the right findings by
  landing on the right block for the wrong reason is indistinguishable from a correct one — until it isn't.
  Hence the explicit `findings.Count` assertions of §11.2 and the "no anomaly was raised" assertion, which
  test *how* the walk behaved rather than merely what it returned.
- **Fault injection must be honest about what it perturbs.** `SimulateSetMiscount` is consumed by whichever
  hooked move comes first — on the setmark layouts that is the `-1` settle inside `MoveToEndOfContentCore`,
  and on `MoveToEndOfContent`'s blank-media fallback it is swallowed entirely. Where that makes it
  unreliable, use `SimulateNavigationFailures` with a specific error code instead.

### 10.2 The damage catalogue — which seam produces which scan state

The repair feature's whole surface is "what the cartridge looks like when something went wrong", so the
tests live or die on being able to manufacture each state deliberately. Every one is already reachable:

| Scan state | How to manufacture | Seam |
|---|---|---|
| `Complete` | back up normally | `VirtualTapeFixture.BackupFiles` |
| `Partial` | abort a backup mid-set, then drop the half-written set from the in-memory TOC | `TestNotifiable { AbortAfterNPreProcessed = 1 }` + `TOC.RemoveLastEmptySet()` — i.e. `DamageTheTail` |
| `Damaged` | the same, on a set whose header is **also** corrupted | `DamageTheTail` + `ContentReadFaults.CorruptOnce` |
| `Unverified` | corrupt the set-header block of an **interior** set, leaving its closing mark intact | `fixture.Backend.ContentReadFaults.CorruptOnce(bits: 2, offset: 48)` |
| `Foreign` | mutate `TOC.Volume` (or `MediaId`) before the scan, exactly as `WrongVolume_NeverRenavigates` does | in-memory TOC edit; no tape damage needed |
| `Absent` | back up N sets, then **append sets to the TOC in memory** that were never written — or scan a saved `.tapetoc` against a shorter tape | TOC edit / `SaveTOCToFile` before the last backups |
| `Undescribed` | back up N sets, **save the TOC after set N−2**, then back up two more without re-saving | `fixture.SaveTOC()` at the chosen point; the on-tape TOC then describes fewer sets than the tape holds |

Two notes on that table, because both cost time if rediscovered during implementation:

**`DamageTheTail` is layout-aware, and must stay so.** On the filemark layouts an aborted mid-set backup
suffices: the orphan set header throws off `MoveToNextFilemark(-3)`. The setmark layout shrugs that off —
correctly, since that is the entire value of a dedicated mark type — so there the trailing setmark must be
erased after the fact via `VirtualTapeFixture.EraseLastSetmark()`. The existing helper already branches on
`Drive.SupportsSetmarks`; reuse it rather than writing a second one.

**`Undescribed` needs no new helper at all.** `VirtualTapeFixture.SaveTOC()` / `LoadTOC()` already let a
test control *when* the TOC is written, and "the TOC on tape is older than the content on tape" is precisely
what an out-of-date TOC is. This is a more faithful fixture than any synthetic one, because it produces
exactly the cartridge a real interrupted session leaves behind.

### 10.3 Three layers, three kinds of test

**Pure.** `BuildPlan`, `VerbalizeRepair` and the placeholder TOC guards need no drive. `BuildPlan` is a
`protected static` on `TapeServiceBase`, so it is reached exactly as `AdviseOnSetAnomalies` already is —
through a tiny probe subclass:

```csharp
private sealed class PlanProbe(ITapeServiceHost host)
    : TapeServiceBase(TestLoggerFactory.Default, host)
{
    public static MediaReconciliationPlan Build(IReadOnlyList<TapeSetScanEntry> f, TapeTOC toc)
        => BuildPlan(f, toc);
}
```

A hand-built findings list then exercises every branch of the policy — the kept prefix, witness selection,
`RequiresTapeWrite`, `IsNoOp`, stranding — in microseconds, with no fixture, no temp files and no
possibility of a layout quirk masking a logic error. **This is where the bulk of the plan's coverage
belongs**, and it is why `BuildPlan` is specified as pure and static in the first place.

**Agent.** `VirtualTapeFixture` across all four `DriveProfile`s, via `[Theory] + [MemberData]`. The four
profiles are not ceremony here: §3's end-of-content bound behaves differently on each, and
`TapeNavigatorTOCInSetWithFmks` is the one where a naive walk marches straight into the TOC.

**Service.** `ServiceTestBase` + `TestTapeService` + `TestTapeServiceHost`, over `TempVirtualMedia`. Two
facilities matter especially:

- **`TestTapeService.OnAgentReady`** hands the test the live agent *inside* the operation, before anything is
  processed — which is how fault injection is armed without racing the worker thread. Repair needs one
  addition in the same spirit: `ScanContentSets` runs under `CreateSetProgressHandler`, which already invokes
  `OnAgentReady`, so **no new hook is required**.
- **`ServiceTestBase.Dispose`** asserts that every un-examined host prompted **zero** times, for both
  `MediaMismatchPrompts` and `SetAnomalyPrompts`. That teardown sweep is what makes "provably silent" a real
  assertion rather than an aspiration, and every repair test must either provoke no prompt or assert on it
  via `AssertMediaPrompts` / clear it explicitly.

### 10.4 Seeding must stay silent

`ServiceSetAnomalyTests.SeedSetsAsync` sets `ProceedOnMediaMismatch` on every seeding backup, for a reason
that applies verbatim to repair fixtures: the first backup overwrites, so it calls `ResetMediaId()` and
stamps a fresh header while the service's cached `_loadedHeader` still describes the pre-format cartridge —
and every append after it would prompt about a mismatch that exists nowhere on tape. Repair's seeding helper
reuses the same discipline, and asserts `Assert.Empty(host.SetAnomalyPrompts)` before handing the media to
the test proper. **A seeding artifact must never be mistakable for the behaviour under examination.**

### 10.5 What deliberately is *not* tested here

- **The ladder's own behaviour.** The scan bypasses `HandleSetHeaderVerdict` entirely, so re-testing drift
  correction under a repair fixture would only duplicate `TapeSetNavigationRecoveryTests` — and would couple
  the two features' suites for no gain. The one assertion repair owes on this axis is the *negative* one:
  that the scan raised **no** anomaly on a cartridge that would certainly trip the ladder (§11.2).
- **File-level integrity.** §12.1 — repair restores navigability, not content. Tests assert byte-for-byte
  restores of sets it claims to have *kept*, which is the strongest honest claim available.
- **Real hardware.** Every write in this feature is post-mark or at-EOD, already isolated by the existing
  **S11** conformance probe and accepted on AIT-2 and DLT-V4. No new probe is needed, exactly as the
  verified-write design concluded for the same reason.

### 10.6 Where the tests live

| File | Layer | Covers |
|---|---|---|
| `TapeTOCRoundTripTests` *(extended)* | pure | `IsUndescribed`, the four guards, TOC v0x0103 round-trip |
| `TapeSetScanTests` *(new)* | agent | the walk, the bound, every scan state, abortability |
| `TapeSetDeleteVerificationTests` *(extended)* | agent | `SetWriteWitness`, the *k*-mark hop, `PositionOnly` |
| `TapeSetSealTests` *(new)* | agent | `SealPartialSet` and its capacity guard |
| `ServiceRepairAnalysisTests` *(new)* | pure + service | `BuildPlan` in isolation; end-to-end analysis; §6.2's checks |
| `ServiceRepairApplyTests` *(new)* | service | the apply paths, the TOC-only case, staleness, refusals |

---

## 11. Implementation plan

Phases 0–4 are agent-only and land behind no UI. Each phase builds, ships and is tested on its own.

### Phase 0 — scaffolding *(≈ half a day)*

1. `TapeSetScanEntry.cs` — `TapeSetScanState`, `TapeSetScanEntry`.
2. `MediaReconciliationPlan.cs` — the plan record, `ReconciliationTocSource`.
3. `SetWriteWitness` (beside `TapeSetHeaderVerdict` in `TapeSetHeader.cs`).
4. `MediaPromptContext.RepairMedia` + host wording in `WpfServiceHost` / `ConsoleUxServiceHost`.
5. `ITapeFileNotifiable.OnSetScanned` (defaulted) + `TapeAgentBase.NotifySetScanned`.
6. The two visibility widenings of §7.1.

**Tests.** No new ones — the phase's whole claim is that it changes nothing.
- `TestNotifiable` gains `SetsScanned` (a `List<TapeSetScanEntry>`) plus `AssertNoSetsScanned()`, mirroring
  its existing `SetAnomalies` / `AssertNoSetAnomalies()` shape. Verify the defaulted interface member
  compiles against all four existing implementers untouched.

**Acceptance:** builds; all ~3,500 existing tests green; no behavioural change anywhere.

### Phase 1 — `TapeSetTOC.IsUndescribed` and TOC v0x0103

1. `IsUndescribed` on `TapeSetTOC`; the four guards of §5.2.
2. `TocVersionWithUndescribedSets = 0x0103`; serialize/deserialize the index list on `TapeTOC`,
   version-gated.
3. Audit the multi-volume path for the "remove the trailing empty set before saving" step and guard it.

**Tests — `TapeTOCRoundTripTests` (extended), pure unless noted:**
- `UndescribedFlag_RoundTrips` — save/reload preserves the flag on the right indices;
- `LegacyToc_LoadsWithoutUndescribedFlag` — a v0x0102 blob loads clean, flag false everywhere;
- **`AddNewSetTOC_AppendsRatherThanReusingUndescribed`** — *the index-invariant test*. Assert the new set's
  index is N+1 **and** the placeholder survives at N. Without this guard the very next backup silently
  offsets every from-begin navigation on the cartridge;
- `RemoveLastEmptySet_RefusesUndescribed`; `IsEmpty_FalseWithOnlyPlaceholder`;
  `ReplaceCurrentSetTOC_ClearsUndescribed`;
- `CreateSetHeader_IndexingSurvivesPlaceholders` — `VolumeSetIndex` / `GlobalSetIndex` stay correct across
  a placeholder;
- **agent-level, all four profiles:** `AppendAfterPlaceholder_LandsAtCorrectSetIndex` — back up over a
  fixture carrying a placeholder, then read the new set's **on-tape** header back and assert its
  `VolumeSetIndex` matches its TOC index; then restore an earlier set byte-for-byte. This is the physical
  counterpart of the pure test above, and the one that would catch an arithmetic slip the TOC alone hides.

### Phase 2 — `TapeSetAgent.ScanContentSets`

1. The §7.1 walk: the end-of-content bound and its fallback, the anchor-before-read discipline, the
   positional-vs-transport split, the described/undescribed evidential asymmetry, the closing
   `ResetContentSet()`.
2. Hard-fail `ERROR_NOT_SUPPORTED` when `!Navigator.SetHeadersExpected`.

**Tests — `TapeSetScanTests` (new), `[Theory] + [MemberData(nameof(AllProfiles))]`:**

*The bound (§3) — the class of false finding this phase must never produce:*
- **`CleanTape_ScansExactlySetCount`** — 4 sound sets ⇒ `Assert.Equal(4, findings.Count)` and all
  `Complete`. The exact count is the assertion: on `SeqFilemarks` and `FilemarksOnly` a walk that missed the
  bound reads a TOC copy or the gap file and reports a phantom fifth set;
- `NoEndOfContent_FallsBackToEod` — damage the tail so `MoveToEndOfContent` cannot settle, then assert the
  walk still terminates and reports the damage rather than hanging or erroring.

*Each state, deliberately manufactured (§10.2):*
- `DamagedTail_LastSetIsPartial` — via `DamageTheTail`;
- **`CorruptInteriorHeader_IsUnverified_AndLaterSetsStillComplete`** — *pins §4.2*, the design's central
  claim. `ContentReadFaults.CorruptOnce` on set 3's header; assert set 3 `Unverified` and sets 4–5
  `Complete`. A version that treated a bad header as structural failure would report two phantom losses;
- `TocDescribesMoreSetsThanTape_ReportsAbsent` — and asserts no tape write is even attempted;
- `TapeHoldsMoreSetsThanToc_ReportsUndescribed` — via the `SaveTOC()`-early fixture; assert **exactly** the
  right count on every profile (the beyond-the-TOC probe is where a TOC copy would be miscounted as a set),
  and that each entry carries the header's `Description`;
- `WrongVolumeAtFirstSet_TerminatesAnalysis` — mutate `TOC.Volume`, assert `ERROR_INVALID_DATA` and that
  nothing moved;
- `HeaderlessVolume_Declines` — `withSetHeaders: false` fixture ⇒ `ERROR_NOT_SUPPORTED`, having moved
  nothing.

*The mechanism, not the outcome:*
- **`Scan_RaisesNoAnomaly_AndPromptsNobody`** — arm `SimulateSetMiscount` so the ladder would certainly
  trip, then assert via `TestNotifiable` that `SetAnomalies` is empty and `SetAnomaliesRecovered` is empty,
  while `SetsScanned` is populated. This is §2's *"observes, does not repair"* asserted rather than assumed,
  and it is the test that catches a future refactor routing the scan through `HandleSetHeaderVerdict`;
- `Scan_LeavesNoBelievedPosition` — assert `Navigator.CurrentContentSet == TapeNavigator.UnknownSet`
  afterwards (RM-1);
- `TransportFault_AbortsScan_RatherThanReportingFindings` — `SimulateNavigationFailures` with
  `ERROR_NOT_READY`; assert the scan **fails** instead of returning a plausible-looking partial list. A plan
  built on a dead drive's silence is fiction;
- `Abort_MidScan_LeavesTapeByteIdentical` — snapshot the virtual medium's bytes before and after;
  `ERROR_CANCELLED`, partial findings, identical bytes.

### Phase 3 — `SetWriteWitness`

1. The defaulted parameter; the §7.2 sequence with `TOC.CurrentSetIndex` saved/restored in a `finally`.
2. The *k*-mark hop; `PositionOnly` in the delete-all branch with INV-4 preserved.

**Tests — `TapeSetDeleteVerificationTests` (extended), all four profiles:**
- **`CorruptTargetHeader_TargetSetRefuses_PrecedingSetSucceeds`** — *the regression this phase exists for*,
  and it asserts **both** halves: `TargetSet` must refuse (proving the v1 draft's defect was real) and
  `PrecedingSet(N)` must succeed on the identical cartridge;
- `PrecedingSet_HopsMultipleMarks` — witness at set 2, delete from set 5, sets 3–4 `Unverified`. The
  capability that lets the boundary set be the corrupt one;
- `PrecedingSet_StillRefusesWhenWitnessIsCorrupt` — the guard is moved, not removed;
- **`BothWitnesses_LandOnTheSameBlock`** — assert `Drive.CurrentBlock` immediately before the setmark
  rewrite is identical under both witnesses on a *healthy* cartridge. This is the test that proves the new
  path is the same physical operation rather than a parallel one;
- `DeleteAll_PositionOnly_PreservesMediaHeader` — corrupt set 1's header; assert the delete succeeds and
  the media header survives (INV-4 regressions are otherwise entirely silent);
- every refusal case asserts the surviving sets still restore **byte-for-byte**.

### Phase 4 — `TapeSetAgent.SealPartialSet`

Per §7.4.

**Tests — `TapeSetSealTests` (new):**
- `Seal_DamagedTail_MakesSetNavigable` — after sealing, the set count is restored and the sealed set
  restores its files; a subsequent `MoveToEndOfContent` settles cleanly;
- **`Seal_NearFullMedia_RefusesAndLeavesTapeUnchanged`** — a small fixture carrying
  `VirtualTapeEwProfile.Lto4Like`, written close to EW; assert `ERROR_DISK_FULL` **and** byte-identical
  media. Refusing after writing the setmark would be strictly worse than not refusing at all;
- `Seal_WithInitiatorPartition_IgnoresContentReserve` — the `Partitions` profile seals regardless;
- `Seal_UnclassifiableHeader_Refuses`.

### Phase 5 — `AnalyzeMediaAsync` + `BuildPlan`

1. `TapeServiceBase.Repair.cs`: working-TOC copy, §6.2's checks and warnings, agent construction, scan,
   `BuildPlan`, `Summary`.

**Tests — `ServiceRepairAnalysisTests` (new):**

*Pure, via `PlanProbe` (§10.3) — no drive, no fixture:*
- `BuildPlan_KeepsLongestSoundPrefix` — `[Theory]` over hand-built findings lists covering every boundary
  state (`Partial`, `Damaged`, `Foreign` at positions 1, middle, last);
- `BuildPlan_WitnessIsNearestPrecedingComplete` — including the case where the boundary's predecessors are
  `Unverified` and the witness must reach further back, and the delete-all case yielding `PositionOnly`;
- `BuildPlan_AbsentOnly_RequiresNoTapeWrite`; `BuildPlan_UndescribedOnly_RequiresNoTapeWrite`;
- `BuildPlan_AllComplete_IsNoOp`;
- `BuildPlan_StrandsOnlyBehindForeign` — asserts the §4.2 conclusion at the policy layer: an `Unverified`
  set strands nothing.

*End-to-end:*
- one analysis per Phase-2 fixture, asserting the expected plan;
- **`Analysis_NeverMutatesLiveToc`** — assert both the reference and the set count afterwards. An analysis
  that quietly re-indexed the user's TOC would corrupt the cartridge on the *next* unrelated operation;
- `MiddleVolume_WarnsButSucceeds` — assert `InterruptsSeries`, `SetsLeavingTocOnLaterVolumes`, and that the
  analysis **returns a plan** rather than refusing (§6.2 rule 3, as revised);
- `ForeignCartridge_PromptsThenAborts` — `MediaMismatchAnswers.Enqueue(Abort)`, then
  `AssertMediaPrompts(host, (TapeMediaVerdict.MediaIdMismatch, MediaPromptContext.RepairMedia))` and assert
  no scan ran;
- `ProceedOnMediaMismatch_SuppressesPromptButLogsWarning` — `AssertNoMediaPrompts` plus
  `host.ContainsMessage(...)`.

### Phase 6 — `RepairMediaAsync`

1. Staleness re-validation, then the §6.3 sequence.
2. Placeholder minting from `TapeSetScanEntry.Header`; seal; truncate-with-witness **or** TOC-only prune.
3. Adopt the working TOC; report via `ReportSetAnomalyOutcome` plus a repair headline.
4. Retarget `AdviseText(SetAnomalyAdvice.RepairTrailingSets)` at Repair Media.

**Tests — `ServiceRepairApplyTests` (new):**
- **`DamagedTail_Repaired_CartridgeIsUsableAgain`** — the feature's reason for existing, and it asserts four
  separable things: the kept sets restore byte-for-byte, the removed ones are gone from the reloaded TOC, a
  **subsequent append succeeds**, and that appended set lands at the right index. Only the last two prove
  the cartridge is *usable*, not merely tidy;
- **`AbsentOnly_TouchesNoContent`** — `TapeUnchanged == true`, TOC corrected, and the virtual medium's bytes
  below the TOC region are byte-identical. The strongest available statement of the TOC-only claim;
- `UndescribedSets_Kept_SurviveTocReload_AndNextAppendIsCorrect` — reload the TOC **from tape** to prove the
  v0x0103 field round-tripped through the real write path, then append and check the on-tape header index;
- `KeepUndescribedSets_False_TruncatesThem`;
- `SalvagePartial_On_SetSurvivesAndValidationIsRecommended` / `_Off_SetIsTruncated`;
- `MiddleVolume_WithoutConfirmation_IsRefused` — tape unchanged;
- **`StalePlan_IsRejectedBeforeAnythingIsWritten`** — swap the cartridge between analyze and repair; assert
  `ERROR_INVALID_STATE`, **no TOC export file created**, and byte-identical media. Ordering matters: a
  version that exported first would leave a stray file describing the wrong cartridge;
- `TocExportFailure_AbortsBeforeAnyTapeWrite` — an unwritable folder;
- `NoOpPlan_ReportsNothingToRepair_AndWritesNothing`;
- `Repair_ReportsThroughTheSetChannel` — `host.ContainsMessage` on the headline, and the teardown sweep
  confirms no unexamined prompt.

### Phase 7 — TapeConNET

`tapecon repair-media [--dry-run] [--salvage-partial] [--drop-undescribed] [--allow-series-break]
[--toc-backup <dir>] [--yes]`. `--dry-run` prints the plan and exits — the CLI's natural expression of
propose-then-act, and a useful diagnostic on its own. A non-zero exit code distinguishes *"refused, tape
unchanged"* from *"failed part-way"*, as `DeleteSetsResult.TapeUnchanged` already does for delete.

**Tests — `TapeConNET.Tests`, in-process via `TapeConHost`:** `--dry-run` prints a plan and leaves the media
byte-identical; a damaged-tail cartridge repairs end-to-end and the restored files match; the
unattended path without `--allow-series-break` exits non-zero on a middle volume with the tape unchanged.

### Phase 8 — TapeWinNET *(§14)*

---
## 12. Invariants

| | |
|---|---|
| **RM-1** | The scan writes nothing, moves no mark, and leaves the navigator with no believed content-set position. |
| **RM-2** | The scan classifies via the pure `ClassifySetHeader` and never enters `HandleSetHeaderVerdict`: no anomaly, no prompt, no correction. |
| **RM-3** | The walk is bounded by end-of-content, or by EOD when end-of-content cannot be established. It never reads the TOC area as content. |
| **RM-4** | A set is kept when its **closing mark** exists. Header verdicts grade confidence, never structural fate — except `Foreign`, which terminates the walk. |
| **RM-5** | Beyond the TOC's described sets, only a positively classified set header with matching identity counts as a set. |
| **RM-6** | A kept undescribed set always receives a TOC placeholder: the TOC's set count on a volume equals the physical set count on that volume. |
| **RM-7** | An undescribed placeholder is never reused as an empty slot, never removed as a trailing empty set, and never makes the TOC read as empty. |
| **RM-8** | Under a `PrecedingSet` witness, SH-13 is satisfied by a set that must SURVIVE the write, read at a verified position during the same operation. SH-13 is never waived. |
| **RM-9** | The delete-all branch under `PositionOnly` asserts the block number, never a header — INV-4 holds unchanged. |
| **RM-10** | The partial set is sealed only at EOD, and only when the estimated remaining capacity covers the TOC reserve. |
| **RM-11** | Nothing is applied before the pre-repair TOC has been exported, or the caller has explicitly waived it. |
| **RM-12** | A plan is validated against `MediaId`, `Volume` and TOC set count before any byte is exported or written. |
| **RM-13** | The analysis never mutates the service's live TOC; adoption happens only on a successful apply. |

---

## 13. Known boundaries, and what they suggest next

### 13.1 Integrity is never claimed
Within a kept set, a file whose bytes are damaged is still caught only by CRC, during a Validate pass. The
repair restores *navigability*, not *content*.

### 13.2 Header-less media is out of scope — but need not stay so
§6.2 declines a volume declaring no set headers. Worth recording: **the structural test needs no set header at
all.** A mark-only scan would still detect the missing closing setmark — the dominant real-world fault — and
would only be blind to `Unverified` and `Undescribed`. A degraded "mark-only" mode is therefore a genuinely
useful future option. It is out of v1 because a plan built on one test must be *presented* very differently
from one built on two, and conflating the two presentations is how a user ends up trusting the weaker evidence.

### 13.3 Capacity accounting under-reports undescribed sets
§5.4. Display accuracy, not safety — the early warning is a physical measurement. A future refinement could
estimate an undescribed set's size from the block delta between its start and the next set's start, which the
scan already knows; worth doing when the display becomes the complaint.

### 13.4 **Next step — "Skip Volume" for multi-volume restore** *(tabled here so it is not lost)*
Today a restore that asks for a volume the user has lost, or does not wish to mount, can only be **aborted** —
losing every set already restored from the volumes in hand. It should instead offer **Skip Volume**: drop
every set and file belonging to that volume from the operation and continue with the rest.

This is the natural companion to §6.2's series warning: a repaired middle volume breaks the chain, and Skip
Volume is what makes the remaining volumes fully usable anyway. Shape, at first sight: a third choice on
`ITapeServiceHost.OnInsertMediaConfirm` (or a sibling `OnVolumeSkipConfirm`), the restore agent filtering the
remaining selection by `TapeSetTOC.Volume`, and `RestoreResult.FilesMissing` distinguishing *skipped by
volume* from *never found*. `MultiVolumeTapeServiceHost` already scripts volume swaps, so the test shape is
"withhold volume 2 and assert volumes 1 and 3 restore byte-for-byte". Its own design increment.

### 13.5 Scan resumability is unnecessary
An aborted scan has written nothing and can simply be re-run. The setmarks on tape are themselves the
resumable points — the same conclusion the calibration trail reached for a far more expensive operation.

---

## 14. WPF — the concluding phase (outline)

Deferred by design, specified only far enough to confirm the service API supports it.

- **Phase 1 — source and scan.** TOC-source radio (In memory · From file…), the media identity summary, the
  series warnings when they apply, and [Scan]. Progress over sets via `OnSetScanned`, cancellable, live log.
- **Phase 2 — the plan.** One read-only row per set, with the partial row's dropdown as **the only editable
  cell**, plus a single "Keep undescribed sets" checkbox:

  | | Set | Description | Files | Disposition |
  |---|---|---|---|---|
  | ✔ | #1 \| -5 | Monthly — March | 1,204 | Keep |
  | ⚠ | #2 \| -4 | Monthly — April | 987 | Keep — *marker unreadable* |
  | ✔ | #3 \| -3 | Monthly — May | 412 | Keep |
  | ◆ | #4 \| -2 | *Monthly — June* (from tape) | — | Keep — *contents not described* |
  | ⚠ | #5 \| -1 | Monthly — July | 566 | **Partial — remove** ▾ |
  | ◌ | #6 \| 0 | Monthly — August | 341 | Not on tape — remove from index |

  Below it, plain language naming the truncation point, the bytes freed, and any series consequence. Then the
  irreversibility warning and [Repair] / [Cancel]. `MediaUsageBar` as in `DeleteBackupSetsWindow`: kept sets
  completed-green, undescribed a distinct shade, removed error-red, partial warning-yellow.
- **Phase 3 — execution.** The shared operation overlay, phase text (*"Scanning sets…"*, *"Sealing partial
  set…"*, *"Truncating…"*), counter advancing by sets.
- **Phase 4 — result.** Sets kept, removed, placeholders added, capacity freed, TOC backup path, and
  **[Validate the repaired sets]** as the closing action.
