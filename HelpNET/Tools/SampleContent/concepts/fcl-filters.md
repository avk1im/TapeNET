---
id: concepts.fcl-filters
title: File filter language (FCL)
kind: concept
keywords: [FCL, filter, file conditions, exclude, include, pattern]
intents:
  - "how do I filter files"
  - "exclude files from backup"
  - "what is FCL"
  - "file conditions language"
related:
  - quickstart.backup
  - concepts.backup-sets
---
# File filter language (FCL)

**FCL** (File Conditions Language) is a small domain-specific language for
expressing file selection criteria. You can type an FCL expression in the
filter pane to control exactly which files are included in or excluded from a
backup or restore operation.

## Basic syntax

```fcl
name ends-with ".tmp" or name ends-with ".log"
```

The expression above excludes all `.tmp` and `.log` files.

## Operators

| Operator | Meaning |
|----------|---------|
| `name contains "text"` | File name contains the given text |
| `name ends-with ".ext"` | File name ends with extension |
| `size > 100 MB` | File is larger than 100 MB |
| `modified before 2024-01-01` | Modified before a date |
| `attr has Hidden` | File has the Hidden attribute |

Combine conditions with `and`, `or`, and `not`.
