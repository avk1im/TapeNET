# The table of contents (TOC)

The TOC indexes a cartridge: each backup set's description and settings, and every file with its position on tape and
its hash. tapecon reads it before almost every operation, and rewrites it after every change.

## Two copies, two places

The TOC is written **twice** on every volume, so one damaged copy never loses it. Where depends on the media:

- **TOC partition:** the TOC has a partition of its own, and content may fill its partition to the very end.
  `format` creates one by default; `--single-partition` declines it. Virtual tapes get one
  with `--init-capacity` or `--initiator` (see `tapecon docs virtual-drives`).
- **TOC in set:** on all other media, the TOC follows the content on the same partition.

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
be, often because something was written behind it or a mark was lost. Re-save it, for example with
`tapecon rename-media` and the same name, so it is found directly next time.

## A spare copy

Keep an exported TOC of important cartridges. It is the rescue path if both copies on tape are lost, and the **only**
way back to the files of an encrypted set without its TOC.

    tapecon toc export D:\Safe\first.toc

`tapecon toc import FILE` loads such a file. An export of an encrypted set stays encrypted.

## When no TOC can be read

`scan-media` reads the whole cartridge, every partition, **without** relying on its TOC, and reports what it finds:
the media header, backup sets, TOC copies, damaged and unidentified data. It is read-only and needs no password:
encrypted sets are counted and named "(encrypted)", never revealed. By default it also recovers every TOC copy it
finds.

    tapecon scan-media --export D:\Scans

`--export` saves the scan as a map file (`.tapescan`); `--json` prints it instead. `recover-toc` then recovers the TOC
from one copy listed in the map: `--save FILE` writes it to a file, `--adopt` uses it like `toc import`. If the loaded
cartridge is not the scanned one, recovery is refused.
