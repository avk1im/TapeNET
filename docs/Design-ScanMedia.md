# Design — Scan Media (TOC-less media survey)

**Status:** v2 · specified to the implementable state
**Scope:** read a cartridge from BOM forward and produce a **fragment map** — everything on the medium that
can be identified — with **no table of contents in hand and none assumed**. Scanner and service layers;
the WPF surface is a thin read-only viewer (§12).
**Relationship to `Design-RepairMedia.md` v4:** this is that design's `ScanContentSets` primitive, extracted,
de-coupled from the TOC, **and moved out of the agent hierarchy** (§7). Repair Media becomes
*Scan + Compare + Apply*; this document delivers **Scan**, and §10 states precisely what the other two inherit.

---

## 1. Why the TOC-less form is the better primitive

The v4 walk took the TOC as its frame: it iterated `FirstSetOnVolume..LastSetOnVolume` and asked *"does the
tape agree?"* That inverts the dependency on the one cartridge that matters. Dropping the TOC is not a
simplification for expedience — it removes three genuine defects:

- **The end-of-content bound was the design's weakest joint.** v4 §3 established `endOfContentBlock` with a
  `MoveToEndOfContent()` seek, then admitted the seek fails on exactly the cartridge the feature exists for,
  and fell back to `long.MaxValue`. So the bound was **load-bearing on healthy media and absent on damaged
  media** — the opposite of the useful arrangement. Walking to EOD unconditionally needs no bound at all: the
  trailing structures are *identified*, not *avoided*.
- **Reading the TOC area was reframed from hazard to harvest.** v4 treated the trailing `[toc1][FM][toc2]`
  as a trap to stop short of. It is in fact the **most valuable thing on a damaged cartridge**: a TOC copy the
  user no longer has. A scan that identifies TOC copies can *hand the user the TOC* the comparison phase then
  runs against — including `toc2` when `toc1` is the casualty.
- **Findings stop being contingent.** A v4 finding meant *"this set disagrees with that TOC"*. A fragment
  means *"this object exists at this block"* — true regardless of which TOC surfaces later, serializable,
  diffable against several TOCs in turn, and attachable to a support report. Scan once, compare many.

**It also ships sooner and safer:** the scan writes nothing, so the entire feature carries no destructive
path, no witness problem, no TOC-version bump, and no confirmation UX.

---

## 2. What is knowable before the tape moves

The layout is **not** discovered — it is **derived**, from drive capabilities and media organization alone:

| Condition | Navigator | Set separator | Trailing structure |
|---|---|---|---|
| partitioned | `TOCInPartition` | SM if supported, else FM | separator, then EOD in the content partition; TOC in partition 0 |
| setmarks supported | `TOCInSetWithSmks` | SM | `[SM][toc1][FM][toc2][FM]` |
| TOC mark supported | `…WithFmksAndTOCMark` | FM | `[FM][gap][FM][FM][FM][toc1]…` |
| otherwise | `TOCInSetWithFmks` | FM | `[FM][toc1][FM][toc2][FM]` |

`TapeNavigator`'s factory already makes this decision, but it makes it **while constructing a navigator over
a TOC**, which the scanner has not got. The decision is therefore extracted into a pure static:

```csharp
public readonly record struct TapeMediaLayout(                    // NEW — TapeMediaLayout.cs
    string   NavigatorKind,        // for display and for the map
    TapeMark SeparatorMark,        // SM or FM
    bool     TocInPartition,
    bool     HasTocMark);

public static TapeMediaLayout Predict(TapeDrive drive);           // NEW — pure; no media required
```

`TapeNavigator`'s factory is then reimplemented **on top of** `Predict`, so the two can never disagree — the
same discipline §4.1 applies to `ClassifySetHeader`.

Two consequences the walk depends on:

- **The separator mark type is known up front**, so the walk hops the right mark from block zero without
  inferring anything.
- **On the filemark layouts the separator and the TOC delimiter are the same mark**, so mark hopping alone
  can never tell content from TOC. Only **classifying the block that follows each mark** can — which is why
  identification, not bounding, is this design's core (§4).

Recorded as the map's `Layout` and reported even when the tape turns out unreadable: on a cartridge that
yields nothing, knowing *what should have been there* is the whole diagnosis.

---

## 3. The walk

From BOM, forward, to EOD. No bound, no TOC, no lookahead.

```
layout = TapeMediaLayout.Predict(Drive)
Drive.MoveToPartition(MediaPartition.Content);  Drive.Rewind()
fragments = []

// ── 1. Block 0 ────────────────────────────────────────────────────────────
Identify(block 0) → MediaHeader | CalibrationHeader | Unknown   // §4; legacy media has content at block 0
if (kind == CalibrationHeader) → §4.4: record, enrich, STOP

// ── 2. Walk the separators ────────────────────────────────────────────────
loop:
    if (CheckForAbort()) break
    start = Drive.CurrentBlock
    Identify(start) → fragment                     // §4
    record fragment;  Report(progress)

    if (!MoveToNextSeparator())                    // SM or FM per layout
        record TrailingRegion { from = start, terminator = EOD | fault }
        break
```

### 3.1 Consecutive marks are a fragment, not an anomaly

A mark hop that lands **immediately on another mark** means an empty region: the TOC mark's triple filemark,
a double-filemark end-of-data convention, or the erased remains of a set. The walk records a
`MarkRun { count }` fragment and continues. On `…WithFmksAndTOCMark` this run **is** the TOC mark, and
recognising it is what lets the scan label everything after it as the TOC area with confidence rather than
inference.

### 3.2 Termination, and the one thing that must not happen

- **EOD** (`ERROR_NO_DATA_DETECTED`) — the normal, expected end. Not a failure.
- **A positional error** — the tape ends here; recorded as the terminator.
- **A transport error** — drive gone. The scan **fails** and returns what it has, flagged `Truncated`. A
  fragment map presented as complete when the drive died mid-walk is worse than no map, because the
  comparison phase would read the silence as absence.
- **A calibration cartridge** — §4.4.
- **No retry loop, no resync-by-seeking.** A scan that tries to skip a bad region by seeking blind is
  guessing at block numbers on damaged tape, and every fragment after the guess inherits the error. Stop,
  report where, and say the map is truncated.

### 3.3 Cost

One mark hop and one 16 KiB read per fragment. Abortable between fragments; an abort yields a `Truncated`
map still perfectly usable for everything up to the cut.

---

## 4. Identification

At each fragment start, read one header block (`TapeHeaderBlock.Read`, which sets and restores the drive's
block size itself) and try the interpretations in order. All are **pure parses against framed, CRC-checked
records** — no TOC, no expectations, no comparison:

1. **`TapeMediaHeader`** — yields `MediaId`, `Volume`, `HasSetHeaders`, label, creation time.
2. **`TapeSetHeader`** — yields `MediaId`, `Volume`, `VolumeSetIndex`, `GlobalSetIndex`, `Description`,
   `CreatedUtc`, block size.
3. **`TapeCalibrationHeader`** — §4.4.
4. **A TOC stream** — the block carries the `TapeTOC` signature (§4.3).
5. **Unknown** — none of the above. Recorded with a short byte fingerprint (first 32 bytes, hex) so a support
   report can distinguish "random data" from "a structure we do not parse yet".

Steps 1–3 cost **one** call between them: `TapeHeader.ConstructFrom` already dispatches on the kind byte and
returns the right subtype. The scanner does not need to know the kinds apart before parsing — only after.

### 4.1 The refactor this requires, and why it is an improvement

`ClassifySetHeader` today fuses two operations: *parse the record* and *compare it with what the TOC expects*.
TOC-less identification needs only the first, so the parse is extracted:

```csharp
// NEW — pure, no TOC access, no agent required
public static bool TryIdentifyHeaderBlock(ReadOnlySpan<byte> block, out TapeHeader? header);
```

`ClassifySetHeader` then becomes *`TryIdentifyHeaderBlock` + the identity/index comparison*, which is what its
name always implied. Worth doing on its own merits: the comparison half is the only half that needs a TOC, and
fusing them is why v4 could not scan without one.

### 4.2 Legacy and undersized media

`TapeHeaderBlock.IsSupportedBy(Drive)` is false when the drive's maximum block is under 16 KiB (tiny virtual
media, some pre-LTO drives). The scanner then falls back to a raw `ReadDirect` at the drive's block size and
parses from that — the same two-step probe `TapeCalibrator.ReadRunHeader` already performs. Without the
fallback, small-block drives would map every fragment as `Unknown`.

### 4.3 TOC copies — cheap probe, optional harvest

A TOC stream is identified by its **signature at the head of the first block** — a ~16-byte comparison that
cannot mis-fire on a header (different magic). That alone earns a `FragmentKind.TableOfContents` with its
**`TocVersion`**: enough to tell the user *"a TOC copy survives at block 4,196"*.

**Full deserialization is a separate, opt-in step** (`HarvestTocCopies`), because it reads a multi-block
stream at the TOC's own block size mid-walk and can fail on a damaged copy after the signature matched. When
it succeeds the scan attaches the parsed `TapeTOC` — and **the comparison phase then has a candidate TOC that
came off the tape itself.** On the filemark layouts two copies are written, so a cartridge whose first copy is
damaged routinely yields an intact second one. A harvest that throws is caught, downgraded to signature-only,
and logged — never fatal.

The harvest reads blocks and feeds `TapeTOC.ConstructFrom`; it does **not** go through `TapeStreamManager`,
whose TOC path assumes a navigator and a located TOC region. A small `TapeTOCReader.TryReadFrom(drive,
blockSize, out toc)` helper carries it, usable by both the scanner and (later) anything else that needs a TOC
off a known block.

On **partitioned** media the TOC lives in partition 0, so the scanner surveys that partition too when
`HarvestTocCopies` is set; the content partition's map is unaffected either way.

### 4.4 Calibration cartridges — recognized, not rejected

`TapeCalibrationHeader` shares the `TapeHeader` grammar precisely so one BOM read can classify a cartridge as
media, set, or calibration. The scanner must honour that: **a calibration cartridge is a fully identified,
legitimate result, not alien media.**

But the walk must **stop** there, and for a concrete reason: past the header a calibration trail is filemark-
delimited checkpoint blocks separated by *gigabytes of random padding*. Walking it would produce hundreds of
`Unknown` fragments, take a very long time, and tell the user nothing. So:

- record the `CalibrationHeader` fragment, with `ProfileKey` and `StartedUtc` from the header;
- set `MediaScanMap.Kind = ScannedMediaKind.CalibrationCartridge`;
- **stop the walk** — `Truncated` stays **false**: the scan reached a complete and correct conclusion;
- when `InspectCalibrationTrail` is set, enrich by calling `new TapeCalibrator(Drive).InspectMedia()` and
  attach the resulting `TapeCalibrationMediaInfo` (resumable / complete / progress).

That last step is the payoff of the sibling arrangement in §7: the calibrator already owns the legacy
run-block probe and the backward checkpoint walk, and the scanner reuses it by *calling* it — no inheritance,
no duplication, no new code path for a cartridge kind this feature does not otherwise care about.

### 4.5 What identification cannot do

A fragment's **contents** are opaque. The scan proves an object begins at a block and that a separator closes
it. It never reads files, never checks CRCs, and never estimates a set's size — the block delta to the next
fragment is `BlockSpan`, an upper bound including marks, not a payload size.

---

## 5. The records

```csharp
public enum FragmentKind                                          // NEW
{ MediaHeader, SetHeader, CalibrationHeader, TableOfContents, MarkRun, Unknown, TrailingRegion }

public enum ScannedMediaKind                                      // NEW
{ Blank, Backup, CalibrationCartridge, Foreign }

public sealed record TapeMediaFragment                            // NEW — TapeMediaFragment.cs
{
    public required int          Ordinal    { get; init; }   // 0-based position in the walk
    public required long         StartBlock { get; init; }
    public required FragmentKind Kind       { get; init; }
    public long BlockSpan         { get; init; }             // to the next fragment; -1 when trailing
    public bool ClosedBySeparator { get; init; }             // false ⇒ EOD followed: an unclosed set

    // Identity — MediaHeader / SetHeader / CalibrationHeader
    public Guid?     Id             { get; init; }           // MediaId, or the calibration RunId
    public int?      Volume         { get; init; }
    public int?      VolumeSetIndex { get; init; }
    public int?      GlobalSetIndex { get; init; }
    public string?   Description    { get; init; }           // label / set description / ProfileKey
    public DateTime? CreatedUtc     { get; init; }

    // TableOfContents
    public ushort?  TocVersion   { get; init; }
    public TapeTOC? HarvestedToc { get; init; }              // only when the harvest succeeded

    // MarkRun / Unknown / TrailingRegion
    public int        MarkCount   { get; init; }
    public string?    Fingerprint { get; init; }             // first 32 bytes, hex
    public TapeResult Diagnosis   { get; init; }
}

public sealed record MediaScanMap                                 // NEW
{
    public required TapeMediaLayout Layout      { get; init; }    // §2 — known before the tape moves
    public required ScannedMediaKind Kind       { get; init; }
    public required IReadOnlyList<TapeMediaFragment> Fragments { get; init; }
    public required DateTime ScannedUtc         { get; init; }
    public bool Truncated       { get; init; }                    // aborted, transport fault, or MaxFragments
    public uint TerminatorWin32 { get; init; }                    // ERROR_NO_DATA_DETECTED on a clean end
    public TapeCalibrationMediaInfo? CalibrationInfo { get; init; }   // §4.4

    public Guid? MediaId => Fragments.FirstOrDefault(f => f.Kind == FragmentKind.MediaHeader)?.Id;
    public int   SetCount => Fragments.Count(f => f.Kind == FragmentKind.SetHeader);
    public bool  LastSetUnclosed => Fragments.LastOrDefault(f => f.Kind == FragmentKind.SetHeader)
                                             is { ClosedBySeparator: false };
}
```

**Deliberately absent: any notion of "expected", "correct", "complete" or "lost".** Those are comparison
verdicts belonging to the phase that has a TOC. The map states what exists. Keeping the record free of
verdicts is what lets one scan be compared against several TOCs.

### 5.1 The map is serializable

`MediaScanMap` round-trips to JSON via `System.Text.Json` (`HarvestedToc` excluded — a harvested TOC is saved
as a `.tapetoc` beside it). Three things fall out for nearly no cost: the comparison phase need not re-scan; a
user can attach the map to a support request; and a scan taken before a repair becomes the before-picture of
one taken after.

---

## 6. What the map already tells the user — with no TOC at all

- **"This cartridge holds 7 backup sets"** — counted from the tape, not from an index that may be wrong.
- **Every set named and dated**, from its own header. A user who lost the TOC entirely still sees what is on
  the tape.
- **"The last set never completed"** — `LastSetUnclosed`, the dominant real-world fault, with no TOC at all.
- **Mixed identity** — fragments whose `MediaId` or `Volume` disagree with the media header mean another
  series overwrote part of this cartridge. Reported as an observation, not a verdict.
- **Index gaps** — `VolumeSetIndex` jumping 1, 2, 4 means a set is missing from the middle.
- **"A table of contents survives at block N"** — and with `HarvestTocCopies`, the TOC itself.
- **"This is a calibration cartridge, 62% written, resumable"** — §4.4.
- **On healthy media, a complete and correct inventory** — a legitimate *Media Properties* deep-dive, not only
  a forensic tool. A feature users exercise on good media is one they trust on bad media.

---

## 7. Where the scanner lives — analysis and recommendation

Three candidate homes were considered: a method on `TapeSetAgent`, a new `TapeFragmentAgent : TapeAgentBase`,
and a standalone class outside the agent hierarchy.

### 7.1 The evidence against `TapeSetAgent`

v1 §7 listed "four obligations" the scan owed its host class. **Three of them were obligations to *suppress*
inherited behaviour**, not to use it:

| v1 obligation | What it actually was |
|---|---|
| *"`EnsureMediaHeaderResolved()` is NOT called"* | disabling an inherited TOC-driven step |
| *"`Navigator.ResetContentSet()` on exit"* | cleaning up navigator state the scan never wanted |
| *"Statistics reuse `SetsProcessed` / `SetsSucceeded`"* | squeezing fragments into set-shaped counters |

A list of things you must switch off is the clearest possible signal that you are in the wrong base class.
And the scan reuses **not one** method of `TapeSetAgent`: not `DeleteSetsFromCurrentSetUp`, not
`NavigateToTargetContentSet`, not `VerifyBeforeDestructiveWrite`, not `ClassifySetHeader` (it needs the pure
half, §4.1). It cannot even be constructed without a TOC, which the feature by definition does not have.

### 7.2 The evidence against `TapeFragmentAgent : TapeAgentBase`

This is the tempting middle road, and it fails on the two members that define the base class:

- **`TapeNavigator`** is TOC-bound by construction: `CurrentContentSet`, `FirstSetOnVolume`,
  `MoveToBeginOfContent`, the believed-position model, `MediaHeaderPresence`. The scanner needs *raw* mark
  hops and absolute block numbers. It would inherit a component whose entire value is the abstraction the
  scanner must see past — and would spend its life keeping that component quiet. Your own phrasing —
  *"struggling in the strange world of fragments"* — names the failure mode exactly.
- **`TapeStreamManager`** is file/record-shaped: `BeginWriteTOC`, read/write sessions, TOC location. The
  scanner reads single blocks at known positions.
- **`TapeAgentBase`'s constructor requires a `TapeTOC`.** Passing an empty throwaway TOC to satisfy a base
  class is precisely the kind of ceremony that later reads as a real dependency and gets "fixed" by someone
  wiring it up.

The one thing `TapeAgentBase` genuinely offers — header parsing — is **static** (§4.1) and available to any
class in the library.

### 7.3 The future-proofing argument dissolves

*"We will reconcile fragments to sets later, so build on `TapeSetAgent` to be ready."* You answered this
yourself, and it is the decisive point: **the handover is a `MediaScanMap` — a pure value.** The comparison
step is

```csharp
public static IReadOnlyList<TapeSetScanEntry> Compare(MediaScanMap map, TapeTOC toc);
```

with no drive, no tape and no agent anywhere in its signature. Inheritance buys the future phase exactly
nothing, and costs it the ability to run a comparison on a map loaded from JSON with no cartridge present.

### 7.4 Recommendation — `TapeScanner : TapeDriveHolder<TapeScanner>`

A **sibling of `TapeCalibrator`**, not a descendant of `TapeAgentBase`. The symmetry is close to exact:
the calibrator *writes* a self-describing trail and reads it back to measure; the scanner *reads* whatever
trail it finds and describes it. Both drive only the public `TapeDrive` surface; both are backend-agnostic;
neither has files, sets, or a TOC.

```csharp
public sealed class TapeScanner : TapeDriveHolder<TapeScanner>    // NEW — TapeScanner.cs
{
    public TapeScanner(TapeDrive drive) : base(drive) { _resultBuilder = new(this); }

    public ScanMediaOptions Options { get; init; } = new();
    public bool IsAbortRequested { get; set; }
    public TapeResult LastResult => _resultBuilder.Result;

    public MediaScanMap? Scan(IProgress<TapeScanProgress>? progress = null);
}

public readonly record struct ScanMediaOptions(                   // NEW
    bool HarvestTocCopies        = false,
    bool InspectCalibrationTrail = true,
    int  MaxFragments            = 10_000);                       // runaway guard
```

What it inherits from `TapeDriveHolder` is exactly what it needs and nothing else: `Drive`, the error channel
(`SetError` / `SyncErrorFrom` / `LastErrorWin32`), `m_logger` and `LogPrefix`. It adds the calibrator's
`TapeResultBuilder` latch — for the same reason the calibrator needs it: the scan **tolerates** failures (a
failed harvest, a failed legacy probe) and resets the error, so the live error state is a poor witness by the
time the run ends. `Scan` returns `null` on failure with `LastResult` carrying the diagnosis, exactly as
`TapeCalibrator.Run` does.

Two obligations follow from leaving the hierarchy, and both are cheap:

- **Restore the drive's block size** on exit. `TapeHeaderBlock.Read` restores its own, but the TOC harvest
  sets the block size deliberately. A small `BlockSizeGuard` struct mirrors the calibrator's `RunGuard`.
- **The head ends somewhere unspecified.** No navigator of the scanner's own believes anything, so there is
  nothing to reset — but the *service* must not hand the drive back to a long-lived agent that does. §8.

### 7.5 The one concession

`TryIdentifyHeaderBlock` (§4.1) must become **public static** rather than `private protected`, since the
scanner is no longer in the hierarchy. That is right on the merits: a pure, total function from bytes to an
identified header is a genuinely reusable library primitive, and hiding it inside an agent base class was
only ever an accident of where it happened to be needed first.

---

## 7.6 Progress: `IProgress<T>`, not a notifiable

Your instinct is right on both counts, and for a third reason too.

**Reusing `ITapeFileNotifiable` is wrong.** The scan uses none of its ~20 members, and no existing implementer
will ever use the fragment member. A defaulted no-op added to a 20-member interface is a member every
implementer must now *ignore on purpose* — and the interface's whole existing contract (files, sets, prompts,
abort-by-throwing) is meaningless here.

**A bespoke `ITapeFragmentNotifiable` would be `IProgress<T>` with extra steps.** One method, `void`, no
return value, no question asked. That is the definition of `IProgress<T>`, and .NET already supplies it.

**So: `IProgress<TapeScanProgress>`, exactly as `TapeCalibrator` does.** The clinching reason is the third
one: `ITapeFileNotifiable`'s callbacks are `void`, so the *only* way an implementer can stop an operation is
**by throwing** — which is why `TapeAgentBase` is full of `TapeAbortRequestedException` handling. The scanner
does not need that mechanism, because it owns a cooperative `IsAbortRequested` flag and polls it between
fragments. Adopting the notifiable would import an exception-based control-flow protocol to solve a problem
that no longer exists.

```csharp
public readonly record struct TapeScanProgress(                   // NEW
    int    FragmentOrdinal,
    long   CurrentBlock,
    FragmentKind Kind,
    string Phase,                        // "scanning" | "harvesting-toc" | "inspecting-calibration"
    TapeMediaFragment? Fragment);        // null for phase-only ticks
```

**One deliberate divergence from the calibrator.** `TapeCalibrator`'s `catch (TapeAbortRequestedException)` in
the service is documented as purely defensive — the calibrator never throws it. The scanner's is **real**: a
WPF progress sink marshals to the UI thread and may well throw, and a `ServiceScanProgressHandler` may bridge
a `CancellationToken` that way. So `Report` is wrapped:

```
try { progress?.Report(p); }
catch (TapeAbortRequestedException) { IsAbortRequested = true; }   // honour it at the next poll
catch (Exception ex)                { LogWarn(...); }              // a reporting fault never fails a scan
```

This is your point exactly: the exception is caught **at the scanner**, converted into the cooperative flag,
and never tossed over to the service. The walk then stops at the next clean fragment boundary rather than
unwinding mid-read.

---

## 8. The service layer

`TapeServiceBase.Scan.cs`:

```csharp
public sealed record ScanMediaRequest : ServiceOperationRequest    // NEW
{
    public bool    HarvestTocCopies        { get; init; } = true;  // cheap, and the payoff is large
    public bool    InspectCalibrationTrail { get; init; } = true;
    public string? MapExportFolder         { get; init; }          // null ⇒ do not export
}

public sealed record ScanMediaResult : ServiceOperationResult       // NEW
{
    public MediaScanMap? Map { get; init; }
    public ScannedMediaKind MediaKind { get; init; }
    public int  SetsFound        { get; init; }
    public int  TocCopiesFound   { get; init; }
    public int  UnknownFragments { get; init; }
    public bool LastSetUnclosed  { get; init; }
    public string? MapExportPath { get; init; }
    public IReadOnlyList<string> HarvestedTocPaths { get; init; } = [];
    public string Summary { get; init; } = string.Empty;
}

public Task<ScanMediaResult> ScanMediaAsync(ScanMediaRequest request);
```

Structurally a near-twin of `ExecuteCalibrateAsync`: `Task.Run` → `_operationLock` → construct the scanner →
`CreateScanProgressHandler` → a `CancellationTokenSource.CreateLinkedTokenSource` registration that sets
`scanner.IsAbortRequested = true` → dispatch → build the result from `scanner.LastResult`. Derived from
`ServiceOperationResult` directly, not `FileOperationResult`.

`ServiceScanProgressHandler : IProgress<TapeScanProgress>` mirrors `ServiceCalibrateProgressHandler`, and
`CreateScanProgressHandler` is `protected virtual` so hosts and tests can substitute.

Three service-level obligations:

- **No media-identity prompt.** There is no expectation to violate: the scan reads whatever cartridge is
  loaded and reports what it finds. This removes the whole `MediaPromptContext` surface v4 needed — the
  clearest single sign the TOC-less framing is simpler.
- **The drive position is unspecified afterwards.** The scanner leaves no believed position of its own, but
  the service must not hand the drive to anything that holds one. Since agents are constructed per operation
  and the scan runs under `_operationLock`, this costs nothing today — but it is stated as a contract so a
  future cached navigator does not silently inherit a stale position.
- **`_toc` is never touched**, and `_loadedHeader` is refreshed rather than assumed: a scan may well be the
  first thing that establishes what this cartridge is.

`VerbalizeScan(map)` sits beside `JudgeFileOperation` in `TapeServiceBase.Outcome.cs` as a **pure static** —
every line of the summary testable from a hand-built fragment list.

**Offered from:** Media | Scan Media…, always; the `SetAnomalyAdvice.RepairTrailingSets` banner; and after any
failed operation that reported `TOCUnlocated`.

---

## 9. Implementation plan

### Phase 0 — records and the two extractions *(≈ half a day)*
1. `TapeMediaFragment.cs` — `FragmentKind`, `ScannedMediaKind`, `TapeMediaFragment`, `MediaScanMap`,
   `ScanMediaOptions`, `TapeScanProgress`.
2. **`TryIdentifyHeaderBlock`** extracted from `ClassifySetHeader` and made `public static` (§4.1, §7.5);
   `ClassifySetHeader` reimplemented as parse-then-compare.
3. **`TapeMediaLayout.Predict`** extracted (§2); `TapeNavigator`'s factory reimplemented on top of it.
4. `TapeTOCReader.TryReadFrom` helper (§4.3).

**Tests:**
- **`ClassifySetHeader_BehaviourUnchangedAfterExtraction`** and **`NavigatorSelection_UnchangedAfterExtraction`**
  — the existing set-header and navigator suites must pass **untouched**. This phase's entire claim is that
  two refactors changed nothing, and the old suites are the only witnesses that matter;
- `TryIdentifyHeaderBlock` (pure) over: a valid media header, a valid set header, **a valid calibration
  header**, a TOC block, random bytes, an all-zero block, a CRC-corrupted header ⇒ correct kind or `Unknown`,
  **never a throw**;
- `TryIdentifyHeaderBlock_NeverConfusesTheThreeHeaderKinds`;
- `Predict` `[Theory]` over all four `DriveProfile`s ⇒ matches the navigator actually constructed, **and
  succeeds with no medium loaded** (§2's diagnostic value rests on this);
- `MediaScanMap` JSON round-trip preserves every fragment field.

**Acceptance:** builds; all ~3,500 existing tests green; no behavioural change.

### Phase 1 — `TapeScanner` *(the core)*
1. New class per §7.4: `TapeDriveHolder<TapeScanner>`, `TapeResultBuilder` latch, `IsAbortRequested`,
   `BlockSizeGuard`.
2. The §3 walk: partition + rewind, identify block 0, hop separators, mark runs, terminate on EOD /
   positional / transport / `MaxFragments`.
3. Identification per §4 including the §4.2 small-block fallback; signature-only for TOC copies.
4. Progress per §7.6, including the `TapeAbortRequestedException` → flag conversion.

**Tests — `TapeScannerTests`, `[Theory]` over all four profiles:**
- **`HealthyMedia_MapsEverySetAndTheTocCopies`** — 4 sets ⇒ 1 media header + 4 set headers + the trailing TOC
  fragments, each `Kind` correct. *The test that proves §1's central claim*: the structures v4 had to avoid
  are now identified. On `…WithFmksAndTOCMark`, assert the triple filemark surfaces as a `MarkRun`;
- `EverySetHeaderCarriesItsIdentity` — `Description`, `CreatedUtc`, `VolumeSetIndex` match what was written,
  with **no TOC consulted anywhere in the test**;
- **`DamagedTail_LastSetUnclosed`** — via the layout-aware `DamageTheTail` (setmark layouts need
  `EraseLastSetmark()`); assert `LastSetUnclosed`, that the scan **succeeds**, and terminator
  `ERROR_NO_DATA_DETECTED`;
- `CorruptSetHeader_IsUnknown_AndTheWalkContinues` — `ContentReadFaults.CorruptOnce` on set 3's header;
  fragment 3 `Unknown` with a fingerprint, sets 4–5 still identified;
- `ForeignSetHeader_IsRecordedNotJudged` — a differing `MediaId` is a `SetHeader`, **not** an error and
  **not** a terminator;
- `SmallBlockDrive_UsesTheRawFallback` (§4.2) — a virtual drive with max block < 16 KiB still identifies
  headers;
- `LegacyMediaWithoutHeaders_YieldsUnknownAtBlockZero_AndStillMapsSeparators`;
- `BlankMedia_YieldsEmptyMap_WithoutFailing` — `Fragments` empty, `Truncated` false, `Kind = Blank`.
  "Blank" and "broken" must never be the same result;
- `TransportFault_MarksMapTruncated` — `SimulateNavigationFailures(ERROR_NOT_READY)`; `Truncated` true and
  the fragments gathered so far still returned;
- `MaxFragments_StopsAndTruncates`;
- `Abort_MidScan_YieldsTruncatedMap_AndTapeByteIdentical` — snapshot the virtual medium before and after;
- **`ProgressSinkThrowingAbort_StopsCleanly`** — an `IProgress` that throws `TapeAbortRequestedException`
  ⇒ `IsAbortRequested` set, the walk ends at a **fragment boundary**, no exception escapes `Scan`. §7.6's
  divergence from the calibrator, asserted;
- `ProgressSinkThrowingOther_DoesNotFailTheScan`;
- `BlockSize_IsRestored` — assert `Drive.BlockSize` unchanged across a scan that harvested a TOC;
- **`Scan_NeverWrites`** — the backend's write counter is zero, asserted in fixture teardown for the whole
  class rather than per-test.

### Phase 2 — calibration cartridges
1. §4.4: identify, set `Kind`, stop the walk with `Truncated` false, optionally enrich via
   `TapeCalibrator.InspectMedia()`.

**Tests — reusing the existing calibration fixtures:**
- `CalibrationCartridge_IsIdentifiedAndNotWalked` — assert `Kind == CalibrationCartridge`, **exactly one**
  fragment, `Truncated == false`, and that the scan took no hundreds of fragments through the trail;
- `CalibrationCartridge_TrailIsInspectedWhenRequested` — `CalibrationInfo` carries the resumable/complete
  state; and with `InspectCalibrationTrail: false`, it is null and no extra tape motion occurs;
- `InterruptedCalibrationRun_IsStillIdentified` — a run that died before its first checkpoint still yields
  the header fragment;
- `LegacyRunBlockHeader_IsIdentified` — the pre-`TapeHeaderBlock` shape (§4.2).

### Phase 3 — TOC harvest
1. `HarvestTocCopies`: signature → `TapeTOCReader` at the TOC's block size → attach; failures downgraded to
   signature-only and logged. Partition-0 survey on partitioned media.

**Tests:**
- `HarvestedToc_MatchesTheTocThatWasWritten` — deep-compare against the in-memory TOC;
- **`FirstTocCopyDamaged_SecondIsStillHarvested`** — corrupt `toc1`, assert `toc2` comes back intact. On the
  filemark layouts this is redundancy the format has always had and nothing has ever exploited;
- `HarvestFailure_DowngradesToSignatureOnly_AndDoesNotFailTheScan`;
- `Partitioned_HarvestsFromPartitionZero_WithoutDisturbingTheContentMap`;
- a v0x0102 TOC harvests with its own `TocVersion` reported.

### Phase 4 — `ScanMediaAsync`
1. `TapeServiceBase.Scan.cs`, `ServiceScanProgressHandler`, `VerbalizeScan`, optional map export, harvested
   TOCs saved as `.tapetoc`.

**Tests — `ServiceScanMediaTests`:**
- **`VerbalizeScan` pure, from hand-built maps** — healthy, unclosed-tail, mixed-identity, index-gap, blank,
  calibration, truncated. The bulk of the summary's coverage belongs here: no drive, no fixture, microseconds;
- end-to-end over each Phase-1 fixture ⇒ expected counts and summary;
- `Scan_RaisesNoPrompt` — `ServiceTestBase`'s teardown already sweeps un-examined prompts; this states the
  intent explicitly, since **"no identity prompt" is a design decision (§8), not an accident**;
- `Cancellation_ViaRequestToken_AbortsTheScan` — the linked-token registration path;
- `MapExport_WritesReloadableJson`; `HarvestedTocs_AreLoadableViaLoadTOCFromFile` — the harvest feeds the
  existing import path with no new plumbing;
- `Scan_DoesNotMutateLiveToc` — reference and set count unchanged;
- seeding uses `ProceedOnMediaMismatch` per the established discipline, asserting a silent fixture first.

### Phase 5 — TapeConNET
`tapecon scan-media [--harvest-toc] [--export <dir>] [--json]`. `--json` prints the map to stdout, which makes
the feature scriptable and is the natural CLI expression of "the map is the deliverable".

**Tests:** in-process via `TapeConHost` — a damaged-tail cartridge produces a map naming the unclosed set,
and the media stays byte-identical.

### Phase 6 — WPF viewer *(§12)*

---

## 10. What Repair Media inherits

A strict subset; blocks nothing in `Design-RepairMedia.md` v4. When that work resumes:

- **v4 §7.1 `ScanContentSets` is deleted**, replaced by `TapeScanner.Scan` + a comparison step.
  `TapeSetScanEntry` becomes *derived*: `Compare(MediaScanMap, TapeTOC) → IReadOnlyList<TapeSetScanEntry>`, a
  **pure function**. Every v4 state survives unchanged in meaning, now computed rather than observed:
  `Complete`, `Unverified`, `Undescribed`, `Partial`, `Damaged`, `Foreign`, `Absent`.
- **The comparison is pure, so v4's whole state model becomes testable with no tape at all** — a strict
  improvement on v4 §10.3, which could only reach `BuildPlan` that way. `Compare` + `BuildPlan` compose into
  one drive-free pipeline from a JSON map to an approved plan.
- **v4 §3's end-of-content bound is dropped entirely**, and with it RM-3.
- **v4 §6.1's TOC source list grows a third entry** — *a TOC harvested from this cartridge* — the one source a
  user with no backup of their index can actually obtain.
- **v4 §7.3's `OnSetScanned` is dropped**; `ITapeFileNotifiable` is left untouched by this feature.
- **Untouched and still required:** `SetWriteWitness`, `SealPartialSet`, `IsUndescribed` + TOC v0x0103,
  `BuildPlan`, and every invariant except RM-2/RM-3, which this design subsumes.
- **The apply phase stays in `TapeSetAgent`**, where it belongs — it navigates sets, verifies headers and
  writes setmarks, which is precisely that class's business. **Scan leaving the hierarchy is not a precedent
  for repair leaving it.**

**The user runs Scan, then chooses a TOC, then sees the verdict table — and only then is anything destructive
offered.** Better sequencing than v4's, because the user reaches the irreversible step having seen the
evidence twice.

---

## 11. Invariants

| | |
|---|---|
| **SM-1** | The scan writes nothing and moves no mark. |
| **SM-2** | The scan reads no TOC and assumes none. Every fragment derives from the medium alone. |
| **SM-3** | The map contains observations, never verdicts. *Complete*, *lost*, *expected* belong to comparison. |
| **SM-4** | An unidentifiable fragment never terminates the walk; only EOD, a positional error, a transport fault, a calibration header, or `MaxFragments` does. |
| **SM-5** | A transport fault, an abort, or `MaxFragments` sets `Truncated`. A partial map is never presented as complete. |
| **SM-6** | Blank media yields an empty, untruncated map — never a failure. |
| **SM-7** | A calibration cartridge is a complete, successful result: identified, not walked, `Truncated` false. |
| **SM-8** | TOC harvest is best-effort: a failed deserialization downgrades to signature-only and never fails the scan. |
| **SM-9** | The scanner restores the drive's block size and never leaves a believed content position anywhere. |
| **SM-10** | A progress sink can abort the scan but can never fail it: `TapeAbortRequestedException` becomes `IsAbortRequested`, any other exception is logged and swallowed. |
| **SM-11** | `BlockSpan` is an upper bound including marks, never a payload size. |

---

## 12. WPF — the viewer

Small, read-only, and the whole reason this ships first.

- **Media | Scan Media…** — one button, a "harvest TOC copies" checkbox, progress by fragment, cancellable.
- **The map** as a flat list, one row per fragment, in tape order:

  | # | Block | Kind | Identity | Detail |
  |---|---|---|---|---|
  | 0 | 0 | Media header | `{a4f…}` vol 1 | "Archive 2026", set headers present |
  | 1 | 1 | Backup set | #1 | "Monthly — March", 2026-03-31 |
  | 2 | 1,204 | Backup set | #2 | "Monthly — April", 2026-04-30 |
  | 3 | 2,455 | *Unknown* | — | `4f 12 00 00 …` |
  | 4 | 3,702 | Backup set | #4 | "Monthly — June" — *index gap* |
  | 5 | 4,196 | Table of contents | v0x0102 | harvested — **[Save as…]** |
  | 6 | 4,610 | — | — | End of data |

- **A plain-language header line**: *"7 backup sets, the last one incomplete, one table of contents
  recovered."* For a calibration cartridge, the calibration-info pane is shown instead of the fragment list.
- **Actions:** [Save map…], [Save harvested TOC…], and — once Repair Media lands — **[Compare with a table of
  contents…]**, the seam between the two features and the only thing this viewer will need to grow.
