---
id: dialog.restore
title: Restore / Validate / Verify
kind: dialog
host: RestoreWindow
keywords: [restore, validate, verify, recover, extract, destination, target, incremental]
intents:
  - "how do I restore files"
  - "get my files back"
  - "validate a backup"
  - "verify a backup"
  - "restore to a different folder"
  - "restore incremental chain"
related:
  - concepts.restore-validate-verify
  - concepts.incremental-backup
  - concepts.backup-sets
ai_excerpt: true
---

# Restore / Validate / Verify

This dialog lets you restore files from tape, or check the integrity of a
backup set without writing anything to disk.

## Backup sets list (left panel)

The table lists every backup set that is available for the selected operation.
Use the checkboxes to choose which sets to include.

- **Select / deselect all** — tick the header checkbox to toggle all rows.
- **Off-volume sets** (dimmed) — sets that reside on a different tape volume
  than the one currently loaded.  They can still be selected; TapeWin will
  prompt you to load the required volume when the operation reaches them.
- **Remove Unchecked** — removes unchecked sets from the list entirely if you
  want a shorter working set.

## Operation

| Choice | What it does |
|--------|-------------|
| **Restore** | Reads files from tape and writes them to disk. |
| **Validate** | Reads tape data and compares checksums against the stored catalog — no files are written. |
| **Verify** | Reads files already restored to disk and compares them byte-for-byte to tape — confirms a restore succeeded. |

See [Restore, Validate, Verify concepts](help://topic/concepts.restore-validate-verify) for
a detailed comparison.

## Restore to

*Available only in Restore mode.*

- **Original location** — files are written back to the path they were backed
  up from.  Existing files are handled according to the **Handle existing**
  setting below.
- **Target folder** — all files are restored under the folder you specify.
  Click **…** to browse.  Enable **Restore with subfolders** to recreate the
  original sub-directory tree inside the target folder.

## Options

| Option | Effect |
|--------|--------|
| **Include incremental chain** | Automatically processes the full incremental chain leading up to the selected set(s), so you get the most up-to-date version of every file. |
| **This volume only** | Confines the operation to sets on the currently loaded tape.  Useful for a quick partial restore when you do not have all volumes to hand. |
| **Uncheck processed files** | After a successful restore / validate / verify, unchecks each file so you can see at a glance what still needs attention. |
| **Handle existing** | Controls what happens when a file already exists at the destination: *Ask*, *Overwrite*, *Skip*, or *Rename*. |
| **Skip all errors** | Silently ignores file-level errors without prompting.  Recommended only for unattended batch operations. |

## Preview

Shows a running count and aggregate size of the files that will be processed,
based on the current selection and filters.

## Starting the operation

Click the action button (**Restore**, **Validate**, or **Verify**) to begin.
Progress is reported in the main window log pane.  You can abort at any time
using the **Abort** button that appears during the operation.
