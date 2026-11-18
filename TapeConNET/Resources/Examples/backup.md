## Examples

Back up a folder and its subfolders to the tape in drive 0. The new set goes **after** the sets already on the tape:

    tapecon backup D:\Data --subdirs

Add an incremental set, holding **only** the files changed since the last backup:

    tapecon backup D:\Data --subdirs --incremental

Start the tape over: the new set **replaces everything** on it, and the media gets a new description:

    tapecon backup D:\Data --subdirs --overwrite-media --mdesc "Archive 2026"

Back up only the files changed in the last week, with an FCL filter (`tapecon docs fcl`):

    tapecon backup D:\Data --subdirs --filter "Modified after today-7d"

Encrypt the set; tapecon asks for the password twice:

    tapecon backup D:\Data --subdirs --encrypt

Back up to **another drive**, e.g. drive 2:

    tapecon backup D:\Data --subdirs --drive 2

Back up to a new **virtual tape**, a file in `C:\Tapes`, and later add to it:

    tapecon backup D:\Data --subdirs --virtual-create C:\Tapes\first
    tapecon backup D:\Data --subdirs --incremental --virtual-open C:\Tapes\first

*Every example above works on the virtual tape*, too: add `--virtual-open C:\Tapes\first`.

*Notice:* a backup appends to **TapeNET media**. Format a new cartridge first (`tapecon format`), or use
`--overwrite-media`. A backup larger than the cartridge asks for the next one, unless you give `--no-multivolume`.
