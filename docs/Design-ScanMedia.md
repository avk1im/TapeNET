# Design — Scan Media (TOC-less media survey)

**Status:** v2.2 · Phases 0–2 implemented and green; this revision brings the document in line with the code
and specifies TOC recovery (§8) ahead of Phase 3.
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
  the damaged cartridge the feature exists for. The bound was load-bearing on healthy media and absent on
  damaged media. Walking to EOD needs no bound: the trailing structures are *identified*, not *avoided*.
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

`TapeNavigator.ProduceNavigator` is implemented **on top of** `Predict`, switching on `NavigatorKind`, so the
scanner and the navigator can never disagree about a cartridge's layout.

`UseSmks` stays a bool, deliberately: it is the navigator's own vocabulary, and there will be no third kind of
set delimiter.

**`SeparatorAmbiguousWithToc`** is the fact that makes identification, not mark-hopping, the scanner's core:
on the filemark layouts the set separator and the TOC delimiter are the same mark, so only classifying the
block after each mark can tell content from TOC.

---

## 3. The walk

From BOM, forward, to EOD. No bound, no TOC, no lookahead, no mark fishing.

```
layout = TapeMediaLayout.Predict(Drive)

// ── Block 0 — through a throwaway agent ─────────────────────────────────
first = IdentifyBomFragment(out bytesRead)        // probe.ReadBomHeader(out bytesRead)
if bytesRead ≤ 0            → Blank map (SM-6)
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
FoldMarkRun(pending); Report(last)
```

### 3.1 Block 0 goes through a probe agent

"Rewind, select the content partition, read block 0, classify" is a ceremony `TapeStreamManager` and
`TapeAgentBase.ReadBomHeader` already own. The scanner borrows it through a throwaway
`new TapeAgentBase(Drive, new TapeTOC())` — the same pattern `TapeServiceBase.RefreshLoadedHeader` uses —
rather than defining the most-read block in the product a second time.

`ReadBomHeader(out int bytesRead)` was added for this: `bytesRead ≤ 0` means *nothing is there* (blank),
positive means *something is there, recognized or not* (legacy or foreign). The parameterless overload
delegates; no existing caller changed. Without it, SM-6 — blank and broken are different results — could not
be kept.

From the second fragment on, the scanner reads raw: those blocks sit at positions the agent has no verb for,
and no navigator state is wanted.

### 3.2 The closing mark depends on the fragment's kind

| Fragment | Closed by |
|---|---|
| media header | **filemark**, on every layout (`TapeHeaderBlock.WritesTrailingMark`) |
| TOC copy | **filemark**, on every in-set layout, setmark ones included |
| set header, unidentified content | the layout's separator (`UseSmks`) |

Hopping a setmark from a media header on a setmark layout sails past the header's filemark, the whole first
set, and its closing setmark — losing set 1 silently. Hopping a setmark from `toc1` runs to EOD and never sees
`toc2`. `ClosingMarkIsSetmark` encodes this table.

A failed hop **syncs the drive's error** before judging it. The drive carries the reason; the scanner's own
error channel sees nothing unless synced, and reading `NO_ERROR` as a transport fault once truncated every
complete map.

### 3.3 Marks are detected by the ordinary read

A read that meets a mark returns nothing but the mark and leaves the head **past** it. So the identification
read doubles as the mark detector: each position is read exactly once, the head never steps back, and a drive
that cannot write consecutive marks pays nothing for the possibility.

`ReadFragmentAt` returns one of three outcomes:

| Outcome | Meaning | Walk |
|---|---|---|
| `Fragment` | a block, readable or not | commit it |
| `Tapemark` | a mark, already crossed | add to the pending run, read on |
| `EndOfData` | nothing was ever written here | end cleanly |

Both boundaries come from `TapeDrive.ReadDirect`'s `out` flags, surfaced by a
`TapeHeaderBlock.Read(…, out bool tapemark, out bool eod)` overload. The drive **resets** its error on both,
so the flags are the only reliable witness; `eod` is defined as the drive's `eof && !tapemark`.

### 3.4 Mark runs, and the TOC mark

Marks read between two data fragments form a pending run, committed by `FoldMarkRun` once the next data block
(or the end) shows where it stops:

- **`TocMark`** — on a `HasTocMark` layout, when the run holds **≥ 2** marks and the fragment before it is an
  `Unknown` that was **read cleanly** and closed. That is the `[gap][FM][FM][FM]` sequence: the gap block, its
  closing filemark, and two more. The gap is retyped rather than left "unidentified" on every healthy
  cartridge of that layout.
- **`MarkRun`** — every other run, as a fragment of its own at the run's first block.

Each guard has a reason. The layout must write TOC marks at all. Two marks, not one, keeps a legacy
double-filemark end of data from reading as a TOC mark. And a damaged header or an unreadable block is also
`Unknown`, but no gap — folding it would hide the damage.

`MarkCount` is informational. Nothing depends on a drive reporting exactly one mark per read; "two or more" is
all the fold asks.

### 3.5 Termination

| Condition | Map | Fragment |
|---|---|---|
| EOD after a mark | complete, `ERROR_NO_DATA_DETECTED` | last fragment closed |
| closing-mark hop fails positionally | complete, that error | last fragment **not closed** |
| closing-mark hop fails otherwise | **Truncated** | last fragment not closed |
| abort | **Truncated**, `ERROR_CANCELLED` | — |
| `MaxFragments` (fragments or run length) | **Truncated**, `ERROR_INVALID_DATA` | — |
| calibration header at block 0 | complete, `NO_ERROR` | one fragment |

No pseudo-fragment ever closes a map. *Why the walk ended* is map-level (`TerminatorWin32`, `Truncated`);
*was this fragment finished* is fragment-level (`ClosedBySeparator`). An unclosed set header is precisely the
"backup died mid-set" signature.

**No retry, no resync-by-seeking.** Skipping a bad region by seeking blind is guessing at block numbers on
damaged tape. Stop, record where, and say the map is truncated.

### 3.6 Fragments are reported when final

A fragment's closing state, its span and a `TocMark` retype are all decided by reads that come *after* it. So
`Commit` reports a fragment's predecessor, and the last fragment is reported when the walk ends. A sink never
receives a value that later changes.

### 3.7 Cost

One 16 KiB read per fragment and per mark, plus one mark hop per data fragment. Abortable between reads; an
abort yields a truncated map still usable up to the cut.

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
length prefix; a TOC stream carries it at byte 0. Each outcome rests on evidence in the block itself — none is
reached because another test failed. Inferring "TOC" from *"our signature, but no header parses"* once turned
every CRC-damaged set header into a phantom TOC copy.

| Order | Test | Outcome |
|---|---|---|
| 1 | `TapeTOC.TryPeek` at offset 0 | `TocCopy`, with version and media id |
| 2 | our signature at offset 4, then `TapeFramer.TryUnpack` | `Header`, or `DamagedRecord` with the reason |
| — | neither | `Foreign` |

**TOC first.** Offset 0 of a plausible frame holds a length of at most 16 KiB, whose high bytes are zero — it
can never read as the signature plus a version. Offset 4 of a TOC stream holds the low bytes of its UID seed,
which can. So a TOC match cannot swallow an intact frame, while the reverse order could.

A record whose **signature** is damaged cannot be told from foreign data, and is reported as `Foreign`.

### 4.2 The framer tells damage from absence

```csharp
public enum FrameStatus { Ok, NotFramed, CrcMismatch, Unparseable }
public static FrameStatus TapeFramer.TryUnpack<T>(byte[] block, int length, out T? record);
```

`Unpack` delegates and keeps its contract — null unless `Ok` — so the calibration resume walk is untouched.
`Unparseable` means a good CRC around a payload this build cannot read: an unknown kind, or a newer version.

A `DamagedRecord` maps to an `Unknown` fragment with a fingerprint and a diagnosis: `ERROR_CRC` for a CRC
mismatch, `ERROR_INVALID_DATA` for a torn frame or an unparseable payload. It stays `Unknown` because its
fields cannot be trusted; *why* is the diagnosis.

### 4.3 The signature probe tolerates the version

`ValidateSignature()` demands exactly `TapeSerializer.Version` (0x0101). The TOC is written as 0x0102, and a
future header format would be newer still. The probe therefore accepts any **0x01xx** version: our record,
possibly unreadable to this build. The two signature bytes alone match random data once in 65 536 blocks; the
version range brings that to once in 16 million.

### 4.4 TOC copies are recognized structurally

```csharp
public static bool TapeTOC.TryPeek(byte[] block, int length, out ushort version, out Guid mediaId);
```

The signature alone cannot identify a TOC: a legacy aligned file record (`TapeFileInfo.SerializeHeaderTo`)
opens a block with the very bytes a v0x0101 TOC does. `TryPeek` reads in exactly `ConstructFrom`'s order and
checks what only a TOC has:

1. the signature, with a version between `TocVersionInitial` and `TocVersion`;
2. a non-zero `nextUID`;
3. the `MediaId`, from `TocVersionWithMediaId` on;
4. a plausible set count;
5. then either the **first set's own signature** — checked strictly, as `TapeSetTOC.ConstructFrom` does — or,
   for an empty TOC, a plausible description, timestamps and volume.

It sits in `TapeTOC.cs` directly below `ConstructFrom`, so a change to the TOC layout is visibly a change to
the peek too. A version newer than this build knows is **not** recognized: its layout past the signature is
unknown, and a structural check would be guessing.

The TOC is written at the fixed 16 KiB block size (`TapeAgentBase.c_fixedTOCBlockSize`), so one standard
header-block read delivers its first block whole.

### 4.5 Calibration cartridges — identified, not walked

A `TapeCalibrationHeader` at block 0 makes the map complete, `Kind = CalibrationCartridge`, one fragment,
`Truncated = false`. Past the header lies filemark-delimited checkpoints separated by gigabytes of random
padding; walking it would yield hundreds of `Unknown` fragments and tell nobody anything.

With `InspectCalibrationTrail`, the scanner calls `new TapeCalibrator(Drive).InspectMedia()` and attaches the
result as `MediaScanMap.CalibrationInfo` — the same answer the calibration UI shows, by construction.
**Off by default:** the feature serves backup tapes, and the service points a calibration cartridge at
Calibrate | Inspect Media instead (§9).

**Not recognized: legacy-shape calibration headers** written in the run block (pre-`TapeHeaderBlock`, or on a
drive whose maximum block is under 16 KiB). Such a cartridge maps as foreign data. The calibrator's own
two-step probe remains the supported path; scarcity in the field does not warrant a second rewind on every
medium.

### 4.6 Small-block drives

`TapeHeaderBlock.IsSupportedBy` is false when the drive's maximum block is under 16 KiB. `ReadIdentificationBlock`
then reads one native block with `ReadDirect` and classifies that. Without it, every fragment on such a drive
would map as `Unknown`.

### 4.7 What identification cannot do

A fragment's contents are opaque. The scan proves an object begins at a block and that a mark closes it. It
never reads files, never checks file CRCs, and never measures a set's payload. `BlockSpan` is an upper bound
including marks.

---

## 5. The records

```csharp
public enum FragmentKind                       // an OBSERVATION, never a verdict (SM-3)
{ Unknown = 0, MediaHeader, SetHeader, CalibrationHeader, TOC, MarkRun, TocMark }
                                               // appended in this order; JSON stores the numeric value

public enum ScannedMediaKind { Blank = 0, Backup, CalibrationCartridge, Foreign }

public sealed record TapeMediaFragment
{
    public required int          Ordinal    { get; init; }   // 0-based, contiguous; assigned by Commit
    public required long         StartBlock { get; init; }
    public required FragmentKind Kind       { get; init; }
    public long BlockSpan          { get; init; } = -1L;     // upper bound INCLUDING marks (SM-11)
    public bool ClosedBySeparator  { get; init; }

    // Identity — headers; for a TOC copy, the media id it describes
    public Guid?     Id             { get; init; }            // MediaId / calibration RunId / TOC MediaId
    public int?      Volume         { get; init; }
    public int?      VolumeSetIndex { get; init; }
    public int?      GlobalSetIndex { get; init; }
    public string?   Description    { get; init; }            // label / set description / ProfileKey
    public DateTime? CreatedUtc     { get; init; }
    public uint?     BlockSize      { get; init; }

    // TOC
    public ushort?  TocVersion   { get; init; }
    [JsonIgnore] public TapeTOC? HarvestedToc { get; init; }  // only when recovery ran and succeeded (§8)

    // MarkRun / TocMark / Unknown
    public int        MarkCount   { get; init; }              // informational
    public string?    Fingerprint { get; init; }              // first 32 bytes, hex
    public TapeResult Diagnosis   { get; init; } = TapeResult.OK;   // default(TapeResult) means FAILURE
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
    public static MediaScanMap? FromJson(string json);        // null on anything that is not a map
}
```

**Deliberately absent: any notion of "expected", "complete" or "lost".** Those are comparison verdicts. The
map states what exists, which is what lets one scan be compared against several TOCs.

`LastSetUnclosed` looks at the last **set header**, not the last fragment, so a trailing TOC copy cannot mask
an unclosed set. `MixedIdentityFragments` is empty without a media header — there is nothing to disagree with.
`SetIndexGaps` reports forward jumps only; a repeat or a step backwards is stranger than a missing set and
belongs to comparison.

**`TapeMediaFragment.ToString`** carries the diagnosis and a short fingerprint, so a failing scan test prints
a map that says *why* a fragment is unknown.

---

## 6. What the map tells the user — with no TOC at all

- **How many backup sets the cartridge holds** — counted from the tape.
- **Every set named and dated**, from its own header.
- **The last set never completed** — `LastSetUnclosed`, the dominant real-world fault.
- **A damaged set header**, with the CRC diagnosis — distinct from foreign data.
- **Mixed identity** — another series overwrote part of this cartridge.
- **Index gaps** — a set missing from the middle.
- **Where TOC copies survive**, their version, and which series they describe — and, with recovery, the TOC
  itself (§8).
- **A calibration cartridge**, identified as such.
- **On healthy media, a complete and correct inventory** — a legitimate Media Properties deep-dive. A feature
  users exercise on good media is one they trust on bad media.

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
    public bool HarvestTocCopies        { get; init; } = false;   // §8
    public bool InspectCalibrationTrail { get; init; } = false;   // §4.5
    public int  MaxFragments            { get; init; } = DefaultMaxFragments;
    public static ScanMediaOptions Default { get; } = new();
}
```

**Not a `TapeAgentBase`.** `TapeNavigator` is TOC-bound by construction — believed positions,
`CurrentContentSet`, `FirstSetOnVolume` — and the scanner needs raw mark hops and absolute block numbers.
Inheriting it would mean spending the whole walk keeping it quiet, and the constructor demands a TOC the
feature by definition does not have. The handover to comparison is a `MediaScanMap` — a pure value — so
inheritance buys the later phases nothing.

**Where an agent still earns its keep, it is borrowed, not inherited:** block 0 (§3.1) and TOC recovery (§8).
Both are ceremonies the agent already owns.

**A `record`, not a `record struct`, for the options.** On a struct, `new()` binds to the implicit
parameterless constructor and zeroes every field, silently skipping the primary constructor's defaults.

`Scan` returns null only when no map is possible at all (no media); a truncated map is still returned. It
never throws at its caller — an unexpected exception is latched into `LastResult`. A `BlockSizeGuard`
restores the drive's block size on the way out (SM-9).

### 7.2 Progress: `IProgress<TapeScanProgress>`

```csharp
public readonly record struct TapeScanProgress(
    int FragmentOrdinal, long CurrentBlock, FragmentKind Kind, string Phase, TapeMediaFragment? Fragment);
// Phases: "scanning", "harvesting-toc", "inspecting-calibration", "completing"
```

Not `ITapeFileNotifiable`: the scan uses none of its members, and its `void` callbacks can stop an operation
only by throwing. The scanner owns a cooperative abort flag instead. `IProgress<T>` mirrors
`TapeCalibrationProgress`.

**The deliberate divergence from the calibrator.** The calibrator's `TapeAbortRequestedException` handling is
purely defensive. The scanner's is real — a WPF sink marshalling to the UI thread may throw — so `Report`
converts it to `IsAbortRequested` and stops at the next clean boundary; any other exception is logged and
swallowed (SM-10).

---

## 8. TOC recovery

A TOC copy the scan located is only an address until someone reads it. Recovering it is the feature's biggest
payoff on a damaged backup tape — and the one place where the scanner needs machinery it deliberately does not
own.

### 8.1 One verb, two callers

Two ways to recover were weighed:

- **(1) point an agent at the copy** — a dedicated verb that reads a TOC at a given block, bypassing the
  navigator's own search for it;
- **(2) teach the scanner to read TOCs.**

(2) would duplicate the TOC stream ceremonies — `TapeTOCStream` creation, the fixed TOC block size, the
multi-block read — and they would drift. (1) keeps them in one place. But the scanner has one genuine
advantage: during the walk, **the head is already standing on the copy**.

The resolution takes both: **the verb lives on the agent; the scanner borrows it**, exactly as it borrows
`ReadBomHeader` for block 0. The walk's "try to recover the TOC" option and the service's separate
"recover this copy" operation then run the same code.

### 8.2 The agent verb — NEW

```csharp
// TapeAgentBase — a utility verb; callers construct the agent over a throwaway TOC
public TapeResult RestoreTOCAt(long block);
```

- `Manager.EndReadWrite()`; content partition; `Drive.MoveToBlock(block)`.
- Read the TOC stream at the fixed TOC block size, with the same stream machinery the ordinary TOC restore
  uses — **minus the navigator's locate step**. The caller has already located it, from evidence; letting the
  navigator search a tail-damaged cartridge on its own is the very thing this avoids.
- `Navigator.ResetContentSet()` afterwards: raw positioning, no believed position.
- Returns the TOC, or null with `LastResult` set. Never throws.

**To confirm against `TapeStreamManager` in Phase 3:** whether the existing TOC read can begin at the current
position. If its entry point locates on its own, split *locate* from *read* — the same split that made
`ReadBomHeader(out bytesRead)` possible — and have both the old path and `RestoreTOCAt` call the read half.

**Out of scope: partitioned media.** Its TOC sits at a fixed place in partition 0, which content-partition
tail damage cannot reach; the ordinary TOC restore already goes straight there. The scan walks the content
partition only and finds no TOC fragments on that layout.

### 8.3 During the scan — `HarvestTocCopies`

When the walk identifies a `TOC` fragment and the option is set:

1. `ReportPhase("harvesting-toc")`.
2. `probe.ReadTOCAt(fragment.StartBlock)` through a throwaway agent.
3. Success ⇒ attach as `HarvestedToc`. Failure ⇒ keep the fragment as detected, log, carry on (SM-8).
4. **`Drive.MoveToBlock(fragment.StartBlock)`** — restore the walk's position contract. The stream read may
   have run into or past the copy's closing filemark; from the copy's first block, the regular
   `CrossClosingMark` finds that mark again. One short locate buys a walk that does not depend on where the
   TOC reader happened to stop (SM-12).

Every copy found is attempted, not just the first: on the filemark layouts two are written, and the second
routinely survives when the first is the casualty. Two copies that both parse but differ are themselves a
finding, reported by the comparison phase.

The option stays **off by default at the scanner**: a scanner call that did not ask for it should not read
multi-block streams. The service turns it on (§9).

### 8.4 After the scan — a separate operation

The user may skip recovery during the scan and ask for it later, against one fragment of the map. The service
operation (§9.2) is a thin wrapper over the same verb, with one addition: **verify before reading.** Between
scan and recovery the user may have swapped cartridges, so the service first re-reads the block at
`StartBlock` and requires `TryPeek` to find a TOC copy of the same media id there. Otherwise it refuses —
reading a TOC off the wrong cartridge and adopting it is the worst outcome this feature could produce.

### 8.5 Adoption goes through the import path

A recovered TOC is treated exactly like one imported from a `.tapetoc` file: the same identity checks, the
same `TocChanged` notification, the same user decision. Recovery never replaces the loaded TOC on its own
(SM-13) — the user adopts it, or saves it as a file, or both.

---

## 9. The service layer

`TapeServiceBase.Scan.cs`.

### 9.1 Scan

```csharp
public sealed record ScanMediaRequest : ServiceOperationRequest          // NEW
{
    public bool    RecoverTocCopies { get; init; } = true;   // → scanner HarvestTocCopies
    public string? MapExportFolder  { get; init; }           // null ⇒ do not export
}

public enum ScanAdvice                                       // NEW — drives the UI's follow-up buttons
{
    InspectCalibrationCartridge,    // a calibration cartridge: use Calibrate | Inspect Media
    AdoptRecoveredToc,              // a TOC copy was recovered during the scan
    RecoverTocFromCopy,             // TOC copies found but not (successfully) recovered
    ReviewUnclosedSet,              // the last set never completed — Repair Media, once it lands
}

public sealed record ScanMediaResult : ServiceOperationResult           // NEW
{
    public MediaScanMap? Map { get; init; }
    public ScannedMediaKind MediaKind { get; init; }
    public int  SetsFound        { get; init; }
    public int  TocCopiesFound   { get; init; }
    public int  TocsRecovered    { get; init; }
    public int  UnknownFragments { get; init; }
    public bool LastSetUnclosed  { get; init; }
    public IReadOnlyList<ScanAdvice> Advice { get; init; } = [];
    public string? MapExportPath { get; init; }
    public string Summary { get; init; } = string.Empty;
}

public Task<ScanMediaResult> ScanMediaAsync(ScanMediaRequest request);
```

- **Calibration inspection is never requested.** The service recognizes the cartridge and advises
  `InspectCalibrationCartridge`, pointing the user at the calibration UI — which owns that answer and can act
  on it (Resume, Recalibrate). The scan feature serves backup tapes: a calibration run can be reproduced at the
  cost of time; the data on a backup tape cannot be restored any other way.
- **TOC recovery is on by default here.** The tape is already positioned, the cost is a few blocks per copy,
  and the payoff is the index a damaged cartridge's user most needs.
- A near-twin of `ExecuteCalibrateAsync`: `Task.Run` → `_operationLock` → construct the scanner →
  `CreateScanProgressHandler` (`protected virtual`) → a linked-token registration setting
  `IsAbortRequested` → result from `scanner.LastResult`. Derived from `ServiceOperationResult` directly.
- **No media-identity prompt.** There is no expectation to violate.
- **`_toc` is never touched** by a scan; `_loadedHeader` is refreshed, since a scan may be the first thing to
  establish what the cartridge is.
- The drive position is unspecified afterwards; agents are per-operation, so nothing inherits a stale belief.

`VerbalizeScan(map)` and `AdviseOnScan(map)` sit beside `JudgeFileOperation` as **pure statics**.

### 9.2 Recover a TOC from the map

```csharp
public sealed record RecoverTocRequest(MediaScanMap Map, int FragmentOrdinal)
    : ServiceOperationRequest                                            // NEW
{
    public bool    Adopt          { get; init; } = false;   // load it as the current TOC (§8.5)
    public string? SaveToFilePath { get; init; }            // also save as .tapetoc
}

public sealed record RecoverTocResult : ServiceOperationResult           // NEW
{
    public TapeTOC? Toc     { get; init; }
    public bool     FromMap { get; init; }                  // already recovered during the scan — no tape I/O
    public bool     Adopted { get; init; }
    public string?  SavedPath { get; init; }
}

public Task<RecoverTocResult> RecoverTocAsync(RecoverTocRequest request);
```

- If the fragment already carries a `HarvestedToc`, use it — **no tape I/O**. This is how the app's "use this
  TOC" button after a scan works.
- Otherwise: refresh the loaded header; require the map's media id; verify the block (§8.4); read through a
  probe agent's `ReadTOCAt`.
- Adoption and saving reuse the import and export paths.

---

## 10. Implementation plan

### 10.0 File layout

```
TapeLibNET/
  Scan/
    TapeScanner.cs               // the walk, termination, mark runs, progress, block-size guard
    TapeScanner.Identify.cs      // block 0 via probe agent; ReadFragmentAt; fragment factories
    TapeScanner.Harvest.cs       // NEW (Phase 3) — the harvest step of §8.3
    TapeMediaFragment.cs         // FragmentKind, TapeMediaFragment
    MediaScanMap.cs              // MediaScanMap, ScannedMediaKind
    ScanMediaOptions.cs          // ScanMediaOptions, TapeScanProgress
  TapeMediaLayout.cs             // beside TapeNavigator — the navigator factory is built on it
  TapeHeaderBlock.Identify.cs    // IdentifyBlock, TryIdentifyHeaderBlock, CarriesRecordSignature
  TapeFramer.cs                  // FrameStatus, TryUnpack
  TapeTOC.cs                     // + TryPeek, beside ConstructFrom
  TapeAgentBase.Headers.cs       // + ReadBomHeader(out bytesRead); + ReadTOCAt (Phase 3)
  Services/
    TapeServiceBase.Scan.cs      // Phase 4
    ServiceScanProgressHandler.cs
```

Primitives the scanner *consumes* — header identification, layout prediction, the TOC peek, the agent verbs —
stay beside the code that owns them. Filing them under `Scan/` would have core code reaching into a survey
feature's folder for its own building blocks.

### Phase 0 — records and extractions ✅

`TapeMediaLayout.Predict` with `ProduceNavigator` rebuilt on it; `TryIdentifyHeaderBlock`; the records.
**Tests:** `TapeMediaLayoutTests` (agreement with the factory on all four profiles, prediction without media),
`TapeMediaIdentifyTests` (totality, signature probe, map views, JSON round-trip).

### Phase 1 — `TapeScanner` ✅

The walk as in §3, identification as in §4, including four corrections made during implementation:

- the media header's filemark crossed explicitly — then generalized to "the kind decides the closing mark";
- `TapeHeaderBlock.Read` surfacing `tapemark` / `eod`, and failed hops syncing the drive's error;
- positive identification (`IdentifyBlock`, `TryUnpack`, `TryPeek`), ending phantom TOC copies;
- mark detection by the ordinary read, with `TocMark` folding — no mark fishing.

**Tests:** `TapeScannerTests` over all four profiles — healthy media, single set, header only, damaged tail,
corrupt set header, read fault, content without a TOC, blank, legacy, foreign, adjacent marks, transport
fault, abort, throwing sinks, `MaxFragments`, progress order, never-writes, block-size restore. Helper:
`ScanMapAssert` (describes the whole map on every failure; byte-level `MediaSnapshot`).

### Phase 2 — calibration cartridges ✅

**Tests:** `TapeScannerCalibrationTests` — the scan agrees field by field with a direct `InspectMedia()` over
early, mid-body and complete trails; identified without inspection; non-destructive (resume still succeeds);
block size restored.

### Phase 3 — TOC recovery

1. Confirm the TOC read path in `TapeStreamManager`; split locate from read if needed.
2. `TapeAgentBase.ReadTOCAt(long block)` (§8.2).
3. `TapeScanner.Harvest.cs`: the §8.3 step, with the position restore.

**Tests:**
- `ReadTOCAt_AtAKnownCopy_MatchesTheTocThatWasWritten` — deep compare, all in-set profiles;
- **`FirstTocCopyDamaged_SecondIsStillRecovered`** — corrupt `toc1`; `toc2` comes back intact;
- `Harvest_OnHealthyMedia_RecoversEveryCopy_AndTheMapIsUnchanged` — the same fragments, blocks and kinds as a
  scan without harvesting. **This pins SM-12**: a harvest that disturbed the walk would shift or lose fragments;
- `HarvestFailure_KeepsTheFragment_AndDoesNotFailTheScan`;
- `Harvest_NeverWrites` — `MediaSnapshot` before and after;
- `Harvest_OnTailDamagedMedia_RecoversTheToc` — the scenario the feature exists for: `EraseLastSetmark`,
  then recover a TOC the navigator could not have located on its own.

### Phase 4 — service

`ScanMediaAsync`, `RecoverTocAsync`, `VerbalizeScan`, `AdviseOnScan`, `ServiceScanProgressHandler`, map export.

**Tests — `ServiceScanMediaTests`:**
- **`VerbalizeScan` and `AdviseOnScan`, pure, from hand-built maps** — healthy, unclosed tail, mixed identity,
  index gap, blank, calibration, truncated, copies found but not recovered;
- `CalibrationCartridge_AdvisesInspect_AndDoesNotInspect`;
- `Scan_RaisesNoPrompt`; `Cancellation_ViaRequestToken_AbortsTheScan`; `Scan_DoesNotMutateLiveToc`;
- `RecoverToc_FromMap_UsesTheHarvest_WithoutTapeIo`;
- **`RecoverToc_AfterCartridgeSwap_Refuses`** — the verify-before-read guard of §8.4;
- `RecoverToc_Adopt_GoesThroughTheImportPath` — `TocChanged` fired, identity checks applied;
- `MapExport_WritesReloadableJson`.

### Phase 5 — TapeConNET

`tapecon scan-media [--no-recover-toc] [--export <dir>] [--json]` and
`tapecon recover-toc --map <file> --fragment <n> [--adopt] [--save <file>]`.

### Phase 6 — WPF viewer (§14)

---

## 11. Invariants

| | |
|---|---|
| **SM-1** | The scan writes nothing and moves no mark. |
| **SM-2** | The scan reads no TOC and assumes none. Every fragment derives from the medium alone. |
| **SM-3** | The map contains observations, never verdicts. |
| **SM-4** | An unidentifiable fragment never terminates the walk; only end-of-data, a failed closing-mark hop, a transport fault, a calibration header, an abort, or `MaxFragments` does. |
| **SM-5** | A transport fault, an abort, or `MaxFragments` sets `Truncated`. A partial map is never presented as complete. |
| **SM-6** | Blank media yields an empty, untruncated map. Blank and broken are never the same result. |
| **SM-7** | A calibration cartridge is a complete result: identified, not walked, `Truncated` false. |
| **SM-8** | TOC recovery is best-effort: a failed read keeps the fragment as detected and never fails the scan. |
| **SM-9** | The scanner restores the drive's block size and leaves no believed content position anywhere. |
| **SM-10** | A progress sink can abort a scan but never fail it. |
| **SM-11** | `BlockSpan` is an upper bound including marks, never a payload size. |
| **SM-12** | Each position is read once, by the identification read; the only repositioning in a walk is the harvest's return to the copy it read. |
| **SM-13** | A recovered TOC is never adopted implicitly, and is never read from a block not re-verified as a TOC copy of the same media. |

---

## 12. What Repair Media inherits

A strict subset; blocks nothing in `Design-RepairMedia.md` v4.

- **v4's `ScanContentSets` is deleted**, replaced by `TapeScanner.Scan` plus a comparison step.
  `TapeSetScanEntry` becomes derived: `Compare(MediaScanMap, TapeTOC) → IReadOnlyList<TapeSetScanEntry>`, a
  pure function. Every v4 state survives in meaning: `Complete`, `Unverified`, `Undescribed`, `Partial`,
  `Damaged`, `Foreign`, `Absent`. A `DamagedRecord` fragment inside the TOC's range with a closed separator is
  v4's `Unverified`.
- **The comparison is pure**, so v4's whole state model becomes testable with no tape at all.
- **v4's end-of-content bound is dropped**, and with it RM-3.
- **v4's TOC source list grows a third entry** — *recovered from this cartridge* — via `RecoverTocAsync`.
- **v4's `OnSetScanned` is dropped**; `ITapeFileNotifiable` is untouched by this feature.
- **Still required:** `SetWriteWitness`, `SealPartialSet`, `IsUndescribed` + TOC v0x0103, `BuildPlan`.
- **The apply phase stays in `TapeSetAgent`.** Scan leaving the hierarchy is not a precedent for repair.

**The user scans, chooses a TOC, sees the verdict table — and only then is anything destructive offered.**

---

## 13. Known limits

- **Legacy-shape calibration cartridges** map as foreign (§4.5).
- **Partitioned media**: TOC recovery uses the ordinary restore (§8.2).
- **A TOC newer than this build** is not recognized as a TOC copy (§4.4); it maps as `Unknown` with a
  fingerprint.
- **A record damaged in its signature** cannot be told from foreign data (§4.1).
- **Real hardware:** two behaviours are relied on and confirmed only on the virtual backend — a read that meets
  a mark leaves the head past it (setmarks need `ReportSetmarks`), and EOD is reported through `eof` without
  `tapemark`. Worth confirming on the DLT family, where consecutive filemarks matter.

---

## 14. WPF — the viewer

- **Media | Scan Media…** — one button, a **"Try to recover the table of contents"** checkbox (ticked by
  default), progress by fragment, cancellable.
- **The map** as a flat list in tape order, one row per fragment:

  | # | Block | Kind | Identity | Detail |
  |---|---|---|---|---|
  | 0 | 0 | Media header | `{a4f…}` vol 1 | "Archive 2026" |
  | 1 | 2 | *Damaged header* | — | CRC mismatch |
  | 2 | 5 | Backup set | #2 | "Monthly — April", 2026-04-30 |
  | 3 | 14 | TOC mark | — | gap + 3 filemarks |
  | 4 | 18 | Table of contents | v0x0102 | recovered — **[Use this TOC]** **[Save as…]** |
  | 5 | 20 | Table of contents | v0x0102 | not recovered — **[Try to recover]** |

- **A plain-language header line** from `VerbalizeScan`, and one button per `ScanAdvice`. For a calibration
  cartridge: *"This is a calibration cartridge"* and **[Open in Calibrate | Inspect Media]**, instead of a
  fragment list.
- **Actions:** [Save map…], and — once Repair Media lands — **[Compare with a table of contents…]**, the seam
  between the two features.
