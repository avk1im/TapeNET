---
id: dialog.scan-media
title: Scan Media
kind: dialog
host: ScanMediaWindow
keywords: [scan, scan media, survey, fragment map, damaged tape, recover table of contents, toc, map]
intents:
  - "how do I scan a tape"
  - "what is on this cartridge"
  - "my tape has no table of contents"
  - "recover a lost table of contents"
related:
  - dialog.scan-result
  - concepts.backup-sets
ai_excerpt: true
---

# Scan Media

Reads the cartridge from the beginning and lists everything that can be identified on it - without
needing a table of contents. **Nothing is written to the tape.**

## Options

- **Try to recover the table of contents** - while scanning, reads any table-of-contents copies found
  so they can be adopted afterwards.
- **Save scan map to** - optionally saves the resulting map as a file in the chosen folder. You can
  also save it later from the result window.

While scanning, use **Abort Scan** to stop; the partial map found so far is still shown.
