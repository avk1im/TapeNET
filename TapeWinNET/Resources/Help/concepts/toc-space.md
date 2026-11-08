---
id: concepts.toc-space
title: TOC Space
kind: concept
keywords: [TOC space, TOC reserve, TOC partition, full, one copy, calibrate, export TOC, found by searching, toc unreadable]
intents:
  - "only one TOC copy fit"
  - "the TOC partition is full"
  - "why did the volume end early"
  - "the TOC was found only by searching"
  - "the table of contents could not be read"
related:
  - concepts.partitions-and-toc
  - concepts.multi-volume
  - dialog.scan-media
ai_excerpt: true
---

# TOC Space

Every volume carries **two copies** of its [Table of Contents](help://glossary/toc), so that one damaged copy never
loses your catalog. Both need room — and how that room is found depends on the tape layout.

## TOC after the content

On a single-partition tape the TOC is written after the last set. While a backup runs, TapeWin keeps a
[TOC reserve](help://glossary/toc-reserve) free at the end of the tape: room for both copies of the TOC as it will be when
the backup ends. The reserve **grows with the backup** — each file adds its entry — and never shrinks during it. It is at
least 16 MiB on LTO drives and 4 MiB on others (less on very small media). When the content reaches the reserve, the set
continues on the next volume.

## TOC in its own partition

With an [initiator partition](help://glossary/initiator-partition) the TOC has a fixed space of its own, chosen when the
tape was formatted. TapeWin checks it as the backup runs: when the next files' entries would no longer fit twice, the
volume **ends early** — even with content space left — and the backup continues on the next volume. The log says:
*"Volume #2 ends early: its TOC partition is full"*.

Every volume holds the TOC of the **whole series**, so a cartridge of the same partition size cannot take more files of
that series. Continue on a cartridge formatted with a larger TOC partition, or start a new series.

## What the advice messages mean

| Message | Meaning | What to do |
|---|---|---|
| **Only one TOC copy fit** | the tape was full; copy 2 is missing or incomplete | Calibrate the drive for this media, so the space left is known precisely; export the TOC to a file now |
| **TOC partition is full** | as above, in a TOC partition | Back up fewer files per set or volume, or format with a larger TOC partition |
| **A TOC copy is damaged** | one copy did not read; the other did | Re-save the TOC — e.g. rename the media |
| **TOC found only by searching** | the TOC was not where the layout keeps it | Re-save the TOC; the tape's tail may be damaged |
| **The TOC could not be read** | no copy was found | [Scan Media](help://topic/dialog.scan-media) and use the TOC it recovers, or import a TOC file exported earlier |

## How TapeWin finds a TOC

Loading a tape tries, in order: the usual place, the other copy, the position recorded in the TOC of another volume of
the series, a short search nearby — and, only after asking you, a search of the **whole medium**. Searching only reads;
you can cancel it.

## Keep an exported TOC

**Media ▸ Export TOC** saves the catalog to a file. If a tape's TOC is ever lost, that file restores access to every set
on it — it matters most for [encrypted](help://topic/concepts.encryption) sets.
