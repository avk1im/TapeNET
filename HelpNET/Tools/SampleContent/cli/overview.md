---
id: cli.overview
title: TapeConNET command-line reference
kind: reference
keywords: [CLI, command line, tapecon, console, arguments, flags]
intents:
  - "how do I use tapecon"
  - "command line backup"
  - "CLI tape backup"
  - "tapecon flags"
related:
  - cli.backup-command
  - cli.restore-command
---
# TapeConNET command-line reference

**TapeConNET** (`tapecon.exe`) is the command-line companion to TapeWinNET. It
exposes the same backup, restore, and validate operations as the GUI, driven by
flags rather than a graphical interface.

## General usage

```
tapecon <command> [options]
```

Available commands:

| Command | Description |
|---------|-------------|
| `backup` | Write a backup set to tape. |
| `restore` | Restore files from a backup set. |
| `validate` | Verify tape readability without writing files. |
| `list` | List all backup sets on a tape. |
| `erase` | Erase a tape (full or quick). |

Run `tapecon <command> --help` for per-command options.
