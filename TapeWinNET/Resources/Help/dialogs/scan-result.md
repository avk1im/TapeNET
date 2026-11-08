---
id: dialog.scan-result
title: Scan Media Result
kind: dialog
host: ScanResultWindow
keywords: [scan result, fragment list, toc, recover toc, adopt toc, save map, unclosed set, damaged, encrypted]
intents:
  - "how do I read the scan result"
  - "use a recovered table of contents"
  - "save the scan map"
related:
  - dialog.scan-media
  - concepts.backup-sets
  - concepts.toc-space
ai_excerpt: true
---

# Scan Media Result

The banner summarizes the scan; the list below shows every fragment found, in tape order.

## Fragment list

Amber rows are suspicious (for example a backup set that was never completed); red rows are damaged blocks. Select a
**table of contents** row to act on it.

- **Use this TOC** - makes that copy the current table of contents and refreshes the tree. If the copy was not recovered
  during the scan, it is read from the tape first.
- **Save as...** - saves that copy to a file without adopting it.

### Encrypted sets

Scanning needs no password, and reveals nothing an encrypted set hides:

- an encrypted set's header shows as **🔒 encrypted** with its method, without a name;
- a set without a set header starts with encrypted data, which shows as **unidentified** — not damaged;
- a recovered TOC lists its encrypted sets, e.g. *recovered — 3 set(s), 1 encrypted*. Once you **Use this TOC**, they
  appear locked in the main window; unlock them there.

## Advice buttons

Suggested follow-ups, most useful first. **Save map...** stores the whole map as a file.

## Controls

**Result banner** — Summary of the scan outcome.

**Fragment list** — Every fragment found, in tape order. Amber rows are suspicious and red rows are damaged.

**TOC actions** — For a selected table-of-contents row: **Use this TOC** or **Save as...**.

**Advice** — Suggested follow-up actions, plus **Save map...**.
