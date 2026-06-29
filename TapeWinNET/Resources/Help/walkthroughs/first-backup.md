---
id: walkthrough.first-backup
title: Your first backup
kind: walkthrough
host: MainWindow
description: Load a tape, pick source files in the main window, then start a new backup.
keywords: [backup, first backup, getting started, new backup]
intents:
  - "how do I make my first backup"
  - "guide me through a backup"
  - "walk me through creating a backup"
related:
  - quickstart.first-backup
  - concepts.backup-sets
  - dialog.backup
---

## [Tree view] Check the drive and tape

Expand the **tree view** on the left to see your tape drives.
Click a drive node to select it, then expand it to reveal the loaded tape.

If no drive appears, make sure your tape device is connected and powered on.

## [Content table] Confirm the tape is ready

The centre pane shows the **tape properties**.
A tape ready for a new backup should show either blank media or existing backup sets.

> **Tip:** If the tape has important data you want to keep, choose *Append* mode in the
> backup dialog; otherwise *Overwrite* will erase existing sets.

## [action:new-backup] Open the New Backup dialog

When you are happy with the tape, open the **New Backup** dialog:

- Click **[Open New Backup…](help://action/new-backup)** right here, or
- Use the **Backup → New Backup…** menu, or
- Click the toolbar button with the tape-and-plus icon.

The dialog has its own **Guide Me** tour that walks you through adding files and starting the job.
