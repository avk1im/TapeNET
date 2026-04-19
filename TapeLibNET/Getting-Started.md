# Getting Started with TapeLibNET

TapeLibNET is a .NET 8 library for tape backup and restore operations on Windows, wrapping the Win32 Tape Backup API in a clean, layered C# architecture. It handles tape drives, media formatting, file-level backup with CRC integrity, table-of-contents management, incremental chains, multi-volume continuation, and more — so you can focus on *what* to back up rather than *how* tape I/O works.

## Library Structure

TapeLibNET is organized in four layers, from hardware to application:

```
┌─────────────────────────────────────────────────────────────────┐
│  Top Layer — Agents                                             │
│  TapeFileBackupAgent · TapeFileRestoreAgent · TapeFileVerify/   │
│  ValidateAgent · TapeFileRestoreAgentEx                         │
├─────────────────────────────────────────────────────────────────┤
│  Mid Layer — Navigation, Streams, TOC                           │
│  TapeNavigator · TapeStreamManager · TapeTOC · TapeSetTOC       │
├─────────────────────────────────────────────────────────────────┤
│  Base Layer — Streams, Buffers, Serialization                   │
│  TapeStream · BufferedTapeStream · TapeStreamBuffer ·           │
│  TapeSerializer · TapeDeserializer · FilterStream               │
├─────────────────────────────────────────────────────────────────┤
│  Hardware Layer — Drive & Backend                                │
│  TapeDrive · TapeDriveBackend · TapeDriveWin32Backend ·         │
│  VirtualTapeDriveBackend (for testing)                          │
└─────────────────────────────────────────────────────────────────┘
```

### Key Classes at a Glance

| Class | Purpose |
|-------|---------|
| `TapeDrive` | Platform-agnostic tape drive controller. Opens the drive, loads media, formats tape, provides low-level read/write. |
| `TapeNavigator` | Tape positioning across content sets and the TOC area. Subclasses handle different tape organizations. |
| `TapeStreamManager` | State-guarded read/write stream provisioning with capacity tracking. |
| `TapeTOC` / `TapeSetTOC` | Table of contents: media metadata + per-set file lists with CRC hashes. |
| `TapeFileBackupAgent` | Backs up file lists to tape with per-file hashing and multi-volume support. |
| `TapeFileRestoreAgent` | Restores files from tape to disk, applying original file attributes. |
| `TapeFileRestoreAgentEx` | Extended restore: target directory redirection, subdirectory preservation, handle-existing policies. |
| `TapeFileValidateAgent` | Reads tape data + validates CRC without writing to disk. |
| `TapeFileVerifyAgent` | Byte-by-byte comparison of tape content against existing disk files. |
| `TapeResult` | Value-type operation result carrying success/failure, Win32 error code, and message. |
| `ITapeFileNotifiable` | Callback interface for progress, skip/retry/abort decisions during file operations. |

## Tape Organization

TapeLibNET supports three tape organization strategies, each with a corresponding `TapeNavigator` subclass:

```
WithPartitions (TOC in dedicated initiator partition):
  ┌──────────────────────────┬────────────────┐
  │ Content Partition        │ Initiator Part. │
  │ [Set1][Set2]...[SetN]    │ [TOC][TOC]      │
  └──────────────────────────┴────────────────┘

WithSetmarks (TOC separated by setmarks):
  ┌──────────────────────────────────────────────┐
  │ [Set1] smk [Set2] smk ... [SetN] smk [TOC]  │
  └──────────────────────────────────────────────┘

WithSeqFilemarks (TOC at end, delimited by sequential filemarks):
  ┌──────────────────────────────────────────────┐
  │ [Set1] fm [Set2] fm ... [SetN] fm fm [TOC]   │
  └──────────────────────────────────────────────┘
```

The organization is determined by drive capabilities and chosen automatically by `TapeNavigator.ProduceNavigator()`. You rarely need to interact with it directly.

## Scenario 1 — Format & Back Up Files

```csharp
using TapeLibNET;
using Microsoft.Extensions.Logging;

// Create drive and open it
using var drive = TapeDrive.CreateWin32();
drive.ReopenDrive(driveNumber: 0);
drive.ReloadMedia();

// Format the tape (creates partitions if supported)
drive.FormatMedia(initiatorPartitionSize: 16 * 1024 * 1024); // 16 MB for TOC

// Create TOC and backup agent
var toc = new TapeTOC();
using var agent = new TapeFileBackupAgent(drive, toc);

// Configure the new backup set
toc.AddNewSetTOC();
toc.CurrentSetTOC.Description = "My first backup";
toc.CurrentSetTOC.BlockSize = drive.BlockSize;
toc.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc32;

// Build file list from directories/patterns
var files = TapeFileBackupAgent.BuildFileNameList(
    [@"C:\Projects\MyApp\src\", @"C:\Projects\MyApp\*.sln"],
    recursive: true);

// Run the backup
var result = agent.BackupFileListToCurrentSet(
    newSet: true, files, ignoreFailures: true);

if (result)
{
    // Write the TOC to tape (dual-copy for redundancy)
    agent.BackupTOC();
    Console.WriteLine($"Backup complete: {agent.Statistics.FilesSucceeded} files");
}
else
{
    Console.WriteLine($"Backup failed: {result.ErrorMessage}");
}
```

## Scenario 2 — Read the TOC & List Tape Contents

```csharp
using TapeLibNET;

using var drive = TapeDrive.CreateWin32();
drive.ReopenDrive(driveNumber: 0);
drive.ReloadMedia();

// Create agent and read the TOC from tape
var toc = new TapeTOC();
using var agent = new TapeFileRestoreAgent(drive, toc);

var result = agent.RestoreTOC();
if (!result)
{
    Console.WriteLine($"Failed to read TOC: {result.ErrorMessage}");
    return;
}

// Enumerate backup sets and their files
Console.WriteLine($"Tape: {toc.MediaDescription}");
Console.WriteLine($"Sets: {toc.Count}, Volume: {toc.Volume}");

for (int i = 1; i <= toc.Count; i++)
{
    toc.CurrentSetIndex = i;
    var set = toc.CurrentSetTOC;

    Console.WriteLine($"\n  Set #{i}: \"{set.Description}\" — {set.Count} files");
    Console.WriteLine($"    Hash: {set.HashAlgorithm}, Block: {set.BlockSize}");
    Console.WriteLine($"    Incremental: {set.Incremental}");

    foreach (var file in set)
    {
        Console.WriteLine($"      {file.FileDescr.FullName}  ({file.FileDescr.Length:N0} bytes)");
    }
}
```

## Scenario 3 — Restore Files

```csharp
using TapeLibNET;

using var drive = TapeDrive.CreateWin32();
drive.ReopenDrive(driveNumber: 0);
drive.ReloadMedia();

var toc = new TapeTOC();
using var agent = new TapeFileRestoreAgentEx(drive,
    targetDir: @"C:\Restored",
    recurseSubdirs: true,
    handleExisting: TapeHowToHandleExisting.Skip,
    legacyTOC: toc);

// Read TOC first
agent.RestoreTOC();

// Restore all files from the latest set
toc.MakeLastSetCurrent();

var result = agent.RestoreFilesFromCurrentSetDown(
    filesSelected: [null],  // null = all files from the set
    ignoreFailures: true);

if (result)
    Console.WriteLine($"Restored {agent.Statistics.FilesSucceeded} files");
else
    Console.WriteLine($"Restore failed: {result.ErrorMessage}");
```

## Progress Notifications

Implement `ITapeFileNotifiable` to receive per-file callbacks during any operation:

```csharp
public class MyNotifiable : ITapeFileNotifiable
{
    public void BatchStart(int setIndex, in TapeFileStatistics stats)
        => Console.WriteLine($"Starting set #{setIndex}, {stats.FilesTotal} files");

    public void BatchEnd(int setIndex, in TapeFileStatistics stats)
        => Console.WriteLine($"Set #{setIndex} done: {stats.FilesSucceeded} ok, {stats.FilesFailed} failed");

    public bool PreProcessFile(TapeFileInfo fileInfo, in TapeFileStatistics stats)
    {
        Console.Write($"  [{stats.FilesProcessed + 1}/{stats.FilesTotal}] {fileInfo.FileDescr.FullName}...");
        return true; // false to skip this file
    }

    public bool PostProcessFile(TapeFileInfo fileInfo, in TapeFileStatistics stats)
    {
        Console.WriteLine(" ✓");
        return true; // false to skip applying file attributes
    }

    public FileFailedAction OnFileFailed(TapeFileInfo fileInfo, TapeResult result, in TapeFileStatistics stats)
    {
        Console.WriteLine($" ✗ {result.ErrorMessage}");
        return FileFailedAction.Skip; // or Retry, or Abort
    }

    public void OnFileSkipped(TapeFileInfo fileInfo, in TapeFileStatistics stats)
        => Console.WriteLine(" (skipped)");
}

// Pass it to any operation:
var notifiable = new MyNotifiable();
agent.BackupFileListToCurrentSet(true, files, ignoreFailures: true, fileNotify: notifiable);
```

## Error Handling

TapeLibNET uses two complementary error mechanisms:

### TapeResult (Agent Layer)

All public agent methods return `TapeResult` — a value type that carries `Success`, `ErrorCode` (Win32), and `ErrorMessage`. It implicitly converts to `bool`:

```csharp
var result = agent.BackupTOC();
if (!result)
    Console.WriteLine($"Error 0x{result.ErrorCode:X}: {result.ErrorMessage}");
```

### IErrorManageable (Drive/Navigator/Manager Layer)

Lower layers expose `LastError` / `LastErrorMessage` via `IErrorManageable`:

```csharp
if (!drive.ReloadMedia())
    Console.WriteLine($"Drive error: {drive.LastErrorMessage} (code {drive.LastError})");
```

### TapeIOException

Thrown internally for tape I/O failures. Carries a Win32 error code and a diagnostic breadcrumb trail:

```csharp
try { /* tape operation */ }
catch (TapeIOException ex)
{
    Console.WriteLine($"Error 0x{ex.Error:X}: {ex.ErrorMessage}");
    if (ex.TrailText.Length > 0)
        Console.WriteLine($"  Trail: {ex.TrailText}");
}
```

## Aborting Operations

Set `agent.IsAbortRequested = true` from any thread (e.g. a cancel button handler). The agent checks this flag periodically and throws `TapeAbortRequestedException`. Alternatively, any `ITapeFileNotifiable` callback can throw `TapeAbortRequestedException` directly.

## Advanced Features

### Incremental Backups

Mark a set as incremental to back up only files modified since the previous set:

```csharp
toc.AddNewSetTOC(incremental: true);
```

The agent automatically skips files whose timestamps match an earlier set in the chain. On restore, `RestoreFilesFromCurrentSetDown` walks the chain newest → oldest, combining incremental deltas into a complete restore.

### Multi-Volume Backups

When a tape fills up mid-backup, the agent saves its context automatically:

```csharp
var result = agent.BackupFileListToCurrentSet(true, files);

while (!result && agent.CanResumeToNextVolume)
{
    // Prompt user to insert next tape, then:
    drive.ReloadMedia();
    drive.FormatMedia();
    result = agent.ResumeBackupToNextVolume();
}
```

Multi-volume restore works symmetrically via `CanResumeFromAnotherVolume` / `ResumeRestoreFromAnotherVolume`.

### File Filtering with FclNET

The companion [FclNET](../FclNET/) library provides a DSL for file filtering:

```
Name matches "*.doc; *.pdf" and Size greaterThan 1MB and Modified after today-30d
```

Integrate via the `ITapeFileFilter` interface and `TapeSetTOC.SelectFiles(filter)`.

### Virtual Drives for Testing

Develop and test without hardware using file-backed virtual tape drives:

```csharp
var backend = VirtualTapeDriveBackend.CreateFileBacked(
    loggerFactory,
    contentFilePath: "tape_content.vtd",
    contentCapacity: 1L * 1024 * 1024 * 1024); // 1 GB

using var drive = new TapeDrive(backend);
drive.ReopenDrive();
drive.ReloadMedia();
drive.FormatMedia();
// ... use exactly like a real drive
```

### Verify & Validate

- **Validate** (`TapeFileValidateAgent`) — reads tape data and checks CRC hashes without writing to disk.
- **Verify** (`TapeFileVerifyAgent`) — reads tape data and compares byte-by-byte against existing disk files.

Both use the same `RestoreFilesFromCurrentSetDown` API and `ITapeFileNotifiable` callbacks.

## Requirements

- **.NET 8** or later
- **Windows** (Win32 Tape Backup API via [CsWin32](https://github.com/microsoft/CsWin32))
- A physical tape drive (LTO, DAT, etc.) — or use `VirtualTapeDriveBackend` for development/testing
