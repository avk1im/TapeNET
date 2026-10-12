# Design — Tape Volume & Set Headers for TapeLibNET

**Status:** v10 for implementation · **Author:** (TapeNET / TapeLibNET) · **Depends on:** MediaId (Guid) in TapeTOC (shipped, tests green)

> Major change in v10 vs. v8b: expanded §10 on service-layer implementation.
> Phase A = media header; Phase B = set header; one branch, shipped together.

---

## 0. Naming & files

| Symbol | Kind | File | Notes |
|--------|------|------|-------|
| `TapeHeader` | **abstract base** | `TapeHeader.cs` | `protected Guid Id`; `protected uint BlockSize`; `Kind`, `CreatedUtc`; polymorphic `ConstructFrom`; abstract `ToString()`. |
| `TapeMediaHeader` | `: TapeHeader` | `TapeMediaHeader.cs` | Kind = Media; `MediaId => Id`; **`TocBlockSize`** (= BlockSize); `Volume`, `TocPlacement`, `OriginalName`, `DisplayName`. |
| `TapeSetHeader` | `: TapeHeader` | `TapeSetHeader.cs` | Kind = Set; `MediaId => Id`; `VolumeSetIndex` (0-based), `GlobalSetIndex` (1-based), `Volume`. (Phase B) |
| `TapeCalibrationHeader` | `: TapeHeader` | (calibration file) | Kind = Calibration; `RunId => Id`; **`RunBlockSize`** (= BlockSize); `ProfileKey`, `CapacityReportedAtBom`, `Plan`; built via `CreateHeader()`. |
| `TapeHeaderKind` | enum | with base | `Unknown=0, Media=1, Calibration=2, Set=3`. |
| `TapeFramer` | static framing | `TapeFramer.cs` | `Pack`/`Unpack<T>`; `Unpack<TapeHeader>` is the polymorphic probe. |
| `TapeCalibrationFramer` | static forwarder | (calibration file) | thin wrapper over `TapeFramer`. |
| `TapeCalibrationRunHeader` | **legacy**, `#if LEGACY_TapeCalibrationRunHeader` | (calibration file) | pre-unification record + `ToHeader()` adapter; read-only fallback. |

---

## 1. Purpose

Single-partition ("TOC-in-set") media stores the TOC at end-of-data, so loading a cartridge forces
a seek to EOD just to learn whether a TOC exists. A **media header at BOM of the content partition**
positively identifies the cartridge as "ours" in one cheap block read.

The header's value has since grown beyond dodging that seek into **uniform, one-block identity &
verification** — which is placement-independent and therefore now written on **every** medium,
partitioned or not (see §4 for why the old exemption was retired). A **set header** in front of each
backup set additionally lets the agent *verify* set navigation against the tape.

`TapeCalibrator` writes a sibling BOM record. All records unify behind `TapeHeader`, so one framed
read classifies **media**, **set**, **calibration**, or **foreign/blank** (§9, §16).

---

## 2. Goals / non-goals

**Goals:** cheap positive "ours?" + identity on every load; verified set navigation; absolute
preservation of the *counting* logic in the navigator; per-tape identity tied to TOC `MediaId`;
graceful legacy & mixed-series coexistence; one framing classifies all kinds; each subsystem
recognizes the other's media precisely.

**Non-goals:** no retrofitting onto written legacy media; no TOC-format change beyond `MediaId`; no
unification of the headers' *physical write paths*; **no change to setmark/filemark counting**.

---

## 3. Core decisions

| # | Decision |
|---|----------|
| D1 | Media header: filemark-free record at BOM of the **content partition**, outside set-counting. |
| D2 | **No trailing filemark** on any header. |
| D3 | Presence/identity by **framed-CRC probe**, never mark-counting. |
| D4 | **Fixed media/set header block = 16 KiB** (calibration header uses the run block). |
| D5 | **CRC external** to the header struct — carried by `TapeFramer`. |
| D6 | **`TapeHeaderKind` byte** after the signature discriminates all kinds. |
| **D7** | **Media header on ALL formatted media**, single- and multi-partition, at content-partition BOM. `TapeTocPlacement` records where the TOC lives. *(Supersedes v6's "no header when `HasInitiatorPartition`".)* |
| D8 | **Never insert** headers into already-written media. |
| D9 | Headers written at format and on a **fresh (blank) volume**, from BOM / set start. |
| D10 | Multi-volume: per-tape, self-describing; series tied by TOC `MediaId` + header `Volume`. |
| D11 | `TapeMediaHeader`/`TapeSetHeader` factoried by **TapeTOC**; `TapeCalibrationHeader` by its own `CreateHeader()`. |
| D12 | Calibration unified; **no `ITapeHeaderMaker`**. |
| D13 | Header I/O via **dedicated `WritingHeader`/`ReadingHeader` states**. |
| **D14** | Presence: **`TapeHeaderPresence { Unknown, Present, Absent }`** — `NotNeeded` retired (every navigator can carry a content-BOM header now). |
| D15 | Position sentinel **`AtHeader = InTOCSet + 1`**. |
| D16 | **Navigator never parses headers.** Agent parses/reads; Navigator counts + holds presence; Service evaluates. |
| D17 | **Set header does NOT alter setmark counting.** |
| D18 | **media-header-present ⟺ set-headers-present** per volume. |
| **D19** | **`BlockSize` is a `protected` base slot** each kind reinterprets: media ⇒ `TocBlockSize`; calibration ⇒ `RunBlockSize`. |
| **D20** | **Polymorphic classification** is `TapeFramer.Unpack<TapeHeader>`; the agent surfaces a polymorphic `TapeHeader?`. Wrong-kind is a first-class verdict. |
| **D21** | **The agent writes the media header**, not the service/fixture. `TapeFileBackupAgent.BeginWriteContentForCurrentSet` writes it when `CurrentSetIndex == FirstSetOnVolume && WritesMediaHeader`, else calls `EnsureHeaderResolved()`. Pure mechanism — the service still owns the *load-time verdict*; the agent just executes the already-authorized "write from BOM = take over this tape". |
| **D22** | Heading is gated by a mutable **`TapeFileBackupAgent.WritesMediaHeader`** property (default `false`). The service/fixture flips it; for Mixed multi-volume it varies per volume, set before each `ResumeBackupToNextVolume`. |
| **D23** | Both test fixtures default to **headerless** (`WithMediaHeader = false` / `VolumeHeaderMode.None`) now that agent-driven heading + the base/derived matrix (§18) give explicit, opt-in header coverage. |

---

## 4. Why the header now goes on partitioned media too (retires the D7 exemption)

In v5/v6 the header had one job — avoid the minutes-long EOD seek — which partitioned media never
suffered, so it was exempt. Two later additions changed the economics:

- **Uniform identity/verification.** The header hands back `MediaId` + `Volume` + `Kind` from ONE
  16 KiB block, versus parsing a whole TOC. That value is placement-independent.
- **Three flows now depend on a cheap per-volume identity:** overwrite validation (whose tape is
  this before I destroy it?), multi-volume restore (we deliberately DON'T re-read the TOC per
  volume — without a header a partitioned continuation volume has no cheap identity at all), and the
  `SkipVolumeCheck` opt-out (only coherent if a *uniform* check exists to skip).

**Placement — content-partition BOM, not the initiator partition:** restore then pays **zero**
partition switches (identity → navigate content, same partition), set headers already live in the
content partition (one skip rule covers both), and the initiator-TOC path — already fast and already
carrying `MediaId` — is left untouched. No cross-partition clobber exists, because on partitioned
media the content-BOM header and the initiator-partition TOC occupy *different* partitions.

Consequence: **`NotNeeded` is retired** (D14); every navigator starts `Unknown` and the agent
resolves; the partition navigator stops being a special case (§6.7).

---

## 5. Media header — `TapeMediaHeader`

Immutable identity projection of the TOC. Written once, never rewritten.

### 5.1 Contents (framed into one 16 KiB block; CRC external)

| Field | Where | Source |
|-------|-------|--------|
| Signature / **Kind** (Media) / **Id** (`MediaId`) / CreatedUtc / **BlockSize** | preamble | TOC |
| **Volume** | body | TOC `Volume` — immutable per tape |
| TocPlacement | body | `InSet` or `InPartition` (self-describes the layout) |
| OriginalName | body | TOC `Description` snapshot, clamped to fit the frame; null ⇒ generated |

**BlockSize is repurposed (D19).** It is *not* the header's own block (that's the caller's fixed
16 KiB and is known before the field is read). It now records the **TOC's** on-tape block size,
exposed as `TocBlockSize` — meaningful, immutable, and forward-looking (adopt larger TOC blocks
later without probing). `protected` in the base; media reinterprets it.

### 5.2 Factory

```csharp
// TapeTOC — sole builder of the media header. tocBlockSize is the agent's fixed TOC block.
public TapeMediaHeader CreateHeader(uint tocBlockSize, TapeTocPlacement placement) =>
    new()
    {
        MediaId      = EnsureMediaId(),
        CreatedUtc   = CreationTime,
        TocBlockSize = tocBlockSize,                 // repurposed BlockSize slot
        Volume       = Volume,
        TocPlacement = placement,                    // InSet | InPartition — from HasInitiatorPartition
        OriginalName = TapeMediaHeader.ClampName(Description),
    };
```

`DisplayName` falls back to `Media {Id:N} · vol {Volume} · {CreatedUtc}` when `OriginalName` is null.
The current, renameable name is never stored here (rename rewrites the TOC); UIs show the TOC's live
description.

---

## 6. Navigation model (media header)

### 6.1 Layout (`‹MH›` = media header block, no FM; `[SHk]` = set-k header)

```
Single-partition:  ‹MH›[SH0][set0][SM][SH1][set1][SM]…[SHn][setN][SM][toc1][FM][toc2][FM]
Partitioned:       content: ‹MH›[SH0][set0][SM]…[SHn][setN][SM]   |   initiator: [toc1][FM][toc2][FM]
```

Header = one logical block at content-partition BOM (single `WriteDirect`); content begins at
logical block 1. Each set header is the first block of its set's data, so **setmark counting is
unchanged** (D17).

### 6.2 Sentinels & presence

`AtHeader = InTOCSet + 1`. `TapeHeaderPresence { Unknown, Present, Absent }` (**no `NotNeeded`**);
field `HeaderPresence`. **Every** navigator starts `Unknown`; the agent resolves.

### 6.3 `NavigateToHeader()`

`Unknown`/`Present` → position at content-partition BOM, `CurrentContentSet = AtHeader`. `Absent` →
**error** (seeking a header known absent is a bug).

### 6.4 Reading resolves position (no FM to skip)

`ReadHeader` (agent) = `NavigateToHeader` → `ReadDirect(16 KiB)` → `TapeFramer.Unpack<TapeHeader>`
(§9). Media header ⇒ `Present`, `CurrentContentSet = 0`. Other kind / garbage ⇒ `Absent` for backup
purposes (service still learns the kind), `Unknown` position. I/O failure ⇒ `Unknown`, `Unknown`.

### 6.5 The one functional change: header-aware begin-of-content

`MoveToBeginOfContent`: `Absent` → position at content BOM → 0; `Present` → content BOM → skip one
header block → 0; `Unknown` → **error** (agent must have resolved; §4-layering). Skip = go to
logical block 1.

### 6.6 Guard fix in `MoveToTargetContentSet`

Add `AtHeader` to the from-end guard so a negative target while `AtHeader` first moves to a known
boundary. No new overloads; the positive branch already routes `AtHeader` through begin-of-content.

### 6.7 REQUIRED: header-aware "assume-blank" fallbacks (INV-12)

Every raw `Drive.Rewind()` "assume blank" fallback lands at BOM before the header and must route
through `MoveToBeginOfContentFromBom()` (rewind → skip the header block when `Present`). Sites: the
no-mark else-branches of the TOC-in-set navigators (§v5 list). **v7 addition:**
`TapeNavigatorTOCInPartition.MoveToBeginOfContent` now switches to the content partition, rewinds,
and skips the header when `Present` — no longer exempt. Its initiator-partition TOC positioning is
untouched; no cross-partition clobber.

---

## 7. Set header — `TapeSetHeader` (Phase B)

Unchanged from v5/v6: one 16 KiB framed block at the front of each set's data, carrying
`VolumeSetIndex` (0-based, drives navigation verification), `GlobalSetIndex` (1-based, TOC/attribution),
`Volume`, and `MediaId`. The agent writes it before the packer anchors (so file `TapeAddress`es sit
past it — no TOC-address surgery) and, on read, verifies `VolumeSetIndex` against the navigator's
target, self-correcting within bounds, else escalating a `MediaInconsistent` verdict. Navigator
counting untouched (D17). `TapeTOC.CreateSetHeader(int)` / `CreateSetHeaderForCurrentSet()`.

---

## 8. TOC-in-partition · legacy · multi-volume

- **Partitioned media (revised):** media header at content-partition BOM (§4). TOC still in the
  initiator partition; its positioning unchanged. `TocPlacement = InPartition` recorded.
- **Legacy media:** reading → `Absent` → ask once about the EOD seek (single-partition) / just load
  the initiator TOC (partitioned). Never add headers to written media. media-header ⟺ set-headers per
  volume.
- **Multi-volume:** header per tape; series = `MediaId`; position = header `Volume`. Guard on each
  inserted volume compares `Id`/`Volume`; proceed-on-confirm; suppressible via `SkipVolumeCheck` (§10).

---

## 9. Classification & reporting (v7)

### 9.1 The polymorphic probe (answers "which kind?")

`TapeFramer.Unpack<TapeHeader>(block, len)` **is** the class-flexible reader: `TapeHeader.ConstructFrom`
dispatches on the Kind byte and returns the concrete type, so:

```csharp
switch (TapeFramer.Unpack<TapeHeader>(block, len))
{
    case TapeMediaHeader m:       /* backup media   — m.MediaId, m.Volume */          break;
    case TapeSetHeader sh:        /* set header      — sh.VolumeSetIndex */            break;
    case TapeCalibrationHeader c: /* calibration     — c.RunId */                      break;
    case null:                    /* foreign / blank / torn */                        break;
}
```

- **Narrow** ("is it MY kind?") — `Unpack<TapeMediaHeader>` returns the media header or `null`
  (wrong kind *or* foreign), because the inherited `ConstructFrom` dispatches then `as T` narrows.
- **Polymorphic** ("what is it?") — `Unpack<TapeHeader>` returns the concrete kind or `null` only for
  blank/foreign. No new framer is needed — the fixed-`T` `Unpack` already provides both modes.

### 9.2 Agent surface & service verdict (answers "report it precisely")

The agent's `ReadHeader()` returns the **polymorphic `TapeHeader?`**. The **service** maps
`(concrete kind vs. expected)` to a verdict and uses `header?.ToString()` for the human-readable
"namely …" detail (each kind's `ToString()` supplies it; `null` ⇒ "Unidentified media").

```csharp
public enum TapeMediaVerdict
{
    Match,             // right kind, right MediaId (+Volume when checked)
    Unidentified,      // null — blank/foreign/legacy
    WrongKind,         // e.g. a calibration cartridge met in a backup context (or vice-versa)
    MediaIdMismatch,   // media header, different series
    WrongVolume,       // media header, right series, wrong Volume
    MediaInconsistent, // set-header verification failed unrecoverably (Phase B)
}
```

Symmetric for calibration: `TapeCalibrator` reads the same polymorphic header, so
`InspectMedia`/inspect can report "backup cartridge — namely {header}" (Kind = Media) instead of a
blank "no calibration header". Concretely, the calibrator's inspect result gains a field for the
foreign header's `ToString()`, populated when `Unpack<TapeHeader>` yields a non-calibration kind.

### 9.3 Cross-detection read size (note for §16.6, not needed now)

A subsystem classifying with a block **smaller** than 16 KiB (e.g. calibration on tiny virtual media)
must size the read to hold a media/set header, or `Unpack`'s length-guard drops it:

```csharp
int probeLen = (int)Math.Max(runBlockSize, TapeHeader.FixedHeaderBlockSize);
```

Self-reading is already safe (write guard in `RecordBlockWriter.Emit`, read guard in `Unpack`); this
guard is required only when cross-classification is implemented. On fixed-block backends note that
`ReadDirect` reads in whole drive blocks — reaching 16 KiB may span several — verify against the
backend at implementation time.

### 9.4 Calibrator-side classification (symmetric to the agent)

`TapeCalibrator` is dual-role like the agent (mechanism that also reports up to
`TapeServiceBase.Calibrate.cs`), so it classifies with the **same polymorphic probe** and remembers
a stranger for the service to name.

- **Mechanism.** `ReadRunHeader` reads once, unpacks polymorphically, narrows to its own kind, and
  captures anything foreign:

  ```csharp
  TapeHeader? any = read > 0 ? TapeCalibrationFramer.Unpack<TapeHeader>(recordBuffer, read) : null;
  // (LEGACY fallback re-parses the SAME buffer as TapeCalibrationRunHeader → ToHeader())
  if (any is TapeCalibrationHeader header) return header;                            // our kind
  if (any is not null) { ForeignHeader = any; /* set error, trace */ return null; }  // Media/Set
  /* else: blank / foreign / torn → null */
  ```

---

## 10 — Service-layer header handling (FINAL)

> Supersedes the v8a §10 sketch, the §14.8/§14.9 scraps, and the intermediate UPDATED draft.
> Reflects v8b (D21/§17.10: **`TapeFileAgent` writes the media header automatically**), so the
> service's job is **check → decide → prompt** — it never writes headers itself, only sets the gate.
> All review points (turns 39–40) are folded in. Verbs covered: format, load, backup, restore,
> calibrate, delete, import-TOC.

### 10.0 Principle: agent writes, service evaluates (D16 realized)

- **Agent writes headers** (mechanism), at the two BOM moments:
  - **format / fresh media** → in `BackupInitialTOC(writeHeader: true)` (§10.1),
  - **fresh continuation volume** → in `BeginWriteContentForCurrentSet` at first-set-on-volume (§17.10).
- **Service evaluates** the loaded header for *identity* (whose tape / which volume / which kind) and
  drives all prompts. It holds **one stored value**, `_loadedHeader`, read at media load and
  interpreted per operation — the verdict depends on the operation.

### 10.1 Format — agent auto-heads via `BackupInitialTOC(bool writeHeader)`

`BackupInitialTOC` builds the initial TOC anyway, so it builds the header from that same TOC and
writes it first — no user interaction. **A parameter (not a mutable flag) selects heading**, removing
the reentrancy footgun and expressing intent at the two call sites:

```csharp
// TapeFileAgent (base) — WritesMediaHeader lives here (Guid1 [DONE]); the base agent is used at format.
public bool WritesMediaHeader { get; set; } = true;

/// <param name="writeHeader">
/// Write the media header before the initial TOC. TRUE only on the format / fresh-media path;
///  FALSE on the delete-all path (which must preserve, never rewrite, the existing header — §10.8).
///  Defaults to <see cref="WritesMediaHeader"/> so continuation heading (which sets the flag) works.
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
```

`WriteHeader()` derives everything internally — placement from the navigator, `Partition = Content`:

```csharp
public TapeResult WriteHeader()
{
    var placement = TOCPlacement;   // agent property: Navigator is TapeNavigatorTOCInPartition ? InPartition : InSet  [DONE]
    var header = TOC.CreateHeader(c_fixedTOCBlockSize, placement);   // placement REQUIRED (TOC can't know where it resides)  [DONE]
    // …frame + Manager.WriteHeaderBlock…  (§7)
}
```

Service format path (works for **all** media, incl. partitioned):

```csharp
_agent = new TapeFileAgent(_drive, new TapeTOC(description));
var initResult = _agent.BackupInitialTOC(writeHeader: true);   // header (from TOC) + initial TOC
```

### 10.2 Loaded-header state + `RefreshLoadedHeader`

```csharp
protected TapeHeader? _loadedHeader;
public TapeHeader?      LoadedHeader      => _loadedHeader;
public TapeMediaHeader? LoadedMediaHeader => _loadedHeader as TapeMediaHeader;

/// <summary>
/// Reads and classifies the loaded media's BOM header into <see cref="_loadedHeader"/> (one cheap BOM
///  block). Non-throwing; any failure leaves it null. Uses the LIVE <see cref="_agent"/> when present so
///  the agent that OWNS the navigator is the one moving the tape — keeping its presence + position
///  coherent (§17.7); else a throwaway probe agent.
/// </summary>
/// <remarks>
/// PRECONDITION: this MOVES the tape (rewinds to BOM). Call only at load / reload / between-volumes —
///  NEVER mid-stream. Per-volume restore: <c>ResumeRestoreFromAnotherVolume</c> calls
///  <c>RenewNavigator</c> afterward, which re-resolves presence via its own <c>EnsureHeaderResolved</c>
///  (a harmless double read); <see cref="_loadedHeader"/> — what the service needs — survives.
/// </remarks>
protected void RefreshLoadedHeader()
{
    _loadedHeader = null;
    if (_drive is null || !_drive.IsMediaLoaded) return;

    try
    {
        if (!_drive.PrepareMedia()) return;

        if (_agent is not null)
            _loadedHeader = _agent.ReadHeader();
        else
        {
            using var probe = new TapeFileAgent(_drive, _toc ?? new TapeTOC());
            _loadedHeader = probe.ReadHeader();
        }

        if (_loadedHeader is not null)
            LogInfoSub($"Media identity: {_loadedHeader}");   // trace-level surfacing only
    }
    catch { _loadedHeader = null; }
}
```

**Call sites:** end of `LoadMediaAsync`; after the reload inside `FormatMediaAsync`; per inserted volume
in the backup/restore continuation loops. **Eager at load** (§10.10-3): the UI learns identity
immediately, one cheap BOM read.

### 10.3 The verdict — `EvaluateLoadedHeader`

```csharp
public enum TapeMediaVerdict
{
    Match, Unidentified, WrongKind, MediaIdMismatch, WrongVolume, MediaInconsistent
}

protected TapeMediaVerdict EvaluateLoadedHeader(Guid? expectedSeriesId = null, int? expectedVolume = null)
    => _loadedHeader switch
    {
        null                  => TapeMediaVerdict.Unidentified,
        TapeCalibrationHeader => TapeMediaVerdict.WrongKind,
        TapeSetHeader         => TapeMediaVerdict.WrongKind,          // defensive: a set header at BOM is wrong
        TapeMediaHeader m when expectedSeriesId is { } s && m.MediaId != s => TapeMediaVerdict.MediaIdMismatch,
        TapeMediaHeader m when expectedVolume  is { } v && m.Volume  != v => TapeMediaVerdict.WrongVolume,
        _                     => TapeMediaVerdict.Match,
    };
```

**Golden rule — `Unidentified` never prompts.** Blank/legacy/foreign can't be verified and a legacy
volume is legitimate; restore proceeds, overwrite treats it as safe. Only the three positive mismatches
(`WrongKind`, `MediaIdMismatch`, `WrongVolume`) prompt in verify contexts.

### 10.4 Unified presentation + the host method (context-typed, localization-ready)

The `overwriteOnProceed` bool couldn't express three distinct Proceed-meanings (overwrite / verify /
use-anyway) and mislabeled import. Replace it with a **prompt-context enum** — richer, correctly
labeled, and localization-friendly (the host builds the localized message from `verdict + context`;
the library passes no free-form user text). Add `ProceedAlways` and gate it per context.

```csharp
// ITapeServiceHost.cs

/// <summary>What the user is being asked to proceed INTO — drives the host's localized wording/labels.</summary>
public enum MediaPromptContext
{
    OverwriteBackup,     // backup, mode 3: this media holds content we'd destroy
    ContinuationVolume,  // backup: fresh continuation volume carries a foreign/other-series header
    VerifyRestore,       // restore/append: header should match the TOC we're using
    ImportToc,           // import-TOC-from-file: header vs the imported TOC (one-off; no ProceedAlways)
}

public enum MediaMismatchChoice { Retry, Proceed, ProceedAlways, Abort }
//  ProceedAlways = "proceed AND stop asking for the rest of this operation".

/// <summary>
/// Presents an identified-media problem and the courses of action. The host builds its own LOCALIZED
///  message from <paramref name="verdict"/> + <paramref name="context"/> + <paramref name="headerDescription"/>
///  (the header's ToString()). When <paramref name="allowProceedAlways"/> is false, the host hides that option.
/// </summary>
/// <remarks>
/// HOST GUIDANCE: a non-interactive / unattended host should return <see cref="MediaMismatchChoice.Proceed"/>
///  (preserving legacy batch behavior — an unattended backup must not stall on an overwrite prompt) and LOG
///  the auto-decision. A stricter host may return Abort. The library imposes no policy; it always asks.
/// </remarks>
MediaMismatchChoice OnMediaMismatchConfirm(
    string headerDescription, TapeMediaVerdict verdict, MediaPromptContext context, bool allowProceedAlways);
```

Library-side logging uses a culture-neutral string; the **prompt** text is the host's, localized:

```csharp
protected static string VerdictToString(TapeMediaVerdict v) => v switch
{
    TapeMediaVerdict.Match             => "Match",
    TapeMediaVerdict.Unidentified      => "Unidentified",
    TapeMediaVerdict.WrongKind         => "Wrong kind",
    TapeMediaVerdict.MediaIdMismatch   => "Media ID mismatch",
    TapeMediaVerdict.WrongVolume       => "Wrong volume",
    TapeMediaVerdict.MediaInconsistent => "Media inconsistent",
    _                                  => $"Unknown ({(int)v})",
};

/// <summary>Match/Unidentified → Proceed silently. Suppressed → Proceed (logged). Else host prompts.</summary>
protected MediaMismatchChoice PresentVerdict(
    TapeMediaVerdict verdict, MediaPromptContext context, bool suppress, bool allowRetry, bool allowProceedAlways)
{
    if (verdict is TapeMediaVerdict.Match or TapeMediaVerdict.Unidentified)
        return MediaMismatchChoice.Proceed;

    string text = _loadedHeader?.ToString() ?? "Unidentified media";

    if (suppress)
    {
        LogWarn($"Media check ({VerdictToString(verdict)}/{context}) suppressed — proceeding: {text}");
        return MediaMismatchChoice.Proceed;
    }

    LogWarn($"Media check ({VerdictToString(verdict)}/{context}): {text}");
    return _host.OnMediaMismatchConfirm(text, verdict, context, allowProceedAlways);
}
```

`Retry` is a small per-operation loop (eject → insert-confirm → reload → `RefreshLoadedHeader` →
re-evaluate), offered only where insert machinery already exists (continuation loops); operation-start
checks offer Proceed/ProceedAlways/Abort only (§10.10-2).

### 10.5 Backup + `ForceVolumeOverwrite`

`BackupRequest`: `public bool ForceVolumeOverwrite { get; init; } = false;`

**Setup (start of `ExecuteBackupCore`):** `_agent.WritesMediaHeader = true;` — the agent's
first-set-on-volume condition self-selects (append leaves it alone; overwrite / fresh-volume head).
Use a **local latch** for run-scoped ProceedAlways rather than mutating the request:

```csharp
bool suppress = request.ForceVolumeOverwrite;   // run-scoped; ProceedAlways flips it on
```

**(1) Overwrite (mode 3) — protect existing content.** Before `RemoveAllSets`:

```csharp
if (!append && _loadedHeader is not null)   // media OR calibration header ⇒ real content to destroy
{
    var verdict = _loadedHeader is TapeMediaHeader
        ? TapeMediaVerdict.MediaIdMismatch    // a different backup lives here
        : TapeMediaVerdict.WrongKind;         // a calibration cartridge

    var choice = PresentVerdict(verdict, MediaPromptContext.OverwriteBackup, suppress);
    if (choice == MediaMismatchChoice.Abort)        return MakeResult(aborted: true);
    if (choice == MediaMismatchChoice.ProceedAlways) suppress = true;
}
```

**Overwrite resets MediaId (YES).** After `RemoveAllSets()` / `Volume = 1`, before the agent heads:
`_toc.ResetMediaId();` — which sets `MediaId = Guid.Empty` so the existing idempotent `EnsureMediaId()`
inside `CreateHeader` mints the fresh id (single minting path). A fresh series id avoids collisions with
surviving volumes of the overwritten (legacy) series.

**(2) Append — verify identity.**

```csharp
if (append)
{
    var verdict = EvaluateLoadedHeader(expectedSeriesId: _toc?.MediaId, expectedVolume: _toc?.Volume);
    if (PresentVerdict(verdict, MediaPromptContext.VerifyRestore, suppress: false) == MediaMismatchChoice.Abort)
        return MakeResult(aborted: true);
}
```

**(3) Fresh continuation volume.** After reload+`PrepareMedia`, before `ResumeBackupToNextVolume`:

```csharp
RefreshLoadedHeader();
if (_loadedHeader is TapeMediaHeader mh)
{
    var verdict = mh.MediaId == _toc.MediaId
        ? TapeMediaVerdict.WrongVolume        // an earlier volume of THIS series — almost certainly wrong
        : TapeMediaVerdict.MediaIdMismatch;   // a different backup
    var choice = PresentVerdict(verdict, MediaPromptContext.ContinuationVolume, suppress);
    if (choice == MediaMismatchChoice.Abort)         { …break… }
    if (choice == MediaMismatchChoice.ProceedAlways) suppress = true;
}
else if (_loadedHeader is TapeCalibrationHeader)
{
    var choice = PresentVerdict(TapeMediaVerdict.WrongKind, MediaPromptContext.ContinuationVolume, suppress);
    if (choice == MediaMismatchChoice.Abort)         { …break… }
    if (choice == MediaMismatchChoice.ProceedAlways) suppress = true;
}
// null ⇒ blank fresh volume ⇒ proceed silently. ResumeBackupToNextVolume → the agent heads this volume.
```

A literal reading of "prompt whenever `_loadedHeader` is not null on overwrite" would nag on format →
overwrite-backup since format writes a header. We implement switch prompts only for a wrong-kind cartridge,
a different MediaId, or a TOC that still holds sets — so freshly-formatted/emptied own media overwrites
silently. This is the one place we deviated from this doc's letter to serve its intent.

### 10.6 Restore + `SkipVolumeCheck`

`RestoreRequest`: `public bool SkipVolumeCheck { get; init; } = false;` — local latch
`bool suppress = request.SkipVolumeCheck;`.

**Initial volume (after TOC restored):**

```csharp
var verdict = EvaluateLoadedHeader(expectedSeriesId: _toc.MediaId, expectedVolume: _toc.Volume);
var choice = PresentVerdict(verdict, MediaPromptContext.VerifyRestore, suppress);
if (choice == MediaMismatchChoice.Abort)         return MakeResult(aborted: true);
if (choice == MediaMismatchChoice.ProceedAlways) suppress = true;
```

**Per-volume continuation guard (the staleness-fix payoff):** after reload+`PrepareMedia` for
`volumeNeeded`, before `ResumeRestoreFromAnotherVolume`:

```csharp
RefreshLoadedHeader();   // re-read per volume; TOC stays the last volume's copy
var verdict = EvaluateLoadedHeader(expectedSeriesId: _toc.MediaId, expectedVolume: volumeNeeded);
var choice = PresentVerdict(verdict, MediaPromptContext.VerifyRestore, suppress);
if (choice == MediaMismatchChoice.Abort)         { …break… }
if (choice == MediaMismatchChoice.ProceedAlways) suppress = true;
```

`Unidentified` (a legacy volume) → proceeds silently — mixed headed/headless series just work
(`_MixHeaded` proves it). Even with `SkipVolumeCheck`, `RefreshLoadedHeader` still **runs** (presence
stays correct for navigation); suppression silences only the prompt.

### 10.7 Calibration — separate `host.Confirm`, reuse `_loadedHeader`, no inline UI

ROI confirmed (Option 1): reuses `_loadedHeader` + existing `host.Confirm`, CLI/WPF-symmetric for free,
and closes the **New-calibration-destroys-a-backup gap** (New reads no header, so this is its only
pre-run guard). `CalibrateRequest`: `public bool SkipMediaHeaderCheck { get; init; } = false;`.

In `ExecuteCalibrateCore`, after the existing multi-partition confirm, before dispatching the mode:

```csharp
if (!request.SkipMediaHeaderCheck && _loadedHeader is TapeMediaHeader mh)
{
    if (!_host.Confirm(
            $"This cartridge holds backup media:\n{mh}\nCalibration is destructive and will erase it. Continue?",
            defaultAnswer: false))
        return MakeResult(aborted: true, message: "Calibration cancelled — cartridge holds a backup", mode: request.Mode);
}
```

Composes with the post-run `ForeignHeader` reporting (§9.4): this pre-run guard catches New *before* the
first destructive write; `ForeignHeader` still explains a *failed* Resume/Recalibrate after the fact.

### 10.8 Import-TOC-from-file, delete, and other verbs

**`ImportTOCFromFileAsync` — verify, then adopt Volume but NOT MediaId.** After loading the file TOC,
compare against the header (drive open; media may or may not be loaded — no media ⇒ `Unidentified` ⇒
silent). `ProceedAlways` is disallowed (one-off operation):

```csharp
_agent.LoadTOCFromFile(filePath);

var verdict = EvaluateLoadedHeader(expectedSeriesId: _toc.MediaId, expectedVolume: _toc.Volume);
var choice = PresentVerdict(verdict, MediaPromptContext.ImportToc, suppress: false, allowProceedAlways: false);
if (choice == MediaMismatchChoice.Abort)
    return false;

// On Proceed, reconcile the IMPORTED TOC to the mounted tape — asymmetrically:
if (_loadedHeader is TapeMediaHeader mh)
{
    // Volume is FUNCTIONAL: multi-volume restore positions by TOC.Volume, so it must reflect the
    //  physically mounted volume. Adopt it whenever a header is present.
    _toc.Volume = mh.Volume;

    // MediaId is IDENTITY: do NOT overwrite the imported TOC's MediaId from the tape.
    //  "Proceed" on a MediaIdMismatch is ambiguous ("right tape, use my TOC" vs "right TOC, wrong tape");
    //  blind-adopting would HIDE a wrong-tape error inside a good TOC. Leave MediaId as imported so a
    //  later save re-surfaces the (genuine) inconsistency. Re-identifying is an explicit rename/format,
    //  never a silent side effect of Proceed.
}
```

**`DeleteSetsFromCurrentSetUp`** navigates content directly, so it resolves presence first and does NOT
head (delete-all's `BackupInitialTOC` must preserve the header):

```csharp
public TapeResult DeleteSetsFromCurrentSetUp(bool navigateFromBegin = false)
{
    EnsureHeaderResolved();                    // §17.3 — MoveToBeginOfContent skips the header, never clobbers it
    …
    // delete-all branch:
    return BackupInitialTOC(writeHeader: false);   // §10.1 — preserve, never rewrite, the header
}
```

**`RenameMediaAsync` / `RenameBackupSetAsync`** — TOC-only (end-relative), never content-from-BOM:
nothing needed. **`RestoreTOCAsync` / `CreateInitialTOCAsync`** — TOC-only, header-agnostic
(create-initial is reached only on already-formatted media, headed at format).

### 10.9 Suppression + request summary

| Request | Flag | Effect |
|---------|------|--------|
| `BackupRequest` | `ForceVolumeOverwrite` | overwrite & continuation identified-media prompts → auto-Proceed |
| `RestoreRequest` | `SkipVolumeCheck` | per-volume identity prompt → auto-Proceed (header still re-read) |
| `CalibrateRequest` | `SkipMediaHeaderCheck` | pre-run backup-media confirm → skipped |

All default **false** (interactive safety). Suppression silences prompts only; header reads + presence
resolution always run. `ProceedAlways` sets a **run-scoped local latch** (not the request record).

### 10.10 Settled sub-decisions

1. **Overwrite MediaId → reset** (`ResetMediaId()` → `Guid.Empty` → minted by `EnsureMediaId`). ✔
2. **Retry depth →** continuation loops offer Retry; operation-start offers Proceed/ProceedAlways/Abort. ✔
3. **`RefreshLoadedHeader` → eager at load.** ✔

### 10.11 Implementation checklist: s. §14 steps 8 ff.

The `allowRetry` parameter in `PresentVerdict` §10.4's signature — we distinguishe "continuation loops get
Retry; operation-start doesn't," which a single flag couldn't express. Pass `allowRetry: true` only inside
the multi-volume continuation loops (Step 9c/9d), false elsewhere.

---

## 11. Legacy calibration compatibility (`#if LEGACY_TapeCalibrationRunHeader`)

Retain the pre-unification `TapeCalibrationRunHeader` (original wire format, no kind byte) plus a
one-line `ToHeader()` adapter, so already-measured scratch cartridges still resume:

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

**Caveat:** a legacy block's CRC still validates under the new reader, so classification then rests
on the byte the new format treats as `Kind` (the 2nd `RunId` byte, ~random): ~98% ⇒ not 1/2/3 ⇒
`Unknown` ⇒ fallback; the rest almost always throw in `DeserializeString` ⇒ caught ⇒ fallback. Sub-1%
residual false-parse, acceptable for a temporary dev flag. To make it exact, bump `TapeSerializer.Version`
so `ValidateSignature` cleanly separates old from new. Checkpoint format is unchanged either way.

---

## 12. Invariants

- **INV-1** Header never increments `CurrentContentSet`.
- **INV-2** Presence/identity only by framed-CRC probe.
- **INV-3 (softened):** the *agent* guarantees presence is resolved before content navigation; the
  navigator treats unresolved `Unknown` permissively as `Absent` (no skip) so it stays usable standalone —
  only `Present` triggers the skip.- **INV-4** Headers written only from format / fresh-volume / set-write paths; never into written media.
- ~~INV-5~~ *(retired — headers now on all media, incl. partitioned).* (`NotNeeded` retired; v7-D7).
- **INV-6** Media/set header = one 16 KiB block, no trailing FM (calibration uses the run block).
- **INV-7** Every header carries a `TapeHeaderKind` byte after the signature.
- **INV-8** Classification positive-only: `Unknown` unless framed CRC validates **and** kind known.
- **INV-9** Kinds mutually exclusive per block.
- **INV-10** Presence reset to `Unknown` on every media (re)load.
- **INV-11** `NavigateToHeader`/`WriteHeader` error on `Absent`.
- **INV-12** No raw `Drive.Rewind()` "assume-blank" fallback survives — all route through
  `MoveToBeginOfContentFromBom()` (now including the partition navigator).
- **INV-13** Navigator never reads/parses a header; only the agent does.
- **INV-14** Set headers do not change setmark/filemark counting.
- **INV-15** media-header-present ⟺ set-headers-present, per volume.
- **INV-16** `BlockSize` is a `protected` base slot; each kind exposes it under its own name
  (`TocBlockSize` / `RunBlockSize`).

**New in v8:**

- **INV-17** header presence is resolved **only** at content choke-points; TOC navigation never
  resolves it.
- **INV-18** header block ops reset content position on failure, and leave begin-of-content on
  success.
- **INV-19** The agent writes the media header at `CurrentSetIndex == FirstSetOnVolume` when
  `WritesMediaHeader`, gated by that mutable flag (default false); heading is mechanism, the
  wrong-media verdict stays service/load-time.
- **INV-20** Any "at BOM ⇒ oldest set / assume blank" navigator handler routes through
  `MoveToBeginOfContentFromBom()` (8 sites, §17.12); TOC-side forward-scan rewinds stay raw.
- **INV-21** `VirtualTapeMedia.SeekToBlock` positions the backing stream via `CurrentPositionBytes()`
  for every landing (inside-data / mark / EOD), so write-after-seek never clobbers earlier data.

---

## 13. Test plan (additions over v6)

- **Partitioned media:** header written at content-partition BOM; TOC still loads from the initiator
  partition; begin-of-content skips the header; restore across a partitioned volume incurs no extra
  partition switch for identity.
- **Polymorphic probe:** `Unpack<TapeHeader>` returns the right concrete kind for media/calibration/set;
  `Unpack<TapeMediaHeader>` returns null for a calibration block; both return null for legacy content.
- **WrongKind reporting:** backup load of a calibration cartridge → `WrongKind` + calibration `ToString`;
  `TapeCalibrator` inspect of backup media → reports the media header text.
- **TocBlockSize round-trip:** value persists and reads back; default equals the agent's TOC block.
- **Legacy calibration:** a pre-unification cartridge resumes via the `#if` fallback (single ReadDirect,
  re-parsed buffer); a unified cartridge still reads directly.
- (Carry all v5/v6 tests: clobber regression incl. the partition navigator, presence re-eval per
  volume, `SkipVolumeCheck`/`ForceVolumeOverwrite`, set-header self-correct, etc.)
- **Calibrator foreign-header:** Resume/Recalibrate on a media cartridge → `ForeignHeader` set, service
  reports the media `ToString()`; on small virtual media, the read block is sized ≥ 16 KiB (§9.3) so
  a media header is still classified rather than dropped.

### 13.1 Test-plan additions in v8 (delivered)

Two new classes exercise the header; the existing 1701 remain the headerless baseline (unchanged).

- **`TapeHeaderRoundTripTests`** (Tier-1, pure, no tape):
  media-header all-fields round-trip; partition/placement matrix; null/empty-name → `null` +
  `DisplayName` fallback; `ClampName` over-budget fit; **polymorphic vs narrow `Unpack`** (media,
  calibration, cross-null); **legacy `TapeFileInfo` bytes → null** (no false-classify); blank → null;
  calibration header through the base; `CreateHeader` mints/shares `MediaId`, idempotent, carries TOC
  fields; `ToString` per kind.

- **`TapeHeaderAgentTests`** (Tier-2, all four profiles):
  write→read returns `TapeMediaHeader` with matching `MediaId` + presence `Present`; blank → `Absent`
  (probe **and** read); **headed backup → restore byte-for-byte**; **clobber regression** (header survives
  content + TOC); **TOC reload then restore** (MediaId preserved); multi-set headed restore; headerless
  backup → `ReadHeader` null/`Absent`.

**Recyclability seam (recommended, default-off so the 1701 never move):** add
`VirtualTapeFixture(..., bool withMediaHeader = false)` that calls `agent.WriteHeader()` after
`PrepareMedia()`. Flipping one arg turns any existing round-trip into a headed one; Phase B extends the
same seam with `withSetHeaders`.

---

## 14. Implementation status / plan

**Phase A — media header**
- [DONE] 0 `TapeFramer.cs` (+ `TapeCalibrationFramer` forwarder).
- [DONE] 1 `TapeHeader.cs` — base; `Media`+`Calibration` wired in `ConstructFrom`; **`BlockSize` protected (D19)**.
- [DONE] 2 `TapeMediaHeader.cs` — **`TocBlockSize`** exposes BlockSize.
- [DONE] 3 `TapeTOC.CreateHeader(uint tocBlockSize, TapeTocPlacement placement)`.
- [DONE] 4 `TapeSerializer.cs` — no change needed.
- [DONE] 4a `TapeCalibrationHeader.cs` + calibrator wiring; **legacy fallback behind `LEGACY_TapeCalibrationRunHeader`**.
- [DONE] 5 `TapeNavigator.cs` — presence `{Unknown,Present,Absent}`, `AtHeader`, `NavigateToHeader`,
  header-aware begin-of-content, `MoveToBeginOfContentFromBom()` (incl. **partition navigator**),
  `MoveToTargetContentSet` guard, `InvalidateHeaderPresence`.
- [DONE] 5b Implement for `TapeNavigatorTOCInPartition`:`NavigateToHeader`/`MoveToBeginOfContent` overrides; retire `NotNeeded`; presence resolves like all navigators -- as per D7.
- [DONE] 6 `TapeStreamManager.cs` — `WritingHeader`/`ReadingHeader` + producers.
- [DONE] 7 `TapeAgent.cs` — `WriteHeader(placement)`/`ReadHeader`(polymorphic)/`EnsureHeaderResolved`.
- [DONE] 7T: Test for Navigator and Agent functionality: `TapeHeaderRoundTripTests`, `TapeHeaderAgentTests`, and the
  **header×profile matrix** across navigator + all agent suites (backup/restore/packed/pipelined) +
  **multi-volume ×{None,All,Mixed}**
- **Step 8 — `ITapeServiceHost.cs`:** `TapeMediaVerdict`, `MediaPromptContext`, `MediaMismatchChoice`,
  `OnMediaMismatchConfirm(header, verdict, context, allowProceedAlways)`. WPF host: 3–4-button dialog
  (ProceedAlways shown only when allowed), message localized from `(verdict, context)`. CLI host:
  Retry/Proceed/ProceedAlways/Abort prompt; **non-interactive → Proceed + log** (legacy-compatible).
- **Step 9a — base agent:** `WritesMediaHeader` [DONE]; `BackupInitialTOC(bool? writeHeader)` [DONE];
  `DeleteSetsFromCurrentSetUp` → `EnsureHeaderResolved` + `BackupInitialTOC(writeHeader:false)`.
- **Step 9b — `TapeServiceBase`:** `_loadedHeader`, `RefreshLoadedHeader`, `EvaluateLoadedHeader`,
  `VerdictToString`, `PresentVerdict`, `ResolveMediaLoop`; `RefreshLoadedHeader` in load + format-reload.
- **Step 9c — `.Backup.cs`:** `WritesMediaHeader=true`; overwrite (+`ResetMediaId`)/append/continuation
  checks; `ForceVolumeOverwrite`; ProceedAlways latch.
- **Step 9d — `.Restore.cs`:** initial + per-volume guards; `SkipVolumeCheck`; ProceedAlways latch.
- **Step 9e — `.Calibrate.cs`:** pre-run `_loadedHeader is TapeMediaHeader` confirm; `SkipMediaHeaderCheck`.
- **Step 9f — import & size:** `ImportTOCFromFileAsync` verdict + Volume-adopt (not MediaId); media-header
  block in `ComputeTotalFileSizeOnTape` (§17.8).
- **Step 10 — service tests:** §18 base/derived seam + a verdict/prompt axis (mock host returning
  Proceed/ProceedAlways/Abort/Retry) × header modes; assert overwrite mints a new `MediaId`, mixed-series
  restore proceeds silently, and import adopts Volume but not MediaId on mismatch. Service tests reuse
  the §18 base/derived seam (add header × placement axis).

**Phase B — set header** — 11 `TapeSetHeader.cs`; 12 `CreateSetHeader`; 13 write-at-set-start +
`VerifyCurrentSetHeader`; 14 backup/restore verify + escalate `MediaInconsistent`; 15 service surfacing;
16 tests.
13a `TapeSetTOC.ComputeTotalFileSizeOnTape`: add + `TapeHeader.FixedHeaderBlockSize` to each set's footprint (both the packed and aligned return paths).

### 14.2 Status update v8b

**Phase A — Steps 0-7 DONE** incl. the pulled-forward tests (Step 7T):
- Steps 0–7 (types, navigator incl. partition, stream manager, agent) — DONE.
- **7T (was Step 10, partially pulled forward):** `TapeHeaderRoundTripTests`, `TapeHeaderAgentTests`, and the
  **header×profile matrix** across navigator + all agent suites (backup/restore/packed/pipelined) +
  **multi-volume ×{None,All,Mixed}** — DONE, green. Bugs found & fixed along the way: §17.11
  (`SeekToBlock` EOD), §17.12 (8-site BOM sweep), plus the §17.1–17.3 resolve-placement corrections.
  Service-layer tests remain Step 10.
- Fixtures default **headerless** (D23); heading is agent-driven & opt-in (D21/D22).

**Remaining Phase A — service layer:**
- 8 `ITapeServiceHost.cs` — `MediaMismatchChoice` + `OnMediaMismatchConfirm`.
- 9 `TapeServiceBase*.cs` — `EvaluateLoadedHeader` (WrongKind); **format sets
  `WritesMediaHeader=true`** (all media, `TocPlacement` from `HasInitiatorPartition`); load-time probe;
  restore per-volume re-read; `ForceVolumeOverwrite` / `SkipVolumeCheck`; `DeleteSetsFromCurrentSetUp`
  resolve (§17.3 watch-item); trace logging; §17.8 size accounting.
- Service tests reuse the §18 base/derived seam (add header × placement axis).

**Phase B — set header** — unchanged plan; §13a size-accounting note stands.

---

## 15. Sign-off

v7 extends the header to **all** media at content-partition BOM (retiring `NotNeeded` and the
partition exemption), repurposes `BlockSize` into `TocBlockSize`/`RunBlockSize` as a `protected`
base slot, formalizes the **polymorphic `Unpack<TapeHeader>`** classifier and the `WrongKind`
verdict, and adds temporary legacy-calibration compatibility. `ConstructFrom` stays a compiler-checked
`switch` (closed, co-versioned, single-assembly hierarchy — a self-registration registry is YAGNI
until cross-assembly kinds appear). 

---

## 16. Unified hierarchy (reference)

```csharp
public enum TapeHeaderKind : byte { Unknown = 0, Media = 1, Calibration = 2, Set = 3 }

public abstract record TapeHeader : ITapeSerializable
{
    public const uint FixedHeaderBlockSize = 16 * 1024;   // media/set; calibration uses the run block

    public abstract TapeHeaderKind Kind { get; }

    protected Guid Id         { get; init; }   // MediaId / RunId
    protected uint BlockSize  { get; init; }   // TocBlockSize / RunBlockSize (D19)
    public DateTime CreatedUtc { get; init; }

    protected void SerializePreamble(TapeSerializer s) { /* sig, Kind, Id(16B), CreatedUtc, BlockSize */ }
    private static TapeHeaderPreamble? ReadPreamble(TapeDeserializer d) { /* null if sig mismatch */ }

    public abstract void SerializeTo(TapeSerializer s);

    public static ITapeSerializable? ConstructFrom(TapeDeserializer d)   // POLYMORPHIC probe entry
    {
        if (ReadPreamble(d) is not { } p) return null;
        return p.Kind switch
        {
            TapeHeaderKind.Media       => TapeMediaHeader.ConstructBody(d, p),
            TapeHeaderKind.Calibration => TapeCalibrationHeader.ConstructBody(d, p),
            // TapeHeaderKind.Set wired in Phase B
            _ => null,
        };
    }

    public abstract override string ToString();
}
```

**§16.6 cross-detection benefit** — backup load sees `Kind.Calibration` → "scratch cartridge";
`InspectMedia` sees `Kind.Media` → "holds a backup". Requires the §9.3 `probeLen` sizing when the
reader's block < 16 KiB.

**§16.8 boundary that stays separate** — only the record grammar + framing are shared; the
calibration header rides in the run block via `RecordBlockWriter` (raw `WriteDirect`), the media/set
headers flow through `TapeStreamManager` at the fixed 16 KiB block. Do not unify the I/O.

---

## 17. Implementation insights (Phase A, learned against the live suite)

These refine — and in three places correct — the earlier design once it met real navigator/agent code.

### 17.1 `read ≤ 0` at BOM means **Absent**, not Unknown

Reaching BOM and finding no data (blank tape, or at-EOD) is a **definitive "no media header"**, so it
resolves to `Absent`. It is NOT an unresolved state: nothing retries `EnsureHeaderResolved`, so leaving
`Unknown` here wedges *all* later content navigation (`MoveToBeginOfContentFromBom` rejects `Unknown`).
`Absent` lets navigation proceed and skip nothing — correct for headerless media. A readable block that
is not our media header (calibration / foreign / torn) likewise resolves `Absent` for backup purposes,
while the service still learns the concrete kind from the polymorphic read.

### 17.2 The navigator must stay usable **standalone** — `Unknown` is permissive, not fatal

`MoveToBeginOfContentFromBom` must NOT hard-fail on `Unknown`. The navigator has to work with no agent
(unit tests, direct use), so **only `Present` triggers the header-block skip**; `Unknown`/`Absent` assume
no header and land at block 0 (legacy-safe default). The strict "must be resolved" guard broke every
standalone navigator test and defended against nothing real — every production path resolves presence at
an agent choke-point *before* reaching here. If belt-and-suspenders is wanted, put a `Debug.Assert` at the
**write** choke-point (agent side), never in the navigator.

### 17.3 Resolve presence at **content** choke-points only — never TOC

Header presence is a *content*-navigation concern. TOC navigation is **end-relative** (fast-forward, step
back over the last mark) and never rewinds to BOM, so it must not resolve. Placing `EnsureHeaderResolved`
in `BeginWriteTOC`/`BeginReadTOC` rewound to BOM and destroyed the `-1` (end-of-content) position the
TOC-mark navigator depends on — breaking `WithFmksAndTOCMark` specifically (single-mark profiles re-derived
position and survived; the 3-FM TOC-mark profile could not). **Rule:**

| Path | Header presence | Rewinds to BOM? |
|------|-----------------|-----------------|
| TOC (read/write) | never resolve — end-relative, header-agnostic | no |
| Content (read/write) | resolve first (agent choke-point) | yes (skips header when Present) |
| Navigator standalone | `Unknown` treated as `Absent` (no skip) | — |

Choke-points that call `EnsureHeaderResolved`: `TapeFileBackupAgent.BeginWriteContentForCurrentSet`,
`TapeFileRestoreBaseAgent.BeginReadContentForCurrentSet`. **Step-9 watch-item:**
`DeleteSetsFromCurrentSetUp` navigates content directly and needs an `EnsureHeaderResolved` before its
begin-of-content move once format writes headers, or delete-all on a headed tape clobbers the header.

### 17.4 Header I/O is atomic — no dedicated `TapeState`

The header is one 16 KiB block with no filemark, so write/read are single `WriteDirect`/`ReadDirect`
operations, not persistent streams. `WriteHeaderBlock`/`ReadHeaderBlock` do `EndReadWrite()` → position →
one block op, staying in `MediaPrepared`. This deviates from D13 (dedicated `WritingHeader`/`ReadingHeader`
states) deliberately: `_operationLock` + `EndReadWrite()` already give the interleaving safety, and the
block ops never leave a restful externally-visible state. They **must** `ResetContentSet()` on any early
failure (torn write/read ⇒ unknown position); on success they set `CurrentContentSet = 0` (write) or leave
it at 0 after a `Present` read, because the media header lives at content BOM on every layout — so we sit
at begin-of-content (block 1) either way. `InTOCSet` is reserved for the future §4.1 initiator header.

### 17.5 `MoveToBeginOfContentFromBom` — the single assume-blank primitive (INV-12)

Every raw `Drive.Rewind()` "assume blank" fallback (in the TOC-in-set navigators' no-mark else-branches)
now routes through `MoveToBeginOfContentFromBom` so the transient initial TOC can never overwrite the
header. This fix is mandatory and independent of the dropped trailing filemark.

### 17.6 Partition navigator — combined LOCATE, `Absent` must still reposition

`TapeNavigatorTOCInPartition` overrides route header I/O and begin-of-content to the **content** partition.
`MoveToBeginOfContentFromBom` there computes one target block (`Present` ⇒ 1, `Absent` ⇒ 0) and issues a
single `MoveToPartition(Content, targetBlock)` (Win32 `SetTapePosition` takes partition + block together),
with a stepwise fallback (switch → rewind → optional `MoveToBlock`). **The `Absent` branch must still switch
+ rewind** — it does real repositioning, not a no-op — otherwise a legacy partitioned tape mis-navigates
from a stale (e.g. initiator) position. The `CurrentContentSet == 0` fast path is retained so a header read
that pre-set 0 doesn't redo the switch.

### 17.7 Agent owns parsing; one physical read serves probe and read

`ProbeHeaderPresence` == `ReadHeader`-and-return-presence: the fixed 16 KiB block pulls the whole header in
one `ReadDirect`, so a positive probe needs **no** re-read. The navigator only caches presence + the parsed
header; it never parses. `TapeCalibrator` mirrors this with `ForeignHeader` (reset per verb) so a
calibration run met by a backup cartridge reports the media header via `ToString()`.

### 17.8 On-tape size accounting must add the header block(s)

Each media header consumes 16 KiB of content capacity per volume (Phase B: +16 KiB per set). Add the media
header to `TapeTOC.ComputeTotalFileSizeOnTape` (per volume, or × distinct-volume-count when
`onVolumeOnly: false`); `TapeServiceBase.Used` and the EW reserve then stay honest. Small VIRTUAL
multivolume tests feel this first. Phase B adds the per-set block to `TapeSetTOC.ComputeTotalFileSizeOnTape`
on both the packed and aligned paths.

### 17.9 `BlockSize` is a `protected` reinterpretable slot (D19/INV-16)

Repurposed from dead weight to meaning: `TapeMediaHeader.TocBlockSize` (the TOC's block size — immutable,
forward-looking) and `TapeCalibrationHeader.RunBlockSize` (the run block). `Id` likewise `protected`,
surfaced as `MediaId`/`RunId`. `CreateHeader` factories set both from within the hierarchy.

### 17.10 Continuation-volume heading belongs in the agent (confirms D21)

`ResumeBackupToNextVolume` does `TOC.Volume++` **and** the first content write atomically — leaving
**no external seam** to inject a correctly-numbered header between the two. Writing the header from
*outside* (fixture/service) records the *previous* volume number (cosmetic, but wrong).

Moving the write **into** `BeginWriteContentForCurrentSet` dissolves this: it runs *after* `Resume`
has bumped `TOC.Volume`, so `TOC.CreateHeader` reads the correct volume. The condition
`CurrentSetIndex == FirstSetOnVolume` (⟺ `CurrentSetIndexOnVolume == 0` ⟺ "write content from BOM")
fires precisely on volume 1's first set *and* every continuation volume's first set — one test, both
moments. The write-XOR-resolve shape:

```csharp
if (CurrentSetIndex == FirstSetOnVolume && WritesMediaHeader)
    WriteHeader();           // heads the (fresh/continuation) volume; sets Present, positions at block 1
else
    EnsureHeaderResolved();  // existing/legacy media: probe → Present or Absent
```

`WriteHeader` resolves presence internally (`OnHeaderWritten` → `Present`), so the `else` is correct
(no redundant read). This is the exact shape step 8/9 (service) inherits — the service sets
`WritesMediaHeader = true` at format / fresh-continuation time and never needs a separate header call.

**Layering unchanged:** by backup time the service has already probed at load, shown any verdict, and
obtained overwrite confirmation, so `TOC.MediaId` is "the identity we're authorized to write". The
agent stamping it is mechanism executing a decision already made — the wrong-media *verdict* stays a
load-time, service-owned concern (D16 intact).

### 17.11 Virtual backend: `SeekToBlock` must position the stream for EOD/mark, not only inside-data

A latent `VirtualTapeMedia` bug the header surfaced: `SeekToBlock` only set `m_stream.Position` when
the target landed **inside a data block**. Seeking to **EOD** (`block == TotalBlockCount`, e.g. block
1 just past a lone 16 KiB header) left the stream stale at 0, so the next `WriteBlocks` overwrote the
header. (`WriteBlocks`'s own `TruncateFromCurrentPosition` returns early at EOD, so nothing
self-corrected.) Fix: position the stream via the existing authority `CurrentPositionBytes()`, which
is correct for **all** cases — inside-data, on-a-mark (walks back to nearest data end), and EOD
(returns `m_bytesWritten`):

```csharp
try { m_stream.Position = CurrentPositionBytes(); }
catch (Exception ex) { SetError(ex); LogErrorAsDebug("Stream seek failed"); return false; }
```

Byte-identical to the old path for inside-data; two cases gained. Regression test:
`SeekToBlock_AtEod_PositionsStreamAtEnd_SoNextWriteAppends`. This fix underpins the whole
write-header-then-append-content flow.

### 17.12 The BOM-handler sweep — INV-12 is 8 sites, not 5

On headed media, **"at BOM" ≠ "at the oldest set"**: the oldest set's start is block 1 (past the
header), BOM is the header itself. Every handler that treats a BOM landing as "we're at the oldest
set's start" must route through `MoveToBeginOfContentFromBom()`. Two families, **8 sites total**:

- **5 assume-blank fallbacks** (no-mark else-branches): `TapeNavigatorTOCInSet.MoveToBeginOfContent`;
  `…WithSmks.MoveToEndOfContentInternal`; `…WithFmks.MoveToEndOfContentInternal`;
  `…WithFmksAndTOCMark.MoveToBeginOfContent` **and** `.MoveToEndOfContent`.
- **3 `ERROR_BEGINNING_OF_MEDIA` handlers** (`ResetError()` "fine, we're at the oldest set"):
  `MoveToTargetContentSet` from-end branch; `MoveToTargetContentSet` from-beginning branch (target 0);
  `…WithSmks.MoveToTargetContentSet` optimized-from-`InTOCSet` override.

Grep rule: `grep -n "ERROR_BEGINNING_OF_MEDIA\|Drive.Rewind()" TapeNavigator.cs` — every content-side
"assume at oldest/blank" hit gets `MoveToBeginOfContentFromBom()`; the **TOC-side** `WithFmksAndTOCMark`
`UnknownSet` rewind stays **raw** (it scans *forward* for the 3-FM TOC mark, header-transparent). These
surfaced one-by-one as the headed test dimension flushed latent block-0 assumptions.

---

## 18. Test methodology — the header × profile matrix

The header pulled the agent- and navigator-level tests forward from Step 10 into the implementation
loop (call it **Step 7T**), and drove a reusable pattern for running an existing suite under multiple
header configurations with near-zero per-method churn.

### 18.1 Base-class parametrization (the standing convention)

Move all tests into an **abstract base** exposing the header axis as a property; **sealed subclasses**
fix it. xUnit discovers inherited `[Theory]`/`[Fact]` per concrete class, so every test runs once per
mode. One funnel method injects the axis; static helpers that take a fixture/agent are unchanged.

```csharp
public abstract class XxxBase
{
    protected abstract bool WithMediaHeader { get; }              // (or an enum for >2 modes)
    protected VirtualTapeFixture CreateFixture(/* mirrors ctor */)
        => new(/* … */, withMediaHeader: WithMediaHeader);
    // …tests verbatim, `new VirtualTapeFixture(` → `CreateFixture(`
}
public sealed class Xxx_Headerless : XxxBase { protected override bool WithMediaHeader => false; }
public sealed class Xxx_Headed     : XxxBase { protected override bool WithMediaHeader => true;  }
```

**Migration aid (the compiler as `#define`):** temporarily remove the fixture's `withMediaHeader`
default → every un-migrated `new VirtualTapeFixture(...)` becomes a compile error → the build
enumerates all call sites exhaustively. Restore the default (now **false**, D23) afterward.

Applied to: `TapeNavigatorTests`, `TapeStreamManagerTests` (optional), `TapeBackupAgentTests`,
`TapeRestoreAgentTests`, `TapeRestoreAgentPipelinedTests`, `TapeBackupAgentPackedTests`,
`TapeRestoreAgentPackedTests`.

### 18.2 Fixtures simulate the service (agent-driven heading)

Rather than pre-feed headed media, the fixtures **have the agent write the header** — a makeshift
service layer proving the real path:

- **`VirtualTapeFixture.BackupFiles`** sets `agent.WritesMediaHeader = WithMediaHeader` (or writes the
  header for the first set on volume) — the agent then heads via §17.10. Construction-time heading is
  retained only for tests that never call `BackupFiles` (Navigator/StreamManager take media as-is).
- **`MultiVolumeVirtualTapeFixture`** heads **per volume** via `ShouldHeadVolume(n)`, flipping
  `WritesMediaHeader` before each `ResumeBackupToNextVolume`.

Restore needs **no** explicit header logic — `EnsureHeaderResolved()` at the restore content
choke-point (`BeginReadContentForCurrentSet`) resolves presence per volume automatically (confirmed
empirically: headed backup tests pass with zero restore-side header code). This is D16 working.

### 18.3 Three multi-volume modes — and why `_MixHeaded` is the crown test

`MultiVolumeVirtualTapeFixture` gains `enum VolumeHeaderMode { None, All, Mixed }` → three sealed
subclasses. `Mixed` (legacy vol 1, headed vol 2+) is the **only end-to-end validation of per-volume
presence re-resolution (INV-10)**: during restore the last (headed) volume loads first → `Present` →
content at block 1; swapping to the (headerless) vol 1 → `RenewNavigator` (presence → `Unknown`) →
re-read → `Absent` → content at block 0. That `Present → Absent` transition is exactly what the
staleness fix exists for, and pre-heading all volumes could never surface it. **Correct because**
the TOC records each file's *physical-per-volume* address (captured on that volume's own layout), so
`MoveToBlock(addr)` lands right on each volume regardless of *this* volume's heading — addresses are
physical-per-volume and presence is re-resolved per volume; the mix composes those two facts.

### 18.4 Header-unsuitable tests

A few `[Fact]`s raw-fill the tape / build their own TOC (e.g.
`OverwriteFullTape_FromBeginning_…`). Keep them in the base but guard with SkippableFact:
`Skip.If(WithMediaHeader, "raw-fills to EOM; a BOM header is N/A here")`, building the fixture
explicitly headerless. (Alternatively, a standalone `_Special` class.)

### 18.5 Header-dependent assertions

Under the headed subclass, any assertion of an **absolute** block/position `0` (or "starts at BOM")
must key off the mode — prefer `fixture.FirstContentBlock` (returns 1 headed / 0 headerless) as the
single source of truth over a literal. The conversion itself flushes latent block-0 assumptions —
that pressure is exactly what surfaced §17.11 and §17.12.

### 18.6 Composability

Each new fixture axis = one more abstract property/enum + one more set of sealed subclasses, tests
untouched. Services will add **header × partition-placement**; Phase B adds **header × set-header** —
same seam, same compiler-driven migration.

---
