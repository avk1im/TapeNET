# Restore, validate and verify

Three verbs read a backup set back. They take the same arguments: the set index first, then an optional selection
(see `tapecon docs fcl`).

| Verb | Reads the tape and | Needs the source files |
|---|---|---|
| `restore` | writes the files to a folder | no |
| `validate` | checks every file against its recorded hash | no |
| `verify` | compares every file with the original on disk, byte by byte | yes, in their original place |

With the password, all three work on encrypted sets, and they check the encryption's authentication as well.

## Choosing the set

- **Positive** indexes count up from the **oldest** set: 1 is the first backup ever written.
- **Zero** is the **latest** set, the default; -1 is the one before, -2 two before.

For an incremental set, the chain rolls together: you get the latest version of every file. `--incremental false`
processes exactly the given set (see `tapecon docs incremental-backup`).

## Restoring

    tapecon restore 0 --target D:\Restored --subdirs

- `--target` names the folder; without it, the files go to the current folder.
- `--subdirs` recreates the original folder structure under the target.
- `--existing` decides what happens to files already there; the default keeps both.

## When a file fails

tapecon asks: retry, skip this file, skip all, or abort. `--skip-errors` skips without asking. The run then ends with
exit code 3, and the log names each file that failed.

## Several volumes

A set that spans cartridges asks for each volume it needs, starting with the last (see `tapecon docs multi-volume`).
`--no-multivolume` stops when the current volume is exhausted.
