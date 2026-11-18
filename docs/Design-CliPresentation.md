# Design — tapecon presentation: rendered docs, structured and colored help

**Status:** as implemented (Steps 1–6). Section numbers are referenced from the code; they stay stable.

**Scope:** how tapecon shows its embedded documentation, its `--help` output and its live progress. Verbs and options
changed only where presentation needed it: §4.6 makes the root options `--quiet` / `--no-color` recursive.

## 1. Problem

- **`tapecon docs`** printed the embedded Markdown line by line, as literal text: `##` headings, `|`-tables, backticks
  and asterisks reached the user verbatim.
- **`--help`** was System.CommandLine's default plain text: correct, but monochrome, and inconsistent with the colored
  output of every verb.
- **Option overload.** Every drive verb attaches the same shared options (drive selection, new-virtual-media shape,
  logging) through `GlobalOptions.Attach`, plus the filter and password sets where they apply. System.CommandLine
  listed them in attachment order — `Attach` first — so the verb's own options drowned below `--drive`,
  `--virtual-open`, `--min-block-size`…
- **No way in.** No walkthrough for a first backup, no FCL reference, no examples in the help.

## 2. Goals and non-goals

| Goals | Non-goals |
|---|---|
| Readable, themed docs in the console | A general Markdown engine |
| Help structured in titled groups, the verb's own options first | A general System.CommandLine help customisation |
| Colored help consistent with `ConsoleTheme` | Switching to Spectre.Console.Cli (a rewrite of every verb) |
| Plain fallback for `--no-color`, `--quiet`, redirected output, tests | New NuGet dependencies |
| One source of truth: help built from the commands' own symbols | Hand-written help text per verb |
| Examples per verb and a getting-started walkthrough, checked against the real command tree | Paging, links, images, localisation |
| Unit-testable without a console | |

## 3. Rendered docs — a minimal Markdown renderer

### 3.1 Why our own

Spectre.Console has no Markdown renderer: it was proposed (discussion #456) and spiked, never completed. Community
packages exist, but they are small and young — a dependency for a feature covered in a few hundred lines, because **we
own the documents** and keep them inside a small subset.

### 3.2 The supported subset

| Markdown | Rich | Plain |
|---|---|---|
| `#` heading | `Rule`, title left, `Heading` | title, `═` under it |
| `##` heading | `Heading` | title, `─` under it |
| `###`–`######` | `SubHeading` (fold into level 3) | title only |
| paragraphs | wrapped markup; lines joined with a space | wrapped text |
| `-` / `*` / `+` bullets, one nesting level | `•`, hanging indent | `-`, hanging indent |
| `1.` / `1)` numbered lists | number padded to the widest, hanging indent | same |
| tables (`\|` rows + `---` separator, alignment) | `Table`, rounded, header in `Heading`, capped at the width | aligned columns, header underlined per column; when too wide, the widest column narrows (not below 8) and its cells wrap |
| indented (4 spaces) and fenced code | `Panel`, rounded, `CodeBlock` text | indented 4 spaces, **never wrapped** — a wrapped command no longer runs when pasted |
| inline `` `code` `` | `Code` | marker stripped |
| inline `**bold**`, `*italic*` | bold, italic | markers stripped |
| `>` quote | `Dimmed`, indented | indented |
| anything else | passed through as plain text — never an error | same |

All user text is escaped (`Markup.Escape`) before the inline tags are applied.

Also supported, because the topics use them: backslash escapes outside code; `\|` in table cells as a literal pipe,
inside code spans too (GitHub's rule); lazy and indented continuation lines in list items and quotes; loose lists; an
ordered item interrupts a paragraph only when it counts from 1; unclosed markers stay literal (`*.docx`, `2 * 3`, a lone
backtick).

Deliberately **not** supported: hard line breaks, links and images (pass through as text), `_` emphasis (file names
carry underscores), nested inline spans, more than one list nesting level.

### 3.3 Shape

The renderer parses once into a block model, then renders that model twice — the rich and the plain output cannot
disagree on structure.

```
MarkdownDocument.Parse(string)                    // pure; line-based block parser
  → IReadOnlyList<MdBlock>
       MdHeading(Level, Inline Text)
       MdParagraph(Inline Text)
       MdList(Ordered, IReadOnlyList<MdListItem>)   // MdListItem(Inline Text, Level 0|1, Number)
       MdTable(Inline[] Header, Inline[][] Rows, MdAlign[] Align)
       MdCode(string Text)
       MdQuote(Inline Text)
  Inline = IReadOnlyList<MdSpan>                  // MdSpan(Kind: Text|Code|Bold|Italic, string Text)

MarkdownRenderer.ToDocument(blocks)               // ConsoleDocument: both forms, width supplied by the console
MarkdownRenderer.ToRenderables(blocks, width)     // rich
MarkdownRenderer.ToPlainLines(blocks, width)      // plain
```

- **Location:** `Ux/Markdown/MarkdownDocument.cs`, `Ux/Markdown/MarkdownRenderer.cs`. No console access: pure
  functions, unit-tested directly.
- **One word wrapper for both forms.** The rich form receives lines already broken at the same words as the plain form,
  so Spectre never re-wraps them (except on a console narrower than 60). A word may cross span borders (`` `--drive`, ``
  never breaks before its comma). A space between two spans of one kind keeps that kind.

### 3.4 Output path

- `IConsoleUx.Write(ConsoleDocument document)` writes whole documents: docs topics, the topic list, help.
- `ConsoleDocument` (`Ux/ConsoleDocument.cs`) carries **both** forms as **factories over the width**:
  `Func<int, IReadOnlyList<IRenderable>> Rich` and `Func<int, IReadOnlyList<string>> Plain`. The console picks the form
  and the width, then renders only that form. `IConsoleUx` gains no width or redirection members.
- `ConsoleDocument.FromLines(...)`: fixed lines, for simple output and tests. `ConsoleDocument.Concat(...)`: documents
  one after another at one width, one empty line between — a verb's help and its Examples (§11.2).
- `SpectreConsoleUx` writes `Plain` straight to the profile's writer when `ConsoleCapabilities.UsePlain(...)` holds
  (§6); otherwise `Rich` inside a Spectre `Rows`, which ends every block with exactly one line break.
- `SilentConsoleUx` always writes `Plain` to its `TextWriter`, at its `Width` (default 80). Tests see exactly what a
  redirected console sees.
- Unlike `Log`, `Write` adds no timestamp and no icon, and `--quiet` does **not** suppress it: the user asked for the
  text. It adds nothing to `SilentConsoleUx.Entries`; tests read documents from `RunResult.Output`.

### 3.5 Contract for the docs

Topics and examples stay within §3.2. `MarkdownTests.EmbeddedTopic_PlainOutput_ShowsNoMarkdownSyntax` renders every
topic through `ToPlainLines` and asserts that no line contains `|---`, `**` or a backtick, and no line starts with `#`
or `|`; `ExamplesTests.Example_PlainOutput_ShowsNoMarkdownSyntax` does the same for every example. **Code blocks are
exempt:** their content is literal. A second test renders every topic through `ToRenderables`, so markup errors such
as an unescaped `[Locked]` fail the build.

## 4. Structured help

### 4.1 Model

An option belongs to at most one **help group**: a stable key, a title, an optional one-line intro, an order, and a
display mode.

```csharp
public sealed record HelpGroup(
    string Key, string Title, string? Intro, int Order,
    HelpGroupMode Mode = HelpGroupMode.Full,
    string? SummaryPointer = null);       // Summary mode: "Details: `tapecon create-virtual --help`"

public enum HelpGroupMode { Full, Summary }

/// An option shown in a group other than its home, described for that group.
public sealed record HelpReference(Option Option, string Description);
```

An option has exactly **one home group** (its full entry) and **zero or more references**. Options without a group form
the implicit group `HelpGroups.Own` ("Options", order 0, always Full): the verb's own options.

### 4.2 Assigning options

Membership is declared **where the option is declared** — the shared options are static instances, so one registration
covers every verb they are attached to (`Cli/Help/HelpGroups.cs`):

| Member | Purpose |
|---|---|
| `Assign(group, options)` | Home group; a different second home throws, the same one again is a no-op. |
| `GroupOf(option)` | Home group, or null (the verb's own). `HelpOption` / `VersionOption` by type: _Output and diagnostics_. |
| `Promote(cmd, group)` | On `cmd`, the group is the command's content: Full, first, under "Options". |
| `Reference(group, option, description)` | Also shown in `group`, with its own description; never into the home. |
| `ReferencesIn(group)`, `ReferencesOf(option)` | Lookups for the model. |

- **Storage:** a `ConditionalWeakTable` per relation, plus a locked list for references. Verb-local options and commands
  are created anew by every `RootCommandFactory.Create` (once per test); the registry must not keep them alive.
- **Registration** in the static constructor of each option class: `GlobalOptions`, `VirtualMediaOptions`,
  `FilterOptions`, `PasswordOptions`. `BackupCommand` assigns its local `--encrypt` / `--encryption` to _Encryption_.
- **Rendering rules:** a row shows only for a command that accepts the option (its own options, plus its ancestors'
  recursive ones); rows follow attachment order, `--help` last; a reference row has no default; Summary mode lists
  canonical names only; a reference **into** a promoted group **replaces** the option's home row on that command; empty
  sections never render.

### 4.3 The groups

| Key | Title | Members | Order | Mode |
|---|---|---|---|---|
| — | **Options** | the verb's own options | 0 | Full |
| `filter` | **File selection** | `--filter`, `--filter-file` | 10 | Full |
| `encryption` | **Encryption** | `--password-file`, `--password-env`, `--password`, `--new-password-file`, `--new-password-env`, `--new-password`; on backup `--encrypt` / `--encryption` first | 20 | Full |
| `drive` | **Drive selection** | `--drive`, `--virtual-open`, `--virtual-create`, `--in-memory`, `--initiator` | 30 | Full |
| `virtual-new` | **New virtual media** | `--capacity`, `--init-capacity`, `--initiator` (ref.), mark profiles, block sizes, `--overwrite-virtual` | 40 | **Summary** |
| `diagnostics` | **Output and diagnostics** | `--log-level`, `--quiet`, `--no-color`, `--help` | 90 | Full |

Intros: _Drive selection_ "At most one; none opens physical drive 0." · _New virtual media_ "Shape media created by
`--virtual-create`. Ignored by `--virtual-open`." · _File selection_ "Bare arguments are classified automatically;
these bypass it." · _Encryption_ "Without these, encrypted sets stay locked, or the password is asked."

`--initiator` has its home in _Drive selection_ (it also serves `--virtual-open`); a reference in _New virtual media_
covers its create-time role. `GlobalOptions.Attach` attaches it after `--init-capacity`, so it closes the selectors in
one group and sits next to the capacities in the other.

`--import-toc` (`TocOptions`) and `--media-description` (`MediaOptions`) have no group: they are the verb's own options.

### 4.4 Summary mode — the key to the overload

On every verb except `create-virtual`, the new-virtual-media options collapse to one paragraph of names plus the
pointer `Details: tapecon create-virtual --help`. `create-virtual` promotes the group: there the options are its
content, in full, first, under "Options".

### 4.5 Order

Groups sort by `Order`, never by attachment — so `GlobalOptions.Attach` may stay first in every verb. Within a group,
options keep their attachment order. There is no "Global options" heading: the groups say what the options are for.

### 4.6 Recursive root options

`--quiet` and `--no-color` are `GlobalOptions.Quiet` / `NoColor` (`Recursive = true`, home _Output and diagnostics_),
attached once to the root: every verb parses them and lists them. `Program`'s pre-scan still applies them before
parsing, for the banner.

### 4.7 Hidden and visible

`Option.Hidden` / `Command.Hidden` are honoured; nothing is hidden today. Aliases show with the option, the canonical
name first.

## 5. Help rendering

### 5.1 Mechanism

`RootCommandFactory` replaces the root `HelpOption`'s action with `TapeConHelpAction`, which reaches every verb's
`--help` (the option is recursive). It builds the model of `parseResult.CommandResult.Command`, renders it, appends the
command's Examples if it has any (§11), and writes through `IConsoleUx.Write` — the docs' output path.

```
HelpModel.Build(Command cmd)             // pure: walks the symbols, applies the groups
  → HelpModel(Title, Description, Usage, Arguments, Sections, Commands, Footer, CommandsTitle = "Commands")
HelpRenderer.Layout(model, width)        // internal: lines of styled runs — HelpRun(Text, Style, KeepWithPrevious)
HelpRenderer.ToPlainLines(model, width)  // the runs' texts, concatenated
HelpRenderer.ToRenderables(model, width) // ONE Markup: each run escaped, wrapped in its style's markup
```

- **Location:** `Cli/Help/` — `HelpGroups.cs`, `HelpModel.cs`, `HelpRenderer.cs`, `TapeConHelpAction.cs`.
- `HelpModel.Build` is the only place that touches System.CommandLine's symbol API. It walks `Parents` (no root
  parameter), reads value types from the generic `Option<T>` base, and defaults from `HasDefaultValue` /
  `GetDefaultValue()` (a throwing factory yields no default).
- `TapeConHelpAction` overrides **`ClearsParseErrors => true`**: since 2.0 RC1 a custom help action no longer clears
  parse errors implicitly, and `backup --help` would fail on the missing `<files>`.
- The root shows as `tapecon` (`HelpModel.RootName`), not the executable name, which in tests is the test host's.
- **One layout, two forms.** The plain form, which the tests pin, is the rich form without colors; a test checks this
  for every command.
- **Own wrapper:** greedy at spaces, minimum width 20; an overlong word breaks after `/ | , \` (`BreakOverlong`).
  Neighbouring runs of one style merge.
- **`CommandsTitle`** titles the command rows; `tapecon docs` lists its topics as such rows, titled "Topics" (§10.3).

### 5.2 Layout

| Section | Content | Rich style |
|---|---|---|
| Title | `tapecon <verb>`, aliases in parentheses | `Heading` |
| Description | `Command.Description`, wrapped | unstyled |
| Usage | `Usage: tapecon <verb> <args>... [options]` | `Emphasis`, whole line |
| Arguments | name, arity marker, description | name `OptionName` |
| Option sections | §4.3 order; title, intro, rows | title `Heading`, intro `Dimmed` |
| Option rows | names and aliases, placeholder, description, default | `OptionName`, `Placeholder`, unstyled, `Dimmed` |
| Commands | subcommands (the root) or topics (`docs`) | names `OptionName` |
| Footer | root only: `Start: tapecon docs getting-started · All topics: tapecon docs · Exit codes: tapecon docs concepts` | `Dimmed` |

**Mentions.** Help texts mark options and other literals with backticks: `` `--password-file` ``,
`` `tapecon docs faq` ``. Every text the help shows takes them: command, argument, option and reference descriptions,
group intros, the summary pointer, the footer.

| Element | Rich | Plain |
|---|---|---|
| `` `…` `` | `ConsoleTheme.Mention`, markers removed | markers removed |
| a marker without a partner | literal | literal |
| an empty pair ` `` ` | literal; its second marker may open the next pair | literal |

- **Punctuation stays attached.** A word may cross a mention border: `` (`--drive`), `` is one word of three runs; every
  run after the first carries `KeepWithPrevious`, and the wrapper moves them as one unit.
- **Spaces inside a mention** split it into several mention words, so a long mention wraps.
- **Only backticks:** the help does not reuse the docs' inline parser — `*.doc*` would turn into italics.
- **Texts used outside the help** pass through `HelpRenderer.PlainText` (e.g. `SchemeWords.EncryptionChoices` in an
  error message).
- **Completeness, by test:** `HelpModelTests.HelpTexts_NameOptions_OnlyAsMentions` finds every real option named
  outside a mention, in every help text of the tree.

Option rows:

- **Placeholder:** `HelpName` when set (`PATH`, `SIZE`, `FCL`, `LEVEL`, `SCHEME`, `SET`, `TEXT`, `VARIABLE`), else from
  the value type — none for a flag, `N` for numbers, `TEXT` for strings, the choices for enums, `VALUE` otherwise.
- **Default:** shown when present and not the type's default (`[default: Crc32]`). **The default is one unbreakable
  run**, so a wrap never separates `[default:` from its value.
- **Name column:** as wide as the widest name in the whole help, capped at 34; longer names push their description to
  the next line.
- **Aliases:** canonical name first, then long before short; Windows-style `/h`, `/?` omitted.

### 5.3 Example — `tapecon backup --help` (abridged)

```
tapecon backup
  Back up files/folders to the loaded media, after the last set on it. Each
  positional argument is a file path, folder path, or wildcard pattern.

Usage: tapecon backup <files>... [options]

Arguments
  <files>                             Files, folders, or wildcard patterns ...

Options
  --description, --desc, -D TEXT      Backup-set description written into the TOC.
  --incremental, -i                   Back up only the files new or changed since
                                      the last set of this incremental chain ...
  --hash, -H <None|Crc32|...>         Per-file hash algorithm. [default: Crc32]
  --append, -a                        Append the new set after the last set on the
                                      media — the default. ...
  --overwrite-media                   Overwrite the ENTIRE media: ...
  --media-description, --mdesc TEXT   Description of the new media, ...

File selection
  Bare arguments are classified automatically; these bypass it.
  --filter FCL                        FCL condition the selected files must meet ...

Encryption
  ...
Drive selection
  ...
New virtual media
  Shape media created by --virtual-create. Ignored by --virtual-open.
  --capacity  --init-capacity  --initiator  --setmarks  --seq-filemarks
  --filemarks-only  --min-block-size  --def-block-size  --max-block-size
  --overwrite-virtual
  Details: tapecon create-virtual --help

Output and diagnostics
  --log-level <Trace|...|None>        ... [default: None]
  --quiet, -q                         ...
  --help, -h, -?                      Show help and usage information.

Examples
────────

Back up a folder and its subfolders to the tape in drive 0. ...

    tapecon backup D:\Data --subdirs
...
```

### 5.4 Errors and help

A parse error keeps System.CommandLine's behaviour (error lines, exit 2). The help action is reached only through
`--help`; the root's own action logs "No verb specified" with pointers to `tapecon --help` and
`tapecon docs getting-started`.

## 6. Shared pieces

- **Theme** (`Ux/ConsoleTheme.cs`): `Heading` (bold yellow), `SubHeading` (yellow), `Emphasis` (bold), `OptionName`
  (cyan), `Placeholder` (grey), `Mention` (darkcyan), `Dimmed` (dim), `Code` and `CodeBlock` (cyan), and the log-level
  styles `LevelCompleted` … `LevelError`. Both renderers take colors only from here.
- **Level icons:** Completed ✓, Info ℹ, Warning ⚠, **Failed ✗** (U+2717), **Error ✖** (U+2716): Failed and Error differ
  by glyph too, so they stay apart without color.
- **Plain mode:** `ConsoleCapabilities.UsePlain(noColor, quiet, redirected) = noColor || quiet || redirected`.
- **Width:** `ConsoleCapabilities.ClampWidth(consoleWidth, redirected)` = 80 when redirected or the console reports no
  width; otherwise the console width clamped to [60, 120].
- **Redirection** comes from the console: `SpectreConsoleUx` asks `Profile.Out.IsTerminal`; `SilentConsoleUx` always
  counts as redirected.

### 6.1 User colors and NO_COLOR

- **`TAPECON_COLORS`** overrides any style by key: `key=style;key=style`, Spectre style syntax. Keys
  (`ConsoleTheme.ColorKeys`): `heading`, `subheading`, `emphasis`, `option`, `placeholder`, `mention`, `dimmed`,
  `code`, `codeblock`, `completed`, `info`, `warning`, `failed`, `error`.
- **Read once,** by `ConsoleTheme`'s static constructor; the styles stay static readonly fields.
- **Never fatal:** a bad entry is skipped, its default kept, and a line added to `ConsoleTheme.ColorWarnings`, which
  `Program` logs. Keys are case-insensitive; a repeated key: the last wins. The parser, `ParseColors`, is pure.
- **`NO_COLOR`** (no-color.org): set and not empty acts as `--no-color` — plain output, not just colorless
  (`ConsoleCapabilities.NoColorRequested`, checked by `Program` before the pre-scan).
- Not supported: a config file.

### 6.2 Prompts and the live progress display

Spectre's live displays and its prompts must never share the console: a progress display redraws its region on every
refresh, writing over a prompt, so the prompt's first line repeats until the user answers. The first symptom: a backup's
file-error prompt (Skip / Retry / Skip all / Abort) printing its headline over and over.

- **`SpectreProgressScope.Suspend()`** ends the live display and returns a token; disposing the token starts a new
  display with the same value and status. Suspensions nest; quiet or disposed scopes return a no-op token.
- **`SpectreConsoleUx`** tracks the active scope (`BeginProgress`) and suspends it around **every** prompt —
  `Confirm`, `Select`, `Ask`, `AskSecret` — only when the prompt is actually shown: an unattended run never stops its bar.
- **State in the scope, not the Spectre task:** value, status and the **clock**. `ScopeElapsedColumn` reads the scope's
  `Stopwatch` instead of the task's start time, so the elapsed time continues after a prompt instead of restarting at
  0. The clock pauses while suspended: answering a prompt is not operation time, as in the service's own timing.
- **`AutoClear(true)`:** a suspended display leaves no stale bar above the prompt, and a finished operation leaves its
  log, not its bar.

## 7. Testing

| Area | Tests | How |
|---|---|---|
| Markdown parser | each block kind; inline spans; escapes; unclosed markers; unsupported syntax passes through | `MarkdownTests`, pure |
| Markdown renderers | plain fixture line by line; wrapping with hanging indent; table shrinking; rich escapes brackets | `MarkdownTests`, pure + Spectre test console |
| Doc subset | every topic: no Markdown syntax in plain prose; rich render does not throw | `MarkdownTests` |
| `tapecon docs` | topics; list = `--list`, sorted, titled, fits 80 columns; unknown topic: exit 2, then the list; case-insensitive names | `DocsCommandTests` |
| Examples | every example names a real command; code lines ≤ 76; **every `tapecon …` line in every example and every topic parses** against the real tree; no Markdown syntax in plain prose; `backup --help` ends with Examples, `eject --help` has none | `ExamplesTests` |
| Help groups | second home throws; reference into home throws; `--initiator` home + reference; built-ins in diagnostics | `HelpModelTests` |
| Help model | every command: sections non-empty and ordered; no option twice in a section; every visible option shown; one home row; no name or alias collides | `HelpModelTests` |
| Help output | every verb `--help` exits 0; root lists commands and footer; backup's section order; summary and pointer; `--help` last; promotion; long names push the description down; lines fit 80 columns; `[default: None]` unbroken | `CliParsingTests` |
| Colored help | every command: rich without colors equals plain; ANSI emitted; brackets escaped; theme style per run; neighbouring runs never share a style | `HelpRendererTests` |
| Mentions | punctuation attached; unpaired and empty markers literal; no marker in any plain help; mention completeness | `HelpRendererTests`, `HelpModelTests` |
| Output path | `Write` on both consoles: plain lines, no timestamp, not quiet-suppressed, at the decided width; capability rules; icons | `ConsoleOutputTests` |
| Colors | `ParseColors` cases; `NoColorRequested` by presence | `ConsoleThemeTests` |
| Prompts and progress | — manual: a prompt during a backup shows once; the clock continues after it | — |

## 8. Plan (as executed)

| Step | Delivered |
|---|---|
| 1 — Output path | `ConsoleDocument`, `ConsoleCapabilities`, `IConsoleUx.Write` on both consoles, presentation styles, the Error icon (§3.4, §6). |
| 2 — Markdown renderer and docs | `MarkdownDocument`, `MarkdownRenderer`, `DocsCommand` on the renderer, `MarkdownTests` (§3). |
| 3 — Help groups and model (plain) | `HelpGroups`, `HelpModel`, `HelpRenderer`, `TapeConHelpAction`, registrations, promotion, recursive root options (§4, §5.1). |
| 4 — Colored help | `HelpRenderer` around `Layout` / `HelpRun`, `Emphasis`, `HelpRendererTests` (§5.1, §5.2). |
| 5 — Verb sweep | Mentions; every description reviewed; placeholders; `TAPECON_COLORS`, `NO_COLOR` (§5.2, §6.1). |
| 6a — Docs mechanism | `DocTopics`, the topic list as a help model, `ConsoleDocument.Concat`, Examples in help (§10, §11). |
| 6b — Content | Topics and examples (§12); FCL classification and combination fixed along the way. |
| 6c — Wrap-up | This document as implemented; `TapeConNET-2.0-Architecture.md`. |

Along the way, outside presentation (TapeConNET-2.0-Architecture §3.5, §3.6): appending as the backup default with
`--overwrite-media`; the media description on every verb that creates or renames media; the TOC commands reworked into
files (`export-toc`, `--import-toc`, `scan-media` saving recovered TOCs, `recover-toc --save`); prompts suspending the
progress display (§6.2).

## 9. Open points

| # | Point | Proposal |
|---|---|---|
| 1 | Root `--help`: commands grouped (lifecycle / write / read / TOC edits / recovery / docs)? | A command → group table in `HelpGroups`, same model as options. |
| 2 | A `--help-all` expanding Summary groups everywhere? | Not now: the pointer to `create-virtual --help` covers it. |
| 3 | A generated `docs/cli-reference.md` + drift test | `HelpRenderer.ToMarkdown(model)` — the same model, a third output. |
| 4 | ASCII underlines in the plain form | Would keep piped output readable under any code page; the FAQ documents the PowerShell setting meanwhile. |

## 10. Docs: a flat list of topics

### 10.1 Resources

```
Resources/Docs/<topic>.md        → docs/<topic>.md        topics of `tapecon docs`
Resources/Examples/<verb>.md     → examples/<verb>.md     the Examples section of `<verb> --help`
```

```xml
<EmbeddedResource Include="Resources\Docs\*.md"     LogicalName="docs/%(Filename)%(Extension)" />
<EmbeddedResource Include="Resources\Examples\*.md" LogicalName="examples/%(Filename)%(Extension)" />
```

- `LogicalName` fixes the resource name, independent of the namespace or folder names. Each prefix maps to one folder.
- **Topic name = file name without `.md`**, matched case-insensitively. **Title = the first heading**, markers removed,
  read with the docs parser.
- Adding a topic or an example means dropping a file into the folder. No code changes.

### 10.2 `tapecon docs`

| Call | Output |
|---|---|
| `tapecon docs` | the topic list |
| `tapecon docs --list` | the same |
| `tapecon docs <topic>` | the topic, rendered (§3) |
| `tapecon docs <unknown>` | error, then the topic list; exit 2 |

- **No default topic.** The list is the landing page: a new user sees `getting-started`, and no topic name sits
  hard-wired in code.
- **Sorting:** alphabetical by topic name (ordinal, case-insensitive).

### 10.3 The list reuses the help layout

The list is a `HelpModel` whose command rows are the topics, titled "Topics" (`CommandsTitle`): the help's two columns,
wrapping with a hanging indent, mentions, both forms and every width, with no code of its own.

```
tapecon docs
  Show the built-in documentation. Without a topic: the list of topics.

Usage: tapecon docs [<topic>] [options]

Topics
  concepts                  Concepts
  encryption                Encryption
  ...
  getting-started           Getting started: your first backup and restore
  media-recovery            Media recovery: when the TOC cannot be read
  ...

Show one: tapecon docs <topic>. Every option of a verb: tapecon <verb> --help.
```

### 10.4 `DocTopics` (`Cli/DocTopics.cs`)

| Member | Use |
|---|---|
| `All` | every `docs/` topic: name and title, sorted; built once |
| `Find(name)` | case-insensitive lookup |
| `Load(topic)` | the Markdown text |
| `Examples(cmd)` | `examples/<path>.md` or null; path = command names below the root, joined by `-` |
| `ExampleNames`, `PathOf(cmd)` | for the tests |

`DocsCommand.TopicNames` and `LoadTopic` delegate to `DocTopics`.

## 11. Examples in verb help

### 11.1 The file

`Resources/Examples/<verb>.md` is ordinary Markdown in the §3.2 subset: code blocks, **bold**, *italics*, mentions,
lists, notes after an example. By convention it starts with `## Examples`, then explains each example in prose and
follows it with code.

- A verb without a file has no Examples section (`eject`, `demo`).
- **One scenario everywhere:** back up `D:\Data`, restore to `D:\Restored`, the virtual tape `C:\Tapes\first`; the
  examples and `getting-started` describe the same steps.
- **A code line holds at most 76 characters:** code never wraps, and its 4-space indent then fits 80 columns.
- **Every `tapecon …` line in a code block parses** against the real command tree (§14); a pipe or redirection ends
  the command.

### 11.2 Rendering

`TapeConHelpAction` appends the examples, rendered by the existing `MarkdownRenderer`:
`ConsoleDocument.Concat(help, MarkdownRenderer.ToDocument(MarkdownDocument.Parse(examples)))`. `HelpModel` and
`HelpRenderer` know nothing of examples.

## 12. Content

| Topic | Content |
|---|---|
| `getting-started` | first backup and restore, every step for a real drive and a virtual tape; format first |
| `concepts` | overview, one section per subject pointing to its topic; exit codes |
| `fcl` | classification, combination (patterns OR, FCL AND), fields, operators, values, shortcuts, quoting, filter files; the cheat sheet is the language's reference |
| `incremental-backup` | by modification time against the chain; no archive attribute |
| `encryption` | password sources, locked sets, chains, password change, spare TOC |
| `multi-volume` | series, ending early, restoring, TOC partition full |
| `toc` | two copies, reserve, search, spare copy, pointer to recovery |
| `media-recovery` | a TOC from a file, a later volume or a scan; recovering files; repairing with `rename-media` / `delete-sets` |
| `virtual-drives` | creating, opening, files, one drive per run |
| `restore-validate-verify` | the three verbs, choosing the set, failures, volumes |
| `faq` | scripting, passwords, colors, paging, TapeWinNET |
| `migration-from-1.0` | verbs, drive selection, file selection, scripts |

Examples: `backup`, `restore`, `validate`, `verify`, `list`, `info`, `format`, `create-virtual`, `export-toc`,
`rename-media`, `rename-set`, `delete-sets`, `scan-media`, `recover-toc`, `docs`.

## 13. No content compression

All topics and examples come to roughly 100–150 KB of text; compression would save about 100 KB on a self-contained
executable of tens of MB. Should single-file size ever matter, `EnableCompressionInSingleFile` (self-contained only)
compresses the whole bundle, docs included, with no code.

## 14. Tests

The docs and examples tests are part of §7: `DocsCommandTests` (§10), `ExamplesTests` (§11, and the command lines of
every topic), `MarkdownTests` (§3.5 for topics).
