---
id: concepts.backup-sets
title: Backup sets
kind: concept
keywords: [backup set, tape, index, TOC, set index]
intents:
  - "what is a backup set"
  - "explain backup sets"
  - "how are backups organised on tape"
related:
  - quickstart.backup
  - concepts.incremental-backup
---
# Backup sets

A **backup set** is a self-contained unit of backup data written to tape. Each
time you run a backup, TapeWinNET appends a new backup set to the tape.

## Set index

Backup sets are numbered in two ways:

- **Standard index** — counts up from the oldest set, starting at 1.
- **Alt index** — counts down from the most recent set (0, −1, −2, …).

Both indexes are shown in the tree view as `#N | −M`.

## Table of Contents (TOC)

The TOC at the beginning of the tape catalogues all sets. TapeWinNET reads the
TOC when you insert a tape so that all sets are available immediately without
scanning the full tape.
