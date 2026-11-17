# TapeConNET 2.0 — Architecture overview

Audience: contributors and reviewers of TapeConNET, the `tapecon.exe` command-line tape backup utility.
Companion to `TapeNET-Context-Primer.md` and the TapeWinNET notes in `.github/copilot-instructions.md`.

This document describes the **as-built** architecture. End-user material ships embedded in the executable — the
Markdown topics under `TapeConNET/Resources/Docs/`, listed and rendered by `tapecon docs`, and the Examples sections
under `TapeConNET/Resources/Examples/`, appended to `tapecon <verb> --help`. Presentation (help, docs, colors) is
designed in `Design-CliPresentation.md`.

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

**One verb per process.** TapeWinNET keeps a session: a TOC imported or recovered stays in memory across operations.
A tapecon run lasts one verb, so whatever must outlive it — a TOC, a scan map — travels between verbs as a **file**
(§3.5).

## 2. Technology stack

| Concern | Choice |
|---|---|
| Argument parsing | System.CommandLine 2.0.7 (parse → validate → invoke, async + cancellation, custom help action) |
| Console UX | Spectre.Console (color, tables, prompts, progress bars) |
| Help and docs | own help model and Markdown-subset renderer over Spectre (Design-CliPresentation §3, §5) |
| Logging | Microsoft.Extensions.Logging (Debug provider + UX bridge) |
| File filter language | FclNET via `FclTapeFileFilter` |
| Size literals | FclNET `FclSizeLiteral` — one grammar for `--capacity 1.5GB` and `Size > 10MB` |
| Tests | xUnit, in-process CLI host (`TapeConHost`) |
| Target framework | `net8.0-windows`, C# 12 |

## 3. CLI surface

The verb tree is composed in `Cli/RootCommandFactory.cs`: one flat level of verbs. Every verb inherits the global
`--quiet` / `--no-color` / `--help`; every verb that opens a drive also takes the shared drive-selection options of
`Cli/GlobalOptions.cs`.

```
tapecon
├─ info │ format │ eject │ create-virtual (new-virtual)       lifecycle
├─ export-toc                                                TOC to a file
├─ backup                                                    write
├─ restore │ validate │ verify │ list                        read
├─ rename-media │ rename-set │ delete-sets                   TOC edits
├─ scan-media │ recover-toc                                  survey and TOC recovery
├─ docs [<topic>] [--list]                                   embedded docs
└─ demo                                                      UX smoke test
```

### 3.1 Drive selection

At most one selector per invocation; none opens physical drive 0, with one info line.

| Option | Meaning |
|---|---|
| `--drive N` | Physical Win32 drive N. |
| `--virtual-open PATH` (`--virtual`, `-V`) | EXISTING file-backed virtual media. Never creates; fails with a hint when absent. Sizes and mark profile come from the media. |
| `--virtual-create PATH` (`--virtual-new`) | NEW file-backed virtual media, formatted with an empty TOC, then the verb runs on it. |
| `--in-memory` (`-M`) | Fresh RAM-backed virtual media, gone when the process ends. |

New media is shaped by `--capacity`, `--init-capacity` / `--initiator`, the mark profile (`--setmarks` default,
`--seq-filemarks`, `--filemarks-only`) and `--min-` / `--def-` / `--max-block-size`.

A PATH without extension gets `.vt` (and `--virtual-open` finds it either way).

**Two overwrite permissions, deliberately separate:**

- `--overwrite-virtual` replaces existing virtual media **files** under `--virtual-create` / `create-virtual`. Without
  it, a prompt names what will be lost — backup sets, calibration data, or a foreign file; an unattended run declines.
- `--overwrite-media` (backup only) starts the **loaded media** over (§3.6). It is the user's consent to erase, nothing
  more: it answers no media check in advance.

**Virtual media records its mark profile** (setmarks, sequential filemarks or filemarks only) in its `.vrt` state, and
every open honors it, so `--virtual-open` needs no profile flag. Legacy media without a recorded profile is probed:
setmarks, then sequential filemarks, then filemarks only, until its TOC reads. Its first write then records the profile
used. For such media, a profile flag given on open overrides the probe.

### 3.2 File selection

Unified across `backup`, `restore`, `validate`, `verify` and `list`. `Filtering/FilterResolver.cs` classifies bare
positional arguments in this order:

1. Path to an existing `.fcl` file → load as FCL.
2. A string opening with `(`, `!` or `not`, or with an FCL field name (`Name`, `FullName`, `Path`, `Extension`,
   `Size`, `Created`, `Modified`, `Attributes`) **followed by whitespace or an operator symbol** → inline FCL. A field
   name alone does not count: `size-report.xlsx` and `names*.txt` stay a file and a wildcard.
3. Existing path → literal file or folder (recurse with `--subdirs`).
4. Otherwise → DOS wildcard.

`--filter <FCL>` and `--filter-file <PATH>` bypass the auto-detection.

**Combining, as TapeWinNET does:** paths and wildcards are the basic selection, OR-ed; every FCL source (inline, `.fcl`
file, `--filter`, `--filter-file`) is a condition AND-ed with it. For `backup`, paths and wildcards are the sources and
the FCL conditions filter what they name. Each part is parenthesized on lines of its own, so a trailing `//` comment in
an `.fcl` file cannot swallow the closing parenthesis.

### 3.3 Sizes

Every size option — capacities and block sizes — accepts `65536`, `64KB`, `64KiB`, `1.5GB`, `2 MB`. Units are binary
(1 KB = 1 KiB = 1,024 B). The CLI refuses `,` and `_`, so a German `1,5GB` fails loudly instead of meaning 15 GB.

### 3.4 Exit codes

| Code | Name | Meaning |
|---|---|---|
| 0 | Ok | Completed. |
| 1 | FatalError | Unexpected error. |
| 2 | UsageError | Command line invalid or contradictory. |
| 3 | OperationFailed | Drive, media or I/O failure; or completed with failed files. |
| 4 | Cancelled | Ctrl+C, or a destructive step declined (e.g. virtual media not overwritten). |
| 5 | WriteRefused | Set-header verification refused a destructive write, or `recover-toc`'s swap guard refused a read; tape unchanged. |
| 6 | PasswordRequired | Encrypted sets needed a password that was missing or wrong. |

### 3.5 TOC files

A TOC travels between verbs as a `.tapetoc` file (`TapeServiceBase.TOCFileExtension`).

| Verb / option | Role |
|---|---|
| `export-toc PATH` | Reads the loaded media's TOC and saves it: a spare copy, or a later volume's **series TOC**, which lists every set of the volumes before it. |
| `scan-media [--export DIR] [--no-recover-toc] [--json]` | Surveys the media without a TOC. By default recovers every TOC copy found and saves the map (`.tapescan`) **and** each recovered copy (`Scan_<id>_<time>_toc<N>.tapetoc`) into DIR — the current folder without `--export`; with `--json`, only when `--export` is given. |
| `recover-toc --map FILE --fragment N --save PATH` | Recovers one TOC copy listed in a map — for maps scanned with `--no-recover-toc`. Reads the copy from the loaded media, guarded against a swapped cartridge; all three options required. |
| `--import-toc PATH` | On `list`, `restore`, `validate`, `verify`, `delete-sets`, `rename-media`: replaces reading the media's TOC (`VerbHost.BuildAndOpen`). Not on `backup`: a backup trusts only the media's own TOC. |

- **A later volume's TOC** of the loaded volume's series is accepted without a question
  (`ImportTOCFromFileAsync(..., acceptSeriesToc: true)`) and held as `TOCSource.Series`: **read-only**. `delete-sets`
  and `rename-media` refuse it — written there, it would make the earlier volume claim the later volumes' sets. An
  earlier volume's TOC is still questioned.
- **Repair** writes an imported TOC back: `rename-media --import-toc` without a new name re-saves it under its current
  name; `delete-sets --import-toc` trims the volume's tail, then saves it.
- **Not yet:** `list --import-toc` without any drive. Both the import and the listing need an open drive today.

User-level walkthrough: `tapecon docs media-recovery`.

### 3.6 Where a backup goes

As in TapeWinNET:

| Option | Effect |
|---|---|
| `--append` (`-a`), the default | After the last set; keeps the media header and the TOC. Needs TapeNET media (formatted, or holding sets): reading the TOC fails otherwise, with the hint to format or overwrite. |
| `--append-after SET` | After the given set; the sets behind it are overwritten. |
| `--overwrite-media` | Over the entire media: every set, the TOC and the media header. Works on blank or foreign media. Excludes `--append`, `--append-after`, `--incremental`. |
| `--virtual-create` | New media, written from its start; `--append-after` and `--incremental` refused. |

**Media description** (`--media-description`, `--mdesc`; `Cli/MediaOptions.cs`) wherever media is created or renamed:
`format`, `create-virtual`, `rename-media` — there also `--name` / `-n` — and `backup` with `--overwrite-media` or
`--virtual-create`. Not `--name` on `backup`, which names both the set and the media; given while appending, it is
ignored with a warning. `rename-set` takes the set's `--description` (`-D`, also `--name`). Given the text, neither
rename asks.

## 4. Project layout (as-built)

```
TapeConNET/
├─ Program.cs                     entry point, NO_COLOR / colour warnings, exit-code translation
├─ Cli/
│  ├─ RootCommandFactory.cs       composes the verb tree, installs the help action
│  ├─ GlobalOptions.cs            drive selection, capacities, --log-level, --quiet, --no-color
│  ├─ VirtualMediaOptions.cs      --virtual-create, mark profile, block sizes, --overwrite-virtual
│  ├─ SizeOptions.cs              size-literal option factory
│  ├─ FilterOptions.cs            --filter / --filter-file
│  ├─ PasswordOptions.cs          password sources; SchemeWords (encryption / compression words)
│  ├─ MediaOptions.cs             --media-description (per verb)
│  ├─ TocOptions.cs               --import-toc
│  ├─ VerbHost.cs                 per-verb plumbing: build service, open drive, lifecycle steps, TOC import
│  ├─ InfoCommand.cs   FormatCommand.cs   EjectCommand.cs   CreateVirtualCommand.cs   ExportTocCommand.cs
│  ├─ BackupCommand.cs   RestoreCommand.cs   ListCommand.cs
│  ├─ RenameCommand.cs   DeleteSetsCommand.cs   ScanMediaCommand.cs
│  ├─ DocsCommand.cs   DocTopics.cs   DemoCommand.cs
│  └─ Help/                       HelpGroups · HelpModel · HelpRenderer · TapeConHelpAction
├─ Services/
│  ├─ TapeService.cs              thin wrapper over TapeServiceBase
│  ├─ TapeService.Backup.cs       backup progress glue
│  └─ TapeService.Restore.cs      restore / validate / verify glue
├─ Ux/
│  ├─ IConsoleUx.cs   SpectreConsoleUx.cs   SilentConsoleUx.cs
│  ├─ ConsoleUxServiceHost.cs     ITapeServiceHost → IConsoleUx bridge
│  ├─ IProgressScope.cs   SpectreProgressScope.cs   (suspendable; Design-CliPresentation §6.2)
│  ├─ ConsoleDocument.cs   ConsoleCapabilities.cs   ConsoleTheme.cs
│  ├─ WarningLevel.cs   LogEntry.cs
│  └─ Markdown/                   MarkdownDocument (parser) · MarkdownRenderer
├─ Filtering/FilterResolver.cs    FCL / path / wildcard auto-detection and combination
├─ FclTapeFileFilter.cs           FclNET → ITapeFileFilter adapter
├─ Logging/
│  ├─ LoggerFactoryBuilder.cs
│  └─ ConsoleUxLoggerProvider.cs  surfaces TapeLibNET logs through IConsoleUx
├─ Infrastructure/
│  └─ TapeConExitCode.cs   TapeConException.cs   CancellationHooks.cs
└─ Resources/
   ├─ Docs/*.md                   'tapecon docs' topics       (resource names docs/<topic>.md)
   └─ Examples/*.md               Examples of '<verb> --help' (resource names examples/<verb>.md)
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
│  RootCommandFactory · verb classes · VerbHost · Help/    │
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
│ TOC, virtual media, scan)  │  │ IConsoleUx · Spectre     │
│ FclNET (filters, sizes)    │  │ ConsoleUxServiceHost     │
└────────────────────────────┘  └──────────────────────────┘
```

### Key types

| Type | Role |
|---|---|
| `Program` | Builds the root command, wires `CancellationHooks`, invokes with System.CommandLine's default exception handler **off**, translates exceptions to `TapeConExitCode`. |
| `RootCommandFactory` | Pure composition of the verb tree; takes only `IConsoleUx`. |
| `VerbHost` | `Build` (service only) and `BuildAndOpen` (service + lifecycle steps, optional `--import-toc` in place of the TOC read); drive selection; shared `CreateVirtualFromOptionsAsync`; `ToExitCode`. |
| `GlobalOptions` / `VirtualMediaOptions` / `TocOptions` | Shared option instances; profile and size resolution. |
| `MediaOptions` | `--media-description`, created per verb (with or without the `--name` aliases). |
| `SizeOptions` | Option factory for byte sizes, parsing through `FclSizeLiteral`. |
| `TapeService` (partials) | Thin shim over `TapeServiceBase`; console progress scope, cancellation token, FCL pattern filter. |
| `IConsoleUx` | Log, banner, documents (`Write`), confirm, select, ask, secret, progress. Hides Spectre from services and tests. |
| `ConsoleUxServiceHost` | `ITapeServiceHost` adapter: engine prompts → `IConsoleUx`; unattended defaults split by stakes. |
| `HelpModel` / `HelpRenderer` / `TapeConHelpAction` | Grouped, themed help; Examples appended from resources. |
| `DocTopics` / `MarkdownDocument` / `MarkdownRenderer` | Docs topics and examples: discovery, parsing, rendering. |
| `TapeConExitCode` / `TapeConException` | Categorized exit codes; verbs throw, `Program` translates. |

### Shared virtual-media layer (TapeLibNET)

| Type | Role |
|---|---|
| `TapeServiceBase.CreateVirtualMediaAsync` | Probe what would be destroyed → confirm → create → load → format. Returns Created / Declined / Failed. |
| `TapeServiceBase.OpenExistingVirtualDriveAsync` | Open existing media only; initiator by convention or explicit path. |
| `VirtualMediaPaths` | File conventions: `.vrt` metadata, `_init` initiator, `_volNN` volumes. Shared with TapeWinNET. |
| `VirtualMediaDefaults` | Default sizes of new media, shared with TapeWinNET. |

### Shared TOC and scan layer (TapeLibNET)

| Member | Role for the CLI |
|---|---|
| `ImportTOCFromFileAsync(path, acceptSeriesToc)` | `--import-toc`; a later volume's TOC → `TOCSource.Series`, read-only. |
| `ExportTOCToFileAsync` | `export-toc`. |
| `ScanMediaAsync` with `ScanMediaRequest.ExportRecoveredTocs` | `scan-media` saves every recovered TOC; paths in `ScanMediaResult.TocExportPaths`. |
| `RecoverTocAsync` with `SaveToFilePath` | `recover-toc`; adoption (`Adopt`) is TapeWinNET's. |

## 6. Execution flow of a typical verb

```
user:  tapecon backup C:\MyDocs --virtual-open D:\vt\weekly --hash xx3 -q
         │
         ▼
Program ── parse ──▶ System.CommandLine
         │
         ▼
BackupCommand action
  ├─ binds options (sources, hash, placement, compression, encryption, …)
  ├─ refuses contradictions before any I/O (UsageError)
  └─ VerbHost.BuildAndOpen(steps)
        ├─ OpenDriveFromOptions ─▶ OpenExistingVirtualDriveAsync
        ├─ LoadMediaAsync
        └─ RestoreTOCAsync (append, the default) — or none (--overwrite-media)
         │
         ▼
TapeServiceBase.ExecuteBackupAsync
  ├─ TapeFileBackupAgent (multi-volume aware)
  ├─ writes / updates the TOC
  └─ file events ─▶ ConsoleUxServiceHost ─▶ IConsoleUx (prompts suspend the progress bar)
         │
         ▼
VerbHost.ToExitCode(result) ─▶ Program ─▶ OS
```

With `--import-toc PATH`, `BuildAndOpen` imports the file after loading the media and skips reading the media's TOC.

Ctrl+C goes through `CancellationHooks`: the first press requests cooperative cancellation, the second aborts hard.
Verbs preserve a writable TOC where possible.

## 7. Test strategy

| Project | Scope |
|---|---|
| TapeConNET.Tests | End-to-end CLI tests through `Helpers/TapeConHost.cs` against `TempVirtualMedia`: round trips, placement (append default, `--overwrite-media`, media description), set index, FCL filters and classification, large and nested trees, incremental chains, selective restore, validate/verify, encryption, virtual-media creation and opening, size parsing, TOC files and recovery (`TocRecoveryTests`); help model, renderer and layout; docs and examples — every command line in every topic and example parses against the real tree. |
| TapeLibNET.Tests | Engine tests: TOC, agents, virtual media, scanner, multi-volume orchestration via `MultiVolumeTapeServiceHost`. |
| FclNET.Tests | Parser, evaluator, formatter, integration. |

`TempVirtualMedia.DriveArgs()` creates its media on first use (`--virtual-create` with its sizes) and opens it
afterwards (`--virtual-open`). `ConsoleUxServiceHost` cannot supply a fresh volume mid-run, and the CLI cannot damage a
tape: multi-volume coverage, damaged TOC copies and a later volume's TOC on an earlier volume stay in TapeLibNET.Tests.

## 8. Out of scope

- Remote drive backends in the CLI.
- Persistent saved filter state (FCL files cover it).
- Tree navigation (covered by `tapecon list`).
- Interactive REPL mode (TapeWinNET offers full interactivity).
- A session across verbs (TOC files cover it, §3.5).

## 9. Versioning and packaging

- `AssemblyName = tapecon`; assembly version 2.0.0.x — `TapeConNET.csproj` overrides the shared 1.2.x baseline from
  `Versioning.targets`.
- Embedded docs and examples ship inside the executable (`LogicalName` `docs/…`, `examples/…`), so `tapecon docs` and
  the help's Examples work in single-file deployments. Not compressed: roughly 100–150 KB of text.
- The legacy DOCX → PDF pipeline is retired.
