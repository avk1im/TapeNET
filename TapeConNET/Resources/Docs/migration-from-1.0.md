# Migration from 1.x to 2.0

tapecon 2.0 is a ground-up rewrite. It replaces the legacy positional command line with a **verb-based** interface,
and shares its backup and restore engine (TapeLibNET.Services) with the Windows application TapeWinNET. On-tape data
formats and TOC structures are unchanged: media written by 1.x reads back fine in 2.0.

## What changed at the command line

### Verbs replace positional commands

1.x took a single command word followed by free-form arguments. 2.0 offers one **verb per operation**:

| 1.x command | 2.0 verb |
|---|---|
| `tapecon backup ...` | `tapecon backup ...` |
| `tapecon restore ...` | `tapecon restore [<set>] ...` |
| `tapecon validate ...` | `tapecon validate [<set>] ...` |
| `tapecon verify ...` | `tapecon verify [<set>] ...` |
| `tapecon list ...` | `tapecon list [<set>...] ...` |
| `tapecon info` | `tapecon info` |
| `tapecon format ...` | `tapecon format --yes ...` |
| `tapecon eject` | `tapecon eject` |
| `tapecon toc ...` | `tapecon export-toc ...`, `tapecon restore --import-toc ...` (also with `delete-sets`) |
| (renaming via TOC editor) | `tapecon rename-media ...`, `tapecon rename-set ...` |

`tapecon --help` lists every verb; `tapecon <verb> --help` documents its options, with examples.

### Drive selection is unified

| Need to access | 2.0 option |
|---|---|
| Default Win32 drive 0 | (omit drive options) |
| Specific Win32 drive | `--drive N` |
| Existing file-backed virtual drive | `--virtual-open PATH` |
| New file-backed virtual drive | `--virtual-create PATH` |
| Throw-away in-memory drive | `--in-memory` |

Only one drive option may be given per run.

### File selection is consistent across verbs

Every verb that works on files takes the same arguments: positional arguments are classified as **FCL files**,
**inline FCL**, **paths**, or **wildcards**. `--filter` and `--filter-file` give FCL explicitly. This replaces the
per-verb pattern handling of 1.x. See `tapecon docs fcl`.

### Confirmation and quiet modes

- `format` asks before it erases; `--yes` answers for unattended runs.
- `--quiet` suppresses informational output, and prompts take their safe answer.
- `--no-color` disables colors and box drawing.

### Exit codes are explicit

2.0 returns documented exit codes (`tapecon docs concepts`). Scripts should branch on them instead of reading log
output.

## What stayed the same

- On-tape format, hash schemes, incremental chains and TOC layout are unchanged. Tapes written with 1.x load and restore
  in 2.0.
- The set index convention (1 = oldest, 0 = latest, -1 = previous) is the same.
- Hash names (Crc32, XxHash3, and so on) are the same.

## Migrating scripts

- Replace each `tapecon <command>` with the matching `tapecon <verb>`.
- Move source paths to the positional arguments; express filters in FCL, positional or with `--filter`.
- For unattended scripts, add `--quiet`, and `--yes` where a verb asks before erasing.
- Replace output parsing with branching on the **exit code**.
- If you used a custom drive number, switch to `--drive N`.

A typical conversion, 1.x then 2.0:

    tapecon backup C:\MyDocs *.docx /h xx3 /d "Daily"
    tapecon backup C:\MyDocs "Extension equals .docx" --hash XxHash3 -D Daily
