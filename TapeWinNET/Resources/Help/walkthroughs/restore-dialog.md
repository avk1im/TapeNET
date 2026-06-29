---
id: walkthrough.restore-dialog
title: Restore dialog walkthrough
kind: walkthrough
host: RestoreWindow
description: Choose destination, set options, and start restoring files.
keywords: [restore, destination, options, start restore]
related:
  - dialog.restore
  - concepts.restore-validate-verify
---

## [Backup sets list] Confirm the backup sets to restore

The list at the top shows the sets you selected in the main window.  You can tick or
untick individual sets here if you change your mind.

Scroll through the **[file filter pane](help://topic/ui.file-filter-pane)** on the right to
preview which files will be restored.

## [Restore to] Choose a destination folder

Select where restored files should go:

- **Original location** — writes each file back to the same path it was backed up from.
- **Specific folder** — click **Browse…** and choose a writable folder.

## [Options] Set restore options

**Overwrite existing files** — choose whether to replace newer files on disk with older
versions from the tape.

**Verify after restore** — re-reads the restored files and confirms their checksums.
Recommended for important data.

## [Start button] Start the restore

Click **Start** to begin restoring.  Watch the progress panel — you can **Abort** at any
time.  A green ✓ in the log indicates success.
