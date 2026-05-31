# Design: Per-File Software Compression (ZSTD) for TapeNET

**Status:** Implemented in `TapeLibNET` (library + tests). UI exposure (WPF, CLI) is pending.  
**Branch:** `dev`  
**Last updated:** 2026-05-31

---

## 1. Motivation and Scope

TapeNET already supports hardware compression — a drive-level toggle that instructs the tape
drive's built-in compressor to compress the on-tape byte stream. Hardware compression is opaque
to the software: the drive handles it transparently, and the software has no visibility into
per-file ratios, no fallback for incompressible data, and no per-file metadata.

Software compression adds a third axis to the existing (`HashAlgorithm`, `BlockSize`) per-set
configuration model:

| Axis | Scope | Stored in |
|------|-------|-----------|
| `HashAlgorithm` | Per set | `TapeSetTOC` |
| `BlockSize` | Per set | `TapeSetTOC` |
| `Compression` + `CompressionLevel` | Per set (applied per file) | `TapeSetTOC` |
| `Codec` (Stored / Zstd) | Per file | `TapeFileInfo` |

The design goal is byte-for-byte identical restore regardless of which compression mode a set
was written with, without any user intervention on restore.

---

## 2. Codec Choice: ZSTD via ZstdNet

**ZstdNet 1.5.7** (MIT licence) wraps **libzstd** (BSD-3-Clause / GPLv2 dual licence,
redistributable under BSD-3-Clause with attribution). The native `libzstd.dll` is bundled
in `runtimes/win-x64/native` by the NuGet package and flows automatically to the output
directories of all dependent projects (`TapeLibNET`, `TapeConNET`, `TapeWinNET`,
`TapeServiceNET`).

A `THIRD-PARTY-NOTICES` file at the repo root records the required BSD-3-Clause attribution
for libzstd.

ZSTD was chosen over alternatives (LZ4, Brotli, Deflate) for its combination of:
- **Streaming API** (`ZstdNet.CompressionStream` / `DecompressionStream`) — no full-file
  buffering required, files of arbitrary size are handled with bounded memory.
- **Adjustable levels 1–19** — `Fast=3`, `Balanced=5` (default), `High=9` cover realistic
  tape workloads; levels above ~12 have diminishing returns and are exposed for power users.
- **Concatenated-frame transparency** — `DecompressionStream` reads multiple consecutive ZSTD
  frames as a single byte stream. This is exploited by `ProbingCompressionStream` (§5) to
  write a probe frame followed by a live frame with no separator and no format change.
- **`ZSTD_compressBound`** — exposed as `Compressor.GetCompressBound(int)`, used to size the
  probe compression buffer precisely.

---

## 3. Compression Modes and Levels

### `TapeCompression` enum (`TapeLibNET/TapeCompression.cs`)

```
None      — no software processing; HW compression is disabled
Hardware  — drive's built-in compressor; no software involvement
Software  — ZSTD, applied per-file with configurable level
```

`None` and `Hardware` are mutually exclusive with `Software`. Selecting `Software` always
disables hardware compression (§7). Hardware mode leaves the drive's own compressor enabled.

### Level constants (`ZstdLevel`)

| Constant | Value | Intent |
|----------|-------|--------|
| `Min` | 1 | Fastest, weakest ratio |
| `Fast` | 3 | Fast preset |
| `Balanced` / `Default` | 5 | Default for new sets |
| `High` | 9 | Better ratio, moderate speed cost |
| `Max` | 19 | Best ratio, slow |

`ZstdLevel.Clamp(int)` normalises any caller-supplied value to `[1, 19]`.

### `CompressionPreset` helper

A shared static helper (`TapeLibNET/TapeCompression.cs`) translates between the human-readable
strings used by the CLI and WPF and the `(TapeCompression, level)` pair stored in the TOC:

| Specifier | Mode | Level |
|-----------|------|-------|
| `off`, `none`, `""` | `None` | — |
| `hardware` | `Hardware` | — |
| `low` | `Software` | `Fast` (3) |
| `medium` | `Software` | `Balanced` (5) |
| `high` | `Software` | `High` (9) |
| `1`–`19` | `Software` | numeric |

`CompressionPreset.TryParse`, `DisplayName`, `PresetName`, and `ToSpecifier` provide the
full round-trip without duplication between the two app front-ends.

---

## 4. Per-Set Metadata (`TapeSetTOC`)

Two fields added to `TapeSetTOC` (`TapeLibNET/TapeTOC.cs`):

```csharp
public TapeCompression Compression     { get; set; } = TapeCompression.None;
public int             CompressionLevel{ get; set; } = ZstdLevel.Default;
```

Both are serialized as consecutive `int32` fields in `SerializeTo` / `ConstructFrom`, appended
after the existing fields so older tapes (missing these bytes) are read back as `None`/`5` by
the default-value path.

`CopyFrom` propagates both to continuation sets, so a multi-volume backup inherits the
original set's compression configuration automatically.

`TapeSetTOCParams` (the immutable snapshot passed at set-creation time) carries both fields;
`TapeSetTOC.ToParams()` and the `AddNewSetTOC` / continuation-creation paths all route through
this record, ensuring the values flow end-to-end without manual threading.

---

## 5. Per-File Codec Flag (`TapeFileInfo`)

Auto store-fallback (§6) means a single set may contain a mix of `Stored` and `Zstd` files.
Restore must therefore know per-file which codec was used:

```csharp
// TapeLibNET/TapeCompressionStream.cs
public enum TapeFileCodec : byte
{
	Stored = 0,   // uncompressed (None/Hardware path, or auto-fallback)
	Zstd   = 1,   // ZSTD-compressed body
}

// TapeLibNET/TapeTOC.cs — TapeFileInfo
internal TapeFileCodec Codec { get; set; } = TapeFileCodec.Stored;
```

`TapeFileInfo.SerializeTo` appends `(byte)Codec` after `SizeOnTape`. `ConstructFrom`
reads it with `DeserializeBytes(1)` and defaults to `Stored` when the byte is absent
(forward-compatibility with tapes written before compression support was added).

`EstimateSerializedSize` adds 1 byte for this field.

---

## 6. Adaptive Compression: `ProbingCompressionStream`

### Problem: avoid full-file buffering, avoid committing to compression prematurely

Naïvely, detecting whether compression wins requires compressing the whole file first. For
multi-gigabyte files this is prohibitive. The solution is a **probe window**: compress the
first 128 KiB (= `ZSTD_BLOCKSIZE_MAX`, one ZSTD block), compare sizes, then decide.

### `ProbingCompressionStream` (`TapeLibNET/TapeCompressionStream.cs`)

A write-only `Stream` that wraps the packer write façade (`wstream`):

```
Source → HashingStream (uncompressed hash) → ProbingCompressionStream → wstream
```

**Probe phase** (first ≤ 128 KiB written):  
Every byte goes simultaneously into `Session.RawBuf` (plain) and through
`_probeZstream` into `Session.CompBuf` (compressed). Both `MemoryStream` buffers live
in a `Session` object (see §6.1) and are reused across files.

**Commit** (triggered when the probe window fills, or on `Dispose` for smaller files):  
`_probeZstream` is disposed to finalize the ZSTD frame, making `CompBuf.Length` accurate.
Then:

- `CompBuf.Length < RawBuf.Length` → **compression wins**: flush `CompBuf` to `wstream`;
  open a live `ZstdNet.CompressionStream` wrapping `wstream` for the remainder. The live
  stream produces a second ZSTD frame immediately after the probe frame. libzstd reads
  multiple concatenated frames transparently on decompress, so the reader sees a single
  unbroken byte stream.  
  **Sub-probe case** (file ≤ 128 KiB): no live stream is opened. The probe frame is the
  complete output. Opening a live stream here would emit a spurious empty frame.  
  `FinalCodec = TapeFileCodec.Zstd`.

- `CompBuf.Length ≥ RawBuf.Length` → **store wins** (incompressible): flush `RawBuf` to
  `wstream`; subsequent bytes pass through `_inner.Write` directly.  
  `FinalCodec = TapeFileCodec.Stored`.

**`FinalCodec` timing** — a subtle correctness requirement: `FinalCodec` is only valid
*after* `Dispose()` has run `Commit()`. For files smaller than the probe window, `Commit()`
runs *only* during disposal. The backup agent therefore calls `probing.Dispose()` explicitly
before reading `probing.FinalCodec`, rather than relying on the implicit `using`-statement
disposal that would follow the codec read.

#### 6.1 `ProbingCompressionStream.Session`

A nested class that owns the session-scoped resources shared across all files in one backup
set:

- `ZstdCodec` — wraps `CompressionOptions` (which holds a native ZSTD context); re-created
  only when the compression level changes (multi-volume edge case).
- `RawBuf` — `MemoryStream` pre-allocated to `ProbeLength` (128 KiB).
- `CompBuf` — `MemoryStream` pre-allocated to `Compressor.GetCompressBound(ProbeLength)`
  bytes. Using the library's own `GetCompressBound` (instead of a heuristic percentage)
  guarantees the buffer never reallocates even when the probe data is fully incompressible
  and ZSTD adds its worst-case frame overhead.

`Session.ResetBuffers()` clears both streams to length zero between files; no heap
allocation per file. `TapeFileBackupAgent` holds the session as `_compressionSession`
(lazily created on first Software-mode file) and disposes it in `Dispose(bool)` via the
base class `TapeFileAgent.Dispose` pattern.

---

## 7. Hardware Compression Interlock

When a tape drive's hardware compressor is active, it re-compresses bytes that arrive from
the host. For a Software-compressed set this would double-compress; for a None set it wastes
drive resources. For a Hardware set the drive's compressor *is* the compression.

The interlock is applied in **two places**:

**Backup** (`TapeFileBackupAgent.BeginWriteContentForCurrentSet`):
```csharp
Drive.SetHardwareCompression(TOC.CurrentSetTOC.Compression == TapeCompression.Hardware);
```

**Restore / Validate / Verify** (`TapeFileRestoreAgent.BeginReadContentForCurrentSet`):
```csharp
Drive.SetHardwareCompression(TOC.CurrentSetTOC.Compression == TapeCompression.Hardware);
```

`TapeDrive.SetHardwareCompression(bool)` reads the current `DriveParameters` snapshot,
overrides only the compression flag, then calls `m_backend.SetDriveParameters(...)`.
Failure is silently swallowed (matching `SetOptimalDriveParams`), so drives that don't
support dynamic toggle are not broken.

---

## 8. Backup Pipeline Integration

`TapeFileBackupAgent.BackupFile` (packed path):

```
Open file (TapeBackupSourceStream / BackupRead)
  ↓
HashingStream          ← hashes uncompressed bytes
  ↓
ProbingCompressionStream   ← probe → ZSTD or passthrough
  ↓
wstream (packer write façade)
```

Header serialization is **always uncompressed** (written directly to `wstream` before the
compression stream is opened), so `TapeFileInfo.DeserializeAndCheckHeaderFrom` on restore
can read the header without knowing the codec.

Hash coverage is deliberately **codec-independent**: `HashingStream` wraps the source
*before* the compressor, so `tfi.Hash` always reflects the original uncompressed bytes.
This means the same hash validates whether the set is stored, hardware-compressed, or
software-compressed — no hash algorithm changes on restore.

After all file bytes are copied, `probing.Dispose()` seals the ZSTD frame (if compressed)
and sets `FinalCodec`. The codec is then passed out of `BackupFile` as an `out` parameter
and registered with `PackedCommitTracker`.

`PackedCommitTracker.PendingEntry` carries `TapeFileCodec Codec` alongside the hash.
`OnCommitted` stamps `Codec` onto the final `TapeFileInfo` that is promoted into the TOC,
so the codec persists across TOC save/load.

---

## 9. Restore Pipeline Integration

`TapeFileRestoreAgent.RestoreNextFile` (packed path):

```csharp
Stream bodyStream = tfi.Codec == TapeFileCodec.Zstd
	? new DecompressionFilterStream(rstream)
	: rstream;

// bodyStream → RestoreFileCore(fileInfo, bodyStream, hasher)
```

`DecompressionFilterStream` wraps `rstream` (which is size-limited to `tfi.SizeOnTape`)
and presents a plain read stream of uncompressed bytes. `RestoreFileCore` — and all three
restore agent overrides (`TapeFileRestoreAgent`, `TapeFileValidateAgent`,
`TapeFileVerifyAgent`) — see uncompressed bytes; hashing inside `RestoreFileCore` is
therefore codec-independent, matching backup.

The hardware interlock (`§7`) ensures the drive's compressor is in the correct state before
reading begins, so hardware-compressed sets read back through the drive's decompressor while
software-compressed sets bypass it.

---

## 10. On-Tape Layout

Per file, in write order:

```
[raw file header — always uncompressed]
[file body — Stored or ZSTD-compressed]
```

For a compressed file the body is one or two ZSTD frames:
- Files ≤ 128 KiB: one probe frame (complete).
- Files > 128 KiB: probe frame + live frame (concatenated; transparent to libzstd).

`SizeOnTape` (set by the packer on commit) covers header + compressed body + ZSTD frame
overhead. `TapeFileInfo.FileDescr.Length` always holds the original logical file size.
`tfi.Codec` tells restore which one to decompress.

---

## 11. Tests (`TapeLibNET.Tests`)

### `CompressionRoundTripTests.cs`

Five targeted `[Theory]` tests parameterised over drive profiles
(`Setmarks`, `Partitions`, `SeqFilemarks`, `FilemarksOnly`):

| Test | What it covers |
|------|----------------|
| `Compression_ByteForByteRoundTrip` | Backup + restore byte equality across all profiles × hash algorithms |
| `Compression_TOC_StoresCodecAndSizeOnTape` | `TapeFileInfo.Codec == Zstd`, `SizeOnTape < logical size` after TOC reload |
| `Compression_StoreFallback_IncompressibleData` | Random-byte files trigger `Stored` fallback; restore still succeeds |
| `Compression_ProbeStraddle_FilesSmallerThanProbeWindow` | Files < 128 KiB compress correctly (sub-probe path, no spurious empty frame) |
| `Compression_SessionReuse_ManyFilesAllRestoreCorrectly` | Session reuse across 20 files; hash and byte equality for all |

### Helper extensions

`VirtualTapeFixture.BackupFiles` gained optional `TapeCompression compression` and
`int compressionLevel` parameters (defaulting to `None`/`5`) so existing tests are
unaffected while compression tests opt in with a single argument.

`TempFileTree.AddRandomFiles` generates files filled with `RandomNumberGenerator.Fill`
bytes — effectively incompressible — for store-fallback coverage.

### `LargeFileTests.cs`

All four large-file tests (`FileOver2GB`, `FileOver4GB`, `MultipleLargeFiles`,
`Validate_FileOver2GB`) are parameterised with `[Theory] [InlineData(false)] [InlineData(true)]`.
The `compress: true` variant exercises the multi-frame path (probe frame + live frame) because
sparse/zero-filled files are several gigabytes of highly compressible data, and confirms
`SizeOnTape < logical size` after compression.

---

## 12. Bugs Found and Fixed During Implementation

### 12.1 `FinalCodec` read before `Commit()` ran

**Symptom:** Files smaller than 128 KiB were recorded as `Stored` in the TOC even when they
compressed well; restore ignored the ZSTD frame on tape and read garbage.

**Root cause:** The backup agent called `codec = probing.FinalCodec` while `probing` was still
alive inside a `using` block. For sub-probe files, `Commit()` only runs during `Dispose()`.
Reading `FinalCodec` before disposal therefore always returned the default value `Stored`.

**Fix:** `probing.Dispose()` is called explicitly before `probing.FinalCodec` is read. The
implicit `using`-statement disposal becomes a no-op (guarded by `_disposed`).

### 12.2 Spurious empty ZSTD frame for sub-probe files

**Symptom:** Even with the codec timing fix, sub-probe compressed files restored as 0 bytes
or triggered decompressor errors.

**Root cause:** `Commit()` unconditionally opened a live `ZstdNet.CompressionStream`. For
sub-probe files this stream was immediately disposed (no bytes written), emitting a valid but
empty ZSTD frame trailer after the probe frame. libzstd processed the empty frame but then
the read window (from `SizeOnTape`) was exhausted prematurely.

**Fix:** The live stream is opened only when `RawBuf.Length >= ProbeLength` — i.e., only when
the probe window was fully saturated and more bytes are guaranteed to follow.

---

## 13. Pending: UI Exposure

The compression feature is fully wired in `TapeLibNET` but not yet surfaced in the WPF app
(`TapeWinNET`) or the CLI (`TapeConNET`). The following sections describe the intended design,
consistent with how `HashAlgorithm` is already exposed.

### 13.1 Service Layer (`TapeLibNET.Services`)

**`BackupRequest`** (`TapeLibNET/Services/ServiceOperationRequest.cs`):
```csharp
public TapeCompression Compression      { get; init; } = TapeCompression.None;
public int             CompressionLevel { get; init; } = ZstdLevel.Default;
```

**`TapeServiceBase` backup path** — where `Description`, `HashAlgorithm`, and `BlockSize`
are applied to the new `TapeSetTOC`, add:
```csharp
setTOC.Compression      = request.Compression;
setTOC.CompressionLevel = request.CompressionLevel;
```

### 13.2 WPF App (`TapeWinNET`)

The natural home is the **BackupWindow "Options"** panel, mirroring the `HashAlgorithm` combo.

**ViewModel additions to `NewBackupSetViewModel`:**

```csharp
// Mode combo: None / Hardware / Software
public TapeCompression Compression { get; set; } = TapeCompression.None;

// Level slider/spinner — enabled only when Compression == Software
public int CompressionLevel { get; set; } = ZstdLevel.Default;

// Preset buttons (bind to CompressionLevel)
public ICommand SetFastCommand    { get; }  // → ZstdLevel.Fast    (3)
public ICommand SetBalancedCommand{ get; }  // → ZstdLevel.Balanced (5)
public ICommand SetHighCommand    { get; }  // → ZstdLevel.High    (9)
```

**XAML sketch (Options panel):**

```xml
<!-- Compression mode -->
<ComboBox ItemsSource="{Binding CompressionModes}"
		  SelectedItem="{Binding Compression}" />

<!-- Level controls — collapsed unless Software -->
<StackPanel Visibility="{Binding Compression,
				Converter={StaticResource EqToVis},
				ConverterParameter=Software}">
	<Slider Minimum="1" Maximum="19"
			Value="{Binding CompressionLevel}"
			TickFrequency="1" IsSnapToTickEnabled="True" />
	<StackPanel Orientation="Horizontal">
		<Button Content="Fast"     Command="{Binding SetFastCommand}" />
		<Button Content="Balanced" Command="{Binding SetBalancedCommand}" />
		<Button Content="High"     Command="{Binding SetHighCommand}" />
	</StackPanel>
	<TextBlock Text="{Binding CompressionLevel,
				   StringFormat='Level {0} — {1}',
				   Converter=…}" />
</StackPanel>
```

**BackupSetInfo pane** — add a "Compression" row alongside "Hash algorithm", using
`CompressionPreset.DisplayName(set.Compression, set.CompressionLevel)`.

### 13.3 CLI (`TapeConNET`)

Add `--compression` to the `backup` verb:

```
--compression <spec>    off | none | hardware | low | medium | high | 1–19
						Default: none
						low=3, medium=5 (Balanced), high=9
```

**Implementation:**

1. Add `Option<string> compressionOption` to the `backup` command builder, defaulting to
   `CompressionPreset.KeyNone`.
2. In the backup handler, call `CompressionPreset.TryParse(spec, out mode, out level, out err)`;
   on failure print the error and exit non-zero.
3. Set `request.Compression = mode; request.CompressionLevel = level;`.
4. Update `--help` and `TapeConNET/Resources/Docs/concepts.md` to document the option.

**Example invocations:**
```
tapecon backup D:\Data --compression medium
tapecon backup D:\Data --compression high
tapecon backup D:\Data --compression 7
tapecon backup D:\Data --compression hardware
tapecon backup D:\Data --compression off
```

**`tapecon info` / set listing** — add a "Compression" column to the set table output,
formatted via `CompressionPreset.DisplayName`.

---

## 14. File Map

| File | Role |
|------|------|
| `TapeLibNET/TapeCompression.cs` | `TapeCompression` enum, `ZstdLevel` constants, `CompressionPreset` parse/format helper |
| `TapeLibNET/TapeCompressionStream.cs` | `TapeFileCodec` enum, `ZstdCodec`, `CompressionFilterStream`, `ProbingCompressionStream` (+ `Session`), `DecompressionFilterStream` |
| `TapeLibNET/TapeTOC.cs` | `TapeSetTOC.Compression` / `CompressionLevel`; `TapeFileInfo.Codec` / `SizeOnTape`; serialization; `TapeSetTOCParams` |
| `TapeLibNET/TapeBackupAgent.cs` | Hardware interlock on write; `ProbingCompressionStream` usage in `BackupFile`; session lifecycle in `Dispose(bool)` |
| `TapeLibNET/TapeRestoreAgent.cs` | Hardware interlock on read; `DecompressionFilterStream` wrapping in `RestoreNextFile` |
| `TapeLibNET/TapeFilePacker/PackedCommitTracker.cs` | `PendingEntry.Codec`; codec propagation through `Register` → `OnCommitted` → TOC |
| `TapeLibNET/TapeDrive.cs` | `SetHardwareCompression(bool)` |
| `TapeLibNET.Tests/CompressionRoundTripTests.cs` | Focused compression test suite (5 × 4 profiles = 20 tests) |
| `TapeLibNET.Tests/LargeFileTests.cs` | Large-file tests parameterised `[InlineData(false/true)]` for compress/no-compress |
| `TapeLibNET.Tests/Helpers/TempFileTree.cs` | `AddRandomFiles(...)` for incompressible test data |
| `TapeLibNET.Tests/Helpers/VirtualTapeFixture.cs` | `BackupFiles(compression:, compressionLevel:)` optional params |
| `THIRD-PARTY-NOTICES` | BSD-3-Clause attribution for libzstd |
