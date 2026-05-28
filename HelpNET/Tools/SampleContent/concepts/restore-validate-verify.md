---
id: concepts.restore-validate-verify
title: Restore, validate, and verify
kind: concept
keywords: [restore, validate, verify, compare, integrity]
intents:
  - "what is the difference between restore and verify"
  - "how do I check tape integrity"
  - "validate backup"
related:
  - quickstart.restore
  - concepts.backup-sets
---
# Restore, validate, and verify

TapeWinNET provides three tape-reading operations:

## Restore

Reads files from a backup set and writes them to a destination folder.
The archive bit of each restored file is cleared.

## Validate

Reads the backup set from tape and compares file checksums against the TOC
metadata. **No files are written to disk.** Use this to confirm that the tape
is readable and that no data has been corrupted.

## Verify

After a backup completes, the verify step re-reads every written file and
compares it against the original on disk. Any mismatch is reported as an error.
Verify adds time but gives the highest confidence that the backup was written
correctly.
