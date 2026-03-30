# TapeNET Solution — Context Primer

## Solution Structure

The **TapeNET** solution (`D:\Documents.DEV\Projects\TapeNET`) targets **.NET 8 / C# 12** and contains six projects:

| Project | Type | Role |
|---------|------|------|
| `TapeLibNET` | Class library | Core tape I/O library — drives, agents, TOC, streams, serialization |
| `FclNET` | Class library | FCL (File Conditions Language) — DSL for file filtering by name, path, size, date, attributes |
| `FclAiNET` | Class library | AI-assisted natural language → FCL translation using `Microsoft.Extensions.AI` |
| `TapeConNET` | Console app | CLI tape backup utility (`tapecon`) — flag-based command-line interface |
| `TapeWinNET` | WPF app | GUI tape backup manager — MVVM, tree-based navigation, log pane, FCL-based file filtering |
| `FclAiNET.Test` | Console app | Interactive NL → FCL REPL for testing AI translation |

**Test projects:** `FclNET.Tests` (xUnit, 469+ tests covering lexer, parser, validator, evaluator, formatter, pipeline).

**Dependencies:** The apps depend on the libraries. `FclNET` has no dependencies on other solution projects. `FclAiNET` depends on `FclNET`. `TapeWinNET` depends on `FclNET` for file filtering in the MainWindow via the `FclTapeFileFilter` adapter (the restore pipeline uses a dictionary-based selection path — see below). `TapeConNET` depends on `FclNET` for its `ITapeFileFilter`-based restore path. `TapeLibNET` remains independent of the FCL projects.

Git repo: `https://github.com/avk1im/TapeNET`, branch `dev`.

---

## FclNET — FCL Language Library

### Overview

FCL (File Conditions Language) is a small domain-specific language for expressing file filtering criteria. It supports matching on file names, paths, sizes, dates, and attributes using comparison operators and logical connectives (`and`, `or`, `not`, parentheses). FCL is case-insensitive for keywords; the formatter emits a canonical form.

Full language specification: `FclNET/FCL-Specification.md`.

### Processing pipeline

```
Input string → FclLexer → Token stream → FclParser → AST
                                                       ↓
                                                  FclValidator → List<FclDiagnostic>
                                                       ↓
                                                  FclEvaluator → bool (per file)
                                                       ↓
                                                  FclFormatter → normalized FCL string
```

`FclPipeline` wraps all stages behind concise entry points: `TryParse`, `Evaluate`, `Select`.

### Key types

| Type | Role |
|------|------|
| `FclLexer` | Tokenizes input, handles comments, quoted strings, size/date literals |
| `FclParser` | Recursive-descent parser producing an immutable AST; expands syntactic sugar (semicolon shortcuts, value chains) |
| `FclValidator` | Semantic validation — type mismatches, invalid regex, unknown attributes |
| `FclEvaluator` | Evaluates AST against `IFclFileInfo`; preprocesses wildcard patterns, caches regex |
| `FclFormatter` | AST → canonical FCL string; supports inline/multi-line modes, value chain collapsing, semicolon re-collapsing |
| `FclPipeline` | One-stop API: `TryParse`, `Evaluate`, `Select(expression, files)`, `Select(string, files)` |
| `FclParseResult` | Record with `Expression` (AST root) and `Diagnostics`; `IsValid` convenience property |

### AST hierarchy

```
FclExpression (abstract)
  FclChainExpression (abstract) — shared operand array + value-chain logic
    FclOrExpression             — two or more branches joined by "or"
    FclAndExpression            — two or more branches joined by "and"
  FclNotExpression              — negation of a sub-expression
  FclGroupExpression            — parenthesized sub-expression
  FclCondition                  — Field Operator Value leaf node
  FclErrorExpression            — sentinel for parse errors

FclValue (abstract)
  FclStringValue                — string literal (quoted/unquoted)
  FclSizeValue                  — parsed size with unit (e.g. 10MB → 10485760 bytes)
  FclAbsoluteDateValue          — concrete date/time
  FclRelativeDateValue          — anchor + offset, resolved at evaluation time
  FclAttributeValue             — attribute flag identifier
```

All nodes carry a `SourceSpan` for diagnostic positions. Formatting logic is encapsulated in each node's `FormatTo` method (polymorphic, not a static helper).

### IFclFileInfo interface

The evaluator operates against an `IFclFileInfo` abstraction, keeping FclNET independent of `TapeLibNET`:

```csharp
public interface IFclFileInfo
{
    string FullName { get; }
    long Size { get; }
    DateTime CreationTime { get; }
    DateTime LastWriteTime { get; }
    FileAttributes Attributes { get; }
}
```

### Language highlights

- **Fields:** `FullName`, `Name`, `Extension`, `Path`, `Size`, `Created`, `Modified`, `Attributes`
- **Operators:** type-specific — string (`equals`, `contains`, `matches`, `notMatches`, `regex`), date (`before`, `after`, `beforeOrOn`, `afterOrOn`), size (`greaterThan`, `lessThan`, etc.), attribute (`have`, `notHave`). Symbolic aliases (`==`, `!=`, `<`, `>`, `<=`, `>=`) accepted.
- **Values:** unquoted/quoted strings, size literals with units (`10MB`, `1.5GB`), absolute dates (ISO 8601 or locale), relative dates (`today-7d`, `now-2h`), attribute flags (`Hidden`, `ReadOnly`, `System`, `Archive`, `Temporary`).
- **Syntactic sugar:** Semicolon shortcuts for multi-pattern matching (`Name matches "*.doc; *.txt"`), value chains (`Extension equals doc or docx or txt`), comment lines (`//`).
- **Diagnostics:** Unified `FclDiagnostic` structure with severity, error code, message, and source span — used for parse, validation, and runtime errors.

---

## FclAiNET — AI-Assisted FCL Generation

### Overview

FclAiNET translates natural language file filter descriptions (e.g. "large photos from last week") into validated FCL expressions using LLM chat completions. Built on `Microsoft.Extensions.AI` (`IChatClient`), it supports both local (Ollama, LM Studio) and cloud (GitHub Models, OpenAI, Azure OpenAI) providers.

### Key types

| Type | Role |
|------|------|
| `FclAiTranslator` | Core translator: NL → chat completion → FCL → `FclPipeline.TryParse` validation → canonical output |
| `FclAiProviderFactory` | Provider discovery and creation: probes local endpoints, checks env vars, falls back to user interaction |
| `FclAiSystemPrompt` | System prompt with condensed FCL language reference (~2 KB); two variants (direct mode / tool mode) |
| `FclAiTools` | `AIFunction` tool definitions (`ValidateFcl`, `FormatFcl`) for LLM self-validation via function calling |
| `IFclAiInteraction` | UI callback interface for provider status and cloud credential prompting (like `ITapeFileNotifiable`) |
| `FclTranslationResult` | Result record: `Success`, `Fcl` (canonical), `Expression` (AST), `Explanation` (on failure), `Attempts` |
| `FclAiProviderChoice` | Provider selection record: `Provider`, `ApiKey`, `ModelId`, `Endpoint` |

### Translation modes

Detected automatically on the first request:

- **Tool mode** — the LLM invokes `ValidateFcl`/`FormatFcl` tools via `FunctionInvokingChatClient` to self-validate within a single chat turn.
- **Direct mode** — fallback when tool calling is unsupported (HTTP 400) or the model emits fake tool-call JSON. The translator parses output externally and re-prompts with error context.

Both modes share an outer retry loop (default 3 attempts) with error feedback for iterative correction.

### Provider discovery order

1. Probe Ollama at `localhost:11434` (enumerate models via `/api/tags`)
2. Probe LM Studio at `localhost:1234` (enumerate models via `/v1/models`)
3. Check environment variables: `GITHUB_TOKEN`, `OPENAI_API_KEY`, `AZURE_OPENAI_API_KEY`
4. Ask user via `IFclAiInteraction.ChooseCloudProviderAsync`

Local providers support model fallback: if the smoke test fails with one model, `TryNextLocalModel()` iterates through remaining models.

### FclAiNET.Test — Interactive Test App

A console REPL that exercises the full pipeline: provider discovery → smoke test (with model fallback) → interactive NL → FCL loop. Implements `IFclAiInteraction` as `ConsoleAiInteraction` for terminal-based provider selection and credential input.

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

TapeTOC file selection (side-effect-free, dictionary-based):
  SelectFilesFromSets(incremental, checkedFilesBySet) — multi-set selection
  SelectFilesForOneSet(setIndex, incremental, selectedFiles) — single-set with chain traversal
  PickFilesByName(setFiles, selectedFiles) — name-based matching into a set
  CombineSelectedFiles(perSetLists) — merges per-set results into a flat list

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

## File Filtering Integration (FclNET ↔ TapeLibNET)

### ITapeFileFilter and dictionary-based selection

`TapeLibNET` defines a minimal `ITapeFileFilter` interface (`bool Matches(in TapeFileDescriptor)`) used by `TapeSetTOC.SelectFiles(filter)` where `null` means "all files match" (null-means-all convention).

**GUI restore pipeline (TapeWinNET):** File selection flows end-to-end as a `Dictionary<int, IReadOnlyList<TapeFileInfo>?>` — from MainWindow checkmarks through `TOCView.GetCheckedFilesBySet()` → `RestoreRequest.CheckedFilesBySet` → `TapeService.ExecuteRestoreAsync` → `TapeTOC.SelectFilesFromSets` → `agent.RestoreFilesFromCurrentSetDown`. No intermediate `ITapeFileFilter` conversion needed.

```
MainWindow checkmarks → TOCView.GetCheckedFilesBySet()
  → RestoreRequest.CheckedFilesBySet
    → TapeService → TapeTOC.SelectFilesFromSets(incremental, dict)
      → agent.RestoreFilesFromCurrentSetDown(combined)
```

**CLI restore pipeline (TapeConNET):** Uses the older `ITapeFileFilter`-based path via `RestoreFilesFromCurrentSet` / `RestoreFilesFromCurrentSetInc` APIs directly on the agent.

**FCL file filtering (FileFilterPane):** `FclTapeFileFilter` adapter bridges `TapeFileDescriptor` → `FclFileInfo` → `FclEvaluator.Evaluate()` for real-time filtering in the MainWindow file list. Has a single constructor accepting `FclEvaluator` and a static `ParsePatterns` helper for wildcard input.

### FileFilterPane (WPF reusable control)

`Controls/FileFilterPane` is a reusable `UserControl` embedded in the MainWindow files pane. It supports two filter input modes:

- **Pattern mode** — DOS-style wildcard filtering (`*`, `?`) with semicolon-separated patterns, entered directly in the text box.
- **Advanced mode** — Full FCL filter built via `FclFilterWindow`, a modal dialog with a visual DNF condition editor (left pane) and an FCL text editor (right pane) with bidirectional sync. The filter pane shows a read-only summary; editing requires reopening the dialog.

The pane supports two **dispatch modes** for host integration:

1. **Direct mode** — set `FilterTarget` to a `FilteredFileList`. The pane applies the filter directly (sets `FilterTarget.Filter`) and awaits completion, then calls `FilterStateChanged` with a restore delegate. Used by `MainViewModel` (main window).
2. **Callback mode** — set `FilterRequested`. The host receives the `FclEvaluator` + restore delegate and applies the filter itself. Available for future use by other hosts that manage filtering themselves.

Direct mode takes priority when `FilterTarget` is non-null. The pane captures its full UI state (text, advanced expression, window layout) into the restore delegate for both apply and disable, so the filter definition survives navigation even when disabled.

### FilteredFileList — Centralized Async Filtering (`TapeWinNET/Utils/`)

`FilteredFileList` is the standard, reusable async filtering mechanism throughout TapeWinNET. It wraps any `IReadOnlyList<TapeFileInfo>` source and centralizes three concerns that were previously scattered across multiple ViewModels:

1. **Async filter computation** — setting `Filter` (an `ITapeFileFilter?`) cancels any in-progress run and starts a new `Task.Run` computation. Setting `null` resets synchronously. `FilterTask` is awaitable; `FilterCompleted` fires on the UI thread when the run finishes.
2. **Checked (selected) state** — a `HashSet<TapeFileInfo>` stores which files are checked for restore. `IsChecked`, `SetChecked` (single and batch), `SetFilteredChecked`, `SetAllChecked`, `ClearChecked`.
3. **Incremental statistics** — `CheckedCount`, `CheckedTotalSize`, `FilteredCheckedCount`, `FilteredCheckedTotalSize`, `AreAllFilteredChecked` (tri-state `bool?`). Single-item changes update stats incrementally; bulk changes recompute from scratch.

It also implements `IReadOnlyList<TapeFileInfo>` (the filtered view) and `INotifyPropertyChanged`, so instances can chain as source for another `FilteredFileList` and bind directly in XAML.

**Thread model:** `Filter` setter and all checked-state methods must be called from the UI thread. Filter computation runs on the thread pool; all `PropertyChanged` and `FilterCompleted` notifications fire on the UI thread via the captured `SynchronizationContext`.

**Usage across the app:**
- `MainViewModel` — one `FilteredFileList` per backup set, owned by the corresponding `BackupSetView` inside a session-level `TOCView`. `FileListItem` proxies hold an owner reference and delegate `IsCheckedForRestore` to it. See **TOCView / BackupSetView** below.
- `BackupSetListItem` — lightweight display model for the RestoreWindow set list. Does *not* own a `FilteredFileList`. Tri-state `IsCheckedForRestore` (`bool?`) for per-set selection. `SelectedFileCountFormatted` and `TotalSizeFormatted` properties for the compact RestoreWindow display. `CheckedFileCount` tracks per-file selections pushed from the MainWindow via `OnFileCheckChanged`.

### TOCView / BackupSetView — Per-Session Data Model (`TapeWinNET/Models/TOCView.cs`)

The `TOCView` / `BackupSetView` pair formalizes the data model for backup-set file content, decoupling it from the tree UI (`TapeTreeItemViewModel`).

**`BackupSetView`** — per-set snapshot, encapsulates:
- `SourceFiles` (`IReadOnlyList<TapeFileInfo>`) — the resolved file list (flat from `TapeSetTOC`, or merged incremental chain).
- `FilteredFiles` (`FilteredFileList`) — async filtering + checked state over `SourceFiles`.
- `SavedFilterState` (`Func<Task>?`) — opaque restore delegate from `FileFilterPane`. Stored per set so the filter definition and pane UI state survive tree navigation, even when the filter is disabled.
- Lazy `FileListItem` dictionary — created on first `BuildDisplayList(showFullPath)` call, refreshed when the full-path toggle changes.
- `MigrateCheckedState(previous)` — carries over checked items when a view is replaced (e.g. `ShowIncrementalSets` toggle).

**`TOCView`** — session-level container:
- Wraps a `TapeTOC` and owns a `BackupSetView?[]` indexed by 1-based set index.
- `GetOrCreate(setIndex, showIncrementalSets)` — returns the cached view if the incremental-view flag still matches, otherwise creates a new one with checked-state migration.
- `GetAllCheckedFiles()` — aggregates checked items across all populated set views.
- `GetCheckedFilesBySet()` — builds a `Dictionary<int, IReadOnlyList<TapeFileInfo>?>` for `TapeTOC.SelectFilesFromSets`. `null` value = all files in set, non-null = specific checked files.
- `GetTotalCheckedCount()` — total checked file count across all sets.
- Lives on `MainViewModel` as `_tocView`; the currently displayed set is `_currentSetView`.

**Data flow:**
```
TapeTOC → TOCView.GetOrCreate(setIndex) → BackupSetView
  BackupSetView.FilteredFiles.Filter = FclTapeFileFilter  → async computation
  BackupSetView.BuildDisplayList(showFullPath) → List<FileListItem> → ListView binding
  BackupSetView.SavedFilterState = FileFilterPane restore delegate
```

**`TapeTreeItemViewModel`** is now purely UI/navigation — it carries display name, icon, set index, and children, but no data-model fields. The `Tag` property can store an opaque reference, but `FilteredFiles` and `SavedFilterState` have moved to `BackupSetView`.

---

## TapeWinNET — WPF App Architecture

### MVVM pattern

- **`ViewModelBase`** — `INotifyPropertyChanged` base with `SetProperty`.
- **`MainViewModel`** — split across partial classes:
  - `MainViewModel.cs` — core: tree management, TOCView ownership (`_tocView`, `_currentSetView`), property/file/set display, logging helpers, virtual drive
  - `MainViewModel.Backup.cs` — backup commands, progress properties, backup dialog flow
  - `MainViewModel.Restore.cs` — restore/validate/verify commands, progress properties, restore dialog flow
- **Commands**: `AsyncRelayCommand` (async), `RelayCommand` (sync). All check `IsBusy` for canExecute.
- **Dialog ViewModels**: `NewBackupSetViewModel`, `RestoreViewModel`, `OpenVirtualDriveViewModel`, `FclFilterWindowVM` — each with OK/Cancel callbacks.
- **`RestoreViewModel`** — compact confirmation dialog (376 lines). Displays pre-selected backup sets with tri-state select-all, aggregate file/size stats, and `RestoreRequest` output. `ExecuteStart` builds a `Dictionary<int, IReadOnlyList<TapeFileInfo>?>` with `null` values (all files) for each checked set. The MainWindow `StartRestore` callback merges this with per-file selections from `TOCView.GetCheckedFilesBySet()` before passing to `TapeService`.
- **`RestoreRequest`** — record: `(Mode, CheckedFilesBySet, Incremental, TargetDirectory, RecurseSubdirectories, HandleExisting)`. `CheckedFilesBySet` is the primary data carrier — no separate `SetIndexes` or `FileFilter` fields.

### TapeService (service layer)

`TapeService` is a partial class split across:
- `TapeService.cs` — drive management, media loading, TOC, events (`LogMessageReceived`, `StatusChanged`)
- `TapeService.Backup.cs` — `ExecuteBackupAsync(...)` with `GuiBackupProgressHandler`
- `TapeService.Restore.cs` — `ExecuteRestoreAsync(mode, checkedFilesBySet, incremental, ...)` with `GuiRestoreProgressHandler`, `RestoreMode` enum. Accepts `Dictionary<int, IReadOnlyList<TapeFileInfo>?>` directly, calls `TapeTOC.SelectFilesFromSets` + `agent.RestoreFilesFromCurrentSetDown` without intermediate `ITapeFileFilter` conversion

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
- **File filtering**: `FileFilterPane` user control with dynamic stats in the GroupBox header (e.g. `"Files (1,234 \u2192 567 filtered \u2192 42 selected)"`), filter state persistence across tree navigation, tri-state select-all checkboxes for both files and backup sets.

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
- ✅ FCL language: lexer, parser, validator, evaluator, formatter, pipeline API (469+ tests)
- ✅ FCL AI translator: natural language → FCL via LLM, with tool-calling and direct modes, provider auto-discovery
- ✅ Advanced file filtering integration: `FclTapeFileFilter` adapter bridging FclNET → `ITapeFileFilter` for MainWindow file list filtering and TapeConNET restore
- ✅ `FileFilterPane` reusable control: pattern mode (wildcards) + advanced mode (`FclFilterWindow` with visual DNF editor and FCL text editor)
- ✅ MainWindow file filtering: filter-as-you-type for backup set files with dynamic stats header, filter state persistence across navigation
- ✅ RestoreWindow: compact 2-column confirmation dialog (720×420) with set table (tri-state checkboxes, Selected/Size columns) and stacked options panels
- ✅ `FilteredFileList` centralized async filtering: single class in `TapeWinNET/Utils/` replacing duplicated filter + checked-state + statistics infrastructure across `MainViewModel`, `BackupSetListItem`, and `RestoreViewModel`
- ✅ `TOCView` / `BackupSetView` data model: per-session container decoupling file-list data (source files, filtered view, checked state, filter persistence) from the tree UI (`TapeTreeItemViewModel`). `FileFilterPane` dual-mode dispatch (direct + callback) with filter state preserved across navigation even when disabled.
- ✅ End-to-end dictionary-based restore selection: `Dictionary<int, IReadOnlyList<TapeFileInfo>?>` flows from MainWindow checkmarks → `TOCView.GetCheckedFilesBySet()` → `RestoreRequest` → `TapeService` → `TapeTOC.SelectFilesFromSets` → `RestoreFilesFromCurrentSetDown`. No intermediate `ITapeFileFilter` conversion.
- ✅ TapeLibNET file selection infrastructure: `TapeTOC.SelectFilesFromSets`, `SelectFilesForOneSet`, `PickFilesByName` — side-effect-free, dictionary-based multi-set selection with incremental chain traversal
- ✅ MainWindow restore/validate/verify: dynamic command text reflecting checked sets/files, tri-state set checkboxes propagating to `FilteredFileList`, per-file check changes pushing back to `BackupSetListItem` tri-state, `RestoreAllSetsCommand` for quick access

## What's Next (Planned)

- `TapeWinNET`: Additional UI polish and workflow refinements
- `TapeLibNET`: Polishing, validation, & hardening of the core functionality
- `TapeLibNET`: Proper commenting of the code, esp. public API and complex logic areas
- `TapeConNET`: Service updates to keep in sync with library changes. Consider switching to classes from the top-level program structure.

- `TapeWinNET`: UI enhancement features
1. Log export / clear — The log pane caps at 1000 entries but there's no way to save or clear it. A "Save Log..." and "Clear Log" in the context menu or View menu would be useful for troubleshooting long operations.
2. [DONE] File filter/search in the backup set table — `FileFilterPane` with pattern mode (wildcards) and advanced mode (full FCL via `FclFilterWindow`). Dynamic stats in the GroupBox header, filter state persistence across tree navigation.
3. Advanced file filtering for Backup — Integrate the `FileFilterPane` into the Backup workflow (New Backup Set dialog or pre-backup file selection) to allow FCL-based filtering of which files to include in a backup operation.
4. [DONE] Window state persistence — Remember window size, position, splitter proportions, and the last-opened drive number between sessions. JSON serializer based implementation in `%LocalAppData%\TapeWinNET\`.
5. Operation-complete notification — A FlashWindowEx or system notification when a long backup/restore finishes while the window is in the background. Tape operations can run for hours; easy to miss completion.
6. Delete most-recent backup set — Removing the newest set from the TOC (the library likely supports this via TapeTOC manipulation + TOC rewrite). Useful when the last backup was accidental or corrupt.
7. Capacity usage bar — A small visual bar in the Media properties or status bar showing used/remaining as a colored segment bar, broken down by set. Quick situational awareness without reading numbers.

