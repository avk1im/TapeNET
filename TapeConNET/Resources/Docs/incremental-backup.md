# Incremental backups

A **full** backup writes every file you select. An **incremental** backup writes only the files that are new, or
changed since the chain last saved them, and so takes a fraction of the time and the tape.

    tapecon backup D:\Data --subdirs
    tapecon backup D:\Data --subdirs --incremental

## How tapecon decides what changed

By **time**. tapecon compares each file's name and modification time with what the TOC records for the chain. A file
the chain already holds in this version is skipped.

The Windows **archive attribute** plays no part. To select files by it, say so in FCL:
`--filter "Attributes have Archive"` (see `tapecon docs fcl`).

## Chains

A full set and the incremental sets after it form a **chain**. An incremental set is always added after the last set
on the tape, so it needs a set to build on: `--incremental` cannot be combined with `--overwrite-media` or
`--virtual-create`. To start a new chain, run a full backup.

An incremental on an encrypted set is encrypted with the same key, and needs that set's password (see
`tapecon docs encryption`).

## Restoring from a chain

Restoring an incremental set rolls the chain together: you receive the latest version of every file, gathered from
the sets it needs.

    tapecon restore 0 --target D:\Restored --subdirs

To restore exactly one set, with nothing rolled in from earlier sets:

    tapecon restore 3 --incremental false --target D:\Restored

`list` works the same way: it expands the chain unless you give `--no-incremental`.
