# tapecon — CLI reference

> Auto-generated from `tapecon --help` and `tapecon <verb> --help` against
> `tapecon 2.0.0`. Regenerate after any change to the verb tree.

## Synopsis

```
tapecon [global-options] <verb> [verb-options] [arguments]
```

`tapecon` is the `TapeConNET 2.0` command-line tape backup utility built on
the shared `TapeLibNET.Services` engine. Run `tapecon --help` for the verb
tree and `tapecon <verb> --help` for verb-specific options.

## Global options

| Option | Description |
|--------|-------------|
| `-q`, `--quiet` | Suppress informational output and auto-confirm prompts. |
| `--no-color`    | Disable ANSI color output. |
| `-?`, `-h`, `--help` | Show help and usage information. |
| `--version`     | Show version information. |

## Drive-selection options (most verbs)

These options are attached to every verb that opens a drive
(`info`, `format`, `eject`, `toc`, `backup`, `restore`, `validate`, `verify`,
`list`, `rename-media`, `rename-set`).

| Option | Description |
|--------|-------------|
| `-d`, `--drive <N>`            | Open the physical Win32 tape drive number N (0-based). Mutually exclusive with `--virtual` / `--in-memory`. Default: `0`. |
| `-V`, `--virtual <PATH>`       | Open a file-backed virtual drive. PATH is the content file. |
| `-I`, `--initiator <PATH>`     | Initiator (TOC) partition file path; only with `--virtual`. |
| `-M`, `--in-memory`            | Open an in-memory virtual drive (no files). |
| `--capacity <BYTES>`           | Content partition capacity for new virtual media (default 1 GiB file / 64 MiB memory). |
| `--init-capacity <BYTES>`      | Initiator partition capacity for new virtual media (default 24 MiB). |
| `--log-level <LEVEL>`          | Minimum severity surfaced from `TapeLibNET` to the console (`Trace` / `Debug` / `Information` / `Warning` / `Error` / `Critical` / `None`). Default: `None`. |

## Filter options (selection verbs)

These options are attached to `backup`, `restore`, `validate`, `verify`,
and `list`. Bare positional arguments are auto-classified as
`.fcl` files, inline FCL, paths, or wildcards.

| Option | Description |
|--------|-------------|
| `--filter <FCL>`           | Inline FCL expression used as the selection filter (e.g. `name matches "*.doc*" && size > 1MB`). |
| `--filter-file <PATH>`     | Path to a file containing the FCL selection filter. |

## Verbs

### `tapecon info`

Show drive, media, and backup-sets overview. Use `--drive` or `--media` to
limit the output depth.

```
tapecon info [options]
```

| Option | Description |
|--------|-------------|
| `--drive` | Show drive hardware properties only (no media access). Superseded by `--media` or `--full`. |
| `--media` | Show drive + media info only (no TOC restore). Superseded by `--full`. |
| `--full`  | Show drive + media + compact backup-sets table (TOC restored). Default when no flag is supplied. |

### `tapecon format`

Format the loaded media and write an empty initial TOC.
**WARNING: erases all data.**

```
tapecon format [options]
```

| Option | Description |
|--------|-------------|
| `-n`, `--name <NAME>`              | Friendly media name written into the new TOC. |
| `--single`, `--single-partition`   | Force single-partition format (TOC stored in the first set instead of an initiator partition). |
| `-y`, `--yes`                      | Skip the confirmation prompt. Required for non-interactive use. |

### `tapecon eject`

```
tapecon eject [options]
```

Unloads the media from the selected drive.

### `tapecon toc`

Export or import the table of contents (TOC) to/from a file.

```
tapecon toc export <path>   # save the loaded TOC to a file
tapecon toc import <path>   # load a TOC from a file (overrides on-tape TOC for this session)
```

### `tapecon backup`

Back up files/folders to the loaded media. Each positional argument is a
file path, folder path, or wildcard pattern. Bare FCL expressions or paths
to `.fcl` files are also accepted as filters.

```
tapecon backup <files>... [options]
```

| Option | Description |
|--------|-------------|
| `-D`, `--desc`, `--description <TEXT>` | Backup-set description written into the TOC. |
| `-s`, `--subdirs`                      | Recurse into subdirectories when expanding folders/patterns. |
| `-i`, `--incremental`                  | Create an incremental backup set (only changed files since the last full set). |
| `-b`, `--block-size <BYTES>`           | Block size in bytes (default: drive's preferred size; clamped to drive limits). |
| `-H`, `--hash <ALG>`                   | Per-file hash algorithm: `None` / `Crc32` / `Crc64` / `XxHash32` / `XxHash3` / `XxHash64` / `XxHash128`. Default: `Crc32`. |
| `-a`, `--append`                       | Append a new set to existing media (default: replace all). |
| `--append-after <SET>`                 | Append after a specific set (replaces all sets after the given index). Implies `--append`. |
| `-f`, `--filemarks`                    | Use filemarks between files (slower seek, more compatible). Default: blob mode. |
| `--skip-errors`                        | Skip per-file errors automatically without prompting. |
| `--emergency-toc <FOLDER>`             | Folder for the emergency TOC export if writing the TOC to tape fails. |

### `tapecon restore`

Restore files from the loaded media to a target folder.

```
tapecon restore [<set> [<filter-args>...]] [options]
```

| Argument / option | Description |
|--------------------|-------------|
| `<set>`                                 | Set index to process (positive = oldest-up; `0` = latest; `-1`/`-2`/… = older). Default: `0`. |
| `<filter-args>`                         | Optional bare filter arguments (wildcards, inline FCL, or `.fcl` path). Combined with `--filter` / `--filter-file`. |
| `-t`, `--target <FOLDER>`               | Target folder for restored files. |
| `-s`, `--subdirs`                       | Recreate the original directory structure under the target folder. |
| `-x`, `--existing <Skip\|Overwrite\|KeepBoth>` | How to handle existing target files. Default: `KeepBoth`. |
| `-i`, `--incremental [true\|false]`     | Force-on or force-off the incremental chain expansion. Default: follow the set's flag. |
| `--skip-errors`                         | Skip per-file errors automatically without prompting. |

### `tapecon validate`

Validate per-file CRC integrity for a backup set without writing files.

```
tapecon validate [<set> [<filter-args>...]] [options]
```

Same `<set>`, `<filter-args>`, `--subdirs`, `--incremental`, and
`--skip-errors` semantics as `restore`.

### `tapecon verify`

Verify a backup set against on-disk files (byte-by-byte compare).

```
tapecon verify [<set> [<filter-args>...]] [options]
```

Same arguments and options as `validate`. Requires the original source
files to still be reachable on disk.

### `tapecon list`

List the contents of the loaded media. Optional set range and file
patterns may be specified.

```
tapecon list [<args>...] [options]
```

`<args>` may be a list of set indexes followed by filter arguments.
Examples: `0`, `1 3`, `-2 0 *.txt`, `photos/*.jpg`,
`0 "size > 1MB"`, `./nightly.fcl`.

| Option | Description |
|--------|-------------|
| `--no-incremental` | List only the selected incremental set without expanding earlier dependencies. |
| `--name-only`      | Show file names only (no full paths). |
| `--sets-only`      | Show a compact backup-sets table only (no per-file listing). |

### `tapecon rename-media`

Rename the loaded tape media.

```
tapecon rename-media [options]
```

The new name is read interactively unless `--quiet` is set.

### `tapecon rename-set`

Rename a backup set on the loaded tape.

```
tapecon rename-set <index> [options]
```

`<index>` is the standard 1-based set index to rename.

### `tapecon docs`

Show conceptual documentation topics: `concepts` | `migration` | `faq`.

```
tapecon docs [<topic>] [options]
```

| Argument / option | Description |
|--------------------|-------------|
| `<topic>` | Topic to display: `concepts` (default) / `migration` / `faq`. |
| `--list`  | List the available topics instead of rendering one. |

### `tapecon demo`

Demonstrate the `tapecon` console UX (logs, progress, prompts). Useful for
smoke-testing.

```
tapecon demo [options]
```

| Option | Description |
|--------|-------------|
| `--fast`   | Run the simulated work in ~1 second instead of ~5 seconds. |
| `--prompt` | Also exercise a Yes/No confirmation prompt. |

## Exit codes

| Code | Name              | Meaning |
|------|-------------------|---------|
| 0    | `Ok`              | All operations succeeded. |
| 1    | `UsageError`      | Bad/missing arguments. |
| 2    | `Cancelled`       | User cancelled or `Ctrl+C`. |
| 3    | `OperationFailed` | Operation completed with file-level failures. |
| 4    | `DriveNotFound`   | Drive could not be opened. |
| 5    | `MediaError`      | Media unloaded, format error, or unrecoverable I/O. |
| 6    | `FatalError`      | Unhandled exception. |
