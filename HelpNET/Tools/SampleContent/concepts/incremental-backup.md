---
id: concepts.incremental-backup
title: Incremental backups
kind: concept
keywords: [incremental, differential, archive bit, changed files]
intents:
  - "what is an incremental backup"
  - "how does incremental backup work"
  - "only back up changed files"
related:
  - concepts.backup-sets
  - quickstart.backup
---
# Incremental backups

An **incremental backup** copies only the files that have changed since the last
backup. TapeWinNET uses the Windows archive attribute to track which files need
to be backed up.

## How it works

1. After a full backup, Windows clears the archive bit on every copied file.
2. When a file is modified, Windows sets its archive bit again.
3. The next incremental backup copies only files whose archive bit is set.

## Restoring from incremental backups

To fully restore from a series of incremental backups, you need:

1. The most recent **full backup** set.
2. Every **incremental** set taken after that full backup, in order.

TapeWinNET's Restore dialog can automatically sequence multi-set restores when
all sets reside on the same tape.
