# Design — Locating the TOC (rugged TOC location, TOC anomalies, one block classifier)

**Version:** 3 (replaces v2). **Status:** proposal; nothing implemented yet.

**Changes since v2** (the user's review, analysed in §0):

| Topic | v2 | v3 |
|---|---|---|
| Peekers | internal methods, ad hoc signatures | one contract, `ITapeBlockPeeker<TPeek>` (static abstract), uniform `TapeFrameStatus` result |
| Projections | `Classify` etc. as one-liners | **no `ITapeClassify`**: typed accessors on `IdentifiedBlock`; redundant projections retired |
| S2 / S4 | sweep and scanner described separately | **both are `TapeScanner` walks** (start anchor + TOC filter) |
| Block addresses | rejected | **adopted**: `ContentEndBlock` in every copy; S3 jumps by the series TOC's file addresses |
| Short-write error | always `ERROR_END_OF_MEDIA` | the drive's own error; synthesized only when the drive reported none, chosen by its flags |
| Partial TOC write | — | success **with a TOC anomaly** |
| Reporting | `TocLocateOutcome` + `TapeResult` | **TOC anomalies**, accumulated per operation across volumes, reported like set anomalies |

---

## 0. Review of the proposals

### 0.1 A uniform peeker interface (§3.2) — **yes, with one change**

A shared contract does buy something concrete: every peeker gets the **same guarantees** (pure, total, never throws), and
**one generic test** can hammer all of them with truncated and corrupted bytes. Before, each needed its own.

The change: the uniform result is **`TapeFrameStatus`, not `bool`**. The dispatcher must tell *damaged* (CRC mismatch,
unparseable) from *not ours*. With a `bool`, every peeker would need a second channel for that, and the
`DamagedRecord` mapping would be repeated per kind. With the status, it lives once in the dispatcher.

**Renames:** no renames across the solution are required. Peekers implement the interface **explicitly**, so the
existing methods keep their names and callers. `LegacyIdentify` stays frozen behind an adapter rather than being touched.

### 0.2 `ITapeClassify` for the projections — **no**

The projections (`Classify`, `TryIdentifyHeaderBlock`, `Read(..., out TapeHeader?)`) are not polymorphic behaviour. They
are *the same call* (`IdentifyBlock`) read through different fields. An interface would add a type without removing a
path. The streamlining that actually removes paths:

- **Typed accessors** on `IdentifiedBlock`: `TryGetHeader<T>(out T)`, `TryGetToc(out TocPeek)`, `TryGetFile(out FileHeaderPeek)`.
- **`Classify` stays** as the one header projection (many callers; one line).
- **Retired:** `TryIdentifyHeaderBlock` (both overloads) and the `TocVersion` / `TocMediaId` fields. They're `[Obsolete]`
  for one release, then removed.
- **`TapeFramer.UnpackHeader`** becomes `internal`: it's peeker machinery, not a classification entry point.

### 0.3 S4 = `TapeScanner` with a TOC-only filter — **yes, and further: S2 too**

The S2 sweep and the S4 walk are the *same loop*: read a file's first block, identify it, hop to the next mark, stop at
EOD. They differ only in **where they start** (an anchor vs. BOM) and **what they keep** (TOC copies vs. everything).
So both become `TapeScanner` walks with options (§4.3). That's one walker instead of two, and the TOC ladder gets the
scanner's proven handling of marks, EOD and partitions for free.

### 0.4 Block addresses — **you're right; adopted**

v2 rejected absolute block numbers as non-portable. That was inconsistent: every `TapeAddress` in the TOC is a block
number, and restore already relies on `MoveToBlock` for every file. Whatever drive can read the files can use block
numbers for the TOC too.

**Where to store them, and what they're good for:**

| Option | Verdict |
|---|---|
| In the media header (BOM) | impossible: the header is written before the TOC location is known, and rewriting BOM truncates a sequential tape |
| In set headers | they're written before the content they'd point past |
| A copy's **own** start block, in the copy | as you said, pointless for finding it; and on a sequential tape a stale copy still sits where it was written, so it doesn't detect staleness either |
| **`ContentEndBlock`** (the start of the TOC session) in **every copy** | **adopted** — see below |

**`ContentEndBlock` doesn't help *find* a copy, but it makes *any* found copy decisive:**

1. **End-of-content for an append is exact**, whichever copy survived and however it was found. A forward walk (S4) on a
   tape with lost marks finds a copy; that copy says where content ends. Copy markers alone could only say "one file
   back", which needs intact marks.
2. **Plausibility check:** a copy found at block X with `ContentEndBlock > X` is impossible, so it's rejected as foreign or
   corrupt.

**Finding by block address** happens through the **series TOC**, not through the copies. On a multi-volume resume (and
on an append, with the in-memory TOC) we already hold the file addresses of every set on this volume. End-of-content is
then (address of the last file + its `SizeOnTape`) rounded up to the block, + the set mark. **S3 becomes a single
`MoveToBlock`** plus a small window, instead of a sum of estimated sizes. This is where your point pays off most.

**Copy markers stay** (`CopyIndex`, `CopyCount`): they tell S1 which way the sibling lies, and they let diagnostics tell
"copy 2 of 2 survived" from "only one copy was ever written".

### 0.5 Partial TOC write = success with an anomaly — **yes**

`BackupTOC` already returns OK when one copy succeeds. What's missing is that nobody learns that only one copy is on
tape. That's a **TOC anomaly**, success-with-warning, exactly like a recovered set anomaly. This also applies when copy 1
fails and copy 2 succeeds; that case is *more* serious, since the copy at the content-end position is the one missing.

### 0.6 A new `TapeTocAnomaly`, not a reused `TapeSetAnomaly` — **agreed**

`TapeSetAnomaly` is built around a set-header verdict: expected/actual set indices, descriptions, `IsDestructive`.
Overloading it would leave most fields meaningless for a TOC event and blur `TapeSetStatistics`. A sibling type with
the **same pattern** keeps both readable and the reporting code parallel (§5).

### 0.7 Ask the user? — **only before the whole-media walk**

Your second thought is the right one:

- **S0–S3** cost seconds and only read, so they run without asking. They're reported afterwards.
- **S4** may read the whole cartridge (hours on LTO). It **asks first**, through the prompt that already exists for
  exactly this question: `OnMediaMismatchConfirm` with `MediaPromptContext.SearchForTOC`, whose wording ("may take a
  while and may find nothing") and unattended default (Proceed) already fit.
- **No new host API is needed.**

### 0.8 Report the drive's real error — **yes, with one exception that matters**

In the F1 trace the drive reported **no error at all**: the reserve clamp returned a short count with `NO_ERROR`. There
is no "actual error" to forward, which is why the bytes were silently lost. Rule:

1. The drive reported an error → **forward it unchanged**.
2. No error, but the write's own flags say early warning / EOM (the stream already captures `EOFEncountered`) →
   `ERROR_END_OF_MEDIA`. That's a fact, not an invention.
3. No error and no flag → `ERROR_WRITE_FAULT`, with the message *"drive accepted N of M bytes without reporting an error"*.

The TOC anomaly then says *that it was a TOC*. The error code only says *what the drive did*.

### 0.9 Integrate via anomalies, also for `RestoreTOC` — **yes, but keep `TapeResult`**

The two answer different questions, as with sets:

| | Answers | Lifetime |
|---|---|---|
| `TapeResult` | *can I use the TOC now?* | one call |
| TOC anomalies | *what did it cost, on which volume?* | the whole operation, all volumes |

`TocLocateOutcome` disappears: the stage that succeeded is a field of the recovery anomaly, and a clean read leaves no
anomaly. `BackupTOC` and `RestoreTOC` report the same way.

### 0.10 One point I'd add: the advice must fit the cause

"Re-save the TOC to restore both copies" is **wrong advice on a full volume**: there's no room for a second copy, and a
re-save fails the same way. So the advice depends on *why* only one copy exists (§5.4). On a full volume the useful
action is **Export TOC to file**. With the F3/F4 reserve fixes, a single-copy volume should become rare in production
anyway; when it does happen, it points to a reserve problem, and the advice says so.

---

## 1. Evidence

### 1.1 Three traces

Content capacity 262 144 B, TOC reserve (`TOCCapacityOverride`) 32 768 B, block 16 384 B, media header 16 384 B. Marks take
no space on the virtual media and are refused once EOM is set.

| # | Layout | Volume 1 as written | Restore of volume 1 |
|---|---|---|---|
| T1 | FilemarksOnly | `[MH][FM][set][FM][TOC1]`: copy 1 complete, its closing FM refused at EOM, copy 2 failed | **fails**: back 3 FMs → BOM → "blank" fallback → media header read as TOC |
| T2 | TOCMark, capacity +64 | `[MH][FM][set][FM][gap][FM×3][TOC1]`: copy 1 complete, no closing FM | **succeeds**: the forward search finds the TOC mark |
| T3 | TOCMark | `[MH][FM][set][FM][gap][FM×3][FM]`: **no TOC data at all** | **fails**: nothing to find |

### 1.2 Findings

| # | Finding | Kind |
|---|---|---|
| R1 | TOC location by pure mark arithmetic; one missing mark breaks it | design |
| R2 | The "no marks → blank" fallback claims success for a TOC search **and** for an append's end-of-content → **overwrites set 1** | **data-loss bug** |
| R3 | No position is identified before it's trusted | design |
| R4 | The retries repeat the same arithmetic | design |
| F1 | **An empty TOC copy is reported as written** (T3: clamped to 0 B, no error, filemark written, "backed up ok") | **data-integrity bug** |
| F2 | The early-warning reserve clamps the TOC write itself | bug |
| F3 | The reserve is anchored without the media header: every headed volume loses one header block of TOC reserve | bug |
| F4 | A layout's TOC footprint (TOCMark: gap + 3 marks + 2 copies) is never checked against the reserve | test-geometry trap |
| F5 | `TapeWriteStream.Write` loops forever when `WriteDirect` returns 0 without an error | latent hang |
| F6 | A volume whose TOC is lost is still fully restorable through the series TOC; only resume identification insists on its own TOC | design |
| F7 | A forward search from a unique anchor survives a missing trailing mark; backward counting doesn't | insight |
| F8 | Nothing records that a volume carries only one TOC copy, or that a read needed a fallback | observability |

---

## 2. Invariants

| # | Invariant |
|---|---|
| TL-1 | **Look before you trust.** No stage claims "at the TOC" unless the block there identifies as a TOC copy. |
| TL-2 | **Mark arithmetic is a hint, never a proof.** |
| TL-3 | **Never write at a guessed position.** End-of-content comes from a TOC copy's `ContentEndBlock`, from the series TOC, or from positive "blank" evidence; otherwise the write is refused. |
| TL-4 | **Bounded, escalating cost.** S0–S3 run unasked; S4 asks first. |
| TL-5 | **Choose the right copy:** expected `MediaId`, expected `Volume`, plausible `ContentEndBlock`, newest `LastSaveTime`, then copy 1 — all from the peek. |
| TL-6 | **Every deviation is a TOC anomaly.** A clean path records none. |
| TL-7 | **A write that wrote nothing is a failure**, reported with the drive's own error when it has one. |
| TL-8 | **The TOC reserve serves the TOC:** armed for content only, measured from the true write position, sized for the layout's footprint. |
| TL-9 | **One classifier, one walker.** |

---

## 3. One block classifier

### 3.1 The peeker contract

```csharp
/// <summary>
/// The contract of every block peeker: identify ONE kind of record at the front of a block. Pure, total — never throws,
///  whatever the bytes — and it reads nothing beyond <paramref name="block"/>.
/// </summary>
/// <remarks>
/// The status says how far the bytes got: <see cref="TapeFrameStatus.Ok"/> (a <typeparamref name="TPeek"/> is returned),
///  <see cref="TapeFrameStatus.NotFramed"/> (not this kind at all), <see cref="TapeFrameStatus.CrcMismatch"/> /
///  <see cref="TapeFrameStatus.Unparseable"/> (this kind, damaged or newer). The dispatcher maps the last two to
///  <see cref="HeaderBlockIdentity.DamagedRecord"/> — in one place, for every kind.
/// </remarks>
internal interface ITapeBlockPeeker<TPeek>
{
    static abstract TapeFrameStatus TryPeek(ReadOnlySpan<byte> block, out TPeek? peek);
}
```

| Implementer (explicit implementation) | `TPeek` | Delegates to (existing, unchanged) |
|---|---|---|
| `TapeTOC` | `TocPeek` | `TryPeek` body (2.1) / `LegacyIdentify.TryPeekToc` (legacy) |
| `TapeFileHeader` | `FileHeaderPeek` | `TapeFrame.ReadInline` over a span stream (moved from the scanner) |
| `TapeHeader` | `TapeHeader` | `TapeFramer.TryUnpackHeader` (both formats) |
| `TapeCalibrationCheckpoint` | `TapeCalibrationCheckpoint` | `TapeFrame.TryUnpack` |
| `LegacyBlockPeeker` (new adapter) | `IdentifiedBlock` | `LegacyIdentify.IdentifyBlock` (frozen) |

### 3.2 The dispatcher

The structure stays `IdentifyBlock`'s existing switch on the record kind. The repeated "status → kind or DamagedRecord"
logic moves into one generic helper:

```csharp
    private static IdentifiedBlock Peek<TPeeker, TPeek>(ReadOnlySpan<byte> data, HeaderBlockIdentity kind,
        Func<TPeek, IdentifiedBlock> found)
        where TPeeker : ITapeBlockPeeker<TPeek>
    {
        TapeFrameStatus status = TPeeker.TryPeek(data, out TPeek? peek);
        return status == TapeFrameStatus.Ok && peek is not null
            ? found(peek)
            : status == TapeFrameStatus.NotFramed
                ? IdentifiedBlock.Foreign
                : new(HeaderBlockIdentity.DamagedRecord, FrameStatus: status);
    }
```

| Record kind (2.1) | Arm |
|---|---|
| `TocHeader` | `Peek<TapeTOC, TocPeek>(…, TocCopy, p => new(TocCopy, Toc: p))` |
| `FileHeader` | `Peek<TapeFileHeader, FileHeaderPeek>(…)`, except that a damaged frame stays **Foreign** (file body may start with the magic; only the CRC confirms a header) |
| `CalibrationCheckpoint` | `Peek<TapeCalibrationCheckpoint, …>` |
| any other | `Peek<TapeHeader, TapeHeader>` |
| not `TpN#` | `LegacyBlockPeeker` |

### 3.3 Types

```csharp
public enum HeaderBlockIdentity { Foreign = 0, Header, DamagedRecord, TocCopy, CalibrationCheckpoint,
                                  FileHeader /* ✚ appended */ }

public readonly record struct TocPeek(ushort Version, Guid MediaId, int Volume, DateTime LastSaveTime, int SetCount,
    byte CopyIndex, byte CopyCount, long ContentEndBlock);       // 0 / -1 = not carried (legacy, older 2.1)

public readonly record struct FileHeaderPeek(Guid SetId, ulong FileId, string Name);

public readonly record struct IdentifiedBlock(HeaderBlockIdentity Kind, TapeHeader? Header = null,
    TapeFrameStatus FrameStatus = TapeFrameStatus.NotFramed, TocPeek? Toc = null, FileHeaderPeek? File = null)
{
    public bool TryGetHeader<T>([NotNullWhen(true)] out T? header) where T : TapeHeader;
    public bool TryGetToc(out TocPeek toc);
    public bool TryGetFile(out FileHeaderPeek file);
}
```

### 3.4 One I/O primitive

The scanner's private `ReadIdentificationBlock` becomes `TapeHeaderBlock.ReadAndIdentify(drive, out tapemark, out eod,
out block, out read)`: a 16 KiB read, the small-block fallback, and the mark / EOD flags.

It's used by the scanner, by BOM header reads (`ReadBomHeader` through the manager), by set-header verification, and by
the resume identification. `TapeHeaderBlock.Read(..., out TapeHeader?)` becomes a projection of it.

### 3.5 Contract test

A theory over every peeker type: random bytes, every truncation of a valid record, single-bit flips in every byte.
Assertions:
- no exception;
- `Ok` only for the untouched record;
- the dispatcher's result agrees with the peeker it delegated to.

---

## 4. The TOC location ladder

### 4.1 Stages

| Stage | Name | Candidates | Asks? |
|---|---|---|---|
| **S0** | Native | the layout's own TOC position, made honest (R2): fail instead of "blank" | no |
| **S1** | Sibling | the other copy, one file away (`CopyIndex` gives the direction) | no |
| **S2** | Anchor walk | scanner walk from the layout's anchor to EOD, TOC copies only | no |
| **S3** | Expected position | `MoveToBlock` to the end-of-content computed from the series / in-memory TOC's **file addresses**, ± W = 4 blocks | no |
| **S4** | Full walk | scanner walk from BOM to EOD, TOC copies only | **yes** — `SearchForTOC` prompt |

Every candidate goes through the same steps:
1. `ReadAndIdentify` → is it a `TocCopy`?
2. Plausibility: `ContentEndBlock ≤ candidate block`.
3. TL-5 ranking on the peek.
4. `RestoreTOCAt` for the full CRC parse.

A copy that **peeks but doesn't parse** (torn) still contributes its `ContentEndBlock` (§6). Each fallback that succeeds,
each copy that fails and each foreign copy skipped is recorded as a TOC anomaly (§5).

### 4.2 Anchors (S2)

Spacing semantics as confirmed: `+n` stops just after the mark, `−n` just before it.

| Layout | Anchor |
|---|---|
| Setmarks | from EOD `MoveToNextSetmark(-1)`, then `+1`: just after the last set's closing setmark |
| TOCMark | `SeekForwardPastTOCMark` from BOM (unique by construction) |
| FilemarksOnly | from EOD up to K = 3 × `MoveToNextFilemark(-1)` (stop at BOM), then `+1` |
| Partitions | BOP of the TOC partition |

The walk crosses filemarks only, so the setmark-vs-filemark spacing rule plays no part. The tail file between the last
mark and EOD (T1's copy 1) is walked like any other.

### 4.3 `TapeScanner` options (S2 and S4)

```csharp
internal sealed record TapeScanOptions
{
    /// <summary>Where the walk starts; null = BOM of the content partition (today's scan).</summary>
    public Func<TapeDrive, bool>? StartAt { get; init; }
    /// <summary>Which fragments are kept and reported; everything else is walked over.</summary>
    public FragmentFilter Filter { get; init; } = FragmentFilter.All;      // All | TocCopies
    /// <summary>Harvest (fully read) each TOC copy found — Scan Media's §8.3.</summary>
    public bool HarvestTocCopies { get; init; } = true;
    /// <summary>Stop after this many TOC copies; 0 = no limit.</summary>
    public int StopAfterTocCopies { get; init; }
}
```

The public Scan Media keeps its current defaults. The ladder calls `TapeScanner.Walk(options)` (internal) with
`Filter = TocCopies, HarvestTocCopies = false`: it peeks, ranks, and reads only the winner. S2 adds
`StartAt = <the anchor>`. *To be confirmed against `TapeScanner.cs`.*

---

## 5. TOC anomalies

### 5.1 The record

```csharp
/// <summary>What happened to a TOC copy, or to the search for one.</summary>
public enum TapeTocAnomalyKind
{
    CopyWriteFailed,        // a copy could not be written; the other one was
    CopyUnreadable,         // a copy was found but did not read (torn, CRC) — another one did
    RecoveredByFallback,    // the TOC was read, but only by stage S1–S4
    ForeignCopySkipped,     // a copy of another series / volume, or implausibly placed
    NoReadableCopy,         // no copy read; the volume was identified otherwise (§7) — or not at all
    NoCopyWritten,          // both copies failed: this volume carries no TOC
}

public enum TapeTocStage { Native, Sibling, AnchorWalk, ExpectedPosition, FullWalk }

/// <summary>A TOC copy, or the search for one, that did not go as planned. By value; carried in a list per operation.</summary>
public readonly record struct TapeTocAnomaly(
    TapeTocAnomalyKind Kind,
    int Volume,                 // the cartridge it concerns
    byte CopyIndex,             // 1, 2; 0 = unknown / not one copy
    TapeTocStage Stage,         // the stage that found or recovered it (Native for write-side kinds)
    long Block,                 // where the copy stands; -1 = unknown
    TapeResult Diagnosis);      // the drive's or reader's own error
```

**Statistics** follow the set pattern: a `TapeTocStatistics` struct nested in `TapeFileStatistics.Toc`, holding counters
only: `CopiesWritten`, `CopyWriteFailures`, `ReadFallbacks`, `ForeignCopiesSkipped`, `VolumesWithoutToc`. The records
themselves live on the agent (`TapeAgentBase.TocAnomalies`), for the same aliasing reason as set anomalies.

### 5.2 Notification

The TOC verbs are also called **outside** file operations (load, identify, rename, delete), where no
`ITapeFileNotifiable` exists. So the callbacks go on a small interface of their own, which the file notifiable
inherits:

```csharp
public interface ITapeTocNotifiable
{
    /// <summary>Informational: a TOC anomaly was recorded.</summary>
    void OnTocAnomaly(in TapeTocAnomaly anomaly, in TapeFileStatistics stats) { }

    /// <summary>
    /// Asked before the full-media walk (S4) — the one TOC step that may take hours. Default: proceed (read-only; an
    ///  unattended run must not stall).
    /// </summary>
    bool OnTocFullWalkConfirm(int volume) => true;
}

public interface ITapeFileNotifiable : ITapeTocNotifiable { /* unchanged */ }
```

`BackupTOC(bool enforce = false, ITapeTocNotifiable? notify = null)` and
`RestoreTOC(TocLocateOptions? options = null, ITapeTocNotifiable? notify = null)` both take it.
`ServiceOperationProgressHandler` overrides the two members:
- `OnTocAnomaly` logs, mirroring `OnSetAnomaly`;
- `OnTocFullWalkConfirm` routes to the existing `PresentVerdict(Unidentified, SearchForTOC)`.

### 5.3 Lifetime across volumes

- **Cleared** at the start of an operation, where `_setAnomalies.Clear()` already runs. The standalone public `RestoreTOC`
  is an operation of its own and clears too.
- **Not cleared** by the internal `LocateAndRestoreTocCore`, which the multi-volume resume calls, nor by the `BackupTOC`
  between volumes.

So a backup that left one copy on volume 1 and one on volume 3, or a restore that had to fall back on volume 2, reports
**all** of it at the end, each entry naming its volume.

### 5.4 Service: advice by cause

`AdviseOnTocAnomalies(in TapeTocStatistics, IReadOnlyList<TapeTocAnomaly>)` mirrors `AdviseOnSetAnomalies`:

| Advice | When | Text (headline / action) |
|---|---|---|
| `TocLost` | `NoCopyWritten` | *"Volume #n carries no table of contents"* / *"Export the TOC to a file now (Media \| Export TOC to file); the files remain restorable through it"* |
| `SingleCopyFullVolume` | `CopyWriteFailed` with EOM / EW | *"Only one TOC copy fit on volume #n"* / *"Export the TOC to a file as a second copy; consider a larger TOC reserve"* |
| `SingleCopyDamaged` | `CopyWriteFailed` with another error, or `CopyUnreadable` | *"A TOC copy on volume #n is damaged"* / *"Re-save the TOC (rename the media) to restore both copies; retire the cartridge if this recurs"* |
| `RecoveredByFallback` | `RecoveredByFallback` | *"The TOC of volume #n was found only by searching"* / *"Re-save the TOC so it can be found directly again"* |
| `None` | nothing recorded | silent |

These are reported by `ReportTocAnomalyOutcome`, right after `ReportSetAnomalyOutcome`, in backup, restore, identify and
delete alike.

---

## 6. Format additions (2.1, optional, non-critical `TocHeaderWire` fields)

| Field | # | Type | Meaning | Absent |
|---|---|---|---|---|
| `CopyIndex` | 7 | byte | 1 or 2 | 0 = unknown |
| `CopyCount` | 8 | byte | copies attempted in this session | 0 = unknown |
| `ContentEndBlock` | 9 | long (varint) | `Drive.CurrentBlock` at the start of the TOC session: first block after the content (TOCMark: before the gap) | −1 = unknown |

- **Written by** `BackupTOC`, which captures the block once after `BeginWriteTOC` and stamps it into both copies.
- **The two copies differ** only in `CopyIndex`; each keeps its own CRC.
- **Goldens** are regenerated once.
- **Partitions layout:** `ContentEndBlock` refers to the **content** partition, which is the useful fact there too, for
  appends and the F32 logic.

---

## 7. Volume identification for a multi-volume resume (F6)

| Step | Evidence | Verdict |
|---|---|---|
| 1 | **Media header** at BOM (`ReadAndIdentify`): `MediaId` = series, `Volume` = requested | identified; a mismatch → reject `WRONG_DISK` |
| 2 | **Own TOC** through the ladder (expected `MediaId` / `Volume`; S3 from the series TOC) | read → `SameSetOnTape`. Unreadable but step 1 identified → **proceed** and record `NoReadableCopy` |
| 3 | Neither: the **first block of the needed set**, at its on-volume index | a `SetHeader` or `FileHeader` with the expected `SetId` → identified; otherwise reject |

Per-file `CheckFileHeader` (SetId + FileId) still guards every file. With this, **T3 restores** on headed media, and on
headerless 2.1 media via step 3.

---

## 8. Write side

### 8.1 End-of-content without guessing (TL-3)

| Source, in order | Exact? |
|---|---|
| Native TOC location (S0) | as today |
| Any TOC copy found by the ladder: its `ContentEndBlock` | **yes**, whichever copy, however found |
| The in-memory TOC's file addresses (S3 arithmetic) | yes, for 2.1 sets with `SizeOnTape` |
| A legacy / pre-marker copy, no `ContentEndBlock`: the earliest copy found by S2 | only if unambiguous; otherwise **refuse** the append ("repair the TOC first") |
| "Blank" | only with positive evidence: EOD at BOM, or right after media header + FM |

### 8.2 Phase-0 defect fixes

**F1 / F5: a short write is a failure, with the truest available error (TL-7, §0.8).** In `TapeWriteStream`:

```csharp
    /// <summary>
    /// The error for bytes the drive did not take: its own error if it reported one; else end-of-media if the write
    ///  itself signalled it (early warning / EOM); else a write fault — never a silent success.
    /// </summary>
    private TapeIOException ShortWriteError(int notAccepted, string what)
    {
        if (WentBad)
            return new TapeIOException(m_mgr, this, what);
        uint code = EOFEncountered ? (uint)WIN32_ERROR.ERROR_END_OF_MEDIA : (uint)WIN32_ERROR.ERROR_WRITE_FAULT;
        return new TapeIOException(code,
            $"{what}: the drive accepted {notAccepted:N0} byte(s) fewer than offered without reporting an error");
    }
```

- **`Flush`**, after the two spills: `if (m_buffer.IsNonEmpty) { var ex = ShortWriteError(m_buffer.ContentSize, "flush incomplete"); Fault(); throw ex; }`
  (covering `WentBad` as well, so it replaces today's `if (WentBad)` block).
- **`Write`**, direct path: `if (WentBad || written == 0) { var ex = ShortWriteError(count - written, "write failed"); Fault(); throw ex; }`

`Fault()` sets `WriteFailed`, so no filemark is written behind the empty copy.

**`BackupTOC` (copy accounting):**
- one copy fails → OK + `CopyWriteFailed` (with the copy's index and diagnosis);
- both fail → failure + `NoCopyWritten`;
- after copy 1 fails at EOM / EW, **don't attempt copy 2** (W1): it can't fit, and the attempt only adds a stray partial
  block.

**F2:** `TapeStreamManager.BeginWriteTOC` disarms the content reserve (`Drive.SetEarlyWarning(0)`). The agent re-arms it
for the next content session. *To be confirmed against `TapeDrive.cs`.*

**F3:** anchor the reserve after the media header. In `BeginWriteContentForCurrentSet`:

```csharp
            long headerFootprint = Navigator.MediaHeaderPresence == TapeHeaderPresence.Present
                ? TapeHeaderBlock.Size : 0L;
            long approxWritten = newSet
                ? (TOC.CurrentSetIndexOnVolume > 0 ? -1L : headerFootprint)
                : headerFootprint + TOC.ComputeContentSizeOnTapeBeforeCurrentSet(Drive.BlockSize,
                    withSetHeaders: Navigator.SetHeadersExpected);
```

*To be confirmed against `TapeTOC.cs`, in case `ComputeContentSizeOnTapeBeforeCurrentSet` already counts the header.*

**F4:** the navigator exposes `TocFootprint(long tocBytes)` = layout overhead + 2 × (TOC size rounded up to whole blocks
+ mark). `BackupTOC` logs a warning when the armed reserve is smaller; the multi-volume fixture asserts it.

**R2:** a navigator's "no marks" fallback applies only with positive blank evidence. A TOC search never uses it.

---

## 9. Integration summary

| Component | Change |
|---|---|
| `TapeHeaderBlock` | `ITapeBlockPeeker` dispatch, `ReadAndIdentify`, typed accessors; retire redundant projections |
| `TapeTOC`, `TapeFileHeader`, `TapeHeader`, checkpoint | explicit `ITapeBlockPeeker` implementations; `TocHeaderWire` fields 7–9 |
| `TapeScanner` | `TapeScanOptions` (start anchor, TOC filter, harvest, stop count); `ReadAndIdentify`; loses `SetContentFragment` / `ReadIdentificationBlock` |
| `TapeAgentBase.TocLocation.cs` (new) | ladder S0–S4, TOC anomaly list and statistics, `LocateAndRestoreTocCore` |
| `TapeAgentBase` | `BackupTOC` copy accounting and `ContentEndBlock`; `RestoreTOC` runs the ladder |
| `TapeFileRestoreBaseAgent` | `CheckVolumeToResumeFrom` per §7 |
| `TapeFileBackupAgent` | F3; end-of-content per §8.1 |
| `TapeStreamManager`, navigators | F2, R2, anchors, `TocFootprint` |
| `TapeWriteStream` | F1 / F5 |
| `ITapeFileNotifiable` | inherits `ITapeTocNotifiable` |
| Service | progress handler overrides; `ReportTocAnomalyOutcome` + advice; S4 through `SearchForTOC` |

---

## 10. Tests: build the damaged tape deterministically

New TEST_HOOKS on `VirtualTapeMedia` (diagnostics partial): `TruncateAfter(i)`, `RemoveMark(i)`,
`CorruptBlock(i, offset, bits)` (at rest), and `DescribeLayout()`.

| Test | Pins |
|---|---|
| `Toc_Copy1Only_NoClosingMark` | T1: S2 finds copy 1; `RecoveredByFallback(AnchorWalk)` |
| `Toc_Copy1Torn_Copy2Reads` | S1; `CopyUnreadable(1)` |
| `Toc_AllMarksAfterSetGone_ExpectedPosition` | S3 by file addresses |
| `Toc_TailDestroyed_FullWalk_Confirmed` / `_Declined` | S4 asks; declined → no walk, `NoReadableCopy` |
| `Toc_ForeignCopy_Skipped`, `Toc_ImplausibleContentEnd_Skipped` | TL-5, plausibility |
| `Append_OneCopyTape_WritesAtContentEndBlock` | **the R2 guard**, via `ContentEndBlock` |
| `Append_LegacyCopyAmbiguous_Refused` | TL-3 |
| `BackupToc_SecondCopyFails_SuccessWithAnomaly` | §0.5; advice `SingleCopyFullVolume` |
| `BackupToc_BothFail_NoCopyWritten` | §5.4 `TocLost` |
| `ShortWrite_ForwardsDriveError` / `_SynthesizesFromFlags` | §0.8 |
| `Reserve_AnchorsAfterMediaHeader` | F3 |
| `TocAnomalies_AccumulateAcrossVolumes` | §5.3 |
| `Resume_VolumeWithoutToc_IdentifiedByHeader` / `_ByFileHeader` | §7 |
| `Peekers_AreTotal` (theory over all peekers) | §3.5 |
| existing `MultiVolumeEncryptionTests` | green with the original geometry |

---

## 11. Plan

| Phase | Content | Exit |
|---|---|---|
| **0** | F1/F5, F2, F3, R2 | no unwritten TOC reported as written; no append at a guessed position |
| 1 | §3 peeker contract, dispatcher, `ReadAndIdentify`, typed accessors; scanner migrated | one classifier; `Peekers_AreTotal` green |
| 2 | §5 TOC anomalies (agent, notifiable, service, advice); `BackupTOC` copy accounting | partial writes reported; advice by cause |
| 3 | §4 S0–S3 with the scanner options; §7 resume identification; tape-surgery hooks | the 6 failing tests green, plus T1/T3 surgery |
| 4 | §6 format fields, goldens; §8.1 end-of-content | append on a one-copy tape at `ContentEndBlock` |
| 5 | S4 with confirmation; Design-MultiVolume cross-reference | full ladder |

---

## 12. Files needed before implementation

| File | For |
|---|---|
| `TapeDrive.cs` | F2 (reserve clamp, `SetEarlyWarning`, `NotifyNextContentWritePosition`) |
| `TapeNavigator.cs` (all navigator classes) | R2, anchors, `TocFootprint` |
| `TapeTOC.cs` | F3; `ComputeContentSizeOnTapeBeforeCurrentSet`; S3 arithmetic |
| `TapeScanner.cs` (main walk) + `ScanMediaResult` / `TapeMediaFragment` | §4.3 options |
| `LegacyIdentify.cs` | `LegacyBlockPeeker`; legacy half of `TocPeek` |
| `TapeFrame.cs` | the peeker contract over frames; `TapeFileHeader` peek |
| `TapeCalibrationCheckpoint.cs` | its peeker |
| `VirtualTapeMedia.cs` + `.Diagnostics.cs` | §10 hooks |
| `MultiVolumeVirtualTapeFixture.cs` | F4 assertion |
| `ServiceOperationResult.cs` | where `TapeTocStatistics` rides into the service result |
| A grep of callers: `TapeTOC.TryPeek`, `TapeHeaderBlock.Classify` / `TryIdentifyHeaderBlock` / `Read`, `TapeFramer.UnpackHeader`, `IdentifiedBlock.TocVersion` / `TocMediaId`, `BackupTOC(`, `RestoreTOC(` | migration |
