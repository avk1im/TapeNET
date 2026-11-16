# Multi-volume backups

When a cartridge fills up, the backup asks for the next one and continues there: the set spans both **volumes**. Each
volume carries its own media header and both TOC copies; the **last** volume's TOC describes the whole series.

`--no-multivolume` stops at the end of the first cartridge instead.

## Ending a series early

If you decline the next volume, or a fresh volume cannot take a single file, the backup ends there. Every volume
written so far stays restorable, TOC included. The backup reports **failed** and says how many files it left out.

## Restoring a series

`restore` opens the **last** volume, then asks for each earlier volume it needs. tapecon identifies a volume by its
media header, its own TOC, or the first block of its set, and, as a last resort, by searching it whole.

A volume that nothing identifies is rejected. Insert it again (or another copy), skip it, or end. A skipped volume's
files are reported as failed; the rest restores. A volume whose TOC alone is damaged is **not** rejected: its header or
its set still identifies it.

## A volume ended although the cartridge was not full

On a cartridge with a **TOC partition**, the partition can fill before the content does, typically with very many
small files. Both TOC copies must fit it, so the volume ends early and says so: "its TOC partition is full".

Continue on a cartridge formatted with a **larger** TOC partition. Every volume of a series holds the whole series'
TOC, so one of the same size cannot take more files of that series. See `tapecon docs toc`.
