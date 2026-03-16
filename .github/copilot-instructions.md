# Copilot Instructions

## Solution Structure

The **TapeNET** solution (`D:\Documents.DEV\Projects\TapeNET`) targets **.NET 8 / C# 12** and contains four projects:

| Project | Type | Role |
|---------|------|------|
| `TapeLibNET` | Class library | Core tape I/O library — drives, agents, TOC, streams, serialization |
| `FclNET`     | Class library | Implements FCL, a "File Conditions Language" to filter large lists of files |
| `TapeConNET` | Console app | CLI tape backup utility (`tapecon`) — flag-based command-line interface |
| `TapeWinNET` | WPF app | GUI tape backup manager — MVVM, tree-based navigation, log pane, dialogs for backup and restore |

- The apps depend on the libraries; the libraries are independent of each other.
- Hence no need to e.g. parse the apps when working on the libraries.
- Only if a comprehensive understanding of the whole solution is required, refer to `D:\Documents.DEV\Projects\TapeNET\TapeNET-Context-Primer.md`.

## Project Guidelines
- User prefers simple, low-overhead approaches over per-item event subscriptions for UI state tracking. Concerned about performance cost of subscribing to PropertyChanged on thousands of items.

### Throughout the Project: Coding Conventions

- **C# 12 / .NET 8** features: primary constructors, collection expressions (`[]`), file-scoped namespaces, records, `required` members where appropriate. Prefer primary constructors where applicable.
- Provide **comments**, especially to match existing style, introduce new functionality sections, or explain complex logic.
- For **multi-line comments**, indent the comments on the following lines by an additional space.
- **Maximize reuse** of existing code, avoid duplication of functionality, ensure consistent behavior and UX throughout the project. Place common functionality in a helper method or class.
- **Constants** for commonly used values, repeated string literals, magic numbers, and formatting patterns.
- **Nullable** usage practice: apply and follow consistently, avoid overriding with `!` unless absolutely necessary - always explain such an exception in a comment.
- **Existing libraries only** — no new packages without necessity.
- **Naming**: PascalCase for public members, `_camelCase` for private fields (`m_mfcStyle` in TapeLibNET for historical reasons), `camelCase` for local functions/variables.
- **`Helpers.BytesToString` / `Helpers.BytesToStringLong`** from `Windows.Win32.System.SystemServices` for human-readable byte sizes.

## Formatting Logic
- Encapsulate formatting logic inside the AST expression classes rather than in a static formatter helper.
- Factor shared behavior across AST node types into an abstract base class rather than using static helper methods with many parameters.

## For WPF App: Structured logging — LogEntry + WarningLevel

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

## For WPF App: UI conventions

- **App.xaml resources**: Centralized `WarningBg.*`, `WarningBr.*`, `WarningFg.*` brushes for all warning levels. `WarningPanelStyle` for dialog warning panels with `DataTrigger`s on `WarningLevel`.
- **Log pane**: `ListBox` with Consolas 11pt, `DataTemplate` typed to `LogEntry`, `MultiDataTrigger` for level-based coloring (only non-sub entries), `DataTrigger` for sub-entry indent.
- **Tree view**: Drive → Tape → BackupSets, with `TapeTreeItemViewModel`. Sets listed newest-first.
- **Content pane**: Switches between `DriveInfo`, `MediaInfo`, `BackupSetInfo` via `ContentPaneType` enum.
- **Progress**: Backup/Restore each have their own progress panel with percent bar, text, current-file display, abort button.
- **Set indexes**: Dual display format `#{standard} | {alt}` where standard counts up from oldest (1-based) and alt counts down from newest (0, -1, -2...).
