## Design: Per-File Software Compression (ZSTD) for TapeNET

**Status:** v2.0 · **as implemented** — library, service, CLI and WPF.
**Last updated:** 2026-11 (encryption branch: the probe chooses the remainder's level, §6.2; the interlock with software
encryption, §7).
**Related:** Design-FormatV21 §5.3 (codec byte), Design-Encryption §9 (cipher seam, compression × encryption).

### 1. Motivation and Scope

TapeNET supports hardware compression — a drive-level toggle that has the drive's built-in compressor compress the
on-tape byte stream. It is opaque to the software: no per-file ratios, no fallback for incompressible data, no per-file
metadata.

Software compression adds a third axis to the per-set configuration:

| Axis | Scope | Stored in |
|---|---|---|
| HashAlgorithm | per set | TapeSetTOC |
| BlockSize | per set | TapeSetTOC |
| Compression + CompressionLevel | per set (applied per file) | TapeSetTOC |
| Codec (Stored / Zstd) | per file | TapeFileInfo, and the file body's first byte |

The goal is byte-for-byte identical restore whichever mode a set was written with, without user intervention on restore.

### 2. Codec Choice: ZSTD via ZstdNet

**ZstdNet 1.5.7** (MIT) wraps **libzstd** (BSD-3-Clause / GPLv2 dual licence, redistributed under BSD-3-Clause). The
native libzstd.dll ships in `runtimes/win-x64/native` and flows to every dependent project (TapeLibNET, TapeConNET,
TapeWinNET, TapeServiceNET). THIRD-PARTY-NOTICES at the repo root carries the attribution.

ZSTD over LZ4, Brotli and Deflate for:
- **Streaming API** (`CompressionStream` / `DecompressionStream`) — bounded memory for files of any size.
- **Levels 1–19** — Fast 3, Balanced 5 (default), High 9; above ~12 for power users.
- **Concatenated frames read as one stream** — exploited by ProbingCompressionStream (§6): a probe frame followed by a
  remainder frame, no separator, no format change.
- **Raw blocks** — an incompressible 128 KiB block is stored raw inside the frame (≈ 3 bytes of block header), so
  compressing incompressible data costs almost nothing (§6.2).
- **ZSTD_compressBound** (`Compressor.GetCompressBound(int)`) — sizes the probe buffer exactly.

### 3. Compression Modes and Levels

#### TapeCompression (TapeLibNET/Compression/TapeCompression.cs)
```
None      — no software processing; hardware compression disabled
Hardware  — the drive's compressor; no software involvement
Software  — ZSTD per file, configurable level
```
Software disables hardware compression (§7). Software **encryption** rules out Hardware compression (§7).

#### Level constants (ZstdLevel)

| Constant | Value | Intent |
|---|---|---|
| Min | 1 | fastest — also the remainder level after an incompressible probe (§6.2) |
| Fast | 3 | fast preset |
| Balanced / Default | 5 | default for new sets |
| High | 9 | better ratio, moderate cost |
| Max | 19 | best ratio, slow |

`ZstdLevel.Clamp(int)` normalises to [1, 19].

#### CompressionPreset (same file)
Translates the CLI / WPF specifiers to (TapeCompression, level) and back:

| Specifier | Mode | Level |
|---|---|---|
| off, none, "" | None | — |
| hardware | Hardware | — |
| low | Software | Fast (3) |
| medium | Software | Balanced (5) |
| high | Software | High (9) |
| 1–19 | Software | numeric |

`TryParse`, `DisplayName`, `PresetName`, `ToSpecifier` give the round trip for both apps. The CLI maps `drive` and `hw`
to `hardware` before calling `TryParse` (§13.3).

### 4. Per-Set Metadata (TapeSetTOC)

```csharp
public TapeCompression Compression      { get; set; } = TapeCompression.None;
public int             CompressionLevel { get; set; } = ZstdLevel.Default;
```
Serialized in the 2.1 TocSet record (Design-FormatV21); legacy TOCs read back as None / Default. TapeSetTOCParams
carries both, so continuation sets on later volumes inherit the configuration. On an **encrypted** set both are hidden
while it is locked (Design-Encryption §6.1) — the sealed TOC entry carries them.

### 5. Per-File Codec Flag (TapeFileInfo)

Auto store-fallback (§6) means a set may mix Stored and Zstd files. Restore learns each file's codec twice:
```csharp
// TapeLibNET/Compression/TapeCompressionStream.cs
public enum TapeFileCodec : byte { Stored = 0, Zstd = 1 }
// TapeFileInfo
internal TapeFileCodec Codec { get; set; } = TapeFileCodec.Stored;
```
- **TOC:** file-entry field 7 (critical) in format 2.1.
- **On tape:** the first byte of every 2.1 file body (`EmitsCodecPrefix`, Design-FormatV21 §5.3). Restore reads the
  body's byte (ReadBodyCodec) — authoritative even when the TOC entry is unavailable (scan, recovered TOC).

### 6. Adaptive Compression: ProbingCompressionStream

#### Problem
Knowing whether compression pays needs compressed output, but compressing a multi-gigabyte file twice — or buffering it —
is prohibitive. The **probe window**: compress the first 128 KiB (ZSTD_BLOCKSIZE_MAX, one ZSTD block), compare, decide.

#### ProbingCompressionStream (TapeLibNET/Compression/TapeCompressionStream.cs)
A write-only Stream over the record's sink:
```
source → HashingStream (uncompressed hash) → ProbingCompressionStream → [cipher sink] → packer write façade
```
**Probe phase** (the first ≤ 128 KiB): every byte goes to `Session.RawBuf` (plain) and through the probe compressor into
`Session.CompBuf`.

**Commit** — when the probe window fills, or on Dispose for smaller files. The probe frame is finalized (CompBuf length
exact), then the decision of §6.2, the codec byte (with `EmitsCodecPrefix`), and the probe's output go to the sink.

**FinalCodec timing.** FinalCodec is valid only after Commit; for a file smaller than the probe window Commit runs only in
Dispose. The backup agent therefore calls `probing.Dispose()` explicitly before reading `FinalCodec` (§12.1).

##### 6.1 ProbingCompressionStream.Session
Session-scoped resources for one backup set, reused for every file:
- **ZstdCodec** at the set's level — re-created only when the level changes (multi-volume edge case).
- **FastCodec** at ZstdLevel.Min — created on first need (§6.2).
- **RawBuf** — pre-allocated to ProbeLength (128 KiB).
- **CompBuf** — pre-allocated to `Compressor.GetCompressBound(ProbeLength)`: never reallocates, even for incompressible
  data with worst-case frame overhead.

`ResetBuffers()` clears both buffers between files — no allocation per file. TapeFileBackupAgent creates the session on
the first Software-mode file and disposes it (both codecs) in `Dispose(bool)`.

##### 6.2 The probe chooses the remainder's level, not whether to compress
The probe sees only a file's head. A file may start incompressible and compress well afterwards (a random header block,
an embedded preview, test data with a random prefix); deciding "store" from the head would store all of it. So:

| File | Probe compresses | Probe does not compress |
|---|---|---|
| **≤ 128 KiB** (seen whole) | Zstd — the probe frame is complete | **Stored** — the raw bytes |
| **> 128 KiB** | Zstd — probe frame, remainder frame at the **set's level** | Zstd — probe frame (raw blocks), remainder frame at **ZstdLevel.Min** |

- **Cost of the worst case:** ZSTD stores an incompressible block raw — about 3 bytes per 128 KiB plus a frame header per
  file. Level 1 runs far ahead of any tape drive.
- **Gain:** a file whose head does not compress still gets every compressible part after it compressed; at level 1
  rather than the set's level, a deliberate speed/ratio trade (the head predicts the rest more often than not).
- **Restore unchanged:** DecompressionFilterStream reads the two frames as one stream; raw blocks are ordinary ZSTD.

**History.** Until 2026-11 an incompressible probe stored the whole file. Test data with a random prefix of ≥ 128 KiB per
1 MiB pattern (≈ 12.5 % randomization) compressed not at all; real files with an incompressible head lost their
compressible remainder.

### 7. Hardware Compression Interlock

An active hardware compressor re-compresses what the host writes — wasted work on Software-compressed or encrypted data.
The interlock sets the drive's compressor per set, on both paths:

**Backup** (TapeFileBackupAgent.BeginWriteContentForCurrentSet) and **Restore / Validate / Verify**
(TapeFileRestoreAgent.BeginReadContentForCurrentSet):
```csharp
Drive.SetHardwareCompression(TOC.CurrentSetTOC.Compression == TapeCompression.Hardware
    && CipherFor(TOC.CurrentSetTOC).AllowsHardwareCompression);
```
- **Software encryption** (`SoftwareSetCipher.AllowsHardwareCompression = false`): ciphertext does not compress. The
  combination is refused before the tape moves — library ERROR_INVALID_PARAMETER (Design-Encryption §10.2), CLI usage
  error, WPF removes Hardware from the compression list while software encryption applies (§13.2).
- **Hardware (drive) encryption (v2)** allows it: the drive compresses before it encrypts.

`TapeDrive.SetHardwareCompression(bool)` updates only the compression flag of the current drive parameters; failure is
swallowed (as `SetOptimalDriveParams`), so drives without a dynamic toggle still work.

### 8. Backup Pipeline Integration

TapeFileBackupAgent.BackupFile (packed path):
```
open file (TapeBackupSourceStream / BackupRead)
  ↓ HashingStream                — hashes the uncompressed bytes
  ↓ ProbingCompressionStream     — §6
  ↓ cipher sink                  — identity, or EncryptingStream (Design-Encryption §9.3)
  ↓ packer write façade
```
- **The 2.1 file header frame** is written before the compressor, uncompressed — restore reads it without knowing the
  codec. (For an encrypted set the whole record, header included, is inside the cipher.)
- **Hash coverage is codec-independent:** the same hash validates stored, hardware- and software-compressed files.
- **Order at the end:** `probing.Dispose()` (seals the ZSTD frame, sets FinalCodec), then the cipher's CompleteWrite,
  then the length is counted and the packer slot closed. FinalCodec is registered with PackedCommitTracker
  (PendingEntry.Codec) and stamped on the TapeFileInfo at commit, so it persists in the TOC.

### 9. Restore Pipeline Integration

TapeFileRestoreAgent.RestoreNextFile (packed path):
```
rstream (bounded to SizeOnTape) → cipher OpenRead (identity / DecryptingStream)
  → CheckFileHeader → ReadBodyCodec
  → Zstd ? DecompressionFilterStream : the stream itself
  → RestoreFileCore(fileInfo, body, hasher)
  → cipher CompleteRead (authenticates what the decoder left unread)
```
All three restore agents (Restore, Validate, Verify) see uncompressed bytes — hashing is codec-independent. The
interlock (§7) puts the drive's compressor in the right state before the first read.

### 10. On-Tape Layout

Per file, in write order:
```
[2.1 file header frame][codec u8][body — Stored, or one / two ZSTD frames]
```
| File | Body |
|---|---|
| ≤ 128 KiB, compressible | one probe frame |
| ≤ 128 KiB, incompressible | raw bytes (Stored) |
| > 128 KiB | probe frame + remainder frame — the remainder at the set's level, or at level 1 after an incompressible probe (§6.2) |

An encrypted set wraps this whole record in its cipher (Design-Encryption §5.7). SizeOnTape (set by the packer at
commit) covers header frame, codec byte and body, incl. ZSTD and cipher overhead; `FileDescr.Length` holds the logical
size.

### 11. Tests (TapeLibNET.Tests)

#### CompressionRoundTripTests.cs
`[Theory]` over the drive profiles (Setmarks, Partitions, SeqFilemarks, FilemarksOnly):

| Test | Covers |
|---|---|
| Compression_ByteForByteRoundTrip | backup + restore byte equality × hash algorithms |
| Compression_TOC_StoresCodecAndSizeOnTape | Codec == Zstd, SizeOnTape < logical size after reload |
| Compression_StoreFallback_IncompressibleData | random files ≤ 128 KiB stay Stored; larger random files become Zstd at about their logical size (`SizeOnTape ≤ Length + header + 64 + Length / 32768`); all restore |
| **Compression_IncompressibleHead_CompressibleBody** | 192 KiB random + 4 MiB zeros → Zstd, `SizeOnTape < Length / 4` (§6.2) |
| Compression_ProbeStraddle_FilesSmallerThanProbeWindow | the sub-probe path, no spurious empty frame |
| Compression_SessionReuse_ManyFilesAllRestoreCorrectly | session reuse over 20 files |

#### Elsewhere
- **LargeFileTests:** the four large-file tests run `[InlineData(false)]` / `[InlineData(true)]`; the compressed variant
  exercises the two-frame path.
- **Encryption suites** (EncryptionAgentTests × compression {None, Software}): compression inside the cipher.
- **Service and CLI:** BackupRequest.Compression; Software encryption + Hardware compression refused
  (ServiceEncryptionTests, EncryptionCliTests).
- **Helpers:** `VirtualTapeFixture.BackupFiles(compression:, compressionLevel:)`; `TempFileTree.AddRandomFiles`.

#### Test data
The test-data generator script spreads its randomization over every 4 KiB page and draws fresh random bytes per chunk:
"15 % random" then means 15 % throughout each file, not a random prefix followed by zeros.

### 12. Bugs Found and Fixed

#### 12.1 FinalCodec read before Commit() ran
**Symptom:** files under 128 KiB recorded as Stored although they compressed; restore read the ZSTD frame as plain data.
**Cause:** FinalCodec was read inside the `using` block; for sub-probe files Commit runs only in Dispose.
**Fix:** `probing.Dispose()` explicitly before reading FinalCodec; the implicit disposal is a no-op (`_disposed`).

#### 12.2 Spurious empty ZSTD frame for sub-probe files
**Symptom:** sub-probe compressed files restored as 0 bytes or failed to decompress.
**Cause:** Commit always opened a remainder stream; disposed unused, it emitted an empty frame.
**Fix:** the remainder stream opens only when the probe window filled (`RawBuf.Length >= ProbeLength`).

#### 12.3 An incompressible head stored the whole file
**Symptom:** test sets with more than ~12.5 % randomization did not compress at any level; real files with an
incompressible head lost their compressible remainder.
**Cause:** the probe decided store-or-compress for the whole file from its first 128 KiB.
**Fix:** §6.2 — a file larger than the probe window is always Zstd; an incompressible probe only lowers the remainder's
level.

### 13. UI Exposure — implemented

#### 13.1 Service layer
`BackupRequest` (ServiceOperationRequest.cs) carries `Compression` and `CompressionLevel`; the backup path applies them
to the new TapeSetTOC with description, hash algorithm and block size. A Software-encryption request with Hardware
compression is refused before the tape moves (Design-Encryption §10.2).

#### 13.2 WPF (TapeWinNET)
- **BackupWindow, Options:** a Compression combo (None / Hardware / Software) with the level for Software; "Load
  defaults" and "Load from last backup" set it.
- **Stored by value:** the selection survives the list changing — Hardware leaves the list while software encryption
  applies (explicit or implied by an encrypted incremental base); the warning pane says *"Hardware compression is not
  available with software encryption — software compression selected."* (Info) until the user picks a compression or
  encryption is off.
- **Display:** the set properties pane and the backup sets list show `CompressionPreset.DisplayName`; "—" for a locked
  set.

#### 13.3 CLI (TapeConNET)
```
--compression <spec>   off | none | hardware | drive | hw | low | medium | high | 1–19     (default: none)
```
`drive` and `hw` map to `hardware` before `CompressionPreset.TryParse`; a parse error is a usage error. With
`--encrypt` / `--encryption software`, hardware compression is a usage error. `tapecon info` / `list --sets-only` show
the compression per set ("—" for a locked one).
```
tapecon backup D:\Data --compression medium
tapecon backup D:\Data --compression 7
tapecon backup D:\Data --compression drive
tapecon backup D:\Data --encrypt --compression high --password-file key.txt
```

### 14. File Map

| File | Role |
|---|---|
| TapeLibNET/Compression/TapeCompression.cs | TapeCompression, ZstdLevel, CompressionPreset |
| TapeLibNET/Compression/TapeCompressionStream.cs | TapeFileCodec, ZstdCodec, ProbingCompressionStream (+ Session with FastCodec), DecompressionFilterStream |
| TapeLibNET/Toc/TapeTOC.cs, TapeTOC.Format.cs | Compression / CompressionLevel; TapeFileInfo.Codec / SizeOnTape; 2.1 serialization; TapeSetTOCParams |
| TapeLibNET/Agents/TapeFileBackupAgent.cs | interlock on write; BackupFile; session lifecycle |
| TapeLibNET/Agents/TapeFileRestoreAgent.cs | interlock on read; ReadBodyCodec; DecompressionFilterStream in RestoreNextFile |
| TapeLibNET/Encryption/TapeSetCipher.cs | AllowsHardwareCompression per scheme |
| PackedCommitTracker.cs | PendingEntry.Codec → TOC |
| TapeLibNET/Drive/TapeDrive.cs | SetHardwareCompression(bool) |
| TapeLibNET/Services/ServiceOperationRequest.cs, TapeServiceBase.Backup.cs, TapeServiceBase.Encryption.cs | request fields; application; the encryption interlock |
| TapeConNET: backup command | `--compression` |
| TapeWinNET: BackupViewModel.cs, BackupViewModel.Encryption.cs, BackupWindow.xaml | the Compression combo; the encryption interlock |
| TapeLibNET.Tests/CompressionRoundTripTests.cs, LargeFileTests.cs, Helpers/TempFileTree.cs, Helpers/VirtualTapeFixture.cs | §11 |
| THIRD-PARTY-NOTICES | libzstd attribution |
