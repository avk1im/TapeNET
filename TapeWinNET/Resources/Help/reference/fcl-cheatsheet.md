---
id: reference.fcl-cheatsheet
title: FCL Cheat Sheet
kind: reference
keywords: [FCL, cheat sheet, reference, fields, operators, examples, syntax]
intents:
  - "FCL syntax reference"
  - "FCL fields and operators"
  - "filter examples"
related:
  - concepts.fcl-filters
  - dialog.fcl-filter
---

# FCL Cheat Sheet

A quick reference for the **File Conditions Language**.  See
[FCL file filters](help://topic/concepts.fcl-filters) for the full guide.

## Fields

| Field | Matches |
|-------|---------|
| `Name` | File name (no path) |
| `Extension` | File extension |
| `Path` | Full path |
| `FullName` | Full path + name |
| `Size` | File size |
| `Created` | Creation date/time |
| `Modified` | Last-write date/time |
| `Attributes` | File attributes |

## Operators

| Type | Operators |
|------|-----------|
| String | `equals` `contains` `matches` `notMatches` `regex` |
| Size | `greaterThan` `lessThan` `==` `!=` `<` `>` `<=` `>=` |
| Date | `before` `after` `beforeOrOn` `afterOrOn` |
| Attributes | `have` `notHave` |

## Values

- **Strings** — quoted or unquoted: `report`, `"my file.txt"`.
- **Sizes** — with units: `10MB`, `1.5GB`.
- **Dates** — absolute (`2024-01-31`) or relative (`today-7d`, `now-2h`).
- **Attributes** — `Hidden`, `ReadOnly`, `System`, `Archive`, `Temporary`.

## Examples

```
Name matches "*.docx; *.xlsx"
Size greaterThan 10MB and Modified after today-7d
Extension equals doc or docx or txt
not (Attributes have Hidden)
```

## Sugar

- **Semicolons** — multiple patterns: `Name matches "*.doc; *.txt"`.
- **Value chains** — `Extension equals doc or docx or txt`.
- **Comments** — lines starting with `//`.
