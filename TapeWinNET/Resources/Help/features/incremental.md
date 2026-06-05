---
id: features.incremental
title: Incremental Backup
kind: feature
keywords: [incremental, changed files, archive attribute, chain]
intents:
  - "incremental backup feature"
  - "back up only changes"
related:
  - concepts.incremental-backup
---

# Incremental Backup

Incremental backups capture **only the files that changed** since the previous
backup, saving time and tape.  TapeWin tracks change state per file and links
incremental sets into an [incremental chain](help://glossary/incremental-chain) so a restore can reconstruct the latest
version of every file.

- Enable it with **Incremental backup** in the
  [New Backup dialog](help://topic/dialog.backup).
- Restore the whole chain with **Include incremental chain** in the
  [Restore dialog](help://topic/dialog.restore).

See [Incremental backup](help://topic/concepts.incremental-backup) for how the
chain works.
