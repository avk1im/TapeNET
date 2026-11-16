# Virtual drives

A **virtual tape** is a file that behaves like a cartridge. tapecon writes it, reads it, and fills it up exactly as it
would a real one: for trying tapecon out, for tests, or for backups to a disk. Two kinds exist:

- **File-backed:** the media lives in files on your disk and survives between runs.
- **In-memory:** `--in-memory` gives a throw-away drive for a single run.

## Creating virtual media

Let the first backup create it:

    tapecon backup D:\Data --subdirs --virtual-create C:\Tapes\first

Or create it on its own, ready for later use:

    tapecon create-virtual C:\Tapes\first --capacity 2GB --mdesc "Test tape"

New media comes formatted, with an empty TOC. Its files:

| File | Holds |
|---|---|
| `C:\Tapes\first.vt` | the content; a path without extension gets `.vt` |
| a `.vrt` file beside it | the drive's metadata |
| `C:\Tapes\first_init.vt` | the TOC partition, when there is one |

- `--capacity` sizes the content; the default is 500 MiB.
- `--init-capacity` adds a TOC partition of that size; `--initiator PATH` names its file.
- Existing virtual media is replaced only after a prompt, or with `--overwrite-virtual`.
- The drive's mark profile (`--setmarks`, `--seq-filemarks`, `--filemarks-only`) emulates different tape drives; see
  `tapecon create-virtual --help`.

## Opening virtual media

    tapecon list --virtual-open C:\Tapes\first

`--virtual` and `-V` are short for `--virtual-open`. A path without extension also finds the `.vt` file, and the TOC
partition is found by its conventional name. Sizes and the mark profile come from the media itself, so the creation
options are ignored here.

`--virtual-open` fails when no media exists; new media takes `--virtual-create` or `create-virtual`.

## One drive per run

`--drive`, `--virtual-open`, `--virtual-create` and `--in-memory` exclude each other. Without any of them, tapecon
opens physical drive 0.
