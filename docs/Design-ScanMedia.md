# Design — Scan Media (TOC-less media survey)

**Status:** v3.1 · Phases 0–6 implemented and green. This revision brings §8–§10 in line with the code and
specifies the WPF surface (Phase 5, written as a hand-off task) and the CLI (Phase 6).
**Scope:** read a cartridge from BOM forward and produce a **fragment map** — everything on the medium that
can be identified — with **no table of contents in hand and none assumed**. Then, as an option or as a
separate operation, **recover a table of contents** from a copy the map located.
**Relationship to `Design-RepairMedia.md` v4:** this is that design's `ScanContentSets` primitive, extracted,
decoupled from the TOC, and moved out of the agent hierarchy. Repair Media becomes *Scan + Compare + Apply*;
this document delivers **Scan** and **TOC recovery**, and §12 states what the other two inherit.

*The code is the authority.* Members marked **NEW** do not exist yet; everything else exists and the
description matches it.

---

## 1. Why the TOC-less form is the better primitive

The v4 walk took the TOC as its frame: it iterated `FirstSetOnVolume..LastSetOnVolume` and asked *"does the
tape agree?"* That inverts the dependency on the one cartridge that matters. Dropping the TOC removes three
genuine defects:

- **No end-of-content bound.** v4 established one with a `MoveToEndOfContent()` seek — which fails on exactly
  the damaged cartridge the feature exists for. Walking to EOD needs no bound: the trailing structures are
  *identified*, not *avoided*.
- **The TOC area is a harvest, not a hazard.** v4 treated `[toc1][FM][toc2]` as a trap to stop short of. It is
  the most valuable thing on a damaged cartridge: a TOC copy the user no longer has — including `toc2` when
  `toc1` is the casualty.
- **Findings stop being contingent.** A fragment means *"this object exists at this block"* — true whichever
  TOC surfaces later, serializable, and diffable against several TOCs in turn. Scan once, compare many.

The scan writes nothing, so it carries no destructive path, no witness problem, and no confirmation UX.

---

## 2. What is knowable before the tape moves

The layout is **derived** from drive capabilities and media organization alone:

| Condition | Navigator | Set separator | Trailing structure |
|---|---|---|---|
| partitioned | `TapeNavigatorTOCInPartition` | SM if supported, else FM | separator, then EOD; TOC in partition 0 |
| setmarks supported | `TapeNavigatorTOCInSetWithSmks` | SM | `[SM][toc1][FM][toc2][FM]` |
| sequential filemarks + TOC mark | `TapeNavigatorTOCInSetWithFmksAndTOCMark` | FM | `[FM][gap][FM][FM][FM][toc1][FM][toc2][FM]` |
| otherwise | `TapeNavigatorTOCInSetWithFmks` | FM | `[FM][toc1][FM][toc2][FM]` |

```csharp
public readonly record struct TapeMediaLayout(
    string NavigatorKind,       // the navigator's type name — for display and the map
    bool   UseSmks,             // real setmarks close content sets
    bool   TocInPartition,
    bool   HasTocMark,
    bool   MediaLoaded);        // false ⇒ partitioning was not observable; TocInPartition is a floor

public static TapeMediaLayout Predict(TapeDrive drive, bool useTOCMark = true);   // pure; works without media
public bool SeparatorAmbiguousWithToc { get; }    // true on the filemark layouts
```

`TapeNavigator.ProduceNavigator` is implemented **on top of** `Predict`, so the scanner and the navigator can
never disagree about a cartridge's layout. `UseSmks` stays a bool: it is the navigator's own vocabulary, and
there will be no third kind of set delimiter.

**`SeparatorAmbiguousWithToc`** is the fact that makes identification, not mark-hopping, the scanner's core:
on the filemark layouts the set separator and the TOC delimiter are the same mark.

---

## 3. The walk

From BOM, forward, to EOD. No bound, no TOC, no lookahead, no mark fishing.

```
layout = TapeMediaLayout.Predict(Drive)

// ── Block 0 — through a throwaway agent ─────────────────────────────────
first = IdentifyBomFragment(out bytesRead)        // probe.ReadBomHeader(out bytesRead)
if bytesRead ≤ 0              → Blank map (SM-6)
if first is CalibrationHeader → one-fragment map, stop (SM-7, §4.5)

// ── Alternate: cross the closing mark, read what follows ────────────────
loop:
    abort / MaxFragments guard                    → Truncated
    if the last fragment is data:
        CrossClosingMark(last)                    // SM or FM, by fragment KIND (§3.2)
        failed, positional   → last is NOT closed; clean end
        failed, otherwise    → transport fault; Truncated
    ReadFragmentAt(position):
        Tapemark   → count into the pending run; read on (the read crossed it)
        EndOfData  → clean end
        Fragment   → FoldMarkRun(pending); Commit(fragment)
                     if TOC && HarvestTocCopies → HarvestLastTocCopy (§8.3)
FoldMarkRun(pending); Report(last)
```

### 3.1 Block 0 goes through a probe agent

"Rewind, select the content partition, read block 0, classify" is a ceremony `TapeStreamManager` and
`TapeAgentBase.ReadBomHeader` already own. The scanner borrows it through a throwaway
`new TapeAgentBase(Drive, new TapeTOC())` — the pattern `TapeServiceBase.RefreshLoadedHeader` uses.

`ReadBomHeader(out int bytesRead)` exists for this: `bytesRead ≤ 0` means *nothing is there* (blank),
positive means *something is there, recognized or not*. Without it, SM-6 could not be kept.

From the second fragment on, the scanner reads raw: those blocks sit at positions the agent has no verb for.

### 3.2 The closing mark depends on the fragment's kind

| Fragment | Closed by |
|---|---|
| media header | **filemark**, on every layout (`TapeHeaderBlock.WritesTrailingMark`) |
| TOC copy | **filemark**, on every in-set layout, setmark ones included |
| set header, unidentified content | the layout's separator (`UseSmks`) |

Hopping a setmark from a media header on a setmark layout loses set 1 silently; hopping a setmark from `toc1`
runs to EOD and never sees `toc2`. `ClosingMarkIsSetmark` encodes this table.

A failed hop **syncs the drive's error** before judging it — reading `NO_ERROR` as a transport fault once
truncated every complete map.

### 3.3 Marks are detected by the ordinary read

A read that meets a mark returns nothing but the mark and leaves the head **past** it. So the identification
read doubles as the mark detector: each position is read once, the head never steps back.

| `ReadOutcome` | Meaning | Walk |
|---|---|---|
| `Fragment` | a block, readable or not | commit it |
| `Tapemark` | a mark, already crossed | add to the pending run, read on |
| `EndOfData` | nothing was ever written here | end cleanly |

Both boundaries come from `TapeDrive.ReadDirect`'s `out` flags, surfaced by
`TapeHeaderBlock.Read(…, out bool tapemark, out bool eod)`. The drive **resets** its error on both, so the
flags are the only reliable witness; `eod` is the drive's `eof && !tapemark`.

### 3.4 Mark runs, and the TOC mark

Marks read between two data fragments form a pending run, committed by `FoldMarkRun`:

- **`TocMark`** — on a `HasTocMark` layout, when the run holds **≥ 2** marks and the fragment before it is an
  `Unknown` that was **read cleanly** and closed: the `[gap][FM][FM][FM]` sequence.
- **`MarkRun`** — every other run, as a fragment of its own at the run's first block.

Two marks, not one, keeps a legacy double-filemark end of data from reading as a TOC mark. A damaged header is
also `Unknown`, but no gap — folding it would hide the damage. `MarkCount` is informational.

### 3.5 Termination

| Condition | Map | Fragment |
|---|---|---|
| EOD after a mark | complete, `ERROR_NO_DATA_DETECTED` | last fragment closed |
| closing-mark hop fails positionally | complete, that error | last fragment **not closed** |
| closing-mark hop fails otherwise | **Truncated** | last fragment not closed |
| harvest cannot return the head | **Truncated** | — |
| abort | **Truncated**, `ERROR_CANCELLED` | — |
| `MaxFragments` (fragments or run length) | **Truncated**, `ERROR_INVALID_DATA` | — |
| calibration header at block 0 | complete, `NO_ERROR` | one fragment |

No pseudo-fragment ever closes a map. *Why the walk ended* is map-level; *was this fragment finished* is
fragment-level (`ClosedBySeparator`). **No retry, no resync-by-seeking.**

### 3.6 Fragments are reported when final

A fragment's closing state, span, `TocMark` retype and harvested TOC are all decided after it is read. So
`Commit` reports a fragment's predecessor, and the last fragment is reported when the walk ends. A sink never
receives a value that later changes.

### 3.7 Cost

One 16 KiB read per fragment and per mark, plus one mark hop per data fragment; with recovery, one
multi-block TOC read and one locate per copy. Abortable between reads.

---

## 4. Identification

### 4.1 One classification, positive in every branch

```csharp
public enum HeaderBlockIdentity { Foreign, Header, DamagedRecord, TocCopy }

public readonly record struct IdentifiedBlock(
    HeaderBlockIdentity Kind, TapeHeader? Header = null,
    TapeFramer.FrameStatus FrameStatus = TapeFramer.FrameStatus.NotFramed,
    ushort TocVersion = 0, Guid TocMediaId = default)
{ public static readonly IdentifiedBlock Foreign = new(HeaderBlockIdentity.Foreign); }

public static IdentifiedBlock TapeHeaderBlock.IdentifyBlock(byte[] block, int length);
```

**Placement first, content second.** A framed header carries our signature at offset 4, behind the framer's
length prefix; a TOC stream carries it at byte 0. Inferring "TOC" from *"our signature, but no header
parses"* once turned every CRC-damaged set header into a phantom TOC copy.

| Order | Test | Outcome |
|---|---|---|
| 1 | `TapeTOC.TryPeek` at offset 0 | `TocCopy`, with version and media id |
| 2 | our signature at offset 4, then `TapeFramer.TryUnpack` | `Header`, or `DamagedRecord` with the reason |
| — | neither | `Foreign` |

**TOC first:** offset 0 of a plausible frame holds a length ≤ 16 KiB whose high bytes are zero — it can never
read as the signature. Offset 4 of a TOC stream holds the low bytes of its UID seed, which can.

### 4.2 The framer tells damage from absence

```csharp
public enum FrameStatus { Ok, NotFramed, CrcMismatch, Unparseable }
public static FrameStatus TapeFramer.TryUnpack<T>(byte[] block, int length, out T? record);
```

`Unpack` delegates and keeps its contract — null unless `Ok`. A `DamagedRecord` maps to an `Unknown` fragment
with a fingerprint and a diagnosis: `ERROR_CRC`, or `ERROR_INVALID_DATA` for a torn frame or unparseable
payload.

### 4.3 The signature probe tolerates the version

`ValidateSignature()` demands exactly 0x0101; the TOC is written as 0x0102. The probe accepts any **0x01xx**
version — random data then matches once in 16 million blocks.

### 4.4 TOC copies are recognized structurally

```csharp
public static bool TapeTOC.TryPeek(byte[] block, int length, out ushort version, out Guid mediaId);
```

The signature alone cannot identify a TOC: a legacy aligned file record opens a block with the very bytes a
v0x0101 TOC does. `TryPeek` reads in `ConstructFrom`'s order: signature and known version, non-zero
`nextUID`, `MediaId` (≥ v0x0102), plausible set count, then the **first set's own signature** (strict) — or,
for an empty TOC, a plausible tail. It sits directly below `ConstructFrom`. A newer version is **not**
recognized.

### 4.5 Calibration cartridges — identified, not walked

A `TapeCalibrationHeader` at block 0 makes the map complete, `Kind = CalibrationCartridge`, one fragment.
With `InspectCalibrationTrail` the scanner calls `TapeCalibrator.InspectMedia()` and attaches
`CalibrationInfo` — **off by default**; the service never asks for it (§9.1).

**Not recognized: legacy-shape calibration headers** written in the run block. They map as foreign; the
calibrator's own probe remains the supported path.

### 4.6 Small-block drives

When the drive's maximum block is under 16 KiB, `ReadIdentificationBlock` reads one native block instead.

### 4.7 What identification cannot do

A fragment's contents are opaque. `BlockSpan` is an upper bound including marks, never a payload size.

---

## 5. The records

```csharp
public enum FragmentKind                       // an OBSERVATION, never a verdict (SM-3)
{ Unknown = 0, MediaHeader, SetHeader, CalibrationHeader, TOC, MarkRun, TocMark }

public enum ScannedMediaKind { Blank = 0, Backup, CalibrationCartridge, Foreign }

public sealed record TapeMediaFragment
{
    public required int          Ordinal    { get; init; }   // 0-based, contiguous; assigned by Commit
    public required long         StartBlock { get; init; }
    public required FragmentKind Kind       { get; init; }
    public long BlockSpan          { get; init; } = -1L;
    public bool ClosedBySeparator  { get; init; }

    public Guid?     Id             { get; init; }            // MediaId / calibration RunId / TOC MediaId
    public int?      Volume         { get; init; }
    public int?      VolumeSetIndex { get; init; }
    public int?      GlobalSetIndex { get; init; }
    public string?   Description    { get; init; }
    public DateTime? CreatedUtc     { get; init; }
    public uint?     BlockSize      { get; init; }

    public ushort?  TocVersion   { get; init; }
    [JsonIgnore] public TapeTOC? HarvestedToc { get; init; }  // recovery ran and succeeded (§8)

    public int        MarkCount   { get; init; }
    public string?    Fingerprint { get; init; }              // first 32 bytes, hex
    public TapeResult Diagnosis   { get; init; } = TapeResult.OK;   // also: why a TOC copy did not recover
}

public sealed record MediaScanMap
{
    public required TapeMediaLayout  Layout     { get; init; }
    public required ScannedMediaKind Kind       { get; init; }
    public required IReadOnlyList<TapeMediaFragment> Fragments { get; init; }
    public required DateTime ScannedUtc         { get; init; }
    public bool Truncated       { get; init; }
    public uint TerminatorWin32 { get; init; }
    [JsonIgnore] public TapeCalibrationMediaInfo? CalibrationInfo { get; init; }

    // Derived: MediaId, Volume, SetCount, TocCopyCount, UnknownCount, LastSetUnclosed,
    //  MixedIdentityFragments, SetIndexGaps
    public const string MapFileExtension = ".tapescan";
    public string ToJson();
    public static MediaScanMap? FromJson(string json);
}
```

**No "expected", "complete" or "lost"** — those are comparison verdicts. `LastSetUnclosed` looks at the last
**set header**, not the last fragment. `MixedIdentityFragments` is empty without a media header.
`SetIndexGaps` reports forward jumps only. `ToString` carries the diagnosis and a short fingerprint.

---

## 6. What the map tells the user — with no TOC at all

- How many backup sets the cartridge holds, each named and dated from its own header.
- **The last set never completed** — the dominant real-world fault.
- A damaged set header, with its CRC diagnosis — distinct from foreign data.
- Mixed identity; index gaps.
- Where TOC copies survive, their version and series — and, with recovery, the TOC itself.
- A calibration cartridge, identified as such.
- On healthy media, a complete inventory — a feature users exercise on good media is one they trust on bad.

---

## 7. Where the scanner lives

### 7.1 `TapeScanner : TapeDriveHolder<TapeScanner>` — a sibling of `TapeCalibrator`

```csharp
namespace TapeLibNET.Scan;

public sealed partial class TapeScanner : TapeDriveHolder<TapeScanner>
{
    public TapeScanner(TapeDrive drive);
    public ScanMediaOptions Options { get; init; } = ScanMediaOptions.Default;
    public bool IsAbortRequested { get; set; }
    public TapeResult LastResult { get; }                  // first failure, latched
    public MediaScanMap? Scan(IProgress<TapeScanProgress>? progress = null);
}

public sealed record ScanMediaOptions
{
    public const int DefaultMaxFragments = 1_000;
    public bool HarvestTocCopies        { get; init; } = false;   // §8.3
    public bool InspectCalibrationTrail { get; init; } = false;   // §4.5
    public int  MaxFragments            { get; init; } = DefaultMaxFragments;
    public static ScanMediaOptions Default { get; } = new();
}
```

**Not a `TapeAgentBase`:** `TapeNavigator` is TOC-bound by construction, and the scanner needs raw mark hops
and absolute blocks. **Where an agent earns its keep, it is borrowed:** block 0 (§3.1) and TOC recovery (§8).
**A `record`, not a `record struct`, for the options:** a struct's `new()` zeroes every field and silently
skips the primary constructor's defaults.

`Scan` returns null only when no map is possible at all; a truncated map is still returned. It never throws.
A `BlockSizeGuard` restores the drive's block size (SM-9).

### 7.2 Progress: `IProgress<TapeScanProgress>`

```csharp
public readonly record struct TapeScanProgress(
    int FragmentOrdinal, long CurrentBlock, FragmentKind Kind, string Phase, TapeMediaFragment? Fragment);
// Phases: "scanning", "harvesting-toc", "inspecting-calibration", "completing"
```

Not `ITapeFileNotifiable`: its `void` callbacks can stop an operation only by throwing. `Report` converts a
sink's `TapeAbortRequestedException` into `IsAbortRequested`; any other exception is logged and swallowed
(SM-10).

---

## 8. TOC recovery

### 8.1 One verb, two callers

Teaching the scanner to read TOCs would duplicate the TOC stream ceremonies. Instead **the verb lives on the
agent and the scanner borrows it**, as it borrows `ReadBomHeader`. The walk's recovery option and the
service's separate "recover this copy" operation run the same code.

### 8.2 The agent verb — `RestoreTOCAt`

```csharp
// TapeAgentBase — same contract as RestoreTOC(): fills TOC via CopyFrom on success
public TapeResult RestoreTOCAt(long block);
```

- **Block is mandatory.** "Read where the head is" is spelled `RestoreTOCAt(Drive.CurrentBlock)` — the
  unanchored read must never be the easy default on this path. `Drive.MoveToBlock` makes it free.
- **One copy, no fallback.** `RestoreTOC`'s retry to the next filemark assumes it began at the *first* copy;
  here the caller may name the second. To try another copy, call again.
- **Leaves no believed position:** `Manager.EndReadWrite()` and `Navigator.ResetContentSet()` whatever the
  outcome — the block came from evidence the navigator cannot verify.

The existing `RestoreTOC → BeginReadTOC → MoveToLocationFor → MoveToBeginOfTOC` chain is **untouched**. That
wrapper also serves `BackupTOC`, so a caller-supplied block stored on the navigator could leak into a TOC
*write*. The new path is parallel and read-only by construction:

| Layer | Addition |
|---|---|
| Navigator | `MoveToTOCCopyAt(long block)` + `protected virtual MoveToTOCCopyAtCore` — `Drive.MoveToBlock` for in-set layouts; the initiator partition for `TOCInPartition`. Same template shape as `MoveToBeginOfTOC`. |
| Manager | `BeginReadTOCAt(long block)` — **always** ends the current session first (`BeginReadWrite` returns early on an unchanged state), then `BeginReadWrite(ReadingTOC, positioner: …)`. The optional `positioner` replaces `MoveToLocationFor` for this call only. |
| Agent | `RestoreTOCAt` = `BeginReadTOCAt` → the unchanged `RestoreTOCCore` → end session → reset navigator. |

`TOCUnlocated` and the TOC-mark layout's invalidation flag stay untouched: reading a copy does not locate the
TOC for writing.

### 8.3 During the scan — `HarvestTocCopies`

When the walk commits a `TOC` fragment and the option is set, `HarvestLastTocCopy`:

1. records `resumeAt = Drive.CurrentBlock`; `ReportPhase("harvesting-toc")`;
2. `probe.RestoreTOCAt(fragment.StartBlock)` through a throwaway agent over a fresh `TapeTOC`;
3. success ⇒ `HarvestedToc`; failure ⇒ the fragment stays a TOC copy, with the reason in `Diagnosis` (SM-8);
4. `Drive.MoveToBlock(resumeAt)` — the walk never depends on where the TOC reader stopped (SM-12). If that
   locate fails, the map is truncated.

Recovery is a **walk step**, not part of identification: it moves the head over many blocks. Since fragments
are reported when final, attaching the TOC is invisible to the sink. **Every copy is attempted** — on the
filemark layouts the second routinely survives the first. **Off by default at the scanner**; the service
turns it on.

**Out of scope: partitioned media.** The content walk finds no TOC fragments there; the ordinary restore
already goes straight to partition 0.

### 8.4 After the scan — guarded twice

The user may recover later, against one fragment. Between scan and recovery the cartridge may have been
swapped, so:

1. **Before reading:** the loaded cartridge's media id must equal the scanned one (one BOM read).
2. **After reading:** the recovered TOC's media id must equal the one the scan peeked at that block.

The second check verifies the CRC-validated result rather than peeking first — stronger, and a read is
harmless: nothing is adopted until both pass. Either failure is `ERROR_MEDIA_CHANGED`.

### 8.5 Adoption goes through the import path

A recovered TOC is adopted exactly like an imported one: same identity verdict and one-off prompt
(`MediaPromptContext.ImportToc`, no ProceedAlways), the mounted volume's number adopted but never its
MediaId, `TocChanged` fired. Never implicit (SM-13).

The service records it as **`TOCSource.Recovered`** (`TapeServiceBase.TOCIsFrom`): the navigator did not
locate this TOC, and a mid-tape copy may be older than the cartridge. Consumers that treat an imported TOC
with caution treat a recovered one the same way; the UI flags it as `"(using recovered TOC)"`.

---

## 9. The service layer

`TapeServiceBase.Scan.cs`.

### 9.1 Scan

```csharp
public sealed record ScanMediaRequest : ServiceOperationRequest
{
    public bool    RecoverTocCopies { get; init; } = true;   // → scanner HarvestTocCopies
    public string? MapExportFolder  { get; init; }           // null ⇒ no export
}

public enum ScanAdvice
{
    InspectCalibrationCartridge,    // use Calibrate | Inspect Media
    AdoptRecoveredToc,              // a copy was recovered — adopt or save it
    RecoverTocFromCopy,             // copies found, none recovered
    ReviewUnclosedSet,              // the last set never completed — Repair Media, once it lands
}

public sealed record ScanMediaResult : ServiceOperationResult
{
    public MediaScanMap? Map { get; init; }                  // also truncated, on abort / fault
    public ScannedMediaKind MediaKind { get; init; }
    public int  SetsFound, TocCopiesFound, TocsRecovered, UnknownFragments;   // init-only
    public bool LastSetUnclosed { get; init; }
    public IReadOnlyList<ScanAdvice> Advice { get; init; } = [];
    public string? MapExportPath { get; init; }
    public string Summary { get; init; } = string.Empty;     // the reported headline
    public bool WasAborted => Outcome == ServiceReportLevel.Failed;
}

public Task<ScanMediaResult> ScanMediaAsync(ScanMediaRequest request);
```

**How a scan is classified** — a map is returned in every row:

| Case | `Success` | `Outcome` | `Diagnosis` |
|---|---|---|---|
| clean scan | true | Completed / Warning / Info, from the headline | OK |
| stopped at `MaxFragments` | true | Warning ("Scan incomplete") | OK |
| user abort | false | Failed | `ERROR_CANCELLED` |
| transport fault | false | Error | the scanner's `LastResult` |

- **Calibration inspection is never requested.** The service advises `InspectCalibrationCartridge` instead;
  the calibration UI owns that answer and can act on it.
- **TOC recovery is on by default here** — the tape is already positioned.
- Shape of `ExecuteCalibrateAsync`: `Task.Run` → `_operationLock` → `PrepareMedia` → `RefreshLoadedHeader` →
  scanner → `CreateScanProgressHandler` (`protected virtual`) → linked-token registration setting
  `IsAbortRequested` → result.
- **No identity prompt; `_toc` is never touched.** `_loadedHeader` is refreshed.
- Map export is best-effort: a failed export is logged, the scan still succeeds.

**Pure statics** beside `JudgeFileOperation`: `VerbalizeScan(map) → (Level, Headline, Details)`,
`AdviseOnScan(map)`, `ScanAdviceText(advice)`. `AdoptRecoveredToc` and `RecoverTocFromCopy` exclude each
other.

**`ServiceScanProgressHandler : IProgress<TapeScanProgress>`** counts fragments, sets and recovered TOCs,
humanises the phase (`CurrentPhase`), and logs one sub-line per noteworthy fragment
(`static DescribeFragment`). Mark runs and the TOC mark are not logged. `ReportProgress` is the app hook;
`CompleteProgress` / `DisposeProgress` mirror the calibration handler.

### 9.2 Recover a TOC from the map

```csharp
public sealed record RecoverTocRequest(MediaScanMap Map, int FragmentOrdinal) : ServiceOperationRequest
{
    public bool    Adopt                  { get; init; } = false;
    public string? SaveToFilePath         { get; init; }       // needs media loaded
    public bool    ProceedOnMediaMismatch { get; init; } = false;   // adoption prompt only, never the guards
}

public sealed record RecoverTocResult : ServiceOperationResult
{
    public TapeTOC? Toc       { get; init; }
    public bool     FromMap   { get; init; }                  // recovered during the scan — no tape I/O
    public bool     Adopted   { get; init; }
    public string?  SavedPath { get; init; }
}

public Task<RecoverTocResult> RecoverTocAsync(RecoverTocRequest request);
```

- A fragment carrying a `HarvestedToc` is used as is — a **private copy**, no tape I/O.
- Otherwise the two guards of §8.4 around a probe agent's `RestoreTOCAt`.
- Save and adoption failures downgrade the outcome to Warning; the recovery itself stands.

---

## 10. Implementation plan

### 10.0 File layout

```
TapeLibNET/
  Scan/
    TapeScanner.cs               // the walk, termination, mark runs, progress, block-size guard
    TapeScanner.Identify.cs      // block 0 via probe agent; ReadFragmentAt; fragment factories
    TapeScanner.Harvest.cs       // HarvestLastTocCopy (§8.3)
    TapeMediaFragment.cs  MediaScanMap.cs  ScanMediaOptions.cs
  TapeMediaLayout.cs             // beside TapeNavigator
  TapeHeaderBlock.Identify.cs    // IdentifyBlock, TryIdentifyHeaderBlock, CarriesRecordSignature
  TapeFramer.cs                  // FrameStatus, TryUnpack
  TapeTOC.cs                     // + TryPeek
  TapeNavigator.cs               // + MoveToTOCCopyAt
  TapeStreamManager.cs           // + BeginReadTOCAt; BeginReadWrite(positioner)
  TapeAgentBase*.cs              // + ReadBomHeader(out bytesRead), RestoreTOCAt
  Services/
    TapeServiceBase.Scan.cs      // ScanMediaAsync, RecoverTocAsync, VerbalizeScan, AdviseOnScan
    ServiceOperationRequest.cs / ServiceOperationResult.cs / ServiceOperationProgressHandler.cs
```

Primitives the scanner *consumes* stay beside the code that owns them.

### Phase 0 — records and extractions ✅
`TapeMediaLayout.Predict`; `TryIdentifyHeaderBlock`; the records.
**Tests:** `TapeMediaLayoutTests`, `TapeMediaIdentifyTests`.

### Phase 1 — `TapeScanner` ✅
The walk (§3) and identification (§4), with four corrections found in implementation: the closing mark by
kind; `tapemark`/`eod` flags and synced hop errors; positive identification; mark detection by the ordinary
read with `TocMark` folding.
**Tests:** `TapeScannerTests` over all four profiles. Helper: `ScanMapAssert` (describes the whole map on
every failure; byte-level `MediaSnapshot`).

### Phase 2 — calibration cartridges ✅
**Tests:** `TapeScannerCalibrationTests` — field-by-field agreement with a direct `InspectMedia()`.

### Phase 3 — TOC recovery ✅
`MoveToTOCCopyAt`, `BeginReadTOCAt`, `RestoreTOCAt`, `TapeScanner.Harvest.cs`.
**Tests:** `TapeScannerHarvestTests` — each copy matches what was written; bad block fails cleanly; no
believed position afterwards; **ordinary `RestoreTOC` still works after `RestoreTOCAt`**; partitioned
initiator read; harvest leaves the map identical (SM-12); first copy damaged, second recovered; **a TOC the
navigator cannot find is still recovered**; never writes; recovered TOC matches the fragment's identity.

### Phase 4 — service ✅
`ScanMediaAsync`, `RecoverTocAsync`, the pure statics, `ServiceScanProgressHandler`, map export,
`TOCSource.Recovered`.
**Tests:** `ServiceScanMediaTests` — pure verbalize/advise over hand-built maps; recovery on by default;
advice without recovery; live TOC untouched; calibration advised, not inspected; cancellation yields an
aborted, truncated map; export round-trips; recovery from the map performs no tape I/O; recovery from tape;
**cartridge swap refused**; adoption through the import path; save round-trips through import; non-TOC
fragment rejected.

### Phase 5 — TapeWinNET · *hand-off task* ✅

> **The next task.** Add a *Scan Media* feature to TapeWinNET. The library and service layers are
> done and tested (`TapeServiceBase.ScanMediaAsync`, `RecoverTocAsync` — §9). This phase is **UI only**:
> no changes to `TapeLibNET`. Follow the existing *Calibrate* feature as the template throughout — it has the
> same shape (a setup dialog, a long-running cancellable operation in the shared overlay, a result window).

**P5.1 — Progress plumbing** *(template: `TapeService.Calibration.cs`, `WpfServiceHost.UpdateCalibrateProgress`)*
- New partial `TapeService.Scan.cs`: override `CreateScanProgressHandler` to return a private
  `GuiScanProgressHandler : ServiceScanProgressHandler` whose `ReportProgress` calls a new
  `WpfServiceHost.UpdateScanProgress(fragmentsFound, setsFound, tocCopiesFound, currentBlock, phase)`.
- `UpdateScanProgress` marshals to the dispatcher and sets scan progress properties on `MainViewModel`.
  There is no known total, so drive the progress bar by `currentBlock` against the
  estimated media capacity in blocks (if that proves too complex, then as indeterminate); text like
  *"Block 1 234 · 5 sets · 2 TOC copies found"*.

**P5.2 — `MainViewModel.Scan.cs`** *(template: `MainViewModel.Calibrate.cs`)*
- Fields/properties: `IsScanInProgress`, `ScanProgressPercent`, `ScanProgressText`, `CurrentScanPhase`,
  `IsAbortScanEnabled`; a `CancellationTokenSource` for the running scan.
- Wire `IsScanInProgress` into the **unified operation overlay** exactly as `IsCalibrateInProgress` is:
  `IsGeneralBusy`, `IsOperationInProgress`, `IsMediaBrowsingEnabled`, and each `Operation*` selector
  (`OperationProgressPercent`, `OperationProgressText`, `CurrentOperationFile`, `AbortOperationCommand`,
  `IsAbortOperationEnabled`, `AbortOperationButtonText` = "Abort Scan").
- Commands: `ScanMediaCommand` (enabled when `!IsBusy && IsMediaLoaded`), `AbortScanCommand` (cancels the
  token; no confirmation needed — a scan writes nothing). Register in an `InitializeScanCommands()` called
  from the constructor.
- `ExecuteScanAsync(ScanMediaRequest)`: set busy/in-progress state, await `ScanMediaAsync`, reset state in
  `finally`, then show the result window (P5.4). **Never reload the tree after a scan** — a scan changes
  nothing on tape and never touches the TOC.

**P5.3 — Scan setup dialog** *(template: `CalibrateWindow` + `CalibrationRunViewModel`, much simpler)*
- `ScanMediaWindow` + `ScanMediaViewModel`: one checkbox **"Try to recover the table of contents"**
  (ticked; → `RecoverTocCopies`), an optional **"Save scan map to…"** folder picker (→ `MapExportFolder`),
  an info line *"Reads the whole media; nothing is written"*, and [Scan] / [Cancel].
- Menu: **Media | Scan Media…** next to the existing media commands; optional toolbar button.

**P5.4 — Result window** *(template: `CalibrationWindow` + `CalibrationResultViewModel`)*
- `ScanResultWindow` + `ScanResultViewModel(TapeService, ScanMediaResult)`.
- **Header banner** from `result.Summary`, coloured by `result.Outcome` via the shared `WarningPanelStyle`.
  Beneath it, the detail lines (`TapeServiceBase.VerbalizeScan(map).Details`).
- **Fragment list** (`ListView`/`DataGrid`), one row per `map.Fragments`, in tape order. Columns:
  `#` · `Block` · `Kind` · `Identity` · `Detail`. Suggested rendering:

  | Kind | Kind text | Identity | Detail |
  |---|---|---|---|
  | MediaHeader | Media header | `Id` short · vol `Volume` | `Description` |
  | SetHeader | Backup set | `#VolumeSetIndex+1` | `Description`, `CreatedUtc`; **"never completed"** in warning colour when `!ClosedBySeparator` |
  | TOC | Table of contents | `v{TocVersion:X4}` | "recovered — N sets" / "not recovered: {Diagnosis}" / "found" |
  | TocMark | TOC mark | — | "gap + filemarks" |
  | MarkRun | Mark run | — | "N consecutive marks" |
  | Unknown | *Damaged* when `!Diagnosis.Success`, else *Unidentified* | — | diagnosis, or the fingerprint |
  | CalibrationHeader | Calibration run | profile key | — |

  Highlight rows with warning/error levels; keep it read-only.
- **Per-row actions** on a TOC row (context menu or row buttons):
  - **[Use this TOC]** → `RecoverTocAsync(new(map, ordinal) { Adopt = true })`; on success close the window
    and refresh the tree with the adopted TOC (as after an import: `UpdateTreeFromTOC` +
    `SelectMostRecentSet`). The existing `TOCSource.Recovered` status/placement wording applies.
  - **[Save as…]** → a `SaveFileDialog` (`TapeAgentBase.TOCFileExtension`), then `RecoverTocAsync(... {
    SaveToFilePath = path })`.
  - For a row without `HarvestedToc`, the first action reads **[Try to recover]** and does the same call —
    the service reads from tape and runs its swap guards. Show `result.Message` on failure.
- **Advice buttons** from `result.Advice` (use `TapeServiceBase.ScanAdviceText` as tooltip/label):
  - `InspectCalibrationCartridge` → close and invoke the existing `InspectCalibrationMediaCommand`
    (the calibration tree/pane is already shown when such a cartridge is loaded).
  - `AdoptRecoveredToc` / `RecoverTocFromCopy` → act on the **last** TOC row (the newest copy).
  - `ReviewUnclosedSet` → informational only for now (Repair Media is a later feature).
- **Footer:** [Save map…] (writes `map.ToJson()` to a `.tapescan` file, if not already exported — show
  `MapExportPath` when it was) and [Close].
- For `MediaKind == CalibrationCartridge` show the banner and the advice button instead of the list.
- For `WasAborted` / truncated maps keep the list and make the banner say the map is incomplete.

**P5.5 — Acceptance**
- A scan of a healthy virtual cartridge lists media header, every set, and the TOC copies (recovered).
- Abort mid-scan: overlay closes, result window shows a truncated map marked as aborted.
- [Use this TOC] replaces the tree; the media pane shows "(recovered TOC)" and the status bar says so.
- A calibration cartridge shows only the banner and the Inspect Media button.
- Nothing is written to tape in any path; the overlay locks tree/list browsing while scanning.

### Phase 6 — TapeConNET ✅

`tapecon scan-media [--no-recover-toc] [--export <dir>] [--json]` — prints the headline, details and one line
per fragment (`ServiceScanProgressHandler.DescribeFragment`); `--json` prints the map to stdout.
`tapecon recover-toc --map <file> --fragment <n> [--adopt] [--save <file>]` — loads a `.tapescan` via
`MediaScanMap.FromJson`; exit code distinguishes success, refused (swap guard), and failure.

---

## 11. Invariants

| | |
|---|---|
| **SM-1** | The scan writes nothing and moves no mark. |
| **SM-2** | The scan reads no TOC and assumes none. Every fragment derives from the medium alone. |
| **SM-3** | The map contains observations, never verdicts. |
| **SM-4** | An unidentifiable fragment never terminates the walk. |
| **SM-5** | A transport fault, an abort, or `MaxFragments` sets `Truncated`. A partial map is never presented as complete. |
| **SM-6** | Blank media yields an empty, untruncated map. Blank and broken are never the same result. |
| **SM-7** | A calibration cartridge is a complete result: identified, not walked. |
| **SM-8** | TOC recovery is best-effort: a failed read keeps the fragment, with the reason, and never fails the scan. |
| **SM-9** | The scanner restores the drive's block size and leaves no believed content position anywhere. |
| **SM-10** | A progress sink can abort a scan but never fail it. |
| **SM-11** | `BlockSpan` is an upper bound including marks, never a payload size. |
| **SM-12** | Each position is read once by the identification read; the only repositioning is the harvest's return to where the walk left the head. |
| **SM-13** | A recovered TOC is never adopted implicitly, and a TOC read after the scan is checked against the scanned cartridge before and after the read. |

---

## 12. What Repair Media inherits

- **v4's `ScanContentSets` is deleted**, replaced by `TapeScanner.Scan` plus a pure
  `Compare(MediaScanMap, TapeTOC) → IReadOnlyList<TapeSetScanEntry>`. Every v4 state survives in meaning.
- **The comparison is pure**, so v4's state model becomes testable with no tape at all.
- **v4's end-of-content bound is dropped**, and with it RM-3.
- **The TOC source list grows a third entry** — *recovered from this cartridge* (`TOCSource.Recovered`).
- **v4's `OnSetScanned` is dropped**; `ITapeFileNotifiable` is untouched.
- **Still required:** `SetWriteWitness`, `SealPartialSet`, `IsUndescribed` + TOC v0x0103, `BuildPlan`.
- **The apply phase stays in `TapeSetAgent`.**

**The user scans, chooses a TOC, sees the verdict table — and only then is anything destructive offered.**

---

## 13. Known limits

- **Legacy-shape calibration cartridges** map as foreign (§4.5).
- **Partitioned media**: TOC recovery uses the ordinary restore (§8.3).
- **A TOC newer than this build** is not recognized as a TOC copy (§4.4).
- **A record damaged in its signature** cannot be told from foreign data (§4.1).
- **Saving a recovered TOC needs media loaded** — `SaveTOCToFile` is an agent instance method. A static
  overload would lift this if it matters.
- **Real hardware:** two behaviours are confirmed only on the virtual backend — a read that meets a mark leaves
  the head past it (setmarks need `ReportSetmarks`), and EOD is reported through `eof` without `tapemark`.
  Worth confirming on the DLT family.

---

## 14. WPF — the viewer

Specified as Phase 5 (§10). In short: **Media | Scan Media…** → a one-checkbox setup dialog → the shared
operation overlay → a result window listing every fragment in tape order, with **[Use this TOC]**,
**[Save as…]** and **[Try to recover]** on TOC rows, one button per `ScanAdvice`, and [Save map…]. The seam
to Repair Media is a later **[Compare with a table of contents…]** button on the same window.
