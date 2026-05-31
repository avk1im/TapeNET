---
id: dialog.fcl-filter
title: Advanced Filter (FCL)
kind: dialog
host: FclFilterWindow
keywords: [filter, FCL, advanced filter, conditions, DNF, AND, OR, all sets]
intents:
  - "how do I build an advanced filter"
  - "edit an FCL filter visually"
  - "filter files by size or date"
  - "apply a filter to all sets"
related:
  - concepts.fcl-filters
  - dialog.backup
  - dialog.restore
ai_excerpt: true
---

# Advanced Filter (FCL)

The advanced filter editor builds a full
[FCL file filter](help://topic/concepts.fcl-filters) two ways at once — a
visual condition builder and a text editor — kept in sync.

## Conditions (visual editor, left)

Build the filter as groups of conditions in **disjunctive normal form** (DNF):

- Conditions **within a group** are combined with **AND**.
- **Groups** are combined with **OR**.
- **+ Add group (OR)** — starts a new alternative group.

Each condition picks a field (Name, Path, Size, Modified, …), an operator, and
a value.

## Program pane (text editor, right)

Shows the equivalent FCL text.  Edit it directly and use:

- **Update →** — push edits from the text into the visual editor.
- **← Apply** — push the visual editor back into the text.
- **Toggle program pane** — show or hide the text editor.
- **Clear Filter** — empty the filter.

The two views stay synchronised so you can work whichever way suits the task.

## All sets

Tick **all sets** to apply the filter across **every backup set** on the tape,
not just the current one.  Newly visited sets inherit the filter automatically.

## Applying

Click **Apply** to use the filter, or **Cancel** to discard your changes.
