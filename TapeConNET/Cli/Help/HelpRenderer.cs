using Spectre.Console;
using Spectre.Console.Rendering;
using TapeConNET.Ux;
using TapeConNET.Ux.Markdown;

namespace TapeConNET.Cli.Help;

/// <summary>
/// Renders a <see cref="HelpModel"/> (Design-CliPresentation §5.2). Step 3: the plain form; the rich form shows the
///  same lines unstyled until Step 4 adds the colours.
/// </summary>
/// <remarks>
/// Two-column rows: names (+ placeholder) left, description right. The name column is as wide as the widest name in the
///  WHOLE help — so all sections line up — capped at <see cref="MaxNameColumn"/>; a longer name gets its own line and
///  its description starts on the next one. Text wraps with the docs' word wrapper (<see cref="MarkdownRenderer.Wrap"/>),
///  plus one help-specific rule: a word longer than the line breaks after a separator (<see cref="BreakAfter"/>).
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

    /// <summary>
    /// Where an overlong word may break — after one of these. Help texts join choices without spaces
    ///  (<c>Trace/Debug/…/None</c>, <c>None|Crc32|…</c>), which the docs' wrapper keeps whole as one word.
    /// </summary>
    private static readonly char[] BreakAfter = ['/', '|', ',', '\\'];

    /// <summary>Both forms of <paramref name="model"/>, ready for <see cref="IConsoleUx.Write"/>.</summary>
    public static ConsoleDocument ToDocument(HelpModel model)
        => new(
            Rich: width => ToRenderables(model, width),
            Plain: width => ToPlainLines(model, width));

    /// <summary>The rich form — Step 3: the plain lines, unstyled. Step 4 replaces it.</summary>
    public static IReadOnlyList<IRenderable> ToRenderables(HelpModel model, int width)
        => [new Text(string.Join('\n', ToPlainLines(model, width)))];

    #region *** Plain ***

    /// <summary>The plain form: one string per line, no markup.</summary>
    public static IReadOnlyList<string> ToPlainLines(HelpModel model, int width)
    {
        var lines = new List<string>();
        int nameColumn = NameColumn(model);

        // ── Title, description, usage ──
        lines.Add(model.Title);
        if (!string.IsNullOrWhiteSpace(model.Description))
            lines.AddRange(Indented(Wrap(model.Description, width - Indent), Indent));

        lines.Add(string.Empty);
        lines.AddRange(Wrap(model.Usage, width));

        // ── Arguments ──
        if (model.Arguments.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Arguments");
            foreach (var argument in model.Arguments)
                lines.AddRange(TwoColumns(argument.Name, argument.Description, nameColumn, width));
        }

        // ── Option sections ──
        foreach (var section in model.Sections)
        {
            lines.Add(string.Empty);
            lines.Add(section.Title);

            if (!string.IsNullOrWhiteSpace(section.Intro))
                lines.AddRange(Indented(Wrap(section.Intro, width - Indent), Indent));

            if (section.Mode == HelpGroupMode.Summary)
            {
                // Canonical names only: the aliases belong to the full description, where the pointer leads
                lines.AddRange(Indented(NameList(section.Rows.Select(r => r.Option.Name), width - Indent), Indent));
                if (section.Group.SummaryPointer is { } pointer)
                    lines.Add(new string(' ', Indent) + pointer);
                continue;
            }

            foreach (var row in section.Rows)
                lines.AddRange(TwoColumns(FirstColumn(row), SecondColumn(row), nameColumn, width));
        }

        // ── Subcommands ──
        if (model.Commands.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Commands");
            foreach (var command in model.Commands)
                lines.AddRange(TwoColumns(command.Names, command.Description, nameColumn, width));
        }

        if (model.Footer is { } footer)
        {
            lines.Add(string.Empty);
            lines.AddRange(Wrap(footer, width));
        }

        return lines;
    }

    #endregion

    #region *** Columns ***

    /// <summary>Names and placeholder: <c>--block-size, -b SIZE</c>.</summary>
    internal static string FirstColumn(HelpOptionRow row)
        => row.Placeholder is null ? row.Names : $"{row.Names} {row.Placeholder}";

    /// <summary>The description, and the default after it: <c>… [default: Crc32]</c>.</summary>
    internal static string SecondColumn(HelpOptionRow row)
        => row.Default is null ? row.Description ?? string.Empty : $"{row.Description} [default: {row.Default}]".TrimStart();

    /// <summary>The widest first column of the whole help — Full sections, arguments, commands — capped.</summary>
    private static int NameColumn(HelpModel model)
    {
        var firsts = model.Arguments.Select(a => a.Name)
            .Concat(model.Sections.Where(s => s.Mode == HelpGroupMode.Full).SelectMany(s => s.Rows).Select(FirstColumn))
            .Concat(model.Commands.Select(c => c.Names));

        return Math.Min(firsts.Select(f => f.Length).DefaultIfEmpty(0).Max(), MaxNameColumn);
    }

    /// <summary>
    /// One row: <paramref name="first"/> padded to <paramref name="nameColumn"/>, the wrapped description beside it and
    ///  under it. A first column wider than <paramref name="nameColumn"/> stands alone; the description follows below.
    /// </summary>
    private static IEnumerable<string> TwoColumns(string first, string? second, int nameColumn, int width)
    {
        string head = new string(' ', Indent) + first;
        string hang = new(' ', Indent + nameColumn + Gap);

        List<string> text = string.IsNullOrWhiteSpace(second) ? [] : Wrap(second, width - hang.Length);
        if (text.Count == 0)
        {
            yield return head;
            yield break;
        }

        if (first.Length > nameColumn)
            yield return head;
        else
        {
            yield return head.PadRight(hang.Length) + text[0];
            text = [.. text.Skip(1)];
        }

        foreach (var line in text)
            yield return hang + line;
    }

    /// <summary>Names joined by two spaces, wrapped at <paramref name="width"/> — never inside a name.</summary>
    private static List<string> NameList(IEnumerable<string> names, int width)
    {
        var lines = new List<string>();
        var line = string.Empty;
        foreach (var name in names)
        {
            if (line.Length > 0 && line.Length + SummaryGap.Length + name.Length > width)
            {
                lines.Add(line);
                line = string.Empty;
            }

            line = line.Length == 0 ? name : line + SummaryGap + name;
        }

        if (line.Length > 0)
            lines.Add(line);
        return lines;
    }

    #endregion

    #region *** Wrapping ***

    /// <summary>
    /// Word wrap at <paramref name="width"/>, then <see cref="BreakOverlong"/> on every line still too long. The docs'
    ///  wrapper sets an overlong word on a line of its own, so such a line IS the word — breaking it disturbs nothing else.
    /// </summary>
    private static List<string> Wrap(string text, int width)
        => [.. MarkdownRenderer.Wrap([new MdSpan(MdSpanKind.Text, text)], width)
            .Select(spans => string.Concat(spans.Select(s => s.Text)))
            .SelectMany(line => BreakOverlong(line, width))];

    /// <summary>
    /// Breaks <paramref name="word"/> after the last <see cref="BreakAfter"/> character that still fits
    ///  <paramref name="width"/>. With none in reach, it breaks at the next one beyond — the piece overruns, but less.
    ///  A word without any separator stays whole: cutting <c>TapeLibNET</c> mid-letters reads worse than an overrun.
    /// </summary>
    /// <remarks>
    /// Never breaks after the word's last character — that would leave an empty piece. <paramref name="width"/> is
    ///  clamped as in <see cref="MarkdownRenderer.Wrap"/>, so a piece fits whatever line the wrapper made.
    /// </remarks>
    internal static IEnumerable<string> BreakOverlong(string word, int width)
    {
        width = Math.Max(width, 20);        // MarkdownRenderer's MinWrapWidth — the width the line was wrapped at

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

    private static IEnumerable<string> Indented(IEnumerable<string> lines, int indent)
        => lines.Select(l => new string(' ', indent) + l);

    #endregion
}
