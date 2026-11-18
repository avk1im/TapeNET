## Examples

Check the latest set: tapecon re-reads every file and compares it with its recorded hash. No source files needed:

    tapecon validate

Check set 1, the oldest one:

    tapecon validate 1

Check only the Word documents of the latest set:

    tapecon validate 0 *.docx

*On the virtual tape*, add `--virtual-open C:\Tapes\first`. To compare with the files on disk instead, use
`tapecon verify`.
