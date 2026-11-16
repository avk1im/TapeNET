## Examples

Survey the tape from start to end without relying on its TOC. Read-only, no password needed:

    tapecon scan-media

Save the scan map into a folder, for `tapecon recover-toc`:

    tapecon scan-media --export D:\Scans

Scan without recovering the TOC copies it finds:

    tapecon scan-media --no-recover-toc
