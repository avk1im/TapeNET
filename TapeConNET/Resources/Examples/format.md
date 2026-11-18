## Examples

Format the cartridge in drive 0 and describe it. tapecon asks before it erases anything:

    tapecon format --mdesc "Archive 2026"

The same, unattended:

    tapecon format --mdesc "Archive 2026" --yes

Format **without** a TOC partition: the TOC then follows the content:

    tapecon format --single-partition

Erase a virtual tape:

    tapecon format --virtual-open C:\Tapes\first --yes

*Notice:* formatting **erases all data**. To start a tape over as part of a backup, use
`tapecon backup --overwrite-media`.
