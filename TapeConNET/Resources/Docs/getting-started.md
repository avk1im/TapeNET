# Getting started: your first backup and restore

This walk-through takes you from nothing to a restored file: a first backup, a look at the tape, a restore, and a
check. Every step shows the command for a **real tape drive** and for a **virtual tape**, a file on your disk that
behaves like a cartridge. No tape drive? Follow the virtual commands; everything else works the same.

## 1. What you need

- A folder to back up. The examples use `D:\Data`.
- Either a tape drive with a cartridge you may erase, or nothing at all: a virtual tape needs only disk space.

## 2. Prepare the media

### Real tape drive

A backup adds to **TapeNET media**, so format a new cartridge first and give it a description:

    tapecon format --mdesc "My first tape"

tapecon asks before it erases the cartridge; `--yes` skips the question. Formatting writes the media header and an
empty table of contents (TOC).

### Virtual tape

Nothing to prepare: the first backup can create the virtual tape (step 3). Alternatively, create it up front:

    mkdir C:\Tapes
    tapecon create-virtual C:\Tapes\first --mdesc "My first tape"

This creates `C:\Tapes\first.vt` with 500 MiB of room. For another size, add e.g. `--capacity 2GB`.

## 3. Your first backup

Back up a folder and its subfolders with a **real tape drive**, using drive 0 by default:

    tapecon backup D:\Data --subdirs

*Notice:* every backup **adds** a new set after the sets already on the tape. Should a backup outgrow the cartridge,
tapecon asks for the next one, unless you give `--no-multivolume`. To start a tape over, use `--overwrite-media`.

To back up to **another physical drive**, e.g. drive 2:

    tapecon backup D:\Data --subdirs --drive 2

Back up to a newly created **virtual tape**, named `first` and placed in the folder `C:\Tapes`:

    tapecon backup D:\Data --subdirs --virtual-create C:\Tapes\first

If you created the virtual tape in step 2, open it instead:

    tapecon backup D:\Data --subdirs --virtual-open C:\Tapes\first

Each backup writes one **backup set**. Its description reads "Backup created" with the date and time, unless you give
`--description`.

## 4. See what is on the tape

The drive, the media and an overview of its sets, then every file:

    tapecon info
    tapecon list

With the virtual tape:

    tapecon info --virtual-open C:\Tapes\first
    tapecon list --virtual-open C:\Tapes\first

Sets are numbered both ways: **1** is the oldest set, **0** the latest, **-1** the one before it.

## 5. Restore

Restore the latest set to `D:\Restored`, recreating its folders:

    tapecon restore --target D:\Restored --subdirs

From the virtual tape:

    tapecon restore --target D:\Restored --subdirs --virtual-open C:\Tapes\first

Without `--target`, the files go to the current folder. To restore another set, give its index first, e.g.
`tapecon restore -1 --target D:\Restored`. To restore only some files, add a pattern after the index:

    tapecon restore 0 *.docx --target D:\Restored

## 6. Check the backup

`validate` re-reads the tape and checks every file against its recorded hash; it needs no source files. `verify`
compares the tape with the files in `D:\Data`, byte by byte:

    tapecon validate
    tapecon verify

With the virtual tape:

    tapecon validate --virtual-open C:\Tapes\first
    tapecon verify --virtual-open C:\Tapes\first

## 7. Where next

- **Back up only what changed:** `--incremental`, see `tapecon docs incremental-backup`.
- **Encrypt a set:** `--encrypt`, see `tapecon docs encryption`.
- **Select files by name, size or date:** `tapecon docs fcl`.
- **Every option of a verb, with examples:** `tapecon backup --help`.
- **All topics:** `tapecon docs`.
