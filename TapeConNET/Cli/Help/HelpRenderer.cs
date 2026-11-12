using Spectre.Console;
using Spectre.Console.Rendering;
using TapeConNET.Ux;

namespace TapeConNET.Cli.Help;

/// <summary>A run of help text in one style — <see cref="Style.Plain"/> for unstyled text, padding and separators.</summary>
internal readonly record struct HelpRun(string Text, Style Style);

/// <summary>
/// Renders a <see cref="HelpModel"/> (Design-CliPresentation §5.2) in both forms: plain lines and themed Spectre markup.
/// </summary>
/// <remarks>
/// <para>
/// ONE layout serves both forms (<see cref="Layout"/>): the help is laid out once as lines of styled runs; the plain
///  form concatenates the runs' texts, the rich form wraps each run in its style's markup. So the two forms break every
///  line at the same place and cannot disagree on structure — the plain form, which the tests pin, IS the rich form
///  without colours.
/// </para>
/// <para>
/// Two-column rows: names (+ placeholder) left, description right. The name column is as wide as the widest name in the
///  WHOLE help — so all sections line up — capped at <see cref="MaxNameColumn"/>; a longer name gets its own line and
///  its description starts on the next one. Text wraps greedily at spaces, as the docs' wrapper does, plus one
///  help-specific rule: a word longer than the line breaks after a separator (<see cref="BreakAfter"/>).
/// </para>
/// <para>
/// Styles (§5.2), all from <see cref="ConsoleTheme"/>: title and section titles <see cref="ConsoleTheme.Heading"/>; the
///  usage line <see cref="ConsoleTheme.Emphasis"/>; option, argument and command names <see cref="ConsoleTheme.OptionName"/>;
///  value placeholders <see cref="ConsoleTheme.Placeholder"/>; intros, <c>[default: …]</c>, the summary pointer and the
///  footer <see cref="ConsoleTheme.Dimmed"/>; descriptions unstyled.
/// </para>
/// </remarks>
public static class HelpRenderer
{
    /// <summary>The name column never grows beyond this; longer names push their description down a line.</summary>
    public const int MaxNameColumn = 34;

    /// <summary>Indentation of everything under a title.</summary>
    private const int Indent = 2;

    /// <summary>Spaces between the name column and the description.</summary>
    private const int Gap = 2;

    /// <summary>Spaces between two names in a Summary section.</summary>
    private const string SummaryGap = "  ";

    /// <summary>The narrowest width text is ever wrapped at — MarkdownRenderer's minimum, too.</summary>
    private const int MinWrapWidth = 20;

    /// <summary>
    /// Where an overlong word may break — after one of these. Help texts join choices without spaces
    ///  (<c>Trace/Debug/…/None</c>, <c>None|Crc32|…</c>), which a word wrapper keeps whole as one word.
    /// </summary>
    private static readonly char[] BreakAfter = ['/', '|', ',', '\\'];

    /// <summary>Both forms of <paramref name="model"/>, ready for <see cref="IConsoleUx.Write"/>.</summary>
    public static ConsoleDocument ToDocument(HelpModel model)
        => new(
            Rich: width => ToRenderables(model, width),
            Plain: width => ToPlainLines(model, width));

    /// <summary>The plain form: one string per line, no markup.</summary>
    public static IReadOnlyList<string> ToPlainLines(HelpModel model, int width)
        => [.. Layout(model, width).Select(line => string.Concat(line.Select(r => r.Text)))];

    /// <summary>
    /// The rich form: ONE <see cref="Markup"/> of all lines. The lines are final — wrapped at <paramref name="width"/>,
    ///  which the console clamped to its own width — so Spectre lays them out as they are.
    /// </summary>
    public static IReadOnlyList<IRenderable> ToRenderables(HelpModel model, int width)
        => [new Markup(string.Join('\n', Layout(model, width).Select(ToMarkup)))];

    #region *** Layout ***

    /// <summary>The help as lines of styled runs — the single source of both forms.</summary>
    internal static List<List<HelpRun>> Layout(HelpModel model, int width)
    {
        var lines = new List<List<HelpRun>>();
        int nameColumn = NameColumn(model);

        // ── Title, description, usage ──
        lines.Add([new(model.Title, ConsoleTheme.Heading)]);
        if (!string.IsNullOrWhiteSpace(model.Description))
            lines.AddRange(Indented(Wrap(Words(model.Description, Style.Plain), width - Indent), Indent));

        lines.Add([]);
        lines.AddRange(Wrap(Words(model.Usage, ConsoleTheme.Emphasis), width));

        // ── Arguments ──
        if (model.Arguments.Count > 0)
        {
            AddTitle(lines, "Arguments");
            foreach (var argument in model.Arguments)
                lines.AddRange(TwoColumns([new(argument.Name, ConsoleTheme.OptionName)],
                    Words(argument.Description, Style.Plain), nameColumn, width));
        }

        // ── Option sections ──
        foreach (var section in model.Sections)
        {
            AddTitle(lines, section.Title);

            if (!string.IsNullOrWhiteSpace(section.Intro))
                lines.AddRange(Indented(Wrap(Words(section.Intro, ConsoleTheme.Dimmed), width - Indent), Indent));

            if (section.Mode == HelpGroupMode.Summary)
            {
                // Canonical names only: the aliases belong to the full description, where the pointer leads
                lines.AddRange(Indented(NameList(section.Rows.Select(r => r.Option.Name), width - Indent), Indent));
                if (section.Group.SummaryPointer is { } pointer)
                    lines.Add([Pad(Indent), new(pointer, ConsoleTheme.Dimmed)]);
                continue;
            }

            foreach (var row in section.Rows)
                lines.AddRange(TwoColumns(FirstColumn(row), SecondColumn(row), nameColumn, width));
        }

        // ── Subcommands ──
        if (model.Commands.Count > 0)
        {
            AddTitle(lines, "Commands");
            foreach (var command in model.Commands)
                lines.AddRange(TwoColumns([new(command.Names, ConsoleTheme.OptionName)],
                    Words(command.Description, Style.Plain), nameColumn, width));
        }

        if (model.Footer is { } footer)
        {
            lines.Add([]);
            lines.AddRange(Wrap(Words(footer, ConsoleTheme.Dimmed), width));
        }

        return [.. lines.Select(Merge)];
    }

    /// <summary>An empty line, then <paramref name="title"/>.</summary>
    private static void AddTitle(List<List<HelpRun>> lines, string title)
    {
        lines.Add([]);
        lines.Add([new(title, ConsoleTheme.Heading)]);
    }

    #endregion

    #region *** Columns ***

    /// <summary>Names and placeholder: <c>--block-size, -b SIZE</c>.</summary>
    internal static List<HelpRun> FirstColumn(HelpOptionRow row)
        => row.Placeholder is null
            ? [new(row.Names, ConsoleTheme.OptionName)]
            : [new(row.Names, ConsoleTheme.OptionName), new(" ", Style.Plain), new(row.Placeholder, ConsoleTheme.Placeholder)];

    /// <summary>The description's words, then the default's: <c>… [default: Crc32]</c>.</summary>
    internal static List<HelpRun> SecondColumn(HelpOptionRow row)
    {
        var words = Words(row.Description, Style.Plain);
        if (row.Default is not null)
            words.AddRange(Words($"[default: {row.Default}]", ConsoleTheme.Dimmed));
        return words;
    }

    /// <summary>The widest first column of the whole help — Full sections, arguments, commands — capped.</summary>
    private static int NameColumn(HelpModel model)
    {
        var widths = model.Arguments.Select(a => a.Name.Length)
            .Concat(model.Sections.Where(s => s.Mode == HelpGroupMode.Full).SelectMany(s => s.Rows)
                .Select(row => Length(FirstColumn(row))))
            .Concat(model.Commands.Select(c => c.Names.Length));

        return Math.Min(widths.DefaultIfEmpty(0).Max(), MaxNameColumn);
    }

    /// <summary>
    /// One row: <paramref name="first"/> padded to <paramref name="nameColumn"/>, the wrapped description beside it and
    ///  under it. A first column wider than <paramref name="nameColumn"/> stands alone; the description follows below.
    /// </summary>
    private static IEnumerable<List<HelpRun>> TwoColumns(List<HelpRun> first, List<HelpRun> second, int nameColumn, int width)
    {
        List<HelpRun> head = [Pad(Indent), .. first];
        int hang = Indent + nameColumn + Gap;
        int firstLength = Length(first);

        List<List<HelpRun>> text = second.Count == 0 ? [] : Wrap(second, width - hang);
        if (text.Count == 0)
        {
            yield return head;
            yield break;
        }

        if (firstLength > nameColumn)
            yield return head;
        else
        {
            yield return [.. head, Pad(hang - Indent - firstLength), .. text[0]];
            text.RemoveAt(0);
        }

        foreach (var line in text)
            yield return [Pad(hang), .. line];
    }

    /// <summary>Names joined by two spaces, wrapped at <paramref name="width"/> — never inside a name.</summary>
    private static List<List<HelpRun>> NameList(IEnumerable<string> names, int width)
    {
        var lines = new List<List<HelpRun>>();
        var line = new List<HelpRun>();
        int length = 0;

        foreach (var name in names)
        {
            if (length > 0 && length + SummaryGap.Length + name.Length > width)
            {
                lines.Add(line);
                line = [];
                length = 0;
            }

            if (length > 0)
            {
                line.Add(new(SummaryGap, Style.Plain));
                length += SummaryGap.Length;
            }

            line.Add(new(name, ConsoleTheme.OptionName));
            length += name.Length;
        }

        if (length > 0)
            lines.Add(line);
        return lines;
    }

    #endregion

    #region *** Wrapping ***

    /// <summary>The words of <paramref name="text"/> (split at spaces, empty ones dropped), each a run of <paramref name="style"/>.</summary>
    private static List<HelpRun> Words(string? text, Style style)
        => text is null
            ? []
            : [.. text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => new HelpRun(w, style))];

    /// <summary>
    /// Greedy word wrap of <paramref name="words"/> at <paramref name="width"/>, then <see cref="BreakOverlong"/> on a
    ///  word longer than the line. Such a word always stands alone — the wrapper flushes the line before any word that
    ///  does not fit — so breaking it disturbs nothing else. Returns at least one (possibly empty) line.
    /// </summary>
    private static List<List<HelpRun>> Wrap(List<HelpRun> words, int width)
    {
        width = Math.Max(width, MinWrapWidth);

        var lines = new List<List<HelpRun>>();
        var line = new List<HelpRun>();
        int length = 0;

        foreach (var word in words)
        {
            if (length > 0 && length + 1 + word.Text.Length > width)
            {
                lines.Add(line);
                line = [];
                length = 0;
            }

            if (length > 0)
            {
                // The space takes the style of both neighbours when they agree — one styled run per phrase, not per word
                line.Add(new(" ", line[^1].Style.Equals(word.Style) ? word.Style : Style.Plain));
                length++;
            }

            line.Add(word);
            length += word.Text.Length;
        }

        lines.Add(line);

        var result = new List<List<HelpRun>>();
        foreach (var l in lines)
        {
            if (l.Count == 1 && l[0].Text.Length > width)
                result.AddRange(BreakOverlong(l[0].Text, width).Select(piece => new List<HelpRun> { new(piece, l[0].Style) }));
            else
                result.Add(l);
        }

        return result;
    }

    /// <summary>
    /// Breaks <paramref name="word"/> after the last <see cref="BreakAfter"/> character that still fits
    ///  <paramref name="width"/>. With none in reach, it breaks at the next one beyond — the piece overruns, but less.
    ///  A word without any separator stays whole: cutting <c>TapeLibNET</c> mid-letters reads worse than an overrun.
    /// </summary>
    /// <remarks>
    /// Never breaks after the word's last character — that would leave an empty piece. <paramref name="width"/> is
    ///  clamped as in <see cref="Wrap"/>, so a piece fits whatever line the wrapper made.
    /// </remarks>
    internal static IEnumerable<string> BreakOverlong(string word, int width)
    {
        width = Math.Max(width, MinWrapWidth);

        while (word.Length > width)
        {
            // Last separator inside the first `width` characters; else the first one after them
            int cut = word.LastIndexOfAny(BreakAfter, width - 1);
            if (cut < 0)
                cut = word.IndexOfAny(BreakAfter, width);

            if (cut < 0 || cut == word.Length - 1)
                break;

            yield return word[..(cut + 1)];
            word = word[(cut + 1)..];
        }

        yield return word;
    }

    #endregion

    #region *** Runs ***

    private static HelpRun Pad(int count) => new(new string(' ', count), Style.Plain);

    private static int Length(List<HelpRun> runs) => runs.Sum(r => r.Text.Length);

    private static IEnumerable<List<HelpRun>> Indented(IEnumerable<List<HelpRun>> lines, int indent)
        => lines.Select(line => (List<HelpRun>)[Pad(indent), .. line]);

    /// <summary>Drops empty runs and joins neighbours of one style — compact markup, same text.</summary>
    private static List<HelpRun> Merge(List<HelpRun> line)
    {
        var merged = new List<HelpRun>();
        foreach (var run in line.Where(r => r.Text.Length > 0))
        {
            if (merged.Count > 0 && merged[^1].Style.Equals(run.Style))
                merged[^1] = merged[^1] with { Text = merged[^1].Text + run.Text };
            else
                merged.Add(run);
        }

        return merged;
    }

    /// <summary>Spectre markup of a line: every text escaped — <c>[default: …]</c> is text, not a tag — then styled.</summary>
    private static string ToMarkup(List<HelpRun> line)
        => string.Concat(line.Select(run =>
        {
            string text = Markup.Escape(run.Text);
            string words = run.Style.ToMarkup();        // Style.Plain has none — "[]" would be a markup error
            return words.Length == 0 ? text : $"[{words}]{text}[/]";
        }));

    #endregion
}
