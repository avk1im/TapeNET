---
id: concepts.partitions-and-toc
title: Partitions and the TOC
kind: concept
keywords: [partition, TOC, table of contents, initiator partition, setmark, filemark, catalog]
intents:
  - "what is the table of contents"
  - "what is an initiator partition"
  - "how does TapeWin find files on tape"
---

# Partitions and the TOC

Every tape TapeWin manages carries a [Table of Contents (TOC)](help://glossary/toc) — a catalog of
the media description, the [backup sets](help://topic/concepts.backup-sets) it
holds, and the files within each set, along with their positions on the tape.

## The Table of Contents

The TOC is what makes a sequential tape navigable: instead of scanning the
whole tape, TapeWin reads the catalog to find exactly where a set or file
begins.  It is updated whenever you write, rename, or delete sets.

## Partitions

TapeWin can use an
[initiator partition](help://glossary/initiator-partition) — a small dedicated partition that stores the TOC
separately from the data partition.

- Faster, more robust TOC access.
- The catalog is less likely to be disturbed by data operations.

You enable this when [formatting](help://topic/dialog.format-media) or creating
[virtual media](help://topic/dialog.open-virtual-drive), via the **initiator
the TOC lives alongside the
data in a single-partition layout delimited by [setmarks or filemarks](help://glossary/setmark-filemark).
