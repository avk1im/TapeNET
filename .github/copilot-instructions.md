# Copilot Instructions

## Solution Structure

The **TapeNET** solution (`D:\Documents.DEV\Projects\TapeNET`) targets **.NET 8 / C# 12** and contains six projects:

| Project | Type | Role |
|---------|------|------|
| `TapeLibNET` | Class library | Core tape I/O library — drives, agents, TOC, streams, serialization |
| `FclNET`     | Class library | FCL (File Conditions Language) — DSL for file filtering by name, path, size, date, attributes |
| `FclAiNET`   | Class library | AI-assisted natural language → FCL translation using `Microsoft.Extensions.AI` |
| `TapeConNET` | Console app | CLI tape backup utility (`tapecon`) — flag-based command-line interface |
| `TapeWinNET` | WPF app | GUI tape backup manager — MVVM, tree-based navigation, log pane, dialogs for backup and restore |
| `FclAiNET.Test` | Console app | Interactive NL → FCL REPL for testing AI translation |

**Test projects:** `FclNET.Tests` (xUnit, 469+ tests).

- The apps depend on the libraries; `FclNET` has no project dependencies; `FclAiNET` depends on `FclNET`.
- `TapeWinNET` and `TapeConNET` depend on `FclNET` for file filtering via the `FclTapeFileFilter` adapter. `TapeLibNET` remains independent of the FCL projects.
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

## For FCL Library: Architecture Conventions

- **Pipeline stages are independent:** Lexer, Parser, Validator, Evaluator, Formatter each operate on their own input (tokens, AST, etc.) and produce their own output. `FclPipeline` is the one-stop convenience wrapper.
- **AST is immutable:** All nodes use primary constructors with read-only properties. All nodes carry `SourceSpan` for diagnostic positions.
- **`IFclFileInfo` abstraction:** The evaluator operates against this interface, keeping FclNET independent of `TapeLibNET`. Consuming projects implement it for their file descriptor types.
- **Diagnostics via `FclDiagnostic`:** Unified structure (severity, error code, message, source span) for parse, validation, and runtime errors. Never throw exceptions for user input errors.
- **Canonical form:** The formatter always emits the word-form operators (not symbolic aliases), ISO 8601 dates, and re-collapses semicolons and value chains where applicable.
- **Test coverage:** FclNET.Tests uses xUnit with `InternalsVisibleTo`. Test helpers (`FclTestHelpers.ParseOk`, `Evaluate`, `ValidateOk`) reduce boilerplate. New features should include parser, evaluator, formatter, and integration tests.

## For AI Translation Library: Conventions

- **`IFclAiInteraction` callback pattern:** Modeled after TapeLibNET's `ITapeFileNotifiable` — a minimal interface that the library calls into, letting each app decide how to present the interaction.
- **Provider-agnostic via `IChatClient`:** All AI interaction goes through `Microsoft.Extensions.AI` abstractions. No direct OpenAI/Ollama SDK calls outside `FclAiProviderFactory`.
- **System prompt is self-contained:** `FclAiSystemPrompt` contains a condensed FCL reference optimized for LLM consumption. When the FCL language changes, update the prompt alongside the spec.

## For WPF App: Data model conventions

- **`TOCView` / `BackupSetView`** own per-set data (source files, `FilteredFileList`, checked state, saved filter state). `TapeTreeItemViewModel` is purely UI/navigation — no data-model fields.
- **`MainViewModel`** owns `_tocView` (session-level) and `_currentSetView` (currently displayed set). Views are created lazily via `TOCView.GetOrCreate`.
- **`BackupSetListItem`** is a lightweight display model — it does not own a `FilteredFileList`. `FilteredFileCount` and `CheckedFileCount` are pushed externally.
- **`FileFilterPane` dual-mode dispatch:** Direct mode (`FilterTarget` + `FilterStateChanged`) when the host has a `FilteredFileList` reference. Callback mode (`FilterRequested`) when the host manages filtering itself (e.g. RestoreWindow set mode).
- **Filter state persistence:** `CaptureRestoreAction(reapply)` captures the pane's full state into a restore delegate. Stored on `BackupSetView.SavedFilterState` for both applied and disabled filters, so the definition survives navigation.

## For WPF App: Structured logging — LogEntry + WarningLevel
public enum WarningLevel { None, Completed, Info, Warning, Failed, Error }

public record LogEntry(WarningLevel Level, string Message, bool IsSub, DateTime Timestamp)
{
    public string DisplayText { get; }  // "[HH:mm:ss] ⚠ Message" or "[HH:mm:ss] Message" (sub/None)
}
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

## For Stream Handling

- **ObserverStream**: The `FilterStream` has been renamed to `ObserverStream` for better semantic clarity. The `ObserverStream` abstract class in `TapeCRC.cs` intercepts Read/Write via `OnRead/OnWrite` hooks without transforming bytes.
