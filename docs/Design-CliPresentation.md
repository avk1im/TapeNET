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

MarkdownRenderer.ToRenderables(blocks, width)     // Spectre: IReadOnlyList<IRenderable>
MarkdownRenderer.ToPlainLines(blocks, width)      // plain:   IReadOnlyList<string>
```

- **Location:** `TapeConNET/Ux/Markdown/` — `MarkdownDocument.cs` (model + parser), `MarkdownRenderer.cs`.
- **No console access** in either: both are pure functions, unit-tested directly.

### 3.4 Output path

- `IConsoleUx` gains `void Write(ConsoleDocument doc)`, where `ConsoleDocument` carries **both** forms:
  `IReadOnlyList<IRenderable> Rich` and `IReadOnlyList<string> Plain`.
  - `SpectreConsoleUx` writes `Rich`, or `Plain` when `ConsoleCapabilities.UsePlain(this)` (§6).
  - `SilentConsoleUx` always writes `Plain` to its `TextWriter` — tests see exactly what a redirected console sees.
- Unlike `Log`, `Write` adds no timestamp and no icon, and is **not** suppressed by `--quiet`: the user asked for the
  text explicitly.
- `DocsCommand` parses the topic and calls `ux.Write(...)` instead of logging line by line.

### 3.5 Contract for the docs

The documents stay within §3.2. A unit test renders every embedded topic through `ToPlainLines` and asserts that no
literal `|---`, `## `, `**` or backtick survives — so a doc edit that leaves the subset fails the build.

## 4. Structured help

### 4.1 Model

An option belongs to at most one **help group**. A group has a stable key, a title, an optional one-line intro, an
order, and a display mode.

```csharp
/// <summary>A titled section of a command's help.</summary>
public sealed record HelpGroup(
    string Key,             // "drive", "virtual-new", ...
    string Title,           // "Drive selection"
    string? Intro,          // one line under the title; may name the defaults
    int Order,              // ascending; the verb's own options are always first
    HelpGroupMode Mode = HelpGroupMode.Full);

public enum HelpGroupMode
{
    /// <summary>Every option with its description.</summary>
    Full,
    /// <summary>One line: the option names only, plus a pointer to where they are described in full.</summary>
    Summary,
}
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
}
```

- Storage: a `Dictionary<Option, HelpGroup>` (reference keys) and a `Dictionary<Command, HelpGroup>` for promotions,
  both filled during the static initialisation of the option classes and during `RootCommandFactory.Create`.
- Each option class registers its own options in its static constructor or right after the fields:
  `GlobalOptions` (drive, diagnostics), `VirtualMediaOptions` (virtual-new), `FilterOptions` (filter),
  `PasswordOptions` + the encryption options (encryption).
- Verb-local options stay unassigned — no change in the verbs except where a verb promotes a group.

### 4.3 The groups

| Key | Title | Members | Order | Mode |
|---|---|---|---|---|
| — | **Options** | the verb's own options | 0 | Full |
| `filter` | **File selection** | `--filter`, `--filter-file` | 10 | Full |
| `encryption` | **Encryption** | `--password-file`, `--password-env`, (backup:) `--encrypt`, `--encryption` | 20 | Full |
| `drive` | **Drive selection** | `--drive`, `--virtual-open`, `--virtual-create`, `--in-memory`, `--initiator` | 30 | Full |
| `virtual-new` | **New virtual media** | `--capacity`, `--init-capacity`, `--setmarks`, `--seq-filemarks`, `--filemarks-only`, `--min-/--def-/--max-block-size`, `--overwrite-virtual` | 40 | **Summary** |
| `diagnostics` | **Output and diagnostics** | `--log-level`, `--quiet`, `--no-color`, `--help` | 90 | Full |

Intros (one line each, under the title):

- **Drive selection** — "At most one; none opens physical drive 0."
- **New virtual media** — "Shape media created by --virtual-create. Ignored by --virtual-open."
- **File selection** — "Bare arguments are classified automatically; these bypass it."
- **Encryption** — "Without these, encrypted sets stay locked, or the password is asked."

`--initiator` sits in *Drive selection*, not *New virtual media*, because it also applies to `--virtual-open`.

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

- **Theme:** `ConsoleTheme` gains the presentation styles — `Heading`, `OptionName`, `Placeholder`, `Dimmed`,
  `Code` — next to the existing level styles. Both renderers take colours only from there.
- **Width:** `ConsoleCapabilities.Width(ux)` = console width, minimum 60, maximum 120 (long lines read badly);
  80 when output is redirected.
- **Plain mode:** `ConsoleCapabilities.UsePlain(ux)` = `NoColor || QuietMode || output redirected`.
- **Output:** `IConsoleUx.Write(ConsoleDocument)` (§3.4) serves docs and help alike.

## 7. Testing

| Area | Tests | How |
|---|---|---|
| Markdown parser | each block kind; inline spans; malformed input passes through | pure unit tests |
| Markdown renderers | plain output of a fixture document, line by line | pure unit tests |
| Doc subset | every embedded topic: no `\|---`, `## `, `**`, backtick in plain output | renders the real resources |
| Help groups | every option attached to a drive verb has a group or is verb-local; no option in two groups | reflection over the built root |
| Help model | per verb: every visible option appears exactly once; groups in order; verb's own group first | `HelpModel.Build` directly |
| Help output | `tapecon <verb> --help` exits 0 and its plain output contains the group titles and the verb's own options; `create-virtual` shows the virtual-new options in full | `TapeConHost` |
| Recursive options | `tapecon backup --quiet …` parses | `TapeConHost` |

Existing `Help_ForVerb_ReturnsOk` tests keep passing.

## 8. Plan

Each step ends green and is usable on its own. The file lists name what to attach for the step — sources I need to
read, plus the tests I extend. "(new)" files I create; you need not attach them.

### Step 1 — Output path and plain-mode helpers

Content: `ConsoleDocument`, `IConsoleUx.Write`, `ConsoleCapabilities` (`UsePlain`, `Width`), presentation styles in
`ConsoleTheme`. No visible change yet.

Exit: builds; all tests green; a unit test for `SilentConsoleUx.Write`.

| Attach | Why |
|---|---|
| `Ux/IConsoleUx.cs` | gains `Write` |
| `Ux/SpectreConsoleUx.cs`, `Ux/SilentConsoleUx.cs` | implement it |
| `Ux/ConsoleTheme.cs` | new styles |
| `Ux/LogEntry.cs`, `Ux/WarningLevel.cs` | context for the theme |
| `Program.cs` | where plain/quiet are decided today |
| `Helpers/TapeConHost.cs` | output capture in tests |

### Step 2 — Markdown renderer and `docs`

Content: `Ux/Markdown/MarkdownDocument.cs` (new), `Ux/Markdown/MarkdownRenderer.cs` (new); `DocsCommand` switched;
parser/renderer unit tests; the doc-subset test.

Exit: `tapecon docs` renders themed; plain under `--no-color`; all tests green.

| Attach | Why |
|---|---|
| `Cli/DocsCommand.cs` | switched to the renderer |
| `Resources/Docs/concepts.md`, `faq.md`, `migration-from-1.0.md` | the subset to support; fixtures |
| `DocsCommandTests.cs` | extended |
| `TapeConNET.csproj` | embedded-resource names, test access (`InternalsVisibleTo`) |
| Step 1 outputs (as applied) | `ConsoleDocument`, `ConsoleCapabilities` |

### Step 3 — Help groups and the help model (plain)

Content: `Cli/Help/HelpGroups.cs`, `HelpModel.cs`, plain half of `HelpRenderer.cs`, `TapeConHelpAction.cs` (all new);
registrations in the option classes; recursive `--quiet` / `--no-color`; `create-virtual` promotion; help tests.

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
