---
id: ui.menus
title: Menus
kind: ui-map
host: MainWindow
keywords: [menus, file, drive, media, backup, restore, view, log, help, unlock, forget passwords]
intents:
  - "what is in the menus"
  - "where is the format command"
  - "list of menu commands"
  - "where do I unlock encrypted sets"
related:
  - ui.main-window
---

# Menus

A quick map of the main menu bar:

| Menu | Key commands |
|---|---|
| **File / Drive** | Open Drive, [Open Virtual Drive…](help://topic/dialog.open-virtual-drive), Recent Virtual Drives, [Connect to Remote Host](help://topic/dialog.connect-remote-host), Virtual IO Speed, Exit |
| **Media** | Rename, Reread Media, Export / Import TOC, Eject, [Scan Media…](help://topic/dialog.scan-media), [Delete Backup Sets…](help://topic/dialog.delete-backup-sets), [Format…](help://topic/dialog.format-media), **Unlock Encrypted Sets…**, **Forget Passwords** |
| **Backup** | [New Backup…](help://topic/dialog.backup), Delete Backup Sets…, Abort Backup |
| **Restore** | [Restore / Validate / Verify](help://topic/dialog.restore), Rename, Abort Operation |
| **View** | Refresh (F5), Show Full Pathname, Show Incremental Sets, Show Usage Bar |
| [**Log**](help://topic/ui.log-pane) | Auto-scroll, Timestamps, Filter, Save, Mirror, Clear |
| **Help** | Help Topics (F1), Manage AI Providers, Reset AI Providers, [About](help://topic/dialog.about) |

The two encryption commands appear when the tape holds [encrypted](help://topic/concepts.encryption) sets, or passwords
are held. **Unlock** follows the selection — *Unlock set #3 | 0…*, *Unlock 2 sets…* — or unlocks all locked sets when
nothing is selected.

## Context menus

- **Tape node** — Rename, Reread Media, Restore All Sets, and the two encryption commands when they apply.
- **Backup set (tree, set list, file list)** — Restore / Validate / Verify for the selection, **Rename Set…** (F2) for the
  set clicked, and **Unlock…** for a locked set.
