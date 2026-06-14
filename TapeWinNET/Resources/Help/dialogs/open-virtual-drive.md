---
id: dialog.open-virtual-drive
title: Open Virtual Drive
kind: dialog
host: OpenVirtualDriveWindow
keywords: [virtual drive, virtual media, file-backed, in-memory, capacity, block size, preset, initiator partition]
intents:
  - "how do I open a virtual drive"
  - "create a virtual tape"
  - "use a file as a tape"
  - "test without hardware"
related:
  - concepts.virtual-drives
  - dialog.format-media
ai_excerpt: true
---

# Open Virtual Drive

A **virtual drive** emulates a tape drive using a file on disk (or RAM), so you
can back up and restore without physical tape hardware.  See
[Virtual drives](help://topic/concepts.virtual-drives) for the concept.

## Mode

- **Open existing virtual media** — point at a virtual-media file you created
  earlier.  Its configuration is read from the file.
- **Create new virtual media** — build a fresh virtual tape with the settings
  below.
- **In-memory** — a throw-away virtual tape held entirely in RAM (nothing is
  written to disk); ideal for quick tests.

## File Paths

The content file (and an optional separate initiator-partition file).  Click
**…** to browse.  A probe indicator shows whether the chosen file is a valid,
openable virtual tape.

- **Enable Initiator Partition** — store the Table of Contents in a separate
  partition file.

## Preset Configuration

Choose a ready-made profile (capacity, features, block sizes) and click
**Apply** to fill in the fields below — a quick starting point you can then
tweak.

## Media Description

A name for the virtual tape, stored in its TOC.

## Capacity

The simulated tape size, with a units selector (MB / GB / …).

## Features

- **Supports Setmarks** — enables setmark-based partitioning.
- **Supports Seq. Filemarks** — enables sequential-filemark layout.

These determine how sets are delimited on the virtual tape.

## Block Sizes

The minimum / maximum block sizes the virtual drive reports.

## Confirming

Click the action button (its label reflects **Open** or **Create**) to mount
the virtual drive, or **Cancel** to abort.

> Shortcut: [Open a virtual drive](help://action/open-virtual-drive).

## Controls

**Drive mode** — Chooses between opening an existing virtual media file or creating a new one. In-memory mode creates a temporary media backed by RAM (no files on disk).
**File paths** — The content-partition file path (required). The initiator-partition file is auto-computed from the content path when enabled.
**Preset configuration** — One-click presets for common virtual drive setups (e.g. LTO-8 equivalent). Applying a preset populates Capacity, Features, and IO Speed.
**Capacity** — Sets the logical size of the content and initiator partitions for new media. Ignored when opening an existing file.
