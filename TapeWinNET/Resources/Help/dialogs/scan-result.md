---
id: dialog.scan-result
title: Scan Media Result
kind: dialog
host: ScanResultWindow
keywords: [scan result, fragment list, toc, recover toc, adopt toc, save map, unclosed set, damaged]
intents:
  - "how do I read the scan result"
  - "use a recovered table of contents"
  - "save the scan map"
related:
  - dialog.scan-media
  - concepts.backup-sets
ai_excerpt: true
---

# Scan Media Result

The banner summarizes the scan; the list below shows every fragment found, in tape order.

## Fragment list

Amber rows are suspicious (for example a backup set that was never completed); red rows are damaged
blocks. Select a **table of contents** row to act on it.

- **Use this TOC** - makes that copy the current table of contents and refreshes the tree. If the copy
  was not recovered during the scan, it is read from the tape first.
- **Save as...** - saves that copy to a file without adopting it.

## Advice buttons

Suggested follow-ups, most useful first. **Save map...** stores the whole map as a file.
