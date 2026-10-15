# Design: On-Tape Format 2.1 — Clean Cut with Read-Only Legacy Support

**Status:** v5 — implemented. **Supersedes:** `Design-Format-v2.md` (v4.3) and its appendices A (virtual media), B
(core types) and the R1–R5 review, all folded in here. The code is the authority; everything described here exists.

**Rule references.** Code comments cite this document by invariant (`F7`, `G9`) wherever a rule applies, and by
section (`§5.3`) only for record layouts. `SM-n` (Design-ScanMedia) and `SH-n` (Design-SetHeader) are cited as before.

---

## 1. Summary

Every record TapeNET writes — TOC, per-file headers, media / set / calibration headers, calibration checkpoints,
virtual-media state — is **format 2.1**. Legacy records (the `TF` + `0x01xx` family) stay **readable forever** and are
**never written** by the product.

- **One prologue for everything:** `TpN#` magic, u16 kind, u8 major, u8 minor, varuint body length. Recognition is one
  4-byte compare at byte 0.
- **Tagged, skippable, refusable fields.** Unknown fields are skipped; unknown *critical* fields refuse the record.
  Adding a field never needs a version bump.
- **CRC-64 everywhere.** Framed records carry their own CRC-64; the TOC stream carries one trailing CRC-64.
- **Self-describing files.** Every 2.1 file on tape carries its SetId, FileId, full path, length, attributes and UTC
  times — a set's first file identifies the set with no TOC.
- **Identity:** `SetId` (GUID) per set, `FileId` (counter) per file within a set. A file is `SetId ‖ FileId`.
- **UTC throughout** — on tape and in memory. Displays convert to local.
- **Classes write themselves** through declarative schema tables; no reflection.
- **Mixed media first-class.** A legacy tape's TOC upgrades to 2.1 on its first write; legacy sets keep their 12-byte
  file headers and restore unchanged.
- **Frozen both ways.** Legacy and 2.1 bytes are pinned by checked-in goldens (§12).

---

## 2. Why a Clean Cut

| # | Legacy problem | Consequence |
|---|---|---|
| 1 | Nested records not length-delimited | A new field in a set or file entry misaligns every following record |
| 2 | Fields appended without a version bump | Several layouts share one version; readers guess |
| 3 | A rejected list item still consumed bytes | A rejected record silently misaligns the rest |
| 4 | One library-wide signature `TF` + version | A legacy file header and a v0x0101 TOC start identically |
| 5 | Framed records put the signature behind an int32 length | "Is this ours?" requires trusting the length first |
| 6 | Strict version equality in nested records | No additive evolution |
| 7 | Single-`Read` deserialization | Short reads corrupt parsing on non-memory streams |
| 8 | Machine-endian primitives | Undocumented byte order |
| 9 | `DateTime` as raw local ticks | Times shift across time zones and DST |
| 10 | Fixed-width integers, full path per TOC entry, 16-byte samples | Larger TOCs (eating the EW reserve); checkpoints outgrow their block sooner |
| 11 | File header carries only a UID | A tape without a usable TOC is unrecoverable at file level |
| 12 | One UID counter per TOC | A TOC rebuilt from an older copy re-issues UIDs already on tape |

---

## 3. Invariants

### 3.1 Format rules (F)

| Rule | Statement |
|---|---|
| **F1** | The product writes 2.1 only. No legacy writer ships; the legacy writers live in the test project (§8.4). |
| **F2** | Legacy is read forever, from one frozen, internal namespace (`TapeLibNET.Legacy`), pinned by goldens. |
| **F3** | An old record is never rewritten in place — a tape write truncates everything behind it. Old headers and checkpoints stay old. The TOC is the exception: it is always rewritten whole, so it upgrades on its first write. |
| **F4** | One set, one data format. An empty set may be re-stamped; a set holding files never changes format (§9.1). |
| **F5** | Every 2.1 record starts with `TpN#` at byte 0. No legacy record can: legacy TOCs and file headers start with `TF`, legacy frames with an int32 length below 2²⁴, whose byte 3 is `0x00`. |
| **F6** | Readers accept major 2 with any minor; a newer major is refused (`NewerMajor`), an older one too (`BadValue`). The minor is informational — it names the release that introduced new critical tags. |
| **F7** | The critical bit belongs to the tag. A known field number is matched whatever its bit; an unknown non-critical field is skipped; an unknown critical field refuses the record (`UnknownCritical`). |
| **F8** | An undefined value of a known enum refuses the record (`BadValue`) — unless the field is a `[Flags]` bitmask. Interpretation-driving fields (`DataFormat`, file `Codec`, UTF-16 name) are additionally critical. |
| **F9** | Default elision applies to optional fields only. Required fields are always written — an empty required string as length 0. |
| **F10** | Declared lengths never drive allocation unchecked: field, body and group-depth limits (§4.6), pre-allocation caps on load (1,024 sets, 65,536 files per set), sample counts bounded by the bytes present. Every limit breach is `LimitExceeded`. |
| **F11** | Readers never read ahead of the record they were asked for — a CRC trailer or a file body follows directly, on forward-only streams too. |
| **F12** | A frame's CRC is verified before its record is judged, so a torn record (`CrcMismatch`) is told from an intact one this build cannot read (`Unparseable`). |
| **F13** | A record whose writer throws mid-way is abandoned; the writer stays usable. |
| **F14** | Malformed input surfaces as `TapeFormatException` only. Domain conversion failures inside a record read (`ArgumentException`, `OverflowException`) become `BadValue`; `InvalidOperationException` stays a programming error. |
| **F15** | A TOC stream ends with `TocEnd`, never with "no more records". Any cross-check mismatch, truncation, or format error refuses the copy, and the second copy is tried. |
| **F16** | TOC file batches are self-contained: front coding restarts in each. |
| **F17** | The `TocHeader` fits the TOC's first 16 KiB block (description ≤ 8 KiB, WrittenBy ≤ 256 B) — the identification anchor (§6). |
| **F18** | `SetId ‖ FileId` is globally unique. FileIds are allocated per attempt and never reused; gaps are harmless. |
| **F19** | On load, a set's `NextFileId` is raised above every FileId on record. |
| **F20** | Times are UTC on tape and in memory. Writers normalize (`Utc` as is, `Local` converted, `Unspecified` taken as UTC — never shifted); readers return `DateTimeKind.Utc`. |
| **F21** | File names travel losslessly: a name that is not well-formed UTF-16 travels as raw UTF-16LE in a critical field, never as replacement characters. Free text (descriptions) may be repaired to U+FFFD. |
| **F22** | A 2.1 file body starts with its codec byte, which must equal the TOC's codec. A mismatch refuses the file. |
| **F23** | Identification is positive in every branch. A record damaged behind its magic reports as damage — never as foreign data, never as a phantom TOC. |
| **F24** | A legacy TOC copy is buffered whole for layout detection: in memory up to 256 MiB, in a delete-on-close temp file beyond. |
| **F25** | Legacy local times are converted with the reading machine's zone. A legacy media header and a legacy TOC from the same cartridge go through the same conversion, so their identities keep matching. |
| **F26** | Virtual media whose metadata cannot be read is never recreated or truncated — the load fails with the reason. Only an explicit `Create` starts over. |
| **F27** | Virtual-media state stores only what cannot be derived, and the rebuild is cross-checked against stored totals. |
| **F28** | `LoadedFromLegacy` is cleared only when a TOC copy has reached the tape. A `.tapetoc` export upgrades nothing. |
| **F29** | A change to the prologue layout or to CRC-64 is a new major. |
| **F30** | A checked-in golden reads forever. From the release tag on, goldens are append-only (§12). |
| **F31** | An absent optional field, group included, is reset to its schema default on read — so one wire object can be reused across records without leaking values. |
| **F32** | On TOC-in-partition media the last set of a volume may end at end-of-media without its closing setmark; the TOC records it (`ClosedByEom`), and end-relative navigation on that volume counts one setmark fewer. Never written, and never honoured, on TOC-in-set media. |

### 3.2 Code rules (G) — the format core

| Rule | Statement |
|---|---|
| **G1** | No reflection in product code. A property's own CLR type picks its codec through overload resolution (§7.4). |
| **G2** | `TapeFieldWriter` / `TapeFieldReader` are sealed classes, not `ref struct`s — lambdas capture them. |
| **G3** | Schemas are `static readonly`, frozen and validated on first use. One test touches every product schema. |
| **G4** | Schema targets are mutable with a parameterless constructor. Immutable domain types get a wire DTO; shape mismatches (split address, front-coded name, delta-coded samples) live in its `From` / `To…` methods, never in the schema. |
| **G5** | Integer fields are non-negative varuint. Signed values exist only where a table says so (timestamps, coded blobs). A negative value on write is a programming error (`ArgumentOutOfRangeException`). |
| **G6** | No `Debug.Assert` on input-dependent conditions — a failed assert fail-fasts the test host. |
| **G7** | Inside a field, values carry no own length prefix — the field's length delimits them. Every typed read consumes the value exactly. |
| **G8** | Every format error carries record, field and body offset where known. |
| **G9** | Writers emit ascending field numbers, enforced by `InvalidOperationException`. Readers accept any order. |
| **G10** | A record that its own reader would refuse never reaches tape: the schema's validate hook runs on write too. |

---

## 4. Wire Format

### 4.1 Primitives

| Type | Encoding |
|---|---|
| varuint | unsigned LEB128, ≤ 10 bytes; non-canonical (overlong, trailing zero group) and > 64-bit refused |
| varint | ZigZag, then varuint |
| f64 | IEEE 754 binary64, little-endian |
| bool | one byte, 0 or 1; anything else refused |
| guid | 16 bytes, `Guid.TryWriteBytes` order |
| timestamp | varint UTC ticks (F20); ticks outside `DateTime` range refused |
| string | UTF-8, strict both ways (a lone surrogate cannot be written; invalid UTF-8 cannot be read) |
| bytes | raw bytes |

Strings and bytes are field values: the field's length delimits them (G7). Fixed-size values (f64, guid) refuse any
other length — too short is `Truncated`, too long `Underrun`. All stream reads are read-until-full.

### 4.2 Record prologue

```
Record   := Prologue Body
Prologue := Magic "TpN#" (4)  Kind:u16 LE  Major:u8  Minor:u8  BodyLength:varuint      (8 + 1..4 bytes)
Body     := Field*
Field    := Tag:varuint  Length:varuint  Value[Length]
```

- **Kind** — §4.3. The top bit (`0x8000`) marks a record **skippable** by readers that do not know the kind; any other
  unknown kind is refused (`UnknownKind`). No registry kind sets the bit.
- **Major.Minor** = 2.1 (F6).
- **Version on top-level records only.** Nested groups (file entries, the calibration plan, the EW point) are covered
  by their parent's version and carry no prologue.
- **BodyLength** makes every record skippable; overrun and underrun are refused. The body buffer grows with the bytes
  that actually arrive, so a lying length cannot pre-allocate 16 MiB (F10).

### 4.3 Kind registry

| Kind | Record | Carrier |
|---|---|---|
| `0x0101` | TOC header | TOC stream |
| `0x0102` | TOC set | TOC stream |
| `0x0103` | TOC file batch | TOC stream |
| `0x0104` | TOC end | TOC stream |
| `0x0201` | File header | inline frame, before each file body |
| `0x0301` | Media header | block frame |
| `0x0302` | Set header | block frame |
| `0x0303` | Calibration run header | block frame |
| `0x0304` | Calibration checkpoint | block frame |
| `0x0F01` | Virtual media state | inline frame in the host metadata file (not on tape) |

High byte = family: 01 TOC, 02 content, 03 headers, 0F host-side.

### 4.4 Fields

```
Tag := (Number << 1) | Critical
```

| Numbers | Shape | Holds |
|---|---|---|
| 1–31 | scalar | integers, bools, timestamps, GUIDs, enums |
| 32–47 | blob | strings and byte arrays |
| 48–63 | blob or group | nested groups and bulk byte arrays (calibration samples, virtual-media block list) |

- Ranges keep "scalars first" as fields are added; every tag ≤ 63 encodes in one byte.
- **No reserved slots; numbers are never reused.** A removed field retires its number.
- Unknown and critical fields: F7. Enums: F8. Elision: F9.
- **Duplicates** of a non-repeated number are refused (`Duplicate`); a missing required field refuses the record
  (`MissingRequired`); only groups may repeat.
- The schema validates its declarations when it freezes: number range per shape, duplicates (inherited ones
  included), `Repeated` only on groups, `Required` only with the type's zero default, `Bitmask` only on enums, no
  record kind on a group's child schema, no `Add` after first use.

### 4.5 Integrity and frames

CRC-64 is `System.IO.Hashing.Crc64` (ECMA-182) everywhere.

| Carrier | Layout | Used by |
|---|---|---|
| **Block frame** | `Record ‖ CRC-64(Record)` at block offset 0; rest of the block padding | media / set / calibration headers, checkpoints |
| **Inline frame** | `Record ‖ CRC-64(Record)`, followed directly by whatever comes next | file header, virtual-media state |
| **CRC-64 envelope** | `Record* ‖ one CRC-64 over all of them` | TOC stream |

Frame unpacking is total — every fault is a status, never an exception — and runs in this order (F12):

| Status | Condition |
|---|---|
| `NotFramed` | no `TpN#` at offset 0, prologue cut off or implausible, or the declared length runs past the bytes available |
| `CrcMismatch` | bounds fit, CRC differs — torn or corrupt |
| `Unparseable` | CRC matches, but newer or older major, unknown kind, wrong kind for the caller, or a body the schema refuses |
| `Ok` | all of the above passed |

`TryUnpackWithLegacy` sends anything without the magic to the record type's frozen legacy reader instead.

### 4.6 Bounds

| Limit | Value |
|---|---|
| `MaxStringBytes` | 128 KiB (a 32,767-char path is ≤ ~96 KiB UTF-8) |
| `MaxBytesField` | 16 MiB |
| `MaxRecordBody` | 16 MiB |
| `MaxGroupDepth` | 4 |

Checked on write and on read, on the stream path and the span path alike (F10).

---

## 5. Record Catalog

Columns: number · field · type · **R** = required, **C** = critical, otherwise the default an optional field elides.

### 5.1 TOC stream

```
TocHeader  (TocSet  TocFileBatch*)*  TocEnd  ‖ CRC-64 over every byte above
```

- **TocHeader first, TocEnd last (F15).** A clean end of stream without `TocEnd` is `Truncated`; a batch before any set,
  or any other kind inside the stream, is `UnexpectedKind`.
- **Batches** close at **4,096 files or ~1 MiB body**, whichever comes first (one entry may overshoot); front coding
  restarts in each (F16). An empty set writes no batch.
- **Cross-checks:** `TocHeader.SetCount` = sets read = `TocEnd.SetCount`; per set, files read = `FileCount` — a batch
  that brings more files than declared is refused as it arrives; `TocEnd.TotalFileCount` = their sum.

**TocHeader (`0x0101`)**

| # | Field | Type | Flags |
|---|---|---|---|
| 1 | MediaId | guid | empty |
| 2 | CreationTime | timestamp | R |
| 3 | LastSaveTime | timestamp | R |
| 4 | Volume | varuint | 1 |
| 5 | ContinuedOnNextVolume | bool | false |
| 6 | SetCount | varuint | R |
| 32 | Description | string | "" — clamped to 8 KiB (F17) |
| 33 | WrittenBy | string | "" — clamped to 256 B |

**TocSet (`0x0102`)**

| # | Field | Type | Flags |
|---|---|---|---|
| 1 | SetId | guid | empty — the validate hook refuses a V2 set without one |
| 2 | CreationTime | timestamp | R |
| 3 | LastSaveTime | timestamp | R |
| 4 | BlockSize | varuint | R |
| 5 | Volume | varuint | R |
| 6 | DataFormat | varuint | **C** — V2 (2); Legacy = 1 |
| 7 | NextFileId | varuint | R — validate: ≥ 1 |
| 8 | HashAlgorithm | varuint | Crc32 |
| 9 | Compression | varuint | None |
| 10 | CompressionLevel | varuint | `ZstdLevel.Default` |
| 11 | Incremental | bool | false |
| 12 | ContinuedFromPrevVolume | bool | false |
| 13 | FileCount | varuint | R |
| 14 | ClosedByEom | bool | false — F32; optional, not critical |
| 32 | Description | string | "" — clamped to `MaxStringBytes` |

**TocFileBatch (`0x0103`)** — repeated field **48**, a group per file. Unknown fields in a batch body are skipped
(F7).

| # | Field | Type | Flags |
|---|---|---|---|
| 1 | FileId | varuint | R |
| 2 | AddressBlock | varuint | R |
| 3 | AddressOffset | varuint | 0 |
| 4 | Length | varuint | 0 |
| 5 | SizeOnTape | varuint | 0 |
| 6 | Attributes | varuint | 0 — bitmask (F8) |
| 7 | Codec | varuint | **C** — Stored |
| 8 | CreationTime | timestamp | R |
| 9 | LastWriteTime | timestamp | R |
| 10 | LastAccessTime | timestamp | R |
| 11 | NameShared | varuint | 0 — UTF-16 chars shared with the previous name in this batch |
| 32 | NameSuffix | string | **R** (may be empty) |
| 33 | Hash | bytes | absent ⇒ no hash |
| 34 | NameUtf16 | bytes | **C** — ill-formed names only (F21) |

**Front coding** (`TapeNameFrontCoder`, one class for writer and reader): each name stores the count of UTF-16 chars it
shares with the previous one plus the differing tail; the split never falls inside a surrogate pair. A name carried in
field 34 has `NameShared = 0`, an empty suffix, and is still *remembered*, so the next entry shares its prefix as usual.

**TocEnd (`0x0104`)**

| # | Field | Type | Flags |
|---|---|---|---|
| 1 | SetCount | varuint | R |
| 2 | TotalFileCount | varuint | R |

### 5.2 Identity — SetId and FileId

| Situation | SetId | NextFileId |
|---|---|---|
| New set (`AddNewSetTOC`, also replacing a trailing empty set; `ReplaceCurrentSetTOC`) | freshly minted | 1 |
| Multi-volume continuation (`AddContinuationSetTOC`) | **same** as the original — one logical set | carried in `TapeSetTOCParams` |
| Legacy set (loaded) | `Guid.Empty` | legacy UID becomes FileId; NextFileId = max + 1 |

- FileIds follow F18; `FileId == 0` means "not set", and `TapeFileInfo.IsValid` ⇔ FileId ≠ 0 and a name is present.
- Leftovers of an overwritten set beyond the new end keep their old SetId and can never be mistaken for the new set.
- A trailing empty set is **replaced** by a fresh instance, never reset field by field — a future attribute can then
  never be forgotten.

### 5.3 File header (`0x0201`, inline frame)

```
[file header frame: TpN# · 0x0201 · 2.1 · fields · CRC-64] [codec u8] [body]
```

| # | Field | Type | Flags |
|---|---|---|---|
| 1 | SetId | guid | R |
| 2 | FileId | varuint | R |
| 3 | Length | varuint | 0 |
| 4 | Attributes | varuint | 0 — bitmask |
| 5 | CreationTime | timestamp | R |
| 6 | LastWriteTime | timestamp | R |
| 7 | LastAccessTime | timestamp | R |
| 32 | FullName | string | **R** — "" when field 33 carries the name |
| 33 | NameUtf16 | bytes | **C** — ill-formed names only (F21) |

Validate (G10): SetId non-empty, FileId ≠ 0, not both name forms.

- **Not front-coded** — one torn header must not break a chain of others.
- **Not in the header:** Hash, SizeOnTape and Codec, all unknown when the header is written.
- **Codec byte (F22).** One byte (0 Stored, 1 Zstd) at the start of the body: written by
  `ProbingCompressionStream.Commit()` once it has decided (`EmitsCodecPrefix`), or by the agent on the plain path.
- **Hash** covers the uncompressed body only. **SizeOnTape** covers frame + codec byte + body.
- **Restore checks** the frame's CRC (→ `ERROR_CRC`), then `SetId` and `FileId` against the TOC (→
  `ERROR_INVALID_DATA`), then the codec byte against the TOC. A **name** mismatch is only logged: the TOC is
  authoritative for names.
- `TapeFileHeader.Measure` gives the exact frame size; `EstimateOverhead` the capacity-planning bound
  (120 B + name bytes, or 256 B assumed when the name is unknown).
- **Body end.** A body has no end marker. A TOC-less reader finds the next file by searching forward for the next valid
  header frame of the same SetId. A per-file trailer can be added later as a new skippable kind.

### 5.4 Block-framed headers

Tags 1–3 mean the same in every header kind (inherited from one shared schema), so identity reads without knowing
the kind. The record kind *is* the header kind; `TapeHeaderKind` is the in-memory enum.

| # | Field | Media header | Set header | Calibration run header |
|---|---|---|---|---|
| 1 | Id (R) | MediaId | MediaId | RunId |
| 2 | CreatedUtc (R) | creation | set creation | run start |
| 3 | BlockSize (R) | TOC block size | set block size (advisory, SH-11) | run block size |

**Media header (`0x0301`)**

| # | Field | Type | Default |
|---|---|---|---|
| 4 | Volume | varuint | 1 |
| 5 | Partition | varuint | Content |
| 6 | TocPlacement | varuint | InSet |
| 7 | HasSetHeaders | bool | false |
| 32 | OriginalName | string | "" ⇒ none — clamped to 15 KiB |

**Set header (`0x0302`)**

| # | Field | Type | Default |
|---|---|---|---|
| 4 | Volume | varuint | R |
| 5 | VolumeSetIndex | varuint | 0 |
| 6 | GlobalSetIndex | varuint | R |
| 7 | SetId | guid | empty — legacy and stand-alone headers carry none |
| 32 | Description | string | "" ⇒ none — clamped to 15 KiB |

`ClassifySetHeader` checks identity, then position, then the set's own identity: **SetIdMismatch** when both SetIds
are known, differ, and the position matches. Drift outranks it (a drifted landing stands on another set, and drift is
correctable); a replacement set with no files yet is exempt (its header still belongs to the set being replaced). Not
repairable by repositioning — terminal on every path.

**Calibration run header (`0x0303`)**

| # | Field | Type | Flags |
|---|---|---|---|
| 4 | CapacityReportedAtBom | varuint | R |
| 32 | ProfileKey | string | "" |
| 48 | Plan | group | R |

Plan group, all required: 1 SampleCount, 2 BodySampleCount, 3 TailSampleCount, 4 BlockSize, 5 BlocksPerChunk,
6 ChunkSize, 7 TailBlocksPerChunk, 8 TailChunkSize, 9 TailCapacityFraction (f64), 10 NumCheckpoints. The wire class
wraps the immutable plan and updates it with `with`, so each line binds to the plan property's own type.

### 5.5 Calibration checkpoint (`0x0304`)

| # | Field | Type | Flags |
|---|---|---|---|
| 1 | RunId | guid | R |
| 2 | Index | varuint | R |
| 3 | BytesWritten | varuint | R |
| 48 | Samples | bytes | delta-coded |
| 49 | EarlyWarning | group | absent as a whole ⇔ no EW seen; 1 ActualWritten R, 2 ReportedRemaining R |

- **The EW point is a group, not two optional scalars:** LTO-3 reports remaining **0 exactly at EW**, and elision (F9)
  would drop a 0.
- **Delta-coded samples** (`TapeSampleCoder`): varuint count, then per sample varuint ΔActualWritten (non-decreasing,
  else `ArgumentException` on write) and varint ΔReportedRemaining, the first from (0, 0). A few bytes per sample
  instead of 16; the gain is cadence-dependent (≈1.6–3×: ~30 GiB deltas at an LTO-9 cadence need 5-byte varuints).
  A count larger than half the remaining bytes is refused before allocating (F10).

### 5.6 Virtual media state (`0x0F01`)

Host-side only: the `.vrt` metadata file of a virtual medium, one inline frame from position 0, the file truncated
behind it.

| # | Field | Type | Flags |
|---|---|---|---|
| 1 | MinBlockSize | varuint | R |
| 2 | MaxBlockSize | varuint | R |
| 3 | DefaultBlockSize | varuint | R |
| 4 | Capacity | varuint | R |
| 5 | BytesWritten | varuint | R — cross-check |
| 6 | BlockCount | varuint | R — virtual-block entries, cross-check |
| 7 | TotalLogicalBlocks | varuint | R — cross-check |
| 32 | Name | string | "" |
| 48 | Blocks | bytes | R — run-coded list |

**Run-coded block list (F27):** one entry per virtual block, `Kind:u8 [BlockSize:varuint BlockCount:varuint]` (the
bracket for data only); kinds 0 Data, 1 Filemark, 2 Setmark. A data entry costs 3–7 bytes, a mark 1 (legacy: 30). Block
positions and stream offsets are **rebuilt** on load — both are contiguous by construction — and checked: entries,
total logical blocks and bytes written must match the stored totals, each block size must lie in [Min..Max], and the
content stream may be **longer** than described (a torn write leaves unreferenced bytes) but never shorter.

**Never wipe (F26).** `TryCreateFromState` reports `Absent`, `Loaded` or `Unreadable`. On `Unreadable` the backend's
`LoadMedia` fails with `ERROR_FILE_CORRUPT` and the reason, for content and initiator media alike — whatever the
`MediaMode`, except `Create`, which is an explicit start-over. A virtual cartridge saved by this build and opened by a
build older than format 2.1 is still wiped by that older build; virtual tapes are developer artefacts.

**Legacy metadata** (`TF` signature, version ≤ 0x0100) reads through `LegacyVirtualMediaState`, which refuses a
non-contiguous layout rather than repairing it and drops emptied data runs as the 2.1 writer does. It is upgraded
lazily: the first save after a change writes 2.1; a read-only open leaves the file as it was.

### 5.7 Blocks and padding

| Record | Block | Padding | Trailing mark |
|---|---|---|---|
| Media header | standard 16 KiB header block (`TapeHeaderBlock`) | zeros | **one filemark** — begin-of-content is a post-mark write position |
| Set header | standard header block, first block of the set | zeros | none (SH-2) |
| Calibration run header | standard header block; the run block on drives whose maximum block is < 16 KiB | zeros / random | — |
| Checkpoint | one run block, after a filemark | random | — |
| TOC copy | 16 KiB TOC blocks | — | one filemark per copy |

`TapeHeaderBlock` sets the drive's block size for the header and restores it afterwards; a frame that does not fit is
a programming error. The TOC uses the same 16 KiB block, so one 16 KiB read always spans a whole header or a TOC's
first block — the basis of identification (§6).

---

## 6. Identification

`TapeHeaderBlock.IdentifyBlock(block, length)` — pure, total, TOC-free (F23):

```
bytes[0..3] == "TpN#"
  prologue does not parse     → DamagedRecord (NotFramed)
  kind TocHeader              → TocCopy (version, MediaId) when TapeTOC.TryPeek parses, else DamagedRecord (Unparseable)
  kind FileHeader             → Foreign: content — the first file of a set at a block boundary
  kind CalibrationCheckpoint  → CalibrationCheckpoint when the frame verifies, else DamagedRecord
  any other kind              → Header when the frame verifies and parses, else DamagedRecord with the status
                                (an unknown kind with an intact CRC is "ours, from a newer TapeNET")
otherwise
  → LegacyIdentify: a structurally verified legacy TOC first, then the signature at offset 4 + the legacy framer
```

- **`TapeTOC.TryPeek`** identifies a 2.1 copy from its first block alone: magic, `TocHeader` as the first record, this
  major, body within the block, and a body that parses. `TocVersion` = `0x0201` for this build's copies; legacy copies
  report `0x0101` / `0x0102` (`LegacyTocVersionMax`).
- Random data passes the magic once in 2³² blocks, then must pass kind, version, bounds and a CRC-64 (or a full
  `TocHeader` parse).
- **Scan Media** turns `Foreign` blocks into `SetContent` fragments when they hold a CRC-verified file header frame —
  the first file of a header-less 2.1 set, carrying its SetId (Design-ScanMedia §4.2). A damaged frame stays `Unknown`:
  a body may begin with the magic by chance.
- **Not identifiable:** a record damaged *in* its magic (reads as foreign); a TOC copy from a newer major (reads as
  damaged).
- `CarriesRecordSignature` answers "is this ours at all?": `TpN#` at 0, or the legacy signature at 0 or 4.

---

## 7. Code Architecture

### 7.1 Layering

```
Domain types    TapeTOC · TapeSetTOC · TapeHeader… · TapeCalibrationCheckpoint · VirtualMediaStateRecord · TapeFileHeader
     │  implement ITapeRecord<T> / ITapeFramedRecord<T> — the BODY only, through a schema
Wire DTOs       mutable mirrors of immutable domain types (G4): TocHeaderWire, TapeSetTOC.Wire, TocFileEntryWire,
     │          TapeMediaHeaderWire, TapeCalibrationPlanWire, …
Schema          TapeSchema<T> — one line per field, both directions
Fields          TapeFieldWriter / TapeFieldReader — tag · length · value, nested groups
Records         TapeRecordWriter / TapeRecordReader / TapeRecord — the prologue
Carriers        TapeFrame (block / inline) · TapeCrc64Envelope (TOC stream) · TapePeekStream
```

Each layer knows only the one below; domain types never touch prologues, lengths or CRCs.

### 7.2 Format core (`TapeLibNET/Format/`)

| Type | Role |
|---|---|
| `TapeFormat` | magic, Major 2, Minor 1, limits, `IsV2(span)`, skippable bit |
| `TapeRecordKind` | §4.3 |
| `TapeFormatException`, `FormatErrorKind`, `TapeRecordInfo` | errors with record / field / offset (G8); `HResult` = the mapped Win32 code |
| `TapePrimitives` | span encode / decode, strict UTF-8, `ToUtcTicks`, `ClampToUtf8Bytes` |
| `TapeFieldWriter` | typed writes; one reusable child writer per nesting depth (`BeginGroup` / `EndGroup`) |
| `TapeFieldReader` | `MoveNext`, typed reads, `ReadGroup`, `SkipUnknown`, `Error` |
| `TapeSchema<T>`, `ITapeField<in T>`, `FieldFlags`, `FieldShape` | §7.4 |
| `ITapeRecord<TSelf>` | `RecordKind`, `WriteBody`, static `Accepts`, static `ReadBody` |
| `ITapeFramedRecord<TSelf>` | + static `TryReadLegacy(block, out record)` — a one-line forward into `Legacy/` |
| `TapeRecordWriter` | `Write(record)`, `BeginRecord` / `EndRecord` / `AbandonRecord`; one `Stream.Write` per record from a pooled buffer whose reserved front gap takes the prologue — no measuring pass |
| `TapeRecordReader`, `TapeRecord` | `ReadRecord()` returns one bounded record (null only at a clean end), skipping unknown skippable kinds; `Read<T>` checks `Accepts`; span `Parse` / `ParsePrologue` |
| `TapeFrame`, `TapeFrameStatus` | §4.5 |
| `TapeFrameWriter` | reusable Record ‖ CRC-64 builder; the static `TapeFrame` methods run through a temporary one |
| `TapeCrc64Envelope` | runs a writer / reader against a hashing wrapper, then writes / verifies the trailer |
| `TapePeekStream` | peeks the magic and replays it, so a forward-only stream can be dispatched by format |
| `TapeNameFrontCoder`, `TapeSampleCoder` | §5.1, §5.5 |

**Polymorphic records.** `TapeHeader` implements `ITapeFramedRecord<TapeHeader>` for all three header kinds: `Accepts`
lists them and `ReadBody` dispatches on `fields.Record.Kind`. `RecordKind` is an instance member for that reason.

### 7.3 TOC stream I/O

```csharp
public void SaveTo(Stream stream);                                               // stamps now + this build
internal void SaveTo(Stream stream, DateTime saveTimeUtc, string writtenBy);     // deterministic — tests, goldens
public static TapeTOC LoadFrom(Stream stream);                                   // either format
public static bool TryPeek(byte[] block, int length, out ushort version, out Guid mediaId);
```

- **One wire object per TOC** serves every file entry on write and on read; safe only because of F31.
- **`LoadFrom`** peeks the magic: 2.1 → envelope + record loop; anything else → `LegacyTocReader` (§8.2). Either way
  every loaded set counts as saved (§10.2).
- **The agent's TOC I/O** is `RecordEmitter.WriteToc(TOC, wstream)` and `TOC.CopyFrom(TapeTOC.LoadFrom(rstream))`.
  `RestoreTOCCore` maps a `TapeFormatException` to `ERROR_CRC` or `ERROR_INVALID_DATA` and the caller tries the other
  copy (F15). `.tapetoc` files use the same pair.

### 7.4 Schema tables

```csharp
private static readonly TapeSchema<Wire> s_schema = new(TapeRecordKind.TocSet, validate: Validate)
{
    // ── scalars 1–31 ──
    { 1,  w => w.SetId,      (w, v) => w.SetId = v },
    { 2,  w => w.CreationTime, (w, v) => w.CreationTime = v, FieldFlags.Required },
    { 6,  w => w.DataFormat, (w, v) => w.DataFormat = v, TapeDataFormat.V2, FieldFlags.Critical },
    // ── strings 32–47 ──
    { 32, w => w.Description, (w, v) => w.Description = v },
};
```

- **The property's type picks the codec (G1).** A lambda converts to a delegate only if its body compiles: for a
  `uint` property the `ulong` overload's setter fails to compile, so only the `uint` overload binds. Enums bind only to
  the generic overload.
- **Default before flags**, each type with and without a default. Enum defaults are written as named members. A bare
  `0` as fourth argument binds to the default — write `FieldFlags.None` or omit it.
- **Defaults when none is given:** numbers 0, `false`, `Guid.Empty`, `TapePrimitives.MinUtc`, "" for strings, an empty
  array for bytes (absent bytes read back as empty, never null), null for a group.
- **Signed integers** use `AddSigned` (ZigZag); everything else is non-negative (G5). `byte`, `ushort`, `uint` and
  `int` reads are range-checked.
- **Groups:** single (absent ⇒ null, F31) or repeated (each occurrence appended — read them into a fresh target).
  `AddCustom` is the escape hatch for fields that are not 1:1 with a property; it declares its shape so freezing still
  validates it.
- **Inheritance:** `inherits:` merges a base wire type's fields — the shared header tags 1–3. It works because
  `ITapeField<in T>` is contravariant.
- **Validate hook** runs on write (G10, `InvalidOperationException`) and read (`CrossCheck`).

### 7.5 The record emitter

```csharp
internal ITapeRecordEmitter RecordEmitter { get; set; } = TapeRecordEmitter21.Instance;   // TapeAgentBase
```

`ITapeRecordEmitter` is the one place an agent turns a record into bytes: `DataFormat`, `WritesCodecPrefix`,
`WriteFileHeader`, `WriteToc`, `FrameHeaderBlock`. Positions, marks, packing and volume handling stay with the
navigator and the packer. The product's emitter writes 2.1; the test project plugs in a legacy emitter (§8.4) — the
seam exists for that alone.

`RecordEmitter` defaults to the agent's own `TapeRecordEmitter21`, created and disposed with the agent; it owns a `TapeFrameWriter` and one file-header wire object, so writing a file header allocates nothing. Setting null returns to it; a borrowed emitter is never disposed.

- `TapeFileBackupAgent.EnsureSetFormat` stamps a set with the emitter's format before its first file (F4), mints a
  SetId for V2 / clears it for legacy, and refuses software compression without a codec prefix (a legacy body has no
  codec byte).
- `BackupTOC` sets `LoadedFromLegacy` to "the emitter writes legacy" — false in the product (F28).

---

## 8. Legacy

### 8.1 `TapeLibNET.Legacy` — frozen, read-only, internal

| Type | Reads |
|---|---|
| `LegacyDeserializer` | the old binary layout; exact reads (short reads retried, not mistaken for the end) |
| `LegacyTocReader` | TOC 0x0101 / 0x0102, set 0x0101, file entry 0x0101 — two layouts (§8.2) |
| `LegacyFramer` | `[int32 len][payload][crc32]`, CRC over the payload only; a parse failure behind a good CRC is `Unparseable` |
| `LegacyHeaderReader`, `TapeHeaderPreamble` | the shared preamble, then media / set / calibration bodies; a media header written before `HasSetHeaders` existed reads `false` |
| `LegacyCheckpointReader` | checkpoints and the 10-field plan; the oldest stand-alone run header is not read (none in the field) |
| `LegacyFileHeader` | the 12-byte `TF` + 0x0101 + u64 UID file header |
| `LegacyIdentify` | the structural TOC probe and the offset-4 framed-record probe |
| `LegacyVirtualMediaState` | the old `.vrt` (§5.6) |
| `LegacyTime` | local → UTC conversion (F25) |
| `LegacyFormat` | the `TF` signature and version 0x0101 — shared with the test writers |

Every reader maps into the same in-memory model the 2.1 readers produce. Loaded legacy sets get
`DataFormat = Legacy`, `SetId = Guid.Empty`, FileId = the old UID, NextFileId = max + 1; the TOC reports
`LoadedFromLegacy`.

**Legacy times (F25).** TOC, set and file times and media / set header times were written as local ticks: converted
with the reading machine's zone. Calibration headers and checkpoints were written as UTC ticks: only the kind is
restored. An ambiguous local time (the repeated DST hour) resolves to standard time; an invalid one (the skipped hour)
is shifted forward instead of throwing — a legacy tape must always load. Zero ticks stay "not set".

### 8.2 Two legacy TOC layouts under one version

- **A — pre-compression:** the set ends after `ContinuedFromPrevVolume`; the file entry after `SizeOnTape`.
- **B — current:** + `Compression`, `CompressionLevel` (2 × int32) per set; + one codec byte per file.

`LegacyTocReader.Load` buffers the copy (F24), tries **B then A**, and accepts the layout whose parse ends exactly where
the CRC-64 of the consumed bytes equals the next 8 bytes. A B-parse must additionally carry *defined* `Compression` and
`Codec` values — software compression and Zstd are legitimate in layout B. Outcomes: not a TOC → `BadMagic`; parses but
the CRC disagrees → `CrcMismatch`; unreadable in both layouts → `BadValue`. All three mean "this copy is bad" (F15).

`LegacyIdentify.TryPeekToc` accepts a first block only when it reads as a TOC: known version, non-zero UID seed,
MediaId from 0x0102 on, a set count in 0..100,000, and the first set's own signature — or, for an empty TOC, a
plausible tail (dates in 1990–2200, volume ≥ 1), which rejects a legacy file record at block 0.

### 8.3 Legacy-set file headers

A set's `DataFormat` selects the restore check: 2.1 frame + codec byte (§5.3), or `LegacyFileHeader.Matches` for the
12-byte header, whose UID must equal the FileId. A legacy body has no codec byte; its codec comes from the TOC alone.
Size estimates follow the same split (`EstimateFileOverhead`: 12 B, or the 2.1 bound).

### 8.4 Legacy writers — test project only

| Test helper | Role |
|---|---|
| `TapeSerializer`, `LegacyFormatWriter`, `LegacyTocWriter`, `LegacyHeaderWriter`, `LegacyFileHeaderWriter` | the old write half, verbatim — layout A and pre-MediaId switches included |
| `LegacyRecordEmitter` | an `ITapeRecordEmitter` on those writers: the real agents, navigator and packer write **genuine legacy tapes** for any profile, header mode or multi-volume series |
| `LegacyVirtualImages` | the frozen Phase 0 virtual images and their deterministic source files |

Pinned twice: the writers reproduce the legacy record goldens byte for byte, and the emitter reproduces the Phase 0
virtual images (`Emitter_MatchesPhase0Image`) — the record bytes by the goldens, the layout by the images the old build
wrote.

### 8.5 Dropping legacy later

Five seams hand off to legacy code: TOC load, block-frame unpack, block identification, the legacy-set file header
check, and the calibration header probe. Removing `Legacy/` and those branches is one change; keep
`TapeDataFormat.Legacy` so such sets are then refused cleanly. The product's promise is that old cartridges stay
readable — the readers stay.

---

## 9. Mixed Media

### 9.1 Per-set data format

| Set written by | DataFormat | File header | Set header |
|---|---|---|---|
| a legacy build | Legacy | 12-byte legacy | legacy frame |
| this build | V2 | 2.1 inline frame + codec byte | 2.1 block frame |

An **empty** legacy set (a trailing slot of a legacy TOC) is upgraded to V2 in place before a backup writes into it; a
legacy set holding files cannot take new ones (F4, `ERROR_INVALID_STATE`).

### 9.2 Appending to a legacy tape — the upgrade

- The TOC loads through the legacy reader; every set is `Legacy`.
- The new set is written in 2.1; the legacy media header stays untouched and its `HasSetHeaders` still governs the
  volume (F3).
- The TOC is written as 2.1. From then on, legacy builds cannot read the tape's TOC.
- **One warning, never a prompt** — unattended runs must not stall. Every verb that writes the TOC goes through
  `TapeServiceBase.SaveTocCore` (backup, rename media, rename set); delete, whose agent writes the TOC itself, captures
  the flag before and calls `ReportTocUpgrade` after. The warning fires only if the TOC *was* legacy and *no longer is*
  (F28), so a failed write stays silent and a second save in the same operation (the next volume) too.
- Format and the initial TOC need neither: they write a TOC that was never legacy.
- Logs show the TOC format and each set's data format (`DescribeTocFormat`, `DescribeDataFormat`, `[Legacy]` flags).

### 9.3 Multi-volume series

Each volume's TOC is rewritten when written, so a series may carry legacy TOCs on early volumes and 2.1 TOCs on later
ones; both readers produce the same model. A continuation set is stamped by the emitter writing *its* volume, so a set
may continue from a legacy volume onto a 2.1 volume — covered by `MixedSeries_LegacyVolume1_V21Volume2_RestoresAcrossBoth`.
A continuation volume written by this build gets a 2.1 media header with the series MediaId.

### 9.4 `.tapetoc` files

Import: both formats. Export and emergency export: 2.1 only — and neither clears `LoadedFromLegacy` (F28).

### 9.5 Calibration

| On the cartridge | Read by |
|---|---|
| legacy run header (standard or run block) | legacy framer |
| 2.1 run header (either block) | 2.1 framer |
| legacy / 2.1 checkpoints | per block, `TryUnpackWithLegacy` |

Resume and Recalibrate of a legacy run work unchanged. The resume rewrites the boundary checkpoint in 2.1, so a resumed
legacy run carries a **mixed trail**, and `FindLastCheckpoint` classifies every block on its own. Legacy checkpoints
carry no kind of their own; identification reports them as `DamagedRecord`, as it always did.

### 9.6 Scan Media

The scanner parses nothing itself: it goes through `IdentifyBlock`, `TryPeek` and `RestoreTOCAt`. Legacy and 2.1
records are therefore identified by the same walk; a recovered TOC copy of ours makes a cartridge a backup cartridge
even when its legacy sets carry no identifiable start. Details: Design-ScanMedia.

### 9.7 Full volumes on partitioned media

On TOC-in-partition media a volume can fill up while its last set is still being written. The drive then reports
end-of-media and the closing setmark cannot be written. The TOC records this on the set (ClosedByEom, F32) rather than
recovering it later: the mark is not on the tape, and no scan can bring it back.

- **Agent.** End-relative set indexes on such a volume shift by one setmark (-1); the agent translates, the navigator
  never knows.
- **Append.** A full-volume guard refuses to append behind a set closed by end-of-media; the series continues on the next
  volume.
- **Restore.** Reading tolerates end of data in place of the missing mark.
- **Scan.** An unclosed last set that a recovered TOC records as closed by end-of-media is judged "runs to the end of the
  medium - the volume is full", without review advice (Design-ScanMedia §9.1).

---

## 10. Behaviour Details

### 10.1 UTC in memory

`TapeFileDescriptor` captures and applies the `…Utc` file times; TOC and set times are `DateTime.UtcNow`; the
incremental up-to-date check compares against `LastWriteTimeUtc`. Displays (service logs, WPF panes, list output)
convert with `ToLocalTime()`; FCL keeps local times by its specification, so `FclTapeFileFilter` converts before
evaluating. A backup restored in another zone or across a DST change keeps its times.

### 10.2 Save stamping

`SaveTo` stamps the TOC's `LastSaveTime` and `WrittenBy`, and the `LastSaveTime` of every set that **changed since it
was last saved or loaded**. "Changed" compares a snapshot of every persisted attribute (`PersistedState`) — no setter can
be forgotten, and the domain class needs no change notifications. The snapshot is taken after the whole stream went out,
so a failed save leaves the sets "modified" for the next attempt; `LoadFrom` and `CopyFrom` carry it, so a set loaded
from tape and copied into the live TOC is not restamped.

### 10.3 Size estimates

- **TOC footprint per entry** (`EstimateSerializedSize`): 120 B fixed + name bytes (UTF-16 bytes for an ill-formed
  name) + hash — deliberately conservative (no shared prefix, maximum varint widths); it sizes the early-warning
  reserve for the TOC.
- **File overhead** per data format (§8.3). Committed files carry their exact `SizeOnTape`.

### 10.4 WrittenBy

`"<entry app> <version> / TapeLibNET <version>"`, or the library part alone when there is no distinct entry assembly.
Informational only.

---

## 11. Counterintuitive Points

| Point | Why |
|---|---|
| **A corrupt TOC copy rarely fails on its CRC.** | The envelope parses before it compares, so damage usually surfaces as `MissingRequired`, `Overrun` or `CrossCheck`. Callers must treat *any* `TapeFormatException` as "this copy is bad" (F15). |
| **The checkpoint's EW point is a group.** | A field at its default is elided — and LTO-3 reports remaining 0 exactly at EW (§5.5). |
| **`Attributes` is a bitmask field.** | `Archive \| ReadOnly` is not a defined `FileAttributes` member; without `Bitmask` every TOC would be unreadable (F8). |
| **A file header at a block boundary is `Foreign`.** | It is content — a set's first file — not a damaged or newer record (§6). |
| **Optional bytes read back empty, not null.** | The setter is non-null; `ToRecord` maps an empty hash to "no hash". |
| **A required `int` field cannot be negative, and a required field cannot have a non-zero default.** | Both are refused when the schema freezes or on write (G5, §4.4) — they would be contradictions on the wire. |
| **Unspecified `DateTime` is taken as UTC.** | Shifting it by the machine zone would make the bytes depend on where they were written (F20). |
| **`DataFormat`, `Codec` and the UTF-16 name are critical.** | A reader that drops them would restore garbage: a wrong header size, an undecompressed body, a different file name. |
| **The front coder remembers a UTF-16 name it did not encode.** | Writer and reader both call `Remember`, so the next entry's shared prefix stays in step. |
| **One `TocFileEntryWire` reads a whole TOC.** | Safe only because absent fields are reset (F31) — a hash cannot leak from one entry into the next. |
| **`TapeFieldWriter` children are reused per depth.** | A batch of 4,096 entries allocates no group buffer per entry. |
| **The legacy TOC reader buffers the whole copy.** | It must try two layouts over the same bytes, from a stream that may not seek (F24). |
| **`SaveTo` and `BackupTOC` treat `LoadedFromLegacy` differently.** | A `.tapetoc` export upgrades nothing on tape; only a copy that reached the tape does (F28). |
| **A media header is followed by a filemark; a set header is not.** | Begin-of-content is a repeated write entry point and must be post-mark; a set header is always the first write at an already legal position (SH-2). |
| **Delete writes the TOC inside the agent.** | Hence the separate `ReportTocUpgrade` call instead of `SaveTocCore` (§9.2). |
| **The v0x0102 legacy probe's `LegacyTocVersionMax` is not `TocVersion`.** | `TocVersion` is 0x0201; using it would open the frozen probe to versions no legacy build wrote. |
| **An unreadable `.vrt` fails the load.** | Recreating the medium over it was the old behaviour — and silently destroyed the virtual cartridge (F26). |
| **A full partitioned volume shifts every end-relative index by one.** | The agent translates; the navigator never knows (§9.7, F32). |

---

## 12. Tests and Goldens

### 12.1 Goldens

| Set | Content | Pinned by |
|---|---|---|
| `Golden/Legacy/` | 12 record goldens: TOC layouts A / B / pre-MediaId, media headers with / without flag, set headers with / without description, calibration headers (standard / run block), checkpoints (EW + many samples, no EW), legacy file header — each with `.expected.json` (length + SHA-256) | `LegacyGoldenTests`: the readers load them; the test writers reproduce them byte for byte |
| `Golden/Legacy/Virtual/` | the Phase 0 virtual images (setmarks; partitions with initiator) and `legacy.tapetoc` — **frozen and irreplaceable**; their generator is removed | `LegacyVirtualGoldenTests`, `VirtualImage_IsGenuinelyLegacy`, `Emitter_MatchesPhase0Image` |
| `Golden/V21/` | 11 cases (`GoldenV21`): TOC with four sets (full, incremental continuation, legacy-in-2.1, empty), TOC across the 4,096-file batch boundary, file header, media header rich / plain, set header, calibration header, checkpoints with EW 0 / without EW / 1,000 samples, TOC with a set closed by end-of-media (`toc-closed-by-eom.v21.bin`) | `FormatGoldenV21Tests` |

**Why 2.1 goldens.** A round trip proves that writer and reader agree with *each other*; change both the same way and
every round trip stays green while every 2.1 tape already written becomes unreadable. Goldens pin the bytes.

| Test | Meaning |
|---|---|
| `Golden_Reads` | today's reader reads the frozen bytes into the values they came from — a promise (F30) |
| `Golden_WriterReproduces` | today's writer still produces those bytes — a tripwire: a bug, or a deliberate change that needs a *new* case |
| `Golden_IsUntouched` | the checked-in file is the one generated |

`GoldenV21Generator` is skipped by default and writes only missing goldens — it can never overwrite one. Inputs are
fixed in code (GUIDs, UTC times, names, seeded hashes); TOCs are written through the deterministic `SaveTo` overload, so
the save stamping (§10.2) is pinned too. **Freeze point:** until the 2.1 release a golden may be regenerated after a
deliberate change (delete, regenerate, commit with the reason); from the release tag on, append only.

### 12.2 Suites

| Area | Tests |
|---|---|
| Format core | `FormatCoreTests`, `FormatCoreCoverageTests` — primitives, prologue, fields, schema validation and overload binding, errors with context, frames (every status), no read-ahead on forward-only streams, envelope, allocation-free batch writes |
| TOC | `TapeTOCFormatTests`, `TapeTOCFormatMigrationTests`, `TapeTOCRoundTripTests` — batches, cross-checks, front coding, legacy → 2.1 → reload, time-zone independence, 2.1 smaller than legacy |
| Headers / identification | `TapeFileHeaderTests`, `TapeHeaderFormatTests`, `TapeHeaderRoundTripTests`, `TapeMediaIdentifyTests` |
| Calibration | `CalibrationFormatTests` — 2.1 records, mixed trails, legacy runs |
| Virtual media | `VirtualMediaStateTests`, `VirtualTapeDriveBackendLoadTests` — rebuild, cross-checks, never-wipe, lazy upgrade |
| Legacy | `LegacyGoldenTests`, `LegacyTimeReaderTests`, `LegacyVirtualGoldenTests`, `LegacyTapeUpgradeTests`, `LegacyEmitterTests` |
| Scenarios × profiles | `FormatScenarioTests` (self-describing headers, codec mismatch, FileId burn, SetId rules, `.tapetoc` both ways), `ServiceMixedMediaTests`, `ServiceLegacyMediaTests` (upgrade warning per verb, legacy and mixed scans) |

The whole suite (about 5,000 tests) saves and reloads virtual media state constantly, so every test also exercises the
`0x0F01` record.

---

## 13. Encryption

Encryption is additive and lands as part of the 2.1 rollout. It adds `TocSet` fields — `Encryption` (scalar,
**critical**), a key envelope (group, 48+), sealed-summary scalars, sealed batches — and a critical `Sealed` field on the
file header, whose fields 3–32 then travel encrypted under the set key (AAD = `SetId ‖ FileId`). The codec byte sits
inside the encrypted body; encrypted sets require `DataFormat = V2`. A build without encryption refuses an encrypted set
(F7). Unencrypted records stay byte-identical, which the 2.1 goldens verify; encryption adds its own golden cases.
Specified in Design-Encryption (its §7, an extension record, is superseded).

---

## 14. Known Limits and Open Items

| Item | Note |
|---|---|
| Legacy-shape calibration headers in the run block | identified by the calibrator's own probe; the scanner maps them as foreign. |
| Per-file trailer | body length + hash after each body, for TOC-less recovery — a future skippable record kind, no format change. |
| Mid-block file headers | locating packed files after a set's first needs a forward search inside blocks; whole-block reads only today. |
| `LegacyGoldenTests` generator | unlike `GoldenV21Generator`, check that it never overwrites an existing golden. |

---

## 15. Decisions Log

| Topic | Decision |
|---|---|
| Version fields | u8 major + u8 minor, top-level records only |
| Magic + kind | common `TpN#` + u16 kind; `0x8000` = skippable |
| Integrity | CRC-64 (ECMA-182) everywhere |
| Field order | number ranges 1–31 / 32–47 / 48–63; ascending emission; no reserved slots |
| Nested records | groups without prologue |
| TOC streaming | bounded file batches; records built in a pooled buffer, no measuring pass |
| Identity | SetId per set, FileId per file, NextFileId per set |
| File header | self-describing frame with the full path; codec byte at body start |
| Integers | non-negative varuint; signed only where stated |
| Timestamps | UTC; Unspecified taken as UTC, never shifted |
| Schema API | declarative tables; default before flags; validate on read and write; groups via Begin / End |
| Legacy writer | test project only, pinned to goldens; legacy tapes produced through the emitter seam |
| Oldest calibration run header | dropped — no such cartridges in the field |
| Aligned (block-per-file) agent API | removed; it had fallen behind BackupRead / BackupWrite and compression. Legacy aligned sets restore through the packed path; `TapeStreamManager`'s raw streams stay for TOC I/O and tests. `BufferedTapeRead/WriteStream` deleted. |
| Virtual media state | a 2.1 record; legacy metadata readable; never wiped on failure |
| Protocol Buffers | not used: it parses whole messages (a TOC would hit its 2 GB ceiling), has no critical-field concept, and a cartridge read decades from now should need only this document |
| 2.1 goldens | generated before encryption; frozen at the release tag |
