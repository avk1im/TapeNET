---
id: walkthrough.backup-dialog
title: New Backup dialog walkthrough
kind: walkthrough
host: BackupWindow
description: Add source files, set options, and start the backup job.
keywords: [backup, sources, options, media description, start backup]
related:
  - dialog.backup
  - concepts.backup-sets
  - concepts.fcl-filters
---

## [Sources list] Add your source files and folders

Click **Add Folder…** or **Add Files…** in the toolbar above the list to add what you want
to back up.  Each entry shows the path and an estimated file count.

Remove items you don't need with the **×** toolbar button.

## [File filter pane] Optionally filter which files are backed up

The [file filter pane](help://topic/ui.file-filter-pane) lets you narrow the backup to
specific file types, sizes, or dates using [FCL expressions](help://topic/concepts.fcl-filters).

Leave it blank to back up everything in the sources list.

## [Options] Choose backup options

**Incremental** — only backs up files changed since the last backup in the chain.
Leave it off for a full backup.

**Append / Overwrite** — *Append* adds a new backup set after existing ones; *Overwrite*
erases the tape first.

## [Media] Set a media description (optional)

Enter a short label for the tape — useful when you have multiple tapes and want to identify
this one later (e.g. `Weekly-2025-01`).

## [Start Backup] Start the backup

Click **Start Backup** when you are ready.  The progress panel will appear below —
you can watch file-by-file progress and **Abort** at any time if needed.
