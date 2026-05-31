# Design: Win32 BackupRead / BackupWrite Stream Wrappers

## Context

TapeNET performs tape backup and restore of NTFS files. The original implementation
opened source files via `FileInfo.OpenRead()` and created target files via `FileInfo.Create()`,
reading and writing only the default data stream. This missed NTFS-specific metadata that
Windows can preserve across backup/restore cycles:

- **ACLs / security descriptors** (`BACKUP_SECURITY_DATA`)
- **Alternate Data Streams** (`BACKUP_ALTERNATE_DATA`)
- **Extended Attributes** (`BACKUP_EA_DATA`)
- **Reparse point data** (junction targets, symlinks — `BACKUP_REPARSE_DATA`)
- **Sparse block maps** (`BACKUP_SPARSE_BLOCK`)
- **Object IDs** (`BACKUP_OBJECT_ID`)

Windows exposes these through a unified framed API — `BackupRead` on the source side and
`BackupWrite` on the target side. Each call delivers or consumes one or more
`WIN32_STREAM_ID`-prefixed blobs. The goal of this design is to route file I/O through
these APIs while keeping all framing details strictly internal to the library.

---

## Design Decisions

### 1. Opaque blob semantics — framing is private

The `WIN32_STREAM_ID` structure is **never exposed outside the wrapper classes**. Callers
(backup agent, restore agent, verify agent) see plain `Stream` objects and an opaque byte
sequence. This decouples the rest of the codebase from Win32 stream framing details and
keeps upgrade/change surface minimal.

Crucially, no `WIN32_STREAM_ID` parsing is done inside the wrappers either. The Win32
`BackupRead`/`BackupWrite` APIs maintain all framing state internally via the opaque
`lpContext` pointer. The wrappers are therefore **true pass-throughs**: `Read()` calls
`BackupRead` directly, `Write()` calls `BackupWrite` directly, and the bytes transferred
are the raw framed blob in both directions.

### 2. `bProcessSecurity = false` throughout

Both `TapeBackupSourceStream` and `TapeBackupTargetStream` pass `bProcessSecurity=false`
to all `BackupRead` / `BackupWrite` calls and keep this value **consistent across every
call on the same context handle**. This is a hard requirement of the Win32 API: mixing
`true` and `false` on the same handle corrupts internal context state and causes hangs
or data loss.

The practical effect is that SACL data is not captured or applied. This is intentional:
`ACCESS_SYSTEM_SECURITY` (needed to read SACLs) requires `SeSecurityPrivilege`, which is
not available in unprivileged processes. DACL is still covered by `READ_CONTROL` on read
and `WRITE_DAC` on write.

### 3. Reduced access rights — no privilege escalation required

To keep the wrappers usable without elevated privileges:

| Handle | Access rights granted | Rationale |
|--------|----------------------|-----------|
| Source (`BackupRead`) | `FILE_GENERIC_READ \| READ_CONTROL` | Captures main data + DACL; SACL omitted (no `ACCESS_SYSTEM_SECURITY`) |
| Target (`BackupWrite`) | `FILE_GENERIC_WRITE \| WRITE_DAC` | Restores main data + DACL; SACL + ownership omitted (no `WRITE_OWNER` / `ACCESS_SYSTEM_SECURITY`) |

Both handles are opened with `FILE_FLAG_BACKUP_SEMANTICS | FILE_ATTRIBUTE_NORMAL`.

If a future hardening pass grants `SeBackupPrivilege` / `SeRestorePrivilege` via
`AdjustTokenPrivileges`, the access rights can be widened accordingly.

### 4. Error handling — fatal for DATA, context-dependent for metadata

Because framing is entirely internal to Win32, the wrappers do not implement a per-stream
skip/discard state machine. Errors are handled at the `BackupRead`/`BackupWrite` call
level:

| Situation | Action |
|-----------|--------|
| `BackupRead` returns `FALSE` | Fatal — `TapeIOException` thrown with the Win32 error code |
| `BackupWrite` returns `FALSE` | Fatal — `TapeIOException` thrown with the Win32 error code |
| `BackupRead` returns 0 bytes with `bAbort=FALSE` | Normal end-of-backup signal; `Read()` returns 0 |

The "best effort" metadata behaviour (skipping unreadable non-DATA streams) is delegated
to the Win32 API itself. In practice, inaccessible streams are silently omitted from the
`BackupRead` output rather than causing errors.

### 5. SizeOnTape — exact on-tape blob length is tracked per file

In the packed backup path, files are stored consecutively without alignment padding.
Restore must know exactly how many bytes each file occupies on tape to locate boundaries.
`TapeFileInfo.SizeOnTape` records the authoritative byte count and is persisted in the TOC.

**Packed path:** The value is set by `PackedCommitTracker.OnCommitted` from the packer's
committed byte count (`CommittedFile.Length`), which reflects header + blob bytes as
written by the packer facade — not the raw `FileInfo.Length`. This is the only correct
source; `FileInfo.Length` is wrong for ADS files, empty files, and files with metadata.

**Legacy aligned path:** The value is set from `wstream.Length` after the buffered copy
completes. `wstream` is opened fresh per file, so its `Length` equals the full on-tape
footprint (serialized header + `BackupRead` blob).

### 6. Legacy aligned path — upgraded to use `TapeBackupSourceStream`

Although the aligned backup path (`BackupFileAligned`) is marked `[Obsolete]`, it was
updated alongside the packed path to ensure cross-path restore compatibility:

- `fileInfo.OpenRead()` replaced with `TapeBackupSourceStream.Open(fileInfo, m_logger)`.
- `tfi.SizeOnTape = wstream.Length` set after the buffered copy.
- `fileInfo.OpenRead()` in the `HasherStream` branch replaced similarly.

This means a set written by the aligned path can be correctly restored by the packed
restore path, and vice versa. The on-tape format is now identical for both paths.

### 7. Empty-file edge case

`BackupRead` on a zero-byte file with no additional NTFS streams may return 0 bytes
immediately (without emitting even a `WIN32_STREAM_ID` header). This is a legitimate
end-of-backup signal, not an error. `TapeBackupSourceStream.Read()` propagates the 0
return to callers as-is.

Consequence: `SizeOnTape` for an empty file may be exactly equal to the serialized
header size (header only, no blob body).

### 8. PostProcess complements BackupWrite

`PostProcessFileInternal` (timestamp, file attribute restoration) still runs after the
`TapeBackupTargetStream` is closed. These properties are not preserved by `BackupWrite`
(which only restores security descriptors and stream content), so the two mechanisms
are complementary, not redundant.

---

## Implementation

### New file: `TapeLibNET/TapeBackupStream.cs`

```
TapeBackupStreamBase       (abstract)
  ├── TapeBackupSourceStream    (sealed) — BackupRead source
  └── TapeBackupTargetStream    (sealed) — BackupWrite target
```

The abstract base handles `SafeFileHandle` ownership, the `_context` void* field,
`_position` tracking, and the `Dispose` / `ReleaseContext` pattern. No framing state
machine is present in the base or derived classes.

#### `TapeBackupSourceStream`

- Opened via `PInvoke.CreateFile` with `FILE_GENERIC_READ | READ_CONTROL` and
  `FILE_FLAG_BACKUP_SEMANTICS | FILE_ATTRIBUTE_NORMAL`.
- `Read()` directly calls `BackupRead` once per `Read()` invocation, forwarding the
  caller's buffer and count. Returns 0 when `BackupRead` reports end-of-backup (0 bytes,
  no error).
- `ReleaseContext()` calls `BackupRead` with `bAbort=TRUE` to free Win32's internal
  context state.

#### `TapeBackupTargetStream`

- Created via `PInvoke.CreateFile` with `FILE_GENERIC_WRITE | WRITE_DAC` and
  `FILE_FLAG_BACKUP_SEMANTICS | FILE_ATTRIBUTE_NORMAL`, disposition `CREATE_ALWAYS`.
- `Write()` directly calls `BackupWrite` once per `Write()` invocation, forwarding the
  caller's buffer and count.
- `ReleaseContext()` calls `BackupWrite` with `bAbort=TRUE`.

#### Unsafe context pointer pattern

`BackupRead` and `BackupWrite` maintain an opaque context pointer across calls.
The pointer is stored in `_context` (a `void*` field on the heap). Because it is a heap
field, it cannot be directly passed by address (`&_context` → CS0212). The pattern used
throughout is:

```csharp
void* ctx = _context;          // copy to stack local
PInvoke.BackupRead(..., &ctx); // pass address of local
_context = ctx;                // write back after call
```

---

## Changed Files

| File | Change |
|------|--------|
| `TapeLibNET/NativeMethods.txt` | Added `BackupRead`, `BackupWrite`, `BackupSeek`, `FILE_FLAGS_AND_ATTRIBUTES`, `FILE_ACCESS_RIGHTS`, `WIN32_STREAM_ID`; enum names only (no individual constants) |
| `TapeLibNET/TapeBackupStream.cs` | **New** — `TapeBackupStreamBase`, `TapeBackupSourceStream`, `TapeBackupTargetStream`; pure pass-through; no framing state machine |
| `TapeLibNET/TapeTOC.cs` | `TapeFileInfo.SizeOnTape` added (`internal long`); serialized in `SerializeTo`/`ConstructFrom`/`EstimateSerializedSize`; used in `ComputeTotalFileSizeOnTape`/`SumRawFileSizes` |
| `TapeLibNET/TapeFilePacker/PackedCommitTracker.cs` | `SizeOnTape = cf.Length` set on commit (packed path) |
| `TapeLibNET/TapeRestoreAgent.cs` | Packed restore path: `TapeBackupTargetStream.Create`; verify path: `TapeBackupSourceStream.Open`; packed window sizing: `tfi.SizeOnTape` |
| `TapeLibNET/TapeBackupAgent.cs` | **Both** packed (`BackupFile`) and legacy aligned (`BackupFileAligned`) paths use `TapeBackupSourceStream.Open`; `BackupFileAligned` also sets `tfi.SizeOnTape = wstream.Length` |
| `TapeLibNET.Tests/Helpers/AdsHelper.cs` | **New** — reusable NTFS ADS helper for tests |
| `TapeLibNET.Tests/Helpers/TempFileTree.cs` | Extended with `AddFileWithAds` (NTFS guard returns `null` on non-NTFS volumes) |
| `TapeLibNET.Tests/BackupStreamTests.cs` | **New** — focused wrapper and round-trip test class (see Test Coverage) |
| `TapeConNET.Tests/TapeConNET.Tests.csproj` | Linked `AdsHelper.cs` so `TempFileTree.AddFileWithAds` compiles in the shared-linked context |

---

## Test Coverage

New test class: `TapeLibNET.Tests.BackupStreamTests`

Tests that require file-system access use `[SkippableFact]` / `[SkippableTheory]` guards:

- `SkipUnlessElevated()` — skips on non-Administrator sessions where `BackupRead` open
  would fail due to insufficient access rights.
- `SkipUnlessNtfs(path)` — skips on non-NTFS volumes where ADS is unavailable.

| Test | What it verifies |
|------|-----------------|
| `SourceStream_PlainFile_BlobLargerThanFile` | Blob from `BackupRead` is ≥ raw file size (includes stream header overhead) |
| `SourceStream_EmptyFile_ProducesZeroOrHeader` | Zero-byte file produces 0 bytes or a ≥ 20-byte blob (both are valid `BackupRead` outcomes) |
| `SourceStream_FileWithAds_BlobLargerThanWithoutAds` | ADS file produces a strictly larger blob than an equivalent plain file |
| `RoundTrip_PlainFile_MainDataRestored` | Source→memory buffer→target round-trip restores main data byte-for-byte |
| `RoundTrip_FileWithAds_MainDataAndAdsRestored` | Round-trip with ADS restores both main data and the named ADS |
| `SizeOnTape_AfterPackedBackup_AllEntriesPositive` | All TOC entries have `SizeOnTape > 0` after packed backup (× 4 drive profiles) |
| `SizeOnTape_SurvivesTocRoundTrip` | `SizeOnTape` values survive TOC save-to-tape / load-from-tape (× 4 drive profiles) |
| `PackedRoundTrip_FileWithAds_BothStreamsRestored` | Full tape backup + restore of an ADS file; main data and ADS both verified on the restored file |
| `PackedVerify_FileWithAds_Succeeds` | Verify agent completes without error on a set containing an ADS file |

**Restore path computation in tests:** `TapeFileRestoreAgentEx` with `recurseSubdirs=true`
preserves the original directory hierarchy under the restore root by stripping the drive
root only (e.g. `C:\` → `restoreRoot\Temp\...`). Tests that compare restored files must
account for this:

```csharp
string pathRoot = Path.GetPathRoot(tree.RootPath)!;
string restoreEquivalentRoot = Path.Combine(restoreTree.RootPath,
    Path.GetRelativePath(pathRoot, tree.RootPath));
string restoredFile = Path.Combine(restoreEquivalentRoot,
    Path.GetRelativePath(tree.RootPath, originalFile));
```

New helpers:

- `TapeLibNET.Tests.Helpers.AdsHelper` — `IsNtfs`, `AdsPath`, `WriteAds`, `ReadAds`,
  `AdsExists`, `AssertAdsContent`; reusable in any future test class.
- `TempFileTree.AddFileWithAds` — creates a main-stream file and attaches named ADS;
  returns `null` on non-NTFS volumes so the caller can `Skip.If(withAds == null)`.

---

## Privilege Requirements

| Access right | Why needed |
|--------------|-----------|
| `SeBackupPrivilege` | Required to open files with `FILE_FLAG_BACKUP_SEMANTICS` when the caller lacks explicit ACL access |
| `SeRestorePrivilege` | Required to restore security descriptors via `BackupWrite` |

The wrappers deliberately do **not** request `ACCESS_SYSTEM_SECURITY` or `WRITE_OWNER`,
so they operate correctly without `SeSecurityPrivilege`. SACLs are not captured or
applied; DACLs are, via `READ_CONTROL` / `WRITE_DAC`.

In production, `TapeConNET` and `TapeWinNET` run elevated. If explicit privilege
management is added in a future pass, `AdjustTokenPrivileges` can enable
`SeBackupPrivilege` + `SeRestorePrivilege` + `SeSecurityPrivilege` as needed and the
access rights can be widened accordingly.
