---
id: concepts.tape-vs-disk
title: Tape vs Disk
kind: concept
keywords: [tape, disk, sequential, random access, archive, backup, capacity]
intents:
  - "how is tape different from disk"
  - "why use tape for backup"
  - "sequential vs random access"
related:
  - concepts.why-tape-backup
  - concepts.backup-sets
  - concepts.partitions-and-toc
---

# Tape vs Disk

Tape is a **sequential** medium: data is written and read in order, from one
end toward the other.  This is different from a disk, where any block can be
reached directly at any time.

## What that means in practice

| | Tape | Disk |
|--|------|------|
| Access | Sequential — position, then read/write | Random — jump anywhere |
| Strength | High capacity, low cost per GB, durable offline storage | Fast random access |
| Best for | Backups and long-term archives | Active, frequently-changed data |

## Why it shapes TapeWin

Because tape is sequential, TapeWin writes each [backup set](help://topic/concepts.backup-sets)
one after another, and keeps a **Table of Contents** so it can locate sets and
files quickly.  Operations like deleting sets or reclaiming space follow the
sequential nature of the medium — see
[Partitions and the TOC](help://topic/concepts.partitions-and-toc).
