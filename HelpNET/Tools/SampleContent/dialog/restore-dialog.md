---
id: dialog.restore
title: Restore dialog
kind: reference
keywords: [restore dialog, restore window, destination, restore options]
intents:
  - "restore dialog options"
  - "where can I choose the restore destination"
related:
  - quickstart.restore
  - concepts.restore-validate-verify
---
# Restore dialog

The Restore dialog opens when you click **Restore** with a backup set selected.

## Backup set information

The top panel shows the selected backup set: tape label, set index, date, and
total file count.

## Destination

Choose where restored files will be written:

- **Original location** — restores each file to its original path on disk.
  Requires that the original drive letter is available.
- **Alternate folder** — writes all files under a new root folder, preserving
  their relative paths.

## Filter

The filter pane lets you enter an FCL expression to restore only a subset of
files from the set.

## Starting the restore

Click **Start restore** to begin. Each restored file appears in the log as it
is written. Click **Abort** to cancel; files already written are kept.
