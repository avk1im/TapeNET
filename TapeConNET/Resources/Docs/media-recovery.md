# Media recovery: when the TOC cannot be read

Every operation starts by reading the cartridge's table of contents (TOC). When neither copy on the tape can be read,
tapecon stops with "Couldn't restore TOC". The data is usually still there: this topic shows how to get a TOC from
somewhere else, recover your files with it, and repair the cartridge.

**A TOC travels between commands as a file.** Each tapecon command runs on its own, so a TOC found by one command
reaches the next one as a `.tapetoc` file, passed with `--import-toc`.

## 1. Get a TOC

Three sources, from the quickest to the most thorough.

### A TOC file you saved earlier

If you exported the TOC after a backup, you are done here:

    tapecon export-toc D:\Safe\first.tapetoc

*Make this a habit:* export the TOC of every important cartridge and keep the file apart from it.

### A later volume of the same series

The TOC of every volume lists the sets of all volumes before it. So the TOC of volume 3 also serves volumes 1 and 2.
Insert the later volume and export its TOC:

    tapecon export-toc D:\Safe\series.tapetoc

Then insert the damaged earlier volume and continue with step 2. tapecon accepts a later volume's TOC without a
question, and uses it **read-only**: you can list, restore, validate and verify with it, but not write it to the
earlier volume.

### Scan the tape

`scan-media` reads the whole cartridge without relying on its TOC, finds every TOC copy on it, and recovers them.
It only reads: nothing on the tape changes.

    tapecon scan-media --export D:\Scans

The folder receives the scan map (`.tapescan`) and one `.tapetoc` file per recovered copy, named after the copy's
fragment number: `Scan_<id>_<time>_toc7.tapetoc`. Any of them will do. Without `--export`, the files go to the
current folder.

**To look before you recover**, scan without recovering, read the fragment list, then recover one copy:

    tapecon scan-media --no-recover-toc --export D:\Scans
    tapecon recover-toc --map D:\Scans\first.tapescan -f 7 --save first.tapetoc

Here `first.tapescan` stands for the map's actual name. `recover-toc` reads the copy from the loaded cartridge, and refuses if that is
not the scanned one.

## 2. Recover your files

Pass the TOC file with `--import-toc`. The cartridge's own TOC is then not read at all:

    tapecon list --import-toc D:\Safe\first.tapetoc
    tapecon restore 0 --target D:\Restored --import-toc D:\Safe\first.tapetoc

`validate` and `verify` take `--import-toc` as well, to check what the recovered TOC promises.

## 3. Repair the cartridge

A command that saves the TOC writes both copies back where they belong. Two commands do, and both take `--import-toc`.

**`rename-media`** saves the TOC unchanged when you give no new name. This repairs a damaged TOC, and also a TOC that
was "found only by searching":

    tapecon rename-media --import-toc D:\Safe\first.tapetoc

**`delete-sets`** removes sets from the given one to the end of the volume, then saves the TOC. Use it when the end of
the volume is damaged, typically a last set that never completed:

    tapecon delete-sets 0 --import-toc D:\Safe\first.tapetoc

A later volume's TOC cannot repair an earlier volume: it would make that volume claim the later volumes' sets. Use the
volume's own TOC, from a file or a scan.

After the repair, the cartridge reads normally again. `tapecon info` shows it.

## Prevention

- Export the TOC after each backup of an important cartridge (`export-toc`).
- For encrypted sets this matters most: without a TOC, their files cannot be found at all.
- When tapecon reports a damaged TOC copy, re-save the TOC soon, before the second copy goes the same way:
  `tapecon rename-media` with the current name.

See also `tapecon docs toc` and `tapecon docs multi-volume`.
