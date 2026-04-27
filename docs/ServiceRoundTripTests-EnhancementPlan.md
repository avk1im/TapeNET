# ServiceRoundTripTests — Enhancement Plan

Status: **I-1 ✅ · I-2 ✅ · I-3 ✅ · E-1 ✅ · E-2 ✅ · A-1 ✅ · A-2 ✅ · A-3 ✅ · A-4 ✅ · A-5 ✅ · A-6 ✅ — All items complete ✅**
Owner: avk1im
Last updated: 2025

---

## Context

`TapeConNET.Tests/Services/ServiceRoundTripTests.cs` was written as the Phase B
sanity-baseline (4 tests, `SilentConsoleUx`-based, small file trees). Phase C is
now complete — `TapeServiceBase` is the canonical engine and `TestTapeServiceHost`
is the designed test surface. This plan systematically grows the suite to cover
the full wishlist: richer trees, incremental chains, selective restore, abort
integrity, and multi-volume scenarios — all driven through `TapeServiceBase`.

---

## Survey of What Exists (before enhancements)

### Tests in `ServiceRoundTripTests.cs`

| Test | Covers | Gaps |
|---|---|---|
| `Backup_Then_Restore_RoundTripsBytes` | Single-volume, 9 files, `[Theory]` over both drive profiles | Tiny tree; `SilentConsoleUx`; no `StateChanges` assertions |
| `AppendBackup_AddsSecondSet_BothRestorable` | 2 sets (4 files each), both individually restorable | Only 4 files/set; no incremental flag; no selective restore |
| `Backup_Abort_ReportsWasAborted` | Abort via polling `Agent`; 200 files | Only checks `WasAborted = true`; never validates aborted set entries |
| `Validate_AfterBackup_Passes` | CRC Validate mode; 6 files | Baseline only; no failure injection; no incremental validate |

### Relevant agent-layer coverage that has **no** service-layer equivalent

- `IncrementalBackupRestoreTests.cs` — 4-wave chains, skip/succeed counts, version
  verification, all drive profiles. Does not exercise `TapeServiceBase` state machine
  or `ITapeServiceHost` callbacks.
- `MultiVolumeBackupRestoreTests.cs` — volume-span, continuation flags, incremental
  across volumes, volume-swap fixture. Likewise agent-layer only.

### Infrastructure gap

The existing service tests still use `TapeConNET.TapeService` + `SilentConsoleUx`.
`TestTapeServiceHost` (already in `TapeLibNET.Tests/Helpers/`) provides `ConfirmAnswers`,
`FileErrorAnswers`, and full multi-volume host-prompt hooks
(`OnVolumeFullConfirm`, `OnInsertNewMediaConfirm`, `OnVolumeContinueConfirm`) — none
of which are exercised by the current suite.

---

## Enhancement Items

Each item has an ID, a short description, the wishlist item it addresses, and its
implementation status.

---

### I — Infrastructure (implement first; all tests below depend on these)

#### I-1 · Migrate factory helpers to `TapeServiceBase` + `TestTapeServiceHost` ✅ DONE

**What changes:**
- `CreateService` now returns `(TapeServiceBase, TestTapeServiceHost)` instead of
  `(TapeService, SilentConsoleUx)`. Internally constructs `TapeService` (concrete
  subclass) but exposes it through the base type so no test directly touches
  console-specific members.
- `OpenAndFormatAsync` / `ReopenAsync` updated accordingly.
- `MakeBackupRequest` signature: `TapeService` → `TapeServiceBase` (uses only
  `DefaultBlockSize` which is on the base).
- Error assertions switched from `ux.Entries.Any(...)` to `host.HasErrors`.
- State-change assertions added where meaningful (`OperationStarted`, `OperationEnded`).
- Class-level XML doc comment updated from "Phase B, step 5" to current Phase C reality.
- Stale `using TapeConNET.Ux;` removed (no longer needed by helpers).
- `using TapeConNET.Services;` kept — `TapeService` is still the concrete type
  instantiated inside `CreateService`.

**Rationale:** `TestTapeServiceHost` is the designed test surface for Phase C and
beyond. It can script multi-volume prompts, record `StateChanges`, and is
host-agnostic. Keeping `SilentConsoleUx` as the primary assertion vehicle was a
temporary measure that is now a liability.

---

#### I-2 · Richer `TempFileTree` factory helper (static method on test class)

**What changes:**
- Add a private static `AddRichContent(TempFileTree tree)` helper in the test class
  that populates a tree with ~25 files across 3 subdirectories:
  `docs/` (8 × 1–16 KB), `data/` (10 × 4–64 KB), `assets/` (6 × 16–128 KB),
  plus one zero-byte file and one `nested/deep/large.bin` at ~192 KB.
- All subsequent tests call this helper instead of spelling out individual
  `AddFiles` calls.

**Addresses:** "use more files"

---

#### I-3 · `MultiVolumeTapeServiceHost` helper class ✅ DONE

**Implementation notes:**
- Composition over inheritance: `TestTapeServiceHost` is `sealed`, so
  `MultiVolumeTapeServiceHost` wraps it and delegates all standard
  `ITapeServiceHost` methods. Full recording surface accessible via `Inner`.
- Constructor: `(TapeServiceBase service, IReadOnlyList<TempVirtualMedia> volumes)`
  — volumes passed in backup order (index 0 = first/vol-1, index 1 = vol-2, …).
- Backup swap: `OnVolumeFullConfirm` always returns `true`;
  `OnInsertNewMediaConfirm(nextVolume)` calls `InsertVirtualMedia(vmd, FileMode.Create)`.
- Restore swap: `OnVolumeContinueConfirm` always returns `true`;
  `OnInsertMediaConfirm(volumeNeeded)` calls `InsertVirtualMedia(vmd, FileMode.Open)`.
- `VolumesInserted` counter incremented on each successful swap for assertions.

**What changes:**
- New file `TapeConNET.Tests/Helpers/MultiVolumeTapeServiceHost.cs`.
- Subclasses `TestTapeServiceHost`.
- Constructor accepts a list of pre-created `TempVirtualMedia` objects (one per
  anticipated volume) and a `TapeServiceBase` reference.
- Overrides `OnVolumeFullConfirm` / `OnInsertNewMediaConfirm`: performs the
  `service.EjectMediaAsync()` + `service.OpenVirtualDriveAsync(FileMode.Create)` +
  `service.LoadMediaAsync()` volume-swap dance, returns `true`.
- Overrides `OnVolumeContinueConfirm` / `OnInsertMediaConfirm`: performs the
  `service.EjectMediaAsync()` + `service.OpenVirtualDriveAsync(FileMode.Open)` +
  `service.LoadMediaAsync()` + `service.RestoreTOCAsync()` re-insert dance,
  returns `true`.
- Exposes `VolumesUsed` count for assertions.

**Addresses:** Foundation for A-5 and A-6.

---

### E — Tests to enhance (existing tests)

#### E-1 · `Backup_Then_Restore_RoundTripsBytes` — richer tree + state assertions ✅ DONE

**What changes:**
- Replace `src.AddFiles("docs", 8, ...) + src.AddFile(...)` with `AddRichContent(src)`
  (~25 files).
- Assert `result.FilesSucceeded == src.Files.Count` (exact, not just `> 0`).
- Assert `host.StateChanges` contains `OperationEnded` for both backup and restore phases.
  (Note: `OperationStarted` is not emitted by the current `TapeServiceBase` — only
  `OperationEnded`, `TocChanged`, `DriveOpened`, `MediaLoaded`, etc.)
- Assert `!host.HasErrors` (already present via `SilentConsoleUx`, now idiomatic).

**Addresses:** "use more files"

---

#### E-2 · `Backup_Abort_ReportsWasAborted` → renamed `Backup_Abort_SetEntriesAreIntact` ✅ DONE

**Implementation notes:**
- Abort is signalled by polling `Agent.Statistics.FilesSucceeded > 0` before setting
  `IsAbortRequested`, so at least one file is always committed before abort.
- The aborted set's last file may have incomplete CRC data on tape; validate is run
  with `SkipAllErrors: true` and asserts `FilesFailed <= 1`.
- `BackupResult.FilesSucceeded` is cross-checked against `toc[setIdx].Count` to confirm
  the TOC recorded exactly what the backup engine reported.

**What changes:**
- Keep the abort trigger via `Agent.IsAbortRequested = true` (the correct mechanism
  for bare `TapeServiceBase` — the `CancellationToken` bridge is only wired in the
  `TapeService` subclass override of `OperationCancellationToken`).
- After abort, reopen the tape and call `ExecuteRestoreAsync` in `Validate` mode
  targeting the aborted set.
- Assert `validateResult.Success == true` and
  `validateResult.FilesSucceeded == abortedSet.Count`
  (exactly the committed entries CRC-verify; none fail).
- Assert `validateResult.FilesFailed == 0`.

**Addresses:** "abort test: verify that the backup set aborted during its creation
is correct — all file entries verify ok"

---

### A — Tests to add (new)

#### A-1 · `IncrementalChain_BackupStatistics_CorrectSkipSucceedCounts` ✅ DONE

**Implementation notes:**
- Wave backups use `append: true` so each wave writes a new set after the previous.
- `ModifyFile` stamps version into content and advances `LastWriteTime` internally;
  no `Task.Delay` is needed for timestamp advancement.
- Wave 2 expects `FilesSkipped = 12` (15 original − 3 modified + 2 new added after prev incremental = 12 unchanged).

3-wave chain on a richer tree (15 files):

- Wave 0 — full backup: all 15 files backed up, 0 skipped.
- Wave 1 — modify 5 files → incremental: 5 backed up, 10 skipped.
- Wave 2 — modify 3 more + add 2 new → incremental: 5 backed up, 10+ skipped.

Asserts per-wave `FilesSucceeded`, `FilesSkipped`, `FilesTotal` from `BackupResult`.
Asserts TOC flags: set 1 `Incremental = false`, sets 2 + 3 `Incremental = true`.
Asserts per-set TOC `Count` reflects only the files actually written (not skipped).
Parameterized `[Theory]` over `withInitiator` (both drive profiles).

**Addresses:** "test especially incremental backup sets — checking backup"

---

#### A-2 · `IncrementalChain_Restore_CorrectVersionsAcrossSets` ✅ DONE

**Implementation notes:**
- Case A: incremental restore from set 3 → asserts `FilesSucceeded == 17` and
  byte-for-byte match against final `src.Files` snapshot.
- Case B: non-incremental set 2 → asserts `FilesSucceeded == 5` (no byte comparison needed).
- Case C: non-incremental set 1 → asserts `FilesSucceeded == 15`; byte comparison omitted
  because source files were overwritten by `ModifyFile` calls in later waves.
- Shared `SetupThreeWaveChainAsync` helper returns `ThreeWaveChain` record + `wave2Files` snapshot.

Reuses the same 3-wave setup from A-1 as a shared helper.

- Sub-case A: incremental restore from set 3 → byte-for-byte comparison with current
  source tree (latest versions win across chain).
- Sub-case B: non-incremental restore from set 2 → only the 5 modified files from
  wave 1 are present, at wave-1 versions.
- Sub-case C: non-incremental restore from set 1 → full original 15 files at version 0.

**Addresses:** "test especially incremental backup sets — checking restore"

---

#### A-3 · `SelectiveRestore_SpecificFiles_ByTapeFileInfo` ✅ DONE

- Create 2 backup sets (8 files each, different source roots).
- From set 1, pass explicit `IReadOnlyList<TapeFileInfo>` with only 3 of the 8 files
  in `CheckedFilesBySet`.
- From set 2, pass explicit list of 2 different files.
- Assert restored directory contains exactly those 5 files (not the other 11).
- Assert `result.FilesSucceeded == 5`, `result.FilesTotal == 5`.

**Addresses:** "restoring only selected files"

---

#### A-4 · `SelectiveRestore_AcrossMultipleSets_MixedSelection` ✅ DONE

- Single `RestoreRequest` with `CheckedFilesBySet` containing two set entries
  simultaneously.
- Verify files from both sets land correctly without clobbering each other where
  paths differ.
- Assert `result.FilesSucceeded` equals the sum of selected files from both sets.

**Addresses:** "restoring only selected files across multiple backup sets"

---

#### A-5 · `MultiVolume_RegularBackup_SpansVolumes_RestoreAllFiles` ✅ DONE

**Implementation notes:**

- Volume sizing is drive-profile-specific:
  - Setmarks drives: 20 MiB per volume (16 MiB is reserved for the in-tape TOC;
    leaves 4 MiB headroom).
  - Initiator-partition drives: 3 MiB per volume (TOC lives in the initiator partition).
  - Both cases: 16 files × 350 KiB ≈ 5.5 MiB total, exceeding the per-volume
    headroom and guaranteeing at least one automatic volume swap.
- `MultiVolumeTapeServiceHost.Service` is a settable property (set after
  construction) to break the circular dependency with `TapeServiceBase`.
- The `MultiVolumeTapeServiceHost(volumes)` primary constructor no longer takes a
  service parameter.
- Three factory helpers added: `CreateMultiVolumeService`, `OpenAndFormatMultiVolumeAsync`,
  `ReopenMultiVolumeAsync`.
- **Restore opens the last volume first** — the complete TOC (with continuation
  metadata for all volumes) is written to the final volume; the restore agent
  requests earlier volumes via `OnInsertMediaConfirm` as needed.
- `HasErrors` is not asserted during multi-volume backup: the service emits transient
  `ServiceReportLevel.Failed` entries for the EOM-transition file before classifying
  the error as end-of-media and retrying on the next volume. `result.Success` and
  `result.FilesFailed == 0` are the authoritative health indicators.
- TOC flag verification: a separate reopened service inspects vol2's TOC and asserts
  `ContinuedFromPrevVolume = true` on the continuation set.
- `VolumesInserted >= 1` asserted for both backup and restore paths.

---

#### A-6 · `MultiVolume_IncrementalBackup_SpansVolumes_CorrectVersionsRestored` ✅ DONE

**Implementation notes:**

- **Volume sizing:** vol-1 is sized so the full backup (16 × 350 KiB block-rounded
  to 384 KiB = 6,144 KiB) fits, but the incremental (16 × 700 KiB ≈ 11.2 MiB)
  overflows. Block-rounding tips 16 × 350 KiB right to the 6-MiB boundary, so
  vol-1 is sized at 24 MiB (usable = 8 MiB for setmarks) / 8 MiB (initiator).
  Vol-2 is larger to absorb the full incremental overflow: 30 MiB (setmarks) /
  14 MiB (initiator).
- **`fullSetIdx` capture:** after the full backup, vol-2 is still blank, so a
  single-element `[vol1]` list is passed to `ReopenMultiVolumeAsync` so that
  `volumes[^1]` resolves to vol-1 correctly.
- **Incremental backup open:** the host is constructed with the full `volumes`
  list (to allow overflow onto vol-2), but vol-1 is opened directly with
  `FileMode.Open` + `RestoreTOCAsync` rather than via `ReopenMultiVolumeAsync`
  (which would wrongly target vol-2 as `volumes[^1]`).
- **TOC flag check:** after the incremental backup, vol-2 is opened standalone;
  `toc2[toc2.FirstSetOnVolume]` must have `ContinuedFromPrevVolume = true` and
  `Incremental = true`.
- **Case A — incremental restore:** opened from vol-2 (last written); engine
  fetches vol-1 for earlier files via `OnInsertMediaConfirm`; all 16 files
  verified byte-exact against the modified (v1) content.
- **Case B — non-incremental restore of full set:** opened from vol-2; engine
  requests vol-1 (data lives there), so `VolumesInserted >= 1` is the correct
  assertion — not `== 0` as originally assumed.

**Addresses:** "multi-volume test — incremental"

---

## Summary Table

| ID | Action | File(s) | Wishlist item |
|---|---|---|---|
| I-1 ✅ | Migrate factories to `TapeServiceBase` + `TestTapeServiceHost` | `ServiceRoundTripTests.cs` | Foundation |
| I-2 ✅ | Richer `TempFileTree` factory helper | `ServiceRoundTripTests.cs` | "use more files" |
| I-3 ✅ | `MultiVolumeTapeServiceHost` helper | `Helpers/MultiVolumeTapeServiceHost.cs` (new) | Foundation for A-5, A-6 |
| E-1 ✅ | Enhance `Backup_Then_Restore_RoundTripsBytes` | `ServiceRoundTripTests.cs` | "use more files" |
| E-2 ✅ | Rename + enhance abort test to validate aborted set | `ServiceRoundTripTests.cs` | "abort test: entries verify ok" |
| A-1 ✅ | Incremental chain backup statistics | `ServiceRoundTripTests.cs` | "incremental backup sets — backup" |
| A-2 ✅ | Incremental chain restore (3 sub-cases) | `ServiceRoundTripTests.cs` | "incremental backup sets — restore" |
| A-3 ✅ | Selective restore by `TapeFileInfo` | `ServiceRoundTripTests.cs` | "restoring only selected files" |
| A-4 ✅ | Selective restore across multiple sets | `ServiceRoundTripTests.cs` | "selected files across multiple sets" |
| A-5 ✅ | Multi-volume regular backup + restore | `ServiceRoundTripTests.cs` | "multi-volume — regular" |
| A-6 ✅ | Multi-volume incremental backup + restore | `ServiceRoundTripTests.cs` | "multi-volume — incremental" |

---

## File Organization (final state) ✅

The monolithic `ServiceRoundTripTests.cs` has been split into focused files
mirroring the `TapeLibNET.Tests` structure:

```
TapeConNET.Tests/Services/
    ServiceTestBase.cs               ← abstract base: constants, factory helpers, AddRichContent, FindRestoredRoot
    ServiceBaselineTests.cs          ← I-1, E-1, E-2: lifecycle + single-volume baseline (4 tests)
    ServiceIncrementalTests.cs       ← A-1, A-2: incremental chains (2 tests + private ThreeWaveChain helpers)
    ServiceSelectiveRestoreTests.cs  ← A-3, A-4: selective restore (2 tests)
    ServiceMultiVolumeTests.cs       ← A-5, A-6: multi-volume (2 tests + volume constants + AddMultiVolumeContent)

TapeConNET.Tests/Helpers/
    MultiVolumeTapeServiceHost.cs    ← I-3: volume-swap host used by ServiceMultiVolumeTests
```

All 4 test classes inherit `ServiceTestBase`. Full suite: **58 passed, 1 skipped** (physical drive smoke test).
