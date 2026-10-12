# Design — Verified Destructive Navigation for TapeLibNET

**Status:** v3.1 · **proposed** (supersedes v2.1 — unified recovery ladder, both paths; some renames vs. 3.0)
**Scope:** set-header verification on the destructive paths (overwrite, delete), a two-stage recovery shared
by every agent, and the set-level notification channel
**Depends on:** the set header (`docs/Design-SetHeader.md`, v3) — record, presence model, verdict ladder,
relative correction; the media header (`docs/Design-TapeHeader.md`, v12) — grammar, framing, `TapeHeaderBlock`
**Realizes:** Design-SetHeader.md §14.2 (set-level notifications) and §14.3 (verified overwrite and delete)

*The code is the authority.* Every method, property and guard named below was read in the current sources:
`TapeAgent.cs`, `TapeBackupAgent.cs`, `TapeRestoreAgent.cs`, `TapeStreamManager.cs`, `TapeNavigator.cs`,
`TapeTOC.cs`, `TapeSetHeader.cs`, `TapeHeader.cs`, `TapeHeaderBlock.cs`, `ITapeFileNotifiable.cs`,
`ServiceOperationProgressHandler.cs`, `TestNotifiable.cs`. No open assumptions remain.

---

## 1. What the feature does

Set headers protect reads. This extends the same record to the two paths that **destroy** data, upgrades the
recovery into something worth sharing with the read path, and adds the channel through which a set-level
anomaly reaches the user instead of dying in the trace log.

Three user-visible capabilities:

- **A destructive write confirms its position before it destroys anything.** Overwriting a set and deleting
  the tail both read the set header standing at the target and compare it against what the TOC believes is
  there. No `Match`, no write.
- **A mis-shapen tail becomes recoverable rather than terminal, on every path.** When trailing marks are
  damaged — the classic aftermath of a backup that died mid-set — navigation counted from end-of-content
  lands on the wrong set. The agent believes the set header over its own arithmetic, moves the delta, and if
  that still does not settle, re-navigates counting forward from begin-of-content and verifies again. Restores
  get this as well as deletes.
- **Set-level anomalies surface.** `OnSetFailed` / `OnSetAnomalyRecovered` on `ITapeFileNotifiable` let the
  host name the sets involved, ask for a decision, and report every drift — corrected or not — in the closing
  summary.

---

## 2. Design position

### 2.1 Why the §8.3(c) prohibition can be lifted

The shipped rule reads *"read-side only — a write-side miscount means the agent is about to overwrite the
wrong set, where failing is correct and correcting is reckless."* That sentence conflates two different acts:
**moving the head** and **writing**. Correction only ever does the first. The sequence is read → reposition →
read → compare, and every step is non-destructive; the write happens strictly after a second, positive
verification.

So the prohibition is not a property of correction. It is a property of what follows it. The rule that
actually carries the safety is narrower and stronger:

> **SH-13: no destructive write proceeds without a `Match` verdict from a set header read at the write
> position during the same operation.**

Failing is still safe. It is simply no longer the *only* safe option — and it is the option that leaves the
user with no way back. A cartridge whose trailing marks are damaged cannot be repaired by any verb the
library exposes if every verb that would repair it refuses to move.

### 2.2 One recovery, not two flavours

The corollary runs the other way too. If the two-stage recovery of §5 is the best strategy available, there is
no principled reason to withhold it from the read path. A restore's failure costs time rather than data, but
that is an argument about *consequences*, not about whether the repair works. The mechanism is identical on
every agent; exactly one policy point differs, and it concerns `Unreadable` alone (§5.5).

### 2.3 The residual asymmetry, kept explicit

Reading the wrong set wastes time; writing the wrong set destroys data. That asymmetry survives — narrowed to
where it actually belongs:

| | Read path | Write path |
|---|---|---|
| Recovery ladder | **identical** (§5) | **identical** (§5) |
| Terminal `Unreadable` | warn, **proceed** | **block** |
| Terminal `SetIndexDrift` | fail the set | block |
| Terminal `WrongMedia` / `WrongVolume` | fail the set | block |
| `SetHeadersExpected == false` (legacy volume) | proceed silently | proceed, **warn once** |

The last row matters twice over: SH-1 makes set-header presence a per-volume declaration, so a legacy
cartridge genuinely carries nothing to verify. Blocking there would make the feature a regression for every
cartridge written before headers shipped. It proceeds — and says so, once per operation, at Warning.

---

## 3. Scope — which writes are verified, and what they cost

| Path | Navigator target | Verified? | Cost |
|---|---|---|---|
| `newSet: true`, `CurrentSetIndexOnVolume > 0` | `-1` (EOD) | **No — nothing to verify** | zero |
| `newSet: true`, first set on volume | `0` (begin-of-content) | **No** (§3.2) | zero |
| `newSet: false` — rewrite the current set | `CurrentSetAsNavigatorContentSet(...)` | **Yes** | one block read + one `MoveToBlock` |
| `DeleteSetsFromCurrentSetUp`, trailing branch | `CurrentSetAsNavigatorContentSet(...)` | **Yes** | one block read + one `MoveToBlock` |
| `DeleteSetsFromCurrentSetUp`, delete-all branch | `MoveToBeginOfContent()` | **Yes, positional only** (§4.4) | one block read |

This settles the cost question §14.3 left open. **The performance-critical path pays nothing.** A set
appended at end-of-data has no predecessor record standing where it will write — there is no read to make.
Verification only arms when the agent is about to land on top of something that already exists, which is
precisely the case where a miscount matters and where a single 16 KiB read is negligible against what is
about to be destroyed. No measurement on real LTO is needed before shipping, because the paths that would
have been measured are the paths that are not verified.

### 3.1 The ordering defect this exposed — `!newSet` on the first set of a volume

`BeginWriteContentForCurrentSet` opens with:

```csharp
if (TOC.CurrentSetIndex == TOC.FirstSetOnVolume && WritesMediaHeader)
    MediaHeaderStamped = WriteMediaHeader();
else
    EnsureMediaHeaderResolved();
```

The condition does **not** consult `newSet`. So overwriting the *first set on a volume* (`newSet: false`,
`CurrentSetIndexOnVolume == 0`) rewrites the media header at BOM before anything else happens — and
`TapeNavigatorTOCInSet.OnMediaHeaderWritten` correctly notes that a BOM write truncates everything beyond it.

For the shipped feature that is harmless: nothing was going to be verified. For *this* feature it is fatal —
the verification read would land at block 1 of a cartridge whose content the agent has already destroyed, and
would report `Unreadable` about a set that no longer exists.

> **The media-header write must move below the verification.** On the `!newSet` path, verification runs
> first (against the header that is still on tape), and only a `Match` authorizes the `WriteMediaHeader()`
> that follows. `EnsureMediaHeaderResolved()` is called up front on that path instead, which is what the
> verification needs anyway and which costs the same single block read the `else` branch already pays.

This is a prerequisite, not a refinement, and it is Step 2's first commit.

### 3.2 Why a fresh volume's first set is not verified

`newSet: true` with `CurrentSetIndexOnVolume == 0` targets begin-of-content on a volume the agent is heading
itself. The media header it writes declares the new series; any set header standing there belongs to the
cartridge being deliberately overwritten. Verification would compare the new identity against the old one and
report `WrongMedia` on every single overwrite — a false positive by construction. The load-time media check
(`TapeServiceBase.EvaluateLoadedHeader`) is the correct guard there, and it already exists.

---

## 4. The verdict ladder

### 4.1 Where it runs

Three call sites, all immediately before the first destructive act (the fourth, the read path, is unchanged
from shipped):

- **`TapeFileBackupAgent.BeginWriteContentForCurrentSet`** — after the hoisted
  `Navigator.MoveToTargetContentSet()`, before `WriteSetHeader()`. That positioning is already hoisted for
  SH-4, so the window exists and costs nothing to open. The hoist must now run whenever verification *or*
  writing is needed, not only when `WritesSetHeaders && MediaHeaderPresence == Present`.
- **`TapeFileAgent.DeleteSetsFromCurrentSetUp`, trailing branch** — after
  `Navigator.MoveToTargetContentSet()` and before `Navigator.MoveToNextContentSetmark(-1)`.
- **`TapeFileAgent.DeleteSetsFromCurrentSetUp`, delete-all branch** — after
  `Navigator.MoveToBeginOfContent()` and before `BackupInitialTOC(writeHeader: false)`.

All three sit in `TapeState.MediaPrepared` — which is what §8.1 opens.

### 4.2 Classification

`ClassifySetHeader` is reused verbatim. It is pure, already tested, reads only `TOC.MediaId`, `TOC.Volume` and
`TOC.CurrentSetIndexOnVolume`, and contains the ordering rule (identity before position) that a second copy
would eventually get wrong.

`NotExpected` is unreachable through it, exactly as on the read side — the *caller* gates on
`Navigator.SetHeadersExpected` and never reads at all. The warn-once of §2.3 lives at that gate.

What each verdict does is now a function of **stage** (§5) rather than of agent, with one exception at the
terminal (§5.5).

### 4.3 Why `Unreadable` blocks a destructive write at the terminal

The read-side golden rule — *a record that cannot be verified never blocks* — rests on a fact that does not
survive the crossing: **restore positions absolutely.** `RestoreNextFile` seeks the pipelined reader to the
file's exact `(block, offset)`, so an unverified set costs a safety net and nothing more. A destructive write
positions *relatively*, by counting marks, and an unreadable block at the presumed set start is exactly the
symptom a miscount produces when it lands somewhere that is not a set start at all. Proceeding there would
take the single strongest signal that the head is lost and treat it as permission.

That is a statement about the **terminal** action only. Before reaching it, `Unreadable` earns the same
re-navigation as a drift, on both paths (§5.4).

One mechanical consequence, inherited: `ReadSetHeaderBlock` calls `Navigator.ResetContentSet()` on a failed
read (SH-6), so after an `Unreadable` the navigator's position is already `UnknownSet` — which is precisely
the state the re-navigation of §5.3 wants.

### 4.4 The delete-all branch

`MoveToBeginOfContent()` lands at a deterministic block — 1 on headed media, 0 on legacy — so there is no
counting to get wrong and nothing to re-anchor. The read is kept anyway, as a **positional assertion**: the
block there must classify as a set header with `VolumeSetIndex == 0`, confirming the head cleared the media
header rather than standing on it. `Unreadable` blocks with a distinct diagnosis, because the one thing this
branch must never do is let `BackupInitialTOC` write a TOC over `TapeMediaHeader` (INV-4). No recovery stage
applies: the navigation was neither end-anchored nor delta-correctable.

Note the branch retains sets from earlier volumes (`TOC.CurrentSetIndex > 1`), so the expected on-volume index
is `TOC.CurrentSetIndexOnVolume`, which the ladder already reads. No special case.

---

## 5. Recovery — one ladder, two stages, every path

### 5.1 The two strategies are sequential, not alternative

An earlier draft selected between a relative delta and a begin-of-content re-anchor by inspecting the failed
navigation's anchor, and armed the second only on the write path. Both halves of that were wrong.

**They compose.** The two moves recover from different faults, and neither precludes the other:

- **The delta is the stronger move wherever it is available**, because it is *earned* rather than assumed. It
  runs only after a healthy set header has been read — a framed, CRC-checked record whose identity matches.
  On a cartridge whose mark structure no longer agrees with the TOC, that record is the single most
  trustworthy fact obtainable, and it states where the head physically is. Declining to use it because of how
  the caller happened to compute its target discards the best information on the tape.
- **The re-navigation addresses a different fault**: that the *counting direction* was wrong. Damage from a
  backup that died mid-set accumulates at the **tail**, so a backward count from EOD crosses the damaged
  region while a forward count from BOM traverses only the healthy part. It applies only when the failed
  navigation counted backwards — a forward count has no other direction to try.

So the ladder is: believe the header and move the delta; if that does not settle, and the navigation was
end-anchored, flip to forward-from-BOM and **run the whole verification again, delta included**. Only then
give up.

**And it belongs on both paths.** `CurrentSetAsNavigatorContentSet` picks the nearest of begin / current / end
and returns `-2 - toEnd` whenever the end is closer, so restores navigate backwards routinely and meet the
same tail damage. Withholding a working repair from them buys nothing.

### 5.2 The strategy

> **SH-14: set-navigation recovery proceeds in two stages — the relative delta (SH-10) whenever a healthy set
> header was read, then, if that does not settle and the navigation was end-anchored, one re-navigation
> counted forward from begin-of-content, itself re-verified and delta-corrected. Identical on every agent;
> only the terminal action differs (§5.5).**

The control flow, with the re-attempt hosted by `VerifySetHeaderForCurrentSet` rather than by
`CorrectSetNavigation` — the outer method owns navigation, the inner one owns the delta:

```
VerifySetHeaderForCurrentSet()                      // runs at most twice
  read → ClassifySetHeader → HandleSetHeaderVerdict
      Match                     → done
      SetIndexDrift             → CorrectSetNavigation()    // delta + one re-verify   [SH-10]
                                      settled? → done
      Unreadable                → no delta stage (no header to believe)
      WrongMedia / WrongVolume  → terminal, never re-attempt                           [§5.4]

  ─ unsettled ∧ end-anchored ∧ not already re-navigated:
        Navigator.ResetContentSet()                                                    [SH-19, §5.3]
        Navigator.TargetContentSet = CurrentSetAsNavigatorContentSet(fromBegin: true)
        Navigator.MoveToTargetContentSet()
        → re-enter VerifySetHeaderForCurrentSet() once
```

Termination is **structural**, not a counter: the second pass targets a non-negative index, so its
end-anchored precondition is false and it cannot re-attempt. A guard flag (`m_renavigatedFromBom`) makes
that explicit rather than inferred, and keeps the property true even if the anchor arithmetic later changes.

`CurrentSetAsNavigatorContentSet` becomes a **method** with a `bool fromBegin = false` parameter, which suits
that dense block of direction arithmetic better than a property in any case. `fromBegin: true` returns
`TOC.CurrentSetIndexOnVolume` directly, skipping the nearest-anchor optimisation — the same value
`DeleteSetsFromCurrentSetUp`'s `navigateFromBegin` branch assigns by hand today. That branch becomes a call to
the shared method, and its flag survives as the user-facing **forced** form of what recovery now does
automatically on evidence.

### 5.3 The reset is load-bearing — the navigator does **not** always reset itself

`ReconcileContentSetAndMove` fails in two distinguishable ways, and only one of them resets:

| Failure mode | Navigator state afterwards |
|---|---|
| The delta **move** failed | `MoveToContentSetByDeltaToReconcile` calls `ResetContentSet()` in all three failure branches → `UnknownSet` |
| The move succeeded, the **re-verify** still disagreed | `CurrentContentSet == targetContentSet` — asserted, never reset |

The second is the common case in the scenario that motivates this feature, and without an explicit reset it
silently disarms the re-attempt: `CurrentContentSet` now holds `TOC.CurrentSetIndexOnVolume`, which is exactly
what `fromBegin: true` computes — so `MoveToTargetContentSet`'s SH-4 idempotence check returns `true` **with
no transport move at all**. The same trap §8.4 of the set-header design documents for
`ReconcileContentSetAndMove`, in a new place.

`Navigator.ResetContentSet()` before re-targeting fixes it, and pays a second dividend: the
`TapeNavigatorTOCInSet` merged-filemark fast path requires `CurrentContentSet < 0`, which `UnknownSet`
satisfies. The re-attempt therefore becomes a rewind plus one merged forward space on the filemark layouts —
the cheapest correct form of "count from BOM", and the same primitive the normal path uses.

> **SH-19: the re-navigation resets `CurrentContentSet` before re-targeting. A believed position is never
> carried across a recovery stage.**

### 5.4 What is and is not re-attempted

`WrongMedia` and `WrongVolume` terminate immediately on every path, exactly as today. Re-navigating a
cartridge that is not the expected cartridge cannot help, and moving the head on a foreign tape is the last
thing anyone wants. The re-attempt gate therefore tests the **verdict**, not merely "unsettled".

`Unreadable` **does** re-attempt. It has no delta stage — there is no healthy header to believe — so the
re-navigation is its only recovery, and a block that fails to classify is precisely the symptom of having
counted into the damaged tail. It is also the cheapest possible confirmation that the fault was positional:
if a valid header appears after re-navigating, the medium was fine and the count was not.

### 5.5 Where the paths finally differ

After both stages are spent, one policy point remains, and it concerns `Unreadable` alone:

| Terminal verdict | Restore / validate / verify | Backup / delete |
|---|---|---|
| `Unreadable` | **proceed unverified**, warn (§4.3 — files position absolutely) | **block** |
| `SetIndexDrift` | **fail the set** | **block** |
| `WrongMedia` / `WrongVolume` | fail the set | block |

The drift row is worth stating explicitly, because "restore may still try to read the files" is tempting to
over-apply: it must not cover drift. A drift that survived both recovery stages means the head sits at a
known *wrong* set, and reading there delivers another set's bytes under this set's names — worse than the
failure it replaces, and a regression against the shipped `UncorrectableDrift_FailsCleanly`.

So exactly **one** virtual remains (§7), governing the terminal treatment of `Unreadable`.

### 5.6 Mechanical notes on the re-attempt

- **Do not re-enter `Manager.BeginReadContent()`.** On the read path the manager already sits in
  `ReadingContent`, and its re-entry branch calls `EndReadContentSet()` → `MoveToNextContentSetmark()`,
  advancing a set before re-targeting. The re-attempt calls `Navigator.MoveToTargetContentSet()` directly,
  which touches no manager state and is legal in both `ReadingContent` and `MediaPrepared`. SH-17 stays
  satisfied throughout: no packer of either kind exists at any of the four call sites.
- **Both miscount injection points stay live** on the re-attempt — `MoveToNextContentSetmark` and the
  `TapeNavigatorTOCInSet` fast path each call `TakeSimulatedMiscount()`. `MoveToBeginOfContentCore` does not,
  so a *persistent* simulated miscount genuinely defeats the re-attempt rather than being bypassed by it,
  which is what makes the bound test in §9 meaningful.
- **`m_correctingSetNavigation` is released by its `finally`** before the re-attempt begins, so the second
  pass gets a fresh delta attempt. That is intended, not an oversight of the bound: it is a new position
  reached by a different route, not a third try at the same one. SH-10's bound is per-position; SH-14's is
  per-operation.
- **The read path's existing `Unreadable` re-anchor stays.** `HandleSetHeaderVerdict` currently calls
  `MoveToTargetContentSet()` when `CurrentContentSet == UnknownSet` before proceeding unverified. That is now
  the *terminal* branch, reached only after the re-navigation of §5.2 has been spent or declined.

## 5.7 The inconvenient half of the same fault

Step 5 wired the recovery to the *polite* failure mode: the navigation completes, the head lands somewhere,
and the set header politely disagrees. That is the presentation we could construct in tests — and it is the
minority of what a genuinely damaged tape does.

The other half is blunter. A backward count that crosses a damaged tail does not always land on the wrong
set; often it runs out of tape and returns `ERROR_NO_DATA_DETECTED` or `ERROR_END_OF_MEDIA`. Nothing is
read, no header disagrees, no verdict is reached — and the operation fails with a transport error, from a
cartridge whose healthy front half is sitting there intact and reachable.

The two are the **same fault** reported through different channels:

| | Step 5 (the wrong one) | Step 5A (the failed one) |
|---|---|---|
| Symptom | header says another set | `ERROR_NO_DATA_DETECTED` / `ERROR_END_OF_MEDIA` |
| Detected by | `ClassifySetHeader` | `Navigator.WentBad` + error code |
| Cause | the tail's mark structure is *wrong* | the tail's mark structure is *short* |
| Cure | **renavigate from BOM** | **renavigate from BOM** |

Once the cure is identical, withholding it from the second case is arbitrary. Worse, on real damaged media
the second case is the likelier one: a set whose closing mark never reached tape removes a mark from the
count, so a backward count of N marks reaches past begin-of-content and hits BOM, or — on the filemark
layouts, whose `MoveToEndOfContentInternal` assumes a fixed mark distance from EOD — walks off the end.

### 5.7.1 The gate is the ERROR CODE, not `WentBad`

A drive that went offline also fails to move. So does a cartridge that was ejected mid-operation. Neither is
helped by a rewind, and both would be *made worse* by one — a pointless full-length transport pass before
the real error surfaces.

> **SH-20: a failed navigation is retried from begin-of-content only when it counted BACKWARD and failed with
> a POSITIONAL error — one that says "there is no more tape that way". Any other error terminates
> immediately.**

The positional set, and why each belongs:

| Error | Why it means "the tail is short" |
|---|---|
| `ERROR_NO_DATA_DETECTED` | ran past EOD looking for a mark that is not there |
| `ERROR_END_OF_MEDIA` | the same, physical rather than logical |
| `ERROR_BEGINNING_OF_MEDIA` | counted back past BOM — the count was too large for the marks present |
| `ERROR_FILEMARK_DETECTED` / `ERROR_SETMARK_DETECTED` | the mark structure is not what the count assumed |

Everything else — `ERROR_NOT_READY`, `ERROR_MEDIA_CHANGED`, `ERROR_BUS_RESET`, `ERROR_CRC`, any I/O failure —
terminates. A rewind cannot make a dead drive live.

### 5.7.2 One helper, three call sites

The whole feature is a wrapper around `MoveToTargetContentSet`:

```
NavigateToTargetContentSet()
  ├─ move → success                              → done
  ├─ move → failed, non-positional               → fail (untouched)
  ├─ move → failed, positional, begin-anchored   → fail (no other direction to try)
  └─ move → failed, positional, END-anchored     → reset, re-target fromBeginOnly, move again
                                                    ├─ success → report the recovery, done
                                                    └─ failed  → fail, with the SECOND error
```

Because it re-targets to a non-negative index, the retry is **structurally bounded** exactly as stage 2 is:
its own end-anchored precondition is false, so it cannot recurse.

And because the verification that follows now runs against the *renavigated* position, `m_renavigatedFromBom`
is already set when the ladder reports — so a set rescued this way is attributed to
`TapeSetAnomalyStage.Renavigated` and counted in `AnomaliesRecoveredFromBom` with no extra wiring. That is
the correct attribution: it indicts the tail, which is exactly what happened.

### 5.7.3 The restore path navigates itself

`BeginReadContentForCurrentSet` previously delegated its positioning to `Manager.BeginReadContent`, which
calls `MoveToLocationFor` → `Navigator.MoveToTargetContentSet` internally. A failure there surfaces as
"failed to transition to reading content", with the navigator's error buried two layers down.

Rather than plumbing that error outward, the restore path now does what the **backup path already does**:
navigates explicitly *before* handing control to the manager. The manager's own
`MoveToTargetContentSet` then finds `TargetContentSet == CurrentContentSet` and returns without touching the
transport (SH-4), so the change costs nothing and the recovery sits where it can see the error.

This also makes the three paths read alike, which is worth something on its own.

### 5.7.4 What this does NOT do

- **It does not retry a failed READ.** `ReadSetHeaderBlock` failing is `Unreadable`, and Step 5 already
  handles that verdict.
- **It does not apply to the delete-ALL branch.** `MoveToBeginOfContent` *is* the forward direction; a
  failure there means the front of the volume is damaged, which §11 already records as out of reach.
- **It does not lower the write path's guard.** The renavigated position is still verified, and still
  blocks on anything but `Match`. The recovery buys a second chance at *reaching* the set, never a
  concession about writing to it.

---

## 6. Surfacing — the set-level notification channel

### 6.1 The payload

Set-level events carry more than an integer. `TapeSetAnomaly` is a `readonly record struct` assembled at the
point of detection:

| Member | Role |
|---|---|
| `Verdict` | the `TapeSetHeaderVerdict` that produced the event |
| `SetIndex` | `TOC.CurrentSetIndex` — standard, 1-based, matching every existing `Report` line |
| `ExpectedVolumeSetIndex` / `ActualVolumeSetIndex` | `TOC.CurrentSetIndexOnVolume` vs. `header.VolumeSetIndex` |
| `ExpectedDescription` / `ActualDescription` | `TOC.CurrentSetTOC.Description` vs. `header.DisplayName` |
| `ExpectedVolume` / `ActualVolume` | populated on `WrongVolume`, else equal |
| `Stage` | `Delta` or `Renavigated` — which recovery stage produced or failed at this event |
| `IsDestructive` | whether a destructive write is gated on this verdict |
| `CanAttemptRecovery` | whether a stage remains untried for this verdict and anchor |
| `Diagnosis` | a `TapeResult` carrying code and message |

`ActualDescription` reads `TapeSetHeader.DisplayName`, documented as never empty — it synthesizes
`"Set #N · vol V · <date>"` when the header carries no description, so a prompt can always name both sides.
`Diagnosis` is a `TapeResult` for consistency with `OnFileFailed`, whose real signature is
`OnFileFailed(TapeFileInfo, TapeResult, in TapeFileStatistics)` — not an exception.

`Stage` exists because a drift corrected by the delta and one corrected only after re-navigating say different
things about the cartridge: the first indicts a mark, the second indicts the tail.

### 6.2 The interface additions

`ITapeFileNotifiable` currently declares six members and **no** default implementations; `TestNotifiable`,
`ServiceOperationProgressHandler` and both app adapters implement all six. Adding two members with default
implementations keeps every one of them compiling untouched:

```csharp
/// Raised when a set cannot be used as the TOC describes it. Return Abort to stop,
///  Proceed to authorize the remaining recovery stages (or, where none remain, the terminal action).
SetFailedAction OnSetFailed(in TapeSetAnomaly anomaly, in TapeFileStatistics stats)
    => SetFailedAction.Abort;

/// Raised after a drift has been corrected and re-verified. Informational.
void OnSetAnomalyRecovered(in TapeSetAnomaly anomaly, in TapeFileStatistics stats) { }

public enum SetFailedAction { Abort, Proceed }
```

Three decisions worth defending:

- **Default interface implementations**, for source compatibility across four implementers. The defaults are
  asymmetric on purpose: `OnSetAnomalyRecovered` does nothing, while `OnSetFailed` defaults to **`Abort`** —
  a notifiable that has not been taught about destructive recovery must not silently authorize it.
- **An enum, not `bool`.** It mirrors `FileFailedAction`, reads at the call site, and leaves room for a future
  `ProceedAlways` without touching the signature. A bare `bool` at a destructive decision point is the kind of
  parameter that gets inverted in a refactor.
- **Throwing still works.** `TapeAbortRequestedException` from either method is caught by the notification
  wrapper, which sets `IsAbortRequested` at the point of observation and converges on `ERROR_CANCELLED` — the
  discipline `NotifyPreProcessFile` / `NotifyFileFailed` already follow.

The wrappers go on `TapeFileAgent` beside the existing ones and follow `NotifyFileFailed`'s shape exactly:
catch `TapeAbortRequestedException` → return `Abort` (do not rethrow); catch everything else → log and return
the safe default; set `IsAbortRequested` when the result is `Abort`.

### 6.3 Where the callback fires — once per set, before the first stage

Not from `CorrectSetNavigation`, as originally proposed, but from `HandleSetHeaderVerdict`.

`CorrectSetNavigation` is one stage of one strategy, and three of the six verdicts never reach it. Asking
there would leave `WrongMedia`, `WrongVolume` and `Unreadable` — the verdicts that actually block a
destructive write — with no way to explain themselves, and would put the decision *after* the strategy had
been chosen. The ladder is the single point that sees every verdict, so it is the single point that asks.

**Once per set, not once per stage.** The first non-`Match` verdict raises it; `Proceed` authorizes the whole
remaining ladder, and a guard suppresses a second prompt on the re-navigated pass. A user asked twice about
one cartridge would reasonably conclude something was looping.

Order at the ladder: classify → build the anomaly → notify (first time only) → act. The answer gates whether
any recovery runs at all, which also means `CorrectsSetNavigation == false` and `OnSetFailed → Abort`
converge on one code path instead of two.

### 6.4 Latch-logging

Both channels described in §10 of the set-header design apply, and they are distinct:

- **A blocked set latches a failure.** `LatchFailure()` after `SetError(...)`, so the diagnosis survives later
  successes and reaches `ServiceOperationResult.Diagnosis` via `FailedOperationResult`. An abort chosen at
  `OnSetFailed` does **not** latch — it is a user decision, not a fault — and `FailedOperationResult` already
  supplies `ERROR_CANCELLED` when `IsAbortRequested` is set.
- **A recovered set latches nothing**, because nothing failed; it accumulates. The agent keeps
  `IReadOnlyList<TapeSetAnomaly> SetAnomalies`, cleared where `_stats.Reset()` and `ResetLatchedFailure()` are
  already called together at every public entry point. The progress handler folds it into
  `ServiceOperationResult`:

```csharp
public int SetAnomaliesRecovered { get; init; }   // drifts corrected and re-verified
public int SetAnomaliesRecoveredFromBom   { get; init; }   // recoveries that needed the second stage
public bool SetWriteBlocked      { get; init; }   // destructive write refused (happens once; then the operation aborts)
```

> **SH-16: a corrected drift is reported at Warning on both surfaces — the host `Report` channel and
> `m_logger` — and appears in the operation's closing summary, naming the stage that settled it.**

This is the operational point §13.2 already makes for the read side, now with a sharper signal: a set that
needed the re-navigation means the cartridge's *tail* is unreliable, which is exactly the fact that should
reach the person holding it.

---

## 7. Placement — lifting the region

The `#region *** Set header verification ***` moves from `TapeFileRestoreBaseAgent` to `TapeFileAgent`. Three
forcing reasons:

- `DeleteSetsFromCurrentSetUp` **already lives on `TapeFileAgent`**. Verification for delete cannot be reached
  from a subclass without either moving the method or duplicating the machinery.
- `ClassifySetHeader` is pure and reads only `TOC` state the base owns. Nothing in it is restore-specific.
- The recovery ladder is now shared by every agent (§2.2), so the base class is where it belongs by rights,
  not merely by convenience.

What moves, with the visibility it needs: `ReadSetHeader` (private → protected), `ClassifySetHeader`
(internal), `VerifySetHeaderForCurrentSet` (private → protected, now hosting the re-attempt),
`HandleSetHeaderVerdict`, `CorrectSetNavigation`, `m_correctingSetNavigation`, the new
`m_renavigatedFromBom`, and `CorrectsSetNavigation` (public, default `true`). `TapeSetHeaderVerdict` moves
from `TapeRestoreAgent.cs` to `TapeSetHeader.cs` — a file move at namespace scope, no API change.
`CurrentSetAsNavigatorContentSet` becomes `CurrentSetAsNavigatorContentSet(bool fromBegin = false)` (§5.2).

**Nothing becomes abstract, and only one thing stays virtual.** Earlier drafts carried two policy hooks; the
unified ladder needs one:

```csharp
// TapeFileAgent — the single policy point (§5.5)
protected virtual bool BlocksOnUnverifiableSet => false;   // read path: proceed unverified
```

`TapeFileBackupAgent` overrides it to `true`. `DeleteSetsFromCurrentSetUp` lives on the base but needs
write-side policy, so it carries an operation-scoped `m_verifyingDestructiveWrite` flag that the property
consults — set in a `try/finally` around the delete body. A flag rather than a subclass because the method is
shared by backup and restore agents alike and has no natural home in either.

`HandleSetHeaderVerdict` and `CorrectSetNavigation` stay `protected virtual` for cases the hook cannot express,
but the expectation is that they are never overridden. Making either abstract would force the ladder to be
written twice, and the ladder's whole value is that every failure mode's diagnosis and error code are defined
*exactly once*. Two copies would drift — a peculiar way to implement drift detection.

Two signature consequences, both additive and defaulted:

```csharp
public TapeResult DeleteSetsFromCurrentSetUp(
    bool navigateFromBegin = false,
    ITapeFileNotifiable? fileNotify = null);      // new, optional

public bool VerifiesSetHeader { get; set; } = true;   // on TapeFileAgent, beside WritesSetHeaders
```

Every existing caller compiles unchanged and behaves as today, minus the verification it never had.

---

## 8. Mechanics — confirmed against the sources

### 8.1 `ReadSetHeaderBlock` rejects `MediaPrepared` — and must not simply be relaxed

The current guard is explicit:

```csharp
if (State != TapeState.ReadingContent) { LastErrorWin32 = ERROR_INVALID_STATE; …; return -1; }
```

All three new call sites run in `MediaPrepared`. The guard exists to bound the SH-7 race window, so widening
it needs the same argument SH-7 itself makes.

That argument holds, and is in fact stronger in `MediaPrepared`. What the guard protects is *"no packer of
either kind is running a worker thread against the drive."* In `ReadingContent` that is contingent — the
pipelined reader is created lazily inside `BeginPackedFileRead`, so the manager backs the state check with an
explicit `m_readPacker is not null` test. In `MediaPrepared` it is **structural**: `EnsurePackerCreated()` runs
inside `BeginWriteContent()`, `EnsureReadPackerCreated()` inside `BeginPackedFileRead()`, and
`EndWriteContent` / `EndReadContent` dispose both before transitioning back. The precedent is already in the
file: `WriteSetHeaderBlock` guards on `State != MediaPrepared || m_packer is not null` for exactly this reason.

So the state test widens and the packer test completes:

```csharp
if (!State.IsOneOf(TapeState.ReadingContent, TapeState.MediaPrepared)) { … }
if (m_readPacker is not null || m_packer is not null) { … }   // SH-17
```

> **SH-17: `ReadSetHeaderBlock` fails hard if a packer of either kind exists, whatever the state.** A guard,
> not a `Debug.Assert` — the consequence of violating it is a data race against a worker thread, so it must
> fail identically in Debug and Release, and an assert would make the violation untestable.

### 8.2 `ByteCounter` — already handled, and the write side needs nothing

`ReadSetHeaderBlock` saves and restores `Drive.ByteCounter` around its read, with a comment that reasons about
exactly this case: *"the write side needs no such care — it runs in `MediaPrepared`, before
`BeginWriteContent` zeroes it."* Correct: `BeginReadWrite` sets `Drive.ByteCounter = 0` on every transition,
which for the write path happens after the verification read. No change.

### 8.3 Block size — confirmed, and the guarantee is stronger than needed

`TapeHeaderBlock.Read` captures `drive.BlockSize`, sets `Size` (16 KiB) for the `ReadDirect`, and restores the
previous value **in a `finally`** via `RestoreBlockSize`. The restore therefore survives a failed
`SetBlockSize`, a short read, and any exception — not merely the success path. `WriteFramed` follows the
identical shape, so both halves of the set-header pair leave the drive exactly as they found it. Nothing in
this design needs to add, wrap, or compensate for block-size handling at any call site.

The stake was real. By the time `BeginWriteContentForCurrentSet` performs its verification read,
`Drive.SetBlockSize(TOC.CurrentSetTOC.BlockSize)` has already run and the set's block size has been reconciled
to what the drive accepted. Had the read left the drive at 16 KiB, `EnsurePackerCreated()` would capture that a
few lines later — and the primer records precisely what follows: `BlocksWritten == 0`, no `FilesCommitted`
events, pending files never promoted. Step 1's test keeps its assertion as a **regression guard** on the
`finally`, not as verification of an open question.

One adjacent edge, noted rather than handled: `TapeHeaderBlock.IsSupportedBy(drive)` is false on a drive whose
`MaximumBlockSize` is below 16 KiB. Such a drive cannot carry set headers at all, so
`Navigator.SetHeadersExpected` is false there and verification never arms — the legacy path of §2.3 covers it
with no new code.

### 8.4 The verify→write gap

`ReadSetHeaderBlock` advances the head by one block. Neither displacement it creates is free-floating:

- **The read advance is undone explicitly.** `Drive.CurrentBlock` is captured before the read and restored with
  `Drive.MoveToBlock(...)` after it — the same capture `WriteSetHeaderBlock` already performs for its trace
  line. Without this, `WriteSetHeader()` would stamp the new header one block late and every file address in
  the set would be off by one block.
- **The setmark step-back is deterministic from a verified position.** `MoveToNextContentSetmark(-1)` is
  position-relative, so it would land correctly even from one block into the set; restoring the block first
  makes the two call sites identical rather than each correct for its own reason.

> **SH-15: every destructive write begins at the block captured *before* the verifying read, or at a position
> derived from it by a single transport step.**

The verification read does not disturb the early-warning arithmetic: `SetEarlyWarning` /
`NotifyNextContentWritePosition` run earlier in `BeginWriteContentForCurrentSet` and key off written bytes,
and `ReadSetHeaderBlock` restores `Drive.ByteCounter` (§8.2).

---

## 9. Implementation plan

Each step ends green. Steps 1–3 ship a usable feature on their own; 4–6 add the unified recovery and the
channel.

### Step 0 — Lift the region *(no behavior change)*

Move the verification region to `TapeFileAgent`; move `TapeSetHeaderVerdict` to `TapeSetHeader.cs`; convert
`CurrentSetAsNavigatorContentSet` to a method with `fromBegin` (all existing call sites pass nothing); make
`HandleSetHeaderVerdict` and `CorrectSetNavigation` `protected virtual`; add `BlocksOnUnverifiableSet` and
`m_verifyingDestructiveWrite` with read-side defaults.

*Tests:* the entire existing suite, unchanged and unmoved. `TapeSetHeaderCorrectionTests` is the regression
gate — if a single case there changes behaviour, the lift was not a lift. `BackupPath_DoesNotCorrect` must
still pass, since Step 0 adds no write-side verification.

### Step 1 — Open the window (§8.1)

Widen the `ReadSetHeaderBlock` state guard and add the `m_packer` half of SH-17. Block-size handling needs no
change (§8.3).

*Tests:* `TapeSetHeaderProbeTests` —

- `ReadSetHeaderBlock_FromMediaPrepared_Succeeds` — and leaves `Drive.BlockSize`, `Drive.ByteCounter` and
  (after the explicit `MoveToBlock`) `Drive.CurrentBlock` exactly as it found them. Assert the block size
  against a set block size deliberately *different* from `TapeHeaderBlock.Size`, or the test passes for the
  wrong reason.
- `ReadSetHeaderBlock_OnFailedRead_StillRestoresBlockSize` — the `finally` of §8.3, via `ContentReadFaults`.
  The property this design leans on is the one that only shows up on the failure path.
- `ReadSetHeaderBlock_WithWritePacker_FailsHard` — SH-17's new half, in Release as in Debug.

### Step 2 — Verify the overwrite path

First the ordering fix of §3.1 — media-header write moves below verification on the `!newSet` path — then
`VerifiesSetHeader` and the gated verification, armed only for `newSet: false`. Block on anything but `Match`;
no recovery yet, so the terminal actions land before the stages that soften them.

*Tests:* `TapeSetHeaderWriteVerificationTests`, all four drive profiles —

- `Overwrite_WithDrift_IsRefused_AndTapeIntact` — `SimulateSetMiscount` armed, the overwrite fails, and the
  pre-existing sets still restore **byte-for-byte**. Asserting the refusal alone would pass for a version that
  refused *after* clobbering the set header.
- `Overwrite_OfFirstSetOnVolume_DoesNotClobberBeforeVerifying` — §3.1 directly: read the media header back and
  confirm the surviving sets still restore. Without this, the defect returns the moment someone "simplifies"
  the gate.
- `Overwrite_Clean_Succeeds_AndCostsNoExtraPosition` — the happy path stays silent and the first file's
  address is still exactly one block past the set start, which is simultaneously the SH-4 and SH-15
  assertion: a stray reposition or an un-restored read advance breaks it.
- `Append_AtEod_PerformsNoVerification` — §3's performance claim, asserted rather than assumed.
- `Overwrite_OnLegacyVolume_ProceedsWithOneWarning` — `SetHeadersExpected == false` is not a block.
- `Overwrite_WrongVolume_IsRefused` — identity failures never attempt anything.

### Step 3 — Verify the delete path

The same gate in both branches of `DeleteSetsFromCurrentSetUp`, including the positional assertion of §4.4,
and `navigateFromBegin` rewired onto `CurrentSetAsNavigatorContentSet(fromBegin: true)`.

*Tests:* `TapeSetDeleteVerificationTests` —

- `TrailingDelete_WithDrift_IsRefused_AndTapeIntact` — and the *retained* sets still restore.
- `DeleteAll_VerifiesSetZero_AndPreservesMediaHeader` — read the media header back afterwards; INV-4
  regressions are silent otherwise.
- `DeleteAll_OnUnreadableSetZero_IsRefused` — the branch that must never write a TOC over the media header.

### Step 4 — The notification channel

`TapeSetAnomaly` (with `Stage`), `SetAnomalyAction`, the two interface members with default implementations,
the `NotifySetAnomaly` / `NotifySetAnomalyRecovered` wrappers on `TapeFileAgent`, and the `SetAnomalies`
accumulator. `TestNotifiable` gains `SetAnomalyEvent` / `SetAnomalyRecoveredEvent` records, the matching lists,
a `SetAnomalyAction SetAnomalyAction { get; set; }` knob and a `SetAnomalyActionFunc` override — mirroring
`FailedAction` / `FailedActionFunc` exactly — plus `Clear()` coverage for the two new lists.

*Tests:* `TapeSetNotificationTests` —

- `BlockedSet_RaisesOnSetAnomaly_WithBothDescriptions` — the payload names both sets; a prompt that cannot name
  them is not a prompt.
- `OnSetAnomaly_ReturningAbort_YieldsErrorCancelled` — a user decision is not a fault, and does not latch.
- `OnSetAnomaly_Throwing_ConvergesWithTheEnum` — the third abort channel: operation fails, `IsAbortRequested`
  recorded, diagnosis non-empty. Same family as the existing three-channel convergence check in
  `ErrorHandlingTests`.
- `NotifiableWithoutOverrides_DefaultsToAbort` — the DIM contract, via a minimal implementer that overrides
  nothing new, *not* `TestNotifiable`.
- `CleanOperation_RaisesNothing` — provable silence on the happy path.

### Step 5 — The unified two-stage recovery

Host the re-attempt in `VerifySetHeaderForCurrentSet` per §5.2, with `Navigator.ResetContentSet()` (SH-19),
the `m_renavigatedFromBom` guard, `Unreadable` admitted to the re-navigation, and the terminal split of
§5.5. This step changes **read-path behaviour** as well, which is the point.

*Tests:* `TapeSetNavigationRecoveryTests`, four profiles — the crown suite of this feature:

- `FailedBackupTail_DeleteRecovers_AndTocIsStored` — the originating scenario end to end. Back up several
  sets, begin one more and abort it mid-set via `TestNotifiable.AbortAfterNPreProcessed` so the set header
  reaches tape and the closing setmark does not, then delete the tail. Assert three things: the *right* sets
  survive and restore byte-for-byte, the TOC is written, and `OnSetAnomalyRecovered` fired with
  `Stage == Renavigated`. Asserting only the return value would pass for a version that deleted one set too
  many.
- `RestoreOnDamagedTail_AlsoRecovers` — §2.2's claim, and the reason this is not a write-only feature: the
  same cartridge restores correctly where it previously failed.
- `ReNavigation_ActuallyMovesTheHead` — SH-19 directly. Drive the delta stage to a *successful move with a
  failing re-verify* (the case that does not self-reset), then assert the re-attempt performed real transport
  rather than tripping the SH-4 idempotence check. Without this assertion the bug of §5.3 is invisible: every
  other test would still pass by landing on the right set for the wrong reason.
- `ReNavigation_IsBoundedStructurally` — `SimulateSetMiscountPersistent` defeats both stages; the operation
  fails cleanly, no recursion, and the second pass raises no second prompt (§6.3). Both injection points are
  live on the re-attempt (§5.6), so this exercises the guard rather than an accident of layout.
- `ForwardNavigation_DoesNotReNavigate` — the structural precondition: a begin-anchored failure gets the
  delta stage and then terminates, with no wasted rewind.
- `Unreadable_ReNavigates_ThenSplitsByPath` — `ContentReadFaults.CorruptOnce` on the first read; the restore
  proceeds and the backup blocks, both after the same re-navigation.
- `Recovery_Declined_BlocksWithoutMoving` — `OnSetFailed → Abort` leaves the head where it was.
- `WrongVolume_NeverReNavigates` — §5.4: identity verdicts skip every stage.

### Step 5A: S. §5.7

Tests — additions to `TapeSetNavigationRecoveryTests`

| Test | Proves |
|---|---|
| `FailedBackwardNavigation_RecoversFromBom` | the feature: an over-long backward count fails positionally, the retry succeeds, the set restores |
| `FailedNavigation_ReportsRenavigatedStage` | attribution — `AnomaliesRecoveredFromBom == 1`, `Stage == Renavigated`, and the operation still reports success |
| `NonPositionalNavigationFailure_DoesNotRetry` | the SH-20 gate: a simulated `ERROR_NOT_READY` terminates with its OWN error, no rewind, no anomaly |
| `FailedForwardNavigation_DoesNotRetry` | the structural precondition — a begin-anchored failure has no other direction |
| `FailedNavigation_BothDirectionsFail_ReportsSecondError` | the terminal: the retry's error is what surfaces, since it describes where we ended |
| `DeleteOnUnreachableTail_RecoversAndDeletes` | the write path gets it too, and the retained sets survive byte-for-byte |


### Step 6 — Service and host surfacing

`ServiceOperationResult.SetAnomaliesRecovered` / `SetsRenavigated` / `SetsBlocked`;
`ServiceOperationProgressHandler` overrides both new members and routes them through `_host.Report` and
`ITapeServiceHost`; `JudgeFileOperation` / `VerbalizeFileOperation` gain the blocked-set verdict. That last one
is the point of the step: a refused destructive write processes no files, so without it the operation reports
*"completed — no files processed"* — the exact defect §10.1 of the set-header design was written to kill.

*Tests:* extend `ServiceBaselineTests` — a blocked delete reports a real message and a non-zero error code; a
recovered delete reports success **and** a warning naming the stage; the exhaustive `AssertMediaPrompts`
teardown still proves the happy paths silent.

### Step 7 — Documentation and hardware

Fold §14.2 and §14.3 of `Design-SetHeader.md` into a reference to this document, and amend its §8.3(c) —
the read-side-only rule — to the narrower SH-13. Update the primer's *What's Been Implemented*. Hardware
validation on AIT-2 and one LTO generation, combined with the set-header hardware item already pending — no
new conformance probe is needed, since every write here is post-mark or at-EOD and is already isolated by the
existing **S11** probe.

---

## 10. New invariants

| | |
|---|---|
| **SH-13** | No destructive write proceeds without a `Match` verdict from a set header read at the write position during the same operation. |
| **SH-14** | Set-navigation recovery runs two stages — relative delta, then one re-navigation counted forward from begin-of-content when the failed navigation was end-anchored — re-verified and delta-corrected on the second pass. Identical on every agent. |
| **SH-15** | Every destructive write begins at the block captured before the verifying read, or at a position derived from it by a single transport step. |
| **SH-16** | A corrected drift is reported at Warning on both the host channel and the logger, and appears in the operation's closing summary, naming the stage that settled it. |
| **SH-17** | `ReadSetHeaderBlock` fails hard if a packer of either kind exists, whatever the manager state — completing the read-packer guard SH-7 introduced. |
| **SH-18** | `OnSetFailed` defaults to `Abort`, is raised at most once per set, and its `Proceed` authorizes every remaining recovery stage. |
| **SH-19** | The re-navigation resets `CurrentContentSet` before re-targeting. A believed position is never carried across a recovery stage. |
| **SH-20** | A navigation that FAILS with a positional error (`ERROR_NO_DATA_DETECTED`, `ERROR_END_OF_MEDIA`, `ERROR_BEGINNING_OF_MEDIA`, `ERROR_FILEMARK_DETECTED`, `ERROR_SETMARK_DETECTED`) after counting BACKWARD is retried once, counted forward from begin-of-content. Any other error terminates immediately: a rewind cannot revive a dead drive. The retry is reported as `TapeSetAnomalyStage.Renavigated`. |

---

## 11. Known boundaries

- **A header-less volume stays unprotected.** SH-1 makes presence a per-volume declaration, and legacy
  cartridges declare nothing. The load-time media check remains their sole guard, as today.
- **A fresh volume's first set is deliberately unverified** (§3.2). The overwrite that mints a new `MediaId`
  cannot be checked against the identity it is replacing.
- **Damage at the head of the volume is not recoverable by either stage.** Both stages assume the *beginning*
  of content is sound — the delta because it moves relative to a position reached through it, the
  re-navigation because it counts forward from there. A cartridge damaged near BOM has no direction left to
  try, and blocks.
- **Recovery cannot repair a set whose header was never written.** A backup that failed before
  `WriteSetHeader()` leaves no record at the set start, so the verdict stays `Unreadable` through both stages.
  The manual escape remains `DeleteSetsFromCurrentSetUp(navigateFromBegin: true)` with
  `VerifiesSetHeader = false` — the right shape for a deliberate, informed override.
- **The file level stays unverified.** Within a correctly identified set, a file address resolving to the
  wrong block is still caught only by CRC. That is §14.4's territory.
