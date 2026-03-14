# TapeNET Solution — Context Primer

## Solution Structure

The **TapeNET** solution (`D:\Documents.DEV\Projects\TapeNET`) targets **.NET 8 / C# 12** and contains three projects:

| Project | Type | Role |
|---------|------|------|
| `TapeLibNET` | Class library | Core tape I/O library — drives, agents, TOC, streams, serialization |
| `TapeConNET` | Console app | CLI tape utility (`tapecon`) — flag-based command-line interface |
| `TapeWinNET` | WPF app | GUI tape backup manager — MVVM, tree-based navigation, log pane |

Git repo: `https://github.com/avk1im/TapeNET`, branch `dev`.

---

## TapeLibNET — Library Architecture

### Core class hierarchy

```
TapeDrive                         — Win32 / virtual tape drive wrapper
TapeStreamManager                 — manages read/write streams, owns TapeNavigator
  TapeNavigator                   — tape positioning (partitions, filemarks, blocks)
TapeTOC                           — table of contents: media description, backup sets
  TapeSetTOC                      — per-set file list, block size, hash algorithm, flags
    TapeFileInfo                  — file descriptor + block position + hash

TapeFileAgent (base)              — TOC backup/restore, Notify* wrappers, TapeFileStatistics
  TapeFileBackupAgent             — backup with multi-volume support (TapeBackupContext)
  TapeFileRestoreBaseAgent        — restore/validate/verify base
    TapeFileRestoreAgent          — writes files to disk
      TapeFileRestoreAgentEx      — adds target dir, subdirectory recursion, handle-existing
    TapeFileValidateAgent         — CRC-only validation (no disk writes)
    TapeFileVerifyAgent           — byte-by-byte comparison against disk files
```

### ITapeFileNotifiable + TapeFileStatistics

The library owns all file-operation statistics via a `TapeFileStatistics` struct (fields: `FilesTotal`, `FilesProcessed`, `FilesSucceeded`, `FilesFailed`, `FilesSkipped`, `BytesProcessed`). The struct is maintained by the agent's `Notify*` methods and passed `in` to every `ITapeFileNotifiable` callback:

```csharp
public interface ITapeFileNotifiable
{
    void BatchStart(int setIndex, in TapeFileStatistics stats);
    void BatchEnd(int setIndex, in TapeFileStatistics stats);
    bool PreProcessFile(ref TapeFileDescriptor fileDescr, in TapeFileStatistics stats);
    bool PostProcessFile(ref TapeFileDescriptor fileDescr, in TapeFileStatistics stats);
    FileFailedAction OnFileFailed(TapeFileDescriptor fileDescr, Exception ex, in TapeFileStatistics stats);
    void OnFileSkipped(TapeFileDescriptor fileDescr, in TapeFileStatistics stats);
}
```

Callers never track their own counters — they just read the snapshot. `StatsUndoFailure()` handles retry and end-of-media edge cases. Stats are reset at each public entry point (`BackupFileListToCurrentSet`, `RestoreFilesFromSets`, etc.).

### Multi-volume workflow

- **Backup**: `BackupFileListToCurrentSet` → `BackupFilesToCurrentSet` (loop) → on end-of-media: saves `TapeBackupContext`, returns `false` with `CanResumeToNextVolume == true` → caller ejects/inserts media → `ResumeBackupToNextVolume`.
- **Restore**: `RestoreFilesFromCurrentSetDownInt` iterates sets oldest-to-newest → if a set is on another volume: saves `TapeRestoreContext`, returns `false` with `CanResumeFromAnotherVolume == true` → caller ejects/inserts media → `ResumeRestoreFromAnotherVolume`.

### Virtual drive support

`VirtualTapeDriveBackend` provides file-backed virtual tape drives for testing without hardware. Created via `VirtualTapeDriveBackend.CreateFileBacked(...)` with configurable capabilities (`WithPartitions` / `WithSetmarks`), capacity, and IO speed simulation.

---

## TapeConNET — CLI Architecture

- **Single-file top-level program** (`Program.cs`) — no classes for the main flow.
- **Flag-based parsing**: `ParseCommandLine(args)` splits args into flag+values groups. A `Dictionary<string, FlagHandler>` maps flags (e.g., `-backup`, `-restore`, `-drive`) to handler functions.
- **`OnFileEventProcessor`** (abstract class implementing `ITapeFileNotifiable`) is the base for `OnFileBackupProcessor`, `OnFileRestoreProcessor`, `OnFileValidateProcessor`, `OnFileVerifyProcessor`. Each syncs stats from the library via `Sync(in TapeFileStatistics)`.
- **`UniversalRestore`** is a shared handler for restore/validate/verify operations with multi-volume support.
- User interaction: `GetConsoleKey()`, `MessageYesNoCancel()`, quiet mode support.

---

## TapeWinNET — WPF App Architecture

### MVVM pattern

- **`ViewModelBase`** — `INotifyPropertyChanged` base with `SetProperty`.
- **`MainViewModel`** — split across partial classes:
  - `MainViewModel.cs` — core: tree management, property/file/set display, logging helpers, virtual drive
  - `MainViewModel.Backup.cs` — backup commands, progress properties, backup dialog flow
  - `MainViewModel.Restore.cs` — restore/validate/verify commands, progress properties, restore dialog flow
- **Commands**: `AsyncRelayCommand` (async), `RelayCommand` (sync). All check `IsBusy` for canExecute.
- **Dialog ViewModels**: `NewBackupSetViewModel`, `RestoreViewModel`, `OpenVirtualDriveViewModel` — each with OK/Cancel callbacks.

### TapeService (service layer)

`TapeService` is a partial class split across:
- `TapeService.cs` — drive management, media loading, TOC, events (`LogMessageReceived`, `StatusChanged`)
- `TapeService.Backup.cs` — `ExecuteBackupAsync(...)` with `GuiBackupProgressHandler`
- `TapeService.Restore.cs` — `ExecuteRestoreAsync(...)` with `GuiRestoreProgressHandler`, `RestoreMode` enum

Both GUI progress handlers implement `ITapeFileNotifiable` and follow the same pattern:
1. No own counting — all stats come from the library snapshot via `Sync(in TapeFileStatistics)`
2. Convenience properties (`FilesProcessed`, `FilesTotal`, etc.) mirror the snapshot for service-level reads
3. A `Log(WarningLevel, msg, sub)` helper emits `LogEntry` structs
4. `ThrowIfAbortRequested()` checks the agent's abort flag

### Structured logging — LogEntry + WarningLevel

```csharp
public enum WarningLevel { None, Completed, Info, Warning, Failed, Error }

public record LogEntry(WarningLevel Level, string Message, bool IsSub, DateTime Timestamp)
{
    public string DisplayText { get; }  // "[HH:mm:ss] ⚠ Message" or "[HH:mm:ss] Message" (sub/None)
}
```

- Icons come from `WarningLevelHelper.GetIcon(Level)`: ✓ ℹ ⚠ ✗ ⚠ (per level)
- Non-sub entries show icon + level-based foreground color
- Sub entries show no icon, default color, 16px left margin indent
- `LogMessages` is `ObservableCollection<LogEntry>` on `MainViewModel`
- Service-level `Log`/`LogInfo`/`LogOk`/`LogWarn`/`LogErr` helpers in `TapeService.cs` emit via the `LogMessageReceived` event
- ViewModel-level `AddLog`/`LogInfo`/`LogOk`/`LogWarn`/`LogErr` helpers dispatch to the UI thread
- Local log helpers (`logOk`, `logInfo`, `logFail`, `logErr`, etc.) are defined at the top of `ExecuteBackupAsync`/`ExecuteRestoreAsync` with `#pragma warning disable CS8321` for unused variants

### UI conventions

- **App.xaml resources**: Centralized `WarningBg.*`, `WarningBr.*`, `WarningFg.*` brushes for all warning levels. `WarningPanelStyle` for dialog warning panels with `DataTrigger`s on `WarningLevel`.
- **Log pane**: `ListBox` with Consolas 11pt, `DataTemplate` typed to `LogEntry`, `MultiDataTrigger` for level-based coloring (only non-sub entries), `DataTrigger` for sub-entry indent.
- **Tree view**: Drive → Tape → BackupSets, with `TapeTreeItemViewModel`. Sets listed newest-first.
- **Content pane**: Switches between `DriveInfo`, `MediaInfo`, `BackupSetInfo` via `ContentPaneType` enum.
- **Progress**: Backup/Restore each have their own progress panel with percent bar, text, current-file display, abort button.
- **Set indexes**: Dual display format `#{standard} | {alt}` where standard counts up from oldest (1-based) and alt counts down from newest (0, -1, -2...).

---

## Throughout the Project: Coding Conventions

- **C# 12 / .NET 8** features: primary constructors, collection expressions (`\[]`), file-scoped namespaces, records, `required` members where appropriate. Prefer primary constructors where applicable.
- Provide **comments**, especially to match existing style, introduce new functionality sections, or explain complex logic.
- For **multi-line comments**, indent the comments on the following lines by an additional space.
- **Maximize reuse** of existing code, avoid duplication of functionality, ensure consistent behavior and UX throughout the project. Place common functionality in a helper method or class.
- **Constants** for commonly used values, repeated string literals, magic numbers, and formatting patterns.
- **Nullable** usage practice: apply and follow consistently, minimize overriding with `!` unless absolutely necessary - always explain such ab exception in a comment.
- **Existing libraries only** — no new packages without necessity.
- **Naming**: PascalCase for public members, `\_camelCase` for private fields (`m\_mfcStyle` in TapeLibNET for historical reasons), `camelCase` for local functions/variables.
- **`Helpers.BytesToString` / `Helpers.BytesToStringLong`** from `Windows.Win32.System.SystemServices` for human-readable byte sizes.

---

## What's Been Implemented

- ✅ Full backup workflow (single & multi-volume, incremental, append/overwrite, filemarks/blob mode)
- ✅ Full restore/validate/verify workflow (single & multi-volume, incremental chain traversal, file patterns)
- ✅ Unified `TapeFileStatistics` across library → CLI → GUI (no duplicate counting)
- ✅ Structured `LogEntry`-based logging with `WarningLevel` icons, colors, sub-entry indentation
- ✅ Virtual drive support with IO speed simulation
- ✅ New Backup Set dialog, Restore dialog, Open Virtual Drive dialog

## What's Next (Planned)

- `TapeWinNET`: Additional UI polish and workflow refinements
- `TapeLibNET`: Polishing, validation, & hardening of the core functionality
- `TapeLibNET`: Proper commenting of the code, esp. public API and complex logic areas
- `TapeConNET`: Service updates to keep in sync with library changes

- `TapeWinNET`: UI enhancement features
1. Log export / clear — The log pane caps at 1000 entries but there's no way to save or clear it. A "Save Log..." and "Clear Log" in the context menu or View menu would be useful for troubleshooting long operations.
2. File filter/search in the backup set table — When browsing a set with thousands of files, a quick filter text box above the file ListView (filter-as-you-type by filename pattern) would be very handy. Low-cost given fileList is already a List<FileListItem> that gets swapped wholesale.
3. [DONE] Window state persistence — Remember window size, position, splitter proportions, and the last-opened drive number between sessions. Could live alongside the MRU file in %LocalAppData%\TapeWinNET\. JSON serializer based implementation.
4. Operation-complete notification — A FlashWindowEx or system notification when a long backup/restore finishes while the window is in the background. Tape operations can run for hours; easy to miss completion.
5. Delete most-recent backup set — Removing the newest set from the TOC (the library likely supports this via TapeTOC manipulation + TOC rewrite). Useful when the last backup was accidental or corrupt.
6. Capacity usage bar — A small visual bar in the Media properties or status bar showing used/remaining as a colored segment bar, broken down by set. Quick situational awareness without reading numbers.

