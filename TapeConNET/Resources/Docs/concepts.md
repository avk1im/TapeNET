# tapecon — Concepts

tapecon is the .NET 8 command-line tape backup utility built on the TapeLibNET engine. It shares its backup and restore
engine (TapeLibNET.Services) with the Windows application TapeWinNET: anything one writes, the other reads.

## Drives

tapecon can talk to:

- **Physical Win32 tape drives** — `--drive N`.
- **File-backed virtual drives** — `--virtual PATH`, plus `--initiator PATH` for a separate TOC partition.
- **In-memory virtual drives** — `--in-memory`, for ad-hoc tests.

Without a drive option, tapecon opens **Win32 drive 0**. Size new virtual media with `--capacity` and `--init-capacity`.

## Media, backup sets and the table of contents (TOC)

A formatted cartridge holds one or more **backup sets**. The **table of contents (TOC)** indexes them: each set's
description, settings, and every file with its position on tape and its hash.

Every cartridge written by tapecon 2.x starts with a **media header** naming the cartridge (its media ID, volume number
and name), and each set starts with a **set header**. tapecon checks them before it reads or writes: a wrong cartridge,
or a set that is not the one the TOC expects, is reported — and a destructive write is refused (exit code 5) rather than
risk the wrong data.

Set indexes use a dual convention:

- **Positive** values count up from the **oldest** set: 1, 2, …
- **Zero** is the **latest** set; -1 the one before, and so on.

## Where the TOC lives

The TOC is written **twice** on every volume, so one damaged copy never loses it. Where depends on the drive:

- **TOC partition** (`--initiator` for virtual drives; partition-capable drives): the TOC has its own partition, and
  content may fill its partition to the very end.
- **TOC in set** (all other drives): the TOC follows the content on the same partition. A backup therefore keeps room
  free for it — the **TOC reserve**.

### The TOC reserve and the TOC partition budget

tapecon sizes the TOC's room **as the backup runs**: every file written adds its TOC entry, and the room grows with it.
No fixed guess, so a backup of millions of small files cannot outgrow it, and a small backup does not waste space.

- **TOC in set:** content stops early enough for both TOC copies to fit. At least 16 MiB is kept on LTO drives, 4 MiB
  on others (less on very small media).
- **TOC partition:** its size is fixed at format. When the next files' TOC entries would not fit it twice, the volume
  ends early — "Volume #n ends early: its TOC partition is full" — and the backup continues on the next volume. Every
  volume of a series holds the **whole series'** TOC, so continue on a cartridge formatted with a **larger** TOC
  partition: one of the same size cannot take more files of that series, and the backup then ends with advice.

`tapecon info` shows the current **TOC Reserve** and the space left for content (**Writable**).

### When the TOC cannot be found where it should be

Loading a TOC tries, in order: its usual place, the second copy, the position the series TOC records, a short search
from a nearby landmark — and, only after asking, a **search of the whole medium**. Each deviation is reported, and the
advice says what to do (for example: re-save the TOC so it is found directly again). Unattended runs search without
asking, since a search only reads.

## Multi-volume backups

When a cartridge fills up, the backup asks for the next volume and continues there; the set spans volumes. Each volume
carries its own media header and both TOC copies; the **last** volume's TOC describes the whole series.

- **Restore** opens the last volume, then asks for each earlier volume it needs. A volume is identified by its media
  header, its own TOC, or the first block of its set — and, as a last resort, by searching it whole. A volume nothing
  identifies is rejected: insert it again (or another copy), skip it (its files are reported failed), or end.
- **Ending a series early** (declining the next volume) leaves every volume written so far restorable. The backup then
  reports as **failed**, naming how many files were not backed up.
- `--no-multivolume` stops at the end of the first cartridge instead.

## Backup sets and incremental chains

`tapecon backup` writes one set, then the TOC. By default a new backup **replaces** all sets; `--append` adds a set
after them, `--append-after N` replaces everything after set N.

`--incremental` writes only files changed since the previous set. Restoring an incremental set (default) rolls the
chain together, so you receive the latest version of every file.

## Encryption

`tapecon backup --encrypt` encrypts the new set with AES-256-GCM — its files **and** its TOC entry (names, sizes, times,
file count). Only what navigation needs stays visible: that the set exists, where it is, and how large it is.

- **The password** comes from `--password-file FILE` (UTF-8, one trailing line break ignored) or
  `--password-env VARIABLE`; otherwise it is asked, masked. A password on the command line (`--password`) is refused:
  it would be visible in the shell history and the process list.
- **Locked sets.** Without the password, an encrypted set is listed as `[Locked]` with its size — nothing else. It can
  still be deleted or overwritten, and every other set stays fully usable. Restore, validate, verify and the file list
  ask for the password; a set that stays locked is skipped (exit code 6).
- **Incremental chains** stay in one encryption: an incremental on an encrypted set is encrypted with the same key, and
  needs that set's password. A plain incremental on an encrypted set (or the reverse) is refused.
- **Changing the password:** `tapecon rename-set N --change-password` (asked twice), or `--new-password-file` /
  `--new-password-env`. Older TOC exports and other volumes' TOC copies keep the old password.
- **Software encryption rules out hardware compression** — encrypted data does not compress. Use software compression
  (`--compression low|medium|high`). Drive encryption (`--encryption hardware` or `drive`) is not supported yet.
- **A lost password cannot be recovered.** And since an encrypted set's files are only findable through the TOC,
  keep an exported TOC (`tapecon toc export`) of important cartridges.

## File selection — paths, wildcards and FCL

Every verb that selects files (backup, restore, validate, verify, list) accepts the same positional arguments and
`--filter` / `--filter-file` options:

- A **path to an existing .fcl file** is loaded as an FCL (File Conditions Language) expression.
- A string starting with an **FCL keyword** (name, path, size, date, attr, extension, `(` …) is **inline FCL**.
- An **existing file or folder** is taken literally (recurse with `--subdirs`).
- Anything else is a **DOS wildcard pattern** (e.g. `*.docx`).

For backup, paths and patterns are the **sources**; FCL fragments are the **selection filter**:

    tapecon backup C:\MyDocs --hash xxhash3 --description "Daily docs"
    tapecon backup C:\MyDocs name matches "*.docx" or extension == ".pdf"
    tapecon backup --filter-file .\nightly.fcl --incremental

## Hashes and compression

`--hash` selects the per-file integrity hash: None, Crc32 (default), Crc64, XxHash32, XxHash3, XxHash64, XxHash128.
`validate` re-reads the tape and checks it; `verify` compares the tape with the files on disk, byte by byte.

`--compression` selects off/none, hardware (or drive), low, medium (default), high, or a ZSTD level 1-19.

## Scan Media and TOC recovery

`tapecon scan-media` reads the cartridge — every partition — from start to end **without relying on its TOC** and
reports what it identifies: the media header, backup sets, TOC copies, damaged and unidentified data. It is read-only,
and needs no password: encrypted sets are counted and named "(encrypted)", never revealed.

By default it also recovers every TOC copy it finds; `--no-recover-toc` turns that off, `--export DIR` saves the map
as a `.tapescan` file, `--json` prints it.

`tapecon recover-toc --map FILE --fragment N` recovers the TOC from one copy in a saved map; `--save FILE` writes it to a
file, `--adopt` uses it for the session (like `toc import`). If the loaded cartridge is not the scanned one, recovery
is refused.

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

Ctrl+C requests **cooperative cancellation**: the file in flight finishes, the TOC is preserved. A second press aborts
hard.

## See also

- `tapecon docs faq` — common questions and troubleshooting.
- `tapecon docs migration` — moving from tapecon 1.x.
- `tapecon <verb> --help` — every option of a verb.
