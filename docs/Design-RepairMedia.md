# Design — Media Reconciliation ("Repair Media") for TapeNET

**Status:** v1 · **proposed**
**Scope:** examine a cartridge whose tail no longer matches its table of contents, propose a
reconciliation, and — on approval — carry it out
**Depends on:** verified destructive navigation (`docs/Design-SetHeader-VerifiedWrite.md`, v4) —
SH-13…SH-20, the verdict ladder, `TapeSetAgent.DeleteSetsFromCurrentSetUp`, the set-level anomaly
channel; the set header (`docs/Design-SetHeader.md`, v3) — record, presence model, `ClassifySetHeader`

*The code is the authority.* This document describes a feature not yet implemented; where it names an
existing member, that member exists today and the description matches it.

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
  describes.
- **It proposes a plan** — which sets it can keep, which it cannot, and what will change — and modifies
  nothing until the user approves.
- **It reconciles in both directions.** The manual route can only delete sets *from the tape*. Reconciliation
  can also correct the *TOC*: sets it cannot reach are removed from the in-memory TOC, whether or not any
  tape write is needed.
- **It works from whichever TOC the user has.** The one in memory after a botched operation, one imported
  from a `.tapetoc` file, or one read from a later volume of the same series — all of which describe sets on
  this volume.

### 1.1 Naming

The user-facing verb is **Repair Media** (`Media | Repair Media…`), subtitled *"reconcile the table of
contents with the tape"*. The internal vocabulary is **reconciliation**: `TapeSetScanEntry`,
`MediaReconciliationPlan`, `AnalyzeMediaAsync`, `RepairMediaAsync`.

*"Set recovery"* was considered and rejected: it undersells a feature whose fix is often entirely in the TOC,
and it collides with the navigation **recovery** of SH-14, which is a different thing at a different layer.

---

## 2. Why this is a separate feature

The verified-write work is stable, shipped, and complete on its own terms. This one:

- **deletes sets the user did not individually name** — a materially different risk profile;
- has its own UI surface (a two-phase dialog with a plan the user approves);
- has its own failure modes (a scan that stalls, a TOC describing another cartridge);
- needs only ONE new agent primitive, and otherwise composes what already exists.

So it ships as its own increment, referencing SH-13…SH-20 rather than extending them.

---

## 3. Design principles

| | |
|---|---|
| **Propose, then act** | Analysis and application are separate calls. The plan is a value the user approves; nothing is written until they do. |
| **Reachability is evidence, integrity is not** | The scan proves a set can be *located*. It says nothing about whether its files are readable — and the result says so. |
| **The scan observes; it does not repair** | Navigation correction is DISABLED during analysis. A recovery that quietly repositions would corrupt the measurement being taken. |
| **Rescue only what is proven** | A set is kept only when both its header verifies AND its closing mark exists. Anything less is a candidate for removal, not for optimism. |
| **Reuse the destructive surface** | The truncation is `DeleteSetsFromCurrentSetUp`, unchanged — so SH-13 re-confirms the scan's conclusion at the moment of the write. |
| **Leave a way back** | The pre-repair TOC is exported to a file before anything is applied. |

---

## 4. The scan — what "rescuable" means

### 4.1 Two tests, not one

For each set, walking forward from begin-of-content:

1. **The set header verifies.** Read the block at the set start and run `ClassifySetHeader` — reused
   verbatim, since it is pure and already tested. Anything but `Match` disqualifies the set.
2. **The closing mark exists.** `MoveToNextContentSetmark(1)` succeeds without running into EOD.

**Test 2 is the decisive one, and the one a header-only scan would miss.** `WriteSetHeader` runs at the set
*start*, before the first file — so a backup killed mid-set leaves a **perfectly healthy set header** behind.
Judged on its header alone, a half-written set looks rescuable. Its missing closing setmark is what proves it
never completed.

That yields three states:

| State | Header | Closing mark | Disposition |
|---|---|---|---|
| **Complete** | `Match` | present | **keep** |
| **Partial** | `Match` | missing (EOD reached) | the user's one decision (§6.3) |
| **Lost** | anything else | — | remove from the TOC; the tape beyond is truncated |

### 4.2 Forward-only, and deliberately uncorrected

The scan navigates **forward from begin-of-content**, never via `CurrentSetAsNavigatorContentSet`'s
nearest-anchor optimisation. The tail is the thing under suspicion; a backward count would cross precisely the
region being measured.

It also runs with `CorrectsSetNavigation = false` for its duration. SH-14's recovery exists to *reach* a set
despite drift — which is exactly what the scan must not do, because "could not be reached without correction"
is a finding, not an obstacle.

### 4.3 Cost

One setmark hop plus one 16 KiB read per set. Minutes on LTO for a large TOC, which is acceptable for a
repair but not acceptable silently: the scan reports progress per set and is abortable throughout.

### 4.4 Two findings the naive plan misses

**The TOC may describe fewer sets than the tape holds** — an imported TOC from an earlier point in the
cartridge's life, for instance. Reconciliation cannot invent file lists for sets it finds but cannot describe,
so those sets can only be truncated away. Never silently: this is its own finding, reported explicitly and
requiring separate confirmation.

**A `WrongMedia` verdict at the first set terminates the scan.** The TOC belongs to a different cartridge and
there is nothing to reconcile. §5.2's media-header check catches most of these up front; this catches a TOC
carrying no `MediaId` (legacy or imported), where `ClassifySetHeader` skips the identity test.

---

## 5. Workflow

```
1. Choose the TOC source        → in memory / from file
2. Check it against the media    → identity, volume, set-header presence
3. SCAN (read-only, abortable)   → per-set findings + progress
4. PRESENT the plan              → keep / remove, plus the partial-set decision
5. APPLY (on confirmation)       → export old TOC, truncate, update TOC, save TOC
6. REPORT                        → what was kept, and what to validate next
```

### 5.1 Step 1 — the TOC source

Three origins, all already supported by `TapeAgentBase`:

- **In memory** — the default, and the most valuable case: the TOC surviving a backup that failed before its
  own TOC write. Perishable, which is why the feature is offered at that moment (§7.2).
- **From a file** — `LoadTOCFromFile`, the emergency export.
- **From a later volume** — a multi-volume series' later TOC describes every set of every earlier volume.
  Reached by loading that volume's TOC first, then swapping cartridges.

### 5.2 Step 2 — does this TOC even fit this media?

Before any scan, the same checks a backup performs: read the BOM header, compare `MediaId` and `Volume`
against the TOC, and note `HasSetHeaders`. A cartridge that declares **no** set headers cannot be scanned at
all — test 1 of §4.1 has nothing to read — and the feature declines with that explanation rather than
producing a plan built on one test instead of two.

### 5.3 Step 5 — what "apply" does

1. **Export the pre-repair TOC** to a `.tapetoc` file (`SaveTOCToFile`). This is the operation's only undo,
   and it is automatic rather than offered.
2. **Truncate the tape**, if any set is being removed: set `TOC.CurrentSetIndex` to the first non-kept set
   and call `DeleteSetsFromCurrentSetUp`.
3. **Update the in-memory TOC** — which step 2 already does via `RemoveSetsAfterCurrent()` /
   `RemoveAllSets()`.
4. **Save the TOC** — likewise already done by the delete's own `BackupTOC` / `BackupInitialTOC`.

When nothing needs truncating — the TOC described sets that are simply absent from a tape that is otherwise
sound — steps 2–4 collapse to a TOC correction and a `BackupTOC()`. That is the TOC-only reconciliation the
manual route cannot express at all.

---

## 6. The one new primitive, and what is reused

### 6.1 Why `DeleteSetsFromCurrentSetUp` needs no changes

This is the finding that makes the feature small.

The on-tape layout is `[SH][set N][SM][SH][set N+1][SM]…`. If set *N* completed, **its closing setmark
exists**. So the position "just past set N's mark" is reachable by counting forward from begin-of-content —
*even when set N+1's header is garbage*, because nothing needs to read it.

And that is exactly where `DeleteSetsFromCurrentSetUp(navigateFromBegin: true)` navigates for
`CurrentSetIndex = N+1`, before stepping back one setmark and rewriting it.

So the reconciler sets `TOC.CurrentSetIndex = N+1` and calls the existing verb. A rescued-count of zero routes
into the delete-all branch, also existing. **The reconciler contributes the scan; the destructive surface is
unchanged** — and its own SH-13 verification independently re-confirms the scan's conclusion at the moment of
the write.

### 6.2 `TapeSetAgent.ScanContentSets` — the new method

```csharp
public TapeResult ScanContentSets(out IReadOnlyList<TapeSetScanEntry> findings,
                                  ITapeFileNotifiable? fileNotify = null);
```

Read-only. Forward from begin-of-content. Per set, records `(SetIndex, Verdict, HasClosingMark, StartBlock,
Description)`. Saves and restores `CorrectsSetNavigation` around its body (§4.2).

It belongs on `TapeSetAgent` rather than `TapeAgentBase` for the same reason
`DeleteSetsFromCurrentSetUp` does: it is a set-level operation, and a validate agent has no business
performing one. It is read-only, so `BlocksOnUnverifiableSet` never comes into play.

`TapeSetStatistics` gains `SetsScanned` / `SetsComplete` so the host can render progress through the existing
channel rather than a new one.

### 6.3 The partial set — the only real decision

**Default: drop it.** Two reasons, and the second is the stronger:

- Its TOC entries may name files that never reached tape, so keeping it means keeping a TOC that lies.
- Keeping it means **writing a closing setmark at EOD**, over a region nothing has verified — the one write
  in this feature that lands outside proven territory.

Offered as an unchecked advanced option, *"attempt to salvage the last partial backup set"*. If taken, the
result mandates a Validate pass rather than suggesting one.

### 6.4 What the result must not claim

Reachability is not integrity. The last kept set's **files** are unverified — the scan proved the set can be
located, nothing more. The result ends with a one-click **"Validate the recovered sets now"**, and the
summary says so in those terms.

---

## 7. Where the feature is offered

### 7.1 Always

`Media | Repair Media…`, for any loaded media.

### 7.2 From the advice banner

`SetAnomalyAdvice.RepairTrailingSets` currently points at Delete Backup Sets. It should point **here**: this
is what that advice always meant, and it removes the *"which sets are lost?"* guesswork that made the manual
route hazardous.

### 7.3 After a failed backup

When the TOC write failed or `TOCUnlocated` is set — the moment the in-memory TOC is simultaneously most
valuable and most perishable.

### 7.4 After a blocked delete

`Sets.SetWriteBlocked` means a verification refused the write, which is precisely the condition
reconciliation examines.

**Not after a successful operation.** Nothing to repair, and offering it would read as an admission of doubt.

---

## 8. UI — the WPF dialog

### 8.1 Phase 1 — source and scan

TOC-source radio (In memory · From file…), the media identity summary, and `[Scan]`. Progress over sets,
cancellable, live log into the existing pane.

### 8.2 Phase 2 — the plan

One row per set:

| | Set | Description | Files | Disposition |
|---|---|---|---|---|
| ✔ | #1 \| -4 | Monthly — March | 1,204 | Keep |
| ✔ | #2 \| -3 | Monthly — April | 987 | Keep |
| ⚠ | #3 \| -2 | Monthly — May | 412 | **Partial — drop** ▾ |
| ✖ | #4 \| -1 | Monthly — June | 0 | Lost — remove |

Below it, plain language: *"Sets #1–#2 will be kept. Set #3 was interrupted and will be removed. The tape
after set #2 will be overwritten and that space becomes available again."* Then the irreversibility warning,
and `[Repair]` / `[Cancel]`.

**The partial row's dropdown is the only editable cell.** Everything else is determined by evidence. A repair
dialog that invites per-set fiddling invites the mistake it exists to prevent.

### 8.3 Phase 3 — result

Sets kept, sets removed, capacity freed, and `[Validate recovered sets]`.

---

## 9. Implementation shape

**Agent** — `TapeSetAgent.ScanContentSets` (§6.2) plus two counters on `TapeSetStatistics`. Nothing else.

**Service** — a new partial `TapeServiceBase.Repair.cs`:

```csharp
public Task<MediaReconciliationPlan> AnalyzeMediaAsync(ReconciliationSource source);
public Task<RepairMediaResult> RepairMediaAsync(MediaReconciliationPlan plan);
```

Splitting analysis from application is what makes the plan a reviewable value — and makes the whole analysis
unit-testable without a single tape write.

**Host** — no new prompt. The plan *is* the prompt, confirmed in the dialog before `RepairMediaAsync` is
called. `OnSetAnomalySelect` already exists for anything the underlying delete raises.

---

## 10. Open questions

- **Partial-set salvage semantics** (§6.3) — the only branch that writes into unverified territory, and the
  piece to settle before implementation starts.
- **Legacy (header-less) media** is out of scope by construction (§5.2). Whether a weaker mark-only scan is
  worth offering there is a separate question.
- **Scan resumability** on very large TOCs — probably unnecessary, since an aborted scan has written nothing
  and can simply be re-run.
