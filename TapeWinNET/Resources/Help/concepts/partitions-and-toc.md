---
id: concepts.partitions-and-toc
title: Partitions and the TOC
kind: concept
keywords: [partition, TOC, table of contents, initiator partition, setmark, filemark, catalog, toc copy, recovered toc]
intents:
  - "what is the table of contents"
  - "what is an initiator partition"
  - "how does TapeWin find files on tape"
related:
  - concepts.toc-space
  - dialog.scan-media
---

# Partitions and the TOC

Every tape TapeWin manages carries a [Table of Contents (TOC)](help://glossary/toc) — a catalog of the media description,
the [backup sets](help://topic/concepts.backup-sets) it holds, and the files within each set, along with their positions
on the tape.

## The Table of Contents

The TOC is what makes a sequential tape navigable: instead of scanning the whole tape, TapeWin reads the catalog to find
exactly where a set or file begins. It is updated whenever you write, rename, or delete sets. Every volume carries **two
copies**, so one damaged copy loses nothing.

## Partitions

TapeWin can use an [initiator partition](help://glossary/initiator-partition) — a small dedicated partition that stores
the TOC separately from the data partition:

- faster, more robust TOC access;
- the catalog is less likely to be disturbed by data operations.

You enable this when [formatting](help://topic/dialog.format-media) or creating
[virtual media](help://topic/dialog.open-virtual-drive). Without it, the TOC lives after the data in a single-partition
layout delimited by [setmarks or filemarks](help://glossary/setmark-filemark).

Either way the TOC needs room — a reserve at the end of the tape, or the size of its partition. See
[TOC space](help://topic/concepts.toc-space).

## Damaged or Missing TOC

If the TOC is not where the layout keeps it, TapeWin **searches by itself**: the other copy, the position recorded by
another volume of the series, nearby — and, after asking you, the whole medium. A TOC found this way is used as usual;
the log advises re-saving it.

If no copy can be found, [Scan Media](help://topic/dialog.scan-media) maps the tape and can recover a TOC from an on-tape
copy. A recovered TOC is shown in the tree as **[recovered TOC]** and, like a TOC loaded from a file, is read-only.
