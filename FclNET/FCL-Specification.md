# FCL — File Conditions Language

**Version:** 1.0 Draft  
**Library:** FclNET  
**Part of:** TapeNET Solution

## Overview

FCL (File Conditions Language) is a small domain-specific language for expressing file filtering
criteria. It supports matching on file names, paths, sizes, dates, and attributes using a rich
set of comparison operators and logical connectives.

FCL is **case-insensitive** for keywords (field names, operators, logical connectives).  
In its **normalized (canonical) form**, keywords use the casing specified in this document.

## Grammar

```ebnf
Expression   ::= OrExpr
OrExpr       ::= AndExpr { "or" AndExpr }
AndExpr      ::= NotExpr { "and" NotExpr }
NotExpr      ::= [ "not" ] Primary
Primary      ::= Condition | "(" Expression ")"

Condition    ::= Field Operator Value

Comment      ::= "//" { any character except newline }
```

## Fields

| Field        | Type       | Description                         | Maps to                            |
|-------------|------------|-------------------------------------|------------------------------------|
| `FullName`  | String     | Full path including file name       | `TapeFileDescriptor.FullName`      |
| `Name`      | String     | File name only (with extension)     | `Path.GetFileName(FullName)`       |
| `Extension` | String     | File extension **including the leading dot** (e.g. `.doc`)| `Path.GetExtension(FullName)`|
| `Path`      | String     | Directory path only                 | `Path.GetDirectoryName(FullName)`  |
| `Size`      | Size       | File size in bytes                  | `TapeFileDescriptor.Length`        |
| `Created`   | DateTime   | File creation date/time             | `TapeFileDescriptor.CreationTime`  |
| `Modified`  | DateTime   | Last modification date/time         | `TapeFileDescriptor.LastWriteTime` |
| `Attributes`| Attributes | File attributes flags               | `TapeFileDescriptor.Attributes`    |

## Operators

### String Operators
For use with string fields (`FullName`, `Name`, `Extension`, `Path`).

| Operator       | Aliases          | Description                                    |
|---------------|------------------|------------------------------------------------|
| `equals`      | `==`             | Exact match (case-insensitive)                 |
| `notEquals`   | `!=`             | Not an exact match                             |
| `contains`    |                  | Substring match (case-insensitive)             |
| `notContains` |                  | Does not contain substring                     |
| `matches`     |                  | DOS-style wildcard match (`*`, `?`)            |
| `notMatches`  |                  | Negated DOS-style wildcard match               |
| `regex`       |                  | Full .NET regular expression match             |

### Date/Time Operators
For use with date fields (`Created`, `Modified`).

| Operator       | Aliases          | Description                                    |
|---------------|------------------|------------------------------------------------|
| `equals`      | `==`             | Same date (time portion ignored for date-only) |
| `notEquals`   | `!=`             | Different date                                 |
| `before`      | `<`              | Strictly before                                |
| `beforeOrOn`  | `<=`             | Before or on the same date                     |
| `after`       | `>`              | Strictly after                                 |
| `afterOrOn`   | `>=`             | After or on the same date                      |

### Size Operators
For use with the `Size` field.

| Operator         | Aliases          | Description                                  |
|-----------------|------------------|----------------------------------------------|
| `equals`        | `==`             | Exact size match                             |
| `notEquals`     | `!=`             | Not the exact size                           |
| `greaterThan`   | `>`              | Strictly greater than                        |
| `greaterOrEqual`| `>=`             | Greater than or equal                        |
| `lessThan`      | `<`              | Strictly less than                           |
| `lessOrEqual`   | `<=`             | Less than or equal                           |

### Attribute Operators
For use with the `Attributes` field.

| Operator       | Aliases          | Description                                    |
|---------------|------------------|------------------------------------------------|
| `have`        | `has`            | File has the specified attribute flag          |
| `notHave`     | `notHas`         | File does not have the attribute flag          |

## Value Literals

### String Values

Strings may be **unquoted** or **quoted**:

- **Unquoted:** Valid when the value contains no spaces, parentheses, or FCL keywords.
  Terminates at whitespace, `)`, or end of input.
  ```
  Name equals readme.txt
  Extension equals .doc
  ```

- **Quoted:** Enclosed in double quotes. Required when the value contains spaces or
  special characters. A literal double quote inside a quoted string is represented by
  doubling it (`""`).
  ```
  Path contains "My Documents"
  Name equals "file ""with"" quotes.txt"
  ```

Backslashes are **always literal** — no escape sequences. This is intentional for
Windows path compatibility.

### Semicolon Shortcut for `matches`, `notMatches`, and `regex`

When the operator is `matches`, `notMatches`, or `regex`, the value may contain
patterns. This is syntactic sugar that expands to an OR-chain:

```
Name matches "*.doc; *.txt; *.pdf"
```
is equivalent to:
```
Name matches "*.doc" or Name matches "*.txt" or Name matches "*.pdf"
```

The formatter may re-collapse such OR-chains back to the semicolon shortcut when all
branches share the same field and operator.

Trailing semicolons are silently ignored: `"*.doc; *.txt;"` is treated identically
to `"*.doc; *.txt"`.

### Size Values

Size literals are an integer or decimal number followed by an optional unit suffix
(case-insensitive). Without a suffix, the value is in bytes.

| Suffix | Multiplier       |
|--------|-----------------|
| `B`    | 1               |
| `KB`   | 1,024           |
| `MB`   | 1,048,576       |
| `GB`   | 1,073,741,824   |
| `TB`   | 1,099,511,627,776 |

Whitespace between the number and the unit suffix is allowed. Locale group
separators (e.g. `1,000,000`) are accepted and stripped during parsing.

Examples:
```
Size greaterThan 10MB
Size greaterThan 10 MB
Size lessThan 1.5GB
Size equals 1,048,576
Size equals 0
```

### Date/Time Values

#### Absolute Dates

Dates are accepted in two formats:

1. **ISO 8601** (always accepted, unambiguous):
   ```
   Modified after 2025-01-15
   Modified after 2025-01-15T14:30
   Modified after 2025-01-15T14:30:00
   ```

2. **System locale format** (parsed via the current culture):
   ```
   Modified after 1/15/2025      (en-US)
   Modified after 15.01.2025     (de-DE)
   ```

   When ambiguous, ISO 8601 takes precedence.

The formatter always emits ISO 8601 for unambiguous serialization.

#### Relative Dates

Relative dates are resolved at **evaluation time**, not parse time. This ensures
filters remain semantically correct when re-applied later.

| Literal         | Meaning                          |
|----------------|----------------------------------|
| `today`        | Start of the current day (00:00) |
| `yesterday`    | Start of the previous day        |
| `now`          | Current date and time            |
| `today-Nd`     | N days before today              |
| `today+Nd`     | N days after today               |
| `today-Nw`     | N weeks before today             |
| `today-Nm`     | N months before today            |
| `today-Ny`     | N years before today             |

`now` may also use offsets: `now-2h` (hours), `now-30min` (minutes).

The same offset syntax applies to `yesterday`: `yesterday+12h`.

Whitespace is allowed between the anchor, sign, number, and unit. All of the
following are equivalent:
```
today-7d
today -7d
today - 7d
today - 7 d
```

The compact form (`today-7d`) is lexed as a single token; the spaced-out forms
produce multiple tokens that the parser reassembles. The formatter always emits
the compact form.

Examples:
```
Modified after today-7d
Modified after today - 7 d
Created before today-3m
Modified afterOrOn yesterday
Modified after now-2h
Modified after now - 30 min
```

### Attribute Values

Attribute values are predefined identifiers (case-insensitive):

| Value       | Maps to                          |
|------------|----------------------------------|
| `Hidden`   | `FileAttributes.Hidden`          |
| `ReadOnly` | `FileAttributes.ReadOnly`        |
| `System`   | `FileAttributes.System`          |
| `Archive`  | `FileAttributes.Archive`         |
| `Temporary`| `FileAttributes.Temporary`       |

Example:
```
Attributes have Hidden
not Attributes have ReadOnly
```

### Value Chain Shortcut

When multiple values share the same field and operator, consecutive values may be
chained using `or` or `and` after the first condition. This is syntactic sugar that
expands each chained value into a full condition repeating the original field and
operator.

The chain continues as long as the next token after `or`/`and` is a value — not a
field name (which would start a new condition) or a logical keyword. If a chained
value contains spaces or special characters, it must be quoted.

#### String field chains

```
Extension equals doc or docx or txt
```
is equivalent to:
```
Extension equals doc or Extension equals docx or Extension equals txt
```

The same works with other string operators:
```
Path contains docs or documents
Name matches "important*" or "urgent*"
Name notContains temp and cache
```

The `and` chain creates a conjunction:
```
FullName contains users and documents
```
is equivalent to:
```
FullName contains users and FullName contains documents
```

#### Attribute field chains

```
Attributes have System or Hidden or Temporary
```
is equivalent to:
```
Attributes have System or Attributes have Hidden or Attributes have Temporary
```

The same works with `notHave`:
```
Attributes notHave Hidden or System
```
is equivalent to:
```
Attributes notHave Hidden or Attributes notHave System
```

#### Chain rules

- Only one connective type (`or` or `and`) is allowed per chain — mixing `or`
  and `and` within a single chain is not supported (use the explicit long form).
- When a chain connective is followed by a field name, the chain ends and a new
  condition begins. For example, `Extension equals doc or Name equals test` is
  parsed as two separate conditions joined by `or`, not as a chain.
- The chain binds at the condition level (inside `ParseCondition`), so it
  effectively groups the values before the surrounding `and`/`or` precedence
  rules apply.

> **Note:** The formatter collapses qualifying chains back to shortcut form.
> A chain qualifies when all operands share the same field and operator and the
> field belongs to a chainable category (string or attribute).

> **Note:** For `matches`, `notMatches`, and `regex`, both the semicolon shortcut
> single value) and the value chain shortcut (separate tokens) are available.
> They should not be mixed in the same expression.

## Logical Connectives

| Keyword | Aliases | Precedence | Description          |
|---------|---------|------------|----------------------|
| `or`    | `\|\|` | Lowest     | Logical disjunction  |
| `and`   | `&&`    | Middle     | Logical conjunction  |
| `not`   | `!`     | Highest    | Logical negation     |

Parentheses `()` override precedence:
```
(Name matches "*.jpg" or Name matches "*.png") and Modified after today-7d
```

## Comments

FCL supports single-line comments introduced by `//`. Everything from `//` to the
end of the line is ignored by the lexer and never reaches the parser.

```
// Only look at Office documents modified this week
Name matches "*.doc; *.docx; *.xlsx" and Modified after today-7d
```

Comments can appear on their own line or after an expression:

```
Size greaterThan 100MB   // large files only
and Modified before today-365d   // older than a year
```

Comments are useful for **temporarily disabling** conditions during iterative
filter development:

```
Name matches "*.jpg; *.png"
// and Size greaterThan 1MB
and Modified after today-30d
```

> **Note:** Comments are not preserved during formatter round-tripping. The
> formatter produces a canonical FCL string from the AST, which does not
> retain comments.

## Examples

Simple wildcard filter:
```
Name matches "*.doc"
```

Multiple patterns (semicolon shortcut):
```
Name matches "*.jpg; *.png; *.gif"
```

Multiple patterns (value chain shortcut):
```
Name matches "*.jpg" or "*.png" or "*.gif"
```

Multiple extensions (value chain shortcut):
```
Extension equals .doc or .docx or .xlsx
```

Large recent files:
```
Size greaterThan 10MB and Modified after today-7d
```

Photos but not RAW files:
```
(Extension == ".jpg" or Extension == ".png") and not Extension == ".raw"
```

Hidden or system files in a specific directory:
```
Path contains "Windows" and (Attributes have Hidden or Attributes have System)
```

Hidden or system files (value chain shortcut):
```
Path contains "Windows" and (Attributes have Hidden or System)
```

Files in user or project directories:
```
FullName contains users and documents
```

Complex query:
```
Name matches "*.doc; *.docx" and Modified afterOrOn 2024-01-01 and Size lessThan 50MB
```

## Relationship to the Basic Filter

The basic wildcard filter bar in the UI (`*.doc; *.txt`) is syntactic sugar over FCL:

```
FullName matches "*.doc" or FullName matches "*.txt"
```

When both a basic filter and an advanced FCL expression are active, they are combined
with `and`:

```
(basic filter expression) and (advanced FCL expression)
```

## Error Handling

### Parse Errors (Syntax)

Detected during parsing. Each error includes:
- Error code (e.g., `FCL001`)
- Human-readable message
- Source position (`Start`, `Length`) pointing to the offending token in the input

### Validation Errors (Semantics)

Detected after parsing by walking the AST. Each error includes the same structure.
Examples:
- Type mismatch: `Name before "2025-01-01"` — `before` requires a date field
- Invalid value: `Size greaterThan abc` — not a valid size literal
- Unknown attribute: `Attributes have Encrypted` — not a recognized attribute value

### Evaluation Errors (Runtime)

Detected during filter evaluation. Rare, but possible:
- Invalid regex pattern in a `regex` condition
- Overflow in date arithmetic
- Other general errors

All error types use the same `FclDiagnostic` structure with severity, code, message,
and source span.

## Architecture Notes

### AST (Abstract Syntax Tree)

The parser produces an immutable AST. All nodes carry a `SourceSpan` referencing
their position in the original input text.

Node types:
- `FclOrExpression` — two or more branches joined by `or`
- `FclAndExpression` — two or more branches joined by `and`
- `FclNotExpression` — negation of a sub-expression
- `FclCondition` — a single `Field Operator Value` triple
- `FclGroupExpression` — parenthesized sub-expression (preserves formatting)

Value nodes:
- `FclStringValue` — string literal
- `FclSizeValue` — parsed size with unit
- `FclAbsoluteDateValue` — concrete date/time
- `FclRelativeDateValue` — anchor + offset (resolved at evaluation time)
- `FclAttributeValue` — attribute flag identifier

### Processing Pipeline

```
Input string → Lexer → Token stream → Parser → AST
                                                 ↓
                                            Validator → List<FclDiagnostic>
                                                 ↓
                                            Evaluator → bool (per file)
                                                 ↓
                                            Formatter → normalized FCL string
```

### IFclFileInfo Interface

The evaluator operates against an `IFclFileInfo` abstraction, not `TapeFileDescriptor`
directly. This keeps FclNET independent of TapeLibNET.

```csharp
public interface IFclFileInfo
{
    string FullName { get; }
    long Size { get; }
    DateTime CreationTime { get; }
    DateTime LastWriteTime { get; }
    FileAttributes Attributes { get; }
}
```

### Visual Editor Mapping

The visual editor (in the WPF UI) works in **Disjunctive Normal Form (DNF)**:
an OR of groups, where each group is an AND of optionally-negated conditions.
This is a strict subset of what FCL can express.

- Visual editor → AST: always produces DNF
- AST → Visual editor: works if the AST is DNF-shaped
- Non-DNF AST (from manual editing): shown as read-only text with an option to
  "Simplify" (convert to DNF) or "Edit directly"

### Formatter Round-Tripping

The formatter converts an AST back to an FCL string in canonical form. When an
OR-chain consists of conditions sharing the same field and `matches`/`notMatches`/`regex` operator,
the formatter collapses them into the semicolon shortcut.
