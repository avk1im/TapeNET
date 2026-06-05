---
id: dialog.delete-backup-sets
title: Delete Backup Sets
kind: dialog
host: DeleteBackupSetsWindow
keywords: [delete, remove, backup set, reclaim space, TOC, capacity]
intents:
  - "how do I delete a backup set"
  - "remove the last backup"
  - "free up space on the tape"
  - "delete the newest set"
related:
  - concepts.backup-sets
  - dialog.format-media
ai_excerpt: true
---

# Delete Backup Sets

Removes one or more of the **most recent** [backup sets](help://glossary/backup-set) from the tape's [Table of Contents](help://glossary/toc).
room for new data.

## Delete from set

Choose the oldest set you want to keep up to — every set newer than it
(including it, depending on the selection) is removed.  Because sets are written
sequentially, only contiguous sets from the newest end can be deleted.

## Warning panel

A warning reminds you that deletion is **permanent** for the affected sets.

## Preview

Shows the impact of the deletion before you commit:

- **Media capacity** and **remaining** space.
- **Delete size** — how much data will be removed.
- **Remaining after delete** — projected free space afterwards.

A media-usage bar visualises the before/after state.

> Note: deleting sets updates the TOC but does not physically reclaim the tape
> until you [Format](help://topic/dialog.format-media) the media.

## Deleting

Click **Delete** to remove the selected sets, or **Cancel** to abort.

> Shortcut: [Delete backup sets](help://action/delete-sets).
