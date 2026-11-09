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

**Backup set** — a single snapshot of files written to tape at one point in time. See
[Backup sets](help://topic/concepts.backup-sets).

**TOC** (Table of Contents) — the on-tape catalog of media description, sets, and files; every volume carries two copies.
See [Partitions and the TOC](help://topic/concepts.partitions-and-toc).

**Initiator partition** — a small dedicated partition that stores the TOC separately from the data.

**TOC reserve** — space TapeWin keeps free at the end of a single-partition tape for both TOC copies; it grows with the
backup. See [TOC space](help://topic/concepts.toc-space).

**Scan Media** — a read-only pass over a tape that maps its contents and can recover a damaged or missing TOC. See
[Scan Media](help://topic/dialog.scan-media).

**Recovered TOC** — a TOC rebuilt by Scan Media; it is read-only, like a TOC loaded from a file.

**Series TOC** — after a restore that ended on an earlier volume, the TOC still shown is the series' one, not the inserted
volume's; reread the media to edit. See [Multi-volume backups](help://topic/concepts.multi-volume).

**Incremental backup** — a backup that includes only files changed since the previous backup. See
[Incremental backup](help://topic/concepts.incremental-backup).

**Incremental chain** — a sequence of incremental sets that together reconstruct the latest version of every file in the
backup set. See [Incremental backup](help://topic/concepts.incremental-backup).

**Multi-volume** — a backup or restore that spans more than one tape. See
[Multi-volume backups](help://topic/concepts.multi-volume).

**Encrypted set** — a backup set protected by a password: its files and description are unreadable without it. See
[Encrypted backup sets](help://topic/concepts.encryption).

**Locked set** — an encrypted set whose password has not been entered in this session; shown grey with a closed padlock.
Unlock it to see its files.

**Forget Passwords** — locks every encrypted set again and drops the keys TapeWin held (Media menu).

**Virtual drive** — a tape drive emulated using a file or RAM. See [Virtual drives](help://topic/concepts.virtual-drives).

**FCL** (File Conditions Language) — the language used to filter files. See
[FCL file filters](help://topic/concepts.fcl-filters).

**Validate** — read tape data and compare checksums, without writing files.

**Verify** — compare files already on disk byte-for-byte against tape.

**Setmark / Filemark** — markers written on tape to delimit sets and files in a single-partition layout.

**Remote service** — a TapeNET service that exposes a tape drive over the network. See
[Remote service](help://topic/concepts.remote-service).
