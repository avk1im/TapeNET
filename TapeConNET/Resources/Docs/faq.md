# FAQ

## Why does tapecon open Win32 drive 0 by default?

Single-drive workstations should not need a drive number on every call. Without `--drive`, `--virtual-open`,
`--virtual-create` or `--in-memory`, tapecon opens `\\.\Tape0`. Pass `--drive N` for another drive.

## Why does my first backup fail with "Couldn't restore TOC"?

A backup **appends** to the sets on the media, so it reads the TOC first. A new or foreign cartridge has none. Format
it first (`tapecon format`), or start it over with `tapecon backup --overwrite-media`.

## Can I script tapecon non-interactively?

Yes. `--quiet` suppresses informational output and prompts; add `--yes` to `format`. Branch on the **exit codes**
(`tapecon docs concepts`), not on log text. Unattended, prompts take their safe answer: a TOC search proceeds, since it
only reads, and an encrypted set without a password source is skipped.

## How do I pass a password in a script?

With `--password-file FILE` or `--password-env VARIABLE`; see `tapecon docs encryption`. In PowerShell, set the
variable with `$env:VARIABLE = '...'`: a plain `$VARIABLE` would set a shell variable, invisible to tapecon. In cmd,
use `set VARIABLE=...`.

## I forgot the password of an encrypted set. Now what?

It cannot be recovered, not by tapecon and not by anyone. The set stays on the cartridge as [Locked]; you can delete or
overwrite it, and every other set stays fully usable.

## Why is my encrypted set listed as [Locked] with no name or file count?

That is the encryption working: without the password, only the set's existence, position and size are visible. Pass
the password (`--password-file`, `--password-env`) to list, restore, validate or verify, or answer the prompt.

## A backup asked for another volume although the cartridge is not full. Why?

Its TOC partition filled first, typically with very many small files. See `tapecon docs multi-volume`.

## The TOC was "found only by searching". Is the cartridge damaged?

No: the TOC is intact, it simply was not where it should be. Re-save it, for example with `tapecon rename-media` and
the same name, so it is found directly next time. See `tapecon docs toc`.

## How do I cancel a running operation?

Ctrl+C. The first press requests cooperative cancellation: the current file finishes and the TOC is preserved. A second
press aborts hard.

## How do I exclude files from a backup?

With an FCL filter. Back up everything except temporary files:

    tapecon backup D:\Data --subdirs --filter "Extension notEquals .tmp"

Join more conditions with `and`, e.g. `and not Attributes have Hidden`, or keep them in a filter file. See
`tapecon docs fcl` for every field and operator.

## Can I restore an older version of a file from a chain?

Yes: `tapecon restore N --incremental false` restores exactly set N. Restoring set 0 with the default gives you the most
recent version of every file across the whole chain.

## How do I start a tape over?

`tapecon backup --overwrite-media` replaces every set, the TOC and the media header; `--mdesc` describes the new media.
To remove only the latest sets, use `tapecon delete-sets`.

## How do I change tapecon's colors, or turn them off?

Set `TAPECON_COLORS` to `key=style` pairs, separated by `;`. A style uses Spectre.Console's syntax: a color name
(`yellow`, `dodgerblue1`), a hex value (`#ff8800`), decorations (`bold`, `dim`, `italic`), and `on` for a background.

    $env:TAPECON_COLORS = 'heading=bold dodgerblue1;mention=darkcyan'

| Key | Colors |
|---|---|
| `heading`, `subheading` | titles in the help and the docs |
| `emphasis` | the help's usage line |
| `option`, `placeholder` | option names and their values (PATH, SIZE) in the help |
| `mention` | options and commands named inside help texts |
| `dimmed` | intros, defaults, footers |
| `code`, `codeblock` | inline code and examples in the docs |
| `completed`, `info`, `warning`, `failed`, `error` | the log levels |

A key you leave out keeps its default. A wrong entry is skipped with a warning; tapecon runs on.

To turn colors off, pass `--no-color`, or set `NO_COLOR` to any value, the convention many command-line tools follow
(no-color.org). Either way the output turns plain: no colors, no box drawing.

## How does this relate to TapeWinNET?

tapecon and TapeWinNET share the same engine, TapeLibNET. Anything backed up with one is fully restorable with the
other, encrypted sets included, with the password.

## The help runs off the screen. How do I page it?

Scroll back. Every modern terminal keeps the output: use the mouse wheel, or **Ctrl+Shift+PgUp** in Windows Terminal.
**Ctrl+Shift+F** searches it.

Paging through a pipe works too, but it drops the colors: tapecon writes plain text into any pipe.

    tapecon backup --help | Out-Host -Paging

To keep the text at hand, write it to a file:

    tapecon backup --help > backup-help.txt

Garbled characters such as `ΓöÇΓöÇ` in piped output mean PowerShell reads tapecon's UTF-8 output with an old code
page. Add this line to your PowerShell profile to fix it for good:

    $OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
