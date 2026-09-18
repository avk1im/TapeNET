# Design — Backup Set Header for TapeLibNET

**Status:** v3 · **implemented and shipped**
**Scope:** the set header (per-set identity + index record) and verified set navigation
**Depends on:** the media header (`docs/Design-TapeHeader.md`, v12) — grammar, framing, `TapeHeaderBlock`, presence model
**Supersedes:** `Design-TapeHeader.md` §16 (Outlook — the set header)

> **The code is the authority.** This document explains the shape and the reasoning; every method named
> here exists, and where the two disagree the code is right. Principal files: `TapeSetHeader.cs`,
> `TapeTOC.cs`, `TapeAgent.cs`, `TapeBackupAgent.cs`, `TapeRestoreAgent.cs`, `TapeStreamManager.cs`,
> `TapeNavigator*.cs`.

---

## 1. What the feature does

Every backup set written on headed media carries a **set header**: one 16 KiB framed record, written as
the first block of the set's data, holding the set's identity (`MediaId`, `Volume`) and its two indices
(`VolumeSetIndex`, `GlobalSetIndex`).

Set positioning was **trusted** — the navigator counted marks and declared itself at set N. It is now
**checked**, and where the fault is a recoverable miscount, **self-corrected**.

Three user-visible capabilities:

- **Navigation drift is caught and repaired.** A restore that lands on the wrong set detects it from the
  set's own header, learns where it actually sits, and moves the remaining delta. The restore succeeds
  where it would previously have failed a CRC check or silently restored the wrong bytes.
- **A mid-operation cartridge swap is caught.** Media identity is verified at the moment of set access,
  not only at load — closing the window the service's load-time check cannot cover.
- **Legacy coexistence is automatic.** Header-less volumes carry no set headers, are known to carry none
  before any read, and restore exactly as they did before.

The set header is **additive and position-neutral**: no tapemark, no change to setmark or filemark
arithmetic, and no extra tape movement on the restore path.

---

## 2. Design principles

| | |
|---|---|
| **Checked, not trusted** | Every content-set read verifies the set it landed on against a framed-CRC record before delivering a single file byte. |
| **Correct on read, fail on write** | A read-side miscount is recoverable — move the delta and re-verify. A write-side miscount would destroy data, so v1 never writes speculatively and never self-corrects a write. |
| **Relative correction only** | The header states *where the head is*. Re-navigating from an absolute anchor reproduces the original miscount; only the delta exploits the new information (§8.3). |
| **A-priori facts only** | The header precedes its set's content, so it carries only what is known before the first file — identity and indices, never file counts or totals. |
| **Presence is declared, never probed** | The media header states whether set headers exist. No per-set "is this a header?" gamble, no backtracking. |
| **Counting stays sacred** | No mark, no boundary, no navigator arithmetic change. The merged-filemark optimization of the media-header design survives untouched. |

---

## 3. The record — `TapeSetHeader`

`TapeSetHeader.cs` defines the record; `TapeHeaderKind.Set` was already reserved, so wiring it into the
polymorphic probe cost one arm in `TapeHeader.ConstructFrom`.

| Member | Role |
|---|---|
| `MediaId` | Series identity; reinterprets the base `Id` slot. |
| `SetBlockSize` | The set's on-tape block size; reinterprets the base `BlockSize` slot (INV-13). **Advisory** (SH-11). |
| `Volume` | The volume carrying this set. |
| `VolumeSetIndex` | 0-based index on its volume — the **functional** index, the one navigation is verified against. |
| `GlobalSetIndex` | 1-based index in the series — **attribution**, advisory (SH-11). |
| `Description` | Clamped snapshot of the set's description, for diagnostics; `DisplayName` synthesizes one when absent. |

### 3.1 What is deliberately absent

Compression mode, hash algorithm, and a *binding* block size are not carried. They would duplicate the
TOC with no decision procedure — a disagreement would either make the header dead weight or demote the
TOC to a secondary authority — and they cannot be acted on at the moment they are read, because
`BeginReadContentForCurrentSet` has already committed to the TOC's parameters. Every field is also a
field to version, and the header hierarchy is closed and co-versioned.

`Description` is the single exception, admitted for the same reason `TapeMediaHeader.OriginalName` is:
a drift message must name *which* set, not merely an integer.

### 3.2 Why identity is carried despite the media header

`MediaId` and `Volume` duplicate a check the media header already performs, but they differ in *when*
they are read and in *what they enable*:

| | Media header | Set header |
|---|---|---|
| Read at | media load (`TapeServiceBase.RefreshLoadedHeader`) | every content-set access (agent) |
| Catches | wrong cartridge before the operation | cartridge swapped *during* the operation |
| Enables | the load-time verdict + prompt | **disambiguating index drift from wrong-tape** |

The last row carries the weight. Without `MediaId` in the set header a set-index mismatch is
uninterpretable — a miscount and a swapped cartridge would be indistinguishable, and the correction of §8
would have no safe entry condition.

### 3.3 The TOC is the sole factory

`TapeTOC.CreateSetHeader(int)` and `CreateSetHeaderForCurrentSet()` build every set header.
`TapeSetTOC` knows neither its own index nor the media identity; `TapeTOC` owns series identity
(`EnsureMediaId`) and set indexing, so it is the only object able to answer "which set of which medium is
this?". This preserves the established rule: **each header kind is built by the subsystem that owns its
identity** — `TapeTOC` for media and set, `TapeCalibrator` for runs.

One subtlety lives in `TapeTOC.FirstSetInternalOfVolume`: `CreateSetHeader` accepts *any* set index, so
it scans forward for the volume's first set rather than using the mounted-volume-anchored
`FirstSetInternalOnVolume` property, which would make `VolumeSetIndex` depend on where the TOC happens to
point — and go negative for a set on an earlier volume.

---

## 4. Presence — declared by the media header

### 4.1 The declaration

`TapeMediaHeader.HasSetHeaders` is one body field, self-describing exactly like `TocPlacement`. The media
header is read once per volume at every content choke-point (`TapeFileAgent.EnsureMediaHeaderResolved`)
and by the service at every load and volume swap, so the flag rides along at zero I/O cost — and
**per-volume re-resolution comes free**, which is what makes a mixed series (legacy volume 1, headed
volumes 2+) work.

The resolved flag is cached on the **navigator** as `SetHeadersExpected`, alongside
`MediaHeaderPresence`, so both reset together on every media (re)load and INV-9 extends to set headers
with no new machinery.

### 4.2 The pairing rule

**SH-1: media-header-present ∧ `HasSetHeaders` ⟺ set-headers-present, uniformly per volume.** A volume is
headed-with-sets, headed-without-sets, or legacy — never mixed within itself.

The flag exists to make the middle state expressible: media written by the release that shipped media
headers alone. A format field is cheapest *before* headers reach the field and cannot be retrofitted
cleanly afterwards, so one bool buys permanent freedom to ship the two features independently.

### 4.3 Why declared rather than probed

Both restore paths position **absolutely** — `RestoreNextFile` seeks the pipelined reader to the file's
exact `(block, offset)` — so consuming a stray block at a set start would cost nothing, and no "un-read"
primitive is needed. Probing is nevertheless the wrong shape for a different reason: it converts "no
header" into an *ambiguity* (blank? torn? legacy? foreign?) at a point where the answer is already
knowable for free.

### 4.4 The anomaly path

When `SetHeadersExpected` is true and the block at the set start does not classify as a set header, that
is a genuine anomaly rather than a legacy case. `HandleSetHeaderVerdict` logs a warning, re-anchors the
navigator if the failed read reset the content position (SH-6), clears the error, and **proceeds**. This
follows the golden rule inherited from the media header: *a record that cannot be verified never blocks*.
An unreadable set header removes a safety net; it does not remove the tape's data.

---

## 5. Layout and navigation

### 5.1 Layout

```
Single-partition:  ‹MH›<FM> ‹SH›[set0] [SM] ‹SH›[set1] [SM] … ‹SH›[setN] [SM] [toc1][FM][toc2][FM]
Partitioned:       content:  ‹MH›<FM> ‹SH›[set0] [SM] … ‹SH›[setN] [SM]   |   initiator: [toc1][FM][toc2][FM]
```

`‹SH›` is one logical block at the head of each set's data region, written with a single `WriteDirect`,
with **no tapemark of any kind** (SH-2).

### 5.2 No trailing filemark

The media header requires its trailing mark because begin-of-content is a **write entry point**: content
starts there repeatedly, and without a mark that write lands mid-data on a strict drive. The set header
faces no such hazard because **it is never written into, only written before**:

- The set-start position is always already legal — immediately after a setmark/filemark, at
  begin-of-content (itself post-mark), or at EOD.
- The set header is the **first** thing written there, so that write is post-mark or at-EOD.
- Tape writes truncate, so the position after that block **is** EOD, and every subsequent write in the
  set is a sequential append.

This retires the caveat carried in `Design-TapeHeader.md` §16. One assumption is load-bearing and named
explicitly: the drive treats a write as truncating. That is universal tape semantics and is modelled by
the virtual backend (`TruncateFromCurrentPosition`), so it is exercised rather than assumed.

### 5.3 The navigator's only addition

The set header lives inside the set's data region, past its opening boundary, so mark counting cannot
observe it (SH-3). `MoveToBeginOfContentFromBom`, the merged-filemark forward count, INV-1, and
`TapeNavigatorTOCInPartition` all stand unmodified.

The single addition is for the correction path: `TapeNavigator.ReconcileContentSetAndMove` (§8.4).

### 5.4 Two costs, in two currencies

The set header consumes **one block number** and **16 KiB of tape**, and these are distinct quantities.
The block cost is 1 regardless of the set's block size; the byte cost is fixed at 16 KiB because
`TapeHeaderBlock` sets and restores its own block size around the write. On a 256 KiB set the two diverge
by a factor of 16 — which is why `ComputeTotalFileSizeOnTape` adds `TapeHeaderBlock.Size` directly rather
than multiplying a block count.

The block cost needs no TOC-address surgery: file addresses are stamped at commit time from the packer's
anchor, which is taken *after* the header is on tape (§6.2).

---

## 6. Writing the set header

### 6.1 The gate

`TapeFileAgent.WritesSetHeaders` mirrors `WritesMediaHeader`; `TapeFileAgent.WriteSetHeader()` builds the
record via the TOC, frames it through `TapeHeaderBlock.Frame`, and hands it to
`TapeStreamManager.WriteSetHeaderBlock`.

**The condition is presence, not novelty.** `TapeFileBackupAgent.BeginWriteContentForCurrentSet` writes a
set header whenever content writing begins at a set's first block — covering `newSet: true` (fresh set at
EOD or at begin-of-content) *and* `newSet: false` (rewrite of the current set from its start). Both
destroy whatever stood there, so both stamp a fresh header. Multi-volume continuation is covered
automatically: a continuation set is a new set on the new volume, with `VolumeSetIndex = 0`.

The gate tests `Navigator.MediaHeaderPresence == Present`, not merely the flag — SH-1 again: on a legacy
volume nothing declares the header's existence, so writing one would produce a block no reader can be
told about.

### 6.2 The ordering seam

`Manager.BeginWriteContent` runs `MoveToTargetContentSet()` and `EnsurePackerCreated()` back to back with
no seam, and the packer anchors on `Drive.CurrentBlock`. Rather than thread a callback through the
manager, `BeginWriteContentForCurrentSet` **hoists** the positioning: it calls `MoveToTargetContentSet()`
itself, writes the header, and only then enters the write state.

The placement within that method is deliberate and documented in the code:

- **after** `SetBlockSize` + the TOC reconciliation, so the header records the block size the drive
  actually accepted;
- **after** `SetEarlyWarning` / `NotifyNextContentWritePosition`, so the header's 16 KiB counts against
  the TOC reserve like any other content byte;
- **before** `Manager.BeginWriteContent`, so the header is on tape before the packer anchors.

**SH-4: positioning to the target content set is idempotent.** `MoveToTargetContentSet` returns
immediately when `TargetContentSet == CurrentContentSet`, so the manager's later call is a genuine no-op.
This holds for the base implementation and for the `TapeNavigatorTOCInSet` fast-path override, whose
precondition (`CurrentContentSet < 0`) declines after a successful positioning.

### 6.3 Paths that need nothing

`BackupInitialTOC` (no content set), both branches of `DeleteSetsFromCurrentSetUp` (deleted sets'
headers truncate with their data; the delete-all branch writes only a TOC), and rename / TOC
save-restore-import (end-relative and header-agnostic).

---

## 7. Reading the set header

`TapeFileRestoreBaseAgent.BeginReadContentForCurrentSet` resolves media presence, ends any read/write,
sets the target, transitions to `ReadingContent`, and then verifies — before the block size and
compression interlock are applied, and long before any file byte is delivered.

### 7.1 Exactly one read per positioning

`Manager.BeginReadContent()` **early-returns without moving** when it already sits in `ReadingContent` at
the requested set. An unconditional read there would consume a *content* block and corrupt the first
file.

SH-8 is therefore enforced by capturing, **before** `BeginReadContent`, whether the head will actually
move:

```csharp
bool willPositionAtSetStart = Navigator.TargetContentSet != Navigator.CurrentContentSet;
```

That one expression decides whether the head will land on the set's first block — the only position where
a set header sits. It is knowable only at that moment, which is why it is captured there rather than
derived afterwards.

### 7.2 Raw block I/O inside `ReadingContent`

The set-header read occurs while the manager sits in `TapeState.ReadingContent`, which normally forbids
raw drive access. It is safe for one specific reason: **`TapeFilePipelinedReader` is constructed lazily
inside `BeginPackedFileRead`**, so no prefetch worker exists yet.

**SH-7** is enforced in `ReadSetHeaderBlock` as a hard guard (`m_readPacker is not null` → fail), not a
`Debug.Assert`: the consequence of violating it is a data race against the prefetch worker, so it must
fail identically in Debug and Release — and an assert would make the violation untestable.

`ReadSetHeaderBlock` also saves and restores `Drive.ByteCounter` around its read: the set header is
metadata the caller never asked for, and `BeginReadWrite` has just zeroed that counter for the set's
*files*.

---

## 8. The verdict ladder and the correction

### 8.1 Classification

`TapeSetHeaderVerdict` (in `TapeRestoreAgent.cs`) is produced by `ClassifySetHeader`, which is **pure** —
no I/O, no state change — so the ladder is unit-testable without a tape.

| Verdict | Action | Rationale |
|---|---|---|
| `Match` | proceed, trace only | the overwhelmingly common case; provably silent |
| `NotExpected` | proceed silently | legacy volumes remain first-class citizens |
| `Unreadable` | **warn, proceed** | an unverifiable record removes a net, not the data (§4.4) |
| `WrongMedia` | **fail the set** | every in-memory assumption is void, including the TOC |
| `WrongVolume` | **fail the set** | file addresses are physical-per-volume; every address would resolve to garbage |
| `SetIndexDrift` | **correct once, re-verify** | recoverable miscount, bounded by a re-entrancy guard (§8.3) |

Checks run in order of what each field can tell us: identity first (is this even the right cartridge?),
then position (are we where we think we are?). Reversing the order would make a swapped cartridge look
like a navigation miscount and invite a correction that cannot help.

**Identity is skipped when the TOC has no `MediaId`.** A TOC that predates identity stamping — imported,
or legacy — would otherwise fail every restore against `Guid.Empty`. The positional checks still apply,
and they are the ones that carry the feature. The same guard exists at the service layer in
`TapeServiceBase.EvaluateLoadedHeader`, where `Guid.Empty` likewise means *no expectation*, never *expect
zero*.

`GlobalSetIndex` and `SetBlockSize` are **checked but never gating** (SH-11): a mismatch logs a warning
and nothing more. `VolumeSetIndex` is functional — it drives navigation — while `GlobalSetIndex` is
attribution, and attribution can legitimately shift after a TOC import or a partial-series rebuild.

### 8.2 Identity mismatch is an agent-level error

The agent detects; the service explains. On `WrongMedia` or `WrongVolume` the agent sets the error and
fails the set, and the diagnosis reaches the user through the `TapeResult` → service → host chain (§10).
No prompt is raised: a cartridge swapped mid-restore is not a decision to offer the user, and the agent
has no host access by design.

### 8.3 The correction — bounded, verified, relative

`CorrectSetNavigation` implements it. Three constraints make it safe:

**(a) Relative, never absolute.** If navigation proceeded from BOM by *x* marks and landed at set *y*,
re-navigating from BOM by *x* marks lands at *y* again — the miscount is in the physical mark structure,
not in the arithmetic. Only the **delta** exploits the new information.

**(b) Bounded — one retry.** Correct, re-read, re-verify. `m_correctingSetNavigation` makes a second
disagreement terminal rather than recursive. The re-verify routes back through
`HandleSetHeaderVerdict`, so every failure mode keeps its own diagnosis and error code, defined exactly
once.

**(c) Read-side only.** Nothing on the backup path calls it. A write-side miscount means the agent is
about to overwrite the wrong set, where failing is correct and correcting is reckless (§12.3).

`CorrectsSetNavigation` (default `true`) turns the repair off without disabling the *check*, surfaced as
`RestoreRequest.CorrectSetNavigation`. Three reasons a user would want it: diagnosing a drive that
miscounts marks, auditing under a strict-verification policy, and preserving a suspect cartridge's
landing position for forensics. Correction never hides the fault, but it does move the head — which is
sometimes exactly what an investigator does not want.

### 8.4 The navigator addition

`TapeNavigator.ReconcileContentSetAndMove(actualContentSet, targetContentSet)` — `internal`, single
caller, documented precondition (SH-9: only from a positively classified header with matching identity).

It takes **both** positions and moves the delta in one call, rather than exposing a bare
`ReconcileContentSet` setter followed by a re-target. The reason is mechanical: `MoveToTargetContentSet`
opens with the SH-4 idempotence check, and on the drift path `TargetContentSet` already holds the
intended set — so setting `CurrentContentSet` and calling it again would return `true` without moving,
and the correction would silently do nothing. The delta must be expressed explicitly.

The backward direction is asymmetric: spacing back by N marks lands *before* the Nth preceding mark, so
the implementation splits it into `delta − 1` then forward-one, with a BOM branch for set 0 — the same
two-step the main navigation path uses, for the same reason.

---

## 9. Capacity and size accounting (SH-12)

Each set header consumes 16 KiB of content capacity. Three consumers learn it, each with its own source
of truth for the flag:

| Consumer | Flag source | Why |
|---|---|---|
| `TapeSetTOC.ComputeTotalFileSizeOnTape(blockSize, withSetHeader)` | caller | feeds `TapeServiceBase.Used` — the number the user reads |
| `TapeTOC.ComputeContentSizeOnTapeBeforeCurrentSet(..., withSetHeaders)` | `Navigator.SetHeadersExpected` | anchors `Drive.NotifyNextContentWritePosition` on the OVERWRITE path |
| `TapeServiceBase.Used` | `LoadedMediaHeader?.HasSetHeaders` | the service's cached media header |

Presence is a property of the **volume**, declared once in the media header (SH-1) — not of the set — so
it arrives as a parameter rather than a stored field. Storing it per set would duplicate the declaration,
create a second place for it to be wrong, and need serializing, which would reopen the format. All
parameters default `false`, so every pre-existing caller's arithmetic is unchanged.

The overwrite anchor matters more than `Used`: under-reporting there would tell the drive more room
remains than actually does, and the early warning would fire too late to reserve the TOC.

---

## 10. What the feature surfaced — error diagnosis and reporting

Implementing the set header exposed a class of defect that had nothing to do with set headers, and fixing
it became the larger half of the work. It is recorded here because the set header's own failure modes
depend on it.

### 10.1 The swallowed diagnosis

A set that fails verification fails *before any file is touched*, so `OnFileFailed` never fires and
`FilesFailed` stays 0. Under `ignoreFailures` (which the service always passes) the loop continues, the
next set's successful `BeginReadContentForCurrentSet` calls `ResetError()`, and the diagnosis is gone.
The service then discarded what remained at the implicit `TapeResult → bool` conversion. Net effect: a
set rejected for `WrongVolume` reported *"completed — no files processed"* and nothing else.

Three mechanisms now carry a diagnosis from the point of failure to the user:

- **`TapeResultBuilder`** — a first-failure latch. `TapeFileAgent.LatchFailure()` records the error at the
  moment it happens; `FailedOperationResult` returns the latched failure in preference to the live error
  state. **First** rather than last, because later failures are usually consequences. An abort
  deliberately does *not* latch — it is a user decision, not a fault — so `BuildFailure` supplies
  `ERROR_CANCELLED` as a fallback rather than yielding a silent `(false, 0, "")`.
- **`ServiceOperationResult.Diagnosis`** — the service result *embeds* a `TapeResult`, and `ErrorCode` /
  `Message` derive from it. Composition rather than conversion: a `with` expression cannot set one and
  forget the other.
- **`JudgeFileOperation` / `VerbalizeFileOperation`** (`TapeServiceBase.Outcome.cs`) — classification
  separated from wording, modelled on `JudgeRecalibration` / `LogRecalibrationDelta`. The
  `NothingProcessed` verdict is the one that most needs the diagnosis: without it the user is told only
  that nothing happened.

`TapeCalibrator` carries the same mechanism (`LastResult`, `FailedRunResult`), and for a sharper reason:
its verbs return `ITapeCalibration?`, so `null` is the entire signal, and several steps — the BOP exits in
`FindLastCheckpoint`, the legacy probe in `ReadRunHeader`, `InspectMedia`'s final reset — tolerate a
failure and clear the error.

### 10.2 Abort-channel convergence

Three channels can request an abort: the caller setting `IsAbortRequested`, a callback returning
`FileFailedAction.Abort`, and a callback **throwing** `TapeAbortRequestedException` — the only channel a
`void` notification has. The third did not record the flag, so it produced `ERROR_INVALID_STATE` and made
the service classify a user decision as a failure.

`IsAbortRequested` is now documented as **recording that an abort was requested, by whatever channel**,
and the notification wrappers (`NotifyPreProcessFile`, `NotifyPostProcessFile`, `NotifyFileSkipped`,
`NotifyFileFailed`) set it at the point of observation. That placement matters on the packed path:
`PackedCommitTracker.DrainPostProcess` converts the exception into a `bool` and loses its identity, so a
handler downstream could no longer tell an abort from a drain failure.

The three channels converge on three properties — the operation fails, the abort is recorded, the
diagnosis is non-empty — but **not** on the error code. `FailedAction` aborts *in response to* a genuine
fault, which the handler latches, and the latched cause rightly outranks the user's reaction to it.

### 10.3 Structural fixes found along the way

- **`SetStart` / `SetEnd` balance.** Backup issued one `Start` per operation and N `Ends` (one per
  volume). Now one of each per set, with `NotifySetStart(filesAdded:)` contributing the file count only
  on the first set — a continuation re-attempts files already counted, and already un-counted by the EOM
  rollback. `TapeBackupContext.isContinuation` carries that fact explicitly, because `fileIndex == 0` is
  legitimately reachable on a continuation when EOM struck on the very first file.
- **Statistics scope.** Every figure the service reports is cumulative across volumes; the agent's
  `_stats` never resets between them. The in-loop report is therefore a *progress notice*, not a
  per-volume verdict. `BytesBackedupInCurrentSet` is the one genuinely per-set figure, anchored once in
  `BeginWriteContentForCurrentSet` and deliberately not re-anchored at set end.
- **Header-cache staleness.** An overwrite backup calls `ResetMediaId()` and the ensuing content write
  stamps a fresh header, while nothing refreshed the service's `_loadedHeader`. A second overwrite in the
  same session then compared a format-time cache against a re-minted TOC and prompted about a mismatch
  that did not exist. Fixed by dropping the cache at `ResetMediaId()` — honest, since we no longer know —
  and by `RefreshLoadedHeader()` at operation entry when overwriting (the path that rewinds to BOM
  anyway; a straight append must not pay a rewind per operation).
- **`MediaHeaderStamped`.** `Manager.ContentWritten` does not cover the header, which is written as a raw
  block before the content session opens. Without this flag, a failure between the two would let the
  service roll the TOC back to the old `MediaId` while the tape carried the new one. Operation-scoped,
  not per-volume: the thing rollback would restore is a *series* identity.
- **`OnMediaHeaderWritten` invalidates the TOC** on co-located layouts
  (`TapeNavigatorTOCInSet.OnMediaHeaderWritten`), since a write at BOM truncates everything beyond it.

---

## 11. Validation

### 11.1 Fault injection rather than corrupt fixtures

Three seams, all `#if DEBUG`, all instance-level so parallel tests never interfere:

- **`TapeNavigator.SimulateSetMiscount`** — the navigator lands N sets away from its target while still
  believing it arrived. Reduces the crown scenario to a one-line arrangement.
- **`VirtualMediaFaultInjector`** — block-level faults at the *medium*, in four modes. `Fail`, `Partial`,
  and `Torn` model drive-reported errors; **`Corrupt`** is the realistic one, modelling host-path
  corruption landing before the drive computes ECC: full byte count, no error, wrong bytes. Nothing below
  the application can detect it — which is exactly what an application-level CRC exists for, and what
  `Restore_SilentCorruption_IsCaughtByCrc` finally exercises end to end. Bit placement is seeded, never
  ambient: an unreproducible failing test is worse than no test.
- **`TestTapeService` hooks** — `OnAgentReady`, `OnCalibrationProgress`, and a hooked progress handler
  that intercepts `ITapeFileNotifiable`. These replaced wall-clock polling of `svc.Agent`, which raced
  the worker thread three ways: a debugger pause burned the deadline, a fast machine finished before the
  first poll, and a slow one armed the simulator after the loop had passed the files it targeted. Each
  hook runs **inside** the operation, at a deterministic point.

### 11.2 The matrix

The media-header design's fixture seam gained one axis, migrated compiler-driven (drop the fixture
default, fix every resulting build error, restore the default last).

| Suite group | Flavours | Why |
|---|---|---|
| Navigator | 2 | layouts are written with raw block ops, never through an agent — a set header cannot appear |
| Backup (aligned + packed) | 2 | the write gate is covered by dedicated tests; a third flavour is pure runtime |
| Restore (aligned + packed + pipelined) | **3** | `_MediaHeader` is the only flavour that catches a read gated on `MediaHeaderPresence` instead of `SetHeadersExpected` |
| Multi-volume | 4 modes | `VolumeHeaderMode` folds headers and set-headers into one axis |

`VolumeHeaderMode.Mixed` is deliberately two-way (volume 1 legacy, volumes 2+ headed). A three-way mix
would depend on how many volumes a test actually spans, so it could not state what it covers. Two-way
pins the property that matters: per-volume re-resolution of `SetHeadersExpected`.

`Fixture_ProducesTheDeclaredHeaderShape` (single-volume) and
`Fixture_ProducesTheDeclaredHeaderShapePerVolume` (multi-volume) read the media header back and assert it
*declares* what the flavour requested. Without them a fixture that quietly dropped a set-header request
would make an entire flavour pass for the wrong reason.

### 11.3 Coverage

- **Unit** — round-trip; polymorphic vs. narrow unpack across all three kinds; a `TapeSetHeader` block met
  by the media path resolves `Absent`; `CreateSetHeader` index arithmetic across `FirstSetOnVolume`
  boundaries and continuation sets.
- **Agent, four drive profiles** — per-set write→read round-trip; the first file's address sits exactly
  one block past the set start (which is simultaneously the SH-4 assertion — a redundant second
  positioning would move the packer's anchor); `WritesSetHeaders = false` produces `_MediaOnly` media
  that restores silently.
- **Correction (the crown suite)** — `SimulateSetMiscount ∈ {−2, −1, +1, +2}` × four profiles: drift
  detected, corrected, re-verified, restore **byte-for-byte**. Every correction test asserts the restored
  bytes, not merely a `true` return: a correction that "succeeded" while delivering a neighbouring set's
  data would be worse than the failure it replaced. Plus the boundary cases (correction to set 0 via the
  BOM path; forward to the newest set), uncorrectable drift, `CorrectsSetNavigation = false`, and
  identity failures that must *not* attempt a correction.
- **Ordering** — SH-8: a second `BeginReadContentForCurrentSet` on the same set consumes no block.
- **Accounting** — the per-set delta is exactly one header block, scales with set count, and is
  independent of the set's block size; the accounted delta matches the *physical* one-block offset of the
  first file, so a consistent-but-wrong formula cannot pass.
- **Diagnosis** (§10) — a failure on an early file survives later successes; an abort yields
  `ERROR_CANCELLED` with a real message; the three abort channels converge; a clean run leaves no latched
  failure; `ErrorCode` and `Message` never disagree with the embedded diagnosis.
- **Service** — happy paths stay provably silent under the exhaustive `AssertMediaPrompts` teardown.

### 11.4 Real hardware

No new conformance probe: §5.2 establishes that every set-header write is post-mark or at-EOD, both
already isolated by the existing **S11** probe and accepted on AIT-2 and DLT-V4.

---

## 12. Invariants

| | |
|---|---|
| **SH-1** | Media-header-present ∧ `HasSetHeaders` ⟺ set-headers-present, uniformly per volume. A volume is never partially headed. |
| **SH-2** | The set header is one 16 KiB framed block at the head of its set's data, with **no tapemark**. |
| **SH-3** | The set header contributes no mark and never alters set counting. |
| **SH-4** | Positioning to the target content set is idempotent — hoisting `MoveToTargetContentSet` ahead of `BeginWriteContent` costs no second transport move. |
| **SH-5** | The set header carries only a-priori facts. No file counts, no totals, no post-hoc data. |
| **SH-6** | Set-header block operations leave the navigator untouched on success and call `ResetContentSet()` on failure. |
| **SH-7** | The set header is read immediately after `BeginReadContent()` and strictly before the first `BeginPackedFileRead` — enforced as a hard guard, not an assert. |
| **SH-8** | The set header is read exactly once per physical positioning at a set. |
| **SH-9** | `CurrentContentSet` is adopted from a set header only via `ReconcileContentSetAndMove`, only on a positively classified header with matching `MediaId` and `Volume`. |
| **SH-10** | Correction is relative, bounded to one retry, verified by a second read, and never applied on a write path. |
| **SH-11** | `GlobalSetIndex` and `SetBlockSize` are advisory — logged on mismatch, never gating, never overriding the TOC. |
| **SH-12** | On-tape size accounting includes one header block per set. |

---

## 13. Known boundaries

### 13.1 The format window is closed

`HasSetHeaders` shipped in `TapeMediaHeader`. Media written before it defaults to `false` — survivable,
and permanent.

### 13.2 Correction masks a real fault

A successfully corrected drift means the drive or the medium miscounted marks — a hardware or media
signal, not a nuisance. It surfaces at **Warning**, never Trace, so it reaches the WPF log pane and the
CLI. A tape that corrects on every set is a tape to retire, and the log is the only place that fact will
ever appear.

### 13.3 The write path stays unverified

v1 verifies on read only. An overwrite that lands on the wrong set still destroys data silently — the
service's load-time media check remains the sole guard. This is a deliberate boundary: a pre-write
verification read costs a read plus a reposition on the performance-critical path. See §14.3.

---

## 14. Outlook

### 14.1 Exposing the opt-outs to the user

Three request flags exist and are not yet reachable from either application:

| Flag | Request | Suppresses |
|---|---|---|
| `ProceedOnMediaMismatch` | `BackupRequest` | the identity prompt before a destructive overwrite |
| `ProceedOnMediaMismatch` | `RestoreRequest` | the per-volume identity prompt |
| `ProceedOnMediaMismatch` | `CalibrateRequest` | the pre-run "holds a backup?" confirm |
| `CorrectSetNavigation` | `RestoreRequest` | *nothing* — it disables the repair, not the check |

**Does disabling a prompt imply disabling the header check? No — and the distinction must be preserved
in the UI wording.**

Each of the first three suppresses an interactive **prompt** that a non-interactive host cannot answer.
None of them disables the underlying check's ability to *detect*, and none of them reaches the set-header
verification at all, which raises no prompt and therefore has nothing to suppress. Concretely:

- `ProceedOnMediaMismatch` suppresses the service's **load-time, per-volume** prompt. The agent's per-set
  verification still runs on every set, still fails a `WrongMedia` or `WrongVolume` set, and still
  corrects drift. That is the intended layering: the prompt is the interactive guard, the set header is
  the unattended one.
- `ProceedOnMediaMismatch` likewise suppresses only checkpoint prompts. The backup path performs no set-header
  verification at all (§13.3), so there is nothing further to disable.
- `CorrectSetNavigation = false` is the only flag that changes *detection behaviour*, and it makes the
  check **stricter**, not weaker: a drift is reported rather than repaired.

So the UI should present these as **"don't ask me" checkboxes**, not as "skip verification". Suggested
wording: *"Proceed without confirming media identity (unattended)"* for the two suppressors, and
*"Report set-navigation drift instead of correcting it"* for the third — the latter belonging in an
advanced or diagnostics group, since its audience is someone investigating a drive.

One genuine gap: a suppressed prompt currently logs at Warning and proceeds, but the *result* does not
record that a check was bypassed. Worth adding a `ChecksSuppressed` flag to `ServiceOperationResult` so a
UI can badge such an operation — an unattended overwrite that silently skipped an identity prompt should
not look identical to one that had nothing to skip.

### 14.2 Other pending steps

- **Surfacing recovered anomalies (§10).** `OnSetFailed` / `OnSetAnomalyRecovered` on
  `ITapeFileNotifiable`, accumulated by the progress handler into `RestoreResult`, so a corrected drift
  appears in the closing summary and a rejected set explains itself. The host `Report` channel and the
  developer logger are strictly separate surfaces — the former user-facing via `ITapeServiceHost`, the
  latter via `m_logger` — and the drift warning belongs on both.
- **Hardware validation with set headers enabled** on AIT-2 and one LTO generation.
- **`ServiceOperationResult.ChecksSuppressed`**, per §14.1.

### 14.3 The next feature — verified overwrite and delete (§13.3's v2)

Set headers currently protect reads. The natural next step is to protect the two **destructive** paths
that today rely solely on the TOC's arithmetic:

- **Overwrite** — `BackupRequest.AppendAfterSetIndex` reuses a set slot mid-tape and destroys everything
  after it. A miscount here writes over the wrong set.
- **Delete** — `DeleteSetsFromCurrentSetUp`'s trailing branch navigates to the first set to delete, steps
  back one setmark, and rewrites it. A miscount deletes the wrong sets.

Both become verifiable with the record already on tape: read the existing set header before the
destructive write and compare it against the set the TOC believes is there. The shape mirrors §8, with
one rule inverted:

| Read side (shipped) | Write side (v2) |
|---|---|
| `CorrectsSetNavigation` on the restore agent | `VerifiesBeforeOverwrite` on the backup agent |
| `RestoreRequest.CorrectSetNavigation` | `BackupRequest.VerifyBeforeOverwrite` |
| drift → correct, identity → fail | **any mismatch → fail, never correct** (§9.3c) |

The asymmetry in the last row is the whole point and should stay explicit in both code and UI: reading
the wrong set wastes time, writing the wrong set destroys data.

Two design questions to settle when it starts. **Cost**: the verification read plus reposition lands on
the performance-critical write path, which argues for an opt-in default-on flag rather than an
unconditional check — and for measuring on real LTO before deciding. **Surfacing**: a refused overwrite is
exactly the case `TapeMediaVerdict.MediaInconsistent` and a new `MediaPromptContext` were reserved for,
so it can raise a genuine prompt rather than a bare failure — unlike the read side, where the user has no
useful decision to make.

### 14.4 Further out — the file header

Deferred, and likely to take a different shape: not a block-level record but a framed region inside the
on-tape file stream.

The groundwork exists. In `TapeFileBackupAgent.BackupFile` the hash, the codec and `wstream.Length` are
all in hand synchronously before `Manager.EndPackedFile()`; only `StartAddress` and `Length` arrive later
via `PackedCommitTracker.OnCommitted`, and a reader standing on the file needs neither. A framed header
plus a **fixed-size** framed trailer around the existing `SerializeHeaderTo` payload would cost no new
asynchrony, and restore could bound the body arithmetically rather than by seeking.

Two constraints carry forward: the file pipeline is the LTO-speed bottleneck, so any per-file record must
be pure serialization with no extra tape operation; and a fixed trailer size is what keeps the restore
path arithmetic rather than seek-based.
