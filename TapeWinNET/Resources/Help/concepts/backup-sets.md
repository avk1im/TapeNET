---
id: concepts.backup-sets
title: Backup Sets
kind: concept
keywords: [backup set, catalog, TOC, tape, incremental, set index]
intents:
  - "what is a backup set"
  - "how are backups organised on tape"
  - "what does the set index mean"
---

# Backup Sets

A [backup set](help://glossary/backup-set) is a single snapshot of the files you chose to back up at a
given point in time.  Each set is written sequentially to tape and recorded in
the tape's [Table of Contents (TOC)](help://glossary/toc).

## Set Indexes

Each backup set has two index numbers shown in the tree:

| Format | Meaning |
|--------|---------|
| `#1`, `#2`, … | Standard index — counts **up** from the oldest set (1-based). |
| `0`, `-1`, `-2`, … | Alternate index — counts **down** from the newest set (0 = newest). |

Use the alternate index to refer to sets relative to *now* (e.g. `-1` is
always yesterday's backup regardless of how many sets exist).

## Incremental Backups

TapeWin supports [incremental backups](help://topic/concepts.incremental-backup):
only files changed since the last backup are included.  Multiple incremental
sets together form an [incremental chain](help://glossary/incremental-chain) that reconstructs the full state.

## Deleting Sets

Use **Tape → Delete Backup Sets…** to remove one or more sets from the TOC.
The tape space is not immediately reclaimed — a **Format** operation is needed
to reuse it.
