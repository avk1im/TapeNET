---
id: cli.backup-command
title: tapecon backup command
kind: reference
keywords: [tapecon, backup, CLI, flags, filter, incremental]
intents:
  - "tapecon backup options"
  - "how to backup from command line"
  - "automate tape backup with tapecon"
related:
  - cli.overview
  - cli.restore-command
  - concepts.fcl-filters
---
# tapecon backup command

Writes a backup set to the tape currently in the default tape drive.

## Syntax

```
tapecon backup --source <path> [options]
```

## Options

| Flag | Description |
|------|-------------|
| `--source <path>` | Root folder to back up (required). |
| `--filter <fcl>` | FCL expression to filter files. |
| `--incremental` | Back up only files whose archive bit is set. |
| `--verify` | Verify each written file after the backup completes. |
| `--label <text>` | Tape label to assign (used if tape is blank). |
| `--drive <n>` | Tape drive index (default 0). |
| `--no-progress` | Suppress per-file progress output. |

## Example

```powershell
tapecon backup `
  --source "D:\Projects" `
  --filter 'not (name ends-with ".tmp" or name ends-with ".obj")' `
  --incremental `
  --verify
```

Exit code 0 = success, 1 = partial failure (see log), 2 = fatal error.
