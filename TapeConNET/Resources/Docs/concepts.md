# Concepts

tapecon is the command-line tape backup utility built on the TapeLibNET engine. It shares that engine with the Windows
application TapeWinNET: anything one writes, the other reads, encrypted sets included. This page gives the overview;
each section points to the topic with the details. New to tapecon? Start with `tapecon docs getting-started`.

## Drives

tapecon works with **physical** Win32 tape drives (`--drive N`), **file-backed** virtual drives (`--virtual-open PATH`,
`--virtual-create PATH`), and **in-memory** virtual drives (`--in-memory`). Without a drive option, it opens physical
drive 0. See `tapecon docs virtual-drives`.

## Media, backup sets and the TOC

A formatted cartridge holds one or more **backup sets**, one per backup. The **table of contents (TOC)** indexes them:
each set's description and settings, and every file with its position and hash. It is written twice on every volume.
See `tapecon docs toc`.

Every cartridge starts with a **media header** naming it: its media ID, volume number and description. Each set starts
with a **set header**. tapecon checks both before it reads or writes. A wrong cartridge, or a set that is not the one
the TOC expects, is reported, and a destructive write is refused (exit code 5) rather than risk the wrong data.

Set indexes count both ways:

- **Positive** values count up from the **oldest** set: 1, 2, and so on.
- **Zero** is the **latest** set; -1 the one before, and so on.

## Where a backup goes

- By default, a new set goes **after the last set** on the media (`--append`).
- `--append-after N` puts it after set N, overwriting the sets behind N.
- `--overwrite-media` starts the media over: every set, the TOC and the media header are replaced, and
  `--media-description` names the new media.

Appending needs TapeNET media: format a new cartridge first (`tapecon format`), or use `--overwrite-media`.
`--incremental` writes only the files changed since the chain last saved them (`tapecon docs incremental-backup`).

## Selecting files

Paths, wildcard patterns and FCL, the File Conditions Language, select files on every verb that works on them. See
`tapecon docs fcl`.

## Reading back

`restore` writes the files of a set to a folder, `validate` checks them against their hashes, and `verify` compares them
with the originals on disk. See `tapecon docs restore-validate-verify`.

## Hashes and compression

`--hash` selects the per-file integrity hash: None, Crc32 (the default), Crc64, XxHash32, XxHash3, XxHash64, XxHash128.

`--compression` selects off or none, hardware (or drive), low, medium (the default), high, or a ZSTD level 1-19.

## Larger and safer

- **Encryption:** `--encrypt` protects a set's files and its TOC entry. See `tapecon docs encryption`.
- **Several cartridges:** a backup that outgrows one continues on the next. See `tapecon docs multi-volume`.

## Exit codes

| Code | Name | Meaning |
|---|---|---|
| 0 | Ok | Everything succeeded. |
| 1 | FatalError | An unexpected error. |
| 2 | UsageError | Bad or missing arguments. |
| 3 | OperationFailed | The operation failed, or completed with failed files. |
| 4 | Cancelled | Cancelled by the user (Ctrl+C, or a declined prompt). |
| 5 | WriteRefused | A destructive write was refused: the set on tape is not the expected one. The tape is unchanged. |
| 6 | PasswordRequired | Encrypted sets stayed locked (no or wrong password), or an encrypted backup had no password. |

## Cancellation

Ctrl+C requests **cooperative cancellation**: the file in flight finishes, and the TOC is preserved. A second press
aborts hard.

## See also

- `tapecon docs` lists every topic.
- `tapecon docs faq` answers common questions.
- `tapecon <verb> --help` shows every option of a verb, with examples.
