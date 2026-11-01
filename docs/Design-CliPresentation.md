# Design — tapecon presentation: rendered docs and coloured help

**Status:** concept · scheduled after the encryption branch.
**Scope:** how `tapecon` shows its embedded documentation and its `--help` output. No change to verbs, options or
behaviour — presentation only.

## 1. Problem

- **`tapecon docs`** prints the embedded Markdown (`Resources/Docs/*.md`) line by line, as literal text: `##` headings,
  `|`-tables, backticks and asterisks reach the user verbatim.
- **`--help`** is System.CommandLine's default plain-text output: correct, but monochrome, and inconsistent with the
  coloured, themed output of every verb.

## 2. Goals and non-goals

| Goals | Non-goals |
|---|---|
| Readable, themed docs in the console | A general Markdown engine |
| Coloured help consistent with `ConsoleTheme` | Switching to `Spectre.Console.Cli` (a rewrite of every verb) |
| Plain fallback for `--no-color`, `--quiet`, redirected output, tests | New NuGet dependencies |
| Unit-testable without a console | Paging, links, images |

## 3. Rendered docs — a minimal Markdown renderer

### 3.1 Why our own

Spectre.Console has no Markdown renderer: it was proposed (discussion #456) and spiked, never completed. Community
packages exist (`BoxOfYellow.ConsoleMarkdownRenderer.Spectre`, `Spectre.Console.Next.Markdown`), but they are small,
young, and one predecessor is already archived — a dependency for a feature we can cover in ~120 lines, because **we
own the documents** and can keep them inside a small subset.

### 3.2 The supported subset

| Markdown | Spectre rendering |
|---|---|
| `#` / `##` / `###` headings | `Rule` with a title (top level), bold themed markup (lower levels) |
| paragraphs | wrapped markup; consecutive lines joined |
| `-` / `*` bullets (one nesting level) | indented `•` lines, hanging indent |
| tables (`\| … \|` with a `---` separator row) | `Table` widget: rounded border, themed header |
| indented code (4 spaces) and fenced blocks | grey text, optionally in a `Panel` |
| inline `` `code` `` | `[cyan]…[/]` |
| inline `**bold**`, `*italic*` | `[bold]…[/]`, `[italic]…[/]` |
| anything else | passed through as plain text — never an error |

All user text is escaped (`Markup.Escape`) before the inline tags are applied.

### 3.3 Shape

- `MarkdownConsoleRenderer` (TapeConNET, `Ux/`): `IReadOnlyList<IRenderable> Render(string markdown)` — a line-based
  block parser (heading / table / list / code / paragraph), then an inline pass. Pure; no console access.
- **Plain fallback:** `IReadOnlyList<string> RenderPlain(string markdown)` — headings as text with an underline, tables
  as aligned columns, inline markers stripped. Used by `SilentConsoleUx`, `--no-color`, `--quiet`, and redirected output.
- `IConsoleUx` gains `void Write(IRenderable)` (Spectre) — `SilentConsoleUx` uses the plain path instead.
- `DocsCommand` calls the renderer instead of logging line by line.

### 3.4 Contract for the docs

The documents stay within §3.2. A unit test renders every embedded topic and asserts no literal `|---`, `##` or
backtick survives the plain fallback — so a doc edit that leaves the subset fails the build.

## 4. Coloured help

### 4.1 Mechanism

System.CommandLine 2.0 lets an application replace the help option's **action** (Microsoft: "How to customize help").
`RootCommandFactory` installs a custom action on the root's `HelpOption` (recursive: every verb inherits it). The action
receives the `ParseResult`, hence the command being helped, and renders it with Spectre.

### 4.2 What it renders

From the command's own symbols — nothing duplicated by hand:

| Section | Content | Style |
|---|---|---|
| Description | `Command.Description` | plain, wrapped to the console width |
| Usage | `tapecon <verb> [arguments] [options]` | bold |
| Arguments | name, arity, description | name themed |
| Options | aliases, `HelpName` / value placeholder, description, default (`DefaultValueFactory`) | names cyan, placeholder grey, default dimmed |
| Commands | subcommands with their descriptions | names themed |
| Global options | the shared `GlobalOptions` (drive selection, logging), grouped last | dimmed heading |

Hidden symbols (`Hidden = true`) are skipped. Option groups (filter, password, encryption) may get their own headings
later — the renderer groups by a small table of known option sets.

### 4.3 Fallback and testing

- `--no-color`, `--quiet`, redirected output: plain text in the same layout (no ANSI).
- Tests run through `TapeConHost` with `SilentConsoleUx`: they assert content (every option of a verb appears, hidden
  ones do not, defaults shown), not colour. Existing `Help_ForVerb_ReturnsOk` tests keep passing.

## 5. Shared pieces

- **Theme:** both renderers take colours from `ConsoleTheme` — one place to adjust.
- **Width:** both wrap to `AnsiConsole.Profile.Width`, minimum 60.
- **Plain mode decision:** one helper, `ConsoleCapabilities.UsePlain(ux)` = `NoColor || QuietMode || output redirected`.

## 6. Plan

| Step | Content | Exit |
|---|---|---|
| 1 | `MarkdownConsoleRenderer` + plain fallback + `IConsoleUx.Write`; `DocsCommand` switched; the doc-subset test | docs render; all tests green |
| 2 | custom help action + plain fallback; content tests per verb | help coloured; all tests green |
| 3 | review the embedded docs and option descriptions for the subset and for wording | — |

**Inputs needed then:** `Program.cs`, `Cli/RootCommandFactory.cs`, `Ux/ConsoleTheme.cs`, `DocsCommandTests.cs`, and the
System.CommandLine version in use (2.0.7 per `GlobalOptions`).
