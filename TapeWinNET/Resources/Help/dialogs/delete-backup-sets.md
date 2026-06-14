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

Removes one or more of the **most recent** [backup sets](help://glossary/backup-set) from the tape's [TOC](help://glossary/toc)
(Table of Content) to create room for new data.

## Delete from set

Choose the oldest set you want to delete — the selected set **and every set newer than it**
is removed.  Because sets are written sequentially, only contiguous sets from the newest end can be deleted.

## Warning panel

A warning reminds you that deletion is **permanent** for the affected sets.

## Preview

Shows the impact of the deletion before you commit:

- **Media capacity** and **remaining** space.
- **Delete size** — how much data will be removed.
- **Remaining after delete** — projected free space afterwards.

A media-usage bar visualises the before/after state.

> Note: deleting sets does not physically overwrite the tape
> until you [Format](help://topic/dialog.format-media) the media.

## Deleting

Click **Delete** to remove the selected sets, or **Cancel** to abort.

> Shortcut: [Delete backup sets](help://action/delete-sets).

## Controls

**Delete from set** — Selects which portion of the tape to erase: the newest set only, all sets from a chosen set onwards, or all sets on the tape.
**Preview** — Live summary of the capacity freed and remaining after the deletion, plus the tape usage bar showing the affected range highlighted in red.
**Delete button** — Permanently removes the selected sets from the tape. This operation cannot be undone.
