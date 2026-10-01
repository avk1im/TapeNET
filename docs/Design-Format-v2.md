# Design: On-Tape Format 2.1 — Clean Cut with Read-Only Legacy Support

**Status:** v4.1 — for implementation. **Branch:** `format-v2` — lands before `encryption`.
**Last updated:** 2026-10-01

**Augmented by***: `Design-Format-v2-AppendixA-VirtualMedia.md` (virtual media state, host-side metadata file).

---

## 1. Summary

Every record TapeNET writes from now on — TOC, per-file headers, media / set / calibration headers,
calibration checkpoints, virtual-media state — uses **format 2.1**. Legacy records (the `TF` + `0x01xx`
family) stay **readable forever** and are **never written again** by the product.

- **One prologue for everything:** `"TpN#"` magic, `u16` kind, `u8` major, `u8` minor, `varuint` body
  length. Recognition is one 4-byte compare at byte 0; the kind follows.
- **Tagged, skippable, refusable fields.** Unknown fields are skipped; unknown *critical* fields refuse the
  record. Adding a field never needs a version bump.
- **CRC-64 everywhere.** Framed headers, checkpoints and per-file headers carry their own CRC-64; the TOC
  stream keeps its single trailing CRC-64.
- **Self-describing files.** Every file on tape carries its `SetId`, `FileId`, full path, length,
  attributes and timestamps — the groundwork for restoring files from a tape whose TOC is lost.
- **Identity:** `SetId` (GUID) per set, `FileId` (counter) per file within a set. `NextUid` disappears.
- **UTC throughout** — on tape and in memory. Displays convert to local.
- **Classes write themselves** through a declarative schema table; the CRC envelope is owned by the TOC.
- **The aligned (block-per-file) agent API is removed.** It had fallen behind `BackupRead`/`BackupWrite`
  and compression. Legacy aligned sets on tape keep restoring through the packed path, as today.
- **Mixed media first-class.** A legacy tape's TOC upgrades to 2.1 on its first write; its legacy sets keep
  their 12-byte file headers and restore unchanged.
- **Assumption:** no legacy tape carries software-compressed data.
- **No new packages.**

---

## 2. Why a Clean Cut

| # | Legacy problem | Consequence |
|---|---|---|
| 1 | Nested records not length-delimited | A new field in `TapeSetTOC` / `TapeFileInfo` misaligns every following record |
| 2 | Fields appended without a version bump (compression, `HasSetHeaders`) | Several layouts share one version; readers guess (`ReadSetHeadersFlag` swallows exceptions) |
| 3 | `Deserialize<List,T>` drops `null` items after they consumed bytes | A rejected record silently misaligns the rest |
| 4 | One library-wide signature `TF` + version | A legacy file header and a v0x0101 TOC start identically — `TryPeek` needs structural guesswork |
| 5 | Framed records put the signature behind an `int32` length | "Is this ours?" requires trusting the length first |
| 6 | Strict version equality in nested records | No additive evolution |
| 7 | `DeserializeBytes` issues one `Read` | Short reads corrupt parsing on non-memory streams |
| 8 | Machine-endian primitives (`Unsafe.As`) | Undocumented byte order |
| 9 | `DateTime` as raw local ticks | Times shift across time zones and DST |
| 10 | Fixed-width integers, full paths per TOC entry, 16-byte calibration samples | Larger TOCs (eating the EW reserve); checkpoints outgrow their block sooner — and then a run stops being resumable |
| 11 | File header carries only a UID | A tape without a usable TOC is unrecoverable at file level |
| 12 | One `NextUid` counter per TOC | A TOC rebuilt from an older copy re-issues UIDs already on tape |

---

## 3. Principles

1. **Write 2.1 only** in the product. The legacy writer lives in the test project only (§7.3).
2. **Read legacy forever**, from one isolated, frozen namespace (`TapeLibNET.Legacy`), pinned by golden files.
3. **Never rewrite an old record in place** — a tape write truncates everything behind it. Old headers and
   checkpoints stay old.
4. **The TOC is the exception** — it is always rewritten whole, so it upgrades on the first write.
5. **A set's data format is fixed** once its content is on tape.
6. **Self-describing and fail-loud.** Every top-level record names its kind and version at byte 0. A record
   we cannot read is an error, never a silent skip.
7. **Extend by adding tags.** Only a field that changes interpretation needs the critical bit; only a
   layout break needs a new major.

---

## 4. Format Specification

### 4.1 Primitives

| Type | Encoding |
|---|---|
| `u8` / `u16` / `u32` / `u64` | little-endian (`BinaryPrimitives`) |
| `varuint` | unsigned LEB128, ≤ 10 bytes; overlong encodings refused |
| `varint` | ZigZag, then `varuint` |
| `f64` | IEEE 754 binary64, little-endian |
| `bool` | `u8` 0 or 1; anything else refused |
| `bytes` | `varuint` length, then raw bytes |
| `string` | `bytes`, UTF-8, validated on read |
| `guid` | 16 bytes, `Guid.TryWriteBytes` order |
| `timestamp` | `varint` UTC ticks; writers call `ToUniversalTime()`; readers return `DateTimeKind.Utc` |

All reads are read-until-full (`ReadExactly`). **Readers never read ahead** of the bytes they consume — the
TOC's trailing CRC and the file body follow immediately.

### 4.2 Record prologue

```
Record   := Prologue Body
Prologue := Magic "TpN#" (4)  Kind:u16  Major:u8  Minor:u8  BodyLength:varuint
Body     := Field*
Field    := Tag:varuint  Length:varuint  Value[Length]
```

- **Magic `TpN#`** is common to all record kinds. A legacy record starts with `TF` (TOC, file header) or with
  an `int32` length below 2²⁴ — whose byte 3 is `0x00`, never `#`. No legacy record can be read as 2.1.
- **Kind** — §4.3. The top bit (`0x8000`) marks a record **skippable** by readers that do not know the kind;
  any other unknown kind is refused.
- **Major.Minor** = `2.1`. Readers accept major 2 with any minor and refuse other majors (*"written by a
  newer TapeNET (format x.y)"*). Minor is informational: it names the release that introduced new critical
  tags, so error messages can say which version is needed (R7).
- **Version on top-level records only.** Nested field groups (file entries inside a batch, the calibration
  plan, later the key envelope) are covered by their parent's version and carry no prologue.
- **BodyLength** makes every record skippable: after reading, the reader stands at `bodyStart + BodyLength`,
  whatever it understood. Overrun and underrun are both refused.

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
| `0x0F01` | Virtual media state | host metadata file (not on tape) |

High byte = family: `01` TOC, `02` content, `03` headers, `0F` host-side.

### 4.4 Tags

```
Tag := (Number << 1) | Critical
```

| Number range | Holds |
|---|---|
| 1–31 | scalars (integers, bools, timestamps, GUIDs, enums) |
| 32–47 | strings and byte arrays |
| 48–63 | nested groups and bulk data |

- **Ranges keep "scalars first"** as fields are added: writers emit ascending tags, so a field added later
  still lands in its category. Every tag ≤ 63 encodes in one byte. Readers accept any order.
- **No reserved slots.** New features take the next free number in their range.
- **Numbers are never reused**; a removed field retires its number.
- **Unknown non-critical tag** → skipped. **Unknown critical tag** → record refused (*"uses feature N unknown
  to this build"*).
- **R3 — the critical bit belongs to the tag.** Readers match known fields by number, ignoring the bit;
  writers emit a field's bit consistently. Separately, **an unknown value of a known interpretation-driving
  enum** (`DataFormat`, `HashAlgorithm`, `Compression`, `Codec`, future `Encryption`) refuses the record.
- **Default elision applies to optional fields only.** Required fields are always written — an empty
  required string (e.g. `NameSuffix`) is written as length 0.
- **Duplicates** of a non-repeated tag are refused; a missing required field refuses the record.

### 4.5 Integrity and frames

**CRC-64 (`System.IO.Hashing.Crc64`, ECMA-182) everywhere.**

| Carrier | Layout |
|---|---|
| **Block frame** (headers, checkpoints) | `Record ‖ CRC-64(Record)` at block offset 0; rest of the block padded (zeros for headers, random for calibration run blocks — as today) |
| **Inline frame** (file header) | `Record ‖ CRC-64(Record)`, immediately followed by the file body |
| **TOC stream** | `Record*` ‖ one CRC-64 over all of them (§5.1) |

`TapeFramer.FrameStatus` keeps its four values:

| Status | Condition |
|---|---|
| `Ok` | magic + known kind, bounds fit, CRC matches, record parses |
| `NotFramed` | no `TpN#` at offset 0, or the declared length runs past the bytes available |
| `CrcMismatch` | bounds fit, CRC differs — torn or corrupt |
| `Unparseable` | CRC matches, but newer major, unknown kind, unknown critical tag, missing required field, bad enum value |

### 4.6 Bounds (R5)

Declared lengths never drive allocation unchecked:

| Limit | Value |
|---|---|
| `MaxStringBytes` | 128 KiB (a 32,767-char path ≤ ~96 KiB UTF-8) |
| `MaxBytesField` | 16 MiB |
| `MaxRecordBody` | 16 MiB |
| Group nesting depth | 4 |

### 4.7 Why not Protocol Buffers

Already shipped for gRPC, and its wire format has the same tag/length property — but it parses whole
messages (a TOC would hit its 2 GB ceiling and live fully in memory), has no critical-field concept (proto3
silently ignores unknown fields — the failure §4.4 prevents), and a cartridge read decades from now should
need only this document.

---

## 5. Record Catalog

Tag tables list `number`; `C` = critical; `R` = required. Defaults apply only to optional fields.

### 5.1 TOC stream

```
TocHeader
(TocSet  TocFileBatch*)*
TocEnd
CRC-64 over every byte above
```

- **Bounded records.** A batch closes at **4,096 files or ~1 MiB body**, whichever comes first. Every record
  is built in a pooled buffer and written in one go — no measuring pass, no seekable stream needed.
- **Batches are self-contained:** front coding restarts in each, so a partly damaged TOC can be salvaged
  batch by batch, and an encrypted set's sealed list is simply its batches.
- **`TocEnd` positively terminates** the sequence. Readers stop on it, never on "looks like no more sets"; a
  copy without it is refused before the CRC is compared.
- The TOC's first block begins with the `TocHeader` prologue at byte 0 — the identification anchor (§5.9).
  `TocHeader` is clamped to fit that block (R6).

**`TocHeader` (0x0101)**

| # | Field | Type | Flags / default |
|---|---|---|---|
| 1 | `MediaId` | guid | empty |
| 2 | `CreationTime` | timestamp | R |
| 3 | `LastSaveTime` | timestamp | R |
| 4 | `Volume` | varuint | 1 |
| 5 | `ContinuedOnNextVolume` | bool | false |
| 6 | `SetCount` | varuint | R — cross-checked |
| 32 | `Description` | string | "" — clamped to 8 KiB |
| 33 | `WrittenBy` | string | "" — e.g. `TapeWinNET 3.4.0 / TapeLibNET 3.4.0`, ≤ 256 B |

**`TocSet` (0x0102)**

| # | Field | Type | Flags / default |
|---|---|---|---|
| 1 | `SetId` | guid | R when `DataFormat = V2`; absent for legacy sets |
| 2 | `CreationTime` | timestamp | R |
| 3 | `LastSaveTime` | timestamp | R |
| 4 | `BlockSize` | varuint | R |
| 5 | `Volume` | varuint | R |
| 6 | `DataFormat` | varuint | C — `V2` (2); `Legacy` = 1 |
| 7 | `NextFileId` | varuint | R |
| 8 | `HashAlgorithm` | varuint | `Crc32` |
| 9 | `Compression` | varuint | `None` |
| 10 | `CompressionLevel` | varuint | `ZstdLevel.Default` |
| 11 | `Incremental` | bool | false |
| 12 | `ContinuedFromPrevVolume` | bool | false |
| 13 | `FileCount` | varuint | R — cross-checked against the batches that follow |
| 32 | `Description` | string | "" |

**`TocFileBatch` (0x0103)** — repeated field 48 `File`, a nested group:

| # | Field | Type | Flags / default |
|---|---|---|---|
| 1 | `FileId` | varuint | R |
| 2 | `AddressBlock` | varuint | R |
| 3 | `AddressOffset` | varuint | 0 |
| 4 | `Length` | varuint | 0 |
| 5 | `SizeOnTape` | varuint | 0 |
| 6 | `Attributes` | varuint | 0 |
| 7 | `Codec` | varuint | `Stored` |
| 8 | `CreationTime` | timestamp | R |
| 9 | `LastWriteTime` | timestamp | R |
| 10 | `LastAccessTime` | timestamp | R |
| 11 | `NameShared` | varuint | 0 — UTF-16 chars shared with the previous entry's full name in this batch |
| 32 | `NameSuffix` | string | R (may be empty) |
| 33 | `Hash` | bytes | absent |

**Front coding.** Consecutive names share long directory prefixes; each entry stores only the differing
tail. The first entry of every batch has prefix 0. The split never falls inside a surrogate pair. Implemented
once in `TapeNameFrontCoder` (stateful, `Reset()` per batch), used by writer and reader.

**`TocEnd` (0x0104)**

| # | Field | Type | Flags |
|---|---|---|---|
| 1 | `SetCount` | varuint | R — must equal sets read and `TocHeader.SetCount` |
| 2 | `TotalFileCount` | varuint | R — must equal the sum of `FileCount` |

Any cross-check mismatch refuses the copy; the second copy is tried as today.

### 5.2 Identity: `SetId` and `FileId`

| Situation | `SetId` | `NextFileId` |
|---|---|---|
| New set | freshly minted | 1 |
| Reuse of a trailing empty set (`AddNewSetTOC`) / `ReplaceCurrentSetTOC` | freshly minted | reset to 1 |
| Multi-volume continuation | **same** as the original set — one logical set | carried in `TapeSetTOCParams` |
| Legacy set (loaded) | `Guid.Empty` | legacy `UID` becomes `FileId`; `NextFileId` = max + 1 |

- `FileId` is allocated per attempt — retries, skips and EOM rollbacks burn numbers and never reuse them, so
  an orphaned attempt still on tape can never collide with the committed file. Gaps are harmless.
- `SetId ‖ FileId` is globally unique. Leftovers of an overwritten set beyond the new end keep their old
  `SetId` and can never be mistaken for the new set.
- `TapeFileInfo.IsValid` ⇔ `FileId != 0 && FullName != ""`.

### 5.3 File header (0x0201, inline frame)

Written before every file body; `SizeOnTape` covers frame + body.

| # | Field | Type | Flags |
|---|---|---|---|
| 1 | `SetId` | guid | R |
| 2 | `FileId` | varuint | R |
| 3 | `Length` | varuint | 0 |
| 4 | `Attributes` | varuint | 0 |
| 5 | `CreationTime` | timestamp | R |
| 6 | `LastWriteTime` | timestamp | R |
| 7 | `LastAccessTime` | timestamp | R |
| 32 | `FullName` | string | R — full path, **not** front-coded (one torn header must not break a chain) |

- **Restore check:** frame CRC, then `SetId == set.SetId && FileId == tfi.FileId`. A `FullName` mismatch is
  logged as a warning (TOC renames are conceivable later), not refused.
- **Codec byte.** The body starts with one `u8` codec (`0` Stored, `1` Zstd), written by
  `ProbingCompressionStream.Commit()` when it decides, and by the agent on the plain path. Restore reads it,
  requires it to equal the TOC's `Codec` (mismatch → `ERROR_INVALID_DATA`), then wraps
  `DecompressionFilterStream` as today. Under encryption the codec byte sits inside the encrypted body.
- **Not in the header:** `Hash`, `SizeOnTape`, `Codec` — unknown when the header is written; TOC-only.
- **Body end.** A body is a `BackupRead` blob with no end marker. A TOC-less recovery reader finds the next
  file by searching forward for the next valid file-header frame with the same `SetId` and a higher `FileId`.
  A per-file trailer (body length + hash) can be added later as a new skippable record kind — no format
  change.

### 5.4 Block-framed headers — shared tags

Tags 1–3 mean the same in every header kind, so identity reads without knowing the kind:

| # | Field | Type |
|---|---|---|
| 1 | `Id` (MediaId / RunId) | guid, R |
| 2 | `CreatedUtc` | timestamp, R |
| 3 | `BlockSize` (TOC / set / run block size) | varuint, R |

The legacy kind byte disappears — the record kind *is* the kind. `TapeHeaderKind` stays as the in-memory enum.

**Media header (0x0301)**

| # | Field | Type | Default |
|---|---|---|---|
| 4 | `Volume` | varuint | 1 |
| 5 | `Partition` | varuint | `Content` |
| 6 | `TocPlacement` | varuint | `InSet` |
| 7 | `HasSetHeaders` | bool | false |
| 32 | `OriginalName` | string | absent — clamped (15 KiB) |

**Set header (0x0302)**

| # | Field | Type | Default |
|---|---|---|---|
| 4 | `Volume` | varuint | R |
| 5 | `VolumeSetIndex` | varuint | 0 |
| 6 | `GlobalSetIndex` | varuint | R |
| 7 | `SetId` | guid | R |
| 32 | `Description` | string | absent — clamped (15 KiB) |

`ClassifySetHeader` gains a check: after `WrongMedia` and `WrongVolume`, if `VolumeSetIndex` matches but
both `SetId`s are non-empty and differ → new terminal verdict **`SetIdMismatch`** — the right position holds
a different set (a stale or foreign TOC). Not repairable by repositioning; blocks destructive writes, warns on
reads like `WrongVolume`.

**Calibration run header (0x0303)**

| # | Field | Type |
|---|---|---|
| 4 | `CapacityReportedAtBom` | varuint, R |
| 32 | `ProfileKey` | string |
| 48 | `Plan` | group, R |

`Plan` group (all R): 1 `SampleCount`, 2 `BodySampleCount`, 3 `TailSampleCount`, 4 `BlockSize`,
5 `BlocksPerChunk`, 6 `ChunkSize`, 7 `TailBlocksPerChunk`, 8 `TailChunkSize`, 9 `TailCapacityFraction` (f64),
10 `NumCheckpoints`.

### 5.5 Calibration checkpoint (0x0304)

| # | Field | Type | Flags |
|---|---|---|---|
| 1 | `RunId` | guid | R |
| 2 | `Index` | varuint | R |
| 3 | `BytesWritten` | varuint | R |
| 4 | `EwActualWritten` | varuint | present ⇔ EW seen |
| 5 | `EwReportedRemaining` | varuint | pairs with 4 |
| 48 | `Samples` | bytes | delta-coded |

**Delta-coded samples:** `varuint count`, then per sample `varuint ΔActualWritten` (monotone) and
`varint ΔReportedRemaining`, the first from `(0, 0)`. A few bytes per sample instead of 16 — the checkpoint
stays within its run block for several times longer, keeping long runs resumable (`RecordBlockWriter.Emit`
stops checkpointing once a frame outgrows the block).

### 5.6 Virtual media state (0x0F01)

S. the additional document `Design-Format-v2-AppendixA-VirtualMedia.md`.

### 5.7 Front coding, batching, schemas — illustration

**The schema table — one line per field, both directions.**

```csharp
public sealed partial class TapeSetTOC : ITapeRecord<TapeSetTOC>
{
    private static readonly TapeSchema<TapeSetTOC> s_schema = new(TapeRecordKind.TocSet)
    {
        // ── scalars 1–31 ──
        { 1,  s => s.SetId,        (s, v) => s.SetId = v,        FieldFlags.RequiredWhenV2 },
        { 2,  s => s.CreationTime, (s, v) => s.CreationTime = v, FieldFlags.Required },
        { 6,  s => s.DataFormat,   (s, v) => s.DataFormat = v,   TapeDataFormat.V2, FieldFlags.Critical },
        { 7,  s => s.NextFileId,   (s, v) => s.NextFileId = v,   FieldFlags.Required },
        { 8,  s => s.HashAlgorithm,(s, v) => s.HashAlgorithm = v, TapeHashAlgorithm.Crc32 },
        // …
        // ── strings 32–47 ──
        { 32, s => s.Description,  (s, v) => s.Description = v },
    };

    public void WriteTo(TapeRecordWriter w) => s_schema.Write(w, this);            // ascending tags, optional defaults elided
    public static TapeSetTOC ReadFrom(TapeFieldReader f) => s_schema.Read(f, new TapeSetTOC());
                                                     // by number; skip unknown; refuse unknown critical; check required
}
```

`TapeSchema<T>` offers one typed `Add` overload per primitive (§4.1) plus a generic one for enums — which is
what makes the collection-initializer form compile. No reflection. Fields that are not 1:1 with a property
(`Address` split, `FileDescr` members, front-coded name, constructor-only `TapeFileInfo`) use the same
lambdas against a small builder.

**The TOC owns its CRC envelope.**

```csharp
// TapeTOC
public void SaveTo(Stream stream)
    => TapeCrc64Envelope.Write(stream, s => WriteRecords(new TapeRecordWriter(s)));

public static TapeTOC LoadFrom(Stream stream)
{
    stream = TapePeekStream.Wrap(stream, out ReadOnlySpan<byte> magic);   // replays the peeked bytes
    return TapeFormat.IsV2(magic)
        ? TapeCrc64Envelope.Read(stream, s => ReadRecords(new TapeRecordReader(s)))
        : LegacyTocReader.Read(stream);                                  // own CRC + layout detection
}

private void WriteRecords(TapeRecordWriter w)
{
    WriteHeader(w);
    foreach (var set in m_setTOCs)
    {
        set.WriteTo(w);
        foreach (var batch in set.Batches(c_maxFilesPerBatch, c_maxBatchBodyBytes))
            batch.WriteTo(w);                                             // front coder reset per batch
    }
    WriteEnd(w);
}
```

The agent's TOC code shrinks to `TOC.SaveTo(wstream)` and `TOC.CopyFrom(TapeTOC.LoadFrom(rstream))`, with one
catch mapping `TapeFormatException` to `ERROR_CRC` / `ERROR_INVALID_DATA`. The same pair serves `.tapetoc`.

### 5.8 Legacy-set file headers

Legacy sets (`DataFormat = Legacy`) keep their 12-byte `TF` + `0x0101` + `u64 UID` headers on tape; restore
checks them with `LegacyFileHeader.Check(rstream, tfi.FileId)`. The size fallback for such sets
(`SizeOnTape == 0`) uses the legacy header size.

### 5.9 Identification (`TapeHeaderBlock.IdentifyBlock`)

```
bytes[0..3] == "TpN#"
  ├─ kind TocHeader         → 2.1 TOC copy (parse TocHeader from this block: version, MediaId)
  ├─ kind Media/Set/CalHdr  → block frame → Header | DamagedRecord
  ├─ kind CalCheckpoint     → calibration-checkpoint fragment
  └─ other kind             → DamagedRecord: "ours, from a newer TapeNET"
otherwise
  └─ LegacyIdentify          → today's logic, moved verbatim (TryPeek first, then offset-4 signature)
```

Random data passes the magic once in 2³² blocks, then must pass kind, version, bounds and a CRC-64 (or a full
`TocHeader` parse). A record damaged behind its magic reports as damaged, never as a phantom TOC.
`CarriesRecordSignature` accepts `TpN#` at 0 plus the legacy signature at 0 and 4.

---

## 6. Mixed Media

### 6.1 Per-set data format

| Set written by | `DataFormat` | File header | Set header |
|---|---|---|---|
| Legacy build | `Legacy` | 12 B legacy | legacy frame |
| New build | `V2` | 2.1 inline frame | 2.1 block frame |

### 6.2 Appending to a legacy tape

1. The TOC loads through the legacy reader (`TapeTOC.LoadedFromLegacy = true`); every set gets
   `DataFormat = Legacy`.
2. The new set is written in 2.1.
3. The TOC is written as 2.1; from here on legacy builds cannot read this tape's TOC. The service logs once
   (§8.6). No prompt — unattended runs must not stall.
4. The legacy media header stays untouched; its `HasSetHeaders` still governs the volume.

### 6.3 Multi-volume series

Each volume's TOC is rewritten when written, so a series may carry legacy TOCs on early volumes and 2.1 TOCs
on later ones. Both readers produce the same in-memory model; `ResumeRestoreFromAnotherVolume` compares
volume numbers and set sizes as today. A continuation volume written by the new build gets a 2.1 media header
with the series `MediaId`.

### 6.4 `.tapetoc` files

Import: both formats. Export and emergency export: 2.1 only.

### 6.5 Calibration

| On the cartridge | Read by |
|---|---|
| Legacy run header (standard or run block) | legacy framer |
| Oldest `TapeCalibrationRunHeader` | **dropped** — no such cartridges in the field |
| 2.1 run header (either block) | 2.1 framer |
| Legacy / 2.1 checkpoints | framer dispatch, block by block |

Resume and Recalibrate of a legacy run work unchanged: `ReadRunHeader` keeps its two probes;
`FindLastCheckpoint` classifies each block on its own, so mixed trails read correctly; the rewritten boundary
checkpoint and all later ones are 2.1. The `#define LEGACY_TapeCalibrationRunHeader` block is deleted.

### 6.6 Scan Media

The scanner parses nothing itself (it goes through `Classify` / `IdentifyBlock` / `RestoreTOCAt`), so it needs
only: `TocVersion = 0x0201` for 2.1 copies; checkpoint blocks reported as calibration fragments; unknown kinds
as "record from a newer TapeNET". Locating file headers mid-block (TOC-less file recovery) is out of scope —
this design lays the on-tape groundwork.

---

## 7. Legacy Read Path

### 7.1 `TapeLibNET.Legacy` (internal, frozen, read-only)

| Type | Reads |
|---|---|
| `LegacyDeserializer` | today's `TapeDeserializer`, with `ReadExactly`; `Deserialize<List,T>` throws on a null item |
| `LegacyTocReader` | TOC 0x0101 / 0x0102, set 0x0101, file entry 0x0101 — layout detection §7.2 |
| `LegacyFramer` | `[int32 len][payload][crc32]` |
| `LegacyHeaderReader` | media (tolerant `HasSetHeaders`), set, calibration headers |
| `LegacyCheckpointReader` | today's checkpoint body |
| `LegacyFileHeader` | 12-byte header check |
| `LegacyIdentify` | today's `TryPeek` + offset-4 probe, verbatim |
| `LegacyVirtualMediaState` | today's `VirtualTapeMedia` metadata |
| `LegacyTime` | `FromLocalTicks` (TOC, set, file, media / set header `CreatedUtc` — written from local TOC times) and `FromUtcTicks` (calibration) |

All map to the same in-memory types the 2.1 readers produce. Identity checks between a legacy BOM header and
an upgraded TOC keep matching because both went through the same conversion (R1).

### 7.2 Two legacy TOC layouts under one version

- **A — pre-compression:** set ends after `ContinuedFromPrevVolume`; file entry after `SizeOnTape`.
- **B — current:** + `Compression`, `CompressionLevel` (2 × int32) per set; + `Codec` byte per file.

The reader buffers the copy (≤ 256 MiB in memory, spilling to a temp file beyond — R9), tries B then A, and
accepts the layout whose parse ends exactly where the CRC-64 of the consumed bytes equals the next 8 bytes.
Plausibility (from the assumption): a B-parse must show `Compression ∈ {None, Hardware}` and `Codec == Stored`.
Neither matches → CRC failure → dual-copy fallback as today.

### 7.3 Legacy writer — test project only

`TapeLibNET.Tests/Helpers/LegacyFormatWriter.cs` holds today's serialization and framing code verbatim
(including layout A and pre-MediaId switches). It produces the legacy media the shipping readers are tested
against. A test pins it: with fixed GUIDs and timestamps, its output equals the Phase 0 goldens byte for byte.

### 7.4 Dropping legacy later

Five seams hand off to legacy code: TOC load, block-frame unpack, block identification, legacy-set file
header check, calibration header probe. Removing the `Legacy/` namespace and those branches is one PR; keep
`TapeDataFormat.Legacy` so such sets are refused cleanly. Recommendation: keep the readers — they are frozen,
golden-tested, and the product's promise is that old cartridges stay readable.

---

## 8. Code Structure

### 8.1 `TapeLibNET/Format/`

| Type | Role |
|---|---|
| `TapeFormat` | magic, `Major = 2`, `Minor = 1`, limits (§4.6), `IsV2(span)` |
| `TapeRecordKind` | §4.3 |
| `TapeFormatException` + `FormatErrorKind` | `NewerMajor`, `UnknownKind`, `UnknownCritical`, `MissingRequired`, `Duplicate`, `Overrun`, `Underrun`, `BadValue`, `CrcMismatch`, `Truncated` |
| `TapePrimitives` | span-based encode / decode (§4.1) |
| `TapeRecordWriter` / `TapeFieldWriter` | `BeginRecord(kind)` → field writer over a pooled buffer; explicit `EndRecord()` writes prologue + body |
| `TapeRecordReader` / `TapeFieldReader` | prologue, bounds, field iteration, skip / refuse, required check; over `Stream` or `ReadOnlySpan<byte>` |
| `TapeSchema<T>` | declarative field table (§5.7) |
| `ITapeRecord<TSelf>` | `static abstract TapeRecordKind Kind`, `WriteTo`, `static abstract ReadFrom` |
| `ITapeFramedRecord<TSelf>` | + `static abstract TSelf? ReadLegacy(ReadOnlySpan<byte>)` — one-line forwards into `Legacy/` |
| `TapeFrame` | `Record ‖ CRC-64` pack / unpack (block and inline) |
| `TapeCrc64Envelope` | §5.7 |
| `TapePeekStream` | first-bytes peek with replay |
| `TapeNameFrontCoder`, `TapeSampleCoder` | §5.1, §5.5 |
| `TapeFileHeader` | write / read-and-check the 2.1 file header frame |

### 8.2 Removed

- `TapeSerializer.cs` from the product (reader → `Legacy/`, writer → tests). `ITapeSerializable` retires.
- **All aligned agent APIs** (§8.4).
- `TapeTOC.GenerateUID`, `m_nextUID`, `TocVersion*` constants, `TapeFileInfo.UID` / `SerializeHeaderTo` /
  `DeserializeAndCheckHeaderFrom`, obsolete `long block` constructors and `Block`.
- `#define LEGACY_TapeCalibrationRunHeader` and its record.

### 8.3 Fix carried in this branch

`TapeStreamManager.FlushAndDisposePacker`: the third `try` disposes `m_packerBackend` a second time and nulls
`m_packerBufferPool` without disposing it — the page-aligned buffer pool leaks on every content session.
Dispose `m_packerBufferPool` there.

### 8.4 Aligned agent API removal

The block-per-file agent path fell behind `BackupRead`/`BackupWrite` (body length bounded to `Length` instead
of `SizeOnTape`; plain `FileStream`s on restore and verify; no decompression). Removed:

| File | Members |
|---|---|
| `TapeFileBackupAgent` | `OpenWriteContentStream`, `BackupFileAligned`, `BackupFilesToCurrentSetAligned` (private + public), `BackupFileListToCurrentSetAligned`; `TapeBackupContext.packed` |
| `TapeFileRestoreAgent` | `OpenReadContentStream`, `RestoreFileCoreAligned` (base + 3 overrides), `RestoreNextFileAligned`, `RestoreFilesFromCurrentSetAligned` ×2, public `Restore*Aligned` ×6; `TapeRestoreContext.packed` |
| `TapeTOC` | `TapeFileInfo.Block` and obsolete constructors |
| Tests | `VirtualTapeFixture.BackupFiles(useAligned:)` and every test passing `useAligned: true` |

**Kept:** `TapeStreamManager`'s raw stream interface (`ProduceWrite/ReadTOCStream`,
`ProduceWrite/ReadContentStream`, `CheckContentCapacity`) for TOC I/O, diagnostics and tests; the packed-layout
detection in `TapeSetTOC` (legacy aligned sets on tape still need it for capacity math). Legacy aligned sets
restore through the packed path, as today. `BufferedTapeRead/WriteStream` go if nothing else uses them.

### 8.5 UTC in memory

| Site | Change |
|---|---|
| `TapeFileDescriptor` ctors, `FillFrom`, `ApplyToFileInfo` (`TapeTOC.cs` ~57–108) | `…Utc` getters / setters |
| `TapeTOC` / `TapeSetTOC` `CreationTime`, `LastSaveTime` | `DateTime.UtcNow` |
| `TapeTOC.IsFileUptodateInc` (~1316) | compare against `fileInfo.LastWriteTimeUtc` |
| `TapeTOC.CreateSetHeader` / `CreateHeader` | `CreatedUtc` now genuinely UTC |
| `FclTapeFileFilter` — `TapeWinNET/Utils/FileFilter.cs:19–20`, `TapeConNET/FclTapeFileFilter.cs:27–28` | `.ToLocalTime()` — FCL dates are local by spec |
| `TapeServiceBase.cs:967–968`, `TapeServiceBase.List.cs:296, 313–314, 341–342, 360` | `.ToLocalTime()` |
| `TapeWinNET`: `BackupSetListItem.cs:137,139`, `FileListItem.cs:72`, `MainViewModel.cs:1745–1746, 1814–1815` | `.ToLocalTime()` |

`IFclFileInfo` keeps exposing local times. The remote backend is unaffected (TOC serialization happens client-side).

### 8.6 Service

- **`SaveTocCore(agent)`** — one helper all TOC-writing verbs call (backup, delete, rename media, rename set,
  format, initial TOC): `bool wasLegacy = agent.TOC.LoadedFromLegacy; var r = agent.BackupTOC(); if (r &&
  wasLegacy) LogWarn("TOC upgraded to format 2.1 — earlier TapeNET versions cannot read this tape any more.")`.
  `TapeTOC.SaveTo` clears the flag.
- `ClassifySetHeader` + `SetIdMismatch` flows through the existing anomaly channel and host prompt.

---

## 9. Behavioural Details

- `WrittenBy` from the entry assembly and TapeLibNET versions.
- `SetCount` / `FileCount` / `TotalFileCount` cross-checks refuse a copy; the second copy is tried.
- TOC capacity estimator: conservative 2.1 estimate (no shared prefix, max varint widths).
- File-header size for capacity estimates: computed exactly by `TapeFileHeader.Measure(setId, tfi)`; legacy
  sets use 12.
- Changing CRC-64 or the prologue layout is a major-version change.

---

## 10. Impact on Design-Encryption

- **Design-Encryption §7 is superseded** — no 0x0102 set record, no extension block. Encryption adds
  `TocSet` fields: `Encryption` (scalar, **critical**), `KeyEnvelope` (group, 48+ range), the sealed-summary
  scalars, `SealedBatches` (bytes). A 2.1 build without encryption refuses an encrypted set.
- **Sealed metadata = the set's `TocFileBatch` records + `Description`**, sealed as one blob. Front coding runs
  inside the seal.
- **File headers leak names.** In content + metadata mode, the file header gets a critical `Sealed` field and
  its fields 3–32 travel encrypted under the set key (AAD = `SetId ‖ FileId`). Specified in Design-Encryption v3.
- Encryption binds each body to `SetId ‖ FileId`; the codec byte is inside the encrypted body.
- Encrypted sets require `DataFormat = V2`.

---

## 11. Tests

### 11.1 Phase 0 — freeze legacy (current build)

Goldens in `TapeLibNET.Tests/Golden/Legacy/` (+ `*.expected.json`), generated by a skipped-by-default
`GoldenGenerator` test:

| Golden | Content |
|---|---|
| TOC layout B | multi-set, multi-volume, incremental, empty set, long / Unicode names, `MediaId` |
| TOC layout A, TOC pre-MediaId | via `LegacyFormatWriter` |
| Media headers | with / without `HasSetHeaders` |
| Set header | with / without description |
| Calibration headers | standard block; run block |
| Checkpoints | with / without EW; many samples |
| Legacy file header | 12 bytes |
| `.tapetoc` | layout B |
| Virtual tape images (`.vt` + metadata) | single volume; two-volume series; with / without headers; interrupted calibration run |

### 11.2 Format core

Primitives (LEB128 bounds and overlong, ZigZag, f64, strict bool, UTF-8 validation, UTC); prologue (unknown kind
refused / skippable bit honoured, newer major refused, overrun / underrun refused); fields (unknown non-critical
skipped, unknown critical refused, duplicate refused, missing required refused, required empty string round-trips,
bad enum value refused); limits (§4.6); front coder (shared prefixes, surrogate boundary, reset per batch); sample
coder; schema (ascending emission, default elision of optional fields only); CRC envelope (mismatch, truncation,
no read-ahead).

### 11.3 Frames and identification

Every `FrameStatus` produced deliberately; `IdentifyBlock` matrix over every golden and every 2.1 kind: TOC copy,
header, damaged-behind-magic, damaged magic → foreign, unknown kind → newer-build fragment, random → foreign,
legacy file header → foreign.

### 11.4 Legacy readers

Every golden reads and matches its JSON; layouts A / B detected; plausibility rejection; corrupted copy fails as
today; `LegacyFormatWriter` output equals goldens byte for byte; legacy → 2.1 → reload equals the legacy load.

### 11.5 Round trip / mixed media (× 4 drive profiles)

| Test | Covers |
|---|---|
| `Format_NewTape_WritesOnly21` | every block identifies as 2.1 or content |
| `Format_FileHeader_SelfDescribing` | header frames on tape carry SetId / FileId / name; codec byte matches TOC |
| `Format_LegacyTape_AppendUpgradesToc` | legacy image + new set → TOC 2.1; both restore; one upgrade log line |
| `Format_LegacyTape_MediaHeaderUntouched` | BOM block byte-identical |
| `Format_LegacyVolume_SetHeaders` | new set follows the legacy volume's `HasSetHeaders` |
| `Format_MixedSeries_Restore` | legacy volume 1 + 2.1 volume 2 |
| `Format_LegacyIncrementalChain_NewIncremental` | up-to-date detection across formats |
| `Format_SetId_ContinuationShared_OverwriteFresh` | §5.2 rules |
| `Format_SetHeader_SetIdMismatch_BlocksOverwrite` | new verdict |
| `Format_FileId_RetryBurnsNumber` | retry / EOM rollback never reuse a FileId |
| `Format_TapetocImportBoth_ExportOnly21` | §6.4 |
| `Format_ScanMedia_BothFamilies` | legacy, 2.1, mixed images |
| `Format_Calibration_ResumeLegacyRun` | mixed checkpoint trail; `InspectMedia` |
| `Format_Calibration_CheckpointHeadroom` | ≥ 3× legacy samples per block |
| `Format_TimeZoneShift` | backup under one `TimeZoneInfo`, restore under another → identical UTC times |
| `Format_TocSize_Smaller` | 2.1 TOC ≤ legacy for a deep tree |
| `Format_VirtualMedia_LegacyMetadataOpens` | golden `.vt` opens; re-saved metadata is 2.1 |

### 11.6 Existing suite — known touch points (from `git grep`)

| File | Change |
|---|---|
| `TapeTOCRoundTripTests.cs` | `SerializeAndDeserialize` helpers → `SaveTo` / `LoadFrom` over `MemoryStream`; `GenerateUID` → `CurrentSetTOC.GenerateFileId()`; UID-continuity tests → per-set `NextFileId`; header tests → `TapeFileHeader` tests; `BuildLegacyEmptyTOCBytes` → `LegacyFormatWriter`; `MakeDescriptor` builds UTC times; estimate-size tests → 2.1 measures |
| `TapeBackupAgentTests.cs:342–344` | uniqueness of `(SetId, FileId)` |
| `TapeHeaderRoundTripTests.cs:188`, `TapeMediaIdentifyTests.cs:255, 343, 465, 481` | crafted legacy bytes → `LegacyFormatWriter` |
| `TapeSetHeaderTests.cs:257` | test header subclass `SerializeTo` → `WriteTo` |
| `TapeSetHeaderFactoryTests.cs:31`, `VirtualDriveBasicTests.cs:489, 498` | `GenerateUID` → `GenerateFileId` |
| `VirtualTapeFixture.cs` | drop `useAligned` |
| aligned-path tests | delete |

Expected mechanical updates beyond these: packed-offset assertions (file header size), raw-byte header
assertions, `TocVersion` expectations in scan tests.

---

## 12. Implementation Plan (GitHub Copilot)

Branch `format-v2`. Each step ends with `dotnet build` and the named tests green; commit per step. Follow
`.github/copilot-instructions.md`: C# 12, file-scoped namespaces, primary constructors, `m_` fields in
TapeLibNET, constants for magic numbers, nullable discipline (explain every `!`). Preserve existing comments
(unless they need updating). Do not change behaviour outside a step's scope.

### Phase 0 — Groundwork and freeze

1. **Fix `FlushAndDisposePacker`** (§8.3) [DONE]. Add a test that the buffer pool is disposed after `EndWriteContent`.
2. **Remove the aligned agent APIs** (§8.4), the `packed` context flags and `useAligned` from the fixture;
   delete aligned-only tests. Full suite green.
3. **`LegacyFormatWriter`** in `TapeLibNET.Tests/Helpers/` — today's `TapeSerializer` write half, `TapeFramer.Pack`,
   every `SerializeTo` body (TOC, set, file entry, media / set / calibration headers, checkpoint, 12-byte file
   header), switches for layout A and pre-MediaId.
4. **`GoldenGenerator`** (category `Golden`, skipped) writes §11.1 with fixed GUIDs / times; run once on the
   current build; check in; `CopyToOutputDirectory=PreserveNewest`.
5. **`LegacyGoldenTests`** — current readers load every golden and match JSON; `LegacyFormatWriter` reproduces
   goldens byte for byte. Must stay green through every later phase.

5a./5b. → s. `Design-Format-v2-AppendixA-VirtualMedia.md` Phase 0.

### Phase 1 — Format core (`TapeLibNET/Format/`)

6. `TapeFormat`, `TapeRecordKind`, `TapeFormatException`, `TapePrimitives`.
7. `TapeRecordWriter` / `TapeFieldWriter` (pooled buffer, explicit `EndRecord`, nested groups).
8. `TapeRecordReader` / `TapeFieldReader` (Stream and span; no read-ahead; limits; skip / refuse; required).
9. `TapeSchema<T>`, `ITapeRecord<T>`, `ITapeFramedRecord<T>`.
10. `TapeFrame`, `TapeCrc64Envelope`, `TapePeekStream`, `TapeNameFrontCoder`, `TapeSampleCoder`.
11. Tests §11.2.
 
11a.  → s. `Design-Format-v2-AppendixA-VirtualMedia.md` Phase 1.

### Phase 2 — Legacy readers (`TapeLibNET/Legacy/`, internal)

12. Move `TapeDeserializer` → `LegacyDeserializer` (+ `ReadExactly`, throw on null list item); `LegacyTime`.
13. `LegacyFramer`, `LegacyHeaderReader`, `LegacyCheckpointReader`, `LegacyFileHeader`, `LegacyIdentify`,
    `LegacyVirtualMediaState`.

13a. → s. `Design-Format-v2-AppendixA-VirtualMedia.md` Phase 2.

14. `LegacyTocReader` (§7.2, R9 spill); sets get `DataFormat = Legacy`, `FileId = UID`; `LoadedFromLegacy = true`.
15. Switch `LegacyGoldenTests` to `Legacy/`; add §11.4.

### Phase 3 — TOC, identity, UTC

16. `TapeTOC` / `TapeSetTOC` / `TapeFileInfo`: `SetId`, `NextFileId`, `GenerateFileId()`, `FileId`,
    `DataFormat`, `WrittenBy`, `LoadedFromLegacy`; §5.2 rules in `AddNewSetTOC`, `ReplaceCurrentSetTOC`,
    `AddContinuationSetTOC`, `TapeSetTOCParams`, `ToParams`, `CopyFrom`, copy constructor; remove UID members.
17. Schemas for `TocHeader`, `TocSet`, file-entry group, `TocFileBatch`, `TocEnd`; `SaveTo` / `LoadFrom`
    (§5.7) with batching and cross-checks; `TryPeek` dispatch.
18. `TapeAgentBase`: `BackupTOCCore`, `RestoreTOCCore`, `SaveTOCToFile`, `LoadTOCFromFile` → `SaveTo` / `LoadFrom`.
19. UTC (§8.5) everywhere listed.
20. `PackedCommitTracker` (`Template.UID` → `FileId`); `TapeWinNET/Models/BackupSourceView.cs` keeps its own
    counter, renamed to `GenerateFileId`.
21. Update the suite (§11.6); add `Format_TimeZoneShift`, `Format_TocSize_Smaller`, legacy → 2.1 → reload.

### Phase 4 — File headers and codec byte

22. `TapeFileHeader` (write, `ReadAndCheck(stream, setId, fileId)`, `Measure`).
23. Backup: `BackupFile` writes the file header frame; `ProbingCompressionStream.Commit()` writes the codec
    byte; plain path writes `Stored`.
24. Restore: per `DataFormat` — 2.1 frame check or `LegacyFileHeader.Check`; codec byte vs TOC; size fallback
    per format.
25. Capacity estimates (`EstimateFileSizeOnTape`, `ComputeTotalFileSizeOnTape`) per format.
26. Tests: `Format_FileHeader_SelfDescribing`, `Format_FileId_RetryBurnsNumber`, compression suite green.

### Phase 5 — Block frames, headers, identification

27. `TapeFramer` → `TapeFrame` 2.1 + legacy dispatch; `TapeHeader` hierarchy on schemas with shared tags 1–3;
    set header `SetId`; remove `ReadSetHeadersFlag`, `SerializePreamble` / `ReadPreamble`.
28. `ClassifySetHeader` + `SetIdMismatch` (verdict, anomaly payload, host text in both apps).
29. `TapeHeaderBlock.Identify` (§5.9), `CarriesRecordSignature`, layer-table doc comment in `TapeHeaderBlock.cs`.
30. `TapeScanner.Identify`: TOC version, checkpoint and newer-record fragments.
31. Tests §11.3, `Format_SetHeader_SetIdMismatch_BlocksOverwrite`, `Format_ScanMedia_BothFamilies`.

### Phase 6 — Calibration and virtual media

32. Calibration header and checkpoint on schemas + `TapeSampleCoder`; `TapeCalibrationFramer` follows `TapeFrame`;
    delete the `LEGACY_TapeCalibrationRunHeader` block.
33. (moved to 11a/11b)
34. Tests: calibration suite, `Format_Calibration_*`, `Format_VirtualMedia_LegacyMetadataOpens`.

### Phase 7 — Service and mixed media

35. `SaveTocCore` (§8.6) used by every TOC-writing verb.
36. `MixedMediaTests` — every remaining §11.5 row × 4 profiles.
37. Full solution: TapeLibNET.Tests, TapeConNET.Tests, FclNET.Tests green.

### Phase 8 — Documentation and 2.1 freeze

38. `docs/TapeNET-Format-2.md` — normative spec from §4–§5 with byte-level examples from 2.1 goldens.
39. Update: primer (format section, aligned removal, test counts), `Getting-Started.md` (serializer layer),
    Design-Compression (§4–5 compatibility note, codec byte), Design-TapeFilePacker (§ file header, restore size),
    Design-TapeHeader ("known wart" resolved), Design-Encryption pointer (§10).
40. 2.1 goldens in `TapeLibNET.Tests/Golden/V21/` with a byte-compare test — freezes 2.1 the way Phase 0 froze legacy.

Then `encryption` rebases onto `format-v2`.

---

## 13. File Map

| File | Change |
|---|---|
| `TapeLibNET/Format/*` | **new** — §8.1 |
| `TapeLibNET/Legacy/*` | **new** — §7.1 |
| `TapeLibNET/TapeSerializer.cs` | removed (reader → `Legacy/`, writer → tests) |
| `TapeLibNET/TapeTOC.cs` | schemas, `SaveTo` / `LoadFrom`, identity, `DataFormat`, UTC, `TryPeek` dispatch, UID removal |
| `TapeLibNET/TapeAgentBase.cs` | TOC I/O via `SaveTo` / `LoadFrom` |
| `TapeLibNET/TapeFileBackupAgent.cs` | aligned removal; file header; `GenerateFileId` |
| `TapeLibNET/TapeFileRestoreAgent.cs` | aligned removal; per-format header check; codec byte |
| `TapeLibNET/TapeCompressionStream.cs` | codec byte in `Commit()` |
| `TapeLibNET/TapeFilePacker/PackedCommitTracker.cs` | `FileId` |
| `TapeLibNET/TapeStreamManager.cs` | buffer-pool disposal fix |
| `TapeLibNET/TapeFramer.cs` | `TapeFrame` 2.1 + legacy dispatch |
| `TapeLibNET/TapeHeader.cs`, `TapeMediaHeader.cs`, `TapeSetHeader.cs`, `TapeCalibrationHeader.cs` | schemas; shared tags; `SetId` |
| `TapeLibNET/TapeHeaderBlock.cs`, `TapeHeaderBlock.Identify.cs` | §5.9 |
| `TapeLibNET/TapeAgentBase.SetVerification.cs` | `SetIdMismatch` |
| `TapeLibNET/TapeCalibrationCheckpoint.cs`, `TapeCalibrator*.cs` | schemas, sample coder, legacy block removed |
| `TapeLibNET/Virtual/VirtualTapeMedia.cs` | state record 0x0F01 |
| `TapeLibNET/Scan/TapeScanner.Identify.cs` | fragment kinds |
| `TapeLibNET/Services/TapeServiceBase*.cs` | `SaveTocCore`; local-time display |
| `TapeWinNET/Utils/FileFilter.cs`, `TapeConNET/FclTapeFileFilter.cs` | local-time view |
| `TapeWinNET/Models/BackupSetListItem.cs`, `FileListItem.cs`, `BackupSourceView.cs`, `ViewModels/MainViewModel.cs` | local-time display; `GenerateFileId` |
| `TapeLibNET.Tests/Golden/*`, `Helpers/LegacyFormatWriter.cs`, `Format*Tests.cs`, `Legacy*Tests.cs`, `IdentifyBlockTests.cs`, `MixedMediaTests.cs` | **new** |
| `docs/TapeNET-Format-2.md` | **new** — normative spec |

---

## 14. Decisions Log

| Topic | Decision |
|---|---|
| Version fields | `u8` major + `u8` minor, top-level records only; no revision / build |
| Magic + kind | common `TpN#` + `u16` kind; `0x8000` = skippable |
| Integrity | CRC-64 everywhere |
| Field order | number ranges 1–31 / 32–47 / 48–63; ascending emission; no reserved slots |
| Nested records | groups without prologue (R4 revised) |
| TOC streaming | bounded file batches; no measuring pass (R10 / `CountingStream` dropped) |
| File identity | `SetId` (GUID) per set + `FileId` (counter) per file; `NextFileId` per set |
| File header | self-describing frame with full path; codec byte at body start |
| Serializer API | schema tables; classes write themselves; TOC owns the CRC envelope |
| Legacy writer | test project only, pinned to goldens |
| Oldest calibration header | dropped |
| Aligned agent APIs | removed; raw `TapeStreamManager` streams kept |
| Time | UTC on tape and in memory; displays and FCL convert to local |
| Virtual media state | migrated to a 2.1 record; legacy metadata readable |

---

## 15. Open Items

1. **`VirtualTapeMedia.cs`** — s. the separate document `Design-Format-v2-AppendixA-VirtualMedia.md`.
2. **`BufferedTapeRead/WriteStream`** — delete if unused (likely) after the aligned removal.
3. **`Design-RepairMedia.md`** — notes the strict `TapeSetTOC` signature problem; mark it resolved by this design.
