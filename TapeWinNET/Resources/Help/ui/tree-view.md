---
id: ui.tree-view
title: Tree View
kind: ui-map
host: MainWindow
keywords: [tree, navigation, drive, tape, backup sets, set index, remote]
intents:
  - "what is the tree view"
  - "navigate drives and sets"
  - "what do the set numbers mean"
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
- **Backup sets** are listed newest-first and carry dual index numbers —
  see [Backup sets](help://topic/concepts.backup-sets) for what `#1` and `0 / -1`
  mean.
- **Remote** drives are shown in green with a tooltip giving the host, server
  name, and version — see [Remote service](help://topic/concepts.remote-service).
- Right-click items for context actions such as **Rename**.

Use **View → Show Incremental Sets** to include or hide the
[incremental chain](help://topic/concepts.incremental-backup) members.
