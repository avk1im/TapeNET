using Spectre.Console;
using Spectre.Console.Rendering;

namespace TapeConNET.Ux.Markdown;

/// <summary>
/// Renders parsed Markdown blocks (<see cref="MarkdownDocument"/>) twice: as Spectre renderables and as plain lines
///  (Design-CliPresentation §3.2, §3.3). Pure: no console access; the width comes in as a parameter.
/// </summary>
/// <remarks>
/// <para>
/// ONE word wrapper serves both forms (<see cref="Wrap"/>): the rich form receives text already broken into lines,
///  so Spectre never re-wraps it, and the two forms break every paragraph at the same words. Only on a console
///  narrower than <see cref="ConsoleCapabilities.MinWidth"/> does Spectre wrap the rich lines once more.
/// </para>
/// <para>
/// Blocks are separated by one empty line. Colours come only from <see cref="ConsoleTheme"/>.
/// </para>
/// </remarks>
public static class MarkdownRenderer
{
    /// <summary>Spaces between two plain table columns.</summary>
    private const int ColumnGap = 2;

    /// <summary>A plain table column never shrinks below this to make the table fit.</summary>
    private const int MinColumnWidth = 8;

    /// <summary>The narrowest width text is ever wrapped at — even inside a deeply indented list.</summary>
    private const int MinWrapWidth = 20;

    /// <summary>Both forms of <paramref name="blocks"/>, ready for <see cref="IConsoleUx.Write"/>.</summary>
    public static ConsoleDocument ToDocument(IReadOnlyList<MdBlock> blocks)
        => new(
            Rich: width => ToRenderables(blocks, width),
            Plain: width => ToPlainLines(blocks, width));

    #region *** Plain ***

    /// <summary>The plain form: no markup, no ANSI, one string per line, at most <paramref name="width"/> wide
    ///  except for words and code lines longer than that.</summary>
    public static IReadOnlyList<string> ToPlainLines(IReadOnlyList<MdBlock> blocks, int width)
    {
        var lines = new List<string>();

        foreach (var block in blocks)
        {
            if (lines.Count > 0)
                lines.Add(string.Empty);

            switch (block)
            {
                case MdHeading heading:
                    var title = Wrap(heading.Text, width).Select(PlainText).ToList();
                    lines.AddRange(title);

                    // # gets a double rule, ## a single one, ### none — the plain stand-in for size
                    char? rule = heading.Level switch { 1 => '═', 2 => '─', _ => null };
                    if (rule is { } r)
                        lines.Add(new string(r, title.Max(t => t.Length)));
                    break;

                case MdParagraph paragraph:
                    lines.AddRange(Wrap(paragraph.Text, width).Select(PlainText));
                    break;

                case MdList list:
                    foreach (var (prefix, indent, item) in ListLayout(list, rich: false))
                        lines.AddRange(Hang(Wrap(item.Text, width - indent - prefix.Length).Select(PlainText),
                            new string(' ', indent) + prefix));
                    break;

                case MdTable table:
                    AppendPlainTable(lines, table, width);
                    break;

                case MdCode code:
                    // Never wrapped: a command line broken in two no longer runs when pasted
                    lines.AddRange(code.Text.Split('\n').Select(l => l.Length == 0 ? l : "    " + l));
                    break;

                case MdQuote quote:
                    lines.AddRange(Wrap(quote.Text, width - 2).Select(l => "  " + PlainText(l)));
                    break;
            }
        }

        return lines;
    }

    private static void AppendPlainTable(List<string> lines, MdTable table, int width)
    {
        int columns = table.Header.Count;
        var rows = new List<IReadOnlyList<IReadOnlyList<MdSpan>>> { table.Header };
        rows.AddRange(table.Rows);

        // Natural widths first: each column as wide as its widest unwrapped cell
        var widths = new int[columns];
        foreach (var row in rows)
            for (int c = 0; c < columns; c++)
                widths[c] = Math.Max(widths[c], row[c].Sum(s => s.Text.Length));

        // Too wide: narrow the widest column, one character at a time — the long free-text column (a
        //  "Meaning", a "Description") gives way first, short code columns stay intact
        while (widths.Sum() + ColumnGap * (columns - 1) > width)
        {
            int widest = Array.IndexOf(widths, widths.Max());
            if (widths[widest] <= MinColumnWidth)
                break;
            widths[widest]--;
        }

        // Wrap every cell; a single word longer than its column widens the column again
        var cells = rows
            .Select(row => row.Select((cell, c) => Wrap(cell, widths[c]).Select(PlainText).ToList()).ToList())
            .ToList();
        for (int c = 0; c < columns; c++)
            widths[c] = Math.Max(widths[c], cells.Max(row => row[c].Max(l => l.Length)));

        string gap = new(' ', ColumnGap);
        for (int r = 0; r < cells.Count; r++)
        {
            int height = cells[r].Max(cell => cell.Count);
            for (int k = 0; k < height; k++)
            {
                var parts = cells[r].Select((cell, c) => Align(k < cell.Count ? cell[k] : string.Empty, widths[c], table.Align[c]));
                lines.Add(string.Join(gap, parts).TrimEnd());
            }

            // The header row is underlined, column by column
            if (r == 0)
                lines.Add(string.Join(gap, widths.Select(w => new string('─', w))));
        }
    }

    private static string Align(string text, int width, MdAlign align)
    {
        int pad = Math.Max(0, width - text.Length);
        return align switch
        {
            MdAlign.Right => new string(' ', pad) + text,
            MdAlign.Center => new string(' ', pad / 2) + text + new string(' ', pad - pad / 2),
            _ => text + new string(' ', pad),
        };
    }

    #endregion

    #region *** Rich ***

    /// <summary>The rich form: Spectre renderables, one per block, empty lines between them.</summary>
    public static IReadOnlyList<IRenderable> ToRenderables(IReadOnlyList<MdBlock> blocks, int width)
    {
        var result = new List<IRenderable>();

        foreach (var block in blocks)
        {
            // A single space, not an empty Text: an empty Text renders nothing — not even its line
            if (result.Count > 0)
                result.Add(new Text(" "));

            result.Add(block switch
            {
                MdHeading { Level: 1 } h => new Rule(Styled(ConsoleTheme.Heading, InlineMarkup(h.Text)))
                {
                    Justification = Justify.Left,
                    Style = ConsoleTheme.Heading,
                },

                MdHeading h => Lines(Wrap(h.Text, width)
                    .Select(l => Styled(h.Level == 2 ? ConsoleTheme.Heading : ConsoleTheme.SubHeading, InlineMarkup(l)))),

                MdParagraph p => Lines(Wrap(p.Text, width).Select(InlineMarkup)),

                MdList list => Lines(ListLayout(list, rich: true).SelectMany(x =>
                    Hang(Wrap(x.Item.Text, width - x.Indent - x.Prefix.Length).Select(InlineMarkup),
                        new string(' ', x.Indent) + x.Prefix))),

                MdTable table => RichTable(table, width),

                MdCode code => new Panel(new Text(code.Text, ConsoleTheme.CodeBlock))
                {
                    Border = BoxBorder.Rounded,
                    Expand = false,
                },

                MdQuote quote => Lines(Wrap(quote.Text, width - 2)
                    .Select(l => "  " + Styled(ConsoleTheme.Dimmed, InlineMarkup(l)))),

                _ => Text.Empty,
            });
        }

        return result;
    }

    private static Table RichTable(MdTable table, int width)
    {
        // Width caps the table; Spectre wraps the cells inside the cap itself
        var result = new Table { Border = TableBorder.Rounded, Width = width };

        for (int c = 0; c < table.Header.Count; c++)
        {
            result.AddColumn(new TableColumn(new Markup(Styled(ConsoleTheme.Heading, InlineMarkup(table.Header[c]))))
            {
                Alignment = table.Align[c] switch
                {
                    MdAlign.Right => Justify.Right,
                    MdAlign.Center => Justify.Center,
                    _ => Justify.Left,
                },
            });
        }

        foreach (var row in table.Rows)
            result.AddRow(row.Select(cell => (IRenderable)new Markup(InlineMarkup(cell))).ToArray());

        return result;
    }

    /// <summary>One <see cref="Markup"/> of markup lines — one renderable per block keeps the block spacing simple.</summary>
    private static Markup Lines(IEnumerable<string> markupLines) => new(string.Join('\n', markupLines));

    /// <summary>Spectre markup of inline spans: every text escaped, then the span's style applied.</summary>
    private static string InlineMarkup(IReadOnlyList<MdSpan> spans)
        => string.Concat(spans.Select(s => s.Kind switch
        {
            MdSpanKind.Code => Styled(ConsoleTheme.Code, Markup.Escape(s.Text)),
            MdSpanKind.Bold => $"[bold]{Markup.Escape(s.Text)}[/]",
            MdSpanKind.Italic => $"[italic]{Markup.Escape(s.Text)}[/]",
            _ => Markup.Escape(s.Text),
        }));

    /// <summary>Wraps ALREADY ESCAPED markup in <paramref name="style"/>.</summary>
    private static string Styled(Style style, string markup)
    {
        // Style.Plain has no markup words — "[]" would be a markup error
        string words = style.ToMarkup();
        return words.Length == 0 ? markup : $"[{words}]{markup}[/]";
    }

    #endregion

    #region *** Shared: lists, wrapping ***

    /// <summary>
    /// Per item: its prefix (bullet or number) and its indentation. Numbers are padded to the widest one, so the
    ///  texts of "9." and "10." line up. Nested items are bullets, two spaces deeper.
    /// </summary>
    private static IEnumerable<(string Prefix, int Indent, MdListItem Item)> ListLayout(MdList list, bool rich)
    {
        string bullet = rich ? "• " : "- ";
        int numberWidth = list.Items.Where(it => it.Level == 0).Select(it => $"{it.Number}.".Length).DefaultIfEmpty(2).Max();

        foreach (var item in list.Items)
        {
            string prefix = list.Ordered && item.Level == 0
                ? $"{item.Number}.".PadRight(numberWidth) + " "
                : bullet;
            yield return (prefix, item.Level * 2, item);
        }
    }

    /// <summary>
    /// Hanging indent: <paramref name="prefix"/> before the first line, as many spaces before every other one.
    ///  The prefix is plain text — in the rich form it needs no escaping (bullets, digits, dots, spaces).
    /// </summary>
    private static IEnumerable<string> Hang(IEnumerable<string> lines, string prefix)
    {
        string hang = new(' ', prefix.Length);
        bool first = true;
        foreach (var line in lines)
        {
            yield return (first ? prefix : hang) + line;
            first = false;
        }
    }

    private static string PlainText(IReadOnlyList<MdSpan> line) => string.Concat(line.Select(s => s.Text));

    /// <summary>
    /// Greedy word wrap of inline spans at <paramref name="width"/> visible characters. Returns at least one
    ///  (possibly empty) line; each line is a list of spans, with the spaces between words included.
    /// </summary>
    /// <remarks>
    /// A "word" may cross span borders — <c>`--drive`,</c> is ONE word of a code span and a text span, and never
    ///  breaks before its comma. Code spans break at their spaces like any text: a long command in a sentence
    ///  must wrap, too. A word longer than the line stands alone on an overlong line.
    /// </remarks>
    internal static IReadOnlyList<IReadOnlyList<MdSpan>> Wrap(IReadOnlyList<MdSpan> spans, int width)
    {
        width = Math.Max(width, MinWrapWidth);

        // ── Words: runs of non-space characters, as span fragments ──
        var words = new List<List<MdSpan>>();
        List<MdSpan>? word = null;
        foreach (var span in spans)
        {
            string[] parts = span.Text.Split(' ');
            for (int p = 0; p < parts.Length; p++)
            {
                if (p > 0)
                    word = null;            // a space ends the current word
                if (parts[p].Length == 0)
                    continue;

                if (word is null)
                {
                    word = [];
                    words.Add(word);
                }

                word.Add(new MdSpan(span.Kind, parts[p]));
            }
        }

        // ── Lines: as many words as fit ──
        var lines = new List<IReadOnlyList<MdSpan>>();
        var line = new List<MdSpan>();
        int length = 0;
        foreach (var w in words)
        {
            int wordLength = w.Sum(f => f.Text.Length);
            if (length > 0 && length + 1 + wordLength > width)
            {
                lines.Add(Merge(line));
                line = [];
                length = 0;
            }

            if (length > 0)
            {
                // The space takes the kind of both its neighbours when they agree, so a bold phrase or a
                //  code span stays one styled run instead of one per word
                var kind = line[^1].Kind == w[0].Kind ? w[0].Kind : MdSpanKind.Text;
                line.Add(new MdSpan(kind, " "));
                length++;
            }

            line.AddRange(w);
            length += wordLength;
        }

        lines.Add(Merge(line));
        return lines;
    }

    /// <summary>Joins adjacent fragments of the same kind.</summary>
    private static List<MdSpan> Merge(List<MdSpan> fragments)
    {
        var merged = new List<MdSpan>();
        foreach (var f in fragments)
        {
            if (merged.Count > 0 && merged[^1].Kind == f.Kind)
                merged[^1] = merged[^1] with { Text = merged[^1].Text + f.Text };
            else
                merged.Add(f);
        }

        return merged;
    }

    #endregion
}
