# File selection and FCL

Every verb that works on files (`backup`, `restore`, `validate`, `verify`, `list`) selects them the same way: with
paths, wildcard patterns, and **FCL**, the File Conditions Language. FCL picks files by name, path, size, date and
attributes.

## How tapecon reads your arguments

Each positional argument is classified in this order:

1. An existing file ending in `.fcl` is loaded as an FCL expression.
2. Text that starts with an FCL field name and an operator, or with `not`, `!` or `(`, is an **inline** FCL expression.
3. An existing file or folder is taken as a path.
4. Anything else is a wildcard pattern: `*` matches any run of characters, `?` one character.

`--filter EXPRESSION` and `--filter-file FILE` give FCL explicitly, with no guessing.

## How they combine

Paths and patterns are the **basic** selection; FCL **narrows** it:

- Several paths or patterns: a file is selected when **any** of them matches.
- Every FCL expression, inline or from a file or option, must **also** hold.

So `tapecon restore 0 *.docx *.xlsx "Size > 1MB"` restores the Word and Excel files larger than 1 MB. TapeWinNET combines
its basic filter and its FCL the same way.

**For `backup`**, the paths and patterns are the **sources**: what to back up. FCL decides which of their files go into
the set. On `restore`, `validate`, `verify` and `list`, the set index comes first, then the selection.

## FCL in brief

A condition is a **field**, an **operator** and a **value**:

    Name matches *.docx
    Size greaterThan 10MB
    Modified after today-7d

Conditions combine with `and`, `or` and `not`; parentheses group them. `not` binds tightest, then `and`, then `or`.
Keywords ignore case: `name MATCHES *.docx` works, too.

## Fields

| Field | Matches | Example value |
|---|---|---|
| Name | file name with extension | `report.docx` |
| Extension | extension, **with** the dot | `.docx` |
| Path | folder, without the name | `D:\Data\Projects` |
| FullName | folder and name | `D:\Data\Projects\report.docx` |
| Size | size in bytes | `10MB` |
| Created | creation date and time | `2026-01-31` |
| Modified | last-write date and time | `today-7d` |
| Attributes | attribute flags | `Hidden` |

## Operators

| Fields | Operators | Symbols |
|---|---|---|
| Name, Extension, Path, FullName | `equals` `notEquals` `contains` `notContains` `matches` `notMatches` `regex` | `==` `!=` |
| Size | `equals` `notEquals` `greaterThan` `greaterOrEqual` `lessThan` `lessOrEqual` | `==` `!=` `>` `>=` `<` `<=` |
| Created, Modified | `equals` `notEquals` `before` `beforeOrOn` `after` `afterOrOn` | `==` `!=` `<` `<=` `>` `>=` |
| Attributes | `have` `notHave` | |

- `matches` compares with a wildcard pattern; `regex` with a .NET regular expression. String comparisons ignore case.
- `has` and `notHas` work as `have` and `notHave`.
- `&&` and the double bar work as `and` and `or`; `!` works as `not`.

## Values

- **Strings** need quotes only when they hold spaces or special characters: `Path contains "My Documents"`. A quote
  inside quotes is doubled. Backslashes are plain characters, so Windows paths need no escaping.
- **Sizes** take a unit: `B`, `KB`, `MB`, `GB`, `TB`, all binary (1 KB = 1,024 bytes); `KiB` and friends work, too.
  Decimals and a space are allowed: `1.5GB`, `10 MB`. Without a unit, a size counts bytes.
- **Dates** are absolute, as `2026-01-31` or `2026-01-31T14:30`, or in your system's date format. Or they are
  **relative**: `today`, `yesterday` or `now`, optionally with an offset such as `today-7d`.
- **Offsets** count `d` days, `w` weeks, `m` **months**, `y` years, `h` hours, `min` **minutes**. Relative dates are
  worked out when the filter runs, so a saved filter stays right.
- **Attributes:** `Hidden`, `ReadOnly`, `System`, `Archive`, `Temporary`.

## Shortcuts

- **Semicolons** list several patterns: `Name matches "*.docx; *.xlsx"` means either.
- **Value chains** repeat the field and operator: `Extension equals .doc or .docx or .txt`.
- **Range chains** repeat the field: `Size greaterThan 100KB and lessOrEqual 1MB`.
- **Comments** run from `//` to the end of the line, handy in `.fcl` files.

## Examples

    Name matches "*.jpg; *.png" and Modified after today-30d
    Extension equals .docx or .xlsx or .pptx
    Size greaterThan 1GB or Modified before 2020-01-01
    Path contains Projects and not (Attributes have Hidden or System)
    Modified afterOrOn 2026-01-01 and before 2026-02-01
    Name regex "^invoice-[0-9]{4}\.pdf$"

## On the command line

Put an FCL expression in double quotes. Inside them, write values **without** quotes wherever they hold no spaces:

    tapecon list 0 "Name matches *.docx and Size > 1MB"
    tapecon backup D:\Data --subdirs --filter "Modified after today-7d"
    tapecon restore 0 "Extension equals .docx or .xlsx" --target D:\Restored

A value with spaces needs quotes inside the quotes. Shells disagree on how to pass those: PowerShell 7.3 and later pass
`'Path contains "My Documents"'` correctly, Windows PowerShell 5.1 does not. A **filter file** avoids the question,
and keeps long filters readable. Save this as `D:\Filters\office.fcl`:

    // Office documents of the last month, outside the archive
    Name matches "*.docx; *.xlsx; *.pptx"
    and Modified after today-1m
    and Path notContains "Old Archive"

Then pass the file, as an argument or with `--filter-file`:

    tapecon backup D:\Data --subdirs D:\Filters\office.fcl
    tapecon list 0 --filter-file D:\Filters\office.fcl

## Common mistakes

- **A pattern needs a field inside FCL.** `matches *.docx` alone is no condition; write `Name matches *.docx`.
- **Extensions carry their dot:** `Extension equals .docx`.
- **`m` means months**, `min` minutes: `now-30min`, not `now-30m`.
- **`Name` is the name only.** To match a folder, use `Path` or `FullName`: `FullName matches *\Projects\*`.
