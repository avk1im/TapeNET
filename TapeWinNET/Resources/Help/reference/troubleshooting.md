---
id: reference.troubleshooting
title: Troubleshooting
kind: reference
keywords: [troubleshooting, problems, errors, help, fix, drive not found, remote, password, locked, toc, volume rejected]
intents:
  - "something went wrong"
  - "common problems"
  - "drive not detected"
  - "cannot connect to remote host"
  - "wrong password"
  - "the volume ended early"
related:
  - dialog.connect-remote-host
  - ui.log-pane
  - concepts.toc-space
---

# Troubleshooting

A few common situations and what to check.

## No drive is detected

- Confirm the tape drive is powered on and connected.
- Try **Drive → Open Drive** and select the drive number explicitly.
- No hardware? Use a [virtual drive](help://topic/concepts.virtual-drives) instead.

## Cannot connect to a remote host

- Verify the **host** and **port** (default 50551, or 50552 for TLS).
- Make sure the TapeNET service is running on the server and the port is open in the firewall.
- If using TLS, confirm the server has TLS enabled — see [Connect to Remote Host](help://topic/dialog.connect-remote-host).

## Restore reports missing files or wrong volume

- The set may live on another tape — load the requested [volume](help://topic/concepts.multi-volume) when prompted.
- A cartridge TapeWin cannot identify is rejected: insert it again, **skip** that volume, or end the restore.
- For incremental backups, enable **Include incremental chain** in the [Restore dialog](help://topic/dialog.restore).

## A backup set is shown as "encrypted set"

- It is [locked](help://glossary/locked-set): double-click it, or use **Media ▸ Unlock Encrypted Sets…**, and enter its
  password. See [Encrypted backup sets](help://topic/concepts.encryption).
- **Wrong password, and now I must wait:** after three wrong passwords each attempt waits longer (up to a minute).
- **The password worked last week:** it may have been changed with **Rename** — on another volume of the series the
  old password may still apply.
- **The password is lost:** there is no recovery. The set can only be deleted.

## The table of contents could not be read

- TapeWin searches for it by itself, and asks before searching the whole medium.
- If nothing is found, use [Scan Media](help://topic/dialog.scan-media), or import a TOC file exported earlier. See
  [TOC space](help://topic/concepts.toc-space).

## Files fail during an operation

- Check the [log pane](help://topic/ui.log-pane) for the specific error.
- Choose **Skip / Retry / Abort** when prompted, or enable **Skip all errors** for unattended runs.

## A backup stops at end-of-media

- Allow [multi-volume](help://topic/concepts.multi-volume) spanning, or free space by
  [deleting sets](help://topic/dialog.delete-backup-sets) / [formatting](help://topic/dialog.format-media).
- **A volume ended although it is not full:** its TOC partition is — see [TOC space](help://topic/concepts.toc-space).

When in doubt, the [log pane](help://topic/ui.log-pane) is the best source of detail — you can save or mirror it to a
file to share when reporting an issue.
