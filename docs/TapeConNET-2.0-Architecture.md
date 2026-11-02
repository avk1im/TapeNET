# TapeConNET 2.0 — Architecture overview

Audience: contributors and reviewers of TapeConNET, the `tapecon.exe` command-line tape backup utility.
Companion to `TapeNET-Context-Primer.md` and the TapeWinNET notes in `.github/copilot-instructions.md`.

This document describes the **as-built** architecture. End-user material ships embedded in the executable —
`TapeConNET/Resources/Docs/{concepts, migration-from-1.0, faq}.md`, rendered by `tapecon docs <topic>` — and every
verb documents itself through `tapecon <verb> --help`.

## 1. Role within the TapeNET solution

TapeConNET is one of two front ends on the shared TapeLibNET engine.

```
┌──────────────────────────────┐    ┌──────────────────────────────┐
│      TapeWinNET (WPF)        │    │   TapeConNET (tapecon.exe)   │
└──────────────┬───────────────┘    └──────────────┬───────────────┘
               │                                   │
               ▼                                   ▼
┌─────────────────────────────────────────────────────────────────┐
│             TapeLibNET.Services  (TapeServiceBase)              │
│  drive · media · virtual media · TOC · backup · restore ·       │
│  validate · verify · list · scan · multi-volume · file events   │
└──────────────┬──────────────────────────────────┬───────────────┘
               │                                  │
               ▼                                  ▼
┌──────────────────────────────┐    ┌──────────────────────────────┐
│ TapeLibNET (drives, agents,  │    │  FclNET (filter language,    │
│  TOC, virtual media, I/O)    │    │  size literals)              │
└──────────────────────────────┘    └──────────────────────────────┘
```

TapeConNET owns **only** its CLI surface, console UX and a thin `TapeService` wrapper. All I/O, multi-volume
orchestration, TOC handling, virtual-media creation policy and filter dispatch live in the shared layer.

## 2. Technology stack

| Concern | Choice |
|---|---|
| Argument parsing | System.CommandLine 2.0.7 (parse → validate → invoke, async + cancellation, auto-help) |
| Console UX | Spectre.Console 0.55 (color, tables, prompts, progress bars) |
| Logging | Microsoft.Extensions.Logging (Debug provider + UX bridge) |
| File filter language | FclNET via `FclTapeFileFilter` |
| Size literals | FclNET `FclSizeLiteral` — one grammar for `--capacity 1.5GB` and `size > 10MB` |
| Tests | xUnit, in-process CLI host (`TapeConHost`) |
| Target framework | net8.0-windows, C# 12 |

## 3. CLI surface

The verb tree is composed in `Cli/RootCommandFactory.cs`. Every verb inherits the global `--quiet` / `--no-color`;
every verb that opens a drive also takes the shared drive-selection options of `Cli/GlobalOptions.cs`.

```
tapecon
├─ info │ format │ eject │ toc {export │ import}         lifecycle
├─ create-virtual (new-virtual)                          new virtual media
├─ backup                                                write
├─ restore │ validate │ verify │ list                    read
├─ rename-media │ rename-set │ delete-sets               TOC edits
├─ scan-media │ recover-toc                              survey and TOC recovery
├─ docs {concepts │ migration │ faq} [--list]            embedded docs
└─ demo                                                  UX smoke test
```

### 3.1 Drive selection

At most one selector per invocation; none opens physical drive 0.

| Option | Meaning |
|---|---|
| `--drive N` | Physical Win32 drive N. |
| `--virtual-open PATH` (`--virtual`, `-V`) | EXISTING file-backed virtual media. Never creates; fails with a hint when absent. Sizes come from the media. |
| `--virtual-create PATH` (`--virtual-new`) | NEW file-backed virtual media, formatted with an empty TOC, then the verb runs on it. |
| `--in-memory` | Fresh RAM-backed virtual media, gone when the process ends. |

New media is shaped by `--capacity`, `--init-capacity` / `--initiator`, the mark profile (`--setmarks` default,
`--seq-filemarks`, `--filemarks-only`) and `--min-` / `--def-` / `--max-block-size`. Existing files are overwritten
only after a prompt that names what will be lost — backup sets, calibration data, or a foreign file — or under
`--overwrite-media`; an unattended run declines.

The mark profile is a **drive** property the media does not record: open media with the profile it was created with.

### 3.2 File selection

Unified across `backup`, `restore`, `validate`, `verify` and `list`. `Filtering/FilterResolver.cs` classifies bare
positional arguments in this order:

1. Path to an existing `.fcl` file → load as FCL.
2. String starting with an FCL keyword (`name`, `path`, `size`, `date`, `attr`, `extension`, `(`, …) → inline FCL.
3. Existing path → literal file or folder (recurse with `--subdirs`).
4. Otherwise → DOS wildcard.

`--filter <FCL>` and `--filter-file <PATH>` bypass the auto-detection.

### 3.3 Sizes

Every size option — capacities and block sizes — accepts `65536`, `64KB`, `64KiB`, `1.5GB`, `2 MB`. Units are binary
(1 KB = 1 KiB = 1,024 B). The CLI refuses `,` and `_`, so a German `1,5GB` fails loudly instead of meaning 15 GB.

### 3.4 Exit codes

| Code | Name | Meaning |
|---|---|---|
| 0 | `Ok` | Completed. |
| 1 | `FatalError` | Unexpected error. |
| 2 | `UsageError` | Command line invalid or contradictory. |
| 3 | `OperationFailed` | Drive, media or I/O failure. |
| 4 | `Cancelled` | Ctrl+C, or a destructive step declined (e.g. virtual media not overwritten). |
| 5 | `WriteRefused` | Set-header verification refused a destructive write; tape unchanged. |
| 6 | `PasswordRequired` | Encrypted sets needed a password that was missing or wrong. |

## 4. Project layout (as-built)

```
TapeConNET/
├─ Program.cs                     entry point, exit-code translation
├─ Cli/
│  ├─ RootCommandFactory.cs       composes the verb tree
│  ├─ GlobalOptions.cs            drive selection, sizes, --overwrite-media, --log-level
│  ├─ VirtualMediaOptions.cs      --virtual-create, mark profile, block sizes
│  ├─ SizeOptions.cs              size-literal option factory
│  ├─ FilterOptions.cs            --filter / --filter-file
│  ├─ VerbHost.cs                 per-verb plumbing: build service, open drive, lifecycle steps
│  ├─ InfoCommand.cs   FormatCommand.cs   EjectCommand.cs   TocCommand.cs
│  ├─ CreateVirtualCommand.cs
│  ├─ BackupCommand.cs   RestoreCommand.cs   ListCommand.cs
│  ├─ RenameCommand.cs   DeleteSetsCommand.cs   ScanMediaCommand.cs
│  └─ DemoCommand.cs   DocsCommand.cs
├─ Services/
│  ├─ TapeService.cs              thin wrapper over TapeServiceBase
│  ├─ TapeService.Backup.cs       backup progress glue
│  └─ TapeService.Restore.cs      restore / validate / verify glue
├─ Ux/
│  ├─ IConsoleUx.cs   SpectreConsoleUx.cs   SilentConsoleUx.cs
│  ├─ ConsoleUxServiceHost.cs     ITapeServiceHost → IConsoleUx bridge
│  ├─ IProgressScope.cs   SpectreProgressScope.cs
│  └─ WarningLevel.cs   LogEntry.cs   ConsoleTheme.cs
├─ Filtering/FilterResolver.cs    FCL / path / wildcard auto-detection
├─ FclTapeFileFilter.cs           FclNET → ITapeFileFilter adapter
├─ Logging/
│  ├─ LoggerFactoryBuilder.cs
│  └─ ConsoleUxLoggerProvider.cs  surfaces TapeLibNET logs through IConsoleUx
├─ Infrastructure/
│  └─ TapeConExitCode.cs   TapeConException.cs   CancellationHooks.cs
└─ Resources/Docs/                embedded markdown for 'tapecon docs'
```

Legacy 1.x sources stay archived under `TapeConNET/Excluded Files/`, excluded from compilation.

## 5. Layered responsibilities

```
┌──────────────────────────────────────────────────────────┐
│                         Program                          │
│  root command · CancellationHooks · exception → exit code│
└────────────────────────────┬─────────────────────────────┘
                             ▼
┌──────────────────────────────────────────────────────────┐
│                           Cli/                           │
│  RootCommandFactory · verb classes · VerbHost            │
│  (System.CommandLine: parse → validate → invoke)         │
└────────────────────────────┬─────────────────────────────┘
                             ▼
┌──────────────────────────────────────────────────────────┐
│                        Services/                         │
│  TapeConNET.TapeService ⇢ TapeLibNET TapeServiceBase     │
└──────────────┬──────────────────────────────┬────────────┘
               ▼                              ▼
┌────────────────────────────┐  ┌──────────────────────────┐
│ TapeLibNET (drives, agents,│  │ Ux/ + Logging/           │
│ TOC, virtual media)        │  │ IConsoleUx · Spectre     │
│ FclNET (filters, sizes)    │  │ ConsoleUxServiceHost     │
└────────────────────────────┘  └──────────────────────────┘
```

### Key types

| Type | Role |
|---|---|
| `Program` | Builds the root command, wires `CancellationHooks`, invokes with System.CommandLine's default exception handler **off**, translates exceptions to `TapeConExitCode`. |
| `RootCommandFactory` | Pure composition of the verb tree; takes only `IConsoleUx`. |
| `VerbHost` | `Build` (service only) and `BuildAndOpen` (service + lifecycle steps); drive selection; shared `CreateVirtualFromOptionsAsync`. |
| `GlobalOptions` / `VirtualMediaOptions` | Shared option instances, attached to every drive-opening verb; profile and size resolution. |
| `SizeOptions` | Option factory for byte sizes, parsing through `FclSizeLiteral`. |
| `TapeService` (partials) | Thin shim over `TapeServiceBase`; console progress scope, cancellation token, FCL pattern filter. |
| `IConsoleUx` | Log, banner, confirm, select, ask, secret, progress. Hides Spectre from services and tests. |
| `ConsoleUxServiceHost` | `ITapeServiceHost` adapter: engine prompts → `IConsoleUx`; unattended defaults split by stakes. |
| `TapeConExitCode` / `TapeConException` | Categorized exit codes; verbs throw, `Program` translates. |

### Shared virtual-media layer (TapeLibNET)

| Type | Role |
|---|---|
| `TapeServiceBase.CreateVirtualMediaAsync` | Probe what would be destroyed → confirm → create → load → format. Returns `Created` / `Declined` / `Failed`. |
| `TapeServiceBase.OpenExistingVirtualDriveAsync` | Open existing media only; initiator by convention or explicit path. |
| `VirtualMediaPaths` | File conventions: `.vrt` metadata, `_init` initiator, `_volNN` volumes. Shared with TapeWinNET. |
| `VirtualMediaDefaults` | Default sizes of new media, shared with TapeWinNET. |

## 6. Execution flow of a typical verb

```
user:  tapecon backup C:\MyDocs --virtual-open D:\vt\weekly.vt --hash xx3 -q
         │
         ▼
Program ── parse ──▶ System.CommandLine
         │
         ▼
BackupCommand action
  ├─ binds options (sources, hash, append, compression, encryption, …)
  ├─ refuses contradictions before any I/O (UsageError)
  └─ VerbHost.BuildAndOpen(steps)
        ├─ OpenDriveFromOptions ─▶ OpenExistingVirtualDriveAsync
        ├─ LoadMediaAsync
        └─ RestoreTOCAsync (append) 
         │
         ▼
TapeServiceBase.ExecuteBackupAsync
  ├─ TapeFileBackupAgent (multi-volume aware)
  ├─ writes / updates the TOC
  └─ file events ─▶ ConsoleUxServiceHost ─▶ IConsoleUx
         │
         ▼
VerbHost.ToExitCode(result) ─▶ Program ─▶ OS
```

Ctrl+C goes through `CancellationHooks`: the first press requests cooperative cancellation, the second aborts hard.
Verbs preserve a writable TOC where possible.

## 7. Test strategy

| Project | Scope |
|---|---|
| TapeConNET.Tests | End-to-end CLI tests through `Helpers/TapeConHost.cs` against `TempVirtualMedia`: round trips, append, set index, FCL filters, large and nested trees, incremental chains, selective restore, validate/verify, encryption, docs, virtual-media creation and opening, size parsing. |
| TapeLibNET.Tests | Engine tests: TOC, agents, virtual media, multi-volume orchestration via `MultiVolumeTapeServiceHost`. |
| FclNET.Tests | Parser, evaluator, formatter, integration. |

`TempVirtualMedia.DriveArgs()` creates its media on first use (`--virtual-create` with its sizes) and opens it
afterwards (`--virtual-open`). `ConsoleUxServiceHost` cannot supply a fresh volume mid-run, so multi-volume coverage
stays in TapeLibNET.Tests.

## 8. Out of scope

- Remote drive backends in the CLI.
- Persistent saved filter state (FCL files cover it).
- Tree navigation (covered by `tapecon list`).
- Interactive REPL mode (TapeWinNET offers full interactivity).

## 9. Versioning and packaging

- `AssemblyName` = `tapecon`; assembly version 2.0.0.x — `TapeConNET.csproj` overrides the shared 1.2.x baseline from
  `Versioning.targets`.
- Embedded docs ship inside the executable, so `tapecon docs` works in single-file deployments.
- The legacy DOCX → PDF pipeline is retired.
