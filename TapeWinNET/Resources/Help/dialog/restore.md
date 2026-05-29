---
id: dialog.restore
title: Restore Dialog
kind: reference
host: RestoreWindow
keywords: [restore dialog, destination, overwrite, file selection, restore files]
intents:
  - "what does the restore dialog do"
  - "options in the restore window"
  - "where to restore files"
  - "overwrite existing files"
---

# Restore Dialog

The **Restore** dialog lets you choose which files to extract from a backup
set and where to put them.

## File Selection

All files in the backup set are listed with checkboxes.  Tick or untick
individual files, or use the header checkbox to select/deselect all.

Apply an [FCL filter](help://topic/concepts.fcl-filters) to quickly narrow
the list by name, size, or date.

## Destination

| Option | Description |
|--------|-------------|
| **Original location** | Restore each file to its original path on disk. |
| **Alternate folder** | Place all files under a single chosen folder, preserving sub-paths. |

## Overwrite Policy

| Option | Behaviour |
|--------|-----------|
| **Skip existing** | Leave files that already exist on disk untouched. |
| **Overwrite** | Replace existing files without prompting. |
| **Rename** | Append a numeric suffix to avoid conflicts. |

## Starting the Restore

Click **Start Restore**.  Progress is shown in the dialog's progress panel.

**See also:** [Restoring files](help://topic/quickstart.restore-files) ·
[Validate and Verify](help://topic/concepts.restore-validate-verify)
