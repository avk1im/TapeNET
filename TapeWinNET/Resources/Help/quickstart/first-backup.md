---
id: quickstart.first-backup
title: Your First Backup
kind: quickstart
keywords: [backup, start, new backup, tape, first]
intents:
  - "how do I back up files"
  - "create a backup"
  - "start a backup"
---

# Your First Backup

This walkthrough takes you from a blank tape to a completed backup in a few steps.

## Before You Begin

- Connect and power on your tape drive.
- Load a tape that is either blank or that you are happy to overwrite.

## Step 1 — Open a Drive

1. Click **File → Open Drive** (or press `Ctrl+D`) and select your tape drive from the list.
2. TapeWin reads the tape header.  The tape appears in the tree on the left.

## Step 2 — Start a New Backup

1. Click **Backup → New Backup…** (or the toolbar **New Backup** button).
2. In the [New Backup dialog](help://topic/dialog.backup), add the folders or files you want to back up.
3. Optionally set a [file filter](help://topic/concepts.fcl-filters) to include or exclude files by name, size, or date.
4. Click **Start Backup**.

## Step 3 — Monitor Progress

The progress bar and current-file readout update in real time.  You can
[abort](help://action/abort-backup) at any point; the partial backup set will be discarded.

## Step 4 — Done

When the progress bar reaches 100 %, TapeWin writes the tape catalog.
The new backup set appears in the tree under your tape.

**Next:** [Restoring files](help://topic/quickstart.restore-files)
