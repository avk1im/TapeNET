# Design — Multi-Volume Operations

**Scope:** how a backup spans cartridges, how a restore / validate / verify collects a set from several cartridges, and
how the agent layer and the service layer divide that work. It covers the volume-change state machine, the volume-error
model (reject / retry / skip / end), diagnosis across volume changes, and the stream lifecycle at a volume boundary.

**Status:** describes the implementation after the multi-volume correctness fixes of October 2026. §9 lists those fixes
and the defects they closed.

**Related:** Design-FormatV21 (TOC, set and file identity, F32 "closed by end-of-media"), Design-SetHeaders (SH-*),
Design-Encryption (§8.3 continuation sets, §9.5 locked sets across volumes).

---

## 1. Concepts

| Term | Meaning |
|---|---|
| **Volume** | One cartridge of a series. `TapeTOC.Volume` is 1-based; every volume carries its own media header (if headed) and its own TOC. |
| **Series** | All volumes of one backup history, identified by `TapeTOC.MediaId` (stamped into every media header). |
| **Logical set** | One backup set as the user sees it. It may consist of several **parts**, one per volume. |
| **Continuation part** | A `TapeSetTOC` with `ContinuedFromPrevVolume = true`. It shares the logical set's `SetId`, `FileId` sequence and (if encrypted) envelope and key. Its description is the set's original description plus `(n)`, the part's volume — never a chain of suffixes. |
| **Volume request** | The agent stops and asks for another cartridge: a fresh one (backup) or a specific earlier one (restore). Not a failure. |
| **Volume rejection** | A cartridge was loaded and identified for a continuation, but cannot be used. The operation is still pending. |

### 1.1 Where the TOC lives

The TOC on volume *n* describes **all sets of the series up to and including volume *n***. The last volume's TOC is
therefore complete, and a restore always starts from it. Earlier volumes are loaded only for their content.

`TapeSetTOC.Volume` records which cartridge a set part lives on. `TapeTOC.FirstSetOnVolume` / `LastSetOnVolume` /
`IsCurrentSetOnVolume` answer the "is this set on the loaded cartridge?" question used throughout.

### 1.2 Invariants

| # | Invariant |
|---|---|
| MV-1 | A multi-volume operation is **one operation**: statistics never reset between volumes, and its diagnosis is its earliest genuine failure. |
| MV-2 | A volume request is not a fault. Nothing is latched for it. |
| MV-3 | A cartridge that cannot be used is **rejected**, never re-offered silently. The pending operation survives the rejection. |
| MV-4 | Nothing that belongs to one cartridge may reach another: no stream, no buffered bytes, no packer, no read session survives a media change. |
| MV-5 | A backup writes nothing to a rejected cartridge — in particular, no TOC. |
| MV-6 | `SetId ‖ FileId` is never reused across volumes (rolled-back attempts burn their ids) — required by software encryption (E3). |

---

## 2. Layers and responsibilities

| Layer | Owns | Never does |
|---|---|---|
| **Agent** (`TapeFileBackupAgent`, `TapeFileRestoreBaseAgent`) | the multi-volume **context** (where the operation stands), continuation sets, cartridge identification for restore, the decision *request / reject / done* | ejecting, loading, prompting |
| **Service** (`TapeServiceBase.Backup` / `.Restore`) | the **volume-change loop**: ask, eject, insert, load, verify identity, resume; TOC saves between backup volumes; the result | navigating, judging a cartridge's content |
| **Host** (`ITapeServiceHost`) | the user's answers | any tape I/O (except inserting virtual media) |
| **Stream manager** (`TapeStreamManager`) | the read/write session state; dropping a stale session on a media change | knowing about volumes |

The agent API is deliberately loop-friendly: any caller can drive a multi-volume operation with
`while (!ok && agent.CanResume…) { swap; ok = agent.Resume…(); }` — the test fixtures do exactly that.

---

## 3. The agent's volume states

Both agents expose the same three-state model:

| Property | True when |
|---|---|
| `CanResumeToNextVolume` / `CanResumeFromAnotherVolume` | the agent **asks** for a volume. |
| `IsVolumeRejected` | the cartridge offered for the continuation cannot be used; the operation is still pending. |
| `IsMultiVolumePending` | either of the two. |

`CanResume…` is `false` while a cartridge is rejected. A caller looping on `CanResume…` therefore **ends** on a
rejection, with the rejection as the result, instead of re-offering the same cartridge forever (§9, defect D1). Callers
that want to offer a choice look at `IsVolumeRejected`.

```
                 ┌──────────── Resume… succeeds / last volume done ───────────┐
                 │                                                             ▼
  [running] ──► (volume needed) ──Resume… on a usable cartridge──► [running] … [done]
                 │   CanResume = true
                 │
                 └──Resume… on an unusable cartridge──► (rejected)
                                                         IsVolumeRejected = true
                                                         CanResume = false
                       Resume… again (another / cleaned cartridge) ◄─┤
                       SkipRequestedVolume()  (restore only) ◄───────┤
                       stop calling (end) ◄──────────────────────────┘
```

---

## 4. Backup across volumes

### 4.1 The flow

1. **Volume full.** The packer raises end-of-media — a real EOM, or, with the TOC in the content partition, the logical
  early warning of the TOC reserve (Design-TocReserve). `HandleEom` rolls back the uncommitted files (their ids stay
  burned, MV-6), snapshots the continuation metadata, closes the write session and sets `TOC.ContinuedOnNextVolume`. The
  agent returns false with `CanResumeToNextVolume`; the live `ERROR_END_OF_MEDIA` is **not latched** (MV-2).
- **Full, but complete.** If the early warning comes from the **final** flush and every file is committed, the set is
  complete on this volume: **no continuation** is set up.
- **Full before the set** — the agent asks for the next volume straight away, without writing to this one:
  - F32: on TOC-in-partition media, the previous set ran to end-of-media;
  - the new set's **set header does not fit** ahead of the TOC reserve (`ContinueOnNextVolumeBeforeSet`) — the same
    outcome as a header-less set whose first write is trimmed to nothing.
- **A fresh continuation volume that takes no file** ends the backup with `ERROR_DISK_FULL`: a volume of that size cannot
  continue it, and asking for another would loop (TR-7).

2. **F32 shortcut.** On TOC-in-partition media, a previous set that ran to end-of-media means the volume is full before
   the new set starts. The agent asks for the next volume straight away.

   If a set header that doesn't fit ahead of the reserve then the volume is full before the set → continue on the next volume (as for a trimmed first content write).

3. **Between volumes (service).**
   - Clean up the TOC (an empty trailing part is removed) and save it to the full volume.
   - Ask the user (`OnVolumeFullConfirm`).
   - Eject and insert fresh media (`OnInsertNewMediaConfirm`), then load.
   - **Verify the cartridge is fresh.** This identity check is inverted: a header with the series' own `MediaId` means
     *an earlier volume of this series* and is `WrongVolume`.

4. **Resume.** `ResumeBackupToNextVolume`:
   - consumes the stale live error, and restores the carried latch (§6);
   - renews the navigator, which also drops any stale session (§7);
   - increments `TOC.Volume` and appends the continuation part;
   - runs the file loop from the resume index.

   The new volume is headed by the agent itself, inside `BeginWriteContentForCurrentSet` (§17.10 of Design-SetHeaders).

5. Repeat until the file list is done. The service saves the final TOC.

### 4.2 Rejecting a continuation cartridge

Anything that fails **before the first content byte reaches the new cartridge** rejects it:
- the navigator cannot be renewed;
- `EnsureSetFormat` refuses the continuation;
- the media header, the set header, the encryption session or the content session cannot be opened.

On rejection the agent:
- rolls the continuation part and the volume number back (`UndoContinuationSet`), so the TOC again describes the
  previous volume exactly as saved;
- corrects the set counters (`StatsUndoSet`);
- returns the rejection with `IsVolumeRejected`.

Nothing of the backup is on the cartridge, and **no TOC may be saved onto it** (MV-5). The service must not run its TOC
cleanup and save for this iteration.

A failure *after* the first block was accepted is not a rejection. It takes the write-fault path and ends the set
(known limit, §10).

The media-header write is checked: a volume whose header could not be written receives no content.
`MediaHeaderStamped` is operation-scoped and only ever turns `true`.

### 4.3 Backup has no Skip

A backup can only go on with a cartridge. Its volume-error prompt is offered with `allowSkip: false`, and a returned
Skip is treated as End.

---

## 5. Restore / validate / verify across volumes

### 5.1 The work package

The service assembles the selection (`TapeTOC.SelectFilesFromSets`) into an array indexed **newest set first**
(`filesSelected[0]` = newest). A `null` entry means "all files of that set". The agent walks it oldest-first
(`filesSelectedIdx` counts down), so the tape only moves forward on each volume.

For the selected sets the `TapeRestoreContext` holds:
- the package;
- the newest set's index (`initialCurrSetIdx`);
- the position (`filesSelectedIdx`);
- `ignoreFailures`;
- the notifiable;
- the running `overallSuccess`.

### 5.2 The flow

1. The service opens the **last** volume (complete TOC) and calls `RestoreFilesFromCurrentSetDown`.
2. **The walk.** The agent works through the package. When the next set is not on the loaded cartridge
   (`!TOC.IsCurrentSetOnVolume`), it:
   - stores the context;
   - sets `VolumeToResumeFrom` to that set's volume;
   - ends the read session, so the pipelined reader is not still prefetching during the swap;
   - returns `false` with `CanResumeFromAnotherVolume`.
3. **Between volumes (service).**
   - Ask (`OnVolumeContinueSelect` → Insert / Skip / End).
   - Eject and insert (`OnInsertMediaConfirm`), load.
   - Verify identity against the series and the requested volume (`VerifyRestore`, with Retry).
4. **Resume.** `ResumeRestoreFromAnotherVolume` identifies the cartridge (Design-TocLocation §7):
   - its **media header** — the series' MediaId and the requested volume; a mismatch rejects (`ERROR_WRONG_DISK`);
   - its **own TOC**, located by the TOC ladder with the series TOC at hand (S0–S3) — it must hold the next set as the
     series TOC describes it (file count; SetId, scheme and size on tape for locked encrypted sets);
   - if neither identifies it: the **first block of the needed set** (a set header or 2.1 file header with its SetId);
   - last resort, asked first: a **full-media search** for its own TOC (S4).
     A cartridge whose own TOC does not read is **not** rejected when step 1 or 3 identifies it: the series TOC addresses
     every file; `NoReadableCopy` is recorded as a TOC anomaly. The series TOC is then put back.

   The original TOC is then put back; it addresses the whole series. The walk continues.
5. The walk takes ownership of the context on entry (it clears it), so no volume is "pending" while it runs. Only a
   new volume request re-establishes it. A `break` (abort, or a failure with `!ignoreFailures`) can therefore never
   leave a stale context behind (§9, D4).

#### 5.3 Rejecting a cartridge

A cartridge is rejected only when it is **unidentifiable** (no step of §5.2 succeeds) or **identified as another**
(`ERROR_WRONG_DISK`: another series, another volume, a set that does not match). The context is kept, and
`VolumeToResumeFrom` still names the volume needed. An abort during identification is not a rejection: it ends the
operation and clears the context.

### 5.4 Skipping a volume

`SkipRequestedVolume(out setsSkipped, out filesSkipped)` gives up every set of the package on the requested volume:

| Skip after… | Files counted as | Latched | Typical cause |
|---|---|---|---|
| a volume request (voluntary) | **skipped** | nothing | the cartridge is not at hand |
| a rejection | **failed** | the rejection, named with the volume | the cartridge cannot be read |

Afterwards the agent either asks for the next volume or is done. It always asks — even for the volume the TOC believes
is loaded — because after a skip, the cartridge in the drive is not to be trusted.

The skipped files never pass a file callback. The service syncs the progress handler's figures explicitly
(`SyncStatistics`). A locked set contributes no file count to a skip (its list is hidden); it still counts as a skipped
set.

The skip happens mid-operation, so it lives in the agent, which alone knows its position in the package. The service's
up-front `NoMultivolume` confinement is a different thing: it trims the package before the agent starts.

---

## 6. Diagnosis across volume changes

The agent keeps two error channels:

| Channel | Holds | Lifetime |
|---|---|---|
| **Live error** (`LastError`) | the last operation's outcome — including signals such as `END_OF_MEDIA` or a rejection | until the next operation |
| **Latch** (`LatchedFailure` = `_resultBuilder.FirstFailure`) | the operation's **earliest genuine failure** | the whole operation |

`LastResult` returns the latch, **falling back to the live error** when nothing is latched. That fallback makes three
rules necessary at a volume boundary:

| Rule | Why |
|---|---|
| **Carry the latch only, never `LastResult`.** Use `LatchedFailure` when saving the diagnosis before a volume change. | `LastResult` would promote the live `END_OF_MEDIA` or a previous rejection into the latch (§9, D5). |
| **Restore the latch after public verbs that reset it.** The volume-identification `RestoreTOC()` (restore) and the caller's `BackupTOC()` between volumes (backup) reset the latch. The resume puts it back with `RestoreLatchedFailure`. | Otherwise an early file failure disappears after the swap (§9, D3). |
| **A resume consumes the stale live error first.** `ResumeBackupToNextVolume` starts with `ResetError()`. | Nothing on a successful backup resume clears the live error again, so a previous rejection would outlive the successful retry (§9, D6). Restore clears it incidentally through `RestoreTOC()`. |

Restore failures on resumed volumes latch like any other: the walk owns the context (§5.2 step 5), so the latch is no
longer gated on "no context pending".

A rejected cartridge that a retry replaces leaves **no trace** in the final diagnosis. A rejection the user gives up on
— by ending, or by skipping after an error — **is** the diagnosis.

---

## 7. Streams and sessions at a volume boundary

### 7.1 The stream lifecycle

A `TapeStream` (TOC reads and writes; content streams in tests) is produced by `TapeStreamManager` and must **sign
out** (`OnDisposeStream`) on disposal. Signing out is what writes the TOC filemark and lets the manager leave its
`ReadingTOC` / `WritingTOC` state.

`TapeStream.Dispose` runs three steps:

| Step | What | On failure |
|---|---|---|
| 1 | `CompleteOnDispose()` — the write stream's final flush | captured, not thrown yet |
| 2 | sign out + release the buffer | **always runs** |
| 3 | rethrow the captured failure (original stack) | — |

The exception still reaches the caller, so a failed TOC copy is reported as failed. By then, the stream is gone and the
manager is clean.

**A failed write faults the stream.** The unwritten bytes are discarded and `WriteFailed` is set. Disposal therefore
neither retries them nor writes a filemark behind the broken file, and further `Write` calls throw.

### 7.2 Dropping a stale session on a media change

`TapeStreamManager.RenewNavigator`, which both agents call right after a swap, first calls `AbandonReadWrite()`:
- a stream still in use, or an active packer, logs a warning — if one fires, a session leaked across a volume change; a
  state left in a finished TOC write is closed lazily and only traced;
- the read packer is released;
- the state is forced back to `MediaPrepared`.

Every branch logs a warning. With §7.1 in place none should fire; if one does, a session leaked across a volume change.

This is defence in depth for MV-4. The manager's ordinary `EndReadWrite()` (force-dispose a stream in use) does tape
I/O and is correct **within** a cartridge; it must never be the thing that cleans up **after** a media change.

---

## 8. The service's volume-change loop

### 8.1 Host callbacks

The host is asked at three volume-level stages, plus the "go on?" prompts:

| Stage | Callback | Returns | Asked when |
|---|---|---|---|
| Continue (restore) | `OnVolumeContinueSelect(volume, mode)` | `VolumeAction` | the agent asks for an earlier volume |
| Continue (backup) | `OnVolumeFullConfirm(…)` | `bool` | the volume is full |
| **Load** | `OnMediaLoadRetryConfirm(error, again)` | `bool` | the drive cannot load the cartridge |
| **Identity** | `OnMediaMismatchConfirm(header, verdict, context, …)` | `MediaMismatchChoice` | the header says it is the wrong cartridge |
| **Content** | `OnVolumeErrorSelect(volume, error, operation, allowSkip)` | `VolumeAction` | loaded and identified, but unusable |

`VolumeAction` is `Insert` (insert the volume again, or another copy, and continue), `Skip` (go on without it; restore
only) or `End`.

**Unattended defaults:**
- `OnVolumeContinueSelect` returns **End**, as the former yes/no did.
- `OnVolumeErrorSelect` returns **End**, logged: a retry would only fail again, and a skip silently gives up data.

### 8.2 Restore loop

```
while (!aborted && !ok && agent.IsMultiVolumePending && !NoMultivolume)
    action = agent.IsVolumeRejected ? OnVolumeErrorSelect(v, error, op, allowSkip: true)
                                    : OnVolumeContinueSelect(v, mode)
    End    → break
    Skip   → agent.SkipRequestedVolume(); sync statistics; continue
    Insert → eject · insert · load (retry) · verify identity (retry) · agent.ResumeRestoreFromAnotherVolume()
```

If the operation ends on a rejected volume, the result is marked failed, even when no file counter shows a failure
(nothing of that volume was started).

### 8.3 Backup loop

```
do
    result = agent.IsMultiVolumePending ? agent.ResumeBackupToNextVolume() : agent.Backup…()
    if agent.IsVolumeRejected
        report "nothing was written — volume #n and its TOC are intact"
        OnVolumeErrorSelect(n+1, error, "Backup", allowSkip: false) → Insert ? swap to fresh volume, continue : end (failed)
        // no TOC cleanup, no TOC save (MV-5)
    TOC cleanup · TOC save (enforce · emergency export) · per-volume report
    done / aborted / NoMultivolume / user declines → break
    swap to fresh volume (eject · insert new · load · verify freshness)
```

The "swap to a fresh volume" steps are one local function, `SwapToFreshVolume`. It is shared by the normal continuation
and by the retry after a rejection.

If the user ends the series before every file is written (declines `OnVolumeFullConfirm`, or cancels the insertion), the
result is **failed**, naming the files left out ("Backup ended after volume #n — k file(s) not backed up"). Only
`NoMultivolume` ends after one volume successfully, because the caller asked for exactly that.

---

## 9. Correctness fixes (October 2026)

| # | Defect | Effect | Fix |
|---|---|---|---|
| D1 | A failed `ResumeRestoreFromAnotherVolume` (unreadable TOC, wrong volume, …) left the context unchanged | every caller looping on `CanResume…` re-offered the same cartridge **forever**; the service never said why | the three-state model, §3 |
| D2 | `ResumeBackupToNextVolume` leaked its context when the navigator could not be renewed or the format check refused; a fresh cartridge refusing the first write ended the backup | the service saved a TOC onto an unusable cartridge, or repeated volume increments; no chance to offer another cartridge | rejection with rollback, §4.2; MV-5 |
| D3 | Restore failures on resumed volumes were not latched; the identification `RestoreTOC()` reset the latch | an early failure vanished after a swap: "operation did not complete" | the walk owns the context; latch restored, §5.2, §6 |
| D4 | A `break` during a resumed walk left the old context set | a stale, out-of-date continuation | the walk clears the context on entry, §5.2 |
| D5 | The diagnosis was carried as `LastResult` | the live `END_OF_MEDIA`, or a previous rejection, was promoted into the latch | carry `LatchedFailure`, §6 |
| D6 | The backup resume never cleared the live error | a retried rejection lingered in `LastResult` | the resume consumes it, §6 |
| D7 | A media-header write failure was unchecked; `MediaHeaderStamped =` reset the flag per volume | content written behind a missing header | checked; `|=`, §4.2 |
| D8 | A write stream whose final flush failed never signed out | the manager stayed in `WritingTOC` across the swap; the **stale TOC tail was flushed onto the next cartridge**; a failed 1st TOC copy handed its broken stream to the 2nd | §7.1, §7.2 |
| D9 | A restore ended on a rejected volume reported success | no file counter showed the failure | result marked failed, §8.2 |
| D10 | `OnVolumeContinueConfirm` offered only yes/no | a missing cartridge ended the whole restore | `OnVolumeContinueSelect` with Skip, §5.4 |
| D11 | A user-ended series reported success | 12 of 16 files written, "completed successfully" | failed, naming the files left out, §8.3 |
| D12 | The final flush's early warning with every file committed set up a continuation | the resume indexed past the file list (crash) | no continuation, §4.1 |
| D13 | A set header refused by the TOC reserve failed the backup | "the operation completed successfully" on a full volume | the set continues on the next volume, §4.1 |
| D14 | Continuation names chained suffixes | "First (2) (3)" | original description + `(n)`, §1 |
| D15 | A reload kept the previous cartridge's cached content capacity | stale capacity with mixed cartridge sizes | `ReloadMedia` clears it |

### 9.1 Regression coverage

| Suite | Layer | Pins |
|---|---|---|
| `MultiVolumeResumeTests` | agent | D1 (unreadable TOC, wrong volume), skip after error and voluntary, D3, D5, D6, backup rejection with rollback and retry |
| `ServiceMultiVolumeErrorTests` | service | Skip at the continue prompt; unreadable volume → End / Insert / Skip; backup refused cartridge → End (nothing on vol 2) / Insert. Every operation under a time bound, so a loop regression fails instead of hanging |
| `TapeStreamDisposalTests` | stream manager | D8: a failed flush still signs out; no stale write on the next transition; a fresh 2nd TOC stream; a faulted stream disposes quietly; abandon on a media change |
| `MultiVolumeBackupRestoreTests`, `ServiceMultiVolumeTests`, `MultiVolumeEncryptionTests` | both | the normal flows over profiles and header modes; encrypted continuation (shared envelope and key, fresh FileIds, locked TOCs across volumes) |
| `MultiVolumeBackupRestoreTests` | agent | D12 (`TwoSets_DifferentVolumes_…`), D13 (`SecondSet_OnAVolumeFullToTheReserve_…`), D14; parity of both TOC copies per volume |
| `TocReserveAgentTests` | agent | D15 (mixed 2×/1× volume sizes, both orders); TR-7 no loop |
| `ServiceMultiVolumeErrorTests` | service | D11 (`Backup_SeriesEndedByUser_…`); unidentifiable vs. merely TOC-less volumes (§5.3) |
| `MultiVolumeEncryptionTests` | agent | D13 on encrypted incrementals |

---

## 10. Known limits

- **Late failure on a fresh cartridge.** A continuation cartridge that fails *after* its first block was accepted takes
  the write-fault path and ends the set. It is not rejected: that would need the packer's rollback state to prove
  nothing was committed.
- **Log lines of a rejected attempt.** On a backup rejection the progress handler has already logged the attempted
  set's "starting… / complete: 0 succeeded". The counters are corrected; the two lines stay.
- **Locked sets in a skip.** A skipped set that is locked contributes no file count (its list is hidden).
- **Mixed-header series.** Header presence is resolved per volume (the navigator is renewed per volume). A backup rejection
  that relies on the media-header write being the first write does not apply to a headerless continuation volume, where
  the first write is content. A volume whose media header is damaged and that S4 identified is read without set-header
  verification (Design-TocLocation §12.5.2).

---

## 11. Code map

| Concern | Where |
|---|---|
| Backup context, EOM handling, resume, rejection | `TapeFileBackupAgent`: `TapeBackupContext`, `HandleEom`, `ResumeBackupToNextVolume`, `UndoContinuationSet`, `RejectVolume` |
| Restore context, walk, identification, skip | `TapeFileRestoreBaseAgent`: `TapeRestoreContext`, `RestoreFilesFromCurrentSetDownCore`, `ResumeRestoreFromAnotherVolume`, `CheckVolumeToResumeFrom`, `SkipRequestedVolume` |
| Shared helpers | `TapeAgentBase.MultiVolume`: `LatchedFailure`, `RestoreLatchedFailure`, `StatsUndoSet`, `StatsAddUnprocessed` |
| Continuation sets | `TapeTOC.AddContinuationSetTOC`, `TapeSetTOC.ToParams` |
| Service loops | `TapeServiceBase.Backup` (`ExecuteBackupCore`, `SwapToFreshVolume`), `TapeServiceBase.Restore` (`ExecuteRestoreCore`) |
| Host contract | `ITapeServiceHost`: `VolumeAction`, `OnVolumeContinueSelect`, `OnVolumeErrorSelect`, `OnVolumeFullConfirm`, `OnInsert…`, `OnMediaLoadRetryConfirm`, `OnMediaMismatchConfirm` |
| Stream lifecycle | `TapeLibNET.Streams.TapeStream` (`Dispose`, `CompleteOnDispose`, `Abandon`), `TapeStreamManager` (`OnDisposeStream`, `RenewNavigator`, `AbandonReadWrite`) |
| Test drivers | `MultiVolumeVirtualTapeFixture` (agent level), `MultiVolumeTapeServiceHost` (service level) |
| Full volume before / at the end of a set | `TapeFileBackupAgent`: `ContinueOnNextVolumeBeforeSet`, the final-flush handler in `BackupFilesToCurrentSet` |
| Volume identification | `TapeFileRestoreAgent.CheckVolumeToResumeFrom`, `TapeAgentBase.TocLocation` (ladder, S4) |
| Test invariants | `TocInvariants`, `MultiVolumeVirtualTapeFixture.VolumeCapacity` |