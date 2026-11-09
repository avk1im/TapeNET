---
id: concepts.backup-sets
title: Backup Sets
kind: concept
keywords: [backup set, catalog, TOC, tape, incremental, set index, encrypted, locked, icons]
intents:
  - "what is a backup set"
  - "how are backups organised on tape"
  - "what does the set index mean"
  - "what do the set icons mean"
related:
  - concepts.incremental-backup
  - concepts.encryption
  - dialog.delete-backup-sets
---


# Backup Sets

A [backup set](help://glossary/backup-set) is a single snapshot of the files you chose to back up at a given point in
time. Each set is written sequentially to tape and recorded in the tape's [Table of Contents (TOC)](help://glossary/toc).

## Set Indexes

Each backup set has two index numbers shown in the tree:

| Format | Meaning |
|---|---|
| #1, #2, … | Standard index — counts **up** from the oldest set (1-based). |
| 0, -1, -2, … | Alternate index — counts **down** from the newest set (0 = newest). |

Use the alternate index to refer to sets relative to *now* (e.g. -1 is always yesterday's backup regardless of how many
sets exist).

## Incremental Backups

TapeWin supports [incremental backups](help://topic/concepts.incremental-backup): only files changed since the last
backup are included. Multiple incremental sets together form an [incremental chain](help://glossary/incremental-chain)
that reconstructs the full state.

## Encrypted Sets

A set can be [encrypted](help://topic/concepts.encryption) with a password. Until you unlock it, it is shown grey as
**encrypted set**, and its files and settings are hidden.

## Set Icons

The icon of a set shows its kind at a glance:

| Overlay | Meaning |
|---|---|
| 🔒 bottom-right | encrypted, locked |
| 🔓 bottom-right | encrypted, unlocked |
| ⊕ bottom-left | incremental set (shown once unlocked, for an encrypted set) |
| ← top-left | continued from the previous volume |
| → top-right | continues on the next volume |

## Deleting Sets

Use **Media → Delete Backup Sets…** to remove the newest sets — see
[Delete Backup Sets](help://topic/dialog.delete-backup-sets). The space is reused right away: the TOC is rewritten behind
the last set kept, and the next backup continues from there. Encrypted sets can be deleted without their password.
