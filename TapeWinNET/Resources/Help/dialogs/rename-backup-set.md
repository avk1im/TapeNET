---
id: dialog.rename-backup-set
title: Rename Backup Set
kind: dialog
host: RenameBackupSetWindow
keywords: [rename, backup set, description, change password, current password, new password]
intents:
  - "rename a backup set"
  - "change the password of a backup set"
  - "change the description of a set"
related:
  - concepts.backup-sets
  - concepts.encryption
ai_excerpt: true
---

# Rename Backup Set

Changes a [backup set](help://glossary/backup-set)'s description — and, for an
[encrypted](help://topic/concepts.encryption) set, its password. The change is written to the tape's
[TOC](help://glossary/toc) at once.

## Encrypted sets

The **current password** is always required. It is checked as soon as you leave the field: a wrong one shows in red, and
everything you typed stays. For a locked set the check also unlocks it — its name, hidden until then, fills in.

Open **Change password** to set a new one; type it twice. The warning panel then reminds you:

- a lost password cannot be recovered — keep an exported TOC;
- the new password applies to the whole chain — the incremental sets and volume continuations this set belongs to;
- afterwards the set locks; the new password opens it;
- on a multi-volume series, the other volumes keep the old password in their own TOC copies.

Renaming is not available while the tree shows a series TOC (after a restore that ended on another volume) — reread the
media first.

## Controls

**Current password** — Required for an encrypted set; checked when the field loses focus. Unlocks a locked set.

**Name** — The set's new description. Disabled while the set is locked.

**Change password** — Expands the fields for a new password, typed twice. The new password covers the whole chain.

**Warning panel** — What still needs doing, and what the password change will do.

**OK button** — Writes the new name and password to the tape's TOC.
