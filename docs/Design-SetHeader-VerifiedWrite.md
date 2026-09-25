# Design — Verified Destructive Navigation for TapeLibNET

**Status:** v4 · **implemented and shipped**
**Scope:** set-header verification on the destructive paths (overwrite, delete), a two-stage navigation
recovery shared by every agent, recovery of failed navigations, and the set-level notification channel
**Depends on:** the set header (`docs/Design-SetHeader.md`, v3) — record, presence model, verdict ladder,
relative correction; the media header (`docs/Design-TapeHeader.md`, v12) — grammar, framing, `TapeHeaderBlock`
**Realizes:** Design-SetHeader.md §14.2 (set-level notifications) and §14.3 (verified overwrite and delete),
and supersedes its §8.3(c) and §13.3

**The code is the authority.** This document explains the shape and the reasoning; every method named here
exists, and where the two disagree the code is right. Principal files: `TapeAgent.cs`, `TapeBackupAgent.cs`,
`TapeRestoreAgent.cs`, `TapeStreamManager.cs`, `TapeNavigator*.cs`, `ITapeFileNotifiable.cs`,
`ServiceOperationProgressHandler.cs`, `ServiceOperationRequest.cs`, `ServiceOperationResult.cs`,
`TapeServiceBase.Backup.cs`, `TapeServiceBase.Restore.cs`, `TapeServiceBase.DeleteSets.cs`,
`TapeServiceBase.Outcome.cs`, `ITapeServiceHost.cs`.

---

## 1. What the feature does

Set headers protected reads. This extends the same record to the two paths that **destroy** data, upgrades
the recovery into something worth sharing with the read path, and adds the channel through which a
set-level anomaly reaches the user instead of dying in a trace log.

Four user-visible capabilities:

- **A destructive write confirms its position before destroying anything.** Overwriting a set and deleting
  the tail both read the set header standing at the target and compare it against what the TOC describes.
  No `Match`, no write.
- **A mis-shapen tail is recoverable rather than terminal, on every path.** When trailing marks are damaged
  — the aftermath of a backup that died mid-set — a count from end-of-content lands on the wrong set, or
  fails outright. The agent believes the set header over its own arithmetic, moves the delta, and if that
  does not settle, re-navigates forward from begin-of-content and verifies again. Restores benefit as much
  as deletes.
- **A cartridge whose tail no longer matches its TOC can be repaired from the UI.** Deleting the trailing
  sets rewrites the damaged mark structure — and the delete itself is what the recovery makes reachable.
- **Set-level anomalies surface.** `ITapeFileNotifiable.OnSetAnomaly` / `OnSetAnomalyRecovered` let the host
  name the sets involved, ask for a decision, and report every drift — corrected or not — in the closing
  summary, with an actionable recommendation.

---

## 2. Design principles

| | |
|---|---|
| **Verified, not merely counted** | A destructive write proceeds only on a positive verdict from a record read at the write position during the same operation (SH-13). |
| **Recover, then refuse** | Refusing is safe but leaves the user no way back. The library attempts the repair it can prove, and refuses only when every stage is spent. |
| **One ladder, every agent** | A restore's failure costs time and a delete's costs data, but that concerns *consequences*, not whether the repair works. Exactly one policy point differs (§5.5). |
| **The header outranks the arithmetic** | A framed, CRC-checked record whose identity matches is the most trustworthy fact obtainable on a drifting cartridge. Navigation counts; the header knows. |
| **Ask before moving a destructive head** | Every recovery that repositions before a destructive write is authorized through the host, once per set. |
| **Diagnose the medium, not just the operation** | A corrected drift indicts a mark; a BOM recovery indicts the tail. The summary says which, and what to do. |

---

## 3. Scope — which writes are verified, and what they cost

Verification is gated by `TapeFileAgent.VerifiesSetHeader` (default `true`) and by
`Navigator.SetHeadersExpected`.

| Path | Navigator target | Verified? | Cost |
|---|---|---|---|
| `newSet: true`, `CurrentSetIndexOnVolume > 0` | `-1` (EOD) | No — nothing stands there | zero |
| `newSet: true`, first set on volume | `0` (begin-of-content) | No (§3.2) | zero |
| `newSet: false` — rewrite the current set | `CurrentSetAsNavigatorContentSet()` | **Yes** | one block read + one `MoveToBlock` |
| `DeleteSetsFromCurrentSetUp`, trailing branch | `CurrentSetAsNavigatorContentSet()` | **Yes** | one block read + one `MoveToBlock` |
| `DeleteSetsFromCurrentSetUp`, delete-all branch | `MoveToBeginOfContent()` | **Yes, positional only** (§4.4) | one block read |

This settles the cost question Design-SetHeader.md §14.3 left open: **the performance-critical path pays
nothing.** A set appended at end-of-data has no predecessor record where it will write, so there is nothing
to read. Verification arms only when the agent is about to land on top of something that already exists —
precisely where a miscount matters, and where one 16 KiB read is negligible against what is about to be
destroyed. No LTO measurement was required, because the paths that would have been measured are the paths
that are not verified.

### 3.1 The ordering defect this exposed

`BeginWriteContentForCurrentSet` wrote the media header on the condition
`TOC.CurrentSetIndex == TOC.FirstSetOnVolume && WritesMediaHeader`, without consulting `newSet`. Overwriting
the *first set on a volume* therefore rewrote the header at BOM before anything else — and
`TapeNavigatorTOCInSet.OnMediaHeaderWritten` records that a BOM write truncates everything beyond it.

Harmless while nothing was verified; fatal here, since the verification read would inspect block 1 of a
cartridge the agent had already destroyed. The write is now **deferred below the verification**
(`deferMediaHeaderWrite`), with `EnsureMediaHeaderResolved()` called up front instead — which is what the
verification needs anyway, at the same one-block cost. Only a `Match` authorizes the deferred
`WriteMediaHeader()`.

### 3.2 Why a fresh volume's first set is not verified

`newSet: true` with `CurrentSetIndexOnVolume == 0` targets begin-of-content on a volume the agent is heading
itself, minting a new `MediaId`. Any set header standing there belongs to the cartridge being deliberately
overwritten, so verification would report `WrongMedia` on every such overwrite — a false positive by
construction. `TapeServiceBase.EvaluateLoadedHeader` is the correct guard there and already exists.

---

## 4. The verdict ladder

### 4.1 Where it runs

`VerifySetHeaderForCurrentSet(fileNotify)` is called from four sites, three of them new:

- `TapeFileBackupAgent.BeginWriteContentForCurrentSet` — after the hoisted navigation, before
  `WriteSetHeader()`. The hoist now runs whenever verification *or* header writing *or* the deferred
  media-header write is needed.
- `TapeFileAgent.DeleteSetsFromCurrentSetUp`, both branches — via `VerifyBeforeDestructiveWrite`.
- `TapeFileRestoreBaseAgent.BeginReadContentForCurrentSet` — the shipped read-side site, now reached after
  the agent navigates explicitly (§5.7.3).

The three write-side sites run in `TapeState.MediaPrepared`, which §8.1 opened.

### 4.2 Classification and outcome

`ClassifySetHeader` is reused unchanged — pure, already tested, and the single place the ordering rule
(identity before position) lives.

The ladder itself, `HandleSetHeaderVerdict`, returns `SetVerdictOutcome` rather than `bool`. A plain boolean
could not carry the distinction the recovery needs: *"unusable, stop"* and *"not settled **yet**, a stage
remains"* are both failures to the caller but opposite instructions to the recovery — and the read path's
*proceed-unverified* is a third thing again, which must not fire while a stage is still untried.

| Outcome | Meaning |
|---|---|
| `Proceed` | The set may be used — verified, or deliberately proceeding unverified. |
| `Recoverable` | Not settled; a stage remains. The error is not yet final. |
| `Terminal` | Unusable, and no stage can help. |

The `recoveryAvailable` parameter defers the terminal action. Each arm logs, sets the error, and raises the
anomaly in that order — enforced by `RaiseSetAnomaly`, so no arm sets an error by hand and the payload's
`Diagnosis` always carries a real code and message.

### 4.3 Why `Unreadable` blocks a destructive write at the terminal

The read-side golden rule — *a record that cannot be verified never blocks* — rests on a fact that does not
survive the crossing: **restore positions absolutely.** `RestoreNextFile` seeks to the file's exact
`(block, offset)`, so an unverified set costs a safety net and nothing more. A destructive write positions
*relatively*, by counting marks, and an unclassifiable block at the presumed set start is exactly the
symptom a miscount produces. Proceeding there would take the strongest available signal that the head is
lost and treat it as permission.

That governs the **terminal** action only. Before reaching it, `Unreadable` earns the same re-navigation as
a drift, on both paths (§5.4).

### 4.4 The delete-all branch

`MoveToBeginOfContent()` lands at a deterministic block — 1 on headed media, 0 on legacy — so there is
nothing to miscount and nothing to re-anchor. The read is kept as a **positional assertion**: the block
there must classify as this volume's set header. The branch then sets `Navigator.TargetContentSet = 0`
explicitly, so the verification that follows offers no renavigation for a navigation that had no direction
to get wrong. The one thing this branch must never do is let `BackupInitialTOC` write a TOC over
`TapeMediaHeader` (INV-4).

---

## 5. Recovery — one ladder, two stages, every path

### 5.1 The two strategies compose

Design-SetHeader.md §8.3(a) argued that re-navigating from an absolute anchor reproduces the original
miscount. That is true **for the anchor the read path uses** — counting forward from begin-of-content again
counts the same marks the same wrong way. It is not true for a different anchor, and the two moves address
different faults:

- **The relative delta** is the stronger move wherever available, because it is *earned*: it runs only after
  a healthy set header has been read, and that record states where the head physically is.
- **The re-navigation** addresses a wrong counting *direction*. Damage accumulates at the **tail**, so a
  backward count from EOD crosses the damaged region while a forward count from BOM traverses only the
  healthy part. It applies only when the failed navigation counted backwards.

So the ladder is sequential: believe the header and move the delta; if that does not settle, and the
navigation was end-anchored, flip to forward-from-BOM and run the whole verification again, delta included.

### 5.2 The strategy

> **SH-14: set-navigation recovery proceeds in two stages — the relative delta (SH-10) whenever a healthy
> set header was read, then, if that does not settle and the navigation was end-anchored, one re-navigation
> counted forward from begin-of-content, itself re-verified and delta-corrected. Identical on every agent;
> only the terminal action differs (§5.5).**

`VerifySetHeaderForCurrentSet` hosts the re-attempt; `CorrectSetNavigation` owns the delta. The outer method
owns navigation, the inner one owns the correction.

Two mechanics are load-bearing:

**The anchor is captured before anything moves.** `ReconcileContentSetAndMove` assigns a non-negative
`TargetContentSet` as part of correcting, so reading the anchor afterwards would report "begin-anchored" for
every navigation that had reached stage 1 — silently disabling stage 2 in exactly the scenario it exists
for.

**Termination is structural.** The second pass targets a non-negative index, so its own end-anchored
precondition is false and it cannot recurse. `m_renavigatedFromBom` states that explicitly rather than
leaving it inferred, and doubles as the stage attribution for anything the second pass reports.

`CurrentSetAsNavigatorContentSet` gained a `bool fromBeginOnly = false` parameter — better suited to that
block of direction arithmetic than a property in any case. `DeleteSetsFromCurrentSetUp`'s `navigateFromBegin`
now routes through it, and survives as the user-facing **forced** form of what recovery does automatically
on evidence.

### 5.3 The reset is load-bearing — the navigator does not always reset itself

`ReconcileContentSetAndMove` fails two ways, and only one resets:

| Failure mode | Navigator afterwards |
|---|---|
| The delta **move** failed | `MoveToContentSetByDeltaToReconcile` calls `ResetContentSet()` → `UnknownSet` |
| The move succeeded, the **re-verify** disagreed | `CurrentContentSet == TargetContentSet` — asserted, never reset |

The second is the common case here, and without an explicit reset it silently disarms the re-attempt:
`CurrentContentSet` then equals what `fromBeginOnly: true` computes, `MoveToTargetContentSet` trips its SH-4
idempotence check, and the stage becomes a no-op that re-reads the same wrong block.

`Navigator.ResetContentSet()` before re-targeting fixes it and pays a dividend: the `TapeNavigatorTOCInSet`
merged-filemark fast path requires `CurrentContentSet < 0`, so the re-attempt becomes a rewind plus one
merged forward space on the filemark layouts.

> **SH-19: the re-navigation resets `CurrentContentSet` before re-targeting. A believed position is never
> carried across a recovery stage.**

### 5.4 What is and is not re-attempted

`WrongMedia` and `WrongVolume` terminate immediately on every path: re-navigating a cartridge that is not
the expected cartridge cannot help, and moving the head on a foreign tape is the last thing anyone wants.
The gate tests the **verdict**, not merely "unsettled".

`Unreadable` **does** re-attempt. It has no delta stage — no healthy header to believe — so the
re-navigation is its only recovery, and a block that fails to classify is precisely the symptom of having
counted into a damaged tail. It is also the cheapest confirmation that the fault was positional: if a valid
header appears after re-navigating, the medium was fine and the count was not.

### 5.5 Where the paths finally differ

| Terminal verdict | Restore / validate / verify | Backup / delete |
|---|---|---|
| `Unreadable` | **proceed unverified**, warn (§4.3) | **block** |
| `SetIndexDrift` | fail the set | block |
| `WrongMedia` / `WrongVolume` | fail the set | block |

The drift row is explicit because "restore may still read the files" is tempting to over-apply: a drift that
survived both stages means the head sits at a known *wrong* set, and reading there delivers another set's
bytes under this set's names.

Exactly one virtual remains: `BlocksOnUnverifiableSet`, governing `Unreadable` alone.
`TapeFileBackupAgent` overrides it to `true`; the base returns `m_verifyingDestructiveWrite`, an
operation-scoped flag set in a `try/finally` around the delete body. A flag rather than a subclass because
`DeleteSetsFromCurrentSetUp` lives on `TapeFileAgent` and is inherited by backup and restore agents alike.

### 5.6 Mechanical notes

- The re-attempt calls `Navigator.MoveToTargetContentSet()` directly and never re-enters
  `Manager.BeginReadContent()`, whose re-entry branch advances a set before re-targeting.
- Both miscount injection points stay live on the re-attempt, so a persistent simulated miscount genuinely
  defeats it rather than being bypassed — which is what makes the bound test meaningful.
- `m_correctingSetNavigation` is released by its `finally` before the re-attempt, so the second pass gets a
  fresh delta attempt. SH-10's bound is per-position; SH-14's is per-operation.

### 5.7 Recovering a FAILED navigation, not merely a wrong one

Stage 2 as described handles the *polite* failure: the navigation completes, the head lands somewhere, and
the header disagrees. On genuinely damaged media the blunter presentation is likelier — the backward count
runs out of marks and fails outright, from a cartridge whose healthy front half is sitting there reachable.

Both are the same fault through different channels: the tail's mark structure is *wrong* versus *short*.
The cure is identical, so withholding it from the second case would have been arbitrary.

**`TapeFileAgent.NavigateToTargetContentSet(fileNotify)`** is the shared wrapper around
`MoveToTargetContentSet` that closes the gap, used by all three destructive/content call sites. It handles
three cases:

- **Move failed, positionally, end-anchored** → prompt, reset, re-target `fromBeginOnly`, move again.
- **Move "succeeded" at BOM** → the navigator reports `CurrentContentSet == 0` because `OnMovedIntoBom`
  settled it there (§5.7.2). If the TOC confirms the target *is* the volume's first set,
  `Navigator.AssumeAtTargetContentSet()` restores the end-anchored index; otherwise the head is
  demonstrably at the wrong set and the same recovery runs — starting from the proven position, so the
  forward count needs no rewind.
- **Anything else** → fail, untouched.

#### 5.7.1 The gate is the error code

> **SH-20: a failed navigation is retried from begin-of-content only when it counted BACKWARD and failed
> with a POSITIONAL error. Any other error terminates immediately.**

`IsPositionalNavigationError` names the set: `ERROR_NO_DATA_DETECTED`, `ERROR_END_OF_MEDIA`,
`ERROR_BEGINNING_OF_MEDIA`, `ERROR_FILEMARK_DETECTED`, `ERROR_SETMARK_DETECTED`. Everything else — a drive
that went offline, an ejected cartridge, a bus reset — terminates. Testing `WentBad` alone would send a dead
drive on a full-length rewind before surfacing the real error.

#### 5.7.2 The navigator reports what it can prove

Reaching the oldest set by counting **backward** is a fencepost: N setmarks delimit N+1 boundaries, so the
oldest set's leading boundary is BOM itself, and reaching it from the end always "overshoots". An overshoot
of five marks lands in the same place and is indistinguishable from inside the navigator — telling them
apart needs the total set count, which `TapeNavigator` deliberately never knows.

`OnMovedIntoBom` therefore settles at begin-of-content and reports `CurrentContentSet = 0` — the one index
it can **prove** — rather than asserting it reached the target. The agent, which does hold the accounting,
resolves the ambiguity (§5.7).

This replaced a silent `CurrentContentSet = TargetContentSet`, which was a claim the navigator could not
support and, on headed media with `HasSetHeaders == false`, could have let a trailing delete truncate a
volume.

#### 5.7.3 The restore path navigates itself

`BeginReadContentForCurrentSet` previously delegated positioning to `Manager.BeginReadContent`, burying any
navigation error two layers down. It now navigates explicitly first — what the backup path always did — and
the manager's own `MoveToTargetContentSet` then finds target == current and returns without touching the
transport (SH-4). The change costs nothing and puts the recovery where it can see the error.

#### 5.7.4 What this does not do

It does not retry a failed *read* (that is `Unreadable`, handled by the ladder); it does not apply to the
delete-all branch, where `MoveToBeginOfContent` *is* the forward direction; and it does not lower the write
path's guard — the renavigated position is still verified and still blocks on anything but `Match`.

---

## 6. Surfacing — the set-level notification channel

### 6.1 The payload

`TapeSetAnomaly` (in `ITapeFileNotifiable.cs`) is a `readonly record struct` assembled by
`TapeFileAgent.BuildSetAnomaly` at the point of detection. Beyond the verdict and the indices it carries
`ExpectedDescription` / `ActualDescription` — a prompt must name *which* set, which is the same reason
`Description` was admitted to the header record at all — plus:

| Member | Role |
|---|---|
| `Stage` | `Detected`, `Delta`, or `Renavigated` — which pass observed or settled this |
| `IsDestructive` | whether a destructive write is gated on this verdict |
| `CanAttemptRecovery` | whether a stage remains untried |
| `Diagnosis` | a `TapeResult`, for consistency with `OnFileFailed` |

`Diagnosis` defaults to the agent's current error — correct for a detection, where `RaiseSetAnomaly` has
just set it. A **recovery** passes `TapeResult.OK` explicitly: that payload reports the set's state *now*,
and the fault it repaired is already recorded in the detection entry. Leaving it to the default there would
produce a failure with no code and no message, because `Navigator.ReconcileContentSetAndMove` calls
`ResetError()` on its way through.

### 6.2 The interface additions

`ITapeFileNotifiable` gained `OnSetAnomaly` and `OnSetAnomalyRecovered` with **default implementations**, so
all four existing implementers compiled untouched. The defaults are asymmetric on purpose:
`OnSetAnomalyRecovered` does nothing, while `OnSetAnomaly` defaults to `SetAnomalyAction.Abort`.

> **SH-18: `OnSetAnomaly` defaults to `Abort`, is raised at most once per set, and its `Proceed` authorizes
> every remaining recovery stage.**

`SetAnomalyAction` is an enum rather than a `bool` — it mirrors `FileFailedAction`, reads at the call site,
and leaves room for a future `ProceedAlways`. A bare `bool` at a destructive decision point is the kind of
parameter that gets inverted in a refactor.

The wrappers `NotifySetAnomaly` / `NotifySetAnomalyRecovered` follow `NotifyFileFailed`'s discipline exactly:
`TapeAbortRequestedException` is caught and converted rather than rethrown, so the enum and the exception
converge on one code path.

### 6.3 Where the callback fires

From `HandleSetHeaderVerdict`, not from `CorrectSetNavigation`. The correction is one stage of one strategy,
and three of the six verdicts never reach it — asking there would leave `WrongMedia`, `WrongVolume` and
`Unreadable` unable to explain themselves, and would put the decision *after* the strategy was chosen.

`m_setAnomalyRaisedForSet` keys the once-per-set guard on the **set index** rather than a bool, so the
re-navigation — which re-enters the ladder for the same set — cannot re-arm it.

### 6.4 Accounting

`TapeSetStatistics` is nested inside `TapeFileStatistics`, so every existing callback already receives it:
legacy implementations ignore the field, and a host deciding whether to abort can weigh the third anomaly
differently from the first. It holds counters only — `SetsProcessed`, `SetsSucceeded`,
`AnomaliesDetected`, `AnomaliesRecovered`, `AnomaliesRecoveredFromBom`, `SetWriteBlocked`. The anomaly
*records* live on the agent as `TapeFileAgent.SetAnomalies`, because a struct copied into every callback
cannot own a list without every copy aliasing it.

Two counting rules are deliberate:

- **`AnomaliesDetected` counts once per set**, the user-meaningful number, while `SetAnomalies` accumulates
  every observation. `SetAnomalies.Count >= AnomaliesDetected` by design — the trail shows what was tried,
  and a progression like *expected 2, found 3* then *expected 2, found 4* diagnoses a persistent miscount
  that a single entry could not.
- **`SetWriteBlocked` is set at the terminal**, in `RejectSet` and the drift arm's `TerminalHere()`, never
  at detection. Before write-side recovery existed the two coincided; now a write-path anomaly is routinely
  detected and then repaired, and setting it early would report "blocked" for every successful recovery.

Scope is per operation, across volumes: the statistics reset where `_stats.Reset()` already runs, which is
the public entry verbs and never the multi-volume resumptions.

> **SH-16: a corrected drift is reported at Warning on both surfaces — the host `Report` channel and
> `m_logger` — and appears in the operation's closing summary, naming the stage that settled it.**

---

## 7. Placement

The set-header verification region moved from `TapeFileRestoreBaseAgent` to `TapeFileAgent` for three
forcing reasons: `DeleteSetsFromCurrentSetUp` already lived on the base; `ClassifySetHeader` is pure and
reads only base-owned TOC state; and the recovery ladder is now shared by every agent.

`TapeSetHeaderVerdict` moved to `TapeSetHeader.cs` — a file move at namespace scope, no API change.

**Nothing became abstract.** Making `HandleSetHeaderVerdict` or `CorrectSetNavigation` abstract would force
the ladder to be written twice, and its whole value is that every failure mode's diagnosis and error code
are defined *exactly once*. Two copies would drift — a peculiar way to implement drift detection. Only
`HandleSetHeaderVerdict` needed `protected` visibility, as the single interface to descendants.

Two additive, defaulted signature changes keep every existing caller compiling:
`DeleteSetsFromCurrentSetUp(bool navigateFromBegin = false, ITapeFileNotifiable? fileNotify = null)` and
`TapeFileAgent.VerifiesSetHeader { get; set; } = true`.

---

## 8. Mechanics

### 8.1 Opening the `MediaPrepared` window

`ReadSetHeaderBlock` refused anything but `TapeState.ReadingContent`. All three write-side call sites run in
`MediaPrepared`, so the guard had to widen — and the widening needed the same argument SH-7 itself makes.

That argument holds, and is **stronger** in `MediaPrepared`. What the guard protects is *"no packer of
either kind is running a worker thread against the drive."* In `ReadingContent` that is contingent, so the
manager backs the state check with an explicit `m_readPacker is not null` test. In `MediaPrepared` it is
structural: both packers are created inside their `Begin*` methods and disposed before transitioning back.
The precedent was already in the file — `WriteSetHeaderBlock` guards on `m_packer` for exactly this reason.

> **SH-17: `ReadSetHeaderBlock` fails hard if a packer of either kind exists, whatever the manager state —
> completing the read-packer guard SH-7 introduced.** A guard, not a `Debug.Assert`: the consequence is a
> data race against a worker thread, so it must fail identically in Debug and Release, and an assert would
> make the violation untestable.

### 8.2 What the read leaves undisturbed

`Drive.ByteCounter` was already saved and restored by `ReadSetHeaderBlock`, and the write side needs nothing
further — `BeginReadWrite` zeroes it after the verification read.

Block size is owned end-to-end by `TapeHeaderBlock`: `Read` captures the drive's size, sets 16 KiB, and
restores in a **`finally`**, so the restore survives a failed `SetBlockSize`, a short read, and any
exception. The stake was real — had the read left the drive at 16 KiB, `EnsurePackerCreated()` would have
captured that moments later, producing `BlocksWritten == 0` and files that never commit.

One adjacent edge needs no code: a drive whose `MaximumBlockSize` is below 16 KiB cannot carry set headers
at all, so `SetHeadersExpected` is false there and verification never arms.

### 8.3 The verify→write gap

The verifying read advances the head one block, and a recovery may land it at a *different set*. The set
start is therefore **derived after** a positive verdict as `Drive.CurrentBlock - 1`, never captured before
the read — a pre-read capture names the old set's start once a recovery has repositioned.

> **SH-15: every destructive write begins at the block derived from the verifying read, or at a position
> derived from it by a single transport step.**

---

## 9. Service and host surfacing

### 9.1 Two defects the step began by fixing

`ServiceOperationProgressHandler` implemented six `ITapeFileNotifiable` members and not the two new ones,
so it inherited SH-18's `Abort` default — correct for a notifiable that knows nothing about set recovery,
catastrophic for the one the service uses. Every repairable drift aborted the operation and reported
*"aborted per user request"* for a decision no user made.

`DeleteBackupSetsAsync` passed no notifiable at all, so the agent's no-notifiable policy declined every
destructive recovery. The originating scenario of the whole feature could not succeed from the UI.

### 9.2 The channel

`ServiceOperationProgressHandler` now overrides both members. It **reports first, asks second** — the host's
dialog is modal in most apps, so the log line must already be on screen when it opens. Sets are named in the
standard TapeNET notation via `DescribeSet` / `DescribeActualSet`: `#std | alt >description<`, with the
actual set's index quoted only when the series matches, since an index from a foreign volume would be
fiction.

`ITapeServiceHost.OnSetAnomalySelect` is the new prompt, mirroring `OnFileErrorSelect`. It returns `bool`
rather than the library enum — the host answers one question, *may I try to recover?*, and the service maps
it. `isDestructive` lets a UI warn accordingly. `WpfServiceHost` reuses `MediaMismatchDialog` with both
optional buttons suppressed; `ConsoleUxServiceHost` presents a two-choice select and, unattended, splits by
stakes: a read-path recovery proceeds, a destructive one declines, both logged.

The `_skipAllErrors` latch suppresses the prompt, deliberately shared with the file path — a caller who
asked not to be prompted about files did not mean *"except about sets"*.

### 9.3 The outcome

`FileOperationVerdict.SetVerificationBlocked` is checked **before** the file counters: a refused destructive
write processes no files, so every counter-based verdict would call it *"nothing happened"* — true and
useless. This is the verdict that names the cause.

Reaching it required one agent-side correction. `NotifySetAnomaly` now records `IsAbortRequested` **only
when the notifiable declined something it could have authorized**:

```
if (result == SetAnomalyAction.Abort && anomaly.CanAttemptRecovery)
```

With no stage left, the ladder was informing rather than asking — reporting that as a user abort would
credit the user with a decision never offered, and `JudgeFileOperation` tests `WasAborted` first, so the
block would have been permanently unreachable.

`SetAnomalyAdvice` and `AdviseOnSetAnomalies` / `AdviseText` (`TapeServiceBase.Outcome.cs`) form a second
judge/verbalize pair beside `JudgeFileOperation`: that one says what happened to the **files**, this says
what the **medium** appears to need. A backup can complete perfectly and still leave a cartridge wanting
attention. Ordered by what the cartridge is saying, not by count — a refusal outranks everything, and one
BOM recovery outranks any number of deltas:

| Advice | Recommendation |
|---|---|
| `WriteRefused` | verify the cartridge, then retry; if the volume is known damaged, delete its trailing sets first |
| `RepairTrailingSets` | deleting the last set(s) would rewrite the damaged area |
| `CheckDriveOrMedia` | the drive or cartridge miscounted marks — clean the drive, retire the cartridge if it recurs |

`ReportSetAnomalyOutcome` is silent when nothing was observed: an advice channel that fires on a clean
cartridge trains users to ignore it.

### 9.4 The delete verb

`DeleteBackupSetsExAsync` returns `DeleteSetsResult`, which derives from `ServiceOperationResult`
**directly** rather than from `FileOperationResult` — routing a delete through file counters would produce
the very *"no files processed"* line this work exists to eliminate. It carries `SetsRequested`,
`SetsDeleted` and `TapeUnchanged`. The old `Task<bool>` survives as a shim.

### 9.5 Requests

`BackupRequest` gained `CorrectSetNavigation` and `VerifySetHeader`; `RestoreRequest` gained
`VerifySetHeader`. All default `true`. Their wording matters: `VerifySetHeader` disables **the check**, not a
prompt — its one legitimate use is repairing a cartridge whose set headers are themselves damaged. It is not
a performance option, since an append pays nothing for it.

---

## 10. What the feature surfaced

As with the set header itself, implementing this exposed defects with no connection to set headers. They are
recorded because the feature's own correctness depends on them.

- **`MultiVolumeContext` doubles as the continuation signal.** `CanResumeToNextVolume` is
  `MultiVolumeContext is not null`, so an early exit that left it set told the service a continuation was
  possible. A refused overwrite was reported as *"volume full"*, saved a TOC it need not save, and skipped
  the outcome verdict entirely — which lives in the *not-continuing* branch. Every failure exit between the
  context's assignment and the file loop now clears it; only `HandleEom` may publish a continuation.
- **`TOCUnlocated` is a statement about the navigator, not the tape.** It is true on any fresh navigator,
  and the service reads the TOC with one agent and backs up with another — so it was true at the start of
  every backup, rewriting a perfectly good TOC after a refused overwrite and making *"the tape is
  unchanged"* a lie. The skip now gates on whether the medium was demonstrably untouched.
- **Restore's failure latch tested the wrong thing.** Latching on "returned false plus a non-skip action"
  manufactured a diagnosis out of a user decision, since that label is also reached with no error at all.
  It now latches on `WentBad`, and the invalid-`TapeFileInfo` branch sets a real error so there is something
  worth latching.
- **The error-reporting rule, settled and applied uniformly:** the result reports the **fault** when there
  was one, and the cancellation only when there wasn't. The user knows they pressed stop; what they don't
  know is what provoked it. Abort and Skip both latch; only Retry withholds, and only until the retry
  itself fails.
- **`_loadedHeader` staleness on append.** Cleared where the media is genuinely renewed — the non-append
  path and the new-volume path — rather than on a blanket refresh that would cost a rewind per append.

---

## 11. Validation

~100 tests were added across five suites, all four drive profiles where the profile matters:

| Suite | Covers |
|---|---|
| `TapeSetHeaderProbeTests` | the `MediaPrepared` window, SH-17's packer guard, and the block-size `finally` exercised on the failure path |
| `TapeSetHeaderWriteVerificationTests` | the overwrite gate, the §3.1 ordering fix, and `Append_AtEod_PerformsNoVerification` — §3's cost claim asserted rather than assumed |
| `TapeSetDeleteVerificationTests` | both delete branches, including that delete-all preserves the media header (INV-4 regressions are otherwise silent) |
| `TapeSetNotificationTests` | payload, the three abort channels, the DIM default, per-set prompting, and the statistics reaching the callback |
| `TapeSetNavigationRecoveryTests` | the crown suite — the originating scenario end to end, plus SH-19, SH-20 and the terminal split |
| `ServiceSetAnomalyTests` | the service channel, the advice policy, and the blocked/declined distinction |

Three test-design rules proved their worth repeatedly:

**Assert the tape, not the return value.** Every refusal test asserts that the pre-existing sets still
restore **byte-for-byte**. A version that refused *after* clobbering the set header would pass a
return-value check and lose the user's data.

**Assert the mechanism where luck could substitute for it.** `Renavigation_ActuallyMovesTheHead` drives
stage 1 to the case that does *not* self-reset and asserts real transport happened. Without it the SH-19
bug is invisible — every other test still passes by landing on the right set for the wrong reason.

**Fault injection must be honest about what it perturbs.** `SimulateSetMiscount` is consumed by whichever
hooked move comes first, which on setmark layouts is the `-1` settle inside `MoveToEndOfContentCore`, and on
`MoveToEndOfContent`'s blank-media fallback is swallowed entirely. Where that made the injector unreliable,
tests use `SimulateNavigationFailures` (a specific error code) or make the drift unrecoverable *by policy*
via `CorrectsSetNavigation = false` — never a persistent miscount at service level, which would take the TOC
write down with it.

`DamageTheTail` manufactures the real fault and is layout-aware: on filemark layouts an aborted mid-set
backup suffices; the setmark layouts shrug that off — correctly, since that is the whole value of a
dedicated mark type — so there the trailing setmark is erased after the fact.

No new hardware conformance probe is needed: every write here is post-mark or at-EOD, already isolated by
the existing **S11** probe.

---

## 12. Invariants

| | |
|---|---|
| **SH-13** | No destructive write proceeds without a `Match` verdict from a set header read at the write position during the same operation. |
| **SH-14** | Set-navigation recovery runs two stages — relative delta, then one re-navigation counted forward from begin-of-content when the failed navigation was end-anchored. Identical on every agent. |
| **SH-15** | Every destructive write begins at the block derived from the verifying read, or at a position derived from it by a single transport step. |
| **SH-16** | A corrected drift is reported at Warning on both the host channel and the logger, and appears in the closing summary, naming the stage that settled it. |
| **SH-17** | `ReadSetHeaderBlock` fails hard if a packer of either kind exists, whatever the manager state. |
| **SH-18** | `OnSetAnomaly` defaults to `Abort`, is raised at most once per set, and its `Proceed` authorizes every remaining recovery stage. |
| **SH-19** | The re-navigation resets `CurrentContentSet` before re-targeting. A believed position is never carried across a recovery stage. |
| **SH-20** | A navigation that FAILS with a positional error after counting BACKWARD is retried once, forward from begin-of-content. Any other error terminates immediately. |

---

## 13. Known boundaries

- **A header-less volume stays unprotected.** SH-1 makes presence a per-volume declaration; legacy
  cartridges declare nothing, and the load-time media check remains their sole guard.
- **A fresh volume's first set is deliberately unverified** (§3.2).
- **Damage at the head of the volume is unrecoverable by either stage.** Both assume the beginning of
  content is sound — the delta because it moves relative to a position reached through it, the re-navigation
  because it counts forward from there.
- **Recovery cannot repair a set whose header was never written.** A backup that failed before
  `WriteSetHeader()` leaves no record at the set start, so the verdict stays `Unreadable` through both
  stages. The manual escape is `DeleteSetsFromCurrentSetUp(navigateFromBegin: true)` with
  `VerifiesSetHeader = false`.
- **The file level stays unverified.** Within a correctly identified set, a file address resolving to the
  wrong block is still caught only by CRC.
- **`TapeNavigator` still cannot distinguish arrival at the oldest set from an overshoot** (§5.7.2). It
  reports what it can prove and the agent resolves it; a caller without set accounting gets the conservative
  answer.

---

## 14. Next steps and outlook

### 14.1 Exposing the feature in the WPF app

The log pane already carries everything (§9.2–9.3). What remains is making the set-level state *actionable*
rather than merely readable.

**The prompt is wired but plain.** `WpfServiceHost.OnSetAnomalySelect` reuses `MediaMismatchDialog`, which
was the right call for shipping — the user already associates that dialog's shape with *"the tape is not
what we expected"*. A dedicated dialog would earn its keep by showing the two sets **side by side** with
their standard indices, and by distinguishing the destructive case visually rather than only by severity
colour. `ProceedAlways` should stay suppressed: a standing "always recover" is exactly the permission a
destructive repositioning must never acquire.

**Surface the advice as an actionable banner.** `SetAnomalyAdvice.RepairTrailingSets` names a specific verb
the app already has. The natural shape is a post-operation banner in the same style as
`CalibrationResultViewModel`'s recalibration-advised banner, with a button that opens the Delete Backup Sets
dialog pre-selected to the volume's trailing sets. That closes the loop the feature was built for: detect a
damaged tail, then repair it in one click.

**Show set health in the sets list.** `ServiceOperationResult.Sets` is available after every operation. A
small glyph on the affected set's row — corrected, recovered-from-tail, or refused — would make the
cartridge's history visible where the user is already looking. Tooltip from `TapeSetAnomaly.ToString()`.

**Two settings need a home in the advanced/diagnostics group**, worded as behaviour rather than as
suppression: *"Report set-navigation drift instead of correcting it"* (`CorrectSetNavigation`) and
*"Skip set-header verification (repair mode)"* (`VerifySetHeader`), the latter with a warning that it
disables a data-protection check. Neither belongs in the main backup dialog.

**Media properties could show the declaration.** `LoadedMediaHeader.HasSetHeaders` is already cached; a
*"Set headers: yes/no"* row tells the user whether this cartridge is protected at all, which is otherwise
invisible.

### 14.2 Exposing the feature in the CLI app

**The prompt works unattended by design** (§9.2), splitting by stakes. Worth documenting in the man page
explicitly, since a script author needs to know a destructive recovery will decline rather than hang.

**Command-line flags for the two new request fields**, mirroring the existing ones:
`--no-correct-set-navigation` and `--no-verify-set-header`, both in the advanced section, the latter with a
prominent warning.

**A non-zero exit code for a refused write.** `SetVerificationBlocked` is a distinct outcome from a partial
failure, and a script that deletes trailing sets as a repair step needs to tell *"refused, tape unchanged"*
from *"failed part-way"*. `DeleteSetsResult.TapeUnchanged` carries exactly that.

**`service.list` could report set-header presence** alongside the other media properties, for the same
reason as the WPF properties pane.

**A dedicated repair verb** would be the CLI's counterpart to the WPF banner —
`tape repair-tail [--from-set N]`, wrapping `DeleteBackupSetsExAsync` with the advice text as its help. The
underlying capability exists; only the affordance is missing.

### 14.3 Library-level items still open

- **`ServiceOperationResult.ChecksSuppressed`**, carried over from Design-SetHeader.md §14.1: a suppressed
  prompt logs at Warning and proceeds, but the result does not record that a check was bypassed. An
  unattended overwrite that skipped an identity prompt should not look identical to one that had nothing to
  skip.
- **Hardware validation with set headers and verified writes enabled**, on AIT-2 and one LTO generation —
  combined with the set-header hardware item already pending.
- **`TOCUnlocated`'s two meanings.** The name reads as a statement about the tape; the flag is a statement
  about the navigator's knowledge. `BackupTOC(enforce: true)` uses it correctly in the knowledge sense. A
  distinct property for *"the TOC on tape is stale"* would stop the next reader making the inference §10
  records — though every current caller depends on the conservative reading, so this is a rename with
  homework, not a quick fix.
- **The file header** (Design-SetHeader.md §14.4) remains the next protection layer: within a correctly
  identified set, a file address resolving to the wrong block is still caught only by CRC.
