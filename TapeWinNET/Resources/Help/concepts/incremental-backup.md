---
id: concepts.incremental-backup
title: Incremental Backup
kind: concept
keywords: [incremental, differential, archive bit, changed files, full backup]
intents:
  - "what is incremental backup"
  - "back up only changed files"
  - "how does incremental work"
---

# Incremental Backup

An [incremental backup](help://glossary/incremental-backup) records only files that have changed since the
previous backup, making it faster and using less tape than a full backup.

## How It Works

TapeWin uses the **archive attribute** (set by Windows whenever a file is
created or modified) to identify changed files:

1. A full backup writes all files and clears the archive attribute.
2. Each subsequent incremental backup writes only files where the archive
   attribute is set, then clears it again.

## Restoring from Incrementals

To restore to the most recent state you may need to apply multiple sets:
the last full backup **plus** every incremental set taken after it, in order —
this sequence is called an [incremental chain](help://glossary/incremental-chain).
TapeWin's Restore dialog handles this automatically when you select a set.

## Full vs Incremental in TapeWin

| Option | Writes | Archive attribute |
|--------|--------|------------------|
| Full backup | All selected files | Cleared after backup |
| Incremental | Changed files only | Cleared after backup |

**See also:** [Backup sets](help://topic/concepts.backup-sets) ·
[FCL filters](help://topic/concepts.fcl-filters)
