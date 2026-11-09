---
id: ui.tree-view
title: Tree View
kind: ui-map
host: MainWindow
keywords: [tree, navigation, drive, tape, backup sets, set index, remote, locked, icons, series TOC]
intents:
  - "what is the tree view"
  - "navigate drives and sets"
  - "what do the set numbers mean"
  - "what do the set icons mean"
related:
  - concepts.backup-sets
  - ui.main-window
---

# Tree View

The tree on the left navigates your storage hierarchy:

```
Drive
└─ Tape (media)
   └─ Backup Sets (newest first)
```

- Selecting an item shows its details in the content pane.
- **Backup sets** are listed newest-first and carry dual index numbers — see
  [Backup sets](help://topic/concepts.backup-sets) for what #1 and 0 / -1 mean.
- **Remote** drives are shown in green with a tooltip giving the host, server name, and version — see
  [Remote service](help://topic/concepts.remote-service).
- Right-click items for context actions such as **Rename**.

Use **View → Show Incremental Sets** to include or hide the
[incremental chain](help://topic/concepts.incremental-backup) members.

## Set icons

Overlays on a set's icon show its kind: 🔒 / 🔓 encrypted (locked / unlocked), ⊕ incremental, ← continued from the
previous volume, → continues on the next volume. A [locked set](help://glossary/locked-set) is shown grey as **encrypted
set**; double-click it to unlock. See [Encrypted backup sets](help://topic/concepts.encryption).

## Tape node markers

- **[recovered TOC]** — the TOC was recovered by Scan Media (read-only).
- **[series TOC — reread to edit]** — after a multi-volume restore the drive holds another volume than the TOC shown;
  see [Multi-volume backups](help://topic/concepts.multi-volume).

After an operation the tree keeps the set you were looking at, if it still exists; a backup shows the tape, with its new
set.
