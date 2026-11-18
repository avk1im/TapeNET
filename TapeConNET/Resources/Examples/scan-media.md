## Examples

Survey the tape without relying on its TOC, and recover every TOC copy found. Read-only, no password needed. The map
and the recovered TOCs go to `D:\Scans`:

    tapecon scan-media --export D:\Scans

Map only, to examine the fragments first; recover one later with `recover-toc`:

    tapecon scan-media --no-recover-toc --export D:\Scans

Then use a recovered TOC, e.g. to list the tape's contents. The file is named after the scan and the copy, here
shortened:

    tapecon list --import-toc D:\Scans\Scan_1a2b3c4d_toc7.tapetoc

See `tapecon docs media-recovery`.
