---
id: ui.main-window
title: Main Window
kind: ui-map
host: MainWindow
keywords: [main window, layout, tree, content pane, log, status bar, menus]
intents:
  - "what are the parts of the main window"
  - "explain the main window layout"
  - "where is everything"
related:
  - ui.tree-view
  - ui.log-pane
  - ui.menus
  - ui.status-bar
---

# Main Window

The main window is your home base in TapeWin.  It is organised into a few key
areas:

- **[Menu bar](help://topic/ui.menus)** — File, Drive, Media, Backup, Restore,
  View, Log, and Help menus.
- **[Tree view](help://topic/ui.tree-view)** (left) — Drive → Tape →
  Backup Sets navigation.
- **Content pane** (centre) — shows details for the selected item: drive info,
  media info, or backup-set contents.
- **[File filter pane](help://topic/ui.file-filter-pane)** — filters the file
  list as you type.
- **[Log pane](help://topic/ui.log-pane)** (bottom) — a running record of every
  operation.
- **[Status bar](help://topic/ui.status-bar)** — drive, media, and remote
  connection status at a glance.

Press **F1** on any focused control to jump straight to its help topic.

## Controls

**Tree view** — Drive → Tape → Backup Set navigation tree on the left. Expand a drive to see its tapes; expand a tape to see its backup sets.

**Properties pane** — Shows details for the selected item: drive statistics, media properties, or backup-set metadata.

**Content table** — The file list (when a backup set is selected) or the backup-sets table (when a tape is selected). Double-click a backup set to drill into its files.

**Backup sets list** — Table of all backup sets on the current tape. Tick rows to select them for a batch restore. Double-click to view files.

**File filter pane** — Filters the file list as you type; supports [FCL expressions](help://topic/ui.file-filter-pane) for advanced filtering.

**Log pane** — Running record of every operation with severity icons. Expand/collapse with the splitter above it.
