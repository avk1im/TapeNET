---
id: ui.log-pane
title: Log Pane
kind: ui-map
host: MainWindow
keywords: [log, messages, filter, severity, timestamps, save, mirror, copy, auto-scroll]
intents:
  - "what is the log pane"
  - "filter the log"
  - "save the log to a file"
  - "turn off auto-scroll"
related:
  - ui.main-window
---

# Log Pane

The log pane at the bottom records every operation with a severity icon, colour,
and timestamp.  Manage it from the **Log** menu or the context menu:

- **Auto-scroll** — follow new entries automatically; turn it off to read back
  through history while an operation runs.
- **Show Timestamps** — toggle the `[HH:mm:ss]` prefix.
- **Filter Log** — show or hide entries by severity (Info, Completed, Warning,
  Error) and sub-detail; the header shows *visible / total* when filtered.
- **Save Log to File…** — export to text or CSV (chosen by file extension).
- **Mirror Log to File** — stream every new entry to a file in real time.
- **Copy** — select entries and press **Ctrl+C**.
- **Clear Log** — empty the pane.

The pane keeps up to 10,000 entries, pruning the lowest-severity ones first so
errors and warnings are preserved longest.
