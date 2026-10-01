# Design-Format-v2 — Appendix A: Virtual Media State

**Status:** for implementation, together with Design-Format-v2 v4. **Replaces:** §5.6 and step 33 of the main
document. **Last updated:** 2026-10-01

---

## A.1 Summary

`VirtualTapeMedia` is the last product user of `TapeSerializer` and the base of nearly every unit test. It moves
to a 2.1 record — **kind `0x0F01`, framed as `Record ‖ CRC-64`** — written to the same metadata stream (`.vrt`).

Three decisions shape it:

1. **Store only what cannot be derived.** `BeginAtBlock` and `StreamOffset` are fully determined by the order of
   the blocks (logical blocks and stream bytes are both contiguous — an invariant the class already asserts).
   The new format stores per virtual block only *kind, block size, block count*, and **rebuilds** the rest on
   load. Metadata becomes consistent by construction, and about 5× smaller.
2. **Never wipe a tape whose metadata cannot be read.** Today an unreadable `.vrt` silently creates a new,
   empty medium over the existing content (§A.3). This branch fixes that before changing the format.
3. **Migrate early.** The migration lands right after the format core (Phase 1), not in Phase 6: it is small,
   self-contained, and turns the whole ~3.7k-test suite into a stress test of the new writer and reader.

---

## A.2 What Is Persisted Today

`SaveState()` / the deserializing constructor (`VirtualTapeMedia.cs`):

| Field | Type today | Derivable? |
|---|---|---|
| signature `TF` + `StateVersion` 0x0100 | 4 B | — |
| `m_minBlockSize`, `m_maxBlockSize`, `m_defaultBlockSize` | 3 × u32 | no |
| `m_capacity` | i64 | no |
| `m_name` | string | no |
| `m_bytesWritten` | i64 | **yes** — Σ `DataLength` (asserted by `AssertByteTotalConsistent`) |
| `List<VirtualTapeBlock>` — per block: | count + 30 B each | |
| &nbsp;&nbsp;`IsMark` | bool | from kind |
| &nbsp;&nbsp;`MarkType` | u8 | no (kind) |
| &nbsp;&nbsp;`BlockSize` | u32 | no (data only) |
| &nbsp;&nbsp;`BeginAtBlock` | i64 | **yes** — previous `EndBlock` |
| &nbsp;&nbsp;`DataLength` | i64 | `BlockSize × BlockCount` |
| &nbsp;&nbsp;`StreamOffset` | i64 | **yes** — previous data block's `StreamOffset + DataLength` (asserted by `AssertStreamLengthMatchesSummation`) |

Not persisted (unchanged): current position (load rewinds), current block size (load resets to default), EW
profile, odometer, fault injectors.

**Why the derivations hold.** `WriteBlocks` / `WriteMark` always first call `TruncateFromCurrentPosition()` and
then append at the head: blocks follow each other with no logical gap, and data bytes follow each other in the
stream with no gap. A torn write (fault injection) leaves *unreferenced* bytes beyond the described length —
the stream may be **longer** than the described data, never shorter or gapped.

---

## A.3 Hazard Found: Unreadable Metadata Wipes the Tape

```
LoadMedia()                                  (VirtualTapeDriveBackend, MediaMode = OpenOrCreate — the default)
  └─ VirtualTapeMedia.TryCreateFromState()   catch { return null; }   ← ANY failure looks like "no state"
       └─ null  →  allowCreateNew
                   TruncateStream(m_contentStream)          ← content file emptied
                   TruncateStream(m_contentMetadataStream)
                   new VirtualTapeMedia(...)                 ← blank medium
```

Any metadata the reader rejects — a torn `.vrt` after a crash mid-`SaveState` (it rewrites in place from
position 0), a bug, or a **newer format opened by an older build** — destroys the virtual cartridge without a
word. `MediaMode` resets to `OpenOrCreate` after every load, so every later reload is exposed too.

**Fix (Phase 0 of this appendix, independent of the format):**

```csharp
/// Outcome of probing a metadata stream.
internal enum VirtualMediaStateProbe { Absent, Loaded, Unreadable }

public static VirtualTapeMedia? TryCreateFromState(..., out VirtualMediaStateProbe probe, out string? reason)
```

- `Absent` — `metadataStream` null or length 0 → create new (as today, when the mode allows).
- `Loaded` — as today.
- `Unreadable` — **never create, never truncate.** `LoadMedia` fails with `ERROR_FILE_CORRUPT` and the reason
  (e.g. *"virtual media metadata unreadable: CRC mismatch"*), whatever the `MediaMode` — except `Create`, which
  is an explicit "start over" and keeps truncating.
- The old overload stays as a thin wrapper for callers that do not care (`Absent`/`Unreadable` → null), but
  `LoadMedia` uses the new one.

Test: `LoadMedia_UnreadableMetadata_FailsWithoutTruncating` — corrupt one metadata byte; `OpenOrCreate` load
fails; content stream length unchanged.

**Known residual risk:** a build *without* this fix (any build older than `format-v2`) that opens a virtual tape
saved in the new format still wipes it. Stated in the release notes: *open 2.1 virtual tapes only with
TapeNET ≥ this version.* Virtual tapes are developer and test artefacts; real cartridges are unaffected.

---

## A.4 The 2.1 Record

Kind `0x0F01` (`VirtualMediaState`), framed `Record ‖ CRC-64` (inline frame, §4.5 of the main document),
written from position 0 of the metadata stream, followed by `SetLength(position)` as today.

| # | Field | Type | Flags |
|---|---|---|---|
| 1 | `MinBlockSize` | varuint | R |
| 2 | `MaxBlockSize` | varuint | R |
| 3 | `DefaultBlockSize` | varuint | R |
| 4 | `Capacity` | varuint | R |
| 5 | `BytesWritten` | varuint | R — cross-check only |
| 6 | `BlockCount` | varuint | R — number of virtual blocks, cross-check |
| 7 | `TotalLogicalBlocks` | varuint | R — `TotalBlockCount`, cross-check |
| 32 | `Name` | string | "" |
| 48 | `Blocks` | bytes | run-coded block list (below) |

**Run-coded block list** — one entry per virtual block, in order:

```
Entry := Kind:u8  [ BlockSize:varuint  BlockCount:varuint ]     ← the bracket only for Kind = Data
Kind  := 0 Data | 1 Filemark | 2 Setmark                          (EndOfData = 3 is never stored → refused)
```

A data entry costs 3–7 bytes, a mark 1 byte (legacy: 30 bytes each). A virtual block list is short in practice
(`WriteBlocks` coalesces contiguous same-size writes), but multi-volume and setmark-heavy tests save it on every
`Flush`, so the saving is worth having.

**Load — rebuild and validate:**

```csharp
long block = 0, streamOffset = 0;
foreach (entry in Blocks)
{
    VirtualTapeBlock vb = entry.Kind == Data
        ? VirtualTapeBlock.CreateData(block, entry.BlockSize, entry.BlockSize * entry.BlockCount, streamOffset)
        : VirtualTapeBlock.CreateMark(block, entry.Kind.ToMarkType());
    // validate: kind known; data → BlockSize ∈ [Min..Max], BlockCount ≥ 1, no overflow
    m_virtualBlocks.Add(vb);
    block = vb.EndBlock;
    if (!vb.IsMark) streamOffset += vb.DataLength;
}
// cross-checks → FormatException("…") → Unreadable (§A.3):
//   entries == BlockCount;  block == TotalLogicalBlocks;  streamOffset == BytesWritten;
//   content stream length ≥ BytesWritten (longer is fine: torn-write orphans);
//   Min ≥ 1, Min ≤ Default ≤ Max (existing checks)
m_bytesWritten = streamOffset;
```

**Save** — the inverse: one entry per `VirtualTapeBlock`, block count = `DataLength / BlockSize`.
`VirtualTapeBlock` drops `ITapeSerializable`.

---

## A.5 Legacy Metadata

`Legacy/LegacyVirtualMediaState.cs` — today's constructor body, verbatim, reading through `LegacyDeserializer`:

- **Dispatch** on the first bytes: `TpN#` → 2.1; `TF` → legacy (`version ≤ 0x0100`); else `Unreadable`.
- **Normalize to the 2.1 model:** after reading, verify contiguity (`BeginAtBlock == previous EndBlock`,
  `StreamOffset == running data offset`, `DataLength % BlockSize == 0`). A legacy file that violates it cannot
  be represented and is `Unreadable` with reason *"legacy virtual media metadata is not contiguous"* — never
  silently repaired. (Not expected in practice: the class has asserted contiguity in DEBUG builds.)
- **Upgrade lazily.** Loading legacy metadata does not rewrite it; the first `SaveState` (on any change, or on
  `Flush` when dirty) writes 2.1. Opening a virtual tape read-only leaves the file as it was.
- Log once per load at Information: *"Virtual media '{name}': legacy metadata — will be saved in format 2.1 on
  the next change."*

`LegacyFormatWriter` (tests) gains `WriteVirtualMediaState(...)`, today's `SaveState` body verbatim.

---

## A.6 Code Changes

| File | Change |
|---|---|
| `Virtual/VirtualTapeMedia.cs` | `SaveState` → `VirtualMediaStateRecord.Write`; deserializing ctor → `VirtualMediaStateRecord.Read` (dispatch, rebuild, validate); `TryCreateFromState` + probe overload; `StateVersion` constant removed; `VirtualTapeBlock : ITapeSerializable` removed |
| `Virtual/VirtualMediaStateRecord.cs` | **new** — schema (§A.4), run coder, rebuild / validate |
| `Virtual/VirtualTapeDriveBackend.cs` | `LoadMedia` uses the probe for content **and** initiator media; `Unreadable` → `ERROR_FILE_CORRUPT`, no truncation (except `MediaMode == Create`) |
| `Legacy/LegacyVirtualMediaState.cs` | **new** — §A.5 |
| `TapeLibNET.Tests/Helpers/LegacyFormatWriter.cs` | `WriteVirtualMediaState` |

Untouched: `MemoryMediaSnapshot`, `CaptureMemorySnapshot`, `InsertMemoryMedia` — they carry metadata as opaque
bytes. Multi-volume tests that swap snapshots therefore exercise save → load of the new record on every swap.

**Optional, not in scope:** crash-safe metadata for file-backed media (write `.vrt.tmp`, then
`File.Replace`). `SaveState` only sees a `Stream`, so this would need the backend to own it. With the CRC and the
§A.3 fix, a torn `.vrt` is now detected and refused instead of wiping the tape — the dangerous half is solved.

---

## A.7 Tests

**New — `VirtualMediaStateTests`:**

| Test | Covers |
|---|---|
| `State_RoundTrip_DataAndAllMarkKinds` | mixed block sizes, filemarks, setmarks; rebuilt `BeginAtBlock` / `StreamOffset` equal the originals |
| `State_RoundTrip_Empty` | no blocks |
| `State_RoundTrip_AfterTruncate` | overwrite mid-tape, then save / load |
| `State_TornWriteOrphan_StreamLongerAccepted` | DEBUG `TearOnce`; reload succeeds; orphan unreferenced |
| `State_CrossCheckMismatch_Unreadable` | tampered `BytesWritten`, `BlockCount`, `TotalLogicalBlocks` |
| `State_EndOfDataKind_Refused` | kind 3 in the list |
| `State_BlockSizeOutOfRange_Refused` | data entry outside `[Min..Max]` |
| `State_StreamShorterThanDescribed_Unreadable` | content truncated externally |
| `State_CrcMismatch_Unreadable` | one flipped byte |
| `State_Size_SmallerThanLegacy` | same media, both writers |
| `Legacy_Golden_Loads` | Phase 0 golden `.vrt` (single volume, two volumes, initiator partition) |
| `Legacy_NonContiguous_Unreadable` | crafted via `LegacyFormatWriter` |
| `Legacy_LoadedReadOnly_FileUnchanged` | open, read, unload without changes → bytes identical |
| `Legacy_UpgradedOnFirstChange` | write one block → metadata now starts with `TpN#` |

**New — `VirtualTapeDriveBackendLoadTests`:**

| Test | Covers |
|---|---|
| `LoadMedia_UnreadableMetadata_FailsWithoutTruncating` | `OpenOrCreate` and `Open`, content and initiator |
| `LoadMedia_UnreadableMetadata_CreateModeStartsOver` | explicit `Create` still truncates |
| `LoadMedia_AbsentMetadata_CreatesNew` | unchanged behaviour |

**Regression:** the full suite — it saves and reloads virtual media state constantly — green after the switch.

---

## A.8 Placement in the Implementation Plan

Step 33 of the main plan is replaced by three steps, moved forward:

**Phase 0 (after step 5)**

- **5a. Wipe-hazard fix (§A.3)** on the *current* format: probe overload, `LoadMedia` without truncation on
  `Unreadable`, `VirtualTapeDriveBackendLoadTests`. Ships value even if the rest slipped.
- **5b. Golden `.vrt`** — already in §11.1 ("virtual tape images"); make sure each image includes its metadata
  file, plus one with an initiator partition.

**Phase 1 (after step 11, before the legacy readers)**

- **11a. `VirtualMediaStateRecord`** (§A.4) on the format core; switch `SaveState` and the loader; legacy
  metadata at this point still read through the not-yet-moved `TapeDeserializer`.
- **11b. Full suite green** — the first broad end-to-end exercise of the format core.

**Phase 2 (with step 13)**

- **13a. `LegacyVirtualMediaState`** in `Legacy/`; `Legacy_*` tests from §A.7.

After step 11a, `TapeSerializer`'s write half has no product users left besides the TOC, file header, headers
and checkpoints — which phases 3–6 migrate as planned.

---

## A.9 Main-Document Edits

- §5.6 → *"See Appendix A."*
- §8.1 file list: add `Virtual/VirtualMediaStateRecord.cs`.
- §11.5: `Format_VirtualMedia_LegacyMetadataOpens` → covered by §A.7.
- §12: replace step 33 per §A.8; renumbering not required (5a/5b, 11a/11b, 13a).
- §13 file map: `Virtual/VirtualTapeDriveBackend.cs` — load probe.
- §15 open item 1 → resolved.
