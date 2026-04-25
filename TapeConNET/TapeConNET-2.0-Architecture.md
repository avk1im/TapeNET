# TapeConNET 2.0 — Architecture & Project Plan

> Audience: contributors and reviewers of `TapeConNET` (the `tapecon.exe` CLI
> tape backup utility). Companion to `TapeNET-Context-Primer.md` and the WPF
> `TapeWinNET` notes in `.github/copilot-instructions.md`.

---

## 1. Why a 2.0 rewrite

`TapeConNET 1.x` started as a quick companion sample for `TapeLibNET` and grew
into a single ~1300-line `Program.cs` driven by a hand-written `-flag value`
parser. It works, but it is hard to extend, test, and use:

- One imperative pass: each `-flag` runs immediately; no validation phase, no
  dry-run, no proper exit codes.
- Hand-rolled help system (`TapeCon.Help.txt` + regex section lookup) drifts
  from the actual options.
- Plain-`Console` UX with `iii`/`vvv`/`!!!` ASCII prefixes; no color, no
  in-place progress, blocking `ReadKey` prompts, and no graceful
  `Ctrl+C` cancellation (the process is killed mid-write).
- Virtual drives are a parse hack inside `HandleDrive`.
- FCL only reaches `restore` / `list`; `backup` is still DOS-pattern only.
- No automated tests.
- Code drift versus `TapeWinNET`'s well-tested `TapeService`.

**TapeConNET 2.0 is a clean cut from 1.x.** No legacy shim — a brief
`docs/migration-from-1.0.md` will help users adapt their scripts.

---

## 2. Goals

1. First-class CLI: discoverable help, predictable parsing, scriptable, with
   proper exit codes.
2. Two-phase execution: parse + validate the **whole** command line, then
   execute a planned pipeline.
3. Modern UX: color by severity, in-place progress bars, interactive prompts
   that degrade cleanly to non-interactive, **cooperative `Ctrl+C`** that
   aborts the current operation (not the process).
4. Code organization that mirrors **TapeWinNET** conventions so changes
   propagate easily.
5. Feature parity with TapeWinNET where it makes sense (virtual drives as
   first-class, FCL for backup selection, multi-volume UX, structured
   logging) — out of scope: remote backends, trailing-set delete UI, saved
   filter state.
6. Cross-validate core operations against TapeWinNET; share/reuse code where
   extraction is cheap.
7. Automated test suite running against virtual drives in CI, with an opt-in
   real-drive suite (mirroring `TapeLibNET.Tests`).

---

## 3. Technology choices

| Concern              | Choice                              | Notes |
|----------------------|-------------------------------------|-------|
| Argument parsing     | `System.CommandLine` 2.0            | Microsoft-supported, parse-then-invoke, async + cancellation, auto-help |
| Console UX           | `Spectre.Console` 0.55              | Color, tables, prompts, progress bars, theming |
| Logging              | `Microsoft.Extensions.Logging` (Debug provider) | Already used by `TapeLibNET`; bridged into `IConsoleUx` |
| Tests                | xUnit + `Xunit.SkippableFact`       | Mirrors `TapeLibNET.Tests`; reuses `VirtualTapeFixture`, `XunitLoggerFactory`, `TempFileTree` |
| Target framework     | `net8.0-windows`, C# 12             | Same as the rest of the solution |

---

## 4. CLI shape

Verb-based — fits PowerShell habits, gets free help/validation, and maps 1:1
onto `TapeService` operations.

```text
tapecon
├─ drive
│  ├─ open [N] [--win32 N | --virtual-file PATH [--initiator PATH]
│  │       | --virtual-memory] [--capacity SIZE] [--init-capacity SIZE]
│  │       [--caps WithSetmarks|WithPartitions]
│  ├─ info
│  └─ eject
├─ media
│  ├─ format [--single] [--description TEXT]
│  ├─ info
│  └─ list  [--set RANGE] [PATTERN | FCL | --filter FCL | --filter-file PATH]
├─ backup   [PATHS|PATTERNS|FCL...] [--append [SET]] [--incremental]
│           [--description TEXT] [--block-size KB] [--capacity SIZE]
│           [--hash ALG] [--filemarks] [--subdirectories]
│           [--filter FCL | --filter-file PATH]
├─ restore  [SET] [--target DIR] [--existing skip|overwrite|keepboth]
│           [--subdirectories] [PATTERN | FCL | --filter ... | --filter-file ...]
├─ validate [SET] [PATTERN | FCL | --filter ... | --filter-file ...]
├─ verify   [SET] [PATTERN | FCL | --filter ... | --filter-file ...]
└─ docs <topic>     # concepts | migration | faq
```

Global options inherited by every verb: `--quiet` / `-q`, `--no-color`,
`--log-level`, `--yes` (auto-confirm), `--help`, `--version`.

### Convenience shortcuts

- **No drive verb implies drive 0.** Any verb that needs an open drive
  auto-opens **Win32 drive 0** with a single info-level log line. Suppressed
  under `--quiet`.
- **`drive open` with no arg = `drive open --win32 0`.** A bare positional
  number routes to `--win32 N`.
- **Bare PATHS / PATTERNS / FCL strings** are accepted everywhere
  `--filter`/`--filter-file` would be. Auto-detect order:
  1. Existing file with `.fcl` extension → FCL file.
  2. String starts with an FCL keyword (`name`, `path`, `size`, `date`,
     `attr`, `(`) → inline FCL.
  3. Existing directory or file path → literal path (recursed per
     `--subdirectories`).
  4. Otherwise → DOS pattern (e.g. `*.doc*`).

Examples:
```powershell
tapecon media list *.doc*
tapecon backup C:\MyDocs --hash xxhash3 --description "Daily docs"
tapecon restore 0 --target D:\Temp *.doc*
tapecon backup --filter-file .\nightly.fcl --incremental
```

---

## 5. Code architecture

### 5.1 Folder layout

```text
TapeConNET/
├─ Program.cs                          # ~30 lines: build root, hook Ctrl+C, run
├─ Cli/
│  ├─ RootCommandFactory.cs            # composes the verb tree
│  ├─ CommonOptions.cs                 # shared global + per-verb options
│  ├─ DriveBinder.cs                   # parses drive options into a DriveSpec
│  ├─ Drive/    DriveOpenCommand.cs    DriveInfoCommand.cs    DriveEjectCommand.cs
│  ├─ Media/    MediaFormatCommand.cs  MediaInfoCommand.cs    MediaListCommand.cs
│  ├─ Backup/   BackupCommand.cs
│  ├─ Restore/  RestoreCommand.cs      ValidateCommand.cs     VerifyCommand.cs
│  └─ Docs/     DocsCommand.cs
├─ Services/                           # PARTIALS of TapeService — mirror TapeWinNET
│  ├─ TapeService.cs                   # ctor, drive lifecycle, dispose, shared state
│  ├─ TapeService.Drive.cs             # OpenWin32, OpenVirtualFile, OpenVirtualMemory, Eject, Info
│  ├─ TapeService.Media.cs             # Format, LoadMedia, MediaInfo
│  ├─ TapeService.Toc.cs               # RestoreTOC, BackupTOC, set-index helpers
│  ├─ TapeService.Backup.cs            # BackupAsync(...) + multi-volume orchestration
│  ├─ TapeService.Restore.cs           # RestoreAsync, ValidateAsync, VerifyAsync
│  ├─ TapeService.List.cs              # ListAsync(setRange, filter)
│  └─ TapeService.Filtering.cs         # ResolveFilter(patterns|fcl|file) -> ITapeFileFilter
├─ Console/
│  ├─ IConsoleUx.cs   SpectreConsoleUx.cs   SilentConsoleUx.cs
│  ├─ WarningLevel.cs LogEntry.cs ConsoleTheme.cs
│  ├─ ProgressReporter.cs   Prompts.cs
├─ Processors/                         # ITapeFileNotifiable implementations
│  ├─ ConsoleFileEventProcessor.cs
│  ├─ BackupFileProcessor.cs   RestoreFileProcessor.cs
│  ├─ ValidateFileProcessor.cs VerifyFileProcessor.cs
├─ Filtering/  FclTapeFileFilter.cs    # already exists; extended for auto-detect
├─ Logging/    LoggerFactoryBuilder.cs
├─ Infrastructure/
│  ├─ DriveSpec.cs                     # discriminated record: Win32 / VirtualFile / VirtualMemory
│  ├─ TapeConExitCode.cs   TapeConException.cs   CancellationHooks.cs
└─ Resources/Docs/                     # embedded markdown rendered by 'tapecon docs'
   ├─ concepts.md   migration-from-1.0.md   faq.md

TapeConNET/Excluded Files/             # archived 1.x sources (excluded from compile)
├─ Program.Legacy.1.2.cs.txt
└─ TapeCon.Legacy.Help.txt
```

### 5.2 Layered responsibilities

```text
            ┌──────────────────────────────────────────────────────┐
            │                       Program                        │
            │   build root command • CancellationHooks • exit code │
            └────────────────────────┬─────────────────────────────┘
                                     │
                                     ▼
            ┌──────────────────────────────────────────────────────┐
            │                       Cli/                           │
            │  RootCommandFactory + verb classes                   │
            │  (System.CommandLine: parse → validate → invoke)     │
            └────────────────────────┬─────────────────────────────┘
                                     │ typed call into service
                                     ▼
            ┌──────────────────────────────────────────────────────┐
            │                     Services/                        │
            │  TapeService partials  (mirrors TapeWinNET)          │
            │  drive · media · toc · backup · restore · list       │
            └────────┬───────────────────────────┬─────────────────┘
                     │                           │
                     ▼                           ▼
        ┌──────────────────────────┐  ┌──────────────────────────┐
        │   TapeLibNET (drives,    │  │   Console/  +  Processors/│
        │   agents, TOC, virtual)  │  │   IConsoleUx + Spectre    │
        │   FclNET   (filters)     │  │   ProgressReporter, etc. │
        └──────────────────────────┘  └──────────────────────────┘
```

### 5.3 Key types and their roles

| Type | Role |
|------|------|
| `Program`                   | Build root command → wire `CancellationHooks` → invoke → translate exceptions to `TapeConExitCode`. |
| `RootCommandFactory`        | Pure composition: returns a `RootCommand` with all verbs. Takes `IConsoleUx` and (later) a `Func<TapeService>`. |
| `DriveSpec`                 | Discriminated record: `Win32(uint)` / `VirtualFile(...)` / `VirtualMemory(...)`. Built by `DriveBinder` from parsed options. |
| `TapeService` (partials)    | Mirrors WPF `TapeService`: owns `TapeDrive?`, cached `legacyTOC`, `IConsoleUx`, `ILoggerFactory`, `CancellationToken`. All ops `Task`-returning. |
| `IConsoleUx`                | Log, table, status, progress, confirm, select. Hides Spectre from services and tests. |
| `ConsoleFileEventProcessor` | `ITapeFileNotifiable` impl backed by Spectre `ProgressTask`; honors `CancellationToken`; prompts via `IConsoleUx.SelectAction`. |
| `WarningLevel` / `LogEntry` | Identical shape to TapeWinNET so service code can move both ways. |
| `LoggerFactoryBuilder`      | Same Debug-vs-Release behavior as 1.x, plus `--log-level` to surface `TapeLibNET` logs in `IConsoleUx`. |
| `TapeConException` + `TapeConExitCode` | Categorized non-zero exits; replaces `Environment.Exit` from arbitrary depth. |

### 5.4 Execution flow for a typical verb

```text
   user:  tapecon backup C:\MyDocs --hash xx3 -q
            │
            ▼
   Program  ── parse ──▶ System.CommandLine
            │
            ▼
   BackupCommand.SetAction:
            ├─ binds Settings (paths, hash, append, incremental, …)
            ├─ validates (mutually-exclusive options, required combos)
            └─ calls service.BackupAsync(settings, ct)
                                      │
                                      ▼
   TapeService.Backup.cs:
            ├─ EnsureDrive()  ← auto-opens Win32 drive 0 if absent
            ├─ ResolveFilter(paths)  ← auto-detect FCL vs path vs pattern
            ├─ creates BackupFileProcessor (progress, prompts, ct)
            ├─ runs TapeFileBackupAgent  (multi-volume loop)
            └─ writes TOC, logs summary via IConsoleUx
```

---

## 6. Phased plan

`✅` = done · `🟡` = in progress · `⬜` = pending

| Phase | Scope | Status |
|------:|-------|:------:|
| **1** | Skeleton: csproj 2.0, packages, `Program.cs`, `IConsoleUx`, `RootCommandFactory`, exit codes, Ctrl+C, `tapecon --help` works | ✅ |
| **2** | Console & infrastructure: `SpectreConsoleUx`, `SilentConsoleUx`, `Prompts`, `ProgressReporter`, `LoggerFactoryBuilder`, theme, demo verb to validate UX | ✅ |
| **3** | `TapeService` extraction: port logic from TapeWinNET `TapeService` partials (primary template) and from legacy `Program.cs` (CLI-only flows). All output via `IConsoleUx` | ✅ |
| **4** | Verb commands: full verb tree from §4, typed binding, validation, examples | ✅ |
| **5** | FCL auto-detect for backup/restore/validate/verify/list; finalize virtual-drive subcommands and shortcuts | ✅ |
| **6** | `TapeConNET.Tests`: parser tests, service tests against `VirtualTapeFixture`, end-to-end `TapeConRunner`, opt-in `[SkippableFact]` real-drive suite | 🟡 |
| **7** | Cross-validation with TapeWinNET; produce `docs/TapeService-Parity.md`; reconcile both apps | ⬜ | <-- Consider merging TapeService as a service layer of TapeLibNET
| **8** | Docs: render embedded `concepts.md`, `migration-from-1.0.md`, `faq.md` via `tapecon docs`; auto-generate `docs/cli-reference.md`; retire `tapecon.pdf` | ⬜ |

### Phase 1 exit criteria

- Solution builds.
- `tapecon --help` prints auto-generated help with the global options and the
  placeholder `docs` verb.
- `tapecon` (no args) prints the banner and a hint.
- `Ctrl+C` is intercepted via `CancellationHooks` (verified manually).
- `AssemblyVersion` reports `2.0.0.x`.

---

## 7. Out-of-scope vs. TapeWinNET (confirmed)

- ❌ Remote drive backends.
- ❌ Trailing-set delete UI (kept indirectly via `--append <set>` semantics).
- ❌ Saved filter state (FCL files cover this).
- ❌ Tree/visual navigation (replaced by `media list` table output).
- ❌ Interactive REPL/`shell` mode (use TapeWinNET for full interactivity).

---

## 8. Compatibility statement

TapeConNET 2.0 is **not** command-line-compatible with 1.x. The legacy
sources are preserved under `TapeConNET/Excluded Files/` for reference and a
short `docs/migration-from-1.0.md` will map the most common 1.x flag chains
to the new verbs.
