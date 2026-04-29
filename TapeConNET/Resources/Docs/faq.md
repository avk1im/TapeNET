# tapecon — FAQ

## Why does `tapecon` open Win32 drive 0 by default?

The previous CLI required a drive number on every invocation. 2.0 keeps
single-drive workstations friction-free by defaulting to `\\.\Tape0` when
no `--drive`, `--virtual`, or `--in-memory` option is supplied. Pass
`--drive N` to target another physical drive.

## Can I script `tapecon` non-interactively?

Yes. Use `--quiet` to suppress prompts and informational output, and add
`--yes` to destructive verbs like `format`. Branch on the documented
**exit codes** (see `tapecon docs concepts`) rather than parsing log
output.

## How do I cancel a running operation?

Press `Ctrl+C`. The first press requests cooperative cancellation —
the current file finishes, agents unwind, and the TOC is preserved. A
second press performs a hard abort.

## What does the set index actually mean?

Positive indexes count up from the **oldest** set on the tape: `1` is the
first backup ever written, `2` is the second, and so on. Zero is always
the **latest** set; `-1` is one before that, `-2` two before, etc. So
`tapecon restore 0` always means “restore the most recent set.”

## My backup is taking forever — can I see progress?

Both `backup` and `restore` show a progress bar with the current file and
percent complete. In `--quiet` mode the bar is suppressed but per-file
log entries are still written.

## How do I exclude files from a backup?

Use FCL on the command line, either inline or via `--filter-file`:

```
tapecon backup C:\Project not (path matches "**\bin\**" or path matches "**\obj\**")
```

You can also keep the rule in a file:

```
tapecon backup C:\Project --filter-file .\exclude-build.fcl
```

## Can I restore an older version of a file from a chain?

Yes. Use `tapecon restore N` (where `N` is a positive index pointing at
the set you want) and pass `--incremental false` to disable the chain
roll-up. Incremental restore from `0` (latest) gives you the most recent
version of every file across the whole chain.

## What's the difference between `validate` and `verify`?

- `validate` re-reads the on-tape data and checks the recorded per-file
  hash. It does **not** compare against the on-disk source.
- `verify` compares the on-tape file with the on-disk file
  byte-for-byte. It requires the source files to still be present.

## How do I move a TOC between two tapes?

Use `tapecon toc export <file>` on the source tape and `tapecon toc
import <file>` on the destination. This is also the rescue path if a
TOC partition becomes unreadable.

## Can I use `tapecon` against a virtual drive for tests?

Yes — that is exactly how the integration tests run. Use `--virtual
<path>` (file-backed) or `--in-memory` (process-local). Format with
`--capacity` and, if you want a separate TOC partition, `--initiator
<path>` plus `--init-capacity`.

## How does this relate to TapeWinNET?

`tapecon` and `TapeWinNET` share the same backup/restore engine
(`TapeLibNET.Services`). Anything you back up with one tool is fully
restorable with the other.
