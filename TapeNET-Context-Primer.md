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

**Dependencies:** The apps depend on the libraries. `FclNET` has no dependencies on other solution projects. `FclAiNET` depends on `FclNET`. `TapeWinNET` and `TapeConNET` depend on `FclNET` for file filtering via the `FclTapeFileFilter` adapter (see below). `TapeLibNET` remains independent of the FCL projects.

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

### ITapeFileFilter pipeline

`TapeLibNET` defines a minimal `ITapeFileFilter` interface (`bool Matches(in TapeFileDescriptor)`) used throughout the restore/validate/verify pipeline. Both `TapeWinNET` and `TapeConNET` provide an `FclTapeFileFilter` adapter that bridges `TapeFileDescriptor` → `FclFileInfo` → `FclEvaluator.Evaluate()`, keeping the library agnostic of FCL internals.

The filter flows through the pipeline as:

```
FclEvaluator → FclTapeFileFilter : ITapeFileFilter → TapeRestoreAgent → TapeSetTOC.SelectFiles(filter)
```

`TapeSetTOC.SelectFiles(ITapeFileFilter?)` returns `List<TapeFileInfo>?` where `null` means "all files match" (null-means-all convention).

### FileFilterPane (WPF reusable control)

`Controls/FileFilterPane` is a reusable `UserControl` embedded in both the MainWindow files pane and the RestoreWindow. It supports two modes:

- **Pattern mode** — DOS-style wildcard filtering (`*`, `?`) with semicolon-separated patterns, entered directly in the text box.
- **Advanced mode** — Full FCL filter built via `FclFilterWindow`, a modal dialog with a visual DNF condition editor (left pane) and an FCL text editor (right pane) with bidirectional sync. The filter pane shows a read-only summary; editing requires reopening the dialog.

When applied, the pane builds an `FclEvaluator` and passes it to the host via a `FilterRequested` callback. The host (MainViewModel or RestoreViewModel) stores an opaque restore delegate on the navigation item so the filter survives navigation.

### BackupSetListItem async filter infrastructure

`BackupSetListItem` exposes an `ITapeFileFilter? FileFilter` property that triggers parallel, cancellable filtered-count computation via `Task.Run`. Key members:

- `FileFilter` — setter cancels any in-progress `CancellationTokenSource`, starts new `Task.Run` computation (or clears on `null`).
- `FilteredFileCount` — read-only cached `int?`; `null` while computing or when no filter is active.
- `FilterTask` — awaitable `Task` for the current computation; callers use `Task.WhenAll(...)` across items for parallel filtering.
- `FileCountFormatted` — displays `"61 \u2192 49"` when filtered, plain `"61"` otherwise.

This design is reused across the RestoreWindow (set-mode aggregate stats) and the MainWindow (backup set table).

---

## TapeWinNET — WPF App Architecture

### MVVM pattern

- **`ViewModelBase`** — `INotifyPropertyChanged` base with `SetProperty`.
- **`MainViewModel`** — split across partial classes:
  - `MainViewModel.cs` — core: tree management, property/file/set display, logging helpers, virtual drive
  - `MainViewModel.Backup.cs` — backup commands, progress properties, backup dialog flow
  - `MainViewModel.Restore.cs` — restore/validate/verify commands, progress properties, restore dialog flow
- **Commands**: `AsyncRelayCommand` (async), `RelayCommand` (sync). All check `IsBusy` for canExecute.
- **Dialog ViewModels**: `NewBackupSetViewModel`, `RestoreViewModel`, `OpenVirtualDriveViewModel`, `FclFilterWindowVM` — each with OK/Cancel callbacks.
- **`RestoreViewModel`** supports two modes: set-based (backup sets with tri-state select-all, per-set filtered counts, aggregate stats) and file-based (individual files with tri-state select-all, FCL filtering). Both modes embed a `FileFilterPane` and use async `ApplyFilterToBackupSetsAsync` / `FileFilter.FilterAsync` for parallel computation.

### TapeService (service layer)

`TapeService` is a partial class split across:
- `TapeService.cs` — drive management, media loading, TOC, events (`LogMessageReceived`, `StatusChanged`)
- `TapeService.Backup.cs` — `ExecuteBackupAsync(...)` with `GuiBackupProgressHandler`
- `TapeService.Restore.cs` — `ExecuteRestoreAsync(...)` with `GuiRestoreProgressHandler`, `RestoreMode` enum, `ITapeFileFilter?` support

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
- **File filtering**: `FileFilterPane` with dynamic stats in the GroupBox header (e.g. `"Files (1,234 \u2192 567 filtered \u2192 42 selected)"`), filter state persistence across tree navigation, tri-state select-all checkboxes for both files and backup sets.

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
- ✅ Advanced file filtering integration: `FclTapeFileFilter` adapter bridging FclNET → `ITapeFileFilter` for the restore/validate/verify pipeline
- ✅ `FileFilterPane` reusable control: pattern mode (wildcards) + advanced mode (`FclFilterWindow` with visual DNF editor and FCL text editor)
- ✅ MainWindow file filtering: filter-as-you-type for backup set files with dynamic stats header, filter state persistence across navigation
- ✅ RestoreWindow file filtering: both set-mode (per-set async filtered counts, aggregate stats) and file-mode (FCL-filtered file list) with tri-state select-all

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

