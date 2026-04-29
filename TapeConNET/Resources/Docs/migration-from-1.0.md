# tapecon — Migration from 1.x to 2.0

`tapecon 2.0` is a ground-up rewrite that replaces the legacy positional
command line with a **verb-based** System.CommandLine interface and a
shared backup/restore engine (`TapeLibNET.Services`) used by both
`tapecon` and the WPF GUI **TapeWinNET**. On-tape data formats and TOC
structures are unchanged — media written by 1.x reads back fine in 2.0.

## What changed at the command line

### Verbs replace positional commands

1.x took a single command word followed by free-form arguments. 2.0
exposes one **verb per operation**:

| 1.x command | 2.0 verb |
|-------------|----------|
| `tapecon backup …`   | `tapecon backup …` |
| `tapecon restore …`  | `tapecon restore [<set>] …` |
| `tapecon validate …` | `tapecon validate [<set>] …` |
| `tapecon verify …`   | `tapecon verify [<set>] …` |
| `tapecon list …`     | `tapecon list [<set>…] …` |
| `tapecon info`       | `tapecon info`     |
| `tapecon format …`   | `tapecon format --yes …` |
| `tapecon eject`      | `tapecon eject`    |
| `tapecon toc …`      | `tapecon toc export\|import …` |
| (renaming via TOC editor) | `tapecon rename-media …` / `tapecon rename-set …` |

Discoverability: `tapecon --help` lists every verb;
`tapecon <verb> --help` documents its options.

### Drive selection is unified

| Need to access | 2.0 option |
|----------------|------------|
| Default Win32 drive 0 | (omit drive options) |
| Specific Win32 drive  | `--drive N` |
| File-backed virtual drive | `--virtual <path> [--initiator <path>]` |
| Throw-away in-memory drive | `--in-memory` |

Only one drive option may be supplied per invocation.

### File selection is consistent across verbs

Every read/write verb accepts the same shape: positional arguments are
auto-classified as **FCL files**, **inline FCL**, **paths**, or
**wildcards**. Explicit forms (`--filter`, `--filter-file`) are available
when ambiguity is undesirable. This replaces the per-verb pattern
machinery from 1.x.

### Confirmation and quiet modes

- Destructive verbs (`format`) require `--yes` in non-interactive contexts;
  prompts auto-cancel when `--quiet` is set.
- `--quiet` suppresses informational output and auto-confirms safe prompts.
- `--no-color` disables ANSI styling.

### Exit codes are explicit

2.0 returns documented exit codes (see `tapecon docs concepts`). Scripts
should branch on these instead of grepping log output.

## What stayed the same

- On-tape format, hash schemes, incremental-chain semantics, and TOC
  layout are unchanged. Tapes written with 1.x mount and restore in 2.0.
- Set indexing convention (`1` = oldest, `0` = latest, `-1` = previous) is
  preserved.
- Hash names (`Crc32`, `XxHash3`, …) are the same.

## Migrating scripts

1. Replace each `tapecon <command>` with the matching `tapecon <verb>`.
2. Move target paths to the **positional** `files` argument; move
   FCL/wildcards into `--filter` (or just leave them positional).
3. For unattended scripts, add `--quiet` and `--yes` (where applicable).
4. Replace ad-hoc output parsing with branching on the **exit code**.
5. If you used a custom drive number, switch to `--drive N`.

A typical 1.x → 2.0 conversion:

```
# 1.x
tapecon backup C:\MyDocs *.docx /h xx3 /d "Daily"

# 2.0
tapecon backup C:\MyDocs --filter "extension == \".docx\"" --hash XxHash3 --description "Daily"
```
