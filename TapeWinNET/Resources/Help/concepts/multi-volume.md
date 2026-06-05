---
id: concepts.multi-volume
title: Multi-Volume Backups
kind: concept
keywords: [multi-volume, volume, end of media, span, continue, swap tape]
intents:
  - "what happens when the tape is full"
  - "back up across multiple tapes"
  - "multi-volume restore"
  - "span a backup over volumes"
---

# Multi-Volume Backups

When a backup is larger than the tape it is being written to, TapeWin spans the
operation across [multiple volumes](help://glossary/multi-volume) (cartridges).

## How a backup continues

1. The backup runs until the tape reaches **end of media**.
2. TapeWin pauses and prompts you to load the next volume.
3. After you insert (or create) a new volume, the backup **resumes** where it
   left off.

The same flow applies to restore: if a needed set lives on another volume,
TapeWin prompts you to load it and then continues.

## Tips

- Label your volumes in order — you will be asked for them in sequence.
- The **No multivolume** option in the [New Backup dialog](help://topic/dialog.backup)
  stops at end-of-media instead of spanning, if you prefer a single-tape
  backup.
- Multi-volume works for [remote](help://topic/concepts.remote-service) drives
  too, prompting you to choose or create the next remote volume.
