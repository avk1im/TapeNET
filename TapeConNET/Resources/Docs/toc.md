# The table of contents (TOC)

The TOC indexes a cartridge: each backup set's description and settings, and every file with its position on tape and
its hash. tapecon reads it before almost every operation, and rewrites it after every change.

## Two copies, two places

The TOC is written **twice** on every volume, so one damaged copy never loses it. Where depends on the media:

- **TOC partition:** the TOC has a partition of its own, and content may fill its partition to the very end.
  `format` creates one by default; `--single-partition` declines it. Virtual tapes get one with `--init-capacity` or
  `--initiator` (see `tapecon docs virtual-drives`).
- **TOC in set:** on all other media, the TOC follows the content on the same partition.

The TOC of each volume of a series lists the sets of **every volume up to its own**: the last volume's TOC describes
the whole series.

## The TOC reserve

tapecon sizes the TOC's room **as the backup runs**: every file written adds its TOC entry, and the room grows with it.
A backup of millions of small files cannot outgrow it, and a small backup wastes no space.

- **TOC in set:** content stops early enough for both TOC copies to fit. tapecon keeps at least 16 MiB free on LTO
  drives and 4 MiB on others, less on very small media.
- **TOC partition:** its size is fixed when you format. When the next files' entries would not fit it twice, the
  volume ends early and the backup continues on the next volume (see `tapecon docs multi-volume`).

`tapecon info` shows the current **TOC Reserve**, and the room left for content as **Writable**.

## When the TOC is not where it should be

Loading a TOC tries, in order: its usual place, the second copy, the position the series TOC records, a short search
from a nearby landmark, and, only after asking, a search of the whole medium. Unattended runs search without asking,
since a search only reads.

Each deviation is reported, with advice. A TOC "found only by searching" is intact: it simply was not where it should
be, often because something was written behind it or a mark was lost. Re-save it, so it is found directly next time:

    tapecon rename-media --name "My first tape"

Any command that saves the TOC rewrites both copies; renaming the media under its current name is the simplest.

## A spare copy

Keep an exported TOC of important cartridges, apart from them. It is the rescue path if both copies on tape are lost,
and the **only** way back to the files of an encrypted set without its TOC. An export of an encrypted set stays
encrypted.

    tapecon export-toc D:\Safe\first.tapetoc

Commands that read a cartridge take such a file instead of the TOC on the tape, with `--import-toc`:

    tapecon list --import-toc D:\Safe\first.tapetoc

## When no TOC can be read

`scan-media` reads the whole cartridge, every partition, **without** relying on its TOC, reports what it finds, and
saves every TOC copy it can recover as a file for `--import-toc`. It only reads, and needs no password: encrypted sets
are counted and named "(encrypted)", never revealed.

The whole procedure, from finding a TOC to repairing the cartridge, is in `tapecon docs media-recovery`.
