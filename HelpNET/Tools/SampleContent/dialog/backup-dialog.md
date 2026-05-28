---
id: dialog.backup
title: Backup dialog
kind: reference
keywords: [backup dialog, backup window, backup options]
intents:
  - "backup dialog options"
  - "what does the backup window do"
related:
  - quickstart.backup
  - concepts.fcl-filters
---
# Backup dialog

The Backup dialog opens when you click **Backup** in the toolbar. It lets you
configure the backup operation before starting.

## Source

The **Source** panel shows the folder or drive you selected in the tree. You can
add additional folders by clicking **Add folder…**.

## Filter

The **Filter** pane lets you enter an FCL expression to include or exclude
specific files. The **Filtered file count** shows how many files match the
current filter.

## Options

| Option | Description |
|--------|-------------|
| Verify after backup | Re-reads every written file and compares it to the original. |
| Append to tape | Adds this backup as a new set rather than overwriting the tape. |
| Incremental | Backs up only files whose archive bit is set. |

## Starting the backup

Click **Start backup** to begin. Progress is shown in the main log pane. You
can click **Abort** at any time to cancel; the partial backup set is discarded.
