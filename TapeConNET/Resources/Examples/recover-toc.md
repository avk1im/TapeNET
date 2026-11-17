## Examples

Recover the TOC copy the scan listed as fragment 7 (`-f` is short for `--fragment`), and save it:

    tapecon recover-toc --map D:\Scans\first.tapescan -f 7 --save first.tapetoc

Then restore with it:

    tapecon restore 0 --target D:\Restored --import-toc first.tapetoc

*Notice:* the cartridge in the drive must be the scanned one; otherwise recovery is refused.
