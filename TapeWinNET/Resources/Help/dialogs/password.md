---
id: dialog.password
title: Enter Password
kind: dialog
host: PasswordDialog
keywords: [password, unlock, encrypted, wrong password, caps lock, try again]
intents:
  - "enter the password of a backup set"
  - "wrong password"
  - "why do I have to wait"
related:
  - concepts.encryption
ai_excerpt: true
---

# Enter Password

Asks for the password of one or more [encrypted](help://topic/concepts.encryption) backup sets.

## Unlocking

Opened by **Unlock…** (files pane, context menu, double-click on a locked set) or by **Media ▸ Unlock Encrypted Sets…**.
The password is checked right away and tried on **every** locked set — it opens all sets it fits. If some sets need
another password, the dialog stays open and says so (amber); type the next one.

## During an operation

A restore, a rename or a backup may ask for a password itself — for example for a set on another volume. Then the dialog
closes when you click **OK**, and TapeWin checks the password; if it is wrong, the dialog appears again with the error
and the attempt count. **Cancel** skips that set; the rest of the operation goes on.

## Wrong passwords

After three wrong passwords in a row, the next attempt must wait — 5 s, then longer, up to a minute. The field counts
down and **OK** stays disabled. The wait applies to every password field in TapeWin.

The warning panel above the buttons shows Caps Lock and, for a multi-volume series, a reminder that a volume's own TOC may
still use an old password.

## Controls

**Password** — The password field. A red message under it reports a wrong password; amber reports that it opened some
sets but not all; ⏳ counts down the wait after repeated failures.

**Warning panel** — Caps Lock, the attempt count, and notes about the sets being unlocked.

**OK button** — Checks the password (or hands it to the running operation). Disabled while the field is empty or a wait
is running.
