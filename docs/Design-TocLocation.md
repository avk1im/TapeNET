# Design — Locating the TOC (rugged TOC location, TOC anomalies, one block classifier)

**Version:** 6 — as implemented (Phases 0–6, with the TOC-reserve detour). This document describes the design as built;
§12 records how it got there. Section and invariant numbers are stable: code comments reference them.

**Siblings:** Design-TocReserve (the TOC's room on tape, TL-8), Design-MultiVolume (series, volumes, resume),
Design-FormatV21 (records, peeks), Design-Encryption (sealed sets, the plain stub).

## 0. Why

TapeNET kept the TOC where each layout puts it and found it by counting marks. One missing mark — a closing filemark
refused at end-of-media, a setmark lost to a power cut — sent the count somewhere else, and nothing checked where it
landed. Worse, when the marks ran out, a fallback declared the medium blank: a TOC read then parsed the media header as a
TOC, and an append wrote over the first set (§1, R2).

The design replaces "count and trust" by **"look before you trust"**: every position the TOC search or an append relies
on is identified first, from the block itself. Positions come from cheap hints — the layout, the marks, a block number
recorded in the TOC — and identification decides. The cost escalates only as far as the damage requires, and the one
expensive step, reading the whole medium, is asked first.

Everything that deviates on the way is recorded as a **TOC anomaly** — mirroring set anomalies — and the service turns
the anomalies into advice by cause.

### 0.1–0.8 *(placeholders — numbering kept)*

The v4 proposal's review notes (§0.1 FirstBlockAfterSet, §0.2 EOM-closed sets, §0.3 per-set values, §0.4 advice on the
reserve, §0.5 anchors, §0.6 anomalies mirroring set anomalies, §0.7 the notifiable hierarchy, §0.8 retained choices) are
now described where they are implemented: §6, §6, §6, §5.4 and Design-TocReserve, §4.2, §5.1, §5.2, §3. The pre-flight
check of §0.4 was dropped in favour of Design-TocReserve §9 (the TOC partition budget); the session landmarks of §0.5
were dropped (§4.3).

## 1. Evidence

### 1.1 Three traces (the originating failures)

Content capacity 262 144 B, a fixed 32 KiB TOC reserve, 16 KiB blocks, a media header. Marks take no space on the
virtual media and are refused once EOM is set.

| # | Layout | Volume 1 as written | Restore of volume 1, before |
|---|---|---|---|
| T1 | FilemarksOnly | `[MH][FM][set][FM][TOC1]` — copy 1 complete, its closing FM refused at EOM, copy 2 failed | failed: back 3 FMs → BOM → "blank" → media header read as TOC |
| T2 | TOC mark | `[MH][FM][set][FM][gap][FM×3][TOC1]` — copy 1, no closing FM | succeeded: the forward search finds the TOC mark |
| T3 | TOC mark | `[MH][FM][set][FM][gap][FM×3][FM]` — no TOC data at all | failed: nothing to find |

All three now restore: T1 by the filemark tail check (§12.0) or S2; T3 by resume identification (§7).

### 1.2 Findings

| # | Finding | Kind | Resolved by |
|---|---|---|---|
| R1 | TOC location by pure mark arithmetic | design | the ladder, §4 |
| R2 | "No marks → blank" claimed success for a TOC search and for an append's end of content | **data-loss bug** | §8.1, §12.0 |
| R3 | No position identified before it is trusted | design | TL-1, §3 |
| R4 | Retries repeated the same arithmetic | design | §4 |
| F1 | An empty TOC copy reported as written | **data-integrity bug** | §8.2 |
| F2 | The early-warning reserve clamped the TOC write itself | bug | §8.2 |
| F3 | The reserve was anchored without the media header | bug | §8.2 |
| F4 | A layout's TOC footprint never checked against the reserve | test-geometry trap | §8.2, Design-TocReserve |
| F5 | `TapeWriteStream.Write` looped forever on a 0-byte write without error | latent hang | §8.2 |
| F6 | A volume whose TOC is lost is fully restorable through the series TOC, yet resume insisted on its own TOC | design | §7 |
| F7 | A forward search from a unique anchor survives a missing trailing mark; backward counting does not | insight | §4.2 |
| F8 | Nothing recorded one-copy volumes or fallbacks | observability | §5 |

## 2. Invariants

| # | Invariant |
|---|---|
| TL-1 | **Look before you trust.** No stage claims "at the TOC" unless the block there identifies as a TOC copy. |
| TL-2 | **Mark arithmetic, anchors and recorded block numbers are hints, never proofs.** Identification decides. |
| TL-3 | **Never write at a guessed position.** An append starts at the recorded end of content when the tape confirms it, at the marks' end of content when both agree, at begin-of-content only on positive blank evidence — otherwise it is refused (§8.1). |
| TL-4 | **Bounded, escalating cost.** S0–S3 run unasked; S4 reads the whole medium and is asked first. |
| TL-5 | **Choose the right copy:** expected series, expected volume, a plausible end of content, then the newest save, then copy 1, then position — all from the peek. |
| TL-6 | **Every deviation is a TOC anomaly; every settlement a recovery event.** A clean path records none. |
| TL-7 | **A write that wrote nothing is a failure**, reported with the drive's own error when it has one. |
| TL-8 | **The TOC's room serves the TOC:** the reserve (TOC in set) and the budget (TOC in partition) follow the TOC, are armed for content writes only, and TOC writes bypass them — Design-TocReserve. |
| TL-9 | **One classifier, one walker.** Every block identification ends in `TapeHeaderBlock.IdentifyBlock`; every multi-block walk is `TapeScanner`. |
| TL-10 | **Per-volume facts are looked up by the identified volume**, never by `TOC.Volume` assumed (§8.1 explains the append guard's case). |
| TL-11 | **Partition-explicit positions.** Every absolute move names its partition: `TapeDrive.MoveToBlock(partition, block)`. |

## 3. One block classifier

### 3.1 The peeker contract

`ITapeBlockPeeker<TPeek>` (`Headers/TapeBlockPeek.cs`, internal): one static abstract `TryPeek(ReadOnlySpan<byte>, out
TPeek?)` returning a `TapeFrameStatus`. Implemented **explicitly** by each type that owns a record format, so the only
caller is the dispatcher, through `TapeBlockPeek.Run<TPeeker, TPeek>`.

- **Total:** never throws, whatever the bytes. `Run` deliberately has no guard: a breach must surface in the totality
  tests, not be hidden.
- **Status, not bool:** `Ok`, `NotFramed` (not this kind), `CrcMismatch` / `Unparseable` (this kind, damaged or newer).
- **2.1 only:** blocks without the `TpN#` magic go to the frozen legacy probe; a peeker may assume the magic.

| Implementer | `TPeek` | Delegates to |
|---|---|---|
| `TapeTOC` | `TapeTocPeek` | its `TocHeader` record, parsed within the block |
| `TapeFileHeader` | `TapeFilePeek` | `TapeFrame.TryUnpack` (CRC-64 verified) |
| `TapeHeader` | `TapeHeader` | `TapeFrame.TryUnpackWithLegacy` (media, set, calibration) |
| `TapeCalibrationCheckpoint` | itself | `TapeFrame.TryUnpack` |

Legacy blocks need no peeker: `LegacyIdentify.IdentifyBlock` is the frozen probe, called directly (§3.2).

### 3.2 The dispatcher

`TapeHeaderBlock.IdentifyBlock(byte[], int)` (`Headers/TapeHeaderBlock.Identify.cs`) — pure, total, TOC-free:

1. No `TpN#` at byte 0 → `LegacyIdentify.IdentifyBlock`: a legacy TOC (structural probe), an intact legacy header, a
   damaged legacy record, or foreign.
2. A prologue that does not parse → `DamagedRecord`.
3. By record kind:

| Kind | Outcome |
|---|---|
| `TocHeader` | `TocCopy` with its `TapeTocPeek`, else `DamagedRecord` |
| `FileHeader` | `FileHeader` with its `TapeFilePeek` when the frame verifies, else **`Foreign`** — a file body may start with the magic by chance, so an unverified frame is no evidence of damage |
| `CalibrationCheckpoint` | `CalibrationCheckpoint`, else `DamagedRecord` |
| any other | `Header`, else `DamagedRecord` (an intact frame of an unknown kind or newer major: `Unparseable`) |

A record whose magic is damaged cannot be told from foreign data; one damaged behind its magic is ours, damaged — never a
phantom TOC.

### 3.3 Types

| Type | Shape |
|---|---|
| `HeaderBlockIdentity` | `Foreign = 0, Header, DamagedRecord, TocCopy, CalibrationCheckpoint, FileHeader` — appended only |
| `TapeTocPeek` | `(ushort Version, Guid MediaId, bool IsLegacy, int? Volume, DateTime? LastSaveTime, int? SetCount, byte? CopyIndex, byte? CopyCount, long? FirstBlockAfterContent)` — the nullable fields are null for legacy copies and for 2.1 copies that do not carry them |
| `TapeFilePeek` | `(Guid SetId, ulong FileId, string Name)` |
| `IdentifiedBlock` | `(Kind, TapeHeader? Header, TapeFrameStatus FrameStatus, TapeTocPeek? Toc, TapeFilePeek? File)`; typed accessors `TryGetHeader<T>`, `TryGetToc`, `TryGetFile`; convenience views `TocVersion`, `TocMediaId` (through `TryGetToc`) |

**Projections** — one-line views of the dispatcher: `TapeHeaderBlock.Classify(buffer, length)` (the intact header, or
null), `TapeTOC.TryPeek(block, length, out version, out mediaId)` (internal), `TapeHeaderBlock.Read(…, out TapeHeader?)`.

### 3.4 One I/O primitive

`TapeHeaderBlock.ReadAndIdentify(drive, out buffer, out read, out tapemark, out eod)` (internal) reads **one** block at
the current position and identifies it. Shared with `Read` through `ReadBlock`, which sets and restores the block size
and reports marks and end-of-data from the drive's flags.

- **One block, at the size our records are written at:** 16 KiB, or the drive's maximum where that is smaller. Never
  several blocks — a read must not cross a mark.
- **A block of another size is a finding**, not a fault: the drive delivers it with `ERROR_INVALID_BLOCK_LENGTH` and the
  head past it; it may be a set's first block (a file header at its front), so it is identified and the error cleared.
- **Moves the head** past the block or the mark.

Used by the scanner's walk, the ladder's S3, set-header verification and resume identification.

### 3.5 Contract tests

`BlockIdentificationTests` (Headers): every peeker total under random bytes, every truncation of a valid record and
single-bit flips; the dispatcher agrees with the peeker it delegated to; the projections agree with the dispatcher.

## 4. The TOC location ladder

`TapeAgentBase.RestoreTOCFromMedia(expectedVolume, notify, seriesToc, fullWalkOnly)` (`Agents/TapeAgentBase.TocLocation.cs`);
`RestoreTOC(notify)` is the plain-load form (no expectation).

### 4.1 Stages

| Stage | Name | What | Asks? |
|---|---|---|---|
| **S0** | Native | the layout's own TOC position (`Navigator.MoveToBeginOfTOC`), made honest (R2): fail instead of "blank" | no |
| **S1** | Sibling | the 2nd copy — sequentially behind copy 1, then directly by one filemark from the TOC position | no |
| **S3** | Expected position | TOC in set, with a series TOC: `MoveToBlock(content, seriesToc.FirstBlockAfterContentOnVolume(v))` — exact; on the TOC-mark layout past the gap and three filemarks; the copy there and the one after it | no |
| **S2** | Anchor walk | `TapeScanner.WalkFrom` from an anchor (§4.2), stopping after four TOC copies | no |
| **S4** | Full walk | `TapeScanner.WalkFrom(content, 0)` to end-of-data, every copy collected | **yes** — `OnTocFullWalkConfirm` |

**Order:** S0, S1, then S3, S2, S4. S3 runs before S2: one locate, exact. Each candidate is:

1. **identified** (`ReadAndIdentify` / the scanner) as a `TocCopy`, with its peek;
2. **filtered** (`RejectionOf`, TL-5) — only against what is expected: another series (the series TOC's `MediaId`, a
   resume), another volume (the requested one), and — for copies in the content partition — an end of content that lies
   after the copy itself;
3. **ranked** — newest `LastSaveTime`, then `CopyIndex`, then block;
4. **read** in full (`RestoreTOCAt`, CRC-64) until one parses.

A copy that peeks but does not parse contributes nothing; the next candidate is read. Rejected copies are recorded as
`ForeignCopySkipped`.

**When S4 does not run:** on a multi-volume resume's ladder (a series TOC is at hand: it addresses every file, and the
volume is identified otherwise — §7, where S4 is the separate last resort), and when an S2 walk already started at content
block 0.

**On partitioned media:** S0 and S1 at initiator block 0; S3 does not apply to the TOC (the recorded end describes the
content partition); S2 walks the whole initiator partition; S4 walks the content partition. A blank initiator partition
fails with "the TOC partition is blank" in the diagnosis.

**An abort** — the agent's flag, or a progress callback throwing `TapeAbortRequestedException` — stops the search at a
block boundary with `ERROR_CANCELLED` and no anomaly.

### 4.2 Anchors (S2)

`Anchors(expectedVolume, seriesToc)`, lazily, cheapest and most specific first; each only a hint (TL-2):

| Layout | Anchor |
|---|---|
| Partitions | block 0 of the initiator partition — the whole TOC partition |
| any, with a series TOC | the start of this volume's last set: the second-last set's `FirstBlockAfterSet` |
| Setmarks | from end-of-data back one setmark and forward over it — just after the last setmark; walked with `NoSetmarksAhead` (every fragment closed by a filemark) |
| filemark layouts (with or without TOC mark) | the deepest of five…one filemarks back from end-of-data that is reachable |

An anchor equal to S3's block starts one file later. A setmark tape whose last setmark is gone has no layout anchor; S4
remains.

### 4.3 Session landmarks *(placeholder — dropped)*

A session landmark log was designed as a further anchor. Dropped: it helps only unhealthy media, every write would have
to invalidate it, and S2/S4 already cover those cases.

### 4.4 The walker

`TapeScanner.WalkFrom(partition, block, progress)` (internal) walks one partition from a position to its end of data with
Scan Media's identification and mark handling. `ScanMediaOptions` for the TOC search:

| Option | Ladder value | Meaning |
|---|---|---|
| `HarvestTocCopies` | false | peek only; the ladder reads the winner |
| `StopAfterTocCopies` | 4 (S2), 0 (S4) | bound the walk |
| `NoSetmarksAhead` | from the anchor | close every fragment by a filemark |
| `SetmarkFallbackToFilemark` | true | a set whose closing setmark is lost does not end the walk before the TOC |
| `AbortPoll` | `() => IsAbortRequested` | stop between reads |

TOC fragments carry their `TapeTocPeek` (`TapeMediaFragment.TocPeek`). Scan Media keeps its defaults (harvest, report a
set without closing setmark as never completed).

## 5. Anomalies and notification

### 5.1 TOC anomalies, mirroring set anomalies

`Agents/TapeTocAnomaly.cs`:

| `TapeTocAnomalyKind` | Meaning |
|---|---|
| `CopyWriteFailed` | one copy could not be written — or its closing filemark did not fit; the other copy was |
| `NoCopyWritten` | no copy reached the tape: the volume carries no TOC |
| `NotFoundNatively` | the layout's TOC position could not be reached; also a volume found without a TOC while locating end of content |
| `CopyUnreadable` | a copy was found but did not read (torn, CRC) |
| `ForeignCopySkipped` | a copy of another series or volume, or implausibly placed, was skipped |
| `NoReadableCopy` | the TOC position was reached, but no copy there read |
| `RecordedEndDisputed` | an append was refused: the tape disputes the recorded end of content (§8.1) |

| `TapeTocAnomalyStage` | |
|---|---|
| `Detected` | the detection |
| `Sibling` | settled by the other copy (S1) |
| `ExpectedPosition` | settled at the expected position (S3) |
| `AnchorWalk` | settled by the S2 walk |
| `FullWalk` | settled by the S4 walk |
| `Retried` | settled by a later attempt on the same volume within the operation — an enforced rewrite, a re-inserted cartridge whose TOC then read, a TOC written to a volume found without one, or a TOC written over a damaged copy (the append cross-check) |

`TapeTocAnomaly(Kind, Stage, Volume, CopyIndex, Partition, Block, CanAttemptRecovery, Diagnosis)` — a readonly record
struct; `IsRecovery`, `Summary` (every kind worded), `RecoverySummary`.

`TapeTocStatistics` (in `TapeFileStatistics.TocStats`): `CopiesWritten`, `CopyWriteFailures`, `VolumesWithoutToc`,
`AnomaliesDetected`, `AnomaliesRecovered`, `AnomaliesRecoveredByWalk` (S2 and S4 recoveries); `HasAnomalies`.

**Rules** (`Agents/TapeAgentBase.TocAnomalies.cs`):
- **Once per volume:** the detection callback and `AnomaliesDetected` count a volume once; every finding joins the trail
  (`TapeAgentBase.TocAnomalies`).
- **Raised once the outcome is known:** a TOC anomaly asks nothing, and the volume is known only from a copy that read
  (TL-10) — copy 1's failure is raised after copy 2 decided.
- **Settled by a later success** (`ResolveTocAnomalies`): what is still open on that volume, among the kinds the success
  makes good, is reported as a recovery with the stage. A copy write failure is settled only by two complete copies; a
  lost TOC by any readable one. `ForeignCopySkipped` is never settled; `RecordedEndDisputed` is not settled within the
  operation — the dispute stays on tape until someone looks.
- **A recoverable detection** (`CanAttemptRecovery`) is reported at Warning, not Failed.

### 5.2 Notifiable hierarchy

`Agents/ITapeFileNotifiable.cs` — linear, every member defaulted, so every implementer compiles unchanged:

| Interface | Members |
|---|---|
| `ITapeTocNotifiable` | `OnTocAnomaly`, `OnTocAnomalyRecovered`, `OnTocFullWalkConfirm(volume)` (default: proceed — read-only, unattended runs must not stall), `OnTocSearchProgress(in TapeScanProgress)` (may throw `TapeAbortRequestedException`) |
| `ITapeSetNotifiable : ITapeTocNotifiable` | `OnSetAnomaly` (default Abort), `OnSetAnomalyRecovered` |
| `ITapeFileNotifiable : ITapeSetNotifiable` | `SetStart` … `OnFileLost` |

| Verb | Notifiable |
|---|---|
| `BackupTOC(enforce, tocNotify)`, `RestoreTOC(tocNotify)`, `BackupInitialTOC(writeHeader, tocNotify)` | `ITapeTocNotifiable?` |
| `TapeSetAgent.DeleteSetsFromCurrentSetUp(navigateFromBegin, setNotify)` | `ITapeSetNotifiable?` |
| backup, restore | `ITapeFileNotifiable?` |

**Service:** every TOC read the service starts on its own (`RestoreTOCAsync`, `IdentifyMediaAsync`, the restore's
initial read) runs with a `ServiceTocProgressHandler` (factory `CreateTocProgressHandler`, wired by
`CreateTocProgressHandlerFor`). The S4 question is a media prompt — `PresentVerdict(TapeMediaVerdict.Unidentified,
MediaPromptContext.FullWalkForTOC)` through `ConfirmFullWalk` — no new host API; unattended: Proceed. After the user said
"Search" to `SearchForTOC` on load, it is not asked again.

### 5.3 Lifetime across volumes

The records belong to the **operation**. `ClearAnomalies` runs where an operation starts — backup, restore, delete —
together with the set records. The TOC verbs never clear: the backup writes the TOC between volumes and the restore reads
each cartridge's TOC through them. A fresh agent starts empty, so the stand-alone TOC verbs (load, rename, format) are
operations of their own. Every record names its volume, so the final report covers the whole operation.

### 5.4 Service advice: by cause and layout

`TapeServiceBase.AdviseOnTocAnomalies(anomalies, placement)` → `(TocAnomalyAdvice, volumes)`; worded by `AdviseTocText`;
reported by `ReportTocAnomalyOutcome(agent)` after the set advice, in backup, restore, identify and delete. Ranked:

| Advice | When | Level / action |
|---|---|---|
| `TocLost` | `NoCopyWritten`, unsettled | Failed — export the TOC to a file (Help: TOC space) |
| `TocUnreadable` | `NotFoundNatively` / `NoReadableCopy`, unsettled | Failed — recover with Scan Media, or import a TOC file |
| `EndOfContentDisputed` | `RecordedEndDisputed` | Failed — Scan Media and adopt the recovered TOC, or reload the TOC; retry |
| `SingleCopyReserve` | `CopyWriteFailed` at EOM, TOC in set | Warning — export the TOC; calibrate the drive (Help: TOC space) |
| `TocPartitionFull` | `CopyWriteFailed` at EOM, TOC in partition | Warning — export the TOC; fewer files per set or volume (Help: TOC space) |
| `SingleCopyDamaged` | `CopyUnreadable` not replaced by a write, or `CopyWriteFailed` for another reason | Warning — re-save the TOC; retire the cartridge if it recurs |
| `TocFoundBySearch` | settled by S3/S2/S4, unless this operation wrote the TOC to that volume since | Warning — re-save the TOC so it is found directly again |
| `None` | nothing open | silent |

**Two settlements of a damaged copy:** settled by its sibling (read path), it is still on tape → re-save; settled by a
write on that volume (`Retried`), it was replaced → no advice.

A full TOC partition that ends a volume early is no TOC anomaly: it is a volume end, advised by the volume-space advisor
(Design-TocReserve §9.6).

## 6. Format additions (2.1, optional, non-critical)

| Record | Field | # | Type | Meaning | Absent |
|---|---|---|---|---|---|
| `TocSet` | `FirstBlockAfterSet` | 15 | varuint, non-negative | first block after the set's closing mark | −1 |
| `TocSealedSet` (plain stub) | `FirstBlockAfterSet` | 9 | same | same — readable while the set is locked | −1 |
| `TocHeader` | `CopyIndex` | 7 | byte | 1 or 2 | 0 |
| `TocHeader` | `CopyCount` | 8 | byte | copies the save intended — always 2 | 0 |
| `TocHeader` | `FirstBlockAfterContent` | 9 | varuint, non-negative | the copy's own volume's end of content — for the peek | −1 |

- **−1 never reaches the wire:** elided on write, applied on read when absent; any other negative value is refused.
- **Captured by the stream manager** (`TapeStreamManager.LastSetFirstBlockAfter`, `Drive.CurrentBlock` right after the
  closing mark; −1 when no mark closed the set — end-of-media (F32) or a failed mark), **stamped by the agent** at the
  places it copies `LastSetClosedByEom`. `EndWriteContent` rethrows on end-of-media and write faults after closing the
  set, so code behind it in the agent would miss exactly those paths.
- **Per volume:** `TapeTOC.FirstBlockAfterContentOnVolume(volume, beginOfContent = −1)` — the volume's last set's value;
  a trailing vacant set without one is looked past; a set holding files without one makes the answer −1 (never guessed
  from an earlier set); a volume without sets answers `beginOfContent`.
- **The stub is authoritative** for encrypted sets: the sealed content carries field 15 too, unchecked, because a locked
  set may be restamped (trailing delete) while its sealed bytes are re-emitted verbatim.
- Part of the set's persisted state: a change restamps `LastSaveTime` and re-seals an unlocked encrypted set. Not carried
  into a continuation part. A trailing delete stamps the retained last set with the new end (only when it differs).
- **All copies of one save carry one save time** — copy 1's — or the ranking (TL-5) would always prefer the last copy.
- The goldens were regenerated once.

## 7. Volume identification for a multi-volume resume

`TapeFileRestoreBaseAgent.CheckVolumeToResumeFrom(volume)`, by the strength and cost of the evidence:

| Step | Evidence | Verdict |
|---|---|---|
| 1 | **Media header** at BOM: series and volume | a mismatch rejects (`ERROR_WRONG_DISK`) |
| 2 | **Own TOC** through the ladder S0–S3 (expected volume, the series TOC: S3, the series anchor, the `MediaId` filter) | read → must hold the next set as the series describes it (`SameSetOnTape`: count for plain sets; SetId, scheme, size on tape for encrypted ones) — else reject. Unreadable, but step 1 identified the cartridge → **restore**, with the TOC anomaly on record |
| 3 | **The set's first block** — located by the previous set's `FirstBlockAfterSet`, else by counting marks; for the first set on the volume, at BOM and past the first filemark | a set header or a 2.1 file header with the expected SetId → restore |
| 4 | **The whole medium** (S4), asked first | a copy found must pass step 2's checks |

A cartridge is **rejected** only when nothing identifies it, or something identifies it as another. The series TOC is
put back whatever the outcome; files stay guarded by their own headers (`CheckFileHeader`: SetId and FileId). Legacy and
encrypted sets cannot be identified by their first block (no SetId / sealed file headers); encrypted volumes are found by
S4 through the plain stub. A volume identified by S4 whose media header did not read is read without set-header
verification (presence unknown).

## 8. Write side

### 8.1 End of content without guessing (TL-3)

An append on TOC-in-set media (`TapeFileBackupAgent.BeginWriteContentForCurrentSet` with a new set behind others):

1. **The recorded end:** `TOC.FirstBlockAfterContentOnVolume(TOC.Volume)`, checked against the tape by
   `Navigator.CheckRecordedEndOfContent`:

| The tape at the recorded block | Verdict |
|---|---|
| the layout's closing mark right before, the TOC area (a copy, the TOC mark, or end-of-data) at it | **Confirmed** — the head stands there; no mark search |
| the read failed | **Unverifiable** |
| something else | **Disputed** — no believed position |
| nothing recorded (−1), or TOC in partition | **NotApplicable** |

2. **Unverifiable or disputed:** the marks locate the end of content, and the two independent sources are compared
   (`CrossCheckEndOfContent`):

| The marks | Outcome |
|---|---|
| land on the recorded block | **append**; `CopyUnreadable` (copy 1, at the recorded block), settled `Retried` by the operation's own TOC write — nothing left to advise |
| land elsewhere | **refused** before anything is written: `ERROR_INVALID_DATA` stating both blocks; `RecordedEndDisputed`; advice `EndOfContentDisputed` |

3. **Nothing recorded:** the navigator locates the end by its marks, and settles at begin-of-content only on positive
   blank evidence (§12.0, R2).
4. **The last set closed by end-of-media** (F32, TOC in partition): the volume is full — the set starts on the next volume.

A legacy copy's end of content is not used.

**TL-10 and `TOC.Volume`:** the guard looks up `TOC.Volume`. Here the identified volume and `TOC.Volume` coincide: an
append writes onto the cartridge whose TOC was just read or written — the service verified the loaded header against this
TOC before appending (§10.5 checkpoint 2 of the media-header design), and a continuation volume was verified fresh before
`ResumeBackupToNextVolume` set `TOC.Volume`. And the guard does not trust the number anyway: the recorded end is checked
against the tape.

**Facts in the agent, advice in the service:** the refusal states the two blocks and that nothing was written; what to do
is `AdviseTocText(EndOfContentDisputed)`.

### 8.2 Phase-0 defect fixes, as built

- **F1 / F5 — a short write is a failure (TL-7).** `TapeWriteStream` throws on a write or flush that left bytes behind,
  with the drive's error when it reported one, else end-of-media when the write signalled it, else a write fault — and
  marks the stream failed, so no filemark is written behind an empty copy.
- **BackupTOC accounting** (`TapeAgentBase.BackupTOC`): a copy counts only when its data **and** its closing filemark
  reached the tape. One copy short → success with `CopyWriteFailed` (end-of-media for a copy whose filemark did not fit —
  still readable, the navigator identifies an open tail); no copy's data on tape → failure with `NoCopyWritten`. **W1:**
  after copy 1 ran into end-of-media — its data or its closing filemark — copy 2 is not attempted. `CopyCount` = 2 on both.
  An enforced rewrite that cannot locate the TOC position on TOC-in-set media refuses rather than risk content.
- **F2 — no disarming needed.** `TapeDrive.WriteDirect(…, honorEwReserve)`: the reserve clamps content writes only; TOC
  writes and the TOC mark's gap file bypass it. Early-warning tracking is unaffected.
- **F3** — the reserve's write-position anchor counts the media header: begin-of-content is the header block past BOM;
  a header just written resets end-of-data itself (no anchor).
- **F4** — `TapeNavigator.TOCFootprint(tocBytes)` (two copies in whole blocks, plus the gap file on the TOC-mark layout).
  `BackupTOC` warns when the footprint exceeds the reserve armed on the drive (or the override). Since Design-TocReserve
  the reserve follows the TOC, so the warning is a backstop.
- **R2** — the navigators' "no marks" fallback settles at begin-of-content only on positive evidence (§12.0).

## 9. Code map

| Concern | Where |
|---|---|
| Peeker contract, peeks | `Headers/TapeBlockPeek.cs` (`ITapeBlockPeeker`, `TapeBlockPeek`, `TapeTocPeek`, `TapeFilePeek`) |
| Dispatcher, projections, `IdentifiedBlock` | `Headers/TapeHeaderBlock.Identify.cs` |
| Block I/O, `ReadAndIdentify` | `Headers/TapeHeaderBlock.cs` |
| Peekers | `Toc/TapeTOC.Format.cs`, `Headers/TapeFileHeader.cs`, `Headers/TapeHeader.cs`, `Calibration/TapeCalibrationCheckpoint.cs`; legacy: `Legacy/LegacyIdentify.cs` |
| Walker | `Scan/TapeScanner.cs` (`WalkFrom`), `ScanMediaOptions` |
| Ladder, anchors, S4 | `Agents/TapeAgentBase.TocLocation.cs` |
| TOC write / read, copy accounting | `Agents/TapeAgentBase.cs` (`BackupTOC`, `RestoreTOC`, `RestoreTOCAt`) |
| Anomaly records, settlement | `Agents/TapeAgentBase.TocAnomalies.cs`, `Agents/TapeTocAnomaly.cs` |
| Notifiables | `Agents/ITapeFileNotifiable.cs` |
| Format fields, per-volume lookup | `Toc/TapeTOC.Format.cs`, `Toc/TapeTOC.Encryption.cs` (stub field 9), `Toc/TapeTOC.cs` (`FirstBlockAfterContentOnVolume`) |
| Capture of the set's end | `Media/TapeStreamManager.cs` (`LastSetFirstBlockAfter`) |
| End-of-content check, R2, F2 | `Media/TapeNavigator.cs` (`CheckRecordedEndOfContent`, `SettleAtBeginOfContentIfBlank`), `Drive/TapeDrive.cs` (`honorEwReserve`) |
| Append guard, stamping | `Agents/TapeFileBackupAgent.cs` (`BeginWriteContentForCurrentSet`, `CrossCheckEndOfContent`) |
| Trailing delete restamp | `Agents/TapeSetAgent.cs` |
| Resume identification | `Agents/TapeFileRestoreAgent.cs` (`CheckVolumeToResumeFrom`, `IdentifySetOnVolume`) |
| Short writes | `Media/TapeStream.cs` |
| Service: advice, S4 prompt, TOC handler | `Services/TapeServiceBase.Toc.cs`, `Services/MediaPrompt.cs` (`FullWalkForTOC`) |

## 10. Tests

Damaged tapes are built deterministically with the virtual media's surgery hooks (`VirtualTapeMedia`, TEST_HOOKS:
truncate, remove a mark, corrupt a block at rest, describe the layout) and per-volume fault plans
(`MultiVolumeTapeServiceHost.VolumeFaults`). Every fixture backup checks the saved TOC's structure
(`TocInvariants.AssertWellFormed`).

| Suite (TapeLibNET.Tests) | Pins |
|---|---|
| `Headers.BlockIdentificationTests` | §3: totality, dispatcher, projections |
| `Toc.TapeTOCWriteIntegrityTests` | F1/F5: short writes fail; empty copies never reported written |
| `Media.TapeNavigatorEndOfContentTests` | R2: content without marks refused, blank media settles at begin-of-content, TOC-only volumes; §8.1: the probe's verdicts (confirmed, disputed, not applicable), the refusal (facts-only diagnosis, `RecordedEndDisputed`, tape unchanged), the cross-check (`CopyUnreadable` settled `Retried`, no advice) — all TOC-in-set layouts, with and without headers |
| `Toc.TapeTocAnomalyTests` | §5.1: copy accounting, once per volume, settled `Retried`, cleared per operation, read-side anomalies, the delete's TOC write, one copy on a full volume; every kind worded |
| `Services.ServiceTocAnomalyTests` | §5.4: advice by cause and layout, end-to-end reports |
| `Toc.TocContentEndTests` | §6: stamped after the mark on every layout, −1 when closed by EOM, survives a trailing delete, in the sealed stub, per-volume lookup |
| `Toc.TocLocationLadderTests` | §4: S1, S3, S2 per layout and damage; foreign and implausible copies skipped; partitions |
| `Toc.TocFullWalkTests` | §4.1 S4: confirmed, declined, progress, abort, not on a resume's ladder |
| `Services.ServiceTocFullWalkTests`, `Services.ServiceTocLocationTests` | the ladder end to end per layout: S0, S1, S4 on load and as the resume's last resort, prompts and advice |
| `Scenarios.ResumeIdentificationTests` | §7: identified by header, by own TOC, by the set's first block; rejected when unidentifiable |
| `Scenarios.MultiVolumeBackupRestoreTests` (all header modes), `Encryption.MultiVolumeEncryptionTests` | series across layouts; the parity guard: both TOC copies on every volume |
| `Scan.TapeScannerTests`, `TapeScannerPartitionTests`, `TapeScannerHarvestTests` | the walker Scan Media and the ladder share |
| `Legacy.LegacyEmitterTests`, `Services.ServiceLegacyMediaTests` | legacy copies identified and read; mixed legacy/2.1 series |

## 11. Plan *(placeholder — completed)*

Phases 0–6 are implemented; §12 records each phase's deviations from the v4 plan.

## 12. History

| Phase | Content |
|---|---|
| 0 | F1/F5, F2, F3, F4, R2, W1 |
| 1 | the one classifier (§3) |
| 2 | the notifiable hierarchy, TOC anomalies, statistics, service advice (§5) |
| 3 | format fields, capture and stamping, per-volume lookup (§6) |
| 4 | the ladder S0–S3, the walker, resume identification, the append guard (§4, §7, §8.1) |
| 5 | S4 with confirmation, progress and abort; 5b S4 as the resume's last resort; 5c partitions; 5d test parity |
| detour | the live TOC reserve and the TOC partition budget (Design-TocReserve) |
| 6 | the end-of-content check tested and its refusal made a TOC anomaly; stale remarks; the obsolete `TryIdentifyHeaderBlock` projections removed |

### 12.0 Deviations from Phase 0
- **R2 has two valid "empty" forms:** end-of-data at begin-of-content (blank), and a TOC copy standing at begin-of-content
  (a volume with zero sets), identified by reading the block. Begin-of-content is established by evidence — a media
  header read at BOM — not by cached presence.
- **A refusal keeps the positional cause** (BOM, EOD, EOM) instead of `ERROR_INVALID_DATA`, so the agent's retry from
  begin-of-content (SH-20) can run. The navigator still settles nowhere.
- **The TOC-mark layout without a mark:** no TOC mark means no TOC; content ends at end-of-data, provided a closing
  filemark stands right before it. The mark is searched forward from the believed position, then backward from
  end-of-data.
- **`TapeNavigator.TOCMissing`:** set when end of content had to be located on a volume without a TOC; the agent reports it
  once per volume as `NotFoundNatively` (`NoteVolumeWithoutToc`), settled `Retried` by a TOC written later.
- **Filemark layout, tail without its closing filemark:** when counting back three filemarks fails, the tail is checked; a
  tail without its closing filemark means the TOC write ran into end-of-media, and copy 1 is located by identification.
  Runs only after the regular count failed.
- **F2 needed no disarming** (§8.2). **W1** checks the drive's error for the closing filemark, the agent's for the data.
- **Error accessors:** `LastErrorWin32` is the single virtual accessor; `LastError`, `WentOK`, `WentBad`, `RanToEOM`,
  `RanToEOMorEOD` derive from it; on a `TapeDriveHolder` they include the drive's error, `OwnErrorWin32` reads the
  holder's own.

### 12.1 Deviations from Phase 1
- `TapeTOC.TryPeek` became internal; a TOC header cut off by the block length identifies as `NotFramed`.
- No legacy peeker adapter: legacy blocks go to `LegacyIdentify.IdentifyBlock` directly.
- Block 0 of the content partition goes through the same projection as every other block: a TOC copy there is a TOC
  fragment and is harvested.
- `HeaderBlockIdentity.FileHeader`: a CRC-verified 2.1 file header at a block start is a positive identity.

### 12.2 Deviations from Phase 2
- The anomaly records belong to the operation (§5.3); raised once the outcome is known (§5.1); stage `Retried`; advice
  `TocUnreadable`.
- TOC statistics come from the agent, not the progress handler: they change without a callback.

### 12.3 Deviations from Phase 3
- Non-negative varuint fields with default −1 (§6); the stub field is 9 and authoritative; capture by the stream manager;
  a trailing delete restamps; `CopyCount` records the intention.

### 12.4 Deviations from Phase 4
- **No window around the expected position:** the block is recorded, so S3 is exact; the size-sum fallback for legacy sets
  was dropped — a set without the value skips S3.
- **Partition-explicit moves (TL-11)**; the plain `MoveToBlock(block)` moves within the current partition.
- **The ladder on partitioned media**, **the anchors**, **the walker**, **identity filtering only against what is
  expected** — as in §4.
- **The block-length rule:** a read at another size than the block on tape reports `ERROR_INVALID_BLOCK_LENGTH`,
  delivers the data and leaves the head past it — on real drives and the virtual drive alike. Identification absorbs it;
  TOC stream and content reads keep failing on it.
- **Resume identification** as in §7; **rejection now means "unidentifiable"**, not "TOC unreadable".
- **The append guard** reports confirmed, unverifiable or disputed (§8.1); a cross-checked confirmation is a TOC anomaly,
  not a set anomaly. Advice tells the two settlements of a damaged copy apart.

### 12.5 Deviations from Phase 5
- S4 never runs on a resume's ladder; it walks the content partition on every layout; no copy limit.
- The question is a media prompt (`FullWalkForTOC`); progress and cancellation reuse the walker; a declined S4 leaves the
  detection recoverable, and the diagnosis says it was declined.
- An anchor equal to S3's block starts one file later; the setmark anchor walks with `NoSetmarksAhead`;
  `SetmarkFallbackToFilemark` in the TOC walks.

#### 12.5.1 Deviations from Phase 5b
- S4 as the resume's last resort (§7 step 4), covering encrypted volumes through the plain stub.
- An append without a loaded TOC is refused (it used to fall back to an overwrite).

#### 12.5.2 Deviations from Phase 5c
- **Known limit:** when the set before the TOC lost its closing mark, the walk reads copy 1 as the set's tail and meets
  copy 2 only; if copy 2 is damaged too, the walk fails although copy 1 is intact. A bounded backward probe for the TOC
  magic before that filemark would close it — not done.
- A volume identified by S4 is read without set-header verification when its media header did not read.
- Partitions: the ladder is S0, S1, S2, S4 (§4.1).
- A recoverable detection is reported at Warning.

#### 12.5.3 Deviations from Phase 5d
- **Test parity:** the multi-volume fixture's default reserve is layout-aware — the footprint of two copies plus their
  closing marks — so every layout keeps both copies on a full volume; a guard test checks every volume of every profile
  and header mode. Single-copy volumes are tested only where a test asks (`SingleCopyTOCCapacity`).

### 12.6 The TOC-reserve detour
- **TL-8 realized by Design-TocReserve:** the reserve follows the TOC (armed from the series TOC, grown by each content
  write for the files it starts); F4's check stays as a backstop.
- **The parity reserve is a fixture choice;** live-reserve tests opt in (`UseLiveTocReserve`). Continuation volumes are no
  longer padded; every volume has its own capacity.
- **Three end-of-volume boundaries** (Design-TocReserve §4.6, Design-MultiVolume §4.1): a final flush full with every file
  committed (no continuation), a set header that does not fit (the set moves on), a fresh volume that takes no file (the
  backup ends).
- **TOC structure is checked after every fixture backup** (`TocInvariants`), including continuation identity by format.

#### 12.6.1 The TOC partition budget
- TOC partitions get a budget (Design-TocReserve §9): a volume ends when the next files' TOC entries would not fit the
  partition twice. This replaced §0.4's pre-flight check. Advice `TocPartitionFull` keeps its meaning: a copy that still
  failed to write.

### 12.7 Phase 6
- **The end-of-content check was untested.** Its three outcomes now have navigator- and agent-level tests (§8.1, §10).
- **The refusal became a TOC anomaly** (`RecordedEndDisputed`) with service advice (`EndOfContentDisputed`); the agent's
  message states facts only. `TapeTocAnomaly.Summary` words every kind, pinned by a test.
- **Remarks:** phase references removed from `TapeTocAnomaly`, `BuildTocAnomaly`, `ITapeTocNotifiable`; the restore agent's
  rejection docs describe "unidentifiable"; the append guard documents its TL-10 reasoning (§8.1).
- **Projections:** the `TryIdentifyHeaderBlock` overloads were removed; `IdentifiedBlock.TocVersion` and `TocMediaId`
  stay as convenience views, routed through `TryGetToc`.
- **Dropped:** session landmarks (§4.3), the pre-flight TOC-size check (§0.4). **Deferred:** the backward probe of
  §12.5.2; a Help topic "TOC space", to which the advice points.
