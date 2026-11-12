# Design — tapecon presentation: rendered docs, structured and coloured help

**Status:** design · ready to implement (follows the encryption branch).
**Scope:** how tapecon shows its embedded documentation and its `--help` output. No change to what verbs and options
do — only to how they present themselves. The one exception is §4.6: the root options `--quiet` / `--no-color` become
recursive, so that the help can list them on every verb (and they parse there).

## 1. Problem

- **`tapecon docs`** prints the embedded Markdown (`Resources/Docs/*.md`) line by line, as literal text: `##`
  headings, `|`-tables, backticks and asterisks reach the user verbatim.
- **`--help`** is System.CommandLine's default plain-text output: correct, but monochrome, and inconsistent with the
  coloured, themed output of every verb.
- **Option overload.** Every drive verb attaches the same 17 shared options (drive selection, new-virtual-media shape,
  logging) through `GlobalOptions.Attach`, plus the filter and password sets where they apply. System.CommandLine lists
  options in the order they were added — and `Attach` is called *first* — so `backup --help` opens with
  `--drive`, `--virtual-open`, `--min-block-size`… and the backup's own options (`--incremental`, `--hash`,
  `--encrypt`) follow somewhere below. The verb's purpose drowns.

## 2. Goals and non-goals

| Goals | Non-goals |
|---|---|
| Readable, themed docs in the console | A general Markdown engine |
| Help structured in titled groups, the verb's own options first | A general System.CommandLine help customisation |
| Coloured help consistent with `ConsoleTheme` | Switching to Spectre.Console.Cli (a rewrite of every verb) |
| Plain fallback for `--no-color`, `--quiet`, redirected output, tests | New NuGet dependencies |
| One source of truth: help built from the commands' own symbols | Hand-written help text per verb |
| Unit-testable without a console | Paging, links, images, localisation |

## 3. Rendered docs — a minimal Markdown renderer

### 3.1 Why our own

Spectre.Console has no Markdown renderer: it was proposed (discussion #456) and spiked, never completed. Community
packages exist, but they are small, young, and one predecessor is already archived — a dependency for a feature we can
cover in ~150 lines, because **we own the documents** and keep them inside a small subset.

### 3.2 The supported subset

| Markdown | Spectre rendering | Plain rendering |
|---|---|---|
| `#` heading | `Rule` with the title, themed | title, then a line of `═` |
| `##` / `###` headings | bold themed line, blank line before | title, then a line of `─` (`##`) / title only (`###`) |
| paragraphs | wrapped markup; consecutive lines joined | wrapped text |
| `-` / `*` bullets, one nesting level | `•` with hanging indent | `-` with hanging indent |
| `1.` numbered lists | number with hanging indent | same |
| tables (`\| … \|` + `---` separator row) | `Table`: rounded border, themed header | aligned columns, header underlined |
| indented (4 spaces) and fenced code | grey text in a `Panel` | indented 4 spaces |
| inline `` `code` `` | `[cyan]…[/]` | marker stripped |
| inline `**bold**`, `*italic*` | `[bold]…[/]`, `[italic]…[/]` | markers stripped |
| `> quote` | dimmed, indented | indented |
| anything else | passed through as plain text — never an error | same |

All user text is escaped (`Markup.Escape`) before the inline tags are applied.

Also supported, because the topics use them:

- **Backslash escapes** (`\*`, `\[`, …) outside code; **`\|` in table cells** becomes a literal pipe — inside code spans, too, as in GitHub Markdown.
- **Lazy and indented continuation lines** in list items and quotes; **loose lists** (blank lines between items).
- **`+` bullets** and **`1)` numbers**; `####`–`######` fold into level 3.
- **Unclosed markers stay literal:** `*.docx`, `2 * 3`, a lone backtick.

Deliberately **not** supported: hard line breaks (lines always join with a space), links and images (pass through as text), `_` emphasis (file names carry underscores), nested inline spans (`**bold `code`**` renders as bold text), more than one list nesting level. A nested item in a numbered list renders as a bullet.

Rendering details as built:

| Markdown | Spectre | Plain |
|---|---|---|
| `#` | `Rule`, title left, `Heading` | title, `═` under it |
| `##` | `Heading` (bold cyan) | title, `─` under it |
| `###` | `SubHeading` (cyan) | title only |
| code block | `Panel`, rounded, `CodeBlock` (grey) text | indented 4 spaces, **never wrapped** — a wrapped command no longer runs when pasted |
| table | `Table`, rounded, header in `Heading`, capped at the width | aligned columns, header underlined per column; when too wide, the widest column narrows (not below 8) and its cells wrap |


### 3.3 Shape

The renderer parses once into a tiny block model, then renders that model twice — so the Spectre and the plain output
can never disagree on structure.

```
MarkdownDocument.Parse(string)                    // pure; line-based block parser
  → IReadOnlyList<MdBlock>
       MdHeading(Level, Inline Text)
       MdParagraph(Inline Text)
       MdList(Ordered, IReadOnlyList<MdListItem>)   // MdListItem(Inline Text, Level 0|1)
       MdTable(Inline[] Header, Inline[][] Rows, Alignment[] Align)
       MdCode(string Text)
       MdQuote(Inline Text)
  Inline = IReadOnlyList<MdSpan>                  // MdSpan(Kind: Text|Code|Bold|Italic, string Text)

MarkdownRenderer.ToDocument(blocks)               // ConsoleDocument: both forms, width supplied by the console
MarkdownRenderer.ToRenderables(blocks, width)     // Spectre: IReadOnlyList<IRenderable>
MarkdownRenderer.ToPlainLines(blocks, width)      // plain:   IReadOnlyList<string>
```

- **Location:** `TapeConNET/Ux/Markdown/` — `MarkdownDocument.cs` (model + parser), `MarkdownRenderer.cs`.
- **No console access** in either: both are pure functions, unit-tested directly.
- **One word wrapper for both forms.** The rich form receives lines already broken at the same words as the plain form, so Spectre never re-wraps them (except on a console narrower than 60). A word may cross span borders (`` `--drive`, `` never breaks before its comma). A space between two spans of the same kind keeps that kind, so a bold phrase stays one styled run.
- `MdListItem` carries `Number` (the source number of an ordered item); numbers are padded to the widest, so `9.` and `10.` texts line up.

## §3.4 Output path

- `IConsoleUx` gains `void Write(ConsoleDocument document)`.
- `ConsoleDocument` (`Ux/ConsoleDocument.cs`) carries **both** forms as **factories over the width**:
  `Func<int, IReadOnlyList<IRenderable>> Rich` and `Func<int, IReadOnlyList<string>> Plain`. The console picks the form and the width, then renders **only** that form. The producer never asks for the width, and `IConsoleUx` gains no width or redirection members.
- `SpectreConsoleUx` writes `Plain` straight to the profile's writer when `ConsoleCapabilities.UsePlain(...)` holds (§6). Otherwise it writes `Rich` inside a Spectre `Rows`, which ends every block with exactly one line break.
- `SilentConsoleUx` always writes `Plain` to its `TextWriter`, at its `Width` (default 80). Tests see exactly what a redirected console sees.
- Unlike `Log`, `Write` adds no timestamp and no icon. `--quiet` does **not** suppress it, because the user asked for the text explicitly. It adds nothing to `SilentConsoleUx.Entries`: tests read documents from `RunResult.Output`.
- `ConsoleDocument.FromLines(...)` creates a fixed-line document for simple output and tests.
- `DocsCommand` parses the topic and calls `ux.Write(MarkdownRenderer.ToDocument(...))`. **`docs --list`** is a document too: a small markdown list with a one-line summary per topic, so `--quiet` no longer hides it.

### 3.5 Contract for the docs

The documents stay within §3.2. A unit test (`MarkdownTests.EmbeddedTopic_PlainOutput_ShowsNoMarkdownSyntax`) renders
every embedded topic through `ToPlainLines` and asserts that no line contains `|---`, `**` or a backtick, and no line
starts with `#` or `|`. **Code blocks are exempt:** their content is literal and may hold `**` globs
(`path matches "**\bin\**"`) on purpose. A second test renders every topic through `ToRenderables` into a test console,
so markup errors such as an unescaped `[Locked]` fail the build.

## 4. Structured help

### 4.1 Model

An option belongs to at most one **help group**. A group has a stable key, a title, an optional one-line intro, an
order, and a display mode.

```csharp
/// <summary>A titled section of a command's help.</summary>
public sealed record HelpGroup(
    string Key, string Title, string? Intro, int Order,
    HelpGroupMode Mode = HelpGroupMode.Full,
    string? SummaryPointer = null);       // Summary mode: "Details: tapecon create-virtual --help"


public enum HelpGroupMode
{
    /// <summary>Every option with its description.</summary>
    Full,
    /// <summary>One line: the option names only, plus a pointer to where they are described in full.</summary>
    Summary,
}
```

The implicit group is a real constant, `HelpGroups.Own` ("Options", order 0). A promoted group renders under its title.

An option has exactly **one home group** (its full entry) and **zero or more references**.
A reference repeats the option in another group, with a description written for that group:

```csharp
/// <summary>An option shown in a group other than its home, described for that group.</summary>
public sealed record HelpReference(Option Option, string Description);
```

Options without a group form the implicit **"Options"** group: the verb's own options, order 0, always `Full`.

### 4.2 Assigning options

Membership is declared **where the option is declared**, not per verb — the shared options are static instances, so
one registration covers every verb they are attached to.

```csharp
internal static class HelpGroups
{
    public static readonly HelpGroup Filter      = new("filter",      "File selection",     "...", 10);
    public static readonly HelpGroup Encryption  = new("encryption",  "Encryption",         "...", 20);
    public static readonly HelpGroup Drive       = new("drive",       "Drive selection",    "...", 30);
    public static readonly HelpGroup VirtualNew  = new("virtual-new", "New virtual media",  "...", 40,
                                                       HelpGroupMode.Summary);
    public static readonly HelpGroup Diagnostics = new("diagnostics", "Output and diagnostics", null, 90);

    /// <summary>Assigns <paramref name="options"/> to <paramref name="group"/>; returns them for chaining.</summary>
    public static void Assign(HelpGroup group, params Option[] options);

    /// <summary>The group of <paramref name="option"/>, or null (the verb's own options).</summary>
    public static HelpGroup? GroupOf(Option option);

    /// <summary>
    /// Per-command override: on <paramref name="cmd"/>, <paramref name="group"/> is the command's PRIMARY content —
    ///  shown first and in Full mode. E.g. the new-virtual-media group on create-virtual.
    /// </summary>
    public static void Promote(Command cmd, HelpGroup group);

    /// <summary>
    /// Shows <paramref name="option"/> ALSO in <paramref name="group"/>, with <paramref name="description"/> in place of
    ///  its own. The option keeps its home group; a reference never targets the home.
    /// </summary>
    public static void Reference(HelpGroup group, Option option, string description);

    /// <summary>The references into <paramref name="group"/>, in registration order.</summary>
    public static IReadOnlyList<HelpReference> ReferencesIn(HelpGroup group);
}
```

- **Storage:** one `ConditionalWeakTable` per relation (option → home, command → promoted group), plus a locked list for references.
  - Verb-local options and commands are created anew by every `RootCommandFactory.Create` (once per test), so the registry must not keep them alive.
  - The tables are thread-safe, which parallel tests need.
- **One home, enforced:** `Assign` throws `InvalidOperationException` when an option already has a *different* home group. Re-assigning the same home does nothing. `Reference` throws when it targets the option's home.
- **Built-ins by type:** System.CommandLine creates its `HelpOption` and `VersionOption` per root. `GroupOf` places them in *Output and diagnostics* by type, instead of registering every instance.
- **Registration** happens in the static constructor of each option class: `GlobalOptions`, `VirtualMediaOptions`, `FilterOptions`, `PasswordOptions`. An option instance in use therefore implies its registration. `BackupCommand` assigns its local `--encrypt` / `--encryption` to *Encryption*.
- `HelpGroups.ReferencesOf(option)` complements `ReferencesIn(group)`: the model walks options, not groups.
- **Rendering rules:**
  - A row shows only for a command that accepts the option: its own options, plus its ancestors' recursive ones.
  - Rows follow the command's attachment order. `--help` comes last.
  - **Full** mode: a reference row has the home row's name column, the reference's description, and no default.
  - **Summary** mode: canonical names only (no aliases), joined by two spaces, wrapped between names.
  - **Promotion:** the promoted group's rows render under *Options*. A reference **into** the promoted group **replaces** the option's home row on that command. On `create-virtual`, `--initiator` therefore shows once, under *Options*, with its create-time description. No *Drive selection* section holds `--initiator` alone under an intro ("At most one; none opens physical drive 0") that does not fit.
  - Empty sections never render.

### 4.3 The groups

| Key | Title | Members | Order | Mode |
|---|---|---|---|---|
| — | **Options** | the verb's own options | 0 | Full |
| `filter` | **File selection** | `--filter`, `--filter-file` | 10 | Full |
| `encryption` | **Encryption** | `--password`, `--new-password-file`, `--new-password-env`, `--new-password` (rename-set). On backup, `--encrypt` / `--encryption` are attached **before** the password options, so the section opens with the switch. | 20 | Full |
| `drive` | **Drive selection** | `--drive`, `--virtual-open`, `--virtual-create`, `--in-memory`, `--initiator` | 30 | Full |
| `virtual-new` | **New virtual media** | `--capacity`, `--initiator` (ref.), `--init-capacity`, `--setmarks`, `--seq-filemarks`, `--filemarks-only`, `--min-/--def-/--max-block-size`, `--overwrite-virtual` | 40 | **Summary** |
| `diagnostics` | **Output and diagnostics** | `--log-level`, `--quiet`, `--no-color`, `--help` | 90 | Full |

Intros (one line each, under the title):

- **Drive selection** — "At most one; none opens physical drive 0."
- **New virtual media** — "Shape media created by --virtual-create. Ignored by --virtual-open."
- **File selection** — "Bare arguments are classified automatically; these bypass it."
- **Encryption** — "Without these, encrypted sets stay locked, or the password is asked."

`--initiator` has its **home** in *Drive selection*, because it also applies to `--virtual-open`. A **reference** in *New virtual media* covers its create-time role:

| Group | Description (as built) |
|---|---|
| Drive selection (home) | Initiator (TOC) partition file of file-backed virtual media. Default: PATH_init.ext beside the content file, when it exists. With --virtual-create: the file to create. |
| New virtual media (reference) | Initiator (TOC) partition file to create; giving it adds the partition. Default with only --init-capacity: PATH_init.ext. |

`GlobalOptions.Attach` attaches `--initiator` **after** `--init-capacity`. It closes the selectors in *Drive selection* (the §4.3 member order) and sits next to the capacities in *New virtual media*. Parsing is unaffected.


### 4.4 Summary mode — the key to the overload

On every verb except `create-virtual`, the eleven new-virtual-media options collapse to one paragraph:

```
New virtual media
  Shape media created by --virtual-create. Ignored by --virtual-open.
  --capacity  --init-capacity  --setmarks  --seq-filemarks  --filemarks-only
  --min-block-size  --def-block-size  --max-block-size  --overwrite-virtual
  Details: tapecon create-virtual --help
```

`create-virtual` promotes the group (`HelpGroups.Promote(cmd, HelpGroups.VirtualNew)`): there the options are its
content, so they render in full, first, under the title "Options".

The pointer line is part of the group (`HelpGroup.SummaryPointer`, a string), so the renderer stays generic.

### 4.5 Order and the "Global" question

The renderer sorts groups by `Order`, never by attachment order — so `GlobalOptions.Attach` may stay first in every
verb. Within a group, options keep their attachment order (the declaration order is deliberate: the selectors first).

There is no separate "Global options" heading: the old §5.2 idea is replaced by the *Drive selection* and *Output and
diagnostics* groups, which say what the options are for rather than where they are declared.

### 4.6 Recursive root options

`--quiet` and `--no-color` are added to the root only. `Program` pre-scans the arguments for them, so they take effect
— but on a verb, System.CommandLine sees them as unknown tokens. Making them recursive (System.CommandLine 2.0:
`Option.Recursive = true`) lets every verb parse them and lets the help list them in *Output and diagnostics*.
The pre-scan in `Program` stays: the banner needs the flags before parsing.

**As built:** `--quiet` / `--no-color` moved from locals in `RootCommandFactory` to `GlobalOptions.Quiet` / `NoColor`
(static, `Recursive = true`, home *Output and diagnostics*), attached once to the root. `Program` is unchanged:
its pre-scan still applies them before parsing.

### 4.7 Hidden and visible

- `Option.Hidden` / `Command.Hidden` are honoured; nothing is hidden today.
- Aliases are listed with the option (`--virtual-open, --virtual, -V`), the canonical name first.

## 5. Help rendering

### 5.1 Mechanism

System.CommandLine 2.0 lets an application replace the help option's **action**. `RootCommandFactory` replaces the
root `HelpOption`'s action with `TapeConHelpAction`, which receives the `ParseResult`, hence the command being helped
(`parseResult.CommandResult.Command`), and writes through `IConsoleUx.Write` — the same output path as the docs.

The help is built in two layers, as the docs are:

```
HelpModel.Build(Command cmd, Command root)     // pure: walks the symbols, applies the groups
  → HelpModel(Title, Description, Usage,
              IReadOnlyList<HelpArgument>,
              IReadOnlyList<HelpSection>,       // HelpSection(Group, IReadOnlyList<HelpOptionRow>)
              IReadOnlyList<HelpCommandRow>)    // subcommands, for the root and 'toc'
HelpRenderer.ToRenderables(model, width)
HelpRenderer.ToPlainLines(model, width)
```

- **Location:** `TapeConNET/Cli/Help/` — `HelpGroups.cs`, `HelpModel.cs`, `HelpRenderer.cs`,
  `TapeConHelpAction.cs`.
- `HelpModel.Build` is the only place that touches System.CommandLine's symbol API — one file to adapt when the
  package moves.
- `TapeConHelpAction : SynchronousCommandLineAction` overrides **`ClearsParseErrors => true`**. Since 2.0 RC1, a custom help action no longer clears parse errors implicitly. Without the override, `backup --help` would fail on the missing `<files>`.
- The root shows as `tapecon` (`HelpModel.RootName`), not `RootCommand`'s executable name, which in tests is the test host's.
- `HelpModel.Build(Command cmd)` takes no root parameter; it walks `Parents`.
- Option value types come from the generic `Option<T>` base type, not from a `ValueType` member.
- The default comes from `Option.HasDefaultValue` / `GetDefaultValue()`. A throwing factory yields no default.


### 5.2 Layout

| Section | Content | Rich style | Plain |
|---|---|---|---|
| Title | `tapecon <verb>` — aliases in parentheses | bold cyan | as is |
| Description | `Command.Description` | wrapped | wrapped |
| Usage | `tapecon <verb> [<args>...] [options]` | bold | as is |
| Arguments | name, arity marker (`...`), description | name themed | two columns |
| Option groups | §4.3 order; per row: aliases, value placeholder, description, default | names cyan, placeholder grey, default dimmed | two columns |
| Commands | subcommands with descriptions (root, `toc`) | names themed | two columns |
| Footer | root only: "Docs: tapecon docs concepts · exit codes: tapecon docs faq" | dimmed | as is |

Option rows:

- **Placeholder:** `HelpName` when set (`PATH`, `SIZE`), else derived from the value type — none for `bool`,
  `N` for integers, `TEXT` for strings, the choices for enums (`None|Crc32|…`).
- **Default:** shown when the option has a default value factory and the value is not the type default —
  e.g. `[default: Crc32]` for `--hash`. Descriptions that already state their default keep it in the text.
- **Wrapping:** the description column wraps at the console width (§6); the name column is sized to the widest name
  in the *whole help*, capped at 34 characters — longer names push their description to the next line.
- **References:** same name column as the home row; the description comes from the `HelpReference`; the default is not repeated.
- **Placeholder** of `string` options without `HelpName`: `TEXT`. Of other unknown types: `VALUE`. Step 3 added `HelpName`s where the shared option files were touched anyway: `--filter-file`, `--password-file`, `--new-password-file` (PATH); `--password-env`, `--new-password-env` (VARIABLE).
- **Aliases:** canonical name first, then long (`--`) before short (`-`). Windows-style `/h`, `/?` are omitted.
- **Commands** rows: name and aliases (`create-virtual, new-virtual`).
- **Rich form (Step 3):** the plain lines, unstyled. Step 4 replaces it.

### 5.3 Example — `tapecon backup --help`

```
tapecon backup
  Back up files/folders to the loaded media. Each positional argument is a file path, folder path, or wildcard pattern.

Usage: tapecon backup <files>... [options]

Arguments
  <files>                        Files, folders, or wildcard patterns to back up. ...

Options
  --description, --desc, -D TEXT Backup-set description written into the TOC.
  --subdirs, -s                  Recurse into subdirectories when expanding folders/patterns.
  --incremental, -i              Create an incremental backup set ...
  --block-size, -b SIZE          Block size ... Accepts B, KB/KiB, ...
  --hash, -H <None|Crc32|...>    Per-file hash algorithm.  [default: Crc32]
  ...
  --overwrite-media              Overwrite the backup sets on the loaded media without a prompt. ...

File selection
  Bare arguments are classified automatically; these bypass it.
  --filter TEXT                  ...
  --filter-file PATH             ...

Encryption
  Without these, encrypted sets stay locked, or the password is asked.
  --encrypt, -e                  ...
  --password-file PATH           ...

Drive selection
  At most one; none opens physical drive 0.
  --drive, -d N                  ...
  --virtual-open, --virtual, -V PATH
                                 Open EXISTING file-backed virtual media; ...
  --virtual-create, --virtual-new PATH
                                 ...

New virtual media
  Shape media created by --virtual-create. Ignored by --virtual-open.
  --capacity  --init-capacity  --setmarks  --seq-filemarks  --filemarks-only
  --min-block-size  --def-block-size  --max-block-size  --overwrite-virtual
  Details: tapecon create-virtual --help

Output and diagnostics
  --log-level <Trace|...>        ...  [default: None]
  --quiet, -q                    ...
  --no-color                     ...
  --help, -h, -?                 Show help and usage information.
```

### 5.4 Errors and help

A parse error keeps System.CommandLine's behaviour (error lines, exit 2). The help action is reached only through
`--help`; the root's own action keeps its "No verb specified" hint.

## 6. Shared pieces

- **Theme:** `ConsoleTheme` gains the presentation styles `Heading` (bold cyan), `OptionName` (cyan), `Placeholder` (grey), `Dimmed` (dim), and `Code` (cyan). They are parsed from the same markup words the banner uses, so they cannot drift from hand-written markup. Both renderers take colours only from there.
- **Level icons:** Completed `✓`, Info `ℹ`, Warning `⚠`, **Failed `✗`** (U+2717, slanted), **Error `✖`** (U+2716, heavy straight). Failed and Error differ by glyph as well as colour, so they stay distinct under `--no-color` and in redirected output.
- **Plain mode:** `ConsoleCapabilities.UsePlain(noColor, quiet, redirected)` = `noColor || quiet || redirected`.
- **Width:** `ConsoleCapabilities.ClampWidth(consoleWidth, redirected)` = 80 when the output is redirected or the console reports no width; otherwise the console width clamped to [60, 120].
- **Redirection** comes from the console, not the helper: `SpectreConsoleUx` asks `Profile.Out.IsTerminal`, so an injected console decides for itself; `SilentConsoleUx` always counts as redirected. Both helpers are pure functions taking flags rather than an `IConsoleUx`, so the interface does not grow.
- **Output:** `IConsoleUx.Write(ConsoleDocument)` (§3.4) serves docs and help alike.
- `SubHeading` (cyan, `###`) and `CodeBlock` (grey, code blocks).


## 7. Testing

| Area | Tests | How |
|---|---|---|
| Markdown parser | each block kind; inline spans; escapes; unclosed markers; unsupported syntax passes through | `MarkdownTests`, pure |
| Markdown renderers | plain fixture line by line; wrapping with hanging indent; table shrinking; rich rendering escapes brackets | `MarkdownTests`, pure + Spectre test console |
| Doc subset | every embedded topic: no Markdown syntax in plain prose; rich render does not throw | `MarkdownTests`, renders the real resources |
| `tapecon docs` | topics, default topic, `--list`, unknown topic; output carries no `#`/`##` lines and no `|---` | `DocsCommandTests` via `RunResult.Output` |
| Help groups | second home throws; reference into home throws; `--initiator` home + reference; built-ins in *Output and diagnostics* | `HelpModelTests` |
| Help model | **every command of the real tree:** sections non-empty and ordered; no option twice in a section; every visible option shown; exactly one home row (zero or one on a promoting command); **no alias collides with a recursive option** | `HelpModelTests`, `HelpModel.Build` |
| Help output | every verb `--help` exits 0; root lists commands and the footer; backup's section order; summary line and pointer; recursive options and `--help` last; create-virtual promotion; long names push the description down; lines fit 80 columns | `CliParsingTests` via `RunResult.Output` |
| Recursive options | `docs --quiet` / `-q` / `--no-color`; `backup --quiet --no-color --help` | `CliParsingTests` |
| Output path | `SilentConsoleUx.Write` / `SpectreConsoleUx.Write` (redirected): plain lines only, no timestamp, not quiet-suppressed, at the decided width; `ConsoleCapabilities` rules; Error/Failed icons | `ConsoleOutputTests` (Step 1) |

Existing `Help_ForVerb_ReturnsOk` tests keep passing.

## 8. Plan

Each step ends green and is usable on its own. The file lists name what to attach for the step — sources I need to
read, plus the tests I extend. "(new)" files I create; you need not attach them.

### Step 1 — Output path and plain-mode helpers

Content (as built): `Ux/ConsoleDocument.cs` (new), `Ux/ConsoleCapabilities.cs` (new), `IConsoleUx.Write`, implementations in `SpectreConsoleUx` / `SilentConsoleUx` (plus `SilentConsoleUx.Width`), presentation styles and the Error icon in `ConsoleTheme`. No visible change except the Error icon.

Exit: builds; all tests green; `ConsoleOutputTests` (new).

| Attach | Why |
|---|---|
| `Ux/IConsoleUx.cs` | gains `Write` |
| `Ux/SpectreConsoleUx.cs`, `Ux/SilentConsoleUx.cs` | implement it |
| `Ux/ConsoleTheme.cs` | new styles |
| `Ux/LogEntry.cs`, `Ux/WarningLevel.cs` | context for the theme |
| `Program.cs` | where plain/quiet are decided today |
| `Helpers/TapeConHost.cs` | output capture in tests |

### Step 2 — Markdown renderer and `docs`

Content (as built): `Ux/Markdown/MarkdownDocument.cs`, `Ux/Markdown/MarkdownRenderer.cs` (new); `ConsoleTheme.SubHeading` / `CodeBlock`; `DocsCommand` switched (`--list` included; `TopicNames` and `LoadTopic` internal for the tests); `MarkdownTests` (new); `DocsCommandTests` read `Output` instead of `Entries`.

Exit: `tapecon docs` renders themed on a console, plain when redirected; all tests green. Plain under `--no-color` / `--quiet` **on the verb** waits for Step 3 (recursive root options); the root form, which the pre-scan in `Program` already honours, works now.

| Attach | Why |
|---|---|
| `Cli/DocsCommand.cs` | switched to the renderer |
| `Resources/Docs/concepts.md`, `faq.md`, `migration-from-1.0.md` | the subset to support; fixtures |
| `DocsCommandTests.cs` | extended |
| `TapeConNET.csproj` | embedded-resource names, test access (`InternalsVisibleTo`) |
| Step 1 outputs (as applied) | `ConsoleDocument`, `ConsoleCapabilities` |

### Step 3 — Help groups and the help model (plain)

Content (as built):
- New: `Cli/Help/HelpGroups.cs`, `HelpModel.cs`, `HelpRenderer.cs` (plain), `TapeConHelpAction.cs`.
- Registrations: `GlobalOptions` (adds `Quiet` / `NoColor`), `VirtualMediaOptions`, `FilterOptions`, `PasswordOptions`, `BackupCommand`.
- Promotion: `CreateVirtualCommand`.
- `RootCommandFactory` installs the help action.
- Tests: `CliParsingTests` (extended), `HelpModelTests` (new).
- Unchanged: `Program`, `TapeConHost`, `SizeOptions`.

Exit: `--help` shows the grouped layout in plain text everywhere; all tests green.

| Attach | Why |
|---|---|
| `Cli/RootCommandFactory.cs` | installs the help action; recursive root options |
| `Cli/GlobalOptions.cs`, `Cli/VirtualMediaOptions.cs` | drive / virtual-new / diagnostics groups |
| `Cli/FilterOptions.cs`, `Cli/PasswordOptions.cs` | filter / encryption groups |
| `Cli/SizeOptions.cs` | `HelpName` conventions |
| `Cli/BackupCommand.cs` | the local `--encrypt` / `--encryption` join *Encryption* |
| `Cli/CreateVirtualCommand.cs` | promotion |
| `Program.cs` | pre-scan vs. recursive options |
| `CliParsingTests.cs` | home of the help tests |
| `Helpers/TapeConHost.cs` | — |
| Step 1 outputs (as applied) | — |

Also needed once, to pin the API: the exact **System.CommandLine package version** from `TapeConNET.csproj` — the help
action and symbol API (`HelpOption`, `Option.Recursive`, `Option.HasDefaultValue`) moved between 2.0 betas and GA.

### Step 4 — Coloured help

Content: rich half of `HelpRenderer.cs`; theme styles applied.

Exit: `--help` coloured on a console, plain when redirected; all tests green.

| Attach | Why |
|---|---|
| `Cli/Help/HelpRenderer.cs`, `HelpModel.cs` (as applied) | extended |
| `Ux/ConsoleTheme.cs`, `Ux/SpectreConsoleUx.cs` (as applied) | styles, output |
| `Ux/Markdown/MarkdownRenderer.cs` (as applied) | shared table/wrap helpers |

### Step 5 — Verb sweep: descriptions and placeholders

Content: review every option description for length and duplication now that the group intro carries context (e.g.
"of NEW virtual media" can go); add missing `HelpName`s; trim descriptions that restate defaults shown by §5.2.

Exit: every verb's help reviewed; all tests green.

| Attach | Why |
|---|---|
| `Cli/BackupCommand.cs`, `RestoreCommand.cs`, `ListCommand.cs`, `InfoCommand.cs` | operation verbs |
| `Cli/FormatCommand.cs`, `EjectCommand.cs`, `TocCommand.cs`, `RenameCommand.cs`, `DeleteSetsCommand.cs`, `ScanMediaCommand.cs` | lifecycle and TOC verbs |
| `Cli/CreateVirtualCommand.cs`, `DocsCommand.cs`, `DemoCommand.cs` | the rest |
| `Cli/GlobalOptions.cs`, `VirtualMediaOptions.cs`, `FilterOptions.cs`, `PasswordOptions.cs` | shared descriptions |
| one captured `--help` output per verb (plain, redirected to a file) | to review the result, not the code |

20 files exceed one upload; split it in two rounds — operation verbs + shared options first, the rest second.

### Step 6 — Docs content and architecture doc

Content: the embedded topics reviewed for the subset and for the current feature set (virtual media, `.vt`,
`--overwrite-virtual`, recorded mark profile, encryption); `TapeConNET-2.0-Architecture.md` §3 and §4 updated for
the help groups and the new files.

| Attach | Why |
|---|---|
| `Resources/Docs/concepts.md`, `faq.md`, `migration-from-1.0.md` | content |
| `docs/TapeConNET-2.0-Architecture.md` | as-built update |
| `Design-Encryption.md` (CLI sections only) | to keep the encryption help consistent |

## 9. Open points

| # | Point | Proposal |
|---|---|---|
| 1 | `--help` on the root: list the verbs grouped (lifecycle / write / read / TOC edits / survey / docs) as in the architecture doc's tree? | Yes — a `Command → group` table in `HelpGroups`, same model as options. Cheap once Step 3 exists. |
| 2 | A `--help-all` (or `--help full`) that expands Summary groups everywhere? | Not now: the pointer to `create-virtual --help` covers it with no new option. Revisit if users ask. |
| 3 | A generated `docs/cli-reference.md` from `HelpModel` + a drift test | Natural follow-up to Step 3: `HelpRenderer.ToMarkdown(model)` — the same model, a third output. |
