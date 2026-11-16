## Examples

Restore the latest set to `D:\Restored`, recreating its folders:

    tapecon restore 0 --target D:\Restored --subdirs

Restore only Word documents. The set index comes **first**, then the patterns:

    tapecon restore 0 *.docx --target D:\Restored

Restore the set **before** the latest one:

    tapecon restore -1 --target D:\Restored --subdirs

Restore the files changed in the last week, with an FCL expression (`tapecon docs fcl`):

    tapecon restore 0 "Modified after today-7d" --target D:\Restored

Restore **exactly** set 3, with nothing rolled in from earlier sets of its incremental chain:

    tapecon restore 3 --incremental false --target D:\Restored

Restore an encrypted set, with the password from an environment variable:

    tapecon restore 0 --target D:\Restored --password-env TAPE_PW

*From the virtual tape*, add `--virtual-open C:\Tapes\first`. Without `--target`, the files go to the current folder.
