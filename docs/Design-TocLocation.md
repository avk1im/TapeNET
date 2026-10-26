# Design — Locating the TOC (rugged TOC location, TOC anomalies, one block classifier)

**Version:** 4 (replaces v3 entirely). **Status:** final pre-implementation proposal; nothing is implemented yet.

**Changes since v3** (the user's review, analysed in §0):

| Topic | v3 | v4 |
|---|---|---|
| Content end | `ContentEndBlock` in the TOC header, captured at the start of the TOC session | **`FirstBlockAfterSet` per set** (authoritative), captured by the stream manager after the set's closing mark; the TOC header mirrors the volume's value as `FirstBlockAfterContent` for the peek |
| Volume safety | implicit | every lookup is **per volume**, and callers pass the *identified* volume |
| EOM-closed sets | — | `-1`: no closing mark, so no reliable "after" position (the existing `ClosedByEom` already says "volume full") |
| S3 | arithmetic from file addresses | direct `MoveToBlock` to the series TOC's `FirstBlockAfterSet`; arithmetic only for legacy sets |
| Anchors | a per-layout table | anchors defined as **positions proven by identification**; per-layout fallback chain; optional session landmarks (§4.3) |
| TOC anomalies | one record with `RecoveredByFallback` kind | **mirrors set anomalies**: detection and recovery as two events, the stage as a field |
| Notifiables | `ITapeTocNotifiable` | linear hierarchy `ITapeTocNotifiable ← ITapeSetNotifiable ← ITapeFileNotifiable` |
| Advice | by symptom | **by cause and layout**: calibrate (TOC-in-set) / reduce files per set (TOC partition); online text short, details in Help; optional pre-flight check |
| Plan | phases | **files needed per phase** |

---

## 0. Review of the proposals

### 0.1 `FirstBlockAfterSet` — what it points to, who captures it (your A, D)

**Meaning:** the **first block after the set's closing mark**: the block where the next set, or the TOC, starts. It's
captured as `Drive.CurrentBlock` right after the mark is written, so it automatically counts marks the way *this drive*
counts them. That's the same convention every `TapeAddress` already relies on.

**Who captures it: the stream manager. Who stamps it: the agent.** There are two reasons not to have the agent read
`Drive.CurrentBlock` itself after `EndWriteContent` returns:
1. `EndWriteContent` closes the set **and then rethrows** EOM or a write fault (by design). The agent's code after the
   call doesn't run on those paths, but they're exactly the paths where the value matters most.
2. Only `EndWriteContentSet` knows whether the mark was **actually written**, or the set was declared closed by EOM
   (F32), or the mark failed.

So:
- **`TapeStreamManager`** records `LastSetFirstBlockAfter` inside `EndWriteContentSet`: `Drive.CurrentBlock` if the mark
  was written, `-1` otherwise. Like `LastSetClosedByEom`, it's cleared when a new write session begins.
- **`TapeFileBackupAgent`** stamps it into `TOC.CurrentSetTOC.FirstBlockAfterSet` at the same two places where it
  already copies `LastSetClosedByEom`: after the final `EndWriteContent`, and in `HandleEom`.

This works for every layout, including TOC-in-partition, because it's taken in the content partition when the set
closes, not when the TOC is written.

**The name:** adopted, as `FirstBlockAfterSet` per set and `FirstBlockAfterContent` per volume. Inside `TapeSetTOC`,
"This" would be redundant.

### 0.2 EOM-closed sets → `-1` (your B)

**Agreed.** A set closed by EOM has no closing mark, and after a failed write at EOM a real drive's reported position
isn't trustworthy enough to append to. `-1` means *no reliable position*. `ClosedByEom` (F32) already tells the append
logic that the volume is full, so nothing is lost. A set whose closing mark failed for another reason is also `-1`.

### 0.3 Per set, not a dictionary — and volume-safe (your C)

**Your second idea is the better one: per set.**

- **It maps onto the existing model.** The TOC already lists every set part with its `Volume`. The last part on volume
  *n* carries volume *n*'s content end, so no second structure needs keeping in sync.
- **Continuation parts and mixed series are covered.** Each part carries its own value, and legacy volumes simply have
  none (`-1`).
- **Deletion and overwrite need nothing special.** Deleting sets writes the TOC right behind the last surviving set,
  whose value is unchanged; an overwrite stamps a fresh value; trailing sets are removed.
- **An empty volume has no last set.** There, content end = begin-of-content (after media header + FM). That's positive
  blank evidence.

**Volume safety (your C, first part): mandatory.** The lookup takes the volume explicitly, and callers pass the volume
they **identified** (media header, or a TOC peek), never `TOC.Volume` by assumption:

```csharp
    /// <summary>
    /// The first block after the content of <paramref name="volume"/>: <see cref="TapeSetTOC.FirstBlockAfterSet"/> of the
    ///  last set part on that volume; begin-of-content when the volume holds no sets; -1 when unknown (legacy, EOM-closed).
    /// </summary>
    public long FirstBlockAfterContentOnVolume(int volume, long beginOfContent);
```

**Encrypted sets:** the value must sit in the **clear stub** (`TocSealedSetWire`). A freshly loaded TOC has its sealed
sets locked, and the resume and the append both need the value without a password. A block number reveals nothing about
the content: it's no more than the stub's `SizeOnTapeTotal` already does.

**The TOC header keeps a mirror,** `FirstBlockAfterContent` (the value for its own volume). The per-set records may lie
beyond the TOC's first block, but the **peek** only ever sees that first block (Design-FormatV21 §6, F17). The plausibility
check and the copy ranking need the value without a full parse. The mirror is derived when the TOC is saved, never edited
separately.

### 0.4 Advice on a too-small TOC reserve (§0.10 of v3)

You're right: "consider a larger TOC reserve" isn't actionable, since the user can't set it. The cause differs by layout,
and so does the action:

| Layout | Real cause | What the user can do |
|---|---|---|
| **TOC in set** | the drive's remaining-capacity estimate is imprecise, so the TOC reserve armed too late | **calibrate** the drive for this media (the calibration feature exists; it makes the early warning precise); for this cartridge, **export the TOC to a file** |
| **TOC in partition** | the TOC outgrew its partition: very many small files | **fewer files per set** (split the backup into several sets or volumes); for this cartridge, **export the TOC to a file** |

**Online vs. Help:**
- **Online (service log):** one headline, one concrete action, and a Help pointer, in the established `AdviseText`
  shape. Example: *"Only one TOC copy fit on volume #2 — Export the TOC to a file now; calibrate the drive for this media
  to prevent it (Help: TOC space)."*
- **Help topic "TOC space":**
  - how much the TOC grows (about 120 B plus the path length per file, more for encrypted sets);
  - why the remaining-capacity estimate drifts on older drives, and what calibration fixes;
  - partition size vs. file count, with a worked example;
  - the emergency TOC file, and how to import it.

**Better than advice afterwards: a pre-flight check (optional).** The service can estimate the TOC size after the backup
before starting, from `EstimateSerializedSize` per file. If that estimate exceeds the TOC partition (partition layout) or
the armed reserve (in-set layout), it warns before anything is written. That's the one moment the user can still act
without loss. It's a cheap addition; it's listed in Phase 6.

### 0.5 Anchors: what they are and where they come from (your 4.1)

**Definition:** an anchor is a position the walk may *start* from, found by a cheap move. Like everything in the ladder,
it's a **hint** (TL-2). The walk identifies every block it reads, so a wrong anchor costs time, never correctness. Two
consequences:

- **Anchors aren't maintained; they're re-derived each time.** Each layout has a chain of anchors, cheapest first (§4.2).
  If a move fails (no setmark found, no TOC mark found), the next anchor in the chain is tried. The chain always ends at
  BOM, which is S4.
- **The series TOC is the best persistent anchor.** When we hold a TOC that describes this volume (resume, append, or
  the TOC we just failed to locate natively but have from an earlier read), the previous set's `FirstBlockAfterSet` is a
  start position immune to every mark problem. It ranks above the mark-based anchors.

**Your "last known reliable anchor" cache:** valuable, in a reduced form. A full block → record map is overkill and goes
stale on every write. What helps is a small **session landmark log** on the navigator: (partition, block, identified
kind) for each successful identification during this load. Its rules:
- **Hints only**, so staleness can't cause harm.
- **Cleared** by `RenewNavigator` (media change).
- **Truncated** by any write at block *b*: landmarks ≥ *b* are dropped.
- **For the walk:** the highest content landmark below EOD becomes an extra anchor.

That shortens the walk when S0/S1 fail later in the same session. It's an optimization, not a correctness piece, so it's
planned for Phase 6 (§4.3).

### 0.6 Mirror set anomalies exactly (your 5.1)

**Agreed.** Set anomalies use one record type, a `Stage` field and **two events**: `OnSetAnomaly` (detected, stage
`Detected`) and `OnSetAnomalyRecovered` (stage `Delta` or `Renavigated`). TOC anomalies now do the same:
- `RecoveredByFallback` is no longer a kind. Recovery is the second event, and *which* fallback succeeded is its `Stage`.
- "Found" and "recovered" are separated exactly as for sets: the detection carries `Stage = Detected`, the recovery
  carries the stage that succeeded.
- Statistics mirror `AnomaliesRecovered` / `AnomaliesRecoveredFromBom` with `AnomaliesRecovered` / `AnomaliesRecoveredByWalk`.

### 0.7 `ITapeSetNotifiable` too (your 5.2)

**Agreed, as a linear hierarchy.** Every set operation reads or writes the TOC, and every file operation is a set
operation:

```
ITapeTocNotifiable      OnTocAnomaly, OnTocAnomalyRecovered, OnTocFullWalkConfirm
   ▲
ITapeSetNotifiable      OnSetAnomaly, OnSetAnomalyRecovered          (moved, unchanged)
   ▲
ITapeFileNotifiable     SetStart … OnFileLost                        (unchanged)
```

`TapeSetAgent` (delete) takes `ITapeSetNotifiable`; the TOC-only verbs take `ITapeTocNotifiable`. Every member keeps its
default implementation, and the moved members keep their signatures, so all existing implementers compile unchanged.

All callbacks keep their `in TapeFileStatistics stats` parameter: it already nests `Sets`, and now `Toc`. Renaming it to
something like `TapeOperationStatistics` would be more accurate but touches every implementer; I'd defer that.

### 0.8 Retained from v3 without change

- The peeker contract (`ITapeBlockPeeker<TPeek>`, `TapeFrameStatus` result), with no renames.
- No `ITapeClassify`; typed accessors on `IdentifiedBlock`.
- S2 and S4 as `TapeScanner` walks.
- Copy markers (`CopyIndex`, `CopyCount`).
- A partial TOC write is a success with an anomaly.
- A new anomaly type rather than a reused one.
- Ask only before the full walk.
- The drive's own error where it reported one.
- Anomalies and `TapeResult` side by side.

---

## 1. Evidence

### 1.1 Three traces

Content capacity 262 144 B, TOC reserve (`TOCCapacityOverride`) 32 768 B, block 16 384 B, media header 16 384 B. Marks
take no space on the virtual media and are refused once EOM is set.

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
| TL-2 | **Mark arithmetic and anchors are hints, never proofs.** Identification decides. |
| TL-3 | **Never write at a guessed position.** End-of-content comes from `FirstBlockAfterContent` (series TOC or a found copy) or from positive "blank" evidence; otherwise the write is refused. |
| TL-4 | **Bounded, escalating cost.** S0–S3 run unasked; S4 asks first. |
| TL-5 | **Choose the right copy:** expected `MediaId`, expected `Volume`, plausible `FirstBlockAfterContent`, newest `LastSaveTime`, then copy 1, all from the peek. |
| TL-6 | **Every deviation is a TOC anomaly; every recovery a recovery event.** A clean path records none. |
| TL-7 | **A write that wrote nothing is a failure**, reported with the drive's own error when it has one. |
| TL-8 | **The TOC reserve serves the TOC:** armed for content only, measured from the true write position, sized for the layout's footprint. |
| TL-9 | **One classifier, one walker.** |
| TL-10 | **Per-volume facts are looked up by the identified volume**, never by `TOC.Volume` assumed. |

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

`IdentifyBlock` keeps its switch on the record kind. The repeated "status → kind or DamagedRecord" logic moves into one
generic helper:

```csharp
    private static IdentifiedBlock Peek<TPeeker, TPeek>(ReadOnlySpan<byte> data, Func<TPeek, IdentifiedBlock> found)
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
| `TocHeader` | `Peek<TapeTOC, TocPeek>(…, p => new(TocCopy, Toc: p))` |
| `FileHeader` | `Peek<TapeFileHeader, FileHeaderPeek>`, except that a damaged frame stays **Foreign**: file body may start with the magic, and only the CRC confirms a header |
| `CalibrationCheckpoint` | `Peek<TapeCalibrationCheckpoint, …>` |
| any other | `Peek<TapeHeader, TapeHeader>` |
| not `TpN#` | `LegacyBlockPeeker` |

### 3.3 Types

```csharp
public enum HeaderBlockIdentity { Foreign = 0, Header, DamagedRecord, TocCopy, CalibrationCheckpoint,
                                  FileHeader /* ✚ appended */ }

public readonly record struct TocPeek(ushort Version, Guid MediaId, int Volume, DateTime LastSaveTime, int SetCount,
    byte CopyIndex, byte CopyCount, long FirstBlockAfterContent);   // 0 / -1 = not carried (legacy, older 2.1)

public readonly record struct FileHeaderPeek(Guid SetId, ulong FileId, string Name);

public readonly record struct IdentifiedBlock(HeaderBlockIdentity Kind, TapeHeader? Header = null,
    TapeFrameStatus FrameStatus = TapeFrameStatus.NotFramed, TocPeek? Toc = null, FileHeaderPeek? File = null)
{
    public bool TryGetHeader<T>([NotNullWhen(true)] out T? header) where T : TapeHeader;
    public bool TryGetToc(out TocPeek toc);
    public bool TryGetFile(out FileHeaderPeek file);
}
```

**Projections:**
- `Classify` stays: `length > 0 ? IdentifyBlock(buffer, length).Header : null`.
- `TryIdentifyHeaderBlock` (both overloads) and `IdentifiedBlock.TocVersion` / `TocMediaId` become `[Obsolete]` for one
  release, then are removed.
- `TapeFramer.UnpackHeader` becomes `internal`.

### 3.4 One I/O primitive

The scanner's private `ReadIdentificationBlock` becomes `TapeHeaderBlock.ReadAndIdentify(drive, out tapemark, out eod,
out block, out read)`: a 16 KiB read, the small-block fallback, and the mark / EOD flags.

**Used by:** the scanner, BOM header reads, set-header verification, and resume identification.
`TapeHeaderBlock.Read(..., out TapeHeader?)` becomes a projection of it.

### 3.5 Contract test

A theory over every peeker type, fed with random bytes, every truncation of a valid record, and single-bit flips in every
byte. It asserts that:
- no exception is thrown;
- the result is `Ok` only for the untouched record;
- the dispatcher agrees with the peeker it delegated to.

---

## 4. The TOC location ladder

### 4.1 Stages

| Stage | Name | Candidates | Asks? |
|---|---|---|---|
| **S0** | Native | the layout's own TOC position, made honest (R2): fail instead of "blank" | no |
| **S1** | Sibling | the other copy, one file away (`CopyIndex` gives the direction) | no |
| **S2** | Anchor walk | a scanner walk from the best anchor (§4.2) to EOD, TOC copies only | no |
| **S3** | Expected position | `MoveToBlock(FirstBlockAfterContentOnVolume(v))` from the series / in-memory TOC, ± W = 4 blocks; arithmetic from file addresses only for legacy sets without the value | no |
| **S4** | Full walk | a scanner walk from BOM to EOD, TOC copies only | **yes** — `OnTocFullWalkConfirm` |

**Order note:** S3 runs **before** S2 whenever a describing TOC is at hand. It's one locate, and it's exact.

Every candidate goes through the same steps:
1. `ReadAndIdentify`: is it a `TocCopy`?
2. Plausibility: the peek's `FirstBlockAfterContent ≤ candidate block`, and it equals the series TOC's value where we
   have one.
3. Rank by TL-5, using the peek.
4. `RestoreTOCAt`: the full CRC parse.

A copy that **peeks but doesn't parse** (torn) still contributes its `FirstBlockAfterContent` (§8.1).

### 4.2 Anchor chains (S2)

Spacing semantics as you confirmed: `+n` stops just after the mark, `−n` just before it. The chain is tried in order;
the first anchor that is reached becomes the walk's start.

| Layout | Anchor chain (cheapest first) |
|---|---|
| any, with a describing TOC | 1. the previous set's `FirstBlockAfterSet` (start of the last set; the walk crosses it); 2. session landmark (§4.3, Phase 6) |
| Setmarks | from EOD `MoveToNextSetmark(-1)`, then `+1` → just after the last set's closing setmark |
| TOCMark | `SeekForwardPastTOCMark` from BOM: unique by construction |
| FilemarksOnly | from EOD, up to K = 3 × `MoveToNextFilemark(-1)` (stop at BOM), then `+1` |
| Partitions | BOP of the TOC partition |
| all | BOM — which is S4, and asks |

The walk crosses filemarks only, so the setmark-vs-filemark spacing rule plays no part. The tail file between the last
mark and EOD (T1's copy 1) is walked like any other.

### 4.3 Session landmarks (Phase 6, optional)

`TapeNavigator.Landmarks` is a small ring of `(MediaPartition, long Block, HeaderBlockIdentity Kind)`:
- **Recorded** on every successful identification: media header, set header, file header, TOC copy.
- **Cleared** by `RenewNavigator`.
- **Truncated** by any write at block *b*: entries ≥ *b* are dropped.

The highest content landmark below EOD becomes an anchor in the chain above. Landmarks are hints only (TL-2), so a stale
entry costs one wasted move.

### 4.4 `TapeScanner` options (S2, S4)

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

The public Scan Media keeps its defaults. The ladder calls the internal `TapeScanner.Walk(options)` with
`Filter = TocCopies, HarvestTocCopies = false`: it peeks, ranks, and reads only the winner.

---

## 5. Anomalies and notification

### 5.1 TOC anomalies, mirroring set anomalies

```csharp
/// <summary>What is wrong with a TOC copy, or with the search for one.</summary>
public enum TapeTocAnomalyKind
{
    CopyWriteFailed,        // a copy could not be written; the other one was
    NoCopyWritten,          // both copies failed: this volume carries no TOC
    NotFoundNatively,       // the layout's own position held no readable TOC (S0, and S1 if tried)
    CopyUnreadable,         // a copy was found but did not read (torn, CRC)
    ForeignCopySkipped,     // a copy of another series / volume, or implausibly placed
    NoReadableCopy,         // the ladder ended without a readable copy
}

/// <summary>Which stage produced — or settled — a TOC anomaly. Mirrors <see cref="TapeSetAnomalyStage"/>.</summary>
public enum TapeTocAnomalyStage { Detected, Sibling, ExpectedPosition, AnchorWalk, FullWalk }

/// <summary>The payload of both TOC callbacks. By value; carried in a list per operation.</summary>
public readonly record struct TapeTocAnomaly(
    TapeTocAnomalyKind Kind,
    TapeTocAnomalyStage Stage,      // Detected on the detection event; the succeeding stage on the recovery event
    int Volume,                     // the identified volume it concerns
    byte CopyIndex,                 // 1, 2; 0 = unknown / not one copy
    MediaPartition Partition,
    long Block,                     // where the copy stands; -1 = unknown
    bool CanAttemptRecovery,        // a further stage remains
    TapeResult Diagnosis);          // the drive's or reader's own error

public struct TapeTocStatistics
{
    public int CopiesWritten, CopyWriteFailures, VolumesWithoutToc;
    public int AnomaliesDetected, AnomaliesRecovered, AnomaliesRecoveredByWalk;   // ByWalk ⊂ Recovered (AnchorWalk/FullWalk)
    public readonly bool HasAnomalies => AnomaliesDetected > 0;
    public void Reset() => this = default;
    public readonly TapeTocStatistics Delta(in TapeTocStatistics baseline);
}
```

| Event | Callback | Typical kinds | Stage |
|---|---|---|---|
| detection | `OnTocAnomaly` | `NotFoundNatively`, `CopyUnreadable`, `CopyWriteFailed`, `NoCopyWritten`, `ForeignCopySkipped`, `NoReadableCopy` | `Detected` |
| recovery | `OnTocAnomalyRecovered` | the detection's kind | the stage that succeeded |

**Raised once per volume per operation,** like set anomalies once per set. Later findings on the same volume are
accumulated in the list without a second callback.

The **statistics** nest in `TapeFileStatistics.Toc`. The **records** live on the agent (`TapeAgentBase.TocAnomalies`),
for the same aliasing reason as set anomalies.

### 5.2 Notifiable hierarchy

```csharp
public interface ITapeTocNotifiable
{
    void OnTocAnomaly(in TapeTocAnomaly anomaly, in TapeFileStatistics stats) { }
    void OnTocAnomalyRecovered(in TapeTocAnomaly anomaly, in TapeFileStatistics stats) { }
    /// <summary>Asked before the full-media walk (S4). Default: proceed — read-only; an unattended run must not stall.</summary>
    bool OnTocFullWalkConfirm(int volume) => true;
}

public interface ITapeSetNotifiable : ITapeTocNotifiable
{
    SetAnomalyAction OnSetAnomaly(in TapeSetAnomaly anomaly, in TapeFileStatistics stats) => SetAnomalyAction.Abort;
    void OnSetAnomalyRecovered(in TapeSetAnomaly anomaly, in TapeFileStatistics stats) { }
}

public interface ITapeFileNotifiable : ITapeSetNotifiable { /* SetStart … OnFileLost, unchanged */ }
```

**Signatures:**

| Verb | Notifiable parameter |
|---|---|
| `BackupTOC(bool enforce = false, …)`, `RestoreTOC(TocLocateOptions? options = null, …)` | `ITapeTocNotifiable? notify = null` |
| `TapeSetAgent`'s delete verbs | `ITapeSetNotifiable` |
| backup, restore | unchanged (`ITapeFileNotifiable`) |

**Service:** `ServiceOperationProgressHandler` overrides:
- the two TOC events, logging them like the set events;
- `OnTocFullWalkConfirm`, routed to `PresentVerdict(Unidentified, SearchForTOC)`.

The TOC-only service verbs (identify, rename, import) get a small `ServiceTocProgressHandler` with the same overrides.

### 5.3 Lifetime across volumes

- **Cleared** at the start of an operation, where `_setAnomalies.Clear()` already runs. The standalone public
  `RestoreTOC` / `BackupTOC` are operations of their own and clear too.
- **Not cleared** by the internal `LocateAndRestoreTocCore` (multi-volume resume) or the `BackupTOCCore` between volumes.

Every entry names its volume, so the final report covers the whole operation.

### 5.4 Service advice: by cause and layout

`AdviseOnTocAnomalies(in TapeTocStatistics, IReadOnlyList<TapeTocAnomaly>, TapeTocPlacement)`:

| Advice | When | Headline / action (+ "Help: TOC space" where noted) |
|---|---|---|
| `TocLost` | `NoCopyWritten` | *"Volume #n carries no table of contents"* / *"Export the TOC to a file now; the files remain restorable through it"* + Help |
| `SingleCopyReserve` | `CopyWriteFailed` with EOM / EW, TOC in set | *"Only one TOC copy fit on volume #n"* / *"Export the TOC to a file now; calibrate the drive for this media to prevent it"* + Help |
| `TocPartitionFull` | `CopyWriteFailed` with EOM, TOC in partition | *"The TOC no longer fits its partition"* / *"Export the TOC to a file now; back up fewer files per set or volume"* + Help |
| `SingleCopyDamaged` | `CopyWriteFailed` with another error, or `CopyUnreadable` | *"A TOC copy on volume #n is damaged"* / *"Re-save the TOC (rename the media) to restore both copies; retire the cartridge if this recurs"* |
| `TocFoundBySearch` | any recovery event | *"The TOC of volume #n was found only by searching"* / *"Re-save the TOC so it can be found directly again"* |
| `None` | nothing recorded | silent |

Reported by `ReportTocAnomalyOutcome`, right after `ReportSetAnomalyOutcome`, in backup, restore, identify and delete.
The Help topic "TOC space" is listed in §0.4.

---

## 6. Format additions (2.1, optional, non-critical)

| Record | Field | # | Type | Meaning | Absent |
|---|---|---|---|---|---|
| `TocSet` | `FirstBlockAfterSet` | 15 | long (varint, signed) | first block after the set's closing mark; −1 = none (EOM-closed, mark failed) | −1 |
| `TocSealedSet` (clear stub) | `FirstBlockAfterSet` | next free | same | same, readable while the set is locked | −1 |
| `TocHeader` | `CopyIndex` | 7 | byte | 1 or 2 | 0 |
| `TocHeader` | `CopyCount` | 8 | byte | copies attempted in this session | 0 |
| `TocHeader` | `FirstBlockAfterContent` | 9 | long | mirror of `FirstBlockAfterContentOnVolume(Volume)`, derived on save, for the peek | −1 |

- `TapeSetTOC.FirstBlockAfterSet` is part of the persisted state (`PersistedState`), so a change re-stamps
  `LastSaveTime` and re-seals an encrypted set.
- It isn't copied into a continuation part; each part gets its own value.
- The two TOC copies differ only in `CopyIndex`; each keeps its own CRC.
- The goldens are regenerated once.

---

## 7. Volume identification for a multi-volume resume (F6)

| Step | Evidence | Verdict |
|---|---|---|
| 1 | **Media header** at BOM (`ReadAndIdentify`): `MediaId` = series, `Volume` = requested | identified; a mismatch → reject with `WRONG_DISK` |
| 2 | **Own TOC** through the ladder (expected `MediaId` / `Volume`; S3 from the series TOC, for the identified volume) | read → `SameSetOnTape`. Unreadable but step 1 identified → **proceed**, with `NoReadableCopy` recorded |
| 3 | Neither: the **first block of the needed set**. Locate directly by the previous set's `FirstBlockAfterSet` if known, else by its on-volume index | a `SetHeader` or `FileHeader` with the expected `SetId` → identified; otherwise reject |

Per-file `CheckFileHeader` (SetId + FileId) still guards every file. With this, **T3 restores**: on headed media via
step 1, on headerless 2.1 media via step 3.

---

## 8. Write side

### 8.1 End-of-content without guessing (TL-3)

| Source, in order | Exact? |
|---|---|
| Series / in-memory TOC: `FirstBlockAfterContentOnVolume(identified volume)` | **yes** |
| Native TOC location (S0), confirmed by identification | yes |
| Any TOC copy found by the ladder: its `FirstBlockAfterContent` | **yes**, whichever copy, however found |
| File addresses (legacy sets without the value) | for 2.1 sets with `SizeOnTape` |
| A legacy copy, no value: the earliest copy found by S2 | only if unambiguous; otherwise **refuse** the append ("repair the TOC first") |
| "Blank" | only with positive evidence: EOD at BOM, or right after media header + FM |
| `ClosedByEom` on the last set | volume full → next volume (F32, unchanged) |

### 8.2 Phase-0 defect fixes

**F1 / F5 — a short write is a failure, with the truest available error (TL-7).** In `TapeWriteStream`:

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

- **`Flush`**, after the two spills: `if (m_buffer.IsNonEmpty || WentBad) { var ex = ShortWriteError(m_buffer.ContentSize, "flush incomplete"); Fault(); throw ex; }`.
  This replaces today's `if (WentBad)` block.
- **`Write`**, direct path: `if (WentBad || written == 0) { var ex = ShortWriteError(count - written, "write failed"); Fault(); throw ex; }`.

`Fault()` sets `WriteFailed`, so no filemark is written behind the empty copy.

**`BackupTOC` copy accounting:**
- one copy fails → OK + `CopyWriteFailed`;
- both fail → failure + `NoCopyWritten`;
- after copy 1 fails at EOM / EW, **copy 2 isn't attempted** (W1).

**F2:** `TapeStreamManager.BeginWriteTOC` disarms the content reserve (`Drive.SetEarlyWarning(0)`). The agent re-arms it
for the next content session. *To be confirmed against `TapeDrive.cs`.*

**F3:** the reserve is anchored after the media header. In `BeginWriteContentForCurrentSet`:

```csharp
            long headerFootprint = Navigator.MediaHeaderPresence == TapeHeaderPresence.Present
                ? TapeHeaderBlock.Size : 0L;
            long approxWritten = newSet
                ? (TOC.CurrentSetIndexOnVolume > 0 ? -1L : headerFootprint)
                : headerFootprint + TOC.ComputeContentSizeOnTapeBeforeCurrentSet(Drive.BlockSize,
                    withSetHeaders: Navigator.SetHeadersExpected);
```

*To be confirmed against `TapeTOC.cs`.* (Phase 3 can replace the arithmetic for an overwrite with the previous set's
`FirstBlockAfterSet`.)

**F4:** the navigator exposes `TocFootprint(long tocBytes)` = layout overhead + 2 × (TOC size rounded up to whole
blocks + mark). `BackupTOC` logs a warning when the armed reserve is smaller, and the multi-volume fixture asserts it.

**R2:** a navigator's "no marks" fallback applies only with positive blank evidence. A TOC search never uses it.

---

## 9. Integration summary

| Component | Change | Phase |
|---|---|---|
| `TapeWriteStream` | F1 / F5 | 0 |
| `TapeStreamManager` | F2; `LastSetFirstBlockAfter` in `EndWriteContentSet` | 0, 3 |
| Navigators | R2; `TocFootprint`; anchor moves; landmarks | 0, 4, 6 |
| `TapeFileBackupAgent` | F3; stamps `FirstBlockAfterSet`; end-of-content per §8.1 | 0, 3 |
| `TapeHeaderBlock` (+ `.Identify`) | peeker dispatch, `ReadAndIdentify`, typed accessors, retired projections | 1 |
| `TapeTOC`, `TapeFileHeader`, `TapeHeader`, checkpoint, `LegacyBlockPeeker` | explicit `ITapeBlockPeeker` implementations | 1 |
| `TapeScanner` | `ReadAndIdentify` (Phase 1); `TapeScanOptions` (Phase 4) | 1, 4 |
| `ITapeFileNotifiable` & co. | hierarchy split; TOC events | 2 |
| `TapeAgentBase` | TOC anomaly list and statistics; `BackupTOC` copy accounting | 2 |
| `TapeSetAgent` | takes `ITapeSetNotifiable` | 2 |
| Service | handler overrides, `ServiceTocProgressHandler`, `ReportTocAnomalyOutcome`, advice | 2 |
| `TapeTOC.Format`, `TapeTOC.Encryption` | fields of §6; `FirstBlockAfterContentOnVolume` | 3 |
| `TapeAgentBase.TocLocation.cs` (new) | ladder S0–S3, `LocateAndRestoreTocCore` | 4 |
| `TapeFileRestoreBaseAgent` | `CheckVolumeToResumeFrom` per §7 | 4 |
| Service | S4 confirmation; pre-flight TOC-size check; Help topic | 5, 6 |

---

## 10. Tests: build the damaged tape deterministically

New TEST_HOOKS on `VirtualTapeMedia` (diagnostics partial): `TruncateAfter(i)`, `RemoveMark(i)`,
`CorruptBlock(i, offset, bits)` (at rest), and `DescribeLayout()`.

| Test | Pins | Phase |
|---|---|---|
| `ShortWrite_ForwardsDriveError` / `_SynthesizesFromFlags` | §8.2 F1/F5 | 0 |
| `Reserve_AnchorsAfterMediaHeader`, `TocWrite_NotClampedByReserve` | F3, F2 | 0 |
| `Append_NoMarksFound_Refused` | R2 | 0 |
| `Peekers_AreTotal`, `Classifier_ProjectionsAgree` | §3.5 | 1 |
| `BackupToc_SecondCopyFails_SuccessWithAnomaly`, `_BothFail_NoCopyWritten` | §5.1 | 2 |
| `TocAnomalies_AccumulateAcrossVolumes`, `_RaisedOncePerVolume` | §5.1, §5.3 | 2 |
| `Advice_ByCauseAndLayout` | §5.4 | 2 |
| `FirstBlockAfterSet_StampedAfterMark` (all profiles), `_MinusOneWhenClosedByEom`, `_SurvivesDelete`, `_InSealedStub` | §0.1–§0.3, §6 | 3 |
| `FirstBlockAfterContent_LookupIsPerVolume` | TL-10 | 3 |
| `Toc_Copy1Only_NoClosingMark` | T1: S2 finds copy 1; recovery stage `AnchorWalk` | 4 |
| `Toc_Copy1Torn_Copy2Reads` | S1 | 4 |
| `Toc_AllMarksAfterSetGone_ExpectedPosition` | S3 by `FirstBlockAfterSet` | 4 |
| `Toc_ForeignCopy_Skipped`, `Toc_ImplausibleContentEnd_Skipped` | TL-5 | 4 |
| `Append_OneCopyTape_WritesAtFirstBlockAfterContent` | **the R2 guard** | 4 |
| `Resume_VolumeWithoutToc_IdentifiedByHeader` / `_ByFileHeader` | §7 | 4 |
| existing `MultiVolumeEncryptionTests` | green with the original geometry | 4 |
| `Toc_TailDestroyed_FullWalk_Confirmed` / `_Declined` | S4 | 5 |
| `Landmarks_ClearedOnMediaChange`, `_TruncatedOnWrite` | §4.3 | 6 |

---

## 11. Plan, with files needed per phase

Each phase's instructions end with a precise list of what the next phase needs. The lists below are the forecast.

| Phase | Content | Exit |
|---|---|---|
| **0** | F1/F5, F2, F3, F4 (warning + fixture assert), R2 | no unwritten TOC reported as written; no append at a guessed position |
| **1** | §3 classifier: peeker contract, dispatcher, `ReadAndIdentify`, accessors; scanner reads through it | one classifier; contract tests green |
| **2** | §5 notifiable hierarchy, TOC anomalies, statistics, `BackupTOC` accounting, service reporting and advice | partial writes reported, by cause |
| **3** | §6 format fields; `FirstBlockAfterSet` capture and stamping; per-volume lookup; goldens | values on tape, all profiles |
| **4** | §4 ladder S0–S3, scanner options; §7 resume identification; §8.1 end-of-content; tape-surgery hooks | the 6 failing tests green; T1/T3 surgery tests |
| **5** | S4 with confirmation; Design-MultiVolume and Design-FormatV21 cross-references | full ladder |
| **6** (optional) | session landmarks; pre-flight TOC-size check; Help topic text | — |

### 11.1 Phase 0

| File | Why |
|---|---|
| `Streams/TapeStream.cs` (current) | F1 / F5 |
| `Drive/TapeDrive.cs` | F2: reserve clamp, `SetEarlyWarning`, `NotifyNextContentWritePosition`, EW / EOM flags |
| `Media/TapeStreamManager.cs` (current) | F2: `BeginWriteTOC` |
| `Media/TapeNavigator.cs` (all navigator classes) | R2 fallbacks; `TocFootprint` |
| `Agents/TapeFileBackupAgent.cs` (current) | F3 |
| `Agents/TapeAgentBase.cs` (current) | `BackupTOC` (W1 and the F4 warning) |
| `Toc/TapeTOC.cs` | `ComputeContentSizeOnTapeBeforeCurrentSet` (F3) |
| `Tests/Helpers/MultiVolumeVirtualTapeFixture.cs`, `VirtualTapeFixture.cs` | F4 assertion; new tests |
| `Tests/.../TapeStreamDisposalTests.cs` | extend for F1 / F5 |
| `Virtual/VirtualTapeMedia*.cs` (only if the clamp lives there) | F2 |

### 11.2 Phase 1

| File | Why |
|---|---|
| `Headers/TapeHeaderBlock.cs`, `TapeHeaderBlock.Identify.cs` | dispatcher, `ReadAndIdentify`, projections |
| `Headers/TapeFramer.cs`, `Headers/TapeHeader.cs`, `Headers/TapeFileHeader.cs` | peekers |
| `Format/TapeFrame.cs` (+ `TapeFrameStatus`, `TapeRecordReader` if separate) | span peeking over frames |
| `Toc/TapeTOC.Format.cs` | `TapeTOC` peeker, `TocPeek` |
| `Legacy/LegacyIdentify.cs` | `LegacyBlockPeeker` adapter |
| `Calibration/TapeCalibrationCheckpoint.cs` | its peeker |
| `Scan/TapeScanner.cs`, `.Identify.cs`, `.Harvest.cs`, `TapeMediaFragment.cs` | migrate to `ReadAndIdentify` |
| `Agents/TapeAgentBase.Headers.cs`, `TapeAgentBase.SetVerification.cs`, `Media/TapeStreamManager.cs` | header-read callers |
| Existing tests on identification / scanning (e.g. `HeaderBlockIdentifyTests`, `ScanMedia*Tests`) | keep green; extend |
| **Grep output:** callers of `TapeTOC.TryPeek`, `TapeHeaderBlock.Classify` / `TryIdentifyHeaderBlock` / `Read`, `TapeFramer.UnpackHeader`, `IdentifiedBlock.TocVersion` / `TocMediaId` | migration |

### 11.3 Phases 2–6 (forecast; refined at the end of each phase)

| Phase | Files |
|---|---|
| 2 | `ITapeFileNotifiable.cs`, `TapeAgentBase.cs`, `TapeSetAgent.cs`, `ServiceOperationProgressHandler.cs`, `ServiceOperationResult.cs`, `TapeServiceBase.Outcome.cs`, `.Backup.cs`, `.Restore.cs`, `.DeleteSets.cs`, `TapeServiceBase.cs`, `TestNotifiable` (tests) |
| 3 | `TapeTOC.Format.cs`, `TapeTOC.Encryption.cs`, `TapeTOC.cs`, `TapeSetTOC` (if separate), `TapeStreamManager.cs`, `TapeFileBackupAgent.cs`, golden generators and tests |
| 4 | `TapeAgentBase.cs`, `TapeFileRestoreAgent.cs`, `TapeNavigator.cs`, `TapeScanner*.cs`, `VirtualTapeMedia*.cs`, `MultiVolumeEncryptionTests.cs` |
| 5 | `TapeServiceBase.cs`, `WpfServiceHost.cs` / `ConsoleUxServiceHost.cs` (only if the prompt wording changes), `Design-MultiVolume.md`, `Design-FormatV21.md` |
| 6 | `TapeNavigator.cs`; Help sources |

---

## 12. Deviations during implementation

### 12.0 Deviations from Phase 0

- **R2 has two valid "empty" forms, not one.** `SettleAtBeginOfContentIfBlank` accepts end-of-data at begin-of-content
  (blank), **and** a TOC copy standing at begin-of-content (a volume with zero content sets), identified by reading the
  block. Begin-of-content is established by evidence — a media header read at BOM — not by cached presence.
- **A refusal keeps the positional cause.** When the mark-based locate failed positionally (BOM, EOD, EOM), the refusal
  reports that error instead of `ERROR_INVALID_DATA`, so the agent's SH-20 retry from begin-of-content can run. The
  navigator still settles nowhere.
- **The TOC-mark layout without a mark.** No TOC mark on tape means no TOC at all: content ends at end-of-data, provided a
  closing filemark stands right before it. The mark is searched forward from the believed position, then backward from
  end-of-data (a miscount leaves the head past it).
- **`TOCMissing` on the navigator.** Set when end-of-content had to be located on a volume that carries no TOC; the agent
  reports it once per volume as `NotFoundNatively` (`NoteVolumeWithoutToc`), and a TOC written later in the operation
  settles it (`Retried`).
- **Filemark layout, tail without its closing filemark.** When counting back three filemarks fails, the tail is checked:
  a complete tail means too few marks (zero sets — left to R2); a tail without its closing filemark means the TOC write ran
  into end-of-media, and copy 1 is located by identification (2nd, then 1st filemark back). Runs only after the regular
  count failed: a healthy tape pays nothing.
- **F2 needed no disarming.** `TapeDrive.WriteDirect(…, honorEwReserve)`: the reserve clamps content writes only; TOC
  writes and the TOC mark's gap file bypass it. Early-warning tracking is unaffected.
- **W1** checks the drive's error for the closing filemark (`Drive.RanToEOM`), the agent's own for the data.
- **Error accessors.** `LastErrorWin32` is the single virtual error accessor (internal); `LastError`, `WentOK`,
  `WentBad`, `RanToEOM`, `RanToEOMorEOD` derive from it. On a `TapeDriveHolder` all of them include the drive's error;
  `OwnErrorWin32` reads the holder's own error alone.

### 12.1 Deviations from Phase 1

- `TapeTOC.TryPeek` became internal; a TOC header cut off by the block length identifies as `NotFramed`.
- Block 0 of the content partition goes through the same projection as every other block in the scanner: a TOC copy there
  (a header-less, TOC-only volume) is a TOC fragment and is harvested.
- `HeaderBlockIdentity.FileHeader`: a CRC-verified 2.1 file header at a block start is a positive identity.

### 12.2 Deviations from Phase 2

- **The anomaly records belong to the operation.** Backup, restore and delete clear them at their start; the TOC verbs
  never do — the backup writes the TOC between volumes and the restore reads each cartridge's TOC through the public
  verbs, and clearing there would lose the earlier volumes' anomalies. A fresh agent starts empty, so the stand-alone TOC
  verbs (load, rename, format) are operations of their own.
- **Raised once the outcome is known.** A TOC anomaly asks nothing, and the volume is known only from a copy that read
  (TL-10): copy 1's failure is raised after copy 2 decided — detected and recovered together.
- **Stage `Retried`.** A later success on the same volume within the operation — an enforced rewrite, a re-inserted
  cartridge whose TOC then reads, or a TOC written to a volume found without one — settles what was open there.
- **Advice `TocUnreadable`** (no copy readable, or the TOC position unreachable).
- **No `ServiceTocProgressHandler` yet.** The TOC-only service verbs report after the fact from the agent's records; the
  handler arrives with the only TOC question, `OnTocFullWalkConfirm` (Phase 5).
- **TOC statistics come from the agent, not the progress handler**: they can change without a callback (the initial TOC
  read, repeat findings on a volume). Set statistics change only inside callbacks.

### 12.3 Deviations from Phase 3

- `FirstBlockAfterSet` and `FirstBlockAfterContent`: plain non-negative `varuint` fields with default −1. The −1 never
  reaches the wire — elided on write, applied on read when absent; any other negative value is refused on write.
- `FirstBlockAfterSet` also travels in the clear stub of an encrypted set (field 9), so the per-volume lookup works while
  the set is locked. **The stub is authoritative**: the sealed content carries field 15 as well, unchecked against the
  stub, because a locked set may be restamped (trailing delete) while its sealed bytes are re-emitted verbatim.
- **Captured by the stream manager** (`LastSetFirstBlockAfter`, right after the closing mark): `EndWriteContent` rethrows on
  end-of-media and write faults after closing the set, so code behind that call in the agent would miss exactly those paths.
- A trailing delete stamps the retained last set with the new end of content (only when it differs).
- `CopyCount` records what the save intended (always 2) — a skipped copy 2 is what a reader must be able to suspect.

### 12.4 Deviations from Phase 4

- **No ±W window around the expected position (S3).** v3 computed the position by summing file sizes; v4 records it. The
  block is exact: S3 jumps there, identifies, and moves on to S2 when it is not a TOC copy. The size-sum fallback for legacy
  sets is dropped — a set without the value skips S3. On the TOC-mark layout, S3 skips the known structure (gap + three
  filemarks) before identifying.
- **Partition-explicit moves (TL-11).** Every absolute position names its partition: `TapeDrive.MoveToBlock(partition,
  block)` — a combined locate on partitioned media. The plain `MoveToBlock(block)` moves within the current partition and
  skips a move whose block number matches, whatever the partition.
- **The ladder on partitioned media.** S0 and S1 as before (initiator block 0). S3 does not apply to the TOC there — the
  recorded end describes the content partition, and the TOC always starts at initiator block 0. S2 walks the whole
  initiator partition (small, exhaustive). A blank initiator partition fails with that finding in the diagnosis.
  The plausibility check (content end ≤ the copy's block) applies only to copies standing in the content partition.
- **Anchors (S2).** The series anchor (start of the volume's last set) first; Setmarks: just after the last setmark;
  filemark layouts: the deepest of five…one filemarks back from end-of-data — not the TOC-mark search, which S0 ran
  already. A setmark tape whose last setmark is gone is left to S4.
- **The scanner is the walker.** `TapeScanner.WalkFrom(partition, block)` walks one partition from a position with the
  scan's identification and mark handling; `ScanMediaOptions.StopAfterTocCopies` bounds it; TOC fragments carry their
  peek (`TocPeek`) for filtering and ranking.
- **Identity filtering only against what is expected**: the series' MediaId (a resume) and the requested volume. A plain
  load expects nothing — the in-memory TOC may belong to the previous cartridge.
- **The block-length rule.** TapeNET writes TOC copies and media / set headers at 16 KiB, always. A read at another size
  than the block on tape reports `ERROR_INVALID_BLOCK_LENGTH`, delivers the data, and leaves the head past the block —
  real drives do, and the virtual drive now does too. Identification reads (`TapeHeaderBlock`) absorb it: a block of
  another size is not ours, but may be a set's first block, so it is identified and the error cleared. TOC stream and
  content reads keep failing on it; calibration absorbs it while walking back over the run header.
- **Identification on small-block drives** reads one block at the size the writer used: 16 KiB, clamped to the drive's
  maximum — never several blocks, which could cross a mark.
- **Resume identification (§7)** as designed, with step 3 locating the set by the previous set's `FirstBlockAfterSet`, else
  by counting from begin-of-content; legacy and encrypted sets cannot be identified by their first block.
- **The append guard (§8.1)** settles at the TOC's recorded end of content only when the tape confirms it — the layout's
  closing mark right before, the TOC area (a copy, the TOC mark, or end-of-data) right at it. Otherwise the navigator
  locates it as before. A legacy copy's end of content is not used.
- **The append guard reports what it found**: confirmed, unverifiable, or disputed. Confirmed replaces the mark search, so an append still verifies no set header and costs about the same; the "zero cost" of the verified-write design §3 now reads "no set-header read". Unverifiable or disputed: the marks locate the end of content, and the append goes ahead only if they land on the recorded block (logged at Warning). Otherwise it is refused before anything is written.
- **A confirmation that failed but was cross-checked is a TOC anomaly**: `CopyUnreadable` at the recorded end, with the drive's or reader's diagnosis, settled (Retried) when the operation's TOC replaces the overwritten copy. Not a set anomaly — no set header was read and no set drifted.
- **Advice tells the two settlements of a damaged copy apart.** Settled by its sibling (read path), the damaged copy is still on tape: re-save. Settled by a TOC write on that volume (`Retried`), it was replaced: no advice. Likewise, a TOC found only by searching advises a re-save only until this operation writes the TOC to that volume.
- **A detection that names no copy** (the TOC position, a search) is settled by any recovery of its kind on its volume; the recovery names the copy it found.
- **Rejection now means "unidentifiable", not "TOC unreadable"**. A cartridge whose own TOC does not read is restored when its media header or its set's first block identifies it (§7), with NoReadableCopy on record. The multi-volume rejection tests (both `Scenarios` and `Services`) now must make the whole cartridge unidentifiable to construct a full rejection case. Design-MultiVolume §5.2/§5.3 to be updated in the final rewrite.

### 12.5 Deviations from Phase 5

- **S4 never runs on a multi-volume resume.** A series TOC is at hand: it addresses every file, and the volume is
  identified by its header or its set (§7). S4 is for a TOC that is otherwise lost — a plain load, the restore's initial
  read. Also skipped when an S2 walk already started at content block 0.
- **S4 walks the content partition on every layout** — on partitioned media, S2 already walked the whole TOC partition.
  No copy limit: every copy is collected and ranked.
- **The question is a media prompt**, `MediaPromptContext.FullWalkForTOC` with verdict `Unidentified` — no new host API.
  Unattended: Proceed (read-only). `IdentifyMediaAsync` does not ask it again after the user said "Search" to
  `SearchForTOC`. No notifiable: S4 runs unasked.
- **Progress and cancellation reuse the walker.** `ITapeTocNotifiable.OnTocSearchProgress` receives the scanner's samples
  (phase `searching-toc`); the agent's abort flag reaches the walk through `ScanMediaOptions.AbortPoll`. An abort is
  `ERROR_CANCELLED` and no anomaly. The service's TOC reads get a `ServiceTocProgressHandler` (factory
  `CreateTocProgressHandler`); WPF shows the progress in the TOC load overlay, whose Cancel stops the walk.
- **A declined S4** leaves the detection with `CanAttemptRecovery = true`, and the diagnosis says it was declined.
- **S2 backlog:** an anchor equal to S3's block starts one file later; the setmark anchor walks with `NoSetmarksAhead`
  (every fragment closed by a filemark — no setmark probe per fragment).
- **Setmark → filemark fallback in the TOC walks** (`SetmarkFallbackToFilemark`): a set whose closing setmark is lost no
  longer ends the walk before the TOC — the next filemark reaches the copy behind it. Scan Media keeps reporting such a set
  as never completed.

#### 12.5.1 Deviations from Phase 5b

- **S4 as the resume's last resort** (step 4 of §7): when neither the media header, nor S0–S3, nor the set's first block
  identifies a requested volume, the whole medium is searched for its own TOC — asked first, filtered by the series TOC,
  checked like step 2. Covers headed volumes whose header and set header are damaged, encrypted ones included (the TOC
  stub is plain text).
- **Append without a loaded TOC is refused** — it used to fall back to an overwrite, a clear functionality gap.

#### 12.5.2 Deviations from Phase 5c

- **Known limit — the walk meets copy 2 only, when the set before the TOC lost its closing mark.** The set fragment then
  ends at the next filemark, copy 1's own; copy 1 is read as the set's tail. Copy 2 of the same save is found — if it is
  damaged too, the walk fails although copy 1 is intact. A bounded backward probe for the TOC magic before that filemark
  would close it; not done.
- **A volume identified by S4 is read without set-header verification** when its media header did not read: presence is
  then unknown, so set headers are not expected. Files stay guarded by their own headers and CRCs.
- **Partitions:** the ladder is S0, S1, S2 (the whole TOC partition), S4 (the content partition). S3 does not apply to the
  TOC there. S4 recovers only on a mixed-up cartridge — on a healthy series it reports, asked first, that nothing was
  found, next to "the TOC partition is blank".
- **A recoverable detection is reported at Warning** (`CanAttemptRecovery`), e.g. step 2's on a resume, where step 4 may
  still follow — no "Failed" line before a recovery.

#### 12.5.3 Deviations from Phase 5d

- **Test parity (5d):** the multi-volume fixture arms a layout-aware TOC reserve — the navigator's footprint for two
  copies plus room for the closing marks — so every layout keeps both copies, closed, on a full volume. A guard test
  checks it on every volume of every profile and header mode. Single-copy and open-tail volumes are tested only where a
  test asks for them (`SingleCopyTOCCapacity`).
