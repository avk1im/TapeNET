## Examples

Scan the tape and save the scan map as JSON, then recover the TOC from the copy the scan lists as fragment 2, and save
it to a file:

    tapecon scan-media --json > scan.json
    tapecon recover-toc --map scan.json --fragment 2 --save first.toc

A map saved with `tapecon scan-media --export FOLDER` works the same way.

*Notice:* the cartridge in the drive must be the scanned one; otherwise recovery is refused.
