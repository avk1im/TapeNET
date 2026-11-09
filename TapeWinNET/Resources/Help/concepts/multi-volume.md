---
id: concepts.multi-volume
title: Multi-Volume Backups
kind: concept
keywords: [multi-volume, volume, end of media, span, continue, swap tape, series, series TOC, skip volume, reject]
intents:
  - "what happens when the tape is full"
  - "back up across multiple tapes"
  - "multi-volume restore"
  - "span a backup over volumes"
  - "the volume I inserted is rejected"
  - "what is a series TOC"
related:
  - concepts.toc-space
  - concepts.backup-sets
  - dialog.restore
---

# Multi-Volume Backups

When a backup is larger than the tape it is being written to, TapeWin spans the operation across
[multiple volumes](help://glossary/multi-volume) (cartridges), together a **series**.

## How a backup continues

- The backup runs until the tape is full — its content reaches the [TOC reserve](help://glossary/toc-reserve), or its
  TOC partition is full (see [TOC space](help://topic/concepts.toc-space)).
- TapeWin writes the TOC, pauses and prompts you to load the next volume.
- After you insert (or create) a new volume, the backup **resumes** where it left off. The set's part on the new volume
  is named after the set, with the volume number: *Daily (2)*.

If a fresh volume cannot take a single file — its TOC partition is too small for the series' TOC — the backup ends with
an error instead of asking for yet another volume.

**Ending a series early** (declining the next volume) leaves every volume written so far restorable, but the backup is
reported as failed, naming the files left out.

## How a restore continues

The **last volume** of a series holds the TOC of the whole series: load it first. A restore then asks for the earlier
volumes it needs. Each inserted cartridge is identified — by its media header, its own TOC, or its set's first block; a
cartridge whose own TOC does not read is still used when it is identified otherwise.

A cartridge that cannot be identified is **rejected**. You can **insert** it again (or another copy), **skip** that volume
— its files are reported as not restored, the rest goes on — or **end** the restore.

## The series TOC

If a restore ends with an earlier volume in the drive, the tree still shows the series' TOC, marked
**[series TOC — reread to edit]**. Restoring from it works; backup, delete and rename are unavailable until you load the
inserted volume's own TOC with **Media ▸ Reread Media** (TapeWin offers to do so).

## Tips

- Label your volumes in order — you will be asked for them in sequence.
- The **No multivolume** option in the [New Backup dialog](help://topic/dialog.backup) stops at end-of-media instead of
  spanning, if you prefer a single-tape backup.
- A series with a TOC partition: use cartridges with the same or a larger TOC partition.
- Multi-volume works for [remote](help://topic/concepts.remote-service) drives too, prompting you to choose or create the
  next remote volume.
