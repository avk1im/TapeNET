---
id: ui.file-filter-pane
title: File Filter Pane
kind: ui-map
host: MainWindow
keywords: [filter, file list, pattern, advanced, FCL, all sets, stats]
intents:
  - "what is the file filter pane"
  - "filter the file list"
  - "filter files as I type"
related:
  - concepts.fcl-filters
  - dialog.fcl-filter
---

# File Filter Pane

The file-filter pane narrows the file list shown for a backup set (or in the
New Backup dialog) as you type.  It has two modes:

- **Pattern mode** — semicolon-separated DOS wildcards (`*.doc; *.txt`).
- **Advanced mode** — a full [FCL filter](help://topic/concepts.fcl-filters)
  edited in the [Advanced Filter window](help://topic/dialog.fcl-filter).

Features:

- The group header shows live stats, e.g.
  *Files (1,234 → 567 filtered → 42 selected)*.
- **All sets** — apply the filter across every backup set on the tape, not just
  the current one.
- The filter **persists across tree navigation**, even when temporarily
  disabled, so its definition is not lost.
