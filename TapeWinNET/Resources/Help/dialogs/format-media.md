---
id: dialog.format-media
title: Format Media
kind: dialog
host: FormatMediaWindow
keywords: [format, erase, initiator partition, TOC, media description, blank tape]
intents:
  - "how do I format a tape"
  - "erase a tape"
  - "prepare new media"
  - "what is an initiator partition"
related:
  - concepts.backup-sets
  - dialog.backup
ai_excerpt: true
---

# Format Media

Formatting prepares a tape for use and **erases all existing backup sets**.
Use it for a brand-new cartridge, or to reclaim space after deleting sets.

## Media description

Give the tape a name.  It is stored in the tape's Table of Contents and shown
throughout TapeWin (tree, status bar, restore dialogs).

## Create initiator partition for TOC

When ticked (and supported by the drive), TapeWin creates a small dedicated
**initiator partition** to hold the Table of Contents separately from the data.
This can make TOC reads faster and more robust.  Leave it unticked to keep a
single-partition layout.

## Warning panel

A warning is shown reminding you that formatting is **destructive** — every
backup set currently on the tape will be lost.  Make sure you have what you
need before continuing.

## Formatting

Click **Format** to erase and initialise the media, or **Cancel** to back out.

> You can reach this dialog from
> [Format the loaded media](help://action/format-media).
