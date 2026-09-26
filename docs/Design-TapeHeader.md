# Design — Tape Media Header for TapeLibNET

**Status:** v12 · **implemented, integrated, and green** across library, service, CLI, and WPF
**Scope:** the media header (BOM identity record) and everything that consumes it
**Depends on:** `MediaId` (Guid) in `TapeTOC`

---

## 1. What the feature does

Every medium TapeLibNET formats or writes from beginning-of-media now carries a **media header**: one
16 KiB framed record, terminated by a filemark, at the beginning of the content partition, which
positively identifies the cartridge in a single cheap block read.

This delivers four user-visible capabilities:

- **No more churning on the wrong cartridge.** Loading a medium reads the header first. A calibration
  cartridge is recognized and reported immediately — the library never seeks to end-of-data hunting for
  a table of contents that cannot exist. Genuinely unidentified media asks the user before the costly
  search.
- **Identity verification before destructive work.** Overwrite, multi-volume continuation, restore, TOC
  import, and calibration all compare the loaded header against what the operation expects, and surface
  a typed verdict with a context-appropriate prompt.
- **Precise cross-subsystem recognition.** Backup and calibration each recognize the other's media by
  name: "this cartridge holds a backup" / "this is a calibration cartridge", rather than a blank failure.
- **Graceful legacy coexistence.** Header-less media written by earlier versions still loads, restores,
  and appends. Mixed series (legacy volume 1, headed volume 2+) restore correctly end to end.

The header is **additive and non-destructive**: it is never inserted into already-written media, and its
own filemark is never counted as a set separator — setmark/filemark *arithmetic* is unchanged, because
every content-side path reaches begin-of-content by spacing over the header's mark rather than by
counting from BOM. **Exception:** some optimization paths that account for the header filemark for filemark
movement operations -- yet only under strict checking for the header-present condition (s. §5.7)

---

## 2. Design principles

| | |
|---|---|
| **Cheap positive identity** | One 16 KiB framed-CRC block answers "whose medium is this, which volume, which kind?" — no TOC parse, no EOD seek. |
| **Uniform across layouts** | Written on *all* formatted media, single- and multi-partition alike, always at content-partition BOM. |
| **Counting is sacred** | The header lives outside *set* counting. Its trailing filemark is never attributed to a set boundary: every content-side path spaces over it via one primitive, so set indices are unaffected. |
| **Agent writes, service evaluates** | Writing is mechanism (agent); judging identity and prompting is policy (service). The navigator does neither — it counts and caches presence. |
| **Positive classification only** | A medium is "ours" only when a framed CRC validates *and* the kind byte is known. Everything else is Unidentified and never blocks. |

---

## 3. Header types and framing

### 3.1 The hierarchy

```csharp
public enum TapeHeaderKind : byte { Unknown = 0, Media = 1, Calibration = 2, Set = 3 }

public abstract record TapeHeader : ITapeSerializable
{
    public const uint FixedHeaderBlockSize = 16 * 1024;   // media header; calibration uses the run block

    public abstract TapeHeaderKind Kind { get; }
    protected Guid Id        { get; init; }   // surfaced as MediaId / RunId
    protected uint BlockSize { get; init; }   // surfaced as TocBlockSize / RunBlockSize
    public DateTime CreatedUtc { get; init; }

    protected void SerializePreamble(TapeSerializer s);           // sig, Kind, Id(16B), CreatedUtc, BlockSize
    private static TapeHeaderPreamble? ReadPreamble(TapeDeserializer d);   // null on signature mismatch
    public abstract void SerializeTo(TapeSerializer s);

    public static ITapeSerializable? ConstructFrom(TapeDeserializer d)     // polymorphic probe entry
    {
        if (ReadPreamble(d) is not { } p) return null;
        return p.Kind switch
        {
            TapeHeaderKind.Media       => TapeMediaHeader.ConstructBody(d, p),
            TapeHeaderKind.Calibration => TapeCalibrationHeader.ConstructBody(d, p),
            _ => null,
        };
    }

    public abstract override string ToString();   // the human-readable "namely …" detail
}
```

| Symbol | Kind | Notes |
|---|---|---|
| `TapeHeader` | abstract base | `Id`, `BlockSize` protected; `Kind`, `CreatedUtc`; polymorphic `ConstructFrom`; abstract `ToString()` |
| `TapeMediaHeader` | `: TapeHeader` | `Kind = Media`; `MediaId ⇒ Id`; `TocBlockSize ⇒ BlockSize`; `Volume`, `TocPlacement`, `OriginalName`, `DisplayName` |
| `TapeCalibrationHeader` | `: TapeHeader` | `Kind = Calibration`; `RunId ⇒ Id`; `RunBlockSize ⇒ BlockSize`; `ProfileKey`, `CapacityReportedAtBom`, `Plan` |
| `TapeFramer` | static framing | `Pack` / `Unpack<T>`; `Unpack<TapeHeader>` is the polymorphic probe |
| `TapeCalibrationFramer` | static forwarder | thin wrapper over `TapeFramer` |

`ConstructFrom` is a compiler-checked switch. The hierarchy is closed, co-versioned, and
single-assembly, so a self-registration registry stays YAGNI until cross-assembly kinds appear.

### 3.2 `BlockSize` is a reinterpretable slot

The base slot is *not* the header's own block size (that is the caller's fixed 16 KiB, known before any
field is read). Each kind gives it meaning: `TapeMediaHeader.TocBlockSize` records the **TOC's** on-tape
block size — immutable and forward-looking, so larger TOC blocks can be adopted later without probing —
while `TapeCalibrationHeader.RunBlockSize` records the calibration run block. `Id` is likewise protected
and surfaced as `MediaId` / `RunId`.

### 3.3 Media header contents

| Field | Where | Source |
|---|---|---|
| Signature / `Kind` (Media) / `Id` (MediaId) / `CreatedUtc` / `BlockSize` | preamble | TOC |
| `Volume` | body | TOC `Volume` — immutable per tape |
| `TocPlacement` | body | `InSet` or `InPartition` — self-describes the layout |
| `OriginalName` | body | TOC `Description` snapshot, clamped to fit the frame; null ⇒ generated |

`DisplayName` falls back to `Media {Id:N} · vol {Volume} · {CreatedUtc}` when `OriginalName` is null. The
current, renameable name is never stored here — renaming rewrites the TOC, and UIs show the TOC's live
description.

### 3.4 Media headers: the TOC is the factory

```csharp
// TapeTOC — sole builder of the MEDIA header. tocBlockSize is the agent's fixed TOC block.
public TapeMediaHeader CreateHeader(uint tocBlockSize, TapeTocPlacement placement) =>
    new()
    {
        MediaId      = EnsureMediaId(),
        CreatedUtc   = CreationTime,
        TocBlockSize = tocBlockSize,
        Volume       = Volume,
        TocPlacement = placement,          // REQUIRED: the TOC cannot know where it resides
        OriginalName = TapeMediaHeader.ClampName(Description),
    };
```

`placement` is a required parameter because the TOC genuinely does not know its own placement; the agent
derives it from the navigator type (`TapeNavigatorTOCInPartition ? InPartition : InSet`).

### 3.5 Calibration headers: the calibrator is the factory

Each header kind is built by the subsystem that owns its identity — there is no single global factory, and
no `ITapeHeaderMaker` abstraction. The TOC owns media identity; **`TapeCalibrator` owns run identity** and
builds its own header:

```csharp
var header = TapeCalibrationHeader.CreateHeader(
    runId, drive.DriveProfileKey, capacityReportedAtBom, runBlockSize, DateTime.UtcNow, plan);
```

The two factories are peers. What they share is the **grammar** (`TapeHeader` preamble + `TapeFramer`) and
the **block** (`TapeHeaderBlock`), not a construction path:

| | Media header | Calibration header |
|---|---|---|
| Built by | `TapeTOC.CreateHeader` | `TapeCalibrationHeader.CreateHeader` (called by `TapeCalibrator`) |
| Identity (`Id`) | `MediaId` — the TOC's series id | `RunId` — this calibration run |
| `BlockSize` slot | `TocBlockSize` — the TOC's on-tape block | `RunBlockSize` — the run payload's block |
| Written by | the agent, via `TapeStreamManager` (navigator-positioned) | the calibrator, directly (positioned by `PrepareDrive`) |
| Block on tape | one standard `TapeHeaderBlock.Size` block | **the same** standard block |

Because both kinds occupy the same standard block, either subsystem can classify the other's cartridge with
one read — which is what makes "this is a calibration cartridge" / "this holds a backup" possible without a
size-negotiation dance.

---

## 4. Why the header goes on partitioned media too

The header's original job was dodging the minutes-long EOD seek, which partitioned media never suffered.
Three later capabilities made placement-independent identity worth having everywhere:

- **Uniform identity.** MediaId + Volume + Kind from one 16 KiB block, versus parsing an entire TOC.
- **Cheap per-volume identity.** Overwrite validation ("whose tape is this before I destroy it?"),
  multi-volume restore (which deliberately does *not* re-read the TOC per volume — without a header a
  partitioned continuation volume would have no cheap identity at all), and the `SkipVolumeCheck`
  opt-out, which is only coherent if a *uniform* check exists to skip.
- **Cross-subsystem recognition** (§8), which is layout-agnostic by nature.

**Placement is content-partition BOM, never the initiator partition.** Restore then pays zero partition
switches for identity (identify → navigate content, same partition); the initiator-TOC path — already
fast, already carrying MediaId — is untouched; and no cross-partition clobber is possible, since on
partitioned media the content-BOM header and the initiator-partition TOC occupy different partitions.

---

## 5. Layout and navigation

### 5.1 Layout

```
Single-partition:  ‹MH›<FM>[set0][SM][set1][SM]…[setN][SM][toc1][FM][toc2][FM]
Partitioned:       content: ‹MH›<FM>[set0][SM]…[setN][SM]   |   initiator: [toc1][FM][toc2][FM]
```

`‹MH›` is one logical block at content-partition BOM, written with a single `WriteDirect` and
**terminated by one filemark**. Content begins immediately past that mark.

**Why the mark exists.** Tape drives classically accept a write only at BOP, at EOD, or immediately after
a mark. Without the filemark, every "write content from begin-of-content" — the first set on a volume, an
overwrite, a delete-all — would be a **mid-data** write whenever anything already follows the header, and
a strict drive family would reject it outright. The mark makes every content-start a legal **post-mark**
write on every drive, at the cost of one mark per volume. (An earlier revision omitted it; §15.1 records
the hazard and the hardware evidence that prompted the change.)

**Position is reached by SPACING, never by block arithmetic.** Begin-of-content is `MoveToNextFilemark(1)`
from BOM, not `MoveToBlock(n)`. Spacing needs no assumption about whether a drive numbers marks in its
logical block space — real drives generally do not, while the virtual backend deliberately does — and it
is the same primitive the TOC path already relies on.

### 5.2 Presence and sentinels

`TapeHeaderPresence { Unknown, Present, Absent }` — held by the navigator as `HeaderPresence`. Every
navigator starts `Unknown`; the agent resolves it. Position sentinel `AtHeader = InTOCSet + 1`.

Presence resets to `Unknown` on every media (re)load, which is what makes mixed headed/headless series
work (§10.3).

### 5.3 Header-aware begin-of-content

`MoveToBeginOfContent`:

- **Present** → content BOM, then **space forward over the header's filemark** → begin-of-content,
  `CurrentContentSet = 0`
- **Absent** → content BOM → block 0, `CurrentContentSet = 0`
- **Unknown** → treated permissively as Absent (no skip)

`MoveToHeader()` positions at content-partition BOM and sets `CurrentContentSet = AtHeader`;
`MoveToTargetContentSet` includes `AtHeader` in its from-end guard so a negative target while at the
header first moves to a known boundary.

**`AtHeader` covers two physical positions, and both work.** `MoveToHeader()` leaves the head *before*
the header block; `ResolveHeaderPresence(Present)` — after an agent read consumed the block — leaves it
*after* the block but *before* the mark. One forward filemark space reaches begin-of-content from either,
because the header block itself carries no marks. This is what lets the `AtHeader` fast paths skip the
rewind entirely.

> **`AtHeader` ≠ header present.** The sentinel says *where the head is*; `HeaderPresence` says *whether a
> header exists*. They are set independently — `MoveToHeader(forWrite: true)` parks at `AtHeader` on a
> not-yet-headed tape, which is exactly what the header *write* path does. Any `AtHeader` shortcut must
> therefore test presence before spacing (INV-19); inferring one from the other spaces to the first mark
> on tape, which on a setmark layout is the **TOC's** — far past all content.

### 5.4 The navigator stays usable standalone

`MoveToBeginOfContentFromBom` must **not** hard-fail on `Unknown`. The navigator has to work with no
agent (unit tests, direct use), so **only `Present` triggers the skip**; `Unknown`/`Absent` assume no
header and land at block 0 — the legacy-safe default. A strict "must be resolved" guard would break
every standalone navigator test while defending against nothing real: every production path resolves
presence at an agent choke-point before reaching here.

### 5.5 `MoveToBeginOfContentFromBom` — the single assume-blank primitive

On headed media, **"at BOM" ≠ "at the oldest set"**: the oldest set starts at block 1, BOM *is* the
header. Every handler that treats a BOM landing as "we're at the oldest set / the tape is blank" routes
through `MoveToBeginOfContentFromBom()`. **Eight sites**, two families:

- **Five assume-blank fallbacks** (no-mark else-branches): `TapeNavigatorTOCInSet.MoveToBeginOfContent`;
  `…WithSmks.MoveToEndOfContentInternal`; `…WithFmks.MoveToEndOfContentInternal`;
  `…WithFmksAndTOCMark.MoveToBeginOfContent` **and** `.MoveToEndOfContent`.
- **Three `ERROR_BEGINNING_OF_MEDIA` handlers** (where `ResetError()` means "fine, we're at the oldest
  set"): `MoveToTargetContentSet` from-end branch; `MoveToTargetContentSet` from-beginning branch
  (target 0); `…WithSmks.MoveToTargetContentSet` optimized-from-`InTOCSet` override.

Maintenance rule: `grep -n "ERROR_BEGINNING_OF_MEDIA\|Drive.Rewind()" TapeNavigator.cs` — every
**content-side** "assume at oldest/blank" hit routes through the primitive. The **TOC-side**
`WithFmksAndTOCMark` `UnknownSet` rewind stays **raw**: it scans *forward* for the 3-filemark TOC mark
and is header-transparent by construction.

### 5.6 Partition navigator — combined LOCATE for Absent, space for Present

`TapeNavigatorTOCInPartition` routes header I/O and begin-of-content to the **content** partition.
`MoveToPartition(Content, block)` sets partition and block in one Win32 `SetTapePosition`, so the
**Absent/Unknown** case is a single combined LOCATE to block 0. The **Present** case cannot use a block
number (that would assume how the drive counts marks), so it switches to the content partition at block 0
and then spaces forward over the header's filemark.

The Absent branch still performs a real repositioning rather than a no-op — otherwise a legacy partitioned
tape would mis-navigate from a stale (e.g. initiator) position. The `CurrentContentSet == 0` fast path is
retained so a header read that already set 0 does not redo the switch.

### 5.7 Merging the header filemark into the forward set count (Optimization)

On a **filemark-delimited** layout the header's trailing mark is the same mark type as the set
separators, so it is indistinguishable from one to a forward SPACE. Counting from BOM:

```
       FM#1          FM#2          FM#3
‹MH›  <FM>  [set0]  <FM>  [set1]  <FM>  [set2] …

filemarksFromBom(N) = N + (headerPresent ? 1 : 0)
```

`TapeNavigatorTOCInSet.MoveToTargetContentSet` therefore takes a fast path when **all three** hold:
`!UseSmks`, `TargetContentSet >= 0`, and `CurrentContentSet < 0` (i.e. `UnknownSet` / `InTOCSet` /
`AtHeader` / a from-end index). It reaches set N with **one** space over `N + headerFilemarks` marks
instead of "space past the header, then space N more" — saving one transport stop-and-restart per set
navigation. From `AtHeader` no rewind is needed at all (§5.3).

**The guard is `!UseSmks`, not the navigator type.** With real setmarks the layout is one *filemark* then
N *setmarks* — two mark types, unmergeable. Since `UseSmks` can be false on a setmark-capable drive, the
condition belongs on the flag. Placing the override in the shared `TapeNavigatorTOCInSet` base gives it to
both filemark navigators (`…WithFmks`, the LTO single-partition path, and `…WithFmksAndTOCMark`) with **no
hierarchy change** — which is why those two remain independent: `…WithFmksAndTOCMark` overrides everything
`…WithFmks` defines (their TOC-locating strategies genuinely differ), so derivation would inherit nothing.

The saving applies when a header is present and the target is ≥ 1. Because the *newest* set is usually
reached by negative indexing, the win lands on older / middle sets — and it makes the header's navigation
cost on this path provably **zero** rather than "one extra space".

---

## 6. Header I/O

### 6.1 Atomic block operations, no dedicated tape state

The header is one 16 KiB block plus one filemark, so write and read remain single-shot operations rather
than persistent streams. `WriteHeaderBlock` / `ReadHeaderBlock` do `EndReadWrite()` → position → the block
op, staying in `MediaPrepared`. The `_operationLock` plus `EndReadWrite()` already provide interleaving
safety, and the block ops never leave a restful externally-visible state.

`TapeHeaderBlock.WriteFramed` emits **block + filemark** as one unit, so the write path ends at
begin-of-content and `OnHeaderWritten()` legitimately records `CurrentContentSet = 0`.

`TapeHeaderBlock.Read` is deliberately **pure** — it reads the block and classifies, leaving the head
*before* the trailing mark. `ResolveHeaderPresence(Present)` therefore parks at **`AtHeader`**, not at
begin-of-content, so any later content navigation routes through the space-over-the-mark primitive. The
write-after / read-before asymmetry is the one thing to keep in mind when touching either path.

Both **reset the content position on any early failure** (a torn write or read leaves position unknown).

### 6.2 `read ≤ 0` at BOM means Absent, not Unknown

Reaching BOM and finding no data (blank tape, or at EOD) is a **definitive "no media header"** and
resolves to `Absent`. It is not an unresolved state: nothing retries `EnsureHeaderResolved`, so leaving
`Unknown` would wedge all later content navigation. `Absent` lets navigation proceed and skip nothing —
exactly right for header-less media. A readable block that is not our media header (calibration,
foreign, torn) likewise resolves `Absent` for backup purposes, while the service still learns the
concrete kind from the polymorphic read.

### 6.3 Presence resolves at content choke-points only — never TOC

Header presence is a *content*-navigation concern. TOC navigation is **end-relative** (fast-forward, step
back over the last mark) and never rewinds to BOM, so it must not resolve.

| Path | Header presence | Rewinds to BOM? |
|---|---|---|
| TOC (read/write) | never resolve — end-relative, header-agnostic | no |
| Content (read/write) | resolve first, at the agent choke-point | yes (skips header when Present) |
| Navigator standalone | `Unknown` treated as `Absent` (no skip) | — |

Choke-points calling `EnsureHeaderResolved`: `TapeFileBackupAgent.BeginWriteContentForCurrentSet`,
`TapeFileRestoreBaseAgent.BeginReadContentForCurrentSet`, and
`TapeFileAgent.DeleteSetsFromCurrentSetUp` (which navigates content directly, so it resolves before its
begin-of-content move — otherwise delete-all on a headed tape would clobber the header).

### 6.4 One physical read serves probe and parse

`ProbeHeaderPresence` is `ReadHeader`-and-return-presence: the fixed 16 KiB block pulls the whole header
in one `ReadDirect`, so a positive probe needs no re-read. The **agent** parses; the navigator only
caches presence.

---

## 7. Writing the header

> Two subsystems write headers, each for the kind it owns: the **agent** writes the media header (via
> `TapeStreamManager`, so the navigator positions it and learns its presence), and the **calibrator** writes
> the calibration run header (positioning itself at BOM in `PrepareDrive`). Neither goes through the other,
> and the *service* writes none at all.
>
> What they share is the block primitive: both call **`TapeHeaderBlock`**, which owns the standard block
> size, the framing, the size guard, and the set-and-restore block-size discipline. That single point ensures
> that a header written by one subsystem is always readable by the other.

### 7.1 Two BOM moments, one condition

```csharp
// TapeFileAgent (base) — the format path uses the base agent, so the gate lives here.
public bool WritesMediaHeader { get; set; } = true;

// TapeFileBackupAgent.BeginWriteContentForCurrentSet
if (CurrentSetIndex == FirstSetOnVolume && WritesMediaHeader)
    WriteHeader();           // heads the (fresh / continuation) volume; sets Present, positions at block 1
else
    EnsureHeaderResolved();  // existing / legacy media: probe → Present or Absent
```

`CurrentSetIndex == FirstSetOnVolume` (⟺ `CurrentSetIndexOnVolume == 0` ⟺ "write content from BOM")
fires precisely on volume 1's first set **and** every continuation volume's first set — one condition,
both moments. `WriteHeader` resolves presence internally, so the `else` is correct with no redundant read.

**Why the write lives inside the agent, not the service:** `ResumeBackupToNextVolume` performs
`TOC.Volume++` and the first content write atomically, leaving no external seam to inject a
correctly-numbered header between them. Writing from outside would record the *previous* volume number.
Inside `BeginWriteContentForCurrentSet` the write runs *after* the volume bump, so `TOC.CreateHeader`
reads the correct volume.

### 7.2 Format writes the header with the initial TOC

```csharp
/// <param name="writeHeader">
/// Write the media header before the initial TOC. TRUE on the format / fresh-media path;
///  FALSE on the delete-all path, which must preserve — never rewrite — the existing header.
///  Defaults to <see cref="WritesMediaHeader"/>.
/// </param>
public TapeResult BackupInitialTOC(bool? writeHeader = null)
{
    Navigator.AssumeBlankMedia();

    if (writeHeader ?? WritesMediaHeader)
    {
        var hr = WriteHeader();
        if (!hr) return hr;
    }

    return BackupTOC();
}

public TapeResult WriteHeader()
{
    var placement = TOCPlacement;   // Navigator is TapeNavigatorTOCInPartition ? InPartition : InSet
    var header = TOC.CreateHeader(c_fixedTOCBlockSize, placement);
    // …frame + Manager.WriteHeaderBlock…
}
```

`DeleteSetsFromCurrentSetUp`'s delete-all branch calls `BackupInitialTOC(writeHeader: false)`: it has
already navigated to begin-of-content (block 1, past the header), so the fresh initial TOC overwrites
content only and the header survives.

### 7.3 Layering stays intact

By backup time the service has already probed at load, shown any verdict, and obtained overwrite
confirmation — so `TOC.MediaId` is "the identity we are authorized to write". The agent stamping it is
mechanism executing a decision already made; the wrong-media *verdict* remains a load-time,
service-owned concern.

---

## 8. Classification and cross-subsystem recognition

### 8.1 The polymorphic probe

`TapeFramer.Unpack<TapeHeader>(block, len)` is the class-flexible reader — `TapeHeader.ConstructFrom`
dispatches on the Kind byte and returns the concrete type:

```csharp
switch (TapeFramer.Unpack<TapeHeader>(block, len))
{
    case TapeMediaHeader m:       /* backup media  — m.MediaId, m.Volume */   break;
    case TapeCalibrationHeader c: /* calibration   — c.RunId */               break;
    case null:                    /* foreign / blank / torn */                break;
}
```

Two modes, one framer: **narrow** (`Unpack<TapeMediaHeader>` → the media header, or null for wrong-kind
*or* foreign) and **polymorphic** (`Unpack<TapeHeader>` → the concrete kind, null only for blank/foreign).

### 8.2 The agent surface

`TapeFileAgent.ReadHeader()` returns the **polymorphic `TapeHeader?`**, and caches presence on the
navigator (`Present` for a media header, `Absent` for anything else). The service maps kind-versus-
expectation to a verdict and uses `header?.ToString()` for the human-readable detail.

### 8.3 Symmetric calibration-side classification

`TapeCalibrator` is dual-role like the agent, so it classifies with the same polymorphic probe and
remembers a stranger for the service to name:

```csharp
TapeHeader? any = read > 0 ? TapeCalibrationFramer.Unpack<TapeHeader>(recordBuffer, read) : null;
if (any is TapeCalibrationHeader header) return header;                            // our kind
if (any is not null) { ForeignHeader = any; /* set error, trace */ return null; }  // Media/Set
/* else: blank / foreign / torn → null */
```

`ForeignHeader` (reset per verb) lets a failed Resume/Recalibrate report *"This cartridge carries a
different header: …"* instead of a blank "no calibration header".

**Cross-detection read size.** A subsystem classifying with a block smaller than 16 KiB must size the
read to hold a media header, or `Unpack`'s length guard drops it:
`int probeLen = (int)Math.Max(runBlockSize, TapeHeader.FixedHeaderBlockSize);`. Self-reading is already
safe (write guard in `RecordBlockWriter.Emit`, read guard in `Unpack`).

---

## 9. Service layer — check, decide, prompt

The service never writes headers; it reads one at every media load, judges it per operation, and drives
every prompt.

### 9.1 Loaded-header state

```csharp
protected TapeHeader? _loadedHeader;
public TapeHeader?             LoadedHeader       => _loadedHeader;
public TapeMediaHeader?        LoadedMediaHeader  => _loadedHeader as TapeMediaHeader;
public TapeCalibrationHeader?  CalibrationHeader  => _loadedHeader as TapeCalibrationHeader;
public TapeCalibrationMediaInfo? CalibrationInfo  => _loadedCalibrationInfo;
```

`RefreshLoadedHeader()` reads and classifies the BOM header into `_loadedHeader` (one cheap block).
Non-throwing: any failure leaves it null. It prefers the **live `_agent`** when present, so the agent
that owns the navigator is the one moving the tape, keeping its presence and position coherent;
otherwise a throwaway probe agent is used.

> **Precondition:** this moves the tape (rewinds to BOM). Call only at load / reload / between volumes —
> never mid-stream. The caller must hold `_operationLock`.

It also clears `_loadedCalibrationInfo`, so a cached calibration trail can never describe a
previously-loaded cartridge. Call sites: end of `LoadMediaAsync`; after the reload inside
`FormatMediaAsync`; per inserted volume in the backup and restore continuation loops; and in
`ClearTocState`.

### 9.2 The verdict

```csharp
public enum TapeMediaVerdict
{
    Match,             // right kind, right MediaId (+ Volume when checked)
    Unidentified,      // null — blank / legacy / foreign
    WrongKind,         // e.g. a calibration cartridge met in a backup context
    MediaIdMismatch,   // media header, different series
    WrongVolume,       // media header, right series, wrong Volume
    MediaInconsistent, // reserved for set-header verification (Outlook)
}

protected TapeMediaVerdict EvaluateLoadedHeader(Guid? expectedSeriesId = null, int? expectedVolume = null)
    => _loadedHeader switch
    {
        null                  => TapeMediaVerdict.Unidentified,
        TapeCalibrationHeader => TapeMediaVerdict.WrongKind,
        TapeMediaHeader m when expectedSeriesId is { } s && m.MediaId != s => TapeMediaVerdict.MediaIdMismatch,
        TapeMediaHeader m when expectedVolume  is { } v && m.Volume  != v => TapeMediaVerdict.WrongVolume,
        _                     => TapeMediaVerdict.Match,
    };
```

**The golden rule: `Unidentified` never blocks an identity check.** Blank, legacy, and foreign media
cannot be *verified*, and a legacy volume is perfectly legitimate — so restore proceeds and overwrite
treats it as safe. Only the three positive mismatches prompt in verify contexts. (The one deliberate
exception is `SearchForTOC`, §9.5, where "I cannot identify this" is precisely the question being asked.)

### 9.3 Unified presentation

```csharp
public enum MediaPromptContext
{
    OverwriteBackup,     // backup, overwrite mode: this medium holds content we would destroy
    ContinuationVolume,  // backup: a fresh continuation volume carries a foreign / other-series header
    VerifyRestore,       // restore / append: the header should match the TOC we are using
    ImportToc,           // import-TOC-from-file: header vs. the imported TOC (one-off)
    CalibrateScratch,    // calibration: the cartridge holds a backup we would erase
    SearchForTOC,        // media load: no header — search end-of-data for a TOC, or not?
}

public enum MediaMismatchChoice { Retry, Proceed, ProceedAlways, Abort }

MediaMismatchChoice OnMediaMismatchConfirm(
    string headerDescription, TapeMediaVerdict verdict, MediaPromptContext context,
    bool allowRetry, bool allowProceedAlways);
```

The **context enum** carries what the user is proceeding *into*, so each host builds its own localized
wording, severity, and button labels from `(verdict, context)`; the library passes no user-facing text.
`allowRetry` is offered only where eject/insert machinery exists (continuation loops, calibration
scratch-swap); `allowProceedAlways` is hidden for one-off operations such as TOC import.

```csharp
protected MediaMismatchChoice PresentVerdict(
    TapeMediaVerdict verdict, MediaPromptContext context,
    bool suppress, bool allowRetry = false, bool allowProceedAlways = true)
{
    // Match is always benign. Unidentified is benign for identity-VERIFY contexts, but under
    //  SearchForTOC it is exactly the case we must ask about — do the lengthy EOD seek or not?
    bool benign = verdict == TapeMediaVerdict.Match
        || (verdict == TapeMediaVerdict.Unidentified && context != MediaPromptContext.SearchForTOC);
    if (benign)
        return MediaMismatchChoice.Proceed;

    string text = _loadedHeader?.ToString() ?? "Unidentified media";

    if (suppress)
    {
        LogWarn($"Media check ({VerdictToString(verdict)} / {context}) suppressed — proceeding: {text}");
        return MediaMismatchChoice.Proceed;
    }

    LogWarn($"Media check ({VerdictToString(verdict)} / {context}): {text}");
    return _host.OnMediaMismatchConfirm(text, verdict, context, allowRetry, allowProceedAlways);
}
```

`VerdictToString` supplies a culture-neutral label for **logs only**; the prompt text is the host's and
is localized there. **Host guidance:** a non-interactive host returns `Proceed` (preserving legacy batch
behaviour — an unattended backup must not stall) and logs the auto-decision; the one exception is
`CalibrateScratch`, where hosts return `Abort` so a quiet run never erases a backup to calibrate.

### 9.4 Media load — identify, then dispatch

`RestoreTOCOrCalibrationAsync` is the header-gated entry point that delivers the headline capability:

```csharp
public enum RestoreTOCOrCalibrationOutcome
{
    TocLoaded,         // backup media identified (or the user opted to search): TOC read
    CalibrationMedia,  // a calibration cartridge — no TOC exists; details surfaced instead
    Unidentified,      // no recognizable header and the user declined the end-of-data search
    Failed,            // a genuine failure preparing the media or reading a TOC that should exist
}
```

After `PrepareMedia` and `RefreshLoadedHeader`, the three-way gate:

- **Calibration header** → no TOC exists. Log a one-line summary, **never seek to EOD**, return
  `CalibrationMedia`.
- **No header** → could be a legacy backup (which *has* a TOC) or blank/foreign (which does not).
  `PresentVerdict(Unidentified, SearchForTOC, …)` asks at warning severity; Abort returns
  `Unidentified` with no tape movement, Proceed falls through to the read.
- **Media header** → identified backup media; the EOD TOC seek is justified work, not churn.

The legacy `RestoreTOCAsync` (unconditional TOC read, `Task<bool>`) is retained — it is the low-level
sub-step and many tests rightly depend on it.

### 9.5 Per-verb integration

**Backup.** `_agent.WritesMediaHeader = true` at the start of the run; the agent's
first-set-on-volume condition self-selects (append leaves it alone, overwrite and fresh volumes head).
A run-scoped local latch carries `ProceedAlways`:

- *Overwrite* — prompts for a wrong-kind cartridge, a **different** MediaId, or a TOC that still holds
  sets. Freshly-formatted or already-emptied own media overwrites silently, which is why
  format-then-backup never nags. On Proceed, `_toc.ResetMediaId()` sets `MediaId = Guid.Empty` so the
  idempotent `EnsureMediaId()` inside `CreateHeader` mints a **fresh series id** — avoiding collisions
  with surviving volumes of the overwritten series.
- *Append* — verifies series and volume against the loaded TOC.
- *Fresh continuation volume* — after reload, `RefreshLoadedHeader()` then a verdict: same series ⇒
  `WrongVolume` (an earlier volume of *this* series, almost certainly wrong); different series ⇒
  `MediaIdMismatch`; calibration ⇒ `WrongKind`; null ⇒ blank, proceed silently. `allowRetry: true` here.

**Restore.** Initial volume verified against the restored TOC; then a per-volume guard before each
`ResumeRestoreFromAnotherVolume` — `RefreshLoadedHeader()` and a verdict against `volumeNeeded`. A legacy
volume classifies `Unidentified` and proceeds silently, which is what makes mixed series work. Even under
`SkipVolumeCheck` the header is still **read** (presence must stay correct for navigation); suppression
silences only the prompt.

**Calibration.** A pre-run guard through the same channel, with retry so the user can swap in a proper
scratch cartridge without restarting:

```csharp
if (!request.SkipMediaHeaderCheck)
{
    while (_loadedHeader is TapeMediaHeader)
    {
        var choice = PresentVerdict(TapeMediaVerdict.WrongKind, MediaPromptContext.CalibrateScratch,
            suppress: false, allowRetry: true, allowProceedAlways: false);

        if (choice == MediaMismatchChoice.Abort)
            return MakeResult(aborted: true,
                message: "Calibration cancelled — cartridge holds a backup", mode: request.Mode);
        if (choice != MediaMismatchChoice.Retry)
            break;   // Proceed — erase and calibrate the loaded cartridge

        // Retry: eject → insert a scratch cartridge → reload → re-probe; the loop re-evaluates.
        …UnloadMedia / OnInsertMediaConfirm / ReloadMedia + PrepareMedia…
        AutoLoadCalibrations();
        RefreshLoadedHeader();
    }
}
```

This is the **only** pre-run guard that protects a New run, which reads no header of its own; the
post-run `ForeignHeader` reporting still explains a failed Resume/Recalibrate after the fact.

**TOC import.** Verified against the mounted medium; on Proceed the imported TOC **adopts `Volume` but
not `MediaId`**. Volume is *functional* — multi-volume restore positions by `TOC.Volume`, so it must
reflect the physically mounted volume. MediaId is *identity*: blind-adopting it would hide a wrong-tape
error inside a good TOC, so it is left as imported and a later save re-surfaces the genuine
inconsistency. Re-identifying is an explicit rename or format, never a silent side effect.

**Delete / rename / TOC round-trip.** `DeleteSetsFromCurrentSetUp` resolves presence and preserves the
header (§7.2). Rename and TOC save/restore are end-relative and header-agnostic — nothing needed.

### 9.6 Suppression flags

| Request | Flag | Effect |
|---|---|---|
| `BackupRequest` | `ForceVolumeOverwrite` | overwrite and continuation prompts → auto-Proceed |
| `RestoreRequest` | `SkipVolumeCheck` | per-volume identity prompt → auto-Proceed (header still re-read) |
| `CalibrateRequest` | `SkipMediaHeaderCheck` | pre-run backup-media guard → skipped |

All default **false** (interactive safety). Suppression silences prompts only; header reads and presence
resolution always run. `ProceedAlways` sets a **run-scoped local latch**, never mutating the request.

---

## 10. Applications

### 10.1 CLI (TapeConNET)

`VerbHost` gained an `IdentifyMedia` lifecycle step (and the `FullOrCalibration` combination) that calls
`RestoreTOCOrCalibrationAsync` and throws only on `Failed` — a calibration cartridge or unidentified
medium is a valid state the verb renders. `list` and `info --full` use it; `backup`, `restore`,
`validate`, and `verify` stay on the strict `RestoreTOC` step, because tolerating a null TOC there would
make the restore engine re-read the TOC and reintroduce the very churn being eliminated.

`ListContentsAsync` short-circuits on a calibration cartridge and prints the calibration report, so both
`list` and `info` present it without either command carrying calibration logic. The identify step logs a
one-liner; `LogCalibrationInfo` (profile key, run id, started, reported capacity, planned
samples/checkpoints, run block size) carries the detail — so nothing double-prints.

The host implements `OnMediaMismatchConfirm` as a `Select` prompt over the allowed choices, with the
non-interactive branch returning `Proceed` (and `Abort` for `CalibrateScratch`) and logging the
auto-decision.

### 10.2 WPF (TapeWinNET)

- **Media load** routes through `LoadTOCOrCalibrationWithUIAsync`, which dispatches on the outcome:
  `TocLoaded` → TOC tree; `CalibrationMedia` → a **Calibration Cartridge** tree node and its property
  pane; `Unidentified` → drive-only tree with a status note; `Failed` → the existing "load a `.tapetoc`?"
  recovery prompt. Because the outcome distinguishes these, that recovery dialog no longer fires for a
  calibration cartridge.
- **Calibration pane.** The upper Properties pane shows drive/media identity; the lower pane shows the
  header's own detail. An **Inspect Media** button runs a modal, lean probe
  (`ExecuteLoadCalibrationMediaInfoAsync` → `TapeCalibrator.InspectMedia()` under the operation lock)
  and enriches the pane with the checkpoint-derived run trail (resumable, complete, checkpointed bytes,
  progress). Modal rather than background: the app's model is *interactive UI* XOR *modal operation*, and
  a live scan would introduce a new concurrency regime for a convenience read.
- **Reread Media** (formerly "Reread TOC") resets and re-identifies, so it now re-scans TOC *or*
  calibration. Refresh routines handle the calibration node; the view-model holds **no** tape-derived
  calibration state — both the header and the inspect result come from the service, like `TOC`.
- **Virtual drive dialog.** `VirtualDriveProber` reads the BOM header before assuming a TOC, and reports
  a `VirtualMediaKind` (`Backup` / `Calibration` / `None`). A calibration `.vt` is now recognized as
  **existing** media — the dialog auto-selects *Open existing*, shows "⚙ Calibration cartridge:
  <profile>", and if the user overrides to *Create new* warns specifically that **calibration data will
  be permanently erased**. Previously such a cartridge probed as "new media" and could be overwritten.

### 10.3 Naming vocabulary

One verb per lifecycle stage, applied across service and view-model:

```
Stage                        Verb              Meaning
---------------------------  ----------------  ------------------------------------------------
Drive handle                 Open / Close      acquire / release the drive
Medium presence              Load / Eject      insert / remove the cartridge in the drive
Medium identity + content    Identify          read the BOM header, then dispatch:
                                                 load TOC  OR  report calibration
On-tape TOC read (sub-step)  Restore           recover the TOC from tape into memory
TOC <-> file                 Import / Export   .tapetoc round-trip
New medium                   Format            erase + write initial TOC / header
Calibration probe            Inspect           read the calibration checkpoint trail
Redisplay, no I/O            Refresh (view)    rebuild the selected pane from in-memory data
Reload content, with I/O     Reload            re-fetch the medium's content into the views
```

Two rules resolve the historical confusion: **"Identify" is the umbrella** and *contains* a "Restore TOC"
sub-step; and **"Refresh" ≠ "Reload"** — Refresh is pure in-memory redisplay, Reload does tape I/O.

---

## 11. Invariants

| | |
|---|---|
| **INV-1** | The header never increments `CurrentContentSet`. |
| **INV-2** | Presence and identity are established only by a framed-CRC probe, never by mark counting. |
| **INV-3** | The *agent* guarantees presence is resolved before content navigation; the navigator treats unresolved `Unknown` permissively as `Absent` so it stays usable standalone — only `Present` triggers the skip. |
| **INV-4** | Headers are written only from format / fresh-volume paths; never inserted into written media. |
| **INV-5** | The media header is one 16 KiB block **terminated by one filemark**; begin-of-content is reached by spacing over that mark, never by block arithmetic. |
| **INV-6** | Every header carries a `TapeHeaderKind` byte after the signature. |
| **INV-7** | Classification is positive-only: `Unknown` unless the framed CRC validates **and** the kind is known. |
| **INV-8** | Kinds are mutually exclusive per block. |
| **INV-9** | Presence resets to `Unknown` on every media (re)load. |
| **INV-10** | `MoveToHeader` / `WriteHeader` error on `Absent`. |
| **INV-11** | Any "at BOM ⇒ oldest set / assume blank" **content-side** handler routes through `MoveToBeginOfContentFromBom()` (8 sites, §5.5); TOC-side forward-scan rewinds stay raw. |
| **INV-12** | The navigator never reads or parses a header; only the agent does. |
| **INV-13** | `BlockSize` is a protected base slot; each kind exposes it under its own name (`TocBlockSize` / `RunBlockSize`). |
| **INV-14** | Header presence is resolved only at content choke-points; TOC navigation never resolves it. |
| **INV-15** | Header block operations reset the content position on failure and leave begin-of-content on success. |
| **INV-16** | The agent writes the media header at `CurrentSetIndex == FirstSetOnVolume` when `WritesMediaHeader`; heading is mechanism, the wrong-media verdict stays service/load-time. |
| **INV-17** | `VirtualTapeMedia.SeekToBlock` positions the backing stream via `CurrentPositionBytes()` for every landing (inside-data / mark / EOD), so a write after a seek never clobbers earlier data. |
| **INV-18** | On-tape size accounting includes the header block per volume. |
| **INV-19** | `AtHeader` states *where the head is*; `HeaderPresence` states *whether a header exists*. Neither may be inferred from the other — every `AtHeader` shortcut tests presence before spacing. |
| **INV-20** | The header's filemark is merged into the forward set count only when `UseSmks == false`; with setmark separators the two mark types cannot be combined. |
| **INV-21** | Every `VirtualTapeMedia` operation that changes the logical position also syncs the backing stream via `CurrentPositionBytes()` — logical position, cached virtual-block index and stream position are one state in three fields. |

---

## 12. Three fixes the header surfaced

### 12.1 `VirtualTapeMedia.SeekToBlock` at EOD

`SeekToBlock` positioned the backing stream only when the target landed **inside a data block**. Seeking
to **EOD** — e.g. block 1, just past a lone 16 KiB header — left the stream stale at 0, so the next
`WriteBlocks` overwrote the header. (`WriteBlocks`'s own `TruncateFromCurrentPosition` returns early at
EOD, so nothing self-corrected.) The fix positions the stream via the existing authority
`CurrentPositionBytes()`, which is correct for all three landings:

```csharp
try { m_stream.Position = CurrentPositionBytes(); }
catch (Exception ex) { SetError(ex); LogErrorAsDebug("Stream seek failed"); return false; }
```

Byte-identical to the old path for inside-data; two cases gained. This underpins the entire
write-header-then-append-content flow. Regression test:
`SeekToBlock_AtEod_PositionsStreamAtEnd_SoNextWriteAppends`.

### 12.2 On-tape size accounting

Each media header consumes 16 KiB of content capacity per volume.
`TapeTOC.ComputeTotalFileSizeOnTape` accounts for it (per volume, or × distinct-volume-count when
`onVolumeOnly: false`), so `TapeServiceBase.Used` and the early-warning reserve stay honest. Small
virtual multi-volume media feel this first.

### 12.3 VirtualTapeMedia position synchronization

Three defects in the virtual backend shared one shape — **logical position updated, physical position
forgotten**: `SeekToBlock` at EOD (§12.1), byte drift in `TruncateFromCurrentPosition`, and finally
`SpaceMarks` / `SpaceSequentialMarks`, which updated `m_currentBlock` and the cached virtual-block index
but never `m_stream.Position`. The last surfaced precisely because the header's trailing filemark made
*spacing* the normal route to begin-of-content, so a write after it landed at a stale offset.

The fix is structural rather than local: one private `SyncStreamPosition()`, derived from the already
authoritative `CurrentPositionBytes()`, called by **every** positioning operation — including `Rewind` and
`SeekToEnd`, which had been hand-rolling the same computation. A DEBUG `AssertPositionConsistent()` pairs
with the existing `AssertByteTotalConsistent()` to check both halves (cached index matches the logical
block; stream matches the logical position), so the next positioning operation cannot silently opt out.

Alongside it, three optimizations: hint-first `FindVirtualBlockIndex` (the cached index, then the next one
— O(1) on sequential traversal, binary search otherwise), an early-out in `SyncVirtualBlockIndex`, and an
O(1) `CalculateStreamLength` from the last data block, cross-checked in DEBUG against the full summation
(which also verifies that data blocks really are contiguous from offset 0).

**Test gap closed.** These bugs survived because `VirtualTapeMedia` had no dedicated suite — every test
reached it *through* a `TapeDrive`, so its own invariants were only exercised incidentally. The new
`VirtualTapeMediaTests` (~30 tests) asserts them directly, and every position test **reads back what
physically landed** rather than trusting the logical state, which is what catches a stale stream.

---

## 13. Validation

### 13.1 Test methodology — the header × profile matrix

An existing suite runs under multiple header configurations with near-zero per-method churn: move the
tests into an **abstract base** exposing the header axis as a property, and let **sealed subclasses** fix
it. xUnit discovers inherited `[Theory]`/`[Fact]` per concrete class, so every test runs once per mode;
one funnel method (`CreateFixture`) injects the axis, and static helpers taking a fixture are unchanged.

```csharp
public abstract class XxxBase
{
    protected abstract bool WithMediaHeader { get; }
    protected VirtualTapeFixture CreateFixture(/* mirrors ctor */)
        => new(/* … */, withMediaHeader: WithMediaHeader);
    // …tests verbatim, `new VirtualTapeFixture(` → `CreateFixture(`
}
public sealed class Xxx_Headerless : XxxBase { protected override bool WithMediaHeader => false; }
public sealed class Xxx_Headed     : XxxBase { protected override bool WithMediaHeader => true;  }
```

**Migration aid (the compiler as `#define`):** temporarily remove the fixture's `withMediaHeader` default
so every un-migrated call site becomes a compile error — the build enumerates them exhaustively.

Applied to the navigator suite and all agent suites (backup, restore, packed, pipelined), plus
multi-volume × `{None, All, Mixed}`. Header-unsuitable tests (those that raw-fill the tape and build
their own TOC) stay in the base guarded by `Skip.If(WithMediaHeader, …)`. Assertions of an absolute block
0 key off `fixture.FirstContentBlock` (1 headed / 0 headerless) rather than a literal.

> Assertions of an absolute block 0 key off `fixture.FirstContentBlock`" → note it is now
> **2 headed / 0 headerless** (one block + one filemark, since the virtual backend numbers marks), and
> that it is derived from a `HeaderBlocks` constant rather than a literal so the on-tape shape can change
> without touching assertions.

### 13.2 Fixtures simulate the service

Rather than pre-feeding headed media, the fixtures **have the agent write the header**, proving the real
path: `VirtualTapeFixture.BackupFiles` sets `agent.WritesMediaHeader`, and
`MultiVolumeVirtualTapeFixture` heads **per volume** via `ShouldHeadVolume(n)`, flipping the flag before
each `ResumeBackupToNextVolume`. Restore needs **no** explicit header logic — `EnsureHeaderResolved()` at
the restore content choke-point resolves presence per volume automatically, confirmed empirically by
headed backup tests passing with zero restore-side header code.

### 13.3 `_MixHeaded` — the crown test

`VolumeHeaderMode.Mixed` (legacy volume 1, headed volume 2+) is the **only end-to-end validation of
per-volume presence re-resolution**: during restore the last (headed) volume loads first → `Present` →
content at block 1; swapping to the header-less volume 1 → `RenewNavigator` (presence → `Unknown`) →
re-read → `Absent` → content at block 0. That `Present → Absent` transition is exactly what per-volume
re-resolution exists for, and pre-heading every volume could never surface it.

It is *correct* because the TOC records each file's **physical-per-volume** address, captured on that
volume's own layout — so `MoveToBlock(addr)` lands right on each volume regardless of whether *this*
volume is headed. Mixed series work because addresses are physical-per-volume and presence is resolved
per volume.

### 13.4 Service-level prompt assertions

Service tests assert prompts exhaustively rather than waiving them. `ServiceTestBase` tracks every
`TestTapeServiceHost` it creates; `AssertMediaPrompts(host, params (verdict, context)[])` drains the
recorded prompts and requires an **exact, ordered match**, marking the host checked. At teardown, every
**un-checked** host must have recorded **zero** prompts.

The result: a prompting test proves *what* prompted (a wrong context or a spurious second prompt fails
the test), and every other test proves *nothing* prompted — so the happy paths are provably silent, not
merely quiet.

### 13.5 Coverage

- **Unit** — media header all-fields round-trip; placement matrix; null/empty name → `DisplayName`
  fallback; `ClampName` budget fit; polymorphic vs. narrow `Unpack`; legacy `TapeFileInfo` bytes → null
  (no false-classify); blank → null; `CreateHeader` mints/shares MediaId idempotently; `ToString` per kind.
- **Agent** (all four drive profiles) — write→read returns the media header with matching MediaId and
  `Present`; blank → `Absent`; headed backup → restore byte-for-byte; clobber regression (header survives
  content + TOC); TOC reload then restore preserves MediaId; multi-set headed restore; header-less backup
  → `ReadHeader` null.
- **Service** — format heads the media (both placements); overwrite prompts, proceeds, and **mints a
  fresh MediaId**; overwrite-abort preserves the medium; `ForceVolumeOverwrite` suppresses; append on
  matching media is silent; a calibration cartridge met by backup-overwrite raises `WrongKind`; the
  calibration guard aborts when declined and is skipped under `SkipMediaHeaderCheck`; legacy header-less
  media restores with no prompt; and the load gate returns `TocLoaded` / `CalibrationMedia` /
  `Unidentified` / `Failed` for each medium kind, including declining the EOD search without touching the
  tape.
- **Virtual backend** — `SeekToBlock` at EOD appends without clobbering (§12.1); strict-write-position
  tests (below).
- **Navigator** — forward navigation from `AtHeader` / `UnknownSet` / `InTOCSet` lands on the requested
  set, verified by the set's own data (the ±1 guard for the merged
  filemark count), across all four profiles × both header modes; plus the `UseSmks = false` case on a
  setmark-capable drive."* and *"**Virtual media** — `VirtualTapeMediaTests`: position consistency after
  every positioning op, block numbering, truncation and byte accounting, capacity enforcement, strict
  write positioning, state round-trip.

### 13.6 Real-hardware validation

The virtual backend models the tape rule that a write is accepted only at BOP, at EOD, or immediately
after a mark (`VirtualTapeMedia.ResumeWriteFromMarkOnly`). Since the header's begin-of-content write
lands at block 1 — which is **mid-data** whenever a transient TOC or an existing set follows the header —
that write had to be validated, not assumed.

Deterministic tests pin the behaviour under emulated strictness (mid-data rejected, EOD accepted,
post-mark accepted). On hardware, two conformance probes isolate the primitive: **S10** overwrites from a
mid-data logical block (the header's actual case) and **S11** overwrites from just after a filemark.
**The AIT-2 accepts both**, confirming the design on a strict-family helical drive; LTO is permissive by
construction. The dropped trailing filemark is therefore validated on hardware at both ends of the
spectrum. Case A (header-only tape, so block 1 *is* EOD) is additionally covered by the existing physical
scenarios' first backup.

The physical fixture gained a per-format `forceNoPartition` override (with `EffectiveUsesPartition`) so a
single test can exercise the TOC-in-set / case-B path on a partition-capable drive without changing the
session's mode.

> S10 (mid-data) is now (v12) *background evidence*; **S11 (post-mark) matches production**.

---

## 14. Legacy calibration compatibility

Behind `#if LEGACY_TapeCalibrationRunHeader`, the pre-unification `TapeCalibrationRunHeader` (original
wire format, no kind byte) plus a one-line `ToHeader()` adapter let already-measured scratch cartridges
still resume:

```csharp
recordBuffer = new byte[blockSize];
int read = Drive.ReadDirect(recordBuffer, 0, recordBuffer.Length, out _, out _);
TapeCalibrationHeader? header = read > 0
    ? TapeCalibrationFramer.Unpack<TapeCalibrationHeader>(recordBuffer, read) : null;
#if LEGACY_TapeCalibrationRunHeader
// Re-parse the SAME already-read block — a second ReadRecord would ReadDirect the NEXT block.
if (header is null && read > 0)
    header = TapeCalibrationFramer.Unpack<TapeCalibrationRunHeader>(recordBuffer, read)?.ToHeader();
#endif
```

**Caveat:** a legacy block's CRC still validates under the new reader, so classification rests on the
byte the new format treats as `Kind` (the 2nd RunId byte, effectively random): ~98% are not 1/2/3 ⇒
`Unknown` ⇒ fallback; the rest almost always throw in `DeserializeString` ⇒ caught ⇒ fallback. The
sub-1% residual false-parse is acceptable for a temporary development flag. Bumping
`TapeSerializer.Version` would make the separation exact; the checkpoint format is unchanged either way.

---

## 15. Known risks and watch-items

The feature is complete and green. The paramount risk of earlier revisions — the missing trailing
filemark — is **resolved** (§15.1, retained for its rationale). Three properties remain worth keeping in
view; none is currently observed as a defect, and each is recorded with the shape of its fix.

### 15.1 ~~No trailing filemark~~ — RESOLVED in v12

**Resolved.** The header now carries a trailing filemark (§5.1), so begin-of-content is a **post-mark**
write on every drive family and the showstopper risk is retired. The reasoning and evidence are kept here
because they justify a mark that otherwise looks like pure overhead.

**The hazard.** Without the mark, a content-start at begin-of-content is a **mid-data** write whenever
anything follows the header (a transient initial TOC, or an existing set). Tape drives classically accept
writes only at BOP, at EOD, or immediately after a mark; a strict family would have rejected it outright.

**The evidence.** The virtual backend models the rule via `VirtualTapeMedia.ResumeWriteFromMarkOnly`, and
deterministic tests pin all three cases (mid-data rejected, EOD accepted, post-mark accepted). On hardware
two conformance probes isolate the primitive: **S10** overwrites from a mid-data logical block, **S11**
from just after a filemark. **AIT-2 and DLT-V4 accept both**, and LTO is permissive by construction — so
the mid-data shape was *not* observed failing anywhere. The mark was reinstated anyway: the untested drive
families are the ones that would fail, and the failure mode is catastrophic rather than degraded.

**Why no compatibility flag was needed.** The feature had not shipped, so the only header-without-mark
media in existence was development test cartridges, which were simply reformatted. A `HeaderTrailingMark`
field would have added a permanent format concession to guard a transient condition. Had both shapes
needed to coexist, the cheaper route would have been **auto-detect** (peek one block after the header: a
tapemark ⇒ new shape and already positioned; data ⇒ old shape, reposition) rather than a serialized flag —
one extra read, self-correcting on both shapes, no wire-format change.

**Bonus.** Spacing over a mark is *more* robust than the `MoveToBlock(1)` it replaced, since it assumes
nothing about whether a drive numbers marks in its logical block space — and it enabled the forward-count
merge of §5.7.

### 15.2 Legacy calibration cartridges classify heuristically

**What.** Behind `#if LEGACY_TapeCalibrationRunHeader` (§14), a pre-unification block's CRC still validates
under the new reader, so classification falls to the byte the new format treats as `Kind` — effectively
random in the legacy layout.

**Exposure.** ~98% of legacy blocks yield a value that is not 1/2/3 ⇒ `Unknown` ⇒ the fallback parser runs;
most of the remainder throw inside `DeserializeString` and are caught into the same fallback. The residual
false-parse is sub-1%, and the flag is a temporary development affordance.

**Resolution path.** Bump `TapeSerializer.Version` so `ValidateSignature` separates old from new cleanly,
making classification exact rather than probabilistic. The checkpoint format is unaffected either way. The
alternative is simply to retire the flag once no legacy scratch cartridges remain in use.

### 15.3 A standard header block assumes the drive can carry 16 KiB

**What.** Both header kinds now occupy one `TapeHeaderBlock.Size` (16 KiB) block. A drive whose *maximum*
block is smaller cannot hold one.

**Exposure.** No physical drive in scope is affected (the agent already requires 16 KiB for its TOC blocks,
so any backup-capable drive qualifies). It can only arise for calibration on a deliberately tiny virtual
medium. `TapeHeaderBlock.IsSupportedBy` guards it: the calibrator falls back to the legacy run-block shape
and logs that the cartridge is not cross-classifiable.

**Resolution path.** If such a drive ever matters, the header would need a size-negotiated form — read the
preamble from the largest block the drive supports and treat the fixed 16 KiB as a maximum rather than an
exact size. Not worth doing speculatively.

### 15.4 Capacity evaluation side-effect due to the header block

A side effect of the standard header block: the payload no longer divides the capacity evenly, so
`PhantomFreeAtEom` is measured to a granularity of one run block (the trailing partial block is genuinely
unwritable), and under a BOM over-report that stub is reported inflated by the same factor. Negligible
against real media capacity; visible only on small virtual cartridges, where tests allow one boosted
block of slack.

---

## 16. Outlook — the set header

The natural next feature, deferred deliberately: a **`TapeSetHeader`** — one 16 KiB framed block at the
front of each set's data, carrying `VolumeSetIndex` (0-based, drives navigation verification),
`GlobalSetIndex` (1-based, TOC attribution), `Volume`, and `MediaId`.

Its value is **verified set navigation**: on read, the agent compares `VolumeSetIndex` against the
navigator's target, self-correcting within bounds and otherwise escalating the `MediaInconsistent`
verdict that already exists in `TapeMediaVerdict`. Today set positioning is trusted; with set headers it
becomes checked.

> The set header inherits the same rule: its block is followed by content with no mark,
> so if verified set navigation ever writes from a set boundary, the §15.1 reasoning applies again.

The design fits the existing foundation without disturbing it:

- **Counting stays untouched.** The set header is the first block of its set's data, so setmark and
  filemark arithmetic is unchanged — the same property that makes the media header safe.
- **Written before the packer anchors**, so file `TapeAddress`es sit past it and no TOC-address surgery
  is needed.
- **Factoried by the TOC**, as `CreateSetHeader(int)` / `CreateSetHeaderForCurrentSet()`, mirroring
  `CreateHeader`.
- **Wired into `ConstructFrom`** by adding the `TapeHeaderKind.Set` arm — the polymorphic probe and the
  framing need nothing else.
- **Paired per volume:** media-header-present ⟺ set-headers-present, so a volume is uniformly headed or
  uniformly legacy.
- **Size accounting** adds one 16 KiB block per set in `TapeSetTOC.ComputeTotalFileSizeOnTape`, on both
  the packed and aligned paths.
- **Tests** extend the §13.1 seam with one more axis (`header × set-header`), the same compiler-driven
  migration.

Other candidates, smaller in scope: surfacing the calibration **run trail** in the CLI `list` behind a
`--calibration` flag (the WPF pane already offers it via Inspect Media); flagging remote virtual volumes
by media kind in the server-side volume list; and a dedicated host verb for the calibration
scratch-cartridge insert prompt, which today borrows the restore-insert wording.
