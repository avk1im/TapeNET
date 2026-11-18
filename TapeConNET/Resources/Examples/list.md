## Examples

List the files on the tape:

    tapecon list

Show only the overview of the sets:

    tapecon list --sets-only

List the latest set, then sets 1 through 3:

    tapecon list 0
    tapecon list 1 3

List the Word documents of the latest set, by name only:

    tapecon list 0 *.docx --name-only

List the large files, with an FCL expression (`tapecon docs fcl`):

    tapecon list 0 "Size greaterThan 100MB"

For an incremental set, the list includes what it rolls in from earlier sets; `--no-incremental` shows the set's own
files only. *On the virtual tape*, add `--virtual-open C:\Tapes\first`.
