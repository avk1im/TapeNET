# tapecon — FAQ

## Why does tapecon open Win32 drive 0 by default?

Single-drive workstations should not need a drive number on every call. Without `--drive`, `--virtual` or `--in-memory`,
tapecon opens `\\.\Tape0`. Pass `--drive N` for another drive.

## Can I script tapecon non-interactively?

Yes. `--quiet` suppresses prompts and informational output; add `--yes` to destructive verbs like `format`. Branch on
the **exit codes** (`tapecon docs concepts`), not on log text. Unattended, prompts take their safe answer: a TOC search
proceeds (it only reads), an overwrite of a cartridge holding data is declined unless you pass `--overwrite-media`, and
an encrypted set without a password source is skipped.

## How do I pass a password in a script?

Use `--password-file FILE` or `--password-env VARIABLE`. The file is read as UTF-8; one trailing line break is ignored,
anything else — including a trailing space — is part of the password. `--password` on the command line is refused on
purpose: the shell history and the process list would show it.

## I forgot the password of an encrypted set. Now what?

It cannot be recovered — not by tapecon, not by anyone. The set stays on the cartridge as `[Locked]`; you can delete or
overwrite it, and every other set stays fully usable.

## Why is my encrypted set listed as [Locked] with no name or file count?

That is the encryption working: without the password, only the set's existence, position and size are visible. Pass the
password (`--password-file` / `--password-env`) to `list`, `restore`, `validate` or `verify`, or answer the prompt.

## Why can't I combine --encrypt with --compression hardware?

Encrypted data does not compress, so the drive's compression would gain nothing. Use software compression
(`--compression medium`, the default): it compresses **before** encrypting.

## A backup asked for another volume although the cartridge is not full. Why?

On a cartridge with a **TOC partition**, the partition can fill before the content does — typically with very many small
files. Both TOC copies must fit it, so the volume ends early and says so: "its TOC partition is full". Continue on a
cartridge formatted with a **larger** TOC partition: every volume of a series holds the whole series' TOC, so one of the
same size cannot take more files of that series. On cartridges without a TOC partition, the TOC's room follows the
backup automatically.

## The backup says "failed" but the files on the first volume are fine?

When you decline the next volume (or a fresh volume cannot take a single file), the backup ends there. Everything on the
volumes written so far — and their TOCs — is intact and restorable; "failed" means files were left out, and the message
says how many.

## A restore asks me to insert a volume it then rejects. What does that mean?

Nothing on that cartridge identifies it as the volume the series needs — not its media header, not its TOC, not its
set. Insert it again (perhaps another copy), skip it (its files are reported failed, the rest restores), or end. A
volume whose TOC alone is damaged is **not** rejected: its header or its set identifies it, and it restores.

## The TOC was "found only by searching" — is the cartridge damaged?

The TOC is fine; it simply was not where it should be, often because something was written behind it or a mark was
lost. tapecon found it anyway. Re-save it (for example `tapecon rename-media` with the same name) so it is found directly
next time.

## How do I cancel a running operation?

Ctrl+C. The first press requests cooperative cancellation — the current file finishes and the TOC is preserved. A second
press aborts hard.

## What does the set index actually mean?

Positive indexes count up from the **oldest** set: 1 is the first backup ever written. Zero is the **latest** set; -1
the one before, -2 two before. `tapecon restore 0` always restores the most recent set.

## How do I exclude files from a backup?

Use FCL inline or from a file:

    tapecon backup C:\Project not (path matches "**\bin\**" or path matches "**\obj\**")
    tapecon backup C:\Project --filter-file .\exclude-build.fcl

## Can I restore an older version of a file from a chain?

Yes: `tapecon restore N --incremental false` restores exactly set N. Restoring set 0 with the default gives you the most
recent version of every file across the whole chain.

## What's the difference between validate and verify?

- `validate` re-reads the tape and checks each file's recorded hash. No source files needed.
- `verify` compares the tape with the files on disk, byte by byte. The source files must still exist.

Both work on encrypted sets with the password, and both check the encryption's authentication as well.

## How do I move a TOC between cartridges, or keep a spare?

`tapecon toc export FILE` saves it; `tapecon toc import FILE` loads it. Keep an export of important cartridges — it is
the rescue path if both on-tape copies are lost, and the **only** way back to the files of an encrypted set without its
TOC. An export of an encrypted set stays encrypted.

## The TOC cannot be read at all. What can I do?

Run `tapecon scan-media`: it reads the whole cartridge without the TOC and recovers every TOC copy it finds. Then
`tapecon recover-toc --map FILE --fragment N --adopt`, or import a TOC you exported earlier.

## Can I use tapecon against a virtual drive for tests?

Yes — the integration tests do exactly that. `--virtual PATH` (file-backed) or `--in-memory`. Size with `--capacity`;
add a TOC partition with `--initiator PATH` and `--init-capacity`.

## How does this relate to TapeWinNET?

They share the same engine (TapeLibNET.Services). Anything backed up with one is fully restorable with the other —
encrypted sets included, with the password.
