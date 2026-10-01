# Design: On-Tape Format 2.1 — Clean Cut with Read-Only Legacy Support

**Status:** Proposed — v3 for implementation. **Branch:** `format-v2` — lands before `encryption`.
**Last updated:** 2026-10-01

---

## 1. Summary

Every record TapeNET writes from now on — TOC, set and file entries, per-file headers, media, set and
calibration headers, calibration checkpoints — uses **format 2.1** (`0x0201`). 2.1 records identify
themselves with a magic word **at byte 0**, carry their own length, use tagged fields that can be
skipped when unknown and refused when critical, and store timestamps in UTC. Legacy records (the
`0x01xx` family) stay **readable forever** and are **never written again**.

- **One rule for writes:** every *new* record is 2.1. Old records on tape are never rewritten —
  except the TOC, which TapeNET always rewrites whole, so a legacy tape's TOC becomes 2.1 on its first
  write by the new build.
- **One rule for reads:** dispatch on the first bytes. `TN` + kind → the 2.1 reader. Anything else →
  the legacy reader, isolated and frozen in `TapeLibNET.Legacy`.
- **Per-set data format.** A set's bytes on tape stay as written. The TOC records each set's
  `DataFormat`, so a 2.1 TOC can describe legacy sets, and restore still reads their 12-byte legacy
  file headers.
- **Framed records put the magic first.** The known wart of the legacy framer — the signature sitting
  behind an untrusted `int32` length — goes away (§5.6).
- **Assumption:** no legacy tape carries software-compressed data. Legacy TOCs still come in two byte
  layouts under the same version number; the legacy reader tells them apart by anchoring on the TOC's
  CRC-64 (§7.2).
- **No new packages.**

---

## 2. Why a Clean Cut

What the legacy format gets wrong:

| # | Problem | Consequence |
|---|---|---|
| 1 | Nested records are **not length-delimited** | Any field added to `TapeSetTOC` or `TapeFileInfo` misaligns every record after it on older tapes |
| 2 | Fields appended **without a version bump** (compression; `HasSetHeaders`) | Several layouts share one version; the reader guesses from context (`ReadSetHeadersFlag` swallows exceptions) |
| 3 | `Deserialize<List,T>` **drops `null` items** after they consumed bytes | A rejected record silently misaligns the rest of the list |
| 4 | One **library-wide signature** `TF` + version for every record kind | A legacy aligned file header and a v0x0101 TOC start with identical bytes — `TryPeek` needs structural guesswork |
| 5 | Framed records put the **signature behind the length prefix** | "Is this block ours?" requires trusting an int32 first; `IdentifyBlock` must test the TOC first to avoid misreading |
| 6 | Strict equality on version (`== 0x0101`) in nested records | No room for an additive minor version |
| 7 | `DeserializeBytes` issues **one** `Read` | Short reads from a non-memory stream corrupt parsing |
| 8 | **Machine-endian** primitives (`Unsafe.As`) | Correct on x64 / ARM64 today; undocumented, not a format |
| 9 | `DateTime` as **raw ticks, `Kind` lost** | Local times read as local on another machine — shifts across time zones and DST |
| 10 | Fixed-width integers everywhere; full paths per file; calibration samples at 16 bytes each | Larger TOCs (competing with content for the early-warning reserve); checkpoints hit the block limit sooner — beyond it a run stops being resumable |

Patching these one by one keeps every workaround alive. The cut fixes all of them at once, and the
encryption feature then needs no format work of its own (§10).

---

## 3. Principles

1. **Write 2.1 only.** No legacy writer ships in the product.
2. **Read legacy forever,** from one isolated place (`TapeLibNET.Legacy`), frozen and covered by golden
   files.
3. **Never rewrite an old record in place.** A tape write truncates everything behind it; rewriting a
   media header at BOM would destroy the volume. Old headers and checkpoints stay old.
4. **The TOC is the exception** — always rewritten whole, so it upgrades on the first write.
5. **A set's data format is fixed** once its content is on tape.
6. **Self-describing and fail-loud.** Every record names its kind and version at byte 0; a record we
   cannot read is an error, never a silent skip.
7. **Extensible without version bumps.** New field = new tag. Only a field that changes interpretation
   needs the *critical* bit (§4.4); only a layout break needs a new major version.

---

## 4. Format 2.1 Specification

### 4.1 Primitives

| Type | Encoding |
|---|---|
| `u8` | 1 byte |
| `varuint` | unsigned LEB128, at most 10 bytes; overlong encodings rejected |
| `varint` | ZigZag, then `varuint` |
| `u16` / `u32` / `u64` fixed | little-endian (`BinaryPrimitives`) |
| `f64` | IEEE 754 binary64, little-endian |
| `bool` | `u8`, 0 or 1; anything else rejected |
| `bytes` | `varuint` length, then raw bytes |
| `string` | `bytes`, UTF-8, validated on read |
| `guid` | 16 bytes, `Guid.TryWriteBytes` order (as today) |
| `timestamp` | `varint` UTC ticks. Writers call `ToUniversalTime()`; readers return `DateTimeKind.Utc` |

All reads use read-until-full semantics (`ReadExactly`).

### 4.2 Record envelope

```
Record  := Magic[4]  Major:u8  Minor:u8  BodyLength:varuint  Body[BodyLength]
Body    := Field*
Field   := Tag:varuint  Length:varuint  Value[Length]
```

- **Magic** names the record kind (§4.3). Every 2.1 magic starts with `TN`; every legacy record starts
  with something else (§5.7). The first two bytes alone pick the reader.
- **Major.Minor** = `2.1` for this cut. Readers accept major 2 with any minor; any other major is
  refused with *"written by a newer TapeNET (format x.y)"*.
- **BodyLength** makes every record — nested or not — skippable. After reading a record the reader is
  at `start + BodyLength`, whatever it understood. Misalignment (§2 #1, #3) cannot happen.
- **Fields** may appear in any order; writers emit ascending tags. A tag appearing twice is an error
  unless the tag is declared *repeated*.

### 4.3 Record kinds

| Magic | Record | Stored as |
|---|---|---|
| `TNTH` | TOC header | TOC stream (§5.1) |
| `TNST` | Set | TOC stream |
| `TNFE` | File entry | nested in `TNST` |
| `TNTE` | TOC end | TOC stream |
| `TNFH` | Per-file header on tape | inline in content, fixed layout (§5.5) |
| `TNMH` | Media header | framed, standard header block |
| `TNSH` | Set header | framed, standard header block |
| `TNCH` | Calibration run header | framed, standard header block (or run block, §5.8) |
| `TNCP` | Calibration checkpoint | framed, run block |
| `TNKE` | Key envelope (encryption) | nested in `TNST` |

### 4.4 Tags

```
Tag := (Number << 1) | Critical
```

- **Numbers are never reused.** A removed field retires its number.
- **Unknown non-critical tag** → skipped. A round trip through an older 2.x build drops it (acceptable
  for informational fields).
- **Unknown critical tag** → the record is refused (*"record uses feature N unknown to this build"*).
  For fields that change how the rest must be interpreted — encryption mode, a new name coding, a new
  hash scheme.
- **Default elision.** A field at its default is omitted. Each table states the default.
- **Required fields** are checked after the body is read; a missing one refuses the record.

### 4.5 Why not Protocol Buffers

`Google.Protobuf` already ships with TapeLibNET (gRPC stubs), and its wire format has the same
tag/length property. Rejected for on-tape records:

- **Whole-message parsing** — a TOC with millions of file entries would be one message with a 2 GB
  ceiling; 2.1 streams file entries one record at a time.
- **No critical-field concept** — proto3 silently ignores unknown fields, exactly the failure §4.4
  prevents.
- **Archival self-sufficiency** — a cartridge read decades from now should need only this document.

---

## 5. Record Catalog

### 5.1 TOC stream

```
TNTH record
TNST record × SetCount
TNTE record
CRC-64 (8 bytes, over every byte above)
```

A *sequence* of records, not one enclosing record: no writer needs the TOC's total length up front, and
file entries stream straight to tape. The CRC-64, the dual-copy strategy and the fixed 16 KiB TOC block
stay as today. The TOC's first block begins with `TNTH` at byte 0, which is what identifies it (§5.7).

**`TNTE` — TOC end**

| Tag | Field | Type | Default | Notes |
|---|---|---|---|---|
| 1 | `SetCount` | varuint | — required | repeats `TNTH` tag 8; a mismatch refuses the copy |
| 2 | `TotalFileCount` | varuint | — required | sum of every set's `FileCount` |

`TNTE` positively marks the end of the set sequence: a reader stops on `TNTE`, never on "looks like no
more sets", and a truncated copy (no `TNTE`) is refused before the CRC is even compared.

### 5.2 `TNTH` — TOC header

| Tag | Field | Type | Default | Notes |
|---|---|---|---|---|
| 1 | `NextUid` | varuint | — required | |
| 2 | `MediaId` | guid | empty | |
| 3 | `Description` | string | "" | |
| 4 | `CreationTime` | timestamp | — required | |
| 5 | `LastSaveTime` | timestamp | — required | |
| 6 | `Volume` | varuint | 1 | |
| 7 | `ContinuedOnNextVolume` | bool | false | |
| 8 | `SetCount` | varuint | 0 | cross-checked against the set records that follow |
| 9 | `WrittenBy` | string | "" | e.g. `TapeWinNET 3.4.0 / TapeLibNET 3.4.0` — forensics |

`TNTH` is kept small and ahead of all sets, so the first 16 KiB block always holds it whole — the
identification in §5.7 parses it from that block alone.

### 5.3 `TNST` — set

| Tag | Field | Type | Default | Notes |
|---|---|---|---|---|
| 1 | `Description` | string | "" | |
| 2 | `CreationTime` | timestamp | — required | |
| 3 | `LastSaveTime` | timestamp | — required | |
| 4 | `BlockSize` | varuint | — required | |
| 5 | `HashAlgorithm` | varuint | `Crc32` | |
| 6 | `Incremental` | bool | false | |
| 7 | `Volume` | varuint | — required | |
| 8 | `ContinuedFromPrevVolume` | bool | false | |
| 9 | `Compression` | varuint | `None` | |
| 10 | `CompressionLevel` | varuint | `ZstdLevel.Default` | |
| 11 | `DataFormat` | varuint | `V2` | `Legacy = 1`, `V2 = 2` — which per-file header the content carries (§6.1). Written with the critical bit when not `V2` |
| 12 | `FileCount` | varuint | 0 | cross-checked |
| 13 | `File` | `TNFE` record, **repeated** | — | in file order |
| 14–31 | *reserved for encryption* | — | — | §10 |

### 5.4 `TNFE` — file entry

| Tag | Field | Type | Default | Notes |
|---|---|---|---|---|
| 1 | `Uid` | varuint | — required | |
| 2 | `AddressBlock` | varuint | — required | |
| 3 | `AddressOffset` | varuint | 0 | |
| 4 | `NameSharedPrefix` | varuint | 0 | UTF-16 chars shared with the **previous entry's** full name |
| 5 | `NameSuffix` | string | — required | the rest of the full name |
| 6 | `Length` | varuint | 0 | |
| 7 | `Attributes` | varuint | 0 | |
| 8 | `CreationTime` | timestamp | — required | |
| 9 | `LastWriteTime` | timestamp | — required | |
| 10 | `LastAccessTime` | timestamp | — required | |
| 11 | `Hash` | bytes | absent | |
| 12 | `SizeOnTape` | varuint | 0 | |
| 13 | `Codec` | varuint | `Stored` | |

**Front coding (tags 4–5).** Files in a set are enumerated directory by directory, so consecutive names
share long prefixes; each entry stores only its differing tail. The first entry of each set has prefix
0, so sets decode independently. The prefix counts UTF-16 chars and never splits a surrogate pair (the
writer backs off one char if it would).

### 5.5 `TNFH` — per-file header on tape (fixed layout)

Inline in the packed content stream, so it stays tiny and fixed-size — no tags, no length:

```
Magic    "TNFH"   4
Major    2        1
Minor    1        1
Flags    u16      2    reserved, 0; a reader refuses unknown bits
Uid      u64      8
─────────────────────
                 16 bytes   (legacy: 12)
```

`TapeFileInfo.EstimateSerializedHeaderSize()` becomes per data format (16 / 12). Flags give room for
per-file features without touching the TOC.

### 5.6 Framed records — magic first

**Legacy frame:** `[int32 len][payload: "TF" + version + …][crc32]` — the signature sits at offset 4,
behind a length that must be trusted before we know the block is ours.

**2.1 frame:** the record envelope already carries its length, so the separate length prefix goes:

```
Frame := Record  CRC-32(Record)          ← CRC over every byte of the record, magic included
```

- **Magic at byte 0.** "Is this ours?" is one 4-byte compare; "which record?" is the same compare.
- **Bounds before trust.** The reader checks `6 + len(varuint) + BodyLength + 4 ≤ bytes read` before
  touching the body. A length that runs past the block → `NotFramed`.
- **CRC-32 stays** — torn-write detection is unchanged in strength.
- **Padding:** the rest of the block. Header blocks: zeros (as today). Calibration run blocks: random
  (as today — incompressible, and immaterial with compression off).

`TapeFramer.FrameStatus` keeps its four values and meanings:

| Status | 2.1 condition |
|---|---|
| `Ok` | magic known, bounds fit, CRC matches, record parses |
| `NotFramed` | magic not `TN…`, or bounds run past the block |
| `CrcMismatch` | bounds fit, CRC differs — torn or corrupt |
| `Unparseable` | CRC matches, but newer major, unknown critical tag, missing required field, or magic of an unknown kind |

### 5.7 Identification (replaces the two-layout probe)

`TapeHeaderBlock.IdentifyBlock` becomes a dispatch on the first two bytes:

```
bytes[0..1] == "TN"
  ├─ magic TNTH            → 2.1 TOC copy     (parse TNTH from this block: version + MediaId)
  ├─ magic TNMH/TNSH/TNCH  → 2.1 framed header: TryUnpack → Header | DamagedRecord
  ├─ magic TNCP            → 2.1 framed checkpoint (calibration trail; reported as its own kind, §5.9)
  └─ other TN magic        → DamagedRecord (Unparseable) — "ours, from a newer build"
otherwise
  └─ LegacyIdentify        → today's logic, moved verbatim: TryPeek first, then signature at offset 4
```

**Why this is safe:**

- A legacy frame opens with an int32 length ≤ 16 KiB, so its bytes 2–3 are zero; a 2.1 magic has
  letters there. A legacy TOC opens with `TF`. No legacy record can be read as 2.1.
- Random data matches `TN` + a known two-letter kind + major 2 once in about 2⁴⁰ blocks — and must
  then pass the bounds check and a CRC-32 (headers) or a full `TNTH` parse (TOC).
- A 2.1 record damaged **in its magic** reads as foreign, as today. A 2.1 record damaged **behind** its
  magic reads as `DamagedRecord`, never as a phantom TOC.

`TapeHeaderBlock.CarriesRecordSignature` accepts `TN` + known magic at offset 0, plus the legacy
signature at offsets 0 and 4.

### 5.8 Headers

The `TapeHeader` class hierarchy, `TapeHeaderBlock` (16 KiB block, block-size set-and-restore,
trailing filemark on the media header) and every positioning rule stay as they are. Only the record
changes.

**Shared tags.** Tags 1–3 mean the same thing in every header kind, so identity can be read from any
header without knowing its kind — the job the legacy preamble did:

| Tag | Field | Type |
|---|---|---|
| 1 | `Id` (MediaId / RunId) | guid, required |
| 2 | `CreatedUtc` | timestamp, required |
| 3 | `BlockSize` (TocBlockSize / SetBlockSize / RunBlockSize) | varuint, required |

The kind byte disappears — the magic is the kind. `TapeHeaderKind` stays as the in-memory enum,
mapped from the magic.

**`TNMH` — media header**

| Tag | Field | Type | Default |
|---|---|---|---|
| 1–3 | shared | | |
| 4 | `Volume` | varuint | 1 |
| 5 | `Partition` | varuint | `Content` |
| 6 | `TocPlacement` | varuint | `InSet` |
| 7 | `OriginalName` | string | absent (= none) |
| 8 | `HasSetHeaders` | bool | false |

The "serialize `HasSetHeaders` after the name so old headers read `false`" arrangement and the
exception-swallowing `ReadSetHeadersFlag` are no longer needed in 2.1 — both move to the legacy reader.

**`TNSH` — set header**

| Tag | Field | Type | Default |
|---|---|---|---|
| 1–3 | shared | | |
| 4 | `Volume` | varuint | — required |
| 5 | `VolumeSetIndex` | varuint | 0 |
| 6 | `GlobalSetIndex` | varuint | — required |
| 7 | `Description` | string | absent (= none) |

**`TNCH` — calibration run header**

| Tag | Field | Type | Default |
|---|---|---|---|
| 1–3 | shared (`RunId`, `StartedUtc`, `RunBlockSize`) | | |
| 4 | `ProfileKey` | string | "" |
| 5 | `CapacityReportedAtBom` | varuint | — required |
| 6 | `Plan` | nested plan record (below) | — required |

**Plan** — a field group encoded as a nested body (`bytes` containing `Field*`), so plan parameters can
evolve independently:

| Tag | Field | Type |
|---|---|---|
| 1 | `SampleCount` | varuint |
| 2 | `BodySampleCount` | varuint |
| 3 | `TailSampleCount` | varuint |
| 4 | `BlockSize` | varuint |
| 5 | `BlocksPerChunk` | varuint |
| 6 | `ChunkSize` | varuint |
| 7 | `TailBlocksPerChunk` | varuint |
| 8 | `TailChunkSize` | varuint |
| 9 | `TailCapacityFraction` | f64 |
| 10 | `NumCheckpoints` | varuint |

All plan fields are required.

**Name budgets.** `ClampUtf8` budgets (15 KiB for `OriginalName` and `Description`) stay; the 2.1
envelope and the shared tags cost well under 100 bytes, so the frame still fits the 16 KiB block.

### 5.9 `TNCP` — calibration checkpoint

| Tag | Field | Type | Default | Notes |
|---|---|---|---|---|
| 1 | `RunId` | guid | — required | |
| 2 | `Index` | varuint | — required | |
| 3 | `BytesWritten` | varuint | — required | |
| 4 | `EwActualWritten` | varuint | absent | present ⇔ the EW landmark was seen |
| 5 | `EwReportedRemaining` | varuint | absent | pairs with tag 4 |
| 6 | `Samples` | bytes | empty | delta-coded, below |

**Delta-coded samples.** `Samples` = `varuint count`, then per sample `varuint ΔActualWritten`
(actual written never decreases) and `varint ΔReportedRemaining` (usually negative). The first delta is
from `(0, 0)`.

Why it matters: the checkpoint carries every sample so far, and `RecordBlockWriter.Emit` **stops
checkpointing** once the frame outgrows the run block — the run then can no longer be resumed. Legacy
samples cost 16 bytes each; delta-coded samples on a regular cadence cost a few bytes each, so a run of
a given block size can checkpoint several times more samples before hitting that limit.

Confirmed against `TapeCalibrationCheckpoint.cs`: the record is `(Guid RunId, int Index, long
BytesWritten, (long, long)? EarlyWarning, IReadOnlyList<(long, long)> Samples)`; the legacy body is
`signature, guid, int32, int64, bool [, int64, int64], int32 count, count × (int64, int64)`.

**Scan Media:** a `TNCP` block is identified positively. The scanner reports it as a calibration
fragment instead of `Unknown` — a small gain for mapping a calibration cartridge.

---

## 6. Mixed Media

### 6.1 Per-set data format

| Set written by | `DataFormat` | Per-file header on tape | Set header (if any) |
|---|---|---|---|
| Legacy build | `Legacy` | 12 bytes, `TF` signature + UID | legacy frame |
| New build | `V2` | 16 bytes, `TNFH` | `TNSH` |

The TOC carries it; the restore agent picks the header check per set:

```csharp
bool ok = TOC.CurrentSetTOC.DataFormat == TapeDataFormat.Legacy
    ? LegacyFileHeader.Check(rstream, tfi.Uid)
    : TapeFileHeader.Check(rstream, tfi.Uid);
```

Set-header verification is unaffected: `ClassifySetHeader` compares an already parsed `TapeSetHeader`,
whichever format produced it.

### 6.2 Appending to a legacy tape

1. The TOC loads through the legacy reader; every set gets `DataFormat = Legacy`.
2. The new set is written in 2.1 — `TNSH` set header (if the volume declares set headers), `TNFH` file
   headers.
3. The TOC is written as 2.1. **From here on, legacy builds cannot read this tape's TOC.**
4. The legacy media header stays at BOM untouched (§3.3). New builds read it through the legacy parser;
   its `HasSetHeaders` still governs set headers on this volume.

The service logs once: *"TOC upgraded to format 2.1 — earlier TapeNET versions cannot read this tape
any more."* No prompt: the cut is deliberate, and a prompt would stall unattended runs.

### 6.3 Multi-volume series

Each volume's TOC is rewritten as it is written, so a series may carry legacy TOCs on early volumes and
2.1 TOCs on later ones. Restore works from the newest TOC, which describes every set with its
`DataFormat`. On a volume swap, `ResumeRestoreFromAnotherVolume` loads that volume's TOC, which may be
legacy — both readers produce the same in-memory model. A continuation volume written by the new build
gets a `TNMH` media header carrying the series' `MediaId`, so the identity checkpoints (§10.5 of
Design-TapeHeader) compare legacy and 2.1 headers on equal terms.

### 6.4 `.tapetoc` files

Import: both formats, dispatched by the first bytes. Export and emergency export: 2.1 only.

### 6.5 Calibration runs (confirmed)

`TapeCalibrator` meets three historical header shapes and, after the cut, two checkpoint formats:

| On the cartridge | Read by |
|---|---|
| Legacy header in the standard block | legacy framer |
| Legacy header in the run block (small-block drives) | legacy framer |
| Oldest `TapeCalibrationRunHeader` (`LEGACY_TapeCalibrationRunHeader`) | legacy reader → `.ToHeader()` |
| `TNCH` in either block | 2.1 framer |
| Legacy / `TNCP` checkpoints | framer dispatch |

**Resume and Recalibrate of a legacy run work unchanged:**

- `ReadRunHeader` goes through the dispatching framer; its two probes (standard block, then run block)
  keep their order.
- `FindLastCheckpoint` walks back filemark by filemark and parses each block through
  `ReadRecord<TapeCalibrationCheckpoint>`. Each block is classified on its own, so a trail of legacy
  checkpoints followed by 2.1 ones — or the reverse — reads correctly. The `RunId` comparison is
  format-neutral.
- `ResumeCore` rewrites the boundary checkpoint and continues. The rewritten one and all later ones are
  `TNCP`.
- `InspectMedia` and Scan Media's `InspectCalibrationTrail` reuse the same code — nothing extra.

The `#define LEGACY_TapeCalibrationRunHeader` "FIXME temporary" branch moves permanently into
`TapeLibNET.Legacy`; the `#if` disappears from `TapeCalibrator`.

### 6.6 Scan Media (confirmed)

The scanner never parses bytes itself: `IdentifyBomFragment` goes through `ReadBomHeader` →
`TapeHeaderBlock.Classify`, and `ReadFragmentAt` through `TapeHeaderBlock.IdentifyBlock`. Both
dispatch (§5.7), so the scanner needs no format code. Changes:

- `TocCopyFragment` reports `TocVersion = 0x0201` for 2.1 copies; `MediaId` comes from `TNTH`.
- A `TNCP` block becomes a calibration-checkpoint fragment (§5.9).
- A `TN` block of an unknown kind becomes a `DamagedRecord` fragment with *"record from a newer
  TapeNET"* — it is ours, just not readable by this build.
- The small-block-drive fallback in `ReadIdentificationBlock` is unchanged.

---

## 7. Legacy Read Path

### 7.1 Scope

`TapeLibNET.Legacy` — `internal`, frozen, read-only:

| Type | Reads |
|---|---|
| `LegacyTocReader` | TOC `0x0101` / `0x0102`, set `0x0101`, file entry `0x0101` |
| `LegacyFramer` | `[len][payload][crc32]` frames — today's `TapeFramer` read half |
| `LegacyHeaderReader` | media, set, calibration headers (preamble + kind byte); `TapeCalibrationRunHeader` |
| `LegacyCheckpointReader` | today's checkpoint body |
| `LegacyFileHeader` | the 12-byte per-file header |
| `LegacyIdentify` | today's `TryPeek` and offset-4 signature probe, moved verbatim |
| `LegacyDeserializer` | today's `TapeDeserializer`, with `ReadExactly` |

Each maps to the same in-memory types the 2.1 reader produces.

**Legacy timestamps.** Calibration headers were written with `DateTime.UtcNow` ticks → read as UTC.
TOC, set and file times were written as local ticks → read as `Local`, then `ToUniversalTime()`. Media
and set headers take their `CreatedUtc` from the TOC's times (`CreatedUtc = CreationTime` /
`setTOC.CreationTime`, both `DateTime.Now`) — confirmed: despite the name, legacy media / set header
`CreatedUtc` holds **local** ticks. The legacy header reader therefore converts it exactly like the
legacy TOC reader does, so identity checks comparing a legacy header against a (legacy or upgraded)
TOC keep matching (§15, R1).

### 7.2 Two legacy TOC layouts, one version number

Under set / file version `0x0101`, a TOC has one of two layouts:

- **A — pre-compression:** set ends after `ContinuedFromPrevVolume`; file entry ends after `SizeOnTape`.
- **B — current:** set adds `Compression` + `CompressionLevel` (2 × int32); file entry adds the `Codec`
  byte.

A TOC is written whole by one build, so it uses one layout throughout. The reader:

1. Reads the TOC copy into memory up to its filemark — `RestoreTOCCore` already consumes the whole copy.
2. Tries layout B, then A. A layout **matches** when parsing ends exactly where the CRC-64 of the
   consumed bytes equals the next 8 bytes. A wrong layout desynchronizes within the first set, so its
   CRC cannot line up.
3. **Plausibility (from the assumption):** a B-parse must show `Compression ∈ {None, Hardware}` and
   `Codec == Stored` throughout; otherwise it is rejected even with a matching CRC.
4. Neither matches → CRC failure, and the dual-copy fallback proceeds as today.

Legacy media headers similarly come with or without the trailing `HasSetHeaders` byte; the legacy reader
keeps today's tolerant read for that.

### 7.3 Test-only legacy writer

The product has no legacy writer. Tests that need legacy media use `LegacyFormatWriter` in
`TapeLibNET.Tests/Helpers/` — today's serialization and framing code, moved there verbatim.

---

## 8. Code Structure

### 8.1 Format core (`TapeLibNET/Format/`)

| Type | Role |
|---|---|
| `TapeRecordWriter` | primitives (§4.1), `BeginRecord(magic)` / `EndRecord()`, `WriteField(tag, …)`, nested bodies |
| `TapeRecordReader` | envelope parsing, field iteration, skip / refuse, required-field check |
| `TapeFormat` | magics, `Major = 2`, `Minor = 1`, tag numbers per record |
| `ITapeRecord<TSelf>` | `static abstract ReadOnlySpan<byte> Magic`, `void WriteTo(TapeRecordWriter)`, `static abstract TSelf ReadFrom(TapeRecordReader)` |
| `ITapeFramedRecord<TSelf>` | `ITapeRecord<TSelf>` + `static abstract TSelf? ReadLegacy(LegacyDeserializer)` — lets the framer dispatch per type |
| `TapeFormatDispatch` | first-bytes dispatch for TOC streams and `.tapetoc` files |
| `TapeTocFormat` | TOC stream write / read (§5.1) |
| `TapeFileHeader` | 16-byte `TNFH` |
| `CountingStream` | length measurement without storage (§8.2) |

`ReadLegacy` implementations are one-line forwards into `TapeLibNET.Legacy`, so legacy parsing code
stays in one place.

### 8.2 Record measurement

A nested record's `BodyLength` precedes its body. Writers measure first:

- **Small records** (headers, checkpoints, file entries, envelopes): serialize into a pooled buffer,
  then copy.
- **Set records:** one measuring pass through `CountingStream`, then the real pass straight to the tape
  stream — memory stays flat for sets with millions of files.
- **TOC:** a sequence (§5.1) — no measurement.

### 8.3 `TapeFramer`

```csharp
public static class TapeFramer
{
    public static byte[] Pack<T>(T record) where T : ITapeRecord<T>;          // 2.1 only
    public static FrameStatus TryUnpack<T>(byte[] block, int length, out T? record)
        where T : class, ITapeFramedRecord<T>;                               // "TN" → 2.1, else LegacyFramer
    public static T? Unpack<T>(byte[] block, int length) where T : class, ITapeFramedRecord<T>;
}
```

`TapeHeader.ReadFrom` dispatches on the magic to the concrete kind, replacing `ConstructFrom`'s kind
byte switch. `TapeCalibrationFramer` (the calibrator's alias) follows.

### 8.4 Call sites

| Site | Change |
|---|---|
| `TapeAgentBase.BackupTOCCore` / `SaveTOCToFile` | `TapeTocFormat.Write` inside the existing `HashingStream` |
| `TapeAgentBase.RestoreTOCCore` / `LoadTOCFromFile` / `RestoreTOCAt` | `TapeFormatDispatch.ReadToc` — 2.1 streamed, legacy via §7.2 |
| `TapeFileBackupAgent.BackupFile` / `BackupFileAligned` | `TapeFileHeader.Write(wstream, uid)` |
| `TapeFileRestoreBaseAgent.RestoreNextFile` / aligned | per-set header check (§6.1); header-size fallback per `DataFormat` |
| `TapeAgentBase.WriteMediaHeader` / `WriteSetHeader` | unchanged — `TapeHeaderBlock.Frame` packs 2.1 |
| `TapeAgentBase.ReadBomHeader` / `ReadSetHeader` | unchanged — `Classify` dispatches |
| `TapeHeader`, `TapeMediaHeader`, `TapeSetHeader`, `TapeCalibrationHeader` | `ITapeFramedRecord`; `SerializeTo` / `ConstructBody` → `WriteTo` / `ReadFrom`; legacy bodies move out |
| `TapeHeaderBlock.Identify` | §5.7 dispatch; legacy probe moves to `LegacyIdentify` |
| `TapeCalibrator` | `#if LEGACY_…` removed; `ReadRecord<T>` constraint → `ITapeFramedRecord<T>` |
| `TapeCalibrationCheckpoint` | `TNCP` with delta-coded samples |
| `TapeTOC.TryPeek` | 2.1: `TNTH` parse; legacy: `LegacyIdentify` |
| TOC capacity estimates | 2.1 estimator; conservative (assumes no shared name prefix) |

---

## 9. Behavioural Details

- **`WrittenBy`** is set from the entry assembly and TapeLibNET versions at write time.
- **Timestamps:** TOC and set times move from `DateTime.Now` to `DateTime.UtcNow`; UIs convert with
  `ToLocalTime()` for display. File timestamps are captured with the `…Utc` getters and applied with
  the `…Utc` setters on restore — correct across time-zone and DST changes.
- **Cross-checks:** `SetCount` and `FileCount` mismatches refuse the TOC copy; the second copy is tried
  as today.
- **Integrity:** TOC keeps CRC-64; framed records keep CRC-32. Changing either is a major-version change.

---

## 10. Impact on Design-Encryption

Encryption lands on top of 2.1 and loses its whole serialization section:

- **Design-Encryption §7 is superseded.** No `0x0102` set record, no extension block: encryption adds
  tags 14–31 to `TNST` — `Encryption` (critical), `KeyEnvelope` as a nested `TNKE` record, the plain
  summary (`FileCount` already exists as tag 12), and `SealedFiles` bytes. A 2.1 build without
  encryption support refuses an encrypted set instead of misreading it.
- **Encrypted sets require `DataFormat = V2`.** Legacy sets are never encrypted retroactively.
- **Sealed blob** = the set's `TNFE` records + `Description`, as a 2.1 byte stream. Front coding runs
  inside the seal, so name lengths never leak through prefixes.
- **Set header of a sealed set:** `Description` absent (tag 7 elided) rather than a placeholder string.
- The `TryPeek`, golden-file and `Deserialize<List,T>` concerns of Design-Encryption §7.1 are resolved
  here.

Design-Encryption v3 will reference this document and drop its §7.

---

## 11. Tests

### 11.1 Freeze legacy first (Phase 0)

Before any code changes, the **current build** generates checked-in golden files
(`TapeLibNET.Tests/Golden/Legacy/`):

| Golden | Content |
|---|---|
| TOC layout B | multi-set, multi-volume, incremental, empty set, long / Unicode names, `MediaId` |
| TOC layout A | synthesized by `LegacyFormatWriter` with the compression fields omitted |
| TOC `0x0101` (pre-MediaId) | layout A without `MediaId` |
| Media headers | with and without `HasSetHeaders` |
| Set header | with and without description |
| Calibration headers | standard block; run block; oldest `TapeCalibrationRunHeader` |
| Checkpoints | with and without EW; many samples |
| Aligned file header | 12 bytes |
| `.tapetoc` | layout B |
| Virtual tape images | legacy single volume; legacy two-volume series; with and without media / set headers; a legacy calibration cartridge interrupted mid-run |

### 11.2 Unit — `Format/`

- Primitives: LEB128 bounds, overlong rejection, ZigZag, `f64`, bool strictness, UTF-8 validation, UTC.
- Envelope: unknown non-critical tag skipped; unknown critical tag refused; duplicate non-repeated tag
  refused; missing required field refused; newer major refused; `BodyLength` overrun / underrun refused.
- Front coding: shared prefixes, surrogate boundary, first entry per set.
- Delta-coded samples: monotone and non-monotone remaining; empty list.
- Every record: round trip; default elision (a defaults-only record is minimal).

### 11.3 Framer and identification

- 2.1 frame round trip; every `FrameStatus` outcome produced deliberately.
- `IdentifyBlock` matrix over every golden block and every 2.1 record kind: TOC copy, header, damaged
  header (flip behind the magic), damaged magic → foreign, unknown `TN` kind → `DamagedRecord`, random
  blocks → foreign, legacy aligned file header → foreign.
- A legacy frame whose length bytes happen to spell `TN` in bytes 0–1 → not 2.1 (bytes 2–3 are zero).

### 11.4 Legacy reader

- Every golden reads; values match hand-written expectations.
- TOC layouts A and B both detected; a B-TOC with a flipped `Compression` value is rejected by
  plausibility; a corrupted TOC fails like today.
- **Legacy → 2.1 → reload** equals the legacy load, field for field.

### 11.5 Round trip / mixed media (× 4 drive profiles)

| Test | Covers |
|---|---|
| `Format_NewTape_WritesOnly21` | every block on the virtual tape identifies as 2.1 or content |
| `Format_LegacyTape_AppendUpgradesToc` | legacy image + new set → TOC 2.1; legacy sets and new set restore |
| `Format_LegacyTape_MediaHeaderUntouched` | BOM block byte-identical after append |
| `Format_LegacyVolume_SetHeaders` | new set on a legacy volume follows its `HasSetHeaders` |
| `Format_MixedSeries_Restore` | legacy volume 1 + 2.1 volume 2 |
| `Format_LegacyIncrementalChain_NewIncremental` | new incremental set on a legacy chain; up-to-date detection works |
| `Format_TapetocImportBoth_ExportOnly21` | §6.4 |
| `Format_ScanMedia_BothFamilies` | scan of legacy, 2.1 and mixed images |
| `Format_Calibration_ResumeLegacyRun` | interrupted legacy run resumes; trail mixes legacy and `TNCP` checkpoints; `InspectMedia` reads it |
| `Format_Calibration_CheckpointHeadroom` | 2.1 checkpoint carries ≥ 3× the samples of legacy in the same block |
| `Format_TimeZoneShift` | backup under one `TimeZoneInfo`, restore under another → identical UTC timestamps |
| `Format_TocSize_Smaller` | 2.1 TOC ≤ legacy TOC for a deep tree |

### 11.6 Existing suite

The ~3.7k tests keep their meaning. Expected mechanical updates: packed-offset assertions (file header
12 → 16 bytes), tests constructing `TapeSerializer` directly, tests inspecting raw header or frame
bytes, and `TocVersion` expectations in scan tests. Tests that exercised legacy *writing* move to
`LegacyFormatWriter` or retire.

---

## 12. Implementation Plan

| Phase | Content | Exit criterion |
|---|---|---|
| 0 | Golden files from the current build; `LegacyFormatWriter` into tests | goldens checked in |
| 1 | `Format/` core; §11.2 | format unit tests green |
| 2 | `Legacy/` readers incl. TOC layout detection and `LegacyIdentify`; §11.4 | every golden reads |
| 3 | TOC, set, file entry on 2.1; `DataFormat`; per-set file header; UTC timestamps | round trips green; existing suite green after mechanical updates |
| 4 | Framer 2.1 (magic first); headers on 2.1; identification dispatch; §11.3 | header, identification and Scan Media tests green |
| 5 | Calibration: `TNCH`, `TNCP`, legacy branch removal | calibration suite green; legacy resume test green |
| 6 | Mixed media, upgrade logging, `.tapetoc`; §11.5 | mixed-media matrix green |
| 7 | Docs: normative spec `docs/TapeNET-Format-2.md`; primer; Design-Compression §4–5 note; Design-TapeHeader "known wart" note | reviewed |

Then `encryption` rebases onto `format-v2`.

---

## 13. File Map

| File | Change |
|---|---|
| `TapeLibNET/Format/*` | **new** — §8.1 |
| `TapeLibNET/Legacy/*` | **new** — §7.1, moved and frozen |
| `TapeLibNET/TapeSerializer.cs` | removed from the product (reader → `Legacy/`, writer → tests) |
| `TapeLibNET/TapeFramer.cs` | 2.1 framing; dispatch to `LegacyFramer` |
| `TapeLibNET/TapeHeader.cs` | shared tags 1–3; magic ↔ `TapeHeaderKind`; `ReadFrom` dispatch |
| `TapeLibNET/TapeMediaHeader.cs`, `TapeSetHeader.cs`, `TapeCalibrationHeader.cs` | 2.1 records; legacy bodies out |
| `TapeLibNET/TapeHeaderBlock.cs` | doc comment: layer table updated, "known wart" resolved |
| `TapeLibNET/TapeHeaderBlock.Identify.cs` | §5.7 dispatch; legacy probe → `LegacyIdentify` |
| `TapeLibNET/TapeCalibrator.cs`, `TapeCalibrationCheckpoint.cs` | `#if` removed; `TNCP` |
| `TapeLibNET/TapeTOC.cs` | `ITapeRecord` for TOC / set / file entry; `DataFormat`; UTC; `TryPeek` dispatch |
| `TapeLibNET/TapeAgentBase.cs` | TOC read / write via format dispatch |
| `TapeLibNET/TapeFileBackupAgent.cs`, `TapeFileRestoreAgent.cs` | per-set file header |
| `TapeLibNET/Scan/TapeScanner.Identify.cs` | TOC version, `TNCP` and newer-record fragments |
| `TapeLibNET/Services/TapeServiceBase*.cs` | upgrade log line |
| `TapeLibNET.Tests/Golden/Legacy/*`, `Helpers/LegacyFormatWriter.cs`, `Format*Tests.cs`, `LegacyReaderTests.cs`, `IdentifyBlockTests.cs`, `MixedMediaTests.cs` | **new** |
| `docs/TapeNET-Format-2.md` | **new** — normative spec |

---

## 14. Remaining Checks During Implementation

1. ~~`TapeCalibrationCheckpoint.cs`~~ — confirmed (§5.9).
2. ~~`TapeTOC.CreateHeader` / `CreateSetHeader`~~ — confirmed: local ticks (§7.1).
3. **Aligned write path** — this design keeps it working with 2.1 file headers. Deleting it is a
   separate decision; the cut is a natural moment.

---

## 15. Review Amendments (normative — they refine §4–§9)

**R1 — Legacy time conversion is one function.** `LegacyTime.FromLocalTicks(long)` =
`new DateTime(ticks, Local).ToUniversalTime()`, used by the legacy TOC, set, file-entry, media-header and
set-header readers alike. Legacy calibration times use `FromUtcTicks`. Identity checks (`MediaId`,
`CreatedUtc`) between a legacy BOM header and an upgraded 2.1 TOC then still agree, because both values
went through the same conversion. Tests: `Format_LegacyTape_IdentityAfterUpgrade` under two time zones.

**R2 — UTC leaks into consumers.** Moving to UTC changes semantics beyond TapeLibNET:
- `FclTapeFileFilter` must expose **local** times through `IFclFileInfo` (FCL relative dates such as
  `today-7d` and absolute dates are local by spec).
- Incremental up-to-date detection compares the TOC time against `FileInfo.LastWriteTimeUtc`, never the
  local getter.
- TapeWinNET / TapeConNET display paths call `ToLocalTime()`; gRPC DTOs carry UTC (`Timestamp` is UTC
  already — verify the mapping no longer double-converts).

**R3 — The critical bit belongs to the tag, not the value.** A reader matches fields on `Number`
(ignoring the bit) once known; the bit matters only for unknown numbers. Writers emit a tag's bit
consistently. `DataFormat` (tag 11) is therefore **always** written critical when present. Separately:
**an unknown value of a known enum field that drives interpretation** (`DataFormat`, `Compression`,
`Codec`, `HashAlgorithm`, `TNFH.Flags`, future `Encryption`) refuses the record (`Unparseable`).

**R4 — Nested record encoding.** A field holding a nested record (`TNST`/13 `File`, `TNST` `KeyEnvelope`)
carries the **complete** nested record (magic + version + length + body) as its `Value`. The outer field
`Length` must equal the nested envelope's total length; a mismatch refuses the record. Field groups
that are not records (`TNCH`/6 `Plan`) carry a bare `Field*` body.

**R5 — Bounded allocation.** Declared lengths are never trusted for allocation: `string`/`bytes`
lengths are capped (`MaxStringBytes = 64 KiB`, `MaxBytesField = 16 MiB`), `BodyLength` of a streamed
record is checked against the remaining input where known, and the reader enforces end-of-body exactly
(overrun and underrun both refuse). Nested-record depth is capped at 4.

**R6 — `TNTH` must fit its block.** `Description` in `TNTH` is clamped with the existing `ClampUtf8`
helper to 8 KiB; `WrittenBy` to 256 bytes. The writer asserts `TNTH` length ≤ TOC block size − 64.

**R7 — Minor version semantics.** Readers never gate on `Minor`; it is informational (diagnostics and
`TocVersion` reporting). New non-critical fields do not require a minor bump; a minor bump marks a
release that introduced new *critical* tags, so error messages can name the needed version.

**R8 — Identification wording.** §5.7's "bytes 2–3 are zero" holds for 16 KiB header blocks but not
for large calibration run blocks. The safe statement: a legacy frame length is < 2^24, so its **byte 3 is
zero**, while every 2.1 magic has a letter at byte 3. The dispatch tests the full 4-byte magic.

**R9 — Legacy TOC detection memory.** §7.2 buffers a whole legacy TOC copy. Bound it: buffer up to
`LegacyTocMaxInMemory = 256 MiB`; beyond that spill to a temporary file stream and re-parse from it.
The 2.1 path stays fully streamed.

**R10 — Front coding is per set and per pass.** The `CountingStream` measuring pass and the real pass
must run the identical front-coding state machine; implement it once in `TapeNameFrontCoder` (stateful,
`Reset()` per set) and use it in both passes and in the reader.

**R11 — `.tapetoc` hash.** `SaveTOCToFile` keeps the trailing CRC-64; `LoadTOCFromFile` dispatches on
the first bytes (`TNTH` vs. legacy). An unknown `TN` magic in a `.tapetoc` reports "written by a newer
TapeNET" rather than "corrupt".

---

## 16. Detailed Implementation Plan (for GitHub Copilot)

Work on branch `format-v2`. Each step ends with `dotnet build` and the named tests green; commit per
step. Follow `.github/copilot-instructions.md` (C# 12, file-scoped namespaces, primary constructors,
`m_` fields in TapeLibNET, constants for magic numbers, nullable discipline). Do not change behaviour
outside the step's scope.

### Phase 0 — Freeze legacy

1. **Add `TapeLibNET.Tests/Helpers/LegacyFormatWriter.cs`.** Copy today's `TapeSerializer` write half,
   `TapeFramer.Pack`, and every `SerializeTo` body (TOC, set, file entry, media / set / calibration
   headers, `TapeCalibrationRunHeader`, checkpoint, 12-byte file header) verbatim as static methods.
   Add a `Layout` switch (`A`, `B`) and a `withMediaId` switch for the TOC.
2. **Add `GoldenGenerator` test (category `Golden`, skipped by default)** that, on the *current* build,
   writes every golden of §11.1 into `TapeLibNET.Tests/Golden/Legacy/` (binary files, plus a
   `*.expected.json` with the hand-checkable field values). Virtual tape images use the existing
   virtual-drive image export.
3. **Run it once, check in the goldens**, mark them `CopyToOutputDirectory=PreserveNewest` in
   `TapeLibNET.Tests.csproj`.
4. **Add `LegacyGoldenTests`** asserting that the *current* readers load every golden and match the
   JSON. This test must stay green through every later phase (it then exercises `Legacy/`).

### Phase 1 — Format core (`TapeLibNET/Format/`)

5. **`TapeFormat.cs`** — constants: magics (`ReadOnlySpan<byte>` u8 literals), `Major = 2`,
   `Minor = 1`, per-record nested static classes of tag numbers, caps from R5, `FileHeaderSize = 16`,
   `LegacyFileHeaderSize = 12`; enum `TapeDataFormat { Legacy = 1, V2 = 2 }`;
   `TapeFormatException` (message + `FormatErrorKind { NewerMajor, UnknownCritical, MissingRequired,
   Duplicate, Overrun, Underrun, BadValue, UnknownKind }`).
6. **`TapePrimitives.cs`** — static span-based encode/decode for §4.1 (`varuint` with overlong / >10-byte
   rejection, ZigZag `varint`, LE fixed ints via `BinaryPrimitives`, `f64`, strict `bool`, UTF-8 with
   `throwOnInvalidBytes`, `guid`, `timestamp` → `DateTimeKind.Utc`). Stream helpers use `ReadExactly`.
7. **`TapeRecordWriter.cs`** — wraps a `Stream`; `BeginRecord(magic)` / `EndRecord()` with an
   `ArrayPool`-backed buffer stack for small records; `WriteField(tag, value)` overloads per type with
   default elision helpers (`WriteFieldIfNot(tag, value, default)`); `WriteNestedRecord<T>(tag, T)`
   (R4); `WriteGroup(tag, Action<TapeRecordWriter>)` for `Plan`. Critical bit via `TapeTag.Critical(n)`.
8. **`CountingStream.cs`** — write-only `Stream` that counts bytes; used for the set measuring pass.
9. **`TapeRecordReader.cs`** — `ReadEnvelope(expectedMagic)` (major check, R7), field iterator
   `bool TryNextField(out int number, out bool critical)`, typed `Read*` for the current value,
   `Skip()`, `RefuseUnknown()` (R3), `SeenTags` bitset for duplicates and `RequireTags(params int[])`,
   `ReadNestedRecord<T>()`, end-of-body enforcement and caps (R5). Works over a `Stream` (TOC) and over
   `ReadOnlySpan<byte>` (framed blocks).
10. **`ITapeRecord.cs`** — `ITapeRecord<TSelf>` and `ITapeFramedRecord<TSelf>` per §8.1.
11. **`TapeNameFrontCoder.cs`** (R10) — `Encode(string full, out int prefix, out string suffix)` with
    surrogate back-off; `Decode(int prefix, string suffix)`; `Reset()`.
12. **`TapeSampleCoder.cs`** — delta coding of calibration samples (§5.9).
13. **Tests `FormatPrimitivesTests`, `FormatEnvelopeTests`, `FrontCoderTests`, `SampleCoderTests`**
    covering every bullet of §11.2 plus R3–R5.

### Phase 2 — Legacy readers (`TapeLibNET/Legacy/`, all `internal`)

14. **Move `TapeDeserializer` → `Legacy/LegacyDeserializer.cs`**; replace the single `Read` in
    `DeserializeBytes` with `ReadExactly`; fix `Deserialize<List,T>` to throw on a `null` item instead
    of dropping it. Add `LegacyTime` (R1).
15. **`LegacyFramer.cs`** — today's `TapeFramer` unpack half, verbatim.
16. **`LegacyHeaderReader.cs`** — preamble + kind byte, media (tolerant `HasSetHeaders`), set,
    calibration, and `TapeCalibrationRunHeader` (lifted from the `#if LEGACY_…` block); converts times
    per R1.
17. **`LegacyCheckpointReader.cs`**, **`LegacyFileHeader.cs`** (12-byte `Check`), **`LegacyIdentify.cs`**
    (today's `TryPeek` + offset-4 probe verbatim).
18. **`LegacyTocReader.cs`** — §7.2 algorithm: buffer (R9), try layout B then A, CRC-64 anchor,
    plausibility, header versions `0x0101`/`0x0102`; produces `TapeTOC` with every set
    `DataFormat = Legacy`.
19. **Switch `LegacyGoldenTests` to the `Legacy/` readers; add `LegacyReaderTests`** (§11.4 except the
    2.1 round trip, which lands in Phase 3).

### Phase 3 — TOC on 2.1

20. **`TapeTOC.cs`** — add `TapeSetTOC.DataFormat` (default `V2`); switch `DateTime.Now` →
    `DateTime.UtcNow`; add `WrittenBy`. Implement `WriteTo`/`ReadFrom` for TOC header, set (two-pass
    via `CountingStream`, R10), file entry (front coding), `TNTE`. Keep public API shapes.
21. **`Format/TapeTocFormat.cs`** — `Write(Stream, TapeTOC)` (sequence §5.1, TNTH clamp R6) and
    `Read(Stream)` with cross-checks (§9, `TNTE`).
22. **`Format/TapeFormatDispatch.cs`** — `ReadToc(Stream)`: peek 4 bytes; `TNTH` → `TapeTocFormat`,
    other `TN` → newer-build error, else `LegacyTocReader`.
23. **`TapeAgentBase`** — `BackupTOCCore` / `SaveTOCToFile` use `TapeTocFormat.Write` inside the existing
    `HashingStream`; `RestoreTOCCore` / `LoadTOCFromFile` / `RestoreTOCAt` use `ReadToc` (R11).
    `TapeTOC.TryPeek` dispatches (§8.4).
24. **`Format/TapeFileHeader.cs`** (16-byte `TNFH`, `Write`, `Check` refusing unknown flags);
    `TapeFileInfo.EstimateSerializedHeaderSize(TapeDataFormat)`; update all call sites in `TapeTOC.cs`
    and `TapeFileRestoreAgent.cs`.
25. **Backup agents** write `TNFH`; **restore agents** pick the check per `CurrentSetTOC.DataFormat`
    (§6.1), including the aligned paths.
26. **UTC consumers (R2)** — file timestamps captured / applied via `…Utc` APIs; incremental compare;
    `FclTapeFileFilter` local view; TapeWinNET / TapeConNET display; gRPC mapping.
27. **TOC capacity estimator** — conservative 2.1 estimate (no shared prefix, max varint widths).
28. **Delete product `TapeSerializer` write half**; fix compile errors; mechanically update the
    existing suite (§11.6). Add `Format_TimeZoneShift`, `Format_TocSize_Smaller`, legacy → 2.1 → reload.

### Phase 4 — Framer, headers, identification

29. **`TapeFramer.cs`** — §8.3 API; 2.1 frame (§5.6) with bounds-before-trust; `TryUnpack` dispatches to
    `LegacyFramer` + `ReadLegacy` when byte 0–1 ≠ `TN`.
30. **`TapeHeader` hierarchy** — shared tags 1–3; `WriteTo`/`ReadFrom`; magic ↔ `TapeHeaderKind`;
    `ReadFrom` dispatch replaces `ConstructFrom`; `ReadLegacy` forwards to `LegacyHeaderReader`;
    remove `ReadSetHeadersFlag` from the product.
31. **`TapeHeaderBlock.Identify.cs`** — §5.7 dispatch (R8); `CarriesRecordSignature` update; doc comment
    in `TapeHeaderBlock.cs`.
32. **`TapeScanner.Identify.cs`** — `TocVersion = 0x0201`, `TNCP` fragment kind, newer-record fragment.
33. **Tests** `FramerTests` (every `FrameStatus`), `IdentifyBlockTests` matrix (§11.3).

### Phase 5 — Calibration

34. **`TapeCalibrationHeader`** → `TNCH` with nested `Plan` group; **`TapeCalibrationCheckpoint`** →
    `TNCP` with `TapeSampleCoder`; `TapeCalibrationFramer` follows `TapeFramer`.
35. **`TapeCalibrator`** — remove `#if LEGACY_TapeCalibrationRunHeader`; `ReadRecord<T>` constraint →
    `ITapeFramedRecord<T>`; resume walks accept both checkpoint families.
36. **Tests** `Format_Calibration_ResumeLegacyRun`, `Format_Calibration_CheckpointHeadroom`; full
    calibration suite green.

### Phase 6 — Mixed media and services

37. **`TapeServiceBase*`** — one-time log *"TOC upgraded to format 2.1 …"* when a legacy-loaded TOC is
    first written (flag on `TapeTOC`: `LoadedFromLegacy`).
38. **`MixedMediaTests`** — every row of §11.5 × 4 drive profiles, plus
    `Format_LegacyTape_IdentityAfterUpgrade` (R1) and `Format_NewerRecord_Refused` (unknown critical tag
    in a TOC and a header).
39. **Full solution test run** (`TapeLibNET.Tests`, `TapeConNET.Tests`, `FclNET.Tests`) green.

### Phase 7 — Documentation

40. **`docs/TapeNET-Format-2.md`** — normative spec extracted from §4–§5 and §15, with byte-level
    examples generated from the 2.1 goldens.
41. **Update** `TapeNET-Context-Primer.md` (format section, test counts), Design-Compression §4–5 note,
    Design-TapeHeader "known wart" note, Design-Encryption pointer (§10).
42. **Add 2.1 goldens** (`TapeLibNET.Tests/Golden/V21/`) generated by the finished build, with a test
    that byte-compares fresh output to them — this freezes 2.1 the way Phase 0 froze legacy.
