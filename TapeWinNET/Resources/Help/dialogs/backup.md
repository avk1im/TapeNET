---
id: dialog.backup
title: New Backup
kind: dialog
host: BackupWindow
keywords: [backup, new backup, sources, add files, add folder, incremental, append, overwrite, media description, compression, software compression, hardware compression, compression level]
intents:
  - "how do I create a backup"
  - "how do I start a new backup"
  - "add files to a backup"
  - "back up a folder to tape"
  - "make an incremental backup"
  - "compress my backup"
related:
  - concepts.backup-sets
  - concepts.incremental-backup
  - concepts.fcl-filters
  - dialog.restore
ai_excerpt: true
---

# New Backup

This dialog builds a new **backup set** and writes it to the loaded tape.  You
choose the source files and folders, optionally filter them, set a few options,
and click **Start Backup**.

## Sources (toolbar and list)

The toolbar at the top adds content to the backup:

- **Add Files…** — pick individual files to include.
- **Add Folder…** — add an entire folder; combine with **Include subfolders**
  (below) to capture the whole tree.
- **Patterns** + **`+`** — type one or more DOS-style wildcards
  (e.g. `*.docx; *.xlsx`) and click **+** to add them as a pattern source.
- **Remove Unchecked** — drops every source that is currently unticked.

You can also **drag files and folders** straight onto the *Files* pane.  The
**Sources** list shows each source with its selected-file count and size.

## Files pane

Selecting a source shows its files in the right-hand **Files** pane (Name,
Size, Modified, Path).  Tick the files you want; the checkbox state feeds the
**Preview** totals.

### File filter

The file-filter strip lets you narrow the file list as you type, in two modes:

- **Pattern mode** — semicolon-separated wildcards (`*`, `?`).
- **Advanced mode** — a full [FCL filter](help://topic/concepts.fcl-filters)
  built in the [Advanced Filter editor](help://topic/dialog.fcl-filter).

See [FCL file filters](help://topic/concepts.fcl-filters) for the filter language.

## Description

A free-text **Description** stored with the set so you can identify it later in
the tree and in restore dialogs.

## Options

| Option | Effect |
|--------|--------|
| **Include subfolders** | Recurses into sub-directories of every folder source. |
| **Incremental backup** | Backs up only files changed since the last backup — see [Incremental backup](help://topic/concepts.incremental-backup). |
| **Block size** | The tape block size used for this set.  Larger blocks can improve throughput. |
| **Hash algorithm** | The checksum stored per file, used later by **Validate** and **Verify**. |
| **Skip all errors** | Silently ignores file-level read errors instead of prompting. |
| **No multivolume** | Stops the backup at end-of-media instead of prompting for another volume. |

### Compression

The **Compression** drop-down selects how data is compressed before it is
written to tape:

- **None** — data is written uncompressed (fastest, largest).
- **Hardware** — the tape drive performs compression itself, with no CPU cost
  on the host.
- **Software** — TapeWin compresses data before writing, which usually achieves
  a higher ratio than hardware compression at the cost of host CPU time.

When **Software** is selected, a **Compression level** slider appears, ranging
from **1** (fastest, lowest ratio) to **19** (slowest, highest ratio).  The
colored bands give a quick sense of the trade-off, and the preset buttons jump
to representative levels:

| Preset | Trade-off |
|--------|-----------|
| **Fast** | Minimal CPU, modest size reduction. |
| **Balanced** | A good compromise for everyday backups. |
| **High** | Smallest output, noticeably more CPU and time. |

## Media

Controls how the new set is placed on the tape:

- **Append after set** — adds the new set after an existing one, preserving
  earlier sets.
- **Overwrite entire media** — erases all existing sets and starts fresh.
- **Media description** — a name for the whole tape (set when formatting or on
  first write).

A warning panel appears here if the chosen media action needs your attention
(for example, overwriting a tape that already holds data).

## Scanning

- **Auto-scan when adding sources** — counts files and sizes automatically as
  you add sources.
- **Scan All Now** — forces a rescan of every source on demand.

## Preview

Live totals for the current selection: **Total files**, **Total size**,
**Selected files**, and **Selected size**.

## Starting the backup

Click **Start Backup** to begin.  Progress is reported in the main window log
pane, and you can stop a running backup with **Abort Backup**.

> Tip: you can launch this dialog any time with
> [Start a new backup](help://action/new-backup).
