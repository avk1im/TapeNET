## Design — Backup Set Header for TapeLibNET

**Status:** v2 · **specified, ready for implementation**
**Scope:** the set header (per-set identity + index record) and verified set navigation
**Depends on:** the media header (`docs/Design-TapeHeader.md`, v12) — grammar, framing, `TapeHeaderBlock`, presence model
**Supersedes:** Design-TapeHeader.md §16 (Outlook — the set header)

---

### 1. What the feature does

Every backup set written on headed media receives a **set header**: one 16 KiB framed record,
written as the first block of the set's data, carrying the set's identity (`MediaId`, `Volume`) and
its two indices (`VolumeSetIndex`, `GlobalSetIndex`).

Today set positioning is **trusted**: the navigator counts marks and declares itself at set N. With
set headers it becomes **checked** — and, where the fault is a recoverable miscount,
**self-corrected**.

Three user-visible capabilities:

- **Navigation drift is caught and repaired.** A restore that lands on the wrong set detects it from
  the set's own header, learns where it actually sits, and moves the remaining delta. The restore
  succeeds where it would previously have failed a CRC check or silently restored the wrong bytes.
- **A mid-operation cartridge swap is caught.** Media identity is verified at the moment of set
  access, not only at load — closing the window the service's load-time check cannot cover.
- **Legacy coexistence stays automatic.** Header-less volumes carry no set headers, are known to
  carry none before any read, and restore exactly as they do today.

The set header is **additive and position-neutral**: it carries no tapemark, never alters setmark or
filemark arithmetic, and costs zero extra tape movement on the restore path.

---

### 2. Design principles

| | |
|---|---|
| **Checked, not trusted** | Every content-set read verifies the set it landed on against a framed-CRC record before delivering a single file byte. |
| **Correct on read, fail on write** | A read-side miscount is recoverable — move the delta and re-verify. A write-side miscount would destroy data, so v1 never writes speculatively and never self-corrects a write. |
| **Relative correction only** | The header states *where the head is*. Re-navigating from an absolute anchor reproduces the original miscount; only the delta exploits the new information (§9.3). |
| **A priori facts only** | The header precedes its set's content, so it carries only what is known before the first file — identity and indices, never file counts or totals. |
| **Presence is declared, never probed** | The media header states whether set headers exist. No per-set "is this a header?" gamble, no backtracking. |
| **Counting stays sacred** | No mark, no boundary, no navigator arithmetic change. The §5.7 merged-filemark optimization survives untouched. |

---

### 3. The record — `TapeSetHeader`

#### 3.1 Shape

```csharp
public enum TapeHeaderKind : byte { Unknown = 0, Media = 1, Calibration = 2, Set = 3 }   // Set already reserved

public sealed record TapeSetHeader : TapeHeader
{
    public override TapeHeaderKind Kind => TapeHeaderKind.Set;

    public Guid MediaId       { get => Id;        init => Id = value; }         // base slot
    public uint SetBlockSize  { get => BlockSize; init => BlockSize = value; }  // base slot (§3.2)

    public required int  Volume         { get; init; }   // volume carrying THIS set
    public required int  VolumeSetIndex { get; init; }   // 0-based on volume — the functional index
    public required int  GlobalSetIndex { get; init; }   // 1-based in series — attribution, advisory
    public string?       Description    { get; init; }   // clamped snapshot, for ToString() only

    internal static TapeSetHeader? ConstructBody(TapeDeserializer d, TapeHeaderPreamble p);
    public override string ToString();   // "Set #3 (vol 2, on-volume 1) of media {Id:N} — 'Weekly'"
}
```

Wiring into the polymorphic probe costs **one arm**:

```csharp
TapeHeaderKind.Set => TapeSetHeader.ConstructBody(d, p),
```

Nothing else in `TapeFramer`, `TapeHeaderBlock`, or the classification path changes. The
calibration-side cross-classification (Design-TapeHeader.md §8.3) already anticipates a `Set`
stranger in its `ForeignHeader` branch.

#### 3.2 `SetBlockSize` fills the base slot, and is advisory

`TapeHeader` mandates a reinterpretable `BlockSize` slot (INV-13). For a set header the only coherent
meaning is the set's own on-tape block size, which is known a priori. It therefore fills the slot at
no cost and with no new field.

It remains **advisory**. The TOC stays authoritative for every set parameter. A disagreement is
logged, never acted on. Believing the header instead would introduce a second trust hierarchy for
the same fact, with no mechanism to arbitrate between them.

#### 3.3 Omitted fields, and why

Compression mode, hash algorithm and a non-advisory block size are deliberately **not** carried:

- **They duplicate the TOC with no decision procedure.** If the header says 256 KiB and the TOC says
  64 KiB, either answer is wrong: ignoring the header makes it dead weight, honouring it demotes the
  TOC to a secondary authority that nothing else in the library recognizes.
- **They cannot be acted on at the moment they are read.** By the time the set header is parsed,
  `BeginReadContentForCurrentSet` has already committed to the TOC's parameters — block size and the
  hardware-compression interlock are applied from `TOC.CurrentSetTOC`.
- **Every field is a field to version.** The header hierarchy is closed and co-versioned; each
  addition is a permanent format concession.

`Description` is the single exception, admitted for the same reason `OriginalName` is admitted to the
media header: diagnostics must name *which* set, not merely an integer.

**`MediaId` and `Volume` are carried despite duplicating the media header's check.** They differ in
*when* they are read and in *what they enable*:

| | Media header | Set header |
|---|---|---|
| Read at | media load (service, `RefreshLoadedHeader`) | every content-set access (agent) |
| Catches | wrong cartridge before the operation | cartridge swapped *during* the operation |
| Enables | the load-time verdict + prompt | **disambiguating index drift from wrong-tape** |

The last row carries the real weight. Without `MediaId` in the set header a set-index mismatch is
uninterpretable — a miscount and a swapped cartridge are indistinguishable, and the correction logic
of §9 would have no safe entry condition. With it, the failure mode is typed before any action is
taken.

#### 3.4 Factory — `TapeTOC`

`TapeSetTOC` knows neither its own index nor the media identity. `TapeTOC` owns series identity
(`EnsureMediaId`) and set indexing (`CurrentSetIndex`, `FirstSetOnVolume`, `CurrentSetIndexOnVolume`),
so it is the only object able to answer "which set of which medium is this?".

```csharp
// TapeTOC — sole builder of the SET header. Mirrors CreateHeader.
public TapeSetHeader CreateSetHeader(int setIndex) => new()
{
    MediaId        = EnsureMediaId(),
    CreatedUtc     = DateTime.UtcNow,
    SetBlockSize   = this[setIndex].BlockSize,
    Volume         = this[setIndex].Volume,
    VolumeSetIndex = setIndex - FirstSetOnVolume,
    GlobalSetIndex = setIndex,
    Description    = TapeSetHeader.ClampName(this[setIndex].Description),
};

public TapeSetHeader CreateSetHeaderForCurrentSet() => CreateSetHeader(CurrentSetIndex);
```

This preserves the established rule: **each header kind is built by the subsystem that owns its
identity** — `TapeTOC` for media and set, `TapeCalibrator` for runs. The three factories share the
grammar, never a construction path.

---

### 4. Presence — declared by the media header, never probed

#### 4.1 A stray block at a set start costs nothing

Both restore paths position **absolutely**. `RestoreNextFile` calls
`Manager.BeginPackedFileRead(tfi.Address, totalBytes)`, which seeks the pipelined reader to the
file's exact `(block, offset)`; the obsolete aligned path calls `Drive.MoveToBlock(tfi.Block)`.
Neither depends on where the head sits when the set's first file begins.

Consuming one block at the start of a set is therefore free, and no "un-read" or backtrack primitive
is required. The question of recovering a mis-consumed block does not arise.

#### 4.2 Presence is nevertheless declared, not probed

Probe-and-recover remains the wrong shape for a different reason: it converts "no header" into an
*ambiguity* (blank? torn? legacy? foreign?) at a point where the answer is already knowable for free.

Instead, **the media header declares it**:

```csharp
// TapeMediaHeader — one new body field, self-describing, exactly like TocPlacement
public bool HasSetHeaders { get; init; }
```

The media header is read once per volume at every content choke-point
(`EnsureMediaHeaderResolved`) and by the service at every load and every volume swap. The flag rides
along at zero I/O cost, and **per-volume re-resolution comes free** — a mixed series (legacy volume 1,
headed volume 2+) yields the correct answer per volume through the same mechanism that already makes
the `_MixHeaded` crown test pass.

The resolved flag is cached on the **navigator**, as `SetHeadersExpected`, alongside
`MediaHeaderPresence` — so both reset together on every media (re)load and INV-9 extends to set
headers without new machinery.

#### 4.3 The pairing rule, and the shipping window

**SH-1: media-header-present ∧ `HasSetHeaders` ⟺ set-headers-present, uniformly per volume.**

A volume is headed-with-sets, headed-without-sets, or legacy — never mixed within itself.

The flag exists to make the middle state expressible: media written by the v12 release, which heads
the cartridge but writes no set headers. If set headers ship in the same release as the media header,
the flag is pure insurance. It should be carried regardless. Design-TapeHeader.md §15.1 establishes
the governing rule — a format field is cheapest *before* headers reach the field, and cannot be
retrofitted cleanly afterwards. One bool buys permanent freedom to ship the two features
independently.

#### 4.4 The anomaly path

When `SetHeadersExpected` is true and the block at the set start does **not** classify as a set
header, that is a genuine anomaly rather than a legacy case. The agent then:

1. Logs a warning naming the set and the classification outcome.
2. Proceeds with the restore — absolute positioning makes this safe (§4.1).
3. Does **not** fail.

This follows the golden rule inherited from the media header: *a record that cannot be verified never
blocks*. An unreadable set header removes a safety net; it does not remove the tape's data.

---

### 5. Layout and navigation

#### 5.1 Layout

```
Single-partition:  ‹MH›<FM> ‹SH›[set0] [SM] ‹SH›[set1] [SM] … ‹SH›[setN] [SM] [toc1][FM][toc2][FM]
Partitioned:       content:  ‹MH›<FM> ‹SH›[set0] [SM] … ‹SH›[setN] [SM]   |   initiator: [toc1][FM][toc2][FM]
```

`‹SH›` is one logical block at the head of each set's data region, written with a single
`WriteDirect`, with **no tapemark of any kind**.

#### 5.2 No trailing filemark — and why §15.1 does not recur

The media header requires its trailing mark because begin-of-content is a **write entry point**:
content starts there repeatedly, and without a mark that write lands mid-data on a strict drive.

The set header faces no such hazard, because **it is never written into, only written before**:

- The set-start position is always already legal — immediately after a setmark/filemark, at
  begin-of-content (itself post-mark since v12), or at EOD.
- The set header is the **first** thing written there. That write is post-mark or at-EOD, hence legal
  on every drive family.
- Tape writes truncate everything beyond the write point, so the position after that block **is
  EOD**. Every subsequent write in the set is a sequential append, hence legal.

This **retires the caveat carried in Design-TapeHeader.md §16** ("if verified set navigation ever
writes from a set boundary, the §15.1 reasoning applies again"). By construction it does not.

One assumption is load-bearing and named explicitly: the drive treats a write as truncating. That is
universal tape semantics and is already modelled by the virtual backend
(`TruncateFromCurrentPosition`), so it is exercised rather than assumed.

#### 5.3 The navigator is unchanged

The set header lives inside the set's data region, past its opening boundary. Mark counting therefore
cannot observe it. Consequences, all of them "nothing to do":

- `MoveToBeginOfContentFromBom` — unaffected; the eight sites of Design-TapeHeader.md §5.5 stand.
- The §5.7 merged-filemark forward count — unaffected; the set header contributes no mark.
- INV-1 (the header never increments `CurrentContentSet`) — holds for the set header too.
- `TapeNavigatorTOCInPartition` — no partition-specific logic; the set header sits in the content
  partition alongside the sets themselves.

**One addition** is required, and only for the correction path: `ReconcileContentSet` (§9.4).

#### 5.4 Two costs, in two currencies

The set header consumes **one block number** and **16 KiB of tape**. These are distinct quantities:

- *Block number* → file `TapeAddress` values sit one block past the set start. Stamped at commit time
  from `Drive.BlockCounter`, so this is automatic: **no TOC-address surgery** (§7.3).
- *16 KiB* → capacity accounting and the early-warning reserve (§11).

The block cost is 1 regardless of the set's block size; the byte cost is fixed at 16 KiB. On a
256 KiB set the two diverge by a factor of 16.

---

### 6. Set header I/O — one primitive, two positioning flavours

#### 6.1 The media primitives cannot be reused as-is

`WriteMediaHeaderBlock` / `ReadMediaHeaderBlock` perform
`EndReadWrite()` → `MoveToMediaHeader()` → block op → navigator notification. For the set header all
three are wrong:

- `EndReadWrite()` would tear down the `ReadingContent` state the restore path has just entered.
- `MoveToMediaHeader()` would rewind to BOM — the opposite of where the head stands.
- `OnMediaHeaderWritten()` / `ResolveMediaHeaderPresence()` describe *media* presence and would
  corrupt it.

#### 6.2 The split

The distinguishing axis is **positioning**, so the factoring follows it, leaving the kind-specific
wrappers thin:

```csharp
// TapeStreamManager — existing pair, renamed for symmetry (Step 0b)
public bool WriteMediaHeaderBlock(byte[] framedBlock);   // was WriteHeaderBlock — behaviour unchanged
public int  ReadMediaHeaderBlock(byte[] buffer);         // was ReadHeaderBlock  — behaviour unchanged

// NEW — operate at the CURRENT position. No EndReadWrite, no move, no navigator mutation on success.
public bool WriteSetHeaderBlock(byte[] framedBlock)
{
    ResetError();
    if (!TapeHeaderBlock.WriteFramed(Drive, framedBlock, withFilemark: false))
    {
        SyncErrorFrom(Drive);
        Navigator.ResetContentSet();      // SH-6: a torn write leaves the position unknown
        return false;
    }
    return true;
}

public int ReadSetHeaderBlock(byte[] buffer)
{
    ResetError();
    int read = TapeHeaderBlock.Read(Drive, buffer, out _);   // already PURE — no mark handling
    if (read != buffer.Length)
    {
        SyncErrorFrom(Drive);
        Navigator.ResetContentSet();      // SH-6
        return -1;
    }
    return read;
}
```

`TapeHeaderBlock.WriteFramed` gains a `withFilemark` parameter (default `true`, preserving the media
path). `TapeHeaderBlock.Read` needs **no change**: v12 already made it deliberately pure, leaving the
head before any trailing mark. The write-after / read-before asymmetry documented in
Design-TapeHeader.md §6.1 is precisely what the set header requires.

#### 6.3 Navigator discipline on the set-header primitives

**On success, nothing. On failure, `ResetContentSet()`.** A torn block operation leaves the physical
position unknowable; INV-15 already establishes that discipline for the media path. Leaving a stale
`CurrentContentSet` after a failed read would feed a falsehood into the very correction logic this
feature exists to power.

#### 6.4 Raw block I/O inside `ReadingContent`

The set-header read occurs while the manager sits in `TapeState.ReadingContent`, which normally
forbids raw drive access. It is safe for one specific reason: **`TapeFilePipelinedReader` is
constructed lazily inside `BeginPackedFileRead`**, so no prefetch worker exists yet.

**SH-7: the set header is read immediately after `BeginReadContent()` returns and strictly before the
first `BeginPackedFileRead`.** Violating the ordering races a live worker thread against a raw
`ReadDirect`.

---

### 7. Writing the set header

#### 7.1 The gate

```csharp
// TapeFileAgent — mirrors WritesMediaHeader
public bool WritesSetHeaders { get; set; } = true;

public TapeResult WriteSetHeader()
{
    var header = TOC.CreateSetHeaderForCurrentSet();
    byte[]? block = TapeHeaderBlock.Frame(header);
    if (block is null)
    {
        SetError(WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER, "Set header too large for its block");
        return TapeResult.Fail(this);
    }
    if (!Manager.WriteSetHeaderBlock(block))
    {
        SyncErrorFrom(Manager);
        return TapeResult.Fail(this);
    }
    m_logger.LogTrace("Set header written: {Header}", header);
    return TapeResult.OK;
}
```

**The condition is presence, not novelty.** A set header is written whenever content writing begins
at a set's first block — covering `newSet: true` (fresh set at EOD or at begin-of-content) *and*
`newSet: false` (rewrite of the current set from its start). Both destroy whatever stood there, so
both stamp a fresh header. Multi-volume continuation is covered automatically: a continuation set is
a new set on the new volume, with `VolumeSetIndex = 0`.

#### 7.2 The ordering seam

`BeginWriteContentForCurrentSet` has no seam between "positioned at the set" and "packer created":
`Manager.BeginWriteContent` performs `MoveToTargetContentSet()` and `EnsurePackerCreated()` back to
back, and the packer anchors on `Drive.BlockCounter` at creation.

Rather than thread a callback through the manager, the positioning is **hoisted**:

```csharp
// TapeFileBackupAgent.BeginWriteContentForCurrentSet — after the media-header gate and EndReadWrite
Navigator.TargetContentSet = newSet ? ((TOC.CurrentSetIndexOnVolume > 0) ? -1 : 0)
                                    : CurrentSetAsNavigatorContentSet;

// NEW — position explicitly, stamp the header, THEN enter the write state.
if (WritesSetHeaders && Navigator.MediaHeaderPresence == TapeHeaderPresence.Present)
{
    if (!Navigator.MoveToTargetContentSet())
    {
        SyncErrorFrom(Navigator);
        return false;
    }
    if (!WriteSetHeader())
        return false;
}

// …SetBlockSize / compression interlock / early warning / NotifyNextContentWritePosition…

if (!Manager.BeginWriteContent(remainingCapacity))   // its MoveToTargetContentSet now short-circuits
    …
```

**SH-4: positioning to the target content set is idempotent.** `MoveToTargetContentSet` returns
immediately when `TargetContentSet == CurrentContentSet`, so the manager's later call is a genuine
no-op — no second transport move, no re-derivation. This holds for the base implementation *and* for
the `TapeNavigatorTOCInSet` §5.7 override, whose fast path requires `CurrentContentSet < 0` and
therefore declines after a successful positioning.

The invariant is load-bearing. It is stated in the navigator's XML documentation and asserted by test
(Step 0d).

#### 7.3 Addresses and block size follow correctly

- `Drive.SetBlockSize(CurrentSetTOC.BlockSize)` still runs after the header write. `TapeHeaderBlock`
  sets and restores 16 KiB around its own `WriteDirect`, so the set's block size is never disturbed.
- `EnsurePackerCreated` anchors `startBlock = Drive.BlockCounter` **after** the header block, so every
  committed `TapeAddress` lands past it. Design-TapeHeader.md §16's prediction — *"written before the
  packer anchors, so file `TapeAddress`es sit past it and no TOC-address surgery is needed"* — holds
  exactly.

#### 7.4 Paths that need nothing

- **Format / `BackupInitialTOC`** — writes the media header and the TOC; no content set. Untouched.
- **`DeleteSetsFromCurrentSetUp`, trailing branch** — rewrites a setmark at the retention boundary;
  deleted sets' headers are truncated with their data, retained sets keep theirs.
- **`DeleteSetsFromCurrentSetUp`, delete-all branch** — lands at begin-of-content past the media
  header and writes a fresh initial TOC. No set exists, so no set header. Untouched.
- **Rename, TOC save/restore/import** — end-relative and header-agnostic.

---

### 8. Reading the set header

```csharp
// TapeFileRestoreBaseAgent.BeginReadContentForCurrentSet
EnsureMediaHeaderResolved();                      // media presence + SetHeadersExpected (§4.2)
if (!Manager.EndReadWrite()) { … }
Navigator.TargetContentSet = CurrentSetAsNavigatorContentSet;
if (!Manager.BeginReadContent()) { … }            // positions at the set start

if (!VerifySetHeaderForCurrentSet())              // NEW — one ReadDirect + verdict + optional correction
    return false;

Drive.SetBlockSize(TOC.CurrentSetTOC.BlockSize);
Drive.SetHardwareCompression(…);
```

#### 8.1 Exactly one read per positioning

`Manager.BeginReadContent()` **early-returns without moving** when it already sits in
`ReadingContent` at the requested set. An unconditional read there would consume a *content* block
and corrupt the first file.

```csharp
private int m_setHeaderVerifiedFor = int.MinValue;   // cleared whenever the navigator is renewed
```

**SH-8: the set header is read exactly once per physical positioning at a set.** The guard keys on
the set index, not on a bare bool.

---

### 9. The verdict ladder and the navigation correction

#### 9.1 Classification

```csharp
public enum TapeSetHeaderVerdict
{
    Match,          // MediaId, Volume, VolumeSetIndex all agree
    NotExpected,    // legacy / header-less volume — never reached
    Unreadable,     // expected but not classifiable — anomaly, warn and proceed (§4.4)
    WrongMedia,     // MediaId differs — cartridge swapped mid-operation
    WrongVolume,    // MediaId agrees, Volume differs — right series, wrong cartridge
    SetIndexDrift,  // identity agrees, VolumeSetIndex differs — recoverable navigation error
}
```

| Verdict | Action | Rationale |
|---|---|---|
| `Match` | proceed silently, trace only | the overwhelmingly common case; must stay provably silent |
| `NotExpected` | proceed silently | legacy volumes remain first-class citizens |
| `Unreadable` | **warn, proceed** | an unverifiable record removes a net, not the data (§4.4) |
| `WrongMedia` | **fail the set** | every in-memory assumption is void, including the TOC; nothing is correctable |
| `WrongVolume` | **fail the set** | file addresses are physical-per-volume, so a right-series wrong-volume tape yields garbage at every address |
| `SetIndexDrift` | **correct once, re-verify** | identity confirmed ⇒ the fault is positional and bounded (§9.3) |

`GlobalSetIndex` is **checked but never gating** — a mismatch logs a warning and nothing more. This
mirrors the TOC-import reasoning in Design-TapeHeader.md §9.5: `Volume` is *functional*, `MediaId` is
*identity*. Here `VolumeSetIndex` is functional (it drives navigation) while `GlobalSetIndex` is
attribution, and attribution can legitimately shift after a TOC import or a partial-series rebuild.
Gating on it would fail correct restores.

#### 9.2 Identity mismatch is an agent-level error

The agent detects; the service explains. On `WrongMedia` or `WrongVolume` the agent sets the error and
returns failure, and the existing `TapeResult` → service → host chain surfaces it. No prompt is
raised in v1: a cartridge swapped mid-restore is not a decision to offer the user, and the agent has
no host access by design.

`TapeMediaVerdict.MediaInconsistent` — already reserved in the media-header design — is the natural
service-side mapping once this is surfaced as a typed verdict rather than a message (§15.3).

#### 9.3 The correction — bounded, verified, relative

Three constraints make the correction safe:

**(a) Relative, never absolute.** If navigation proceeded from BOM by *x* marks and landed at set *y*,
re-navigating from BOM by *x* marks lands at *y* again — the miscount is in the physical mark
structure, not in the arithmetic. Only the **delta** (*x − y*) exploits the new information. Setting
`CurrentContentSet = y` and re-issuing `MoveToTargetContentSet()` does exactly that, since the
navigator's set moves are relative mark-spacing operations. Corollary: the correction must **not**
re-anchor to BOM or EOD.

**(b) Bounded — one retry.** Correct, re-read, re-verify. On `Match`, proceed with a warning; on
anything else, fail. No third attempt: a tape whose mark structure defeats a short relative move is
inconsistent rather than noisy.

**(c) Read-side only.** A write-side miscount means the agent is about to overwrite the wrong set.
There, failing is correct and correcting is reckless. v1 writes the set header and never reads one on
the backup path, which also leaves the write path's cost profile — the LTO-speed bottleneck —
untouched.

```csharp
// TapeFileRestoreBaseAgent — the correction, in full
case TapeSetHeaderVerdict.SetIndexDrift:
{
    int actual   = header.VolumeSetIndex;
    int expected = TOC.CurrentSetIndexOnVolume;
    m_logger.LogWarning(
        "Set navigation drift: navigator reported set {Expected} on volume, header says {Actual}; correcting by {Delta}",
        expected, actual, expected - actual);

    Navigator.ReconcileContentSet(actual);        // believe the header — SH-9
    Navigator.TargetContentSet = expected;        // relative delta from the TRUE position
    if (!Navigator.MoveToTargetContentSet())
    {
        SyncErrorFrom(Navigator);
        return false;
    }

    var again = ReadSetHeader();                  // re-verify — exactly once
    if (again is null || again.VolumeSetIndex != expected)
    {
        SetError(WIN32_ERROR.ERROR_INVALID_DATA,
            $"Set navigation could not be corrected for set #{TOC.CurrentSetIndex}");
        return false;
    }
    m_logger.LogWarning("Set navigation corrected — now positioned at set {Set} on volume", expected);
    return true;
}
```

#### 9.4 The one navigator addition

```csharp
/// <summary>
/// Adopts a content-set position established by a POSITIVELY VERIFIED set header (SH-9).
/// The only path that may set CurrentContentSet without having moved the tape.
/// </summary>
internal void ReconcileContentSet(int setIndexOnVolume) => CurrentContentSet = setIndexOnVolume;
```

`internal`, single caller, documented precondition. The head sits one block *into* the set when this
runs — semantically still "in set *y*", and correct for relative mark spacing, which is
position-relative rather than block-relative.

---

### 10. A defect the feature surfaces — the stale presence latch

`TapeFileAgent.m_headerResolved` is a per-agent latch that outlives the navigator it describes:

```csharp
internal void EnsureHeaderResolved()
{
    if (m_headerResolved || Navigator.HeaderPresence != TapeHeaderPresence.Unknown)
    {
        m_headerResolved = true;
        return;
    }
    ReadHeader();
}
```

`Manager.RenewNavigator()` (volume swap) installs a fresh navigator with `HeaderPresence = Unknown`,
while `m_headerResolved` stays `true` on the same agent — so `EnsureHeaderResolved` short-circuits
and presence is never re-resolved on the new volume. The defect is currently **masked, not absent**:
the service's `RefreshLoadedHeader()` calls `agent.ReadHeader()` directly per volume, bypassing the
latch. Any agent-driven multi-volume path has no such rescue.

The field is **redundant**. Every route that resolves presence also records it on the navigator:
`ReadHeader()` always calls `ResolveHeaderPresence` (Present *or* Absent, including the `read <= 0`
and failure branches), and `WriteHeader()` → `WriteHeaderBlock` → `OnHeaderWritten()` sets `Present`.
`Navigator.HeaderPresence != Unknown` is therefore exactly equivalent, and resets per navigator by
construction.

**Resolution:** delete `m_headerResolved` and gate solely on the navigator's presence field. This is
a prerequisite rather than a nicety: `SetHeadersExpected` is cached in the same place and inherits
the same reset semantics (§4.2), so building on the latch would propagate the defect to the new
feature. Sequenced as Step 0a.

---

### 11. Capacity and size accounting

Each set header consumes 16 KiB of content capacity. Three consequences:

- `TapeSetTOC.ComputeTotalFileSizeOnTape` — add `TapeHeaderBlock.Size` per set, on both the packed and
  aligned paths. Feeds `TapeServiceBase.Used`.
- `TapeTOC.ComputeContentSizeOnTapeBeforeCurrentSet` — must include preceding sets' headers, since it
  anchors `Drive.NotifyNextContentWritePosition` on the overwrite path.
- The early-warning reserve is unaffected in form: it reserves for the TOC, and the set header is
  spent before any file is written, so it falls naturally inside the accounted content.

Small virtual multi-volume media feel this first, as they did for the media header.

---

### 12. Invariants

| | |
|---|---|
| **SH-1** | Media-header-present ∧ `HasSetHeaders` ⟺ set-headers-present, uniformly per volume. A volume is never partially headed. |
| **SH-2** | The set header is one 16 KiB framed block at the head of its set's data, with **no tapemark**. |
| **SH-3** | The set header contributes no mark and never alters set counting; §5.5 and §5.7 of the media-header design stand unmodified. |
| **SH-4** | Positioning to the target content set is idempotent — hoisting `MoveToTargetContentSet` ahead of `BeginWriteContent` costs no second transport move. |
| **SH-5** | The set header carries only a-priori facts. No file counts, no totals, no post-hoc data. |
| **SH-6** | Set-header block operations leave the navigator untouched on success and call `ResetContentSet()` on failure. |
| **SH-7** | The set header is read immediately after `BeginReadContent()` and strictly before the first `BeginPackedFileRead`. |
| **SH-8** | The set header is read exactly once per physical positioning at a set. |
| **SH-9** | `CurrentContentSet` is adopted from a set header only via `ReconcileContentSet`, only on a positively classified header with matching `MediaId` and `Volume`. |
| **SH-10** | Correction is relative, bounded to one retry, verified by a second read, and never applied on a write path. |
| **SH-11** | `GlobalSetIndex` and `SetBlockSize` are advisory — logged on mismatch, never gating, never overriding the TOC. |
| **SH-12** | On-tape size accounting includes one header block per set. |

---

### 13. Validation

#### 13.1 Fault injection rather than corrupt fixtures

Constructing a genuinely miscounted tape by hand is expensive and fragile. The navigator lies
deterministically instead, following the precedent set by `SimulateFileFailures` and
`SimulateTOCFailureMask` — both instance-level so parallel agents do not interfere:

```csharp
#if DEBUG
/// <summary>
/// Offset injected into forward/backward set moves so the navigator lands N sets away from its
/// target while still believing it arrived. Instance-level. Drives the set-header correction tests.
/// </summary>
public int SimulateSetMiscount { get; set; } = 0;
#endif
```

This reduces the crown scenario to a one-line arrangement and exercises the real correction code
against a real tape rather than a mock.

#### 13.2 The matrix

The §13.1 seam of the media-header design gains one more axis, migrated the same compiler-driven way
(temporarily drop the fixture default so every un-migrated call site becomes a build error):

```csharp
public abstract class XxxBase
{
    protected abstract bool WithMediaHeader { get; }
    protected abstract bool WithSetHeaders  { get; }
    protected VirtualTapeFixture CreateFixture(…)
        => new(…, withMediaHeader: WithMediaHeader, withSetHeaders: WithSetHeaders);
}
public sealed class Xxx_Headerless  : XxxBase { … false, false; }
public sealed class Xxx_MediaOnly   : XxxBase { … true,  false; }   // the v12-release shape — SH-1's middle state
public sealed class Xxx_FullyHeaded : XxxBase { … true,  true;  }
```

The `_MediaOnly` flavour is not filler: it is the **only** validation that `HasSetHeaders = false`
media restores cleanly, which is the entire justification for the flag (§4.3). The combination
`false, true` is invalid by SH-1 and is asserted to throw at fixture construction.

#### 13.3 Coverage

- **Unit** — all-fields round-trip; polymorphic vs. narrow `Unpack` across all three kinds; a
  `TapeSetHeader` block read by the media path classifies as `Absent`, not `Present`; `ClampName`
  budget fit; `CreateSetHeader` index arithmetic across `FirstSetOnVolume` boundaries; `ToString()`
  naming.
- **Agent, all four drive profiles** — write→read round-trip per set; the first file's `TapeAddress`
  sits exactly one block past the set start; multi-set headed backup restores byte-for-byte;
  `WritesSetHeaders = false` produces a `_MediaOnly` tape that restores silently.
- **Correction (the crown suite)** — under `SimulateSetMiscount` ∈ {−2, −1, +1, +2}: drift detected,
  corrected, re-verified, restore completes byte-for-byte, exactly one warning pair logged. Plus:
  uncorrectable drift fails cleanly; `WrongMedia` and `WrongVolume` fail without attempting
  correction; `Unreadable` warns and completes.
- **Ordering regressions** — SH-8: a second `BeginReadContentForCurrentSet` on the same set consumes
  no block (assert the first file restores intact). SH-4: exactly one positioning per set (count
  `MoveToNextContentSetmark` invocations).
- **Multi-volume** — headers × {None, MediaOnly, All, Mixed}; the mixed case proves per-volume
  re-resolution of `SetHeadersExpected`, and doubles as the regression test for §10.
- **Service** — happy paths stay provably silent under the existing exhaustive `AssertMediaPrompts`
  teardown; a `WrongMedia` set header surfaces as an error report without a prompt.

#### 13.4 Real hardware

No new conformance probe is required. §5.2 establishes that every set-header write is post-mark or
at-EOD — both already isolated by the existing **S11** probe and accepted on AIT-2 and DLT-V4. The
existing physical scenarios exercise the shape end to end once set headers are enabled by default.

---

### 14. Implementation plan

Eleven steps. Step 0 is preparatory and behaviour-neutral; Steps 1–3 build the format and the
primitives; Steps 4–5 deliver detection; Step 6 delivers correction; Steps 7–10 complete accounting,
coverage and hardware validation.

Each step lists its changes, its exit criteria, and — where relevant — the tests that must be green
before the next step begins. **The full legacy suite must pass at the end of every step.**

---

#### Step 0 — Preparation: no new behaviour

The whole of Step 0 is refactoring. Every existing `TapeLibNET.Tests` test must pass unchanged at its end, and no test
should require modification beyond renames.

**0a — Remove the stale presence latch (§10).**

- Delete `TapeFileAgent.m_headerResolved`.
- `EnsureHeaderResolved()` gates solely on `Navigator.HeaderPresence != TapeHeaderPresence.Unknown`.
- Remove the two assignments of the field in `WriteHeader()` and `ReadHeader()`; presence is already
  recorded on the navigator by `OnHeaderWritten()` / `ResolveHeaderPresence()` respectively.
- *Exit criteria:* full suite green. Add one regression test —
  `MultiVolume_PresenceReResolvesAfterRenewNavigator`: resolve presence on a headed volume, call
  `Manager.RenewNavigator()`, assert `EnsureHeaderResolved()` performs a physical read (observable via
  `HeaderPresence` transitioning `Unknown → Absent` on a header-less second volume).

**0b — Disambiguating renames.**

Purely mechanical, IDE-driven, compiler-verified. Cheapest before new code references the old names,
and it prevents `HeaderPresence` from silently meaning two things once `SetHeadersExpected` arrives.

| Old | New | Location |
|---|---|---|
| `WriteHeaderBlock` | `WriteMediaHeaderBlock` | `TapeStreamManager` |
| `ReadHeaderBlock` | `ReadMediaHeaderBlock` | `TapeStreamManager` |
| `WriteHeader` | `WriteMediaHeader` | `TapeFileAgent` |
| `ReadHeader` | `ReadBomHeader` | `TapeFileAgent` — returns the polymorphic `TapeHeader?`, which may be a calibration header; naming it "media" would be wrong |
| `ProbeHeaderPresence` | `ProbeMediaHeaderPresence` | `TapeFileAgent` |
| `EnsureHeaderResolved` | `EnsureMediaHeaderResolved` | `TapeFileAgent` |
| `HeaderPresence` | `MediaHeaderPresence` | `TapeNavigator` |
| `ResolveHeaderPresence` | `ResolveMediaHeaderPresence` | `TapeNavigator` |
| `InvalidateHeaderPresence` | `InvalidateMediaHeaderPresence` | `TapeNavigator` |
| `OnHeaderWritten` | `OnMediaHeaderWritten` | `TapeNavigator` |
| `MoveToHeader` | `MoveToMediaHeader` | `TapeNavigator` (+ partition override) |
| `AtHeader` | `AtMediaHeader` | `TapeNavigator` sentinel |
| `WritesMediaHeader` | *(unchanged — already explicit)* | `TapeFileAgent` |

The **enum** `TapeHeaderPresence` keeps its name: it is kind-agnostic.

- Update the `MoveToBeginOfContentFromBom` maintenance grep rule in Design-TapeHeader.md §5.5 and the
  INV-10 / INV-19 wording, which name `MoveToHeader` and `AtHeader`.
- *Exit criteria:* clean build, full suite green, zero diff in test logic.

**0c — `TapeHeaderBlock.WriteFramed(…, bool withFilemark = true)`.**

Signature-only preparation. The default preserves the media path byte-for-byte; no caller changes.

- *Exit criteria:* full suite green; one unit test asserting `withFilemark: false` emits the block and
  no mark (verified against `VirtualTapeMedia` read-back, per the §12.3 methodology of reading what
  physically landed rather than trusting logical state).

**0d — Pin SH-4 (idempotent positioning).**

The hoisted write path (§7.2) depends on `MoveToTargetContentSet` being a no-op when already at the
target. This holds today but is undocumented and untested.

- Add the guarantee to the `MoveToTargetContentSet` XML documentation on `TapeNavigator`, and to the
  `TapeNavigatorTOCInSet` override.
- Add `MoveToTargetContentSet_WhenAlreadyAtTarget_PerformsNoTransportMove` across all four drive
  profiles, counting `Drive.MoveToNextFilemark` / `MoveToNextSetmark` invocations.
- *Exit criteria:* new tests green on all profiles.

**0e — Pin SH-7 (read ordering).**

- Document on `TapeStreamManager.BeginPackedFileRead` that the pipelined reader is constructed lazily
  and that raw block I/O in `ReadingContent` is legal only before the first call.
- Add `Debug.Assert(m_readPacker is null)` at the top of `ReadSetHeaderBlock` (added in Step 3) — noted
  here so the requirement is not lost.
- *Exit criteria:* documentation only; no behaviour change.

**Step 0 exit gate:** full suite green with zero logic diffs. Commit separately from all subsequent
steps so the rename churn never mixes with feature review.

---

#### Step 1 — The record and the format change

One commit, one format version bump. Everything that touches the on-tape wire format lands here, so
the shipping window (§15.1) closes exactly once.

- Add `TapeSetHeader` (§3.1) with `ConstructBody`, `ToString`, `ClampName`.
- Add the `TapeHeaderKind.Set` arm to `TapeHeader.ConstructFrom`.
- Add `HasSetHeaders` to `TapeMediaHeader` (body field) and the corresponding parameter to
  `TapeTOC.CreateHeader`, sourced from the agent's `WritesSetHeaders`.
- Add `SetHeadersExpected` to `TapeNavigator`, reset alongside `MediaHeaderPresence` in
  `InvalidateMediaHeaderPresence`; set by `ResolveMediaHeaderPresence` from the parsed media header
  and by `OnMediaHeaderWritten`.
- *Tests:* the §13.3 **Unit** group in full, including the negative classification cases.
- *Exit criteria:* round-trip green; a `TapeSetHeader` block met by the media path resolves `Absent`;
  legacy `TapeFileInfo` bytes still classify as `null`.

---

#### Step 2 — The factory

- `TapeTOC.CreateSetHeader(int)` and `CreateSetHeaderForCurrentSet()` (§3.4).
- *Tests:* index arithmetic across `FirstSetOnVolume` boundaries, including continuation sets where
  `VolumeSetIndex` resets to 0 while `GlobalSetIndex` continues; `MediaId` shared idempotently with
  the media header.
- *Exit criteria:* pure in-memory tests green; no tape I/O involved.

---

#### Step 3 — Manager primitives

- `WriteSetHeaderBlock` / `ReadSetHeaderBlock` (§6.2), with the SH-6 failure discipline and the SH-7
  debug assertion from Step 0e.
- *Tests:* write-at-current-position then read-back through a raw virtual-media inspection; assert the
  navigator's `CurrentContentSet` is untouched on success and reset on an injected failure.
- *Exit criteria:* primitives exercised directly, without agent involvement.

---

#### Step 4 — Backup write path

- `TapeFileAgent.WritesSetHeaders` and `WriteSetHeader()` (§7.1).
- Hoist positioning in `TapeFileBackupAgent.BeginWriteContentForCurrentSet` (§7.2).
- *Tests:* per-set write→read round-trip on all four profiles; first file's `TapeAddress` exactly one
  block past the set start; SH-4 transport-move count unchanged from Step 0d's baseline; multi-set and
  multi-volume backups produce a header per set with correct indices.
- *Exit criteria:* headed tapes carry set headers; **restore still ignores them entirely** and all
  existing restore tests pass untouched. This step is independently revertible.

---

#### Step 5 — Restore read and verdict, without correction

The first shippable milestone: detection and reporting, no self-correction.

- `VerifySetHeaderForCurrentSet()` with the SH-8 latch (§8.1).
- `TapeSetHeaderVerdict` and the ladder (§9.1), with `SetIndexDrift` treated as a **failure** for now —
  logged in full, corrected in Step 6.
- *Tests:* `Match` proceeds silently; `NotExpected` on legacy and `_MediaOnly` media proceeds silently;
  `Unreadable` warns and completes; `WrongMedia` / `WrongVolume` fail with the expected error code.
- *Exit criteria:* full round-trip suite green under `_FullyHeaded`; drift detected and reported
  under `SimulateSetMiscount` (introduced early here if convenient, or stubbed until Step 6).

---

#### Step 6 — Correction

- `TapeNavigator.ReconcileContentSet` (§9.4).
- The `SetIndexDrift` branch (§9.3), replacing Step 5's failure.
- `#if DEBUG` `SimulateSetMiscount` on the navigator (§13.1), injected into
  `MoveToNextContentSetmark` and the §5.7 fast path alike.
- *Tests:* the §13.3 **Correction** crown suite in full.
- *Exit criteria:* all four miscount offsets corrected and verified; uncorrectable drift fails cleanly;
  the simulator asserts back to `0` at teardown.

---

#### Step 7 — Size accounting

- `TapeSetTOC.ComputeTotalFileSizeOnTape` and
  `TapeTOC.ComputeContentSizeOnTapeBeforeCurrentSet` (§11).
- *Tests:* `TapeServiceBase.Used` matches physical consumption on a small virtual cartridge across
  several sets; the overwrite path's `NotifyNextContentWritePosition` anchor stays correct (regression:
  no premature early warning on a multi-set overwrite).
- *Exit criteria:* multi-volume capacity tests green on small virtual media, where the drift is largest.

---

#### Step 8 — Test matrix migration

- Add `withSetHeaders` to `VirtualTapeFixture` and `MultiVolumeVirtualTapeFixture`; assert the
  invalid `(false, true)` combination throws.
- Split every migrated suite into the three sealed flavours of §13.2, using the compiler-as-`#define`
  technique: drop the fixture default, fix every resulting build error, restore the default last.
- Extend `VolumeHeaderMode` with the `MediaOnly` case for multi-volume suites.
- *Exit criteria:* the full matrix green; the `_MixHeaded` crown test additionally proves per-volume
  `SetHeadersExpected` re-resolution.

---

#### Step 9 — Surfacing

Deliberately minimal. No new host callback, no new prompt (§9.2).

- Ensure the drift and anomaly messages reach the service's `Report` channel at `Warning`, so they
  appear in the WPF log pane and the CLI (§15.2).
- Extend the media-kind reporting in `VirtualDriveProber` only if a set header can be the first block
  encountered — it cannot, since the media header precedes it, so this is expected to be a no-op check.
- *Exit criteria:* a corrected restore leaves a visible warning pair in both applications' logs.

---

#### Step 10 — Hardware validation

- Run the existing physical scenarios with set headers enabled on AIT-2 (strict family) and one LTO
  generation.
- Confirm no new conformance probe is needed (§13.4) — the writes are post-mark/at-EOD, already
  covered by **S11**.
- *Exit criteria:* physical backup→restore round-trip byte-for-byte on both drives; no unexpected
  drift warnings, which would indicate a real mark-counting fault worth investigating on its own
  terms.

---

#### Effort summary

| Step | Area | Size |
|---|---|---|
| 0 | Preparation — latch removal, renames, two pinned invariants | S |
| 1 | Record + format change (one version bump) | S |
| 2 | Factory | XS |
| 3 | Manager primitives | S |
| 4 | Backup write path | M |
| 5 | Restore read + verdict | M |
| 6 | Correction + fault injection | M |
| 7 | Size accounting | S |
| 8 | Test matrix migration | **L** |
| 9 | Surfacing | XS |
| 10 | Hardware validation | S |

The library work is modest and largely mechanical; the matrix migration and the correction suite carry
the cost. The fault-injection seam of §13.1 is what keeps Step 8 an L rather than an XL.

---

### 15. Known risks and watch-items

#### 15.1 The format window closes at first release

`HasSetHeaders` must enter `TapeMediaHeader` **before** headed media reaches the field. Afterwards it
must default to `false` for pre-existing media — survivable, but permanent. Any other pending
media-header field should be bundled into the same version bump (Step 1).

#### 15.2 Correction masks a real fault

A successfully corrected drift means the drive or the medium miscounted marks — a hardware or media
signal, not a nuisance. It must surface at **Warning**, never at Trace, so it reaches the WPF log pane
and the CLI. A tape that corrects on every set is a tape to retire, and the log is the only place that
fact will ever appear.

#### 15.3 The write path stays unverified

v1 verifies on read only. An overwrite that lands on the wrong set still destroys data silently — the
service's load-time media check remains the sole guard, exactly as today. This is a deliberate v1
boundary: a pre-write verification read costs a read plus a reposition on the performance-critical
path. The natural v2 is a `VerifiesBeforeOverwrite` opt-in that reads the existing set header before a
destructive `newSet: false` or first-set overwrite, and maps a mismatch to the reserved
`TapeMediaVerdict.MediaInconsistent` through a new `MediaPromptContext`.

#### 15.4 `SimulateSetMiscount` must never reach Release

Guarded by `#if DEBUG`, like its two precedents. The matrix teardown asserts it returns to `0`; a
leaked non-zero value would silently corrupt every subsequent test in the class.

---

### 16. Outlook — the file header

Deferred deliberately, and likely to take a **different shape**: not a block-level record but a framed
region inside the on-tape file stream itself.

The groundwork already exists. In `TapeFileBackupAgent.BackupFile`, the hash, the codec and
`wstream.Length` are all in hand synchronously before `Manager.EndPackedFile()` — only `StartAddress`
and `Length` arrive later via `PackedCommitTracker.OnCommitted`, and a reader standing on the file
needs neither. A framed header plus a **fixed-size** framed trailer around the existing
`SerializeHeaderTo` payload would therefore cost no new asynchrony, and restore could bound the body
as `SizeOnTape − headerLen − TrailerSize` without a tail read.

Two constraints carry forward: the file pipeline is the LTO-speed bottleneck, so any per-file record
must be pure serialization with no extra tape operation; and a fixed trailer size is what keeps the
restore path arithmetic rather than seek-based.
