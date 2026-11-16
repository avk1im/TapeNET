## Examples

Compare the latest set with the original files on disk, byte by byte. The files must still be where they were backed up:

    tapecon verify

Compare only the Word documents of the latest set:

    tapecon verify 0 *.docx

*Notice:* a file changed since the backup shows up as a difference. To check the tape alone, use `tapecon validate`.

*On the virtual tape*, add `--virtual-open C:\Tapes\first`.
