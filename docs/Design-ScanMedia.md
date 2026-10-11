# Design — Scan Media (TOC-less media survey)

**Status:** v3.2 · Phases 0–7 implemented. v3.2 adds the **partition update** (SM-14: every partition walked, each
fragment records its partition), the **nearest-mark rule** (§3.2), **header-less 2.1 sets** (`FragmentKind.SetContent`)
and the **format 2.1** identification order (§4.1).

**Scope:** read a cartridge — every partition it has — forward, and produce a **fragment map**: everything on the medium
that can be identified, with **no table of contents in hand and none assumed**. Then, as an option or as a separate
operation, **recover a table of contents** from a copy the map located.

**Relationship to Design-RepairMedia.md v4:** this is that design's `ScanContentSets` primitive, extracted, decoupled from
the TOC, and moved out of the agent hierarchy. Repair Media becomes *Scan + Compare + Apply*; this document delivers
**Scan** and **TOC recovery**, and §12 states what the other two inherit.

*The code is the authority.* Everything described here exists.

---

## 1. Why the TOC-less form is the better primitive

The v4 walk took the TOC as its frame: it iterated `FirstSetOnVolume..LastSetOnVolume` and asked *"does the tape agree?"*
That inverts the dependency on the one cartridge that matters. Dropping the TOC removes three genuine defects:

- **No end-of-content bound.** v4 established one with a `MoveToEndOfContent()` seek — which fails on exactly the damaged
  cartridge the feature exists for. Walking to EOD needs no bound: the trailing structures are *identified*, not
  *avoided*.
- **The TOC area is a harvest, not a hazard.** v4 treated `[toc1][FM][toc2]` as a trap to stop short of. It is the most
  valuable thing on a damaged cartridge: a TOC copy the user no longer has — including `toc2` when `toc1` is the casualty.
- **Findings stop being contingent.** A fragment means *"this object exists at this block of this partition"* — true
  whichever TOC surfaces later, serializable, and diffable against several TOCs in turn. Scan once, compare many.

The scan writes nothing, so it carries no destructive path, no witness problem, and no confirmation UX.

---

## 2. What is knowable before the tape moves

The layout is **derived** from drive capabilities and media organization alone:

| Condition | Navigator | Set separator | Trailing structure |
|---|---|---|---|
| partitioned | `TapeNavigatorTOCInPartition` | SM if supported, else FM | separator, then EOD; TOC in the initiator partition |
| setmarks supported | `TapeNavigatorTOCInSetWithSmks` | SM | `[SM][toc1][FM][toc2][FM]` |
| sequential filemarks + TOC mark | `TapeNavigatorTOCInSetWithFmksAndTOCMark` | FM | `[FM][gap][FM][FM][FM][toc1][FM][toc2][FM]` |
| otherwise | `TapeNavigatorTOCInSetWithFmks` | FM | `[FM][toc1][FM][toc2][FM]` |

```csharp
// TapeLibNET.Media
public readonly record struct TapeMediaLayout(
    string NavigatorKind,       // the navigator's type name — for display and the map
    bool   UseSmks,             // real setmarks close content sets
    bool   TocInPartition,
    bool   HasTocMark,
    bool   MediaLoaded);        // false ⇒ partitioning was not observable; TocInPartition is a floor

public static TapeMediaLayout Predict(TapeDrive drive, bool useTOCMark = true);   // pure; works without media
public bool SeparatorAmbiguousWithToc { get; }    // true on the filemark layouts
```

`TapeNavigator.ProduceNavigator` is implemented **on top of** `Predict`, so the scanner and the navigator can never
disagree about a cartridge's layout.

**`SeparatorAmbiguousWithToc`** is the fact that makes identification, not mark-hopping, the scanner's core: on the
filemark layouts the set separator and the TOC delimiter are the same mark.

**The layout predicts; the scan does not rely on it.** Every partition the medium has is walked and identified the same
way (§3, SM-14). The layout decides only which mark closes a fragment by *convention* (§3.2) and what counts as *out of
place* (§5).

---

## 3. The walk

Every partition, forward, to its end of data. Content first, then — on partitioned media — the initiator partition. No
bound, no TOC, no lookahead.

```
layout = TapeMediaLayout.Predict(Drive)

// ── Content partition ─────────────────────────────────────────────────────
first = IdentifyBomFragment(out bytesRead)     // probe.ReadBomHeader(out bytesRead, out block)
if bytesRead ≤ 0               → content partition BLANK — continue (SM-6)
elif first is CalibrationHeader → one-fragment map, stop (SM-7, §4.5)
else WalkPartition(Content, crossFirst: true)

// ── Initiator partition (PartitionCount > 1) ──────────────────────────────
if aborted or MaxFragments hit → initiator NOT SCANNED (truncated, 0 fragments)
else MoveToPartition(Initiator, 0)             // unreachable ⇒ truncated
     WalkPartition(Initiator, crossFirst: false)
MoveToPartition(Content, 0)                    // courtesy, best effort

WalkPartition(p):
  loop:
    abort / MaxFragments guard                 → Truncated
    if the last fragment of p is data:
        CrossClosingMark(last)                 // convention, or the NEAREST mark (§3.2)
        failed, positional → last NOT closed; clean end of p
        failed, otherwise  → transport fault; Truncated
    ReadFragmentAt(position):
        Tapemark  → count into the pending run; read on (the read crossed it)
        EndOfData → clean end of p
        Fragment  → stamp p; FoldMarkRun(pending); Commit(fragment)
                    if TOC && HarvestTocCopies → HarvestLastTocCopy (by partition, §8.3)
  FoldMarkRun(pending); Report(last of p)

Kind: Blank ⇔ no fragment in any partition; Backup ⇔ any fragment is ours; else Foreign
```

**Content first, always.** Block 0 of the content partition decides whether the cartridge is a calibration cartridge
(SM-7), and keeping it first keeps `Fragments[0]` meaning *"block 0 of the content partition"* whenever that block holds
anything.

**A blank content partition does not end the scan.** A freshly formatted partitioned cartridge carries a TOC in its
initiator partition and nothing in content. The cartridge is `Blank` only when every partition is.

**A content fault does not skip the initiator partition** — the TOC there is the most valuable thing to save from a
failing cartridge. An abort or the fragment guard does: both mean *stop now*, and the tape does not move again.

### 3.1 Block 0 of the content partition goes through a probe agent

"Rewind, select the content partition, read block 0, classify" is a ceremony `TapeStreamManager` and
`TapeAgentBase.ReadBomHeader` already own. The scanner borrows it through a throwaway
`new TapeAgentBase(Drive, new TapeTOC())` — the pattern `TapeServiceBase.RefreshLoadedHeader` uses.

```csharp
public TapeHeader? ReadBomHeader();                                    // presence only
public TapeHeader? ReadBomHeader(out int bytesRead);                   // + blank vs. something
public TapeHeader? ReadBomHeader(out int bytesRead, out byte[] block); // + the bytes themselves
```

`bytesRead ≤ 0` means *nothing is there* (blank); positive means *something is there, recognized or not*. The block's
bytes let the scanner identify what stands at BOM when it is **not** a header — the first file header of a header-less
2.1 set (§4.2). One read, the same navigator side effect, on every overload.

**Block 0 of the initiator partition needs no ceremony** — no media header is expected or skipped there — so it is read
raw, like every block after the first. From the second fragment of the content partition on, the scanner reads raw too:
those blocks sit at positions the agent has no verb for.

### 3.2 The closing mark — by convention, or measured

| Fragment | Closed by |
|---|---|
| anything, on a filemark-only layout | filemark |
| media header — any partition | **filemark** (`TapeHeaderBlock.WritesTrailingMark`) |
| TOC copy — any partition | **filemark** |
| **unidentified** — any partition | **nearest mark**, measured |
| **anything else in the initiator partition** (debris, mixed-up content) | **nearest mark**, measured |
| set header, header-less set, calibration header — content partition | the layout's separator (`UseSmks`) |

Hopping a setmark from a media header on a setmark layout loses set 1 silently; hopping a setmark from `toc1` runs to EOD
and never sees `toc2`. And a fragment the scan could not identify has **no** convention: it may be a damaged TOC copy
(closed by a filemark) or a damaged set header (closed by a setmark). Guessing either way loses whatever stands behind it.

**Nearest mark, measured** (`CrossNearestMark`): from the fragment's end, space one filemark and note the block; locate
back; space one setmark and note the block; stay at the setmark if it is nearer, else locate to the filemark. A
positional failure counts as *"no such mark ahead"*; a transport fault fails the hop.

**Why measured, not inferred.** With setmark reporting on, Win32 stops a filemark space at a setmark with
`ERROR_SETMARK_DETECTED` — but where the head then stands is not something to rely on across drives, and a drive without
the report spaces past it silently. Block numbers from the same start decide unambiguously. **Cost:** up to two locates
and a second space, paid only for fragments no convention covers — a healthy cartridge has none.

A failed hop **syncs the drive's error** before judging it — reading `NO_ERROR` as a transport fault once truncated every
complete map.

### 3.3 Marks are detected by the ordinary read

A read that meets a mark returns nothing but the mark and leaves the head **past** it. So the identification read doubles
as the mark detector: each position is read once, the head never steps back (except for §3.2's measurement and §8.3's
return).

| `ReadOutcome` | Meaning | Walk |
|---|---|---|
| `Fragment` | a block, readable or not | commit it |
| `Tapemark` | a mark, already crossed | add to the pending run, read on |
| `EndOfData` | nothing was ever written here | end this partition cleanly |

Both boundaries come from `TapeDrive.ReadDirect`'s `out` flags, surfaced by
`TapeHeaderBlock.Read(…, out bool tapemark, out bool eod)`. The drive **resets** its error on both, so the flags are the
only reliable witness; `eod` is the drive's `eof && !tapemark`.

### 3.4 Mark runs, and the TOC mark

Marks read between two data fragments form a pending run, committed by `FoldMarkRun`:

- **`TocMark`** — on a `HasTocMark` layout, when the run holds **≥ 2** marks and the fragment before it is an `Unknown`
  of the **same partition** that was **read cleanly** and closed: the `[gap][FM][FM][FM]` sequence.
- **`MarkRun`** — every other run, as a fragment of its own at the run's first block.

Two marks, not one, keeps a legacy double-filemark end of data from reading as a TOC mark. A damaged header is also
`Unknown`, but no gap — folding it would hide the damage. A run at the start of the initiator partition never folds into
the content partition's last fragment. `MarkCount` is informational.

### 3.5 Termination

| Condition | Map | Fragment / partition |
|---|---|---|
| EOD after a mark | complete, `ERROR_NO_DATA_DETECTED` | last fragment closed |
| closing-mark hop fails positionally | complete | last fragment **not closed** |
| closing-mark hop fails otherwise | **Truncated** | last fragment not closed |
| harvest cannot return the head | **Truncated** | — |
| abort | **Truncated**, `ERROR_CANCELLED` | initiator *not scanned* if not yet reached |
| `MaxFragments` (fragments or run length) | **Truncated**, `ERROR_INVALID_DATA` | initiator *not scanned* if not yet reached |
| transport fault in the content walk | **Truncated** | initiator **still walked** |
| initiator partition unreachable | **Truncated** | initiator truncated, 0 fragments |
| calibration header at content block 0 | complete, `NO_ERROR` | one fragment |

No pseudo-fragment ever closes a map. *Why a walk ended* is partition-level (`ScannedPartition`) and map-level;
*was this fragment finished* is fragment-level (`ClosedBySeparator`). **No retry, no resync-by-seeking.**

### 3.6 Fragments are reported when final

A fragment's closing state, span, `TocMark` retype and harvested TOC are all decided after it is read. So `Commit`
reports a fragment's predecessor in the **same partition**, and the last fragment of each partition is reported when that
partition's walk ends. A sink never receives a value that later changes, and never the same fragment twice.

### 3.7 Cost

One 16 KiB read per fragment and per mark, plus one mark hop per data fragment (two spaces and up to two locates for a
fragment closed by the nearest mark); with recovery, one multi-block TOC read and one locate per copy; one partition
locate per partitioned cartridge. Abortable between reads.

---

## 4. Identification

### 4.1 One classification, positive in every branch

```csharp
public enum HeaderBlockIdentity { Foreign, Header, DamagedRecord, TocCopy, CalibrationCheckpoint }
public readonly record struct IdentifiedBlock(
    HeaderBlockIdentity Kind, TapeHeader? Header = null,
    TapeFrameStatus FrameStatus = TapeFrameStatus.NotFramed,
    ushort TocVersion = 0, Guid TocMediaId = default);
public static IdentifiedBlock TapeHeaderBlock.IdentifyBlock(byte[] block, int length);
```

**Format first, by the first bytes.** `TpN#` at byte 0 is a format 2.1 record; anything else goes to the frozen legacy
probe (`LegacyIdentify.IdentifyBlock`). No legacy record can begin with the magic.

| 2.1 record kind | Outcome |
|---|---|
| `TocHeader` | `TocCopy` with version and media id when `TapeTOC.TryPeek` parses; else `DamagedRecord` |
| `FileHeader` | `Foreign` — content: the first file of a set at a block boundary (§4.2) |
| `CalibrationCheckpoint` | `CalibrationCheckpoint`, or `DamagedRecord` |
| any other kind | `Header` when the frame verifies and parses; else `DamagedRecord` with the reason |
| prologue does not parse | `DamagedRecord` (`NotFramed`) |

**A record of ours damaged behind its magic is reported as damage** — never as foreign data, never as a set, never as a
phantom TOC. At a mark boundary on a 2.1 tape the magic is always one of our records. A record damaged *in* its magic
cannot be told from foreign data (§13).

The legacy probe keeps the v3.1 order: a structurally verified TOC at offset 0 first, then our signature at offset 4 and
the legacy framer — *placement first, content second*.

### 4.2 Header-less 2.1 sets — `SetContent`

Since format 2.1, every set starts with its first file's header frame (magic, kind, CRC-64, `SetId`, name). A set written
without a set header is therefore still identifiable: `SetContentFragment` parses the frame and yields
`FragmentKind.SetContent` with `Id = SetId` and `Description` = the first file's path. Anything the CRC does not confirm
stays `Unknown`. Block 0 included, via `ReadBomHeader(out _, out block)`.

Legacy content (12-byte `TF` file headers) has no such frame and stays `Unknown`: not distinctive enough to rely on.

### 4.3 The framer tells damage from absence

```csharp
public enum TapeFrameStatus { Ok, NotFramed, CrcMismatch, Unparseable }   // TapeLibNET.Format
public static TapeFrameStatus TapeFramer.TryUnpackHeader(byte[] block, int length, out TapeHeader? header);
```

A `DamagedRecord` maps to an `Unknown` fragment with a fingerprint and a diagnosis: `ERROR_CRC`, or `ERROR_INVALID_DATA`
for a torn frame or an unparseable payload (unknown kind, newer version).

### 4.4 TOC copies are recognized structurally

```csharp
public static bool TapeTOC.TryPeek(byte[] block, int length, out ushort version, out Guid mediaId);
```

Reads both formats: a 2.1 TOC header record, or a legacy TOC in `ConstructFrom`'s order (signature and known version,
non-zero `nextUID`, `MediaId` from v0x0102, plausible set count, the first set's own signature). A TOC from a newer build
is not recognized as a TOC copy (§13).

### 4.5 Calibration cartridges — identified, not walked

A `TapeCalibrationHeader` at content block 0 makes the map complete, `Kind = CalibrationCartridge`, one fragment. With
`InspectCalibrationTrail` the scanner calls `TapeCalibrator.InspectMedia()` and attaches `CalibrationInfo` — **off by
default**; the service never asks for it (§9.1). Legacy-shape calibration headers written in the run block map as foreign;
the calibrator's own probe remains the supported path.

### 4.6 Small-block drives

When the drive's maximum block is under 16 KiB, `ReadIdentificationBlock` reads one native block instead.

### 4.7 What identification cannot do

A fragment's contents are opaque. `BlockSpan` is an upper bound including marks, never a payload size. Only the first
block after a mark is read, so packed files after a set's first are content, not fragments (SM-11).

---

## 5. The records

```csharp
public enum FragmentKind                       // an OBSERVATION, never a verdict (SM-3)
{ Unknown = 0, MediaHeader, SetHeader, CalibrationHeader, TOC, MarkRun, TocMark, SetContent }

public enum ScannedMediaKind { Blank = 0, Backup, CalibrationCartridge, Foreign }

public sealed record TapeMediaFragment
{
    public required int          Ordinal    { get; init; }   // contiguous across partitions; assigned by Commit
    public MediaPartition        Partition  { get; init; } = MediaPartition.Content;
    public required long         StartBlock { get; init; }   // relative to Partition
    public required FragmentKind Kind       { get; init; }
    public long BlockSpan          { get; init; } = -1L;
    public bool ClosedBySeparator  { get; init; }

    public Guid?     Id             { get; init; }   // MediaId / RunId / TOC MediaId / SetId (SetContent)
    public int?      Volume         { get; init; }
    public int?      VolumeSetIndex { get; init; }
    public int?      GlobalSetIndex { get; init; }
    public string?   Description    { get; init; }   // label / set name / profile key / first file (SetContent)
    public DateTime? CreatedUtc     { get; init; }
    public uint?     BlockSize      { get; init; }

    public ushort?  TocVersion   { get; init; }
    [JsonIgnore] public TapeTOC? HarvestedToc { get; init; }  // recovery ran and succeeded (§8)

    public int        MarkCount   { get; init; }
    public string?    Fingerprint { get; init; }              // first 32 bytes, hex
    public TapeResult Diagnosis   { get; init; } = TapeResult.OK;   // also: why a TOC copy did not recover

    [JsonIgnore] public string Where { get; }   // "block N" / "block N of the TOC partition"
    public bool IsHeader { get; }               // media, set, calibration header — NOT SetContent
}

public sealed record ScannedPartition(MediaPartition Partition, int FragmentCount, bool Truncated, uint TerminatorWin32)
{ [JsonIgnore] public bool Blank { get; } }    // walked to its end, nothing there

public sealed record MediaScanMap
{
    public required TapeMediaLayout  Layout     { get; init; }
    public required ScannedMediaKind Kind       { get; init; }
    public required IReadOnlyList<TapeMediaFragment> Fragments { get; init; }   // content first, then initiator
    public IReadOnlyList<ScannedPartition> Partitions { get; init; }
    public required DateTime ScannedUtc         { get; init; }
    public bool Truncated       { get; init; }   // any partition truncated or not reached
    public uint TerminatorWin32 { get; init; }   // first truncating partition's reason, else the normal end
    [JsonIgnore] public TapeCalibrationMediaInfo? CalibrationInfo { get; init; }

    // Derived:
    public IEnumerable<TapeMediaFragment> FragmentsIn(MediaPartition partition);
    // PrimaryMediaHeader, MediaId, Volume, SetCount, TocCopyCount, UnknownCount,
    // LastSetUnclosed, UnclosedLastSet, MixedIdentityFragments, OutOfPlaceFragments, SetIndexGaps

    public const string MapFileExtension = ".tapescan";
    public string ToJson();
    public static MediaScanMap? FromJson(string json);
}
```

**No "expected", "complete" or "lost"** — those are comparison verdicts.

| Derived view | Rule |
|---|---|
| `PrimaryMediaHeader` | the first media header of the content partition, else the first anywhere |
| `MediaId`, `Volume` | from the primary media header — never from a `SetContent` (whose `Id` is a SetId) |
| `SetCount` | set headers **and** header-less 2.1 sets (`SetContent`), every partition |
| `LastSetUnclosed`, `UnclosedLastSet` | per partition: the last **set header** of some partition is not closed |
| `SetIndexGaps` | per partition, forward jumps only, set headers only |
| `MixedIdentityFragments` | set headers and further media headers whose id differs from the primary media header's |
| `OutOfPlaceFragments` | headers or sets in the initiator partition; a TOC copy in the content partition of a partitioned layout |

`OutOfPlaceFragments` is an observation relative to the layout, as mixed identity is relative to the media header —
evidence that partitions were mixed up, made by the map on its own. Unidentified data is counted elsewhere.

**JSON:** `FragmentKind` is serialized as a number; new kinds are appended, so existing values never shift.

---

## 6. What the map tells the user — with no TOC at all

- How many backup sets the cartridge holds, each named and dated from its own header — or, without set headers, counted
  and identified by its first file.
- **The last set never completed** — the dominant real-world fault.
- A damaged set header or TOC copy, with its diagnosis — distinct from foreign data.
- Mixed identity; index gaps.
- **Where every TOC copy stands — including in the TOC partition of partitioned media**: version, series, and with
  recovery the TOC itself.
- **Partitions mixed up**: content in the TOC partition, a TOC in the content partition.
- Whether the TOC partition is empty, or was not scanned.
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

**Not a `TapeAgentBase`:** `TapeNavigator` is TOC-bound by construction, and the scanner needs raw mark hops and absolute
blocks. **Where an agent earns its keep, it is borrowed:** content block 0 (§3.1) and TOC recovery (§8).

**A `record`, not a `record struct`, for the options:** a struct's `new()` zeroes every field and silently skips the
defaults.

`Scan` returns `null` only when no map is possible at all; a truncated map is still returned. It never throws. A
`BlockSizeGuard` restores the drive's block size (SM-9); a partitioned scan leaves the drive in the content partition.

### 7.2 Progress: `IProgress<TapeScanProgress>`

```csharp
public readonly record struct TapeScanProgress(
    int FragmentOrdinal, long CurrentBlock, FragmentKind Kind, string Phase, TapeMediaFragment? Fragment);
// Phases: "scanning", "scanning-initiator", "harvesting-toc", "inspecting-calibration", "completing"
```

Not `ITapeFileNotifiable`: its void callbacks can stop an operation only by throwing. `Report` converts a sink's
`TapeAbortRequestedException` into `IsAbortRequested`; any other exception is logged and swallowed (SM-10).

---

## 8. TOC recovery

### 8.1 One verb, two callers

Teaching the scanner to read TOCs would duplicate the TOC stream ceremonies. Instead **the verb lives on the agent and
the scanner borrows it**, as it borrows `ReadBomHeader`. The walk's recovery option and the service's separate
"recover this copy" operation run the same code.

### 8.2 The agent verb — `RestoreTOCAt`

```csharp
// TapeAgentBase — same contract as RestoreTOC(): fills TOC via CopyFrom on success
public TapeResult RestoreTOCAt(long block, MediaPartition partition = MediaPartition.Current);
```

- **Block AND partition.** `Current` means *where this layout keeps its TOC*; a scan passes the partition it **found**
  the copy in — on a mixed-up cartridge not the layout's.
- **Block is mandatory.** "Read where the head is" is spelled `RestoreTOCAt(Drive.CurrentBlock)` — the unanchored read
  must never be the easy default on this path.
- **One copy, no fallback.** `RestoreTOC`'s retry to the next filemark assumes it began at the *first* copy; here the
  caller may name the second. To try another copy, call again.
- **Leaves no believed position:** `Manager.EndReadWrite()` and `Navigator.ResetContentSet()` whatever the outcome.

The existing `RestoreTOC → BeginReadTOC → MoveToLocationFor → MoveToBeginOfTOC` chain is **untouched**. That wrapper also
serves `BackupTOC`, so a caller-supplied block stored on the navigator could leak into a TOC *write*. The new path is
parallel and read-only by construction:

| Layer | Addition |
|---|---|
| Navigator | `MoveToTOCCopyAt(long block, MediaPartition partition = Current)` + `protected virtual MoveToTOCCopyAtCore(block, partition)` and `DefaultTocPartition` (Content; Initiator on `TapeNavigatorTOCInPartition`). A combined locate on partitioned media, a block locate otherwise. |
| Manager | `BeginReadTOCAt(long block, MediaPartition partition = Current)` — **always** ends the current session first, then `BeginReadWrite(ReadingTOC, positioner: …)`. The positioner replaces `MoveToLocationFor` for this call only. |
| Agent | `RestoreTOCAt` = `BeginReadTOCAt` → the unchanged `RestoreTOCCore` → end session → reset navigator. |

`TOCUnlocated` and the TOC-mark layout's invalidation flag stay untouched: reading a copy does not locate the TOC for
writing.

### 8.3 During the scan — `HarvestTocCopies`

When the walk commits a TOC fragment and the option is set, `HarvestLastTocCopy`:

- records `resumeAt = Drive.CurrentBlock`; `ReportPhase("harvesting-toc")`;
- `probe.RestoreTOCAt(fragment.StartBlock, fragment.Partition)` through a throwaway agent over a fresh `TapeTOC`;
- success ⇒ `HarvestedToc`; failure ⇒ the fragment stays a TOC copy, with the reason in `Diagnosis` (SM-8);
- returns to `resumeAt` **in the same partition** — the walk never depends on where the TOC reader stopped (SM-12). If
  that locate fails, the map is truncated.

Recovery is a **walk step**, not part of identification: it moves the head over many blocks. **Every copy is attempted**,
in every partition — the second routinely survives the first. **Off by default at the scanner**; the service turns it on.

### 8.4 After the scan — guarded three times

The user may recover later, against one fragment. Between scan and recovery the cartridge may have been swapped, so:

- **Guard 0:** a copy in the initiator partition is refused on a cartridge without one.
- **Before reading:** the loaded cartridge's media id must equal the scanned one (one BOM read).
- **After reading:** the recovered TOC's media id must equal the one the scan peeked at that block.

The last check verifies the CRC-validated result rather than peeking first — stronger, and a read is harmless: nothing is
adopted until every guard passes. Any failure is `ERROR_MEDIA_CHANGED`.

### 8.5 Adoption goes through the import path

A recovered TOC is adopted exactly like an imported one: same identity verdict and one-off prompt
(`MediaPromptContext.ImportToc`, no `ProceedAlways`), the mounted volume's number adopted but never its `MediaId`,
`TocChanged` fired. Never implicit (SM-13). The service records it as **`TOCSource.Recovered`**; the UI flags it as
"(using recovered TOC)".

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

| Case | Success | Outcome | Diagnosis |
|---|---|---|---|
| clean scan | true | Completed / Warning / Info, from the headline | OK |
| stopped at `MaxFragments` | true | Warning ("Scan incomplete") | OK |
| user abort | false | Failed | `ERROR_CANCELLED` |
| transport fault | false | Error | the scanner's `LastResult` |

**`VerbalizeScan(map)` → (Level, Headline, Details)**, pure, beside `AdviseOnScan` and `ScanAdviceText`:

- **No sets identified, but a recovered TOC lists some** → *"N backup set(s) per the recovered table of contents — they
  carry no headers to identify them by"* (legacy media). A TOC copy of ours makes the cartridge a backup cartridge.
- **The recovered TOC lists more sets than the scan identified** → a detail line saying how many carry no headers.
- **Header-less sets found** → a detail line: their order and completeness cannot be checked from the tape alone.
- **The TOC partition** → *"empty — no table of contents was ever written there"*, or *"was not scanned"*; never the same.
- **Out-of-place fragments** → a detail line, and the cartridge *needs attention* (as do an unclosed set, index gaps,
  mixed identity, damage).
- `AdoptRecoveredToc` and `RecoverTocFromCopy` exclude each other.

Further: calibration inspection is never requested — the service advises `InspectCalibrationCartridge`; TOC recovery is on
by default here; no identity prompt, `_toc` never touched, `_loadedHeader` refreshed; map export is best-effort.

**`ServiceScanProgressHandler : IProgress<TapeScanProgress>`** counts fragments, **sets (set headers and `SetContent`)**
and recovered TOCs — so the live counter agrees with `MediaScanMap.SetCount` — humanises the phase ("Scanning the TOC
partition"), and logs one sub-line per noteworthy fragment (`static DescribeFragment`), naming the TOC partition where a
fragment stands there. A header-less set is Info, not Warning: the volume chose not to write set headers.

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
- Otherwise the guards of §8.4 around a probe agent's `RestoreTOCAt(copy.StartBlock, copy.Partition)`.
- Save and adoption failures downgrade the outcome to Warning; the recovery itself stands.

---

## 10. Implementation

### 10.0 File layout

```
TapeLibNET/
  Scan/
    TapeScanner.cs               // ScanCore, WalkPartition, closing marks (nearest), mark runs, progress, guard
    TapeScanner.Identify.cs      // content block 0 via probe agent; ReadFragmentAt; fragment factories (SetContent)
    TapeScanner.Harvest.cs       // HarvestLastTocCopy — by partition and block (§8.3)
    TapeMediaFragment.cs  MediaScanMap.cs  ScanMediaOptions.cs
  Media/
    TapeMediaLayout.cs           // Predict — beside TapeNavigator
    TapeNavigator.cs             // + MoveToTOCCopyAt(block, partition), DefaultTocPartition
    TapeStreamManager.cs         // + BeginReadTOCAt(block, partition); BeginReadWrite(positioner)
  Headers/
    TapeHeaderBlock.Identify.cs  // IdentifyBlock (2.1 dispatch + legacy probe), TryIdentifyHeaderBlock
    TapeFramer.cs                // TryUnpackHeader / UnpackHeader
  Format/
    TapeFrameStatus.cs
  Toc/
    TapeTOC.cs                   // + TryPeek
  Agents/
    TapeAgentBase.cs             // + RestoreTOCAt(block, partition)
    TapeAgentBase.Headers.cs     // + ReadBomHeader(out bytesRead), ReadBomHeader(out bytesRead, out block)
  Services/
    TapeServiceBase.Scan.cs      // ScanMediaAsync, RecoverTocAsync, VerbalizeScan, AdviseOnScan
    ServiceOperationRequest.cs / ServiceOperationResult.cs / ServiceOperationProgressHandler.cs
  Virtual/
    VirtualTapeDriveBackend.Diagnostics.cs   // Content/Initiator Read/Write fault injectors
```

### 10.1 Phases

| Phase | Delivered | Tests |
|---|---|---|
| 0 — records and extractions | `TapeMediaLayout.Predict`; `TryIdentifyHeaderBlock`; the records | `TapeMediaLayoutTests`, `TapeMediaIdentifyTests` |
| 1 — `TapeScanner` | the walk and identification: closing mark by kind; tapemark/eod flags and synced hop errors; positive identification; mark detection by the ordinary read with `TocMark` folding | `TapeScannerTests` (all four profiles); helper `ScanMapAssert` |
| 2 — calibration cartridges | identified, not walked; optional inspection | `TapeScannerCalibrationTests` |
| 3 — TOC recovery | `MoveToTOCCopyAt`, `BeginReadTOCAt`, `RestoreTOCAt`, harvest | `TapeScannerHarvestTests` |
| 4 — service | `ScanMediaAsync`, `RecoverTocAsync`, pure statics, progress handler, map export, `TOCSource.Recovered` | `ServiceScanMediaTests` |
| 5 — TapeWinNET | setup dialog, overlay, result window (§14) | manual acceptance |
| 6 — TapeConNET | `scan-media`, `recover-toc` | CLI tests |
| **7 — partitions, nearest mark, format 2.1** | every partition walked (SM-14); `ScannedPartition`, `OutOfPlaceFragments`; nearest-mark rule; `SetContent`; TOC recovery by partition; initiator fault injectors; partition column in WPF | `TapeScannerPartitionTests`; exemptions removed from the earlier suites; `ServiceLegacyMediaTests` (legacy and mixed cartridges) |

### 10.2 Phase 7 — why the earlier suites missed it

They encoded the limitation as expected behaviour: TOC assertions were skipped with `if (!map.Layout.TocInPartition)`,
and every harvest test ran on in-set profiles only. A partitioned cartridge scanned as *no TOC* — a legacy one as
*Foreign* — without a red test. No test asserted that one cartridge content yields one set of findings across layouts.

**The parity theory** `SameContent_SameFindings_OnEveryLayout` does exactly that, on all four profiles. With it:

| Region | Tests |
|---|---|
| parity | same findings on every layout; harvest leaves every layout's map unchanged |
| partitioned | copies in the TOC partition from block 0 · return to content · empty TOC partition is *blank* · blank content + TOC is *Backup* · debris in the TOC partition · media header in the TOC partition (out of place) · TOC copy in the content partition, recovered from there · damaged first copy, second recovered |
| nearest mark | a damaged TOC copy closed by a filemark does not hide the next copy (setmark layouts) · debris closed by a setmark is closed by it |
| stopping | abort in content ⇒ TOC partition *not scanned* · never writes, both partitions |
| map | JSON round trip of partitions · `DescribeFragment` names the TOC partition |

`ScanMapAssert` checks block order **per partition**, snapshots **both** partitions for the never-writes tests, and
offers `PartitionBlank` / `PartitionNotScanned`, `TocCopiesWhereTheLayoutPutsThem`, `NothingOutOfPlace`,
`NothingDamaged`, `AllTocCopiesRecovered`.

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
| **SM-12** | Each position of each partition is read once by the identification read. The head is repositioned only for the harvest's return (same partition, same block), the nearest-mark measurement, and the final return to the content partition. |
| **SM-13** | A recovered TOC is never adopted implicitly, and a TOC read after the scan is checked against the scanned cartridge before and after the read. |
| **SM-14** | Every partition the medium has is walked and identified the same way. Nothing is presumed about what a partition holds; each fragment records its partition, and an empty partition is never confused with an unscanned one. |

---

## 12. What Repair Media inherits

- **v4's `ScanContentSets` is deleted**, replaced by `TapeScanner.Scan` plus a pure
  `Compare(MediaScanMap, TapeTOC) → IReadOnlyList<TapeSetScanEntry>`. Every v4 state survives in meaning.
- **The comparison is pure**, so v4's state model becomes testable with no tape at all.
- **v4's end-of-content bound is dropped**, and with it RM-3.
- **The TOC source list grows a third entry** — *recovered from this cartridge* (`TOCSource.Recovered`).
- **Header-less sets compare by `SetId`** — the TOC records the same id the set's first file carries.
- **v4's `OnSetScanned` is dropped**; `ITapeFileNotifiable` is untouched.
- **Still required:** `SetWriteWitness`, `SealPartialSet`, `IsUndescribed` + TOC v0x0103, `BuildPlan`.
- **The apply phase stays in `TapeSetAgent`.**

**The user scans, chooses a TOC, sees the verdict table — and only then is anything destructive offered.**

---

## 13. Known limits

- **Legacy-shape calibration cartridges** map as foreign (§4.5).
- **Legacy sets without headers** are not counted; a recovered TOC lists them (§4.2, §9.1).
- **A header-less 2.1 set left open by EOD** is not flagged — `LastSetUnclosed` looks at set headers only (§15).
- **A TOC newer than this build** is not recognized as a TOC copy (§4.4); a record from a newer build reads as damaged.
- **A record damaged in its magic or signature** cannot be told from foreign data (§4.1).
- **Saving a recovered TOC needs media loaded** — `SaveTOCToFile` is an agent instance method.
- **Real hardware:** confirmed only on the virtual backend — a read that meets a mark leaves the head past it (setmarks
  need `ReportSetmarks`), EOD is reported through `eof` without `tapemark`, and the nearest-mark measurement's cost on a
  long damaged partition (§15). Worth confirming on the DLT and LTO families.

---

## 14. WPF and CLI

**TapeWinNET** (Phase 5): **Media | Scan Media…** → a one-checkbox setup dialog ("Try to recover the table of contents",
optional map folder) → the shared operation overlay → a result window:

- header banner from `Summary`, coloured by `Outcome`; detail lines from `VerbalizeScan`;
- one row per fragment in tape order — `# · Partition · Block · Kind · Identity · Detail`. The Partition column is hidden
  (width 0, set in code-behind) on single-partition media;
- *Backup set (no header)* rows show the short SetId and the first file, and — matched by SetId against a recovered TOC —
  the set's name; not amber;
- out-of-place rows are amber with an "out of place —" prefix;
- on TOC rows: **[Use this TOC]**, **[Save as…]**, or **[Try to recover]**;
- one button per `ScanAdvice`; **[Save map…]**, **[Close]**.

A scan changes nothing on tape and never touches the TOC, so the tree is never reloaded after it. The seam to Repair
Media is a later **[Compare with a table of contents…]** button on the same window.

**TapeConNET** (Phase 6): `tapecon scan-media [--no-recover-toc] [--export <dir>] [--json]` prints the headline, details
and one line per fragment (`DescribeFragment`); `tapecon recover-toc --map <file> --fragment <n> [--adopt] [--save <file>]`
loads a `.tapescan`; the exit code distinguishes success, refused (a guard), and failure.

---

## 15. Open items

| Item | Note |
|---|---|
| Nearest-mark cost on hardware | Two spaces from one start: on a damaged, very long partition the setmark space may run far before failing. If costly, bound it by the filemark's block (locate there, space one setmark backwards). |
| Header-less set left open by EOD | `ClosedBySeparator` is already recorded on `SetContent`; extending `LastSetUnclosed` needs a rule for "closed" without a set header. |
| Mid-block file headers | Packed files after a set's first are not located (SM-11); whole-block reads at mark boundaries only. |
| Fault injection on hardware-like partitioned media | The initiator injectors exist; a damaged-first-copy harvest test in the TOC partition can arm `InitiatorReadFaults` like its content twin. |
