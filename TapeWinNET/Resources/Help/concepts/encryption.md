---
id: concepts.encryption
title: Encrypted Backup Sets
kind: concept
keywords: [encryption, encrypted, password, locked, unlocked, unlock, key, AES, forget passwords, change password]
intents:
  - "how do I encrypt a backup"
  - "what does locked mean"
  - "how do I unlock a backup set"
  - "I forgot my password"
  - "change the password of a backup set"
related:
  - dialog.backup
  - dialog.password
  - dialog.rename-backup-set
  - concepts.backup-sets
ai_excerpt: true
---

# Encrypted Backup Sets

A [backup set](help://glossary/backup-set) can be **encrypted** with a password when you create it. Its files — names,
contents, dates — and its description are then unreadable without that password, on the tape and in any
[TOC](help://glossary/toc) file exported from it. TapeWin uses AES-256-GCM, performed by the computer (*Software*
encryption).

## Locked and unlocked

When a tape is loaded, its encrypted sets are [locked](help://glossary/locked-set): the tree and the set list show them
grey, with a closed padlock and the placeholder name **encrypted set**. You can still see what is not secret — how many
sets there are, their size on tape and the volume they are on.

To look inside, **unlock** the set: click **Unlock…** in the files pane, double-click the set, or use
**Media ▸ Unlock Encrypted Sets…**. Enter the password once; every set it fits opens with it — the whole
[incremental chain](help://glossary/incremental-chain), and any other set protected by the same password. An unlocked set
shows an open padlock.

Restoring a locked set asks for its password **before** the Restore window opens; sets you do not unlock are simply left
out.

## Passwords during the session

TapeWin keeps the opened keys — never the passwords — while it runs, also when you swap tapes: the same tape inserted
again, or another volume of the same series, opens without asking. **Media ▸ Forget Passwords** locks everything again;
closing TapeWin does the same.

After three wrong passwords in a row, each further attempt has to wait a little longer (up to a minute) — the field shows
the countdown.

## Chains share one password

An [incremental](help://topic/concepts.incremental-backup) set continues its base set's encryption and password: you
type the base set's password when creating it. A new, independent set may use a new password, or reuse the password of
an existing set — see [New Backup](help://topic/dialog.backup).

**Changing the password** is part of [Rename Backup Set](help://topic/dialog.rename-backup-set). It applies to the whole
chain on this volume. Other volumes of a [multi-volume](help://glossary/multi-volume) series and older exported TOC files
keep the old password.

## What encryption does not do

- **A lost password cannot be recovered.** There is no back door. Keep the password safe, and keep an exported TOC.
- **Deleting needs no password.** Anyone with the tape can erase it — encryption protects confidentiality, not the tape.
- **Hardware compression is not available** with software encryption: encrypted data does not compress, so TapeWin
  compresses in software before encrypting.

## Icons at a glance

| Overlay | Meaning |
|---|---|
| 🔒 bottom-right | encrypted, locked |
| 🔓 bottom-right | encrypted, unlocked |
| ⊕ bottom-left | incremental set |
| ← top-left | continued from the previous volume |
| → top-right | continues on the next volume |
