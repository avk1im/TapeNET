---
id: concepts.virtual-drives
title: Virtual Drives
kind: concept
keywords: [virtual drive, virtual media, file-backed, in-memory, emulation, testing]
intents:
  - "what is a virtual drive"
  - "back up without a tape drive"
  - "use a file as a tape"
---

# Virtual Drives

A **virtual drive** emulates a tape drive using ordinary storage — a file on
disk or a block of RAM — instead of physical tape hardware.  To TapeWin it
behaves exactly like a real drive: you format it, write backup sets, and
restore from it.

## Why use one

- **No hardware required** — try out backup and restore workflows on any PC.
- **Testing and demos** — reproduce scenarios quickly and safely.
- **Archiving to disk** — keep a file-backed virtual tape as a portable
  archive.

## File-backed vs in-memory

| Type | Stored | Survives restart |
|------|--------|------------------|
| **File-backed** | One (or two) files on disk | Yes |
| **In-memory** | RAM only | No — discarded when closed |

File-backed virtual media uses a content file and, optionally, a separate
**initiator-partition** file for the Table of Contents.

## Opening one

Use **File → Open Virtual Drive…** to mount a virtual drive — see the
[Open Virtual Drive dialog](help://topic/dialog.open-virtual-drive).  For a
virtual tape hosted on another machine, see
[Remote service](help://topic/concepts.remote-service).
