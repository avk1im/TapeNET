# tapecon — Concepts

`tapecon` is the .NET 8 command-line tape backup utility built on top of the
`TapeLibNET` engine. It shares its core backup/restore engine
(`TapeLibNET.Services.TapeServiceBase`) with the WPF GUI application
**TapeWinNET**, so the two tools are functionally equivalent.

## Drives

`tapecon` can talk to:

- **Physical Win32 tape drives** (`--drive N`, default `0`).
- **File-backed virtual drives** (`--virtual PATH` plus optional
  `--initiator PATH`).
- **In-memory virtual drives** (`--in-memory`), useful for ad-hoc tests.

When no drive option is supplied, `tapecon` auto-opens **Win32 drive 0**.

A virtual drive can be sized at format time with `--capacity` and
`--init-capacity` (initiator partition for the TOC).

## Media and the table of contents (TOC)

A formatted tape (real or virtual) is the host of one or more
**backup sets**. The on-tape **TOC** indexes every set: its description,
file list, hash flags, and incremental-chain pointer.

Set indexes use a dual convention:

- **Positive** values count up from the **oldest** set: `1`, `2`, …
- **Zero** is the **latest** set; `-1` is the second-latest, and so on.

Most CLI verbs accept this same convention (`restore 1`, `validate 0`,
`rename-set -1 …`).

## Backup sets and incremental chains

`tapecon backup` writes a single set, then updates the TOC. By default each
new backup **replaces** all sets on the media; `--append` adds a new set
after the existing ones, and `--append-after N` replaces everything after
set `N`.

`--incremental` makes the new set a **delta** referencing the previous full
set in the chain: only files newer than their previously-backed-up copy are
written. Restoring an incremental set with the default `--incremental`
behaviour rolls the chain back together so you receive the latest version
of every file.

## File selection — paths, wildcards, and FCL

Every verb that selects files (backup, restore, validate, verify, list)
accepts the same shapes for its positional arguments and `--filter` /
`--filter-file` options:

1. A **path to an existing `.fcl` file** is loaded as an
   FCL (File Conditions Language) expression.
2. A string starting with an **FCL keyword** (`name`, `path`, `size`, `date`,
   `attr`, `extension`, `(` …) is treated as **inline FCL**.
3. An **existing file or folder path** is taken literally (recurse with
   `--subdirs`).
4. Otherwise the argument is treated as a **DOS wildcard pattern**
   (e.g. `*.docx`).

`--filter` (inline FCL) and `--filter-file` (load from disk) are explicit
forms; bare positional arguments are auto-classified the same way.

For backup, recognized **paths/patterns** become the **sources** to scan,
while **FCL fragments** become the **selection filter** applied to the scan
results. This lets you write things like:

```
tapecon backup C:\MyDocs --hash xxhash3 --description "Daily docs"
tapecon backup C:\MyDocs name matches "*.docx" or extension == ".pdf"
tapecon backup --filter-file .\nightly.fcl --incremental
```

## Hashes

`--hash` selects a per-file integrity hash recorded in the TOC. Choices:
`None`, `Crc32` (default), `Crc64`, `XxHash32`, `XxHash3`, `XxHash64`,
`XxHash128`. `validate` re-reads the tape and recomputes the hash; `verify`
compares the on-tape file with the on-disk file byte-by-byte.

## Exit codes

Every verb returns one of:

| Code | Name | Meaning |
|------|------|---------|
| 0    | `Ok`            | All operations succeeded. |
| 1    | `UsageError`    | Bad/missing arguments — printed to stderr. |
| 2    | `Cancelled`     | User cancelled or `Ctrl+C`. |
| 3    | `OperationFailed` | Operation completed with file-level failures. |
| 4    | `DriveNotFound` | Drive could not be opened. |
| 5    | `MediaError`    | Media unloaded, format error, or unrecoverable I/O. |
| 6    | `FatalError`    | Unhandled exception. |

## Cancellation

`Ctrl+C` is intercepted. The first press requests **cooperative cancellation**
of the current operation (the file-in-flight finishes, the agent unwinds,
the TOC is preserved); a second press performs a hard abort.

## See also

- `tapecon docs migration` — moving from `tapecon 1.x` to 2.0.
- `tapecon docs faq` — common questions and troubleshooting.
- `tapecon <verb> --help` — verb-specific options.
- `docs/cli-reference.md` — auto-generated full reference.
- `docs/TapeConNET-2.0-Architecture.md` — internal architecture.
