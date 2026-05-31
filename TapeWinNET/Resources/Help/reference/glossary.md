---
id: reference.glossary
title: Glossary
kind: reference
keywords: [glossary, terms, definitions, vocabulary]
intents:
  - "what does this term mean"
  - "definitions"
  - "glossary of terms"
related:
  - concepts.backup-sets
  - concepts.partitions-and-toc
---

# Glossary

**Backup set** — a single snapshot of files written to tape at one point in
time.  See [Backup sets](help://topic/concepts.backup-sets).

**TOC (Table of Contents)** — the on-tape catalog of media description, sets,
and files.  See [Partitions and the TOC](help://topic/concepts.partitions-and-toc).

**Initiator partition** — a small dedicated partition that stores the TOC
separately from the data.

**Incremental backup** — a backup that includes only files changed since the
previous backup.  See [Incremental backup](help://topic/concepts.incremental-backup).

**Incremental chain** — a sequence of incremental sets that together reconstruct
the latest version of every file.

**Multi-volume** — a backup or restore that spans more than one tape.  See
[Multi-volume backups](help://topic/concepts.multi-volume).

**Virtual drive** — a tape drive emulated using a file or RAM.  See
[Virtual drives](help://topic/concepts.virtual-drives).

**FCL (File Conditions Language)** — the language used to filter files.  See
[FCL file filters](help://topic/concepts.fcl-filters).

**Validate** — read tape data and compare checksums, without writing files.

**Verify** — compare files already on disk byte-for-byte against tape.

**Setmark / Filemark** — markers written on tape to delimit sets and files in a
single-partition layout.

**Remote service** — a TapeNET service that exposes a tape drive over the
network.  See [Remote service](help://topic/concepts.remote-service).
