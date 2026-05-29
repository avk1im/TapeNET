---
id: dialog.backup
title: New Backup Dialog
kind: reference
host: MainWindow
keywords: [new backup, backup dialog, source files, filter, start backup]
intents:
  - "what does the backup dialog do"
  - "options in the new backup window"
  - "how to add files to a backup"
---

# New Backup Dialog

The **New Backup** dialog lets you define the scope of a new backup set and
start the backup operation.

## Source Files

Add files and folders using **Add Files…** or **Add Folder…**, or drag items
directly into the list.  You can add items from multiple locations.

## File Filter

Apply an [FCL filter](help://topic/concepts.fcl-filters) to include or exclude
specific files within the selected folders.  The filter is saved with the
backup set so you can re-apply it on subsequent backups.

## Backup Type

| Option | Description |
|--------|-------------|
| **Full** | Backs up all selected files; clears the archive attribute. |
| **Incremental** | Backs up only files changed since the last backup. |

## Description

An optional free-text description is stored in the tape catalog and displayed
in the tree view for quick identification.

## Starting the Backup

Click **Start Backup**.  Progress is shown in the main window's progress panel.
You can [abort](help://action/abort-backup) at any time.

**See also:** [Your first backup](help://topic/quickstart.first-backup) ·
[Backup sets](help://topic/concepts.backup-sets)
