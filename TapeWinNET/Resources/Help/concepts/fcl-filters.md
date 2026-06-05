---
id: concepts.fcl-filters
title: FCL File Filters
kind: concept
keywords: [FCL, filter, include, exclude, file condition, size, date, attribute]
intents:
  - "how do I filter files"
  - "what is FCL"
  - "include only certain files"
  - "exclude files by extension"
---

# FCL File Filters

[FCL](help://glossary/fcl) (File Conditions Language) is TapeWin's built-in DSL for selecting
which files to include in a backup or restore operation.

## Basic Syntax

An FCL expression is a series of **conditions** separated by semicolons:

```
name ends-with ".log"; size > 1MB; modified after 2024-01-01
```

All conditions must match (implicit AND).  Use `OR` explicitly:

```
name ends-with ".log" OR name ends-with ".tmp"
```

## Common Conditions

| Condition | Example |
|-----------|---------|
| Name match | `name ends-with ".bak"` |
| Path contains | `path contains "Temp"` |
| Size | `size > 10MB`, `size <= 500KB` |
| Date modified | `modified after 2024-06-01` |
| Date created | `created before 2023-01-01` |
| Attributes | `attribute hidden`, `attribute not archive` |

## Negation

Prefix any condition with `NOT`:

```
NOT name ends-with ".exe"
```

## Saving Filters

Filters applied during backup are saved with the backup set and restored
automatically when you navigate back to that set.

**See also:** [Incremental backup](help://topic/concepts.incremental-backup)
