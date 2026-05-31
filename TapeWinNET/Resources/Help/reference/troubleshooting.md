---
id: reference.troubleshooting
title: Troubleshooting
kind: reference
keywords: [troubleshooting, problems, errors, help, fix, drive not found, remote]
intents:
  - "something went wrong"
  - "common problems"
  - "drive not detected"
  - "cannot connect to remote host"
related:
  - dialog.connect-remote-host
  - ui.log-pane
---

# Troubleshooting

A few common situations and what to check.

## No drive is detected

- Confirm the tape drive is powered on and connected.
- Try **Drive → Open Drive** and select the drive number explicitly.
- No hardware?  Use a [virtual drive](help://topic/concepts.virtual-drives)
  instead.

## Cannot connect to a remote host

- Verify the **host** and **port** (default 50551, or 50552 for TLS).
- Make sure the TapeNET service is running on the server and the port is open
  in the firewall.
- If using TLS, confirm the server has TLS enabled —
  see [Connect to Remote Host](help://topic/dialog.connect-remote-host).

## Restore reports missing files or wrong volume

- The set may live on another tape — load the requested
  [volume](help://topic/concepts.multi-volume) when prompted.
- For incremental backups, enable **Include incremental chain** in the
  [Restore dialog](help://topic/dialog.restore).

## Files fail during an operation

- Check the [log pane](help://topic/ui.log-pane) for the specific error.
- Choose **Skip / Retry / Abort** when prompted, or enable **Skip all errors**
  for unattended runs.

## A backup stops at end-of-media

- Allow [multi-volume](help://topic/concepts.multi-volume) spanning, or free
  space by [deleting sets](help://topic/dialog.delete-backup-sets) /
  [formatting](help://topic/dialog.format-media).

When in doubt, the [log pane](help://topic/ui.log-pane) is the best source of
detail — you can save or mirror it to a file to share when reporting an issue.
