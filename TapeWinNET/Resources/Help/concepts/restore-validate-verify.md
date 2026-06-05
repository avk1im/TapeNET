---
id: concepts.restore-validate-verify
title: Restore, Validate, and Verify
kind: concept
keywords: [restore, validate, verify, integrity, checksum, compare]
intents:
  - "what is the difference between restore validate verify"
  - "how do I check backup integrity"
  - "validate backup"
---

# Restore, Validate, and Verify

TapeWin provides three operations for working with backed-up data:

## Restore

Reads files from tape and writes them to a destination folder on disk.
Use Restore when you actually need the files back.

**[→ How to restore files](help://topic/quickstart.restore-files)**

## Validate

Reads each block from tape and verifies the **checksums** stored in the tape
catalog, without writing anything to disk.  Use [Validate](help://glossary/validate) to confirm the tape
is readable and the data has not been corrupted.

- Faster than Restore (no disk I/O).
- Does **not** compare against the original source files.

## Verify

Reads files from tape and **compares them byte-for-byte** with the original
source files (which must still be accessible on disk).  Use [Verify](help://glossary/verify) immediately
after a backup to confirm that what was written matches what is on disk.

| Operation | Reads tape | Writes disk | Compares to source |
|-----------|-----------|-------------|-------------------|
| Restore   | ✓ | ✓ | — |
| Validate  | ✓ | — | — |
| Verify    | ✓ | — | ✓ |

## Recommendations

- Run **Verify** right after a critical backup.
- Run **Validate** periodically on tapes stored long-term.
- Use **Restore** to a temp folder for a full round-trip check.
