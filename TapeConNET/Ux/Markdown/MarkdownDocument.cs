using System.Text;
using System.Text.RegularExpressions;

namespace TapeConNET.Ux.Markdown;

// ─── Block model ─────────────────────────────────────────────────────────────

/// <summary>One block of a parsed Markdown document (Design-CliPresentation §3.3).</summary>
public abstract record MdBlock;

/// <summary><c>#</c> / <c>##</c> / <c>###</c> heading; deeper levels fold into 3.</summary>
public sealed record MdHeading(int Level, IReadOnlyList<MdSpan> Text) : MdBlock;

/// <summary>A paragraph; its source lines joined with single spaces.</summary>
public sealed record MdParagraph(IReadOnlyList<MdSpan> Text) : MdBlock;

/// <summary>A bullet or numbered list. Nested items (<see cref="MdListItem.Level"/> 1) always render as bullets.</summary>
public sealed record MdList(bool Ordered, IReadOnlyList<MdListItem> Items) : MdBlock;

/// <summary>A list item; <paramref name="Number"/> is the source number of an ordered item, else 0.</summary>
public sealed record MdListItem(IReadOnlyList<MdSpan> Text, int Level, int Number = 0);

/// <summary>A pipe table. Every row holds exactly as many cells as the header.</summary>
public sealed record MdTable(
    IReadOnlyList<IReadOnlyList<MdSpan>> Header,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<MdSpan>>> Rows,
    IReadOnlyList<MdAlign> Align) : MdBlock;

/// <summary>An indented or fenced code block; lines separated by <c>\n</c>, taken literally.</summary>
public sealed record MdCode(string Text) : MdBlock;

/// <summary>A <c>&gt;</c> quote; its lines joined like a paragraph.</summary>
public sealed record MdQuote(IReadOnlyList<MdSpan> Text) : MdBlock;

/// <summary>Column alignment of an <see cref="MdTable"/>, from the separator row (<c>:--</c>, <c>:-:</c>, <c>--:</c>).</summary>
public enum MdAlign { Left, Center, Right }

// ─── Inline model ────────────────────────────────────────────────────────────

/// <summary>Kind of an inline span. Spans never nest: bold or italic text holds no code (§3.2).</summary>
public enum MdSpanKind { Text, Code, Bold, Italic }

/// <summary>A run of inline text of one <see cref="MdSpanKind"/>, its Markdown markers already removed.</summary>
public sealed record MdSpan(MdSpanKind Kind, string Text);

// ─── Parser ──────────────────────────────────────────────────────────────────

/// <summary>
/// A line-based parser for the Markdown subset of Design-CliPresentation §3.2 — the subset the embedded
///  <c>tapecon docs</c> topics keep to. Pure: no console, no I/O.
/// </summary>
/// <remarks>
/// <para>
/// NEVER an error: whatever the subset does not cover passes through as paragraph text. The doc-subset test
///  (<c>MarkdownTests</c>) is what keeps the topics inside the subset — not this parser.
/// </para>
/// <para>
/// Deliberate simplifications against CommonMark: no hard line breaks (lines always join with a space), no links or
///  images, no <c>_</c> emphasis (file names carry underscores), no nesting of inline spans, and one list nesting level.
/// </para>
/// </remarks>
public static partial class MarkdownDocument
{
    /// <summary>Parses <paramref name="markdown"/> into blocks. CRLF, CR and LF line breaks alike.</summary>
    public static IReadOnlyList<MdBlock> Parse(string markdown)
    {
        string[] lines = SplitLines(markdown);
        var blocks = new List<MdBlock>();

        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i];

            if (IsBlank(line))
            {
                i++;
                continue;
            }

            // Order matters: the first match decides. An indented line is code only OUTSIDE a list —
            //  ParseList consumes its own indented continuation lines before we get back here.
            if (IsFence(line, out string fence))
                i = ParseFencedCode(lines, i, fence, blocks);
            else if (Indent(line) >= 4)
                i = ParseIndentedCode(lines, i, blocks);
            else if (TryHeading(line, out int level, out string title))
            {
                blocks.Add(new MdHeading(level, ParseInline(title)));
                i++;
            }
            else if (IsTableStart(lines, i))
                i = ParseTable(lines, i, blocks);
            else if (TryListMarker(line, out _))
                i = ParseList(lines, i, blocks);
            else if (IsQuote(line))
                i = ParseQuote(lines, i, blocks);
            else
                i = ParseParagraph(lines, i, blocks);
        }

        return blocks;
    }

    #region *** Lines ***

    private static string[] SplitLines(string text)
    {
        // Tabs in the indentation count as 4 spaces — only there: a tab inside text stays a tab
        return text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(ExpandLeadingTabs)
            .ToArray();
    }

    private static string ExpandLeadingTabs(string line)
    {
        int n = 0;
        while (n < line.Length && line[n] is ' ' or '\t')
            n++;

        return line[..n].Contains('\t')
            ? line[..n].Replace("\t", "    ") + line[n..]
            : line;
    }

    private static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);

    private static int Indent(string line)
    {
        int n = 0;
        while (n < line.Length && line[n] == ' ')
            n++;
        return n;
    }

    /// <summary>
    /// True when <paramref name="lines"/>[<paramref name="i"/>] starts a block that interrupts a paragraph, a list item's
    ///  lazy continuation, or a quote. Indented code does NOT interrupt (as in CommonMark).
    /// </summary>
    private static bool StartsBlock(string[] lines, int i)
    {
        string line = lines[i];
        if (Indent(line) >= 4)
            return false;

        // An ordered item interrupts a paragraph only when it counts from 1 — so a wrapped line that happens to
        //  begin with "2. " stays text (CommonMark's rule, too)
        if (TryListMarker(line, out var marker))
            return !marker.Ordered || marker.Number == 1;

        return IsFence(line, out _) || TryHeading(line, out _, out _) || IsTableStart(lines, i) || IsQuote(line);
    }

    #endregion

    #region *** Code ***

    private static bool IsFence(string line, out string fence)
    {
        fence = string.Empty;
        if (Indent(line) >= 4)
            return false;

        string t = line.TrimStart();
        foreach (char c in "`~")
        {
            int run = 0;
            while (run < t.Length && t[run] == c)
                run++;

            if (run >= 3)
            {
                fence = new string(c, run);
                return true;
            }
        }

        return false;
    }

    private static int ParseFencedCode(string[] lines, int i, string fence, List<MdBlock> blocks)
    {
        int indent = Indent(lines[i]);
        var code = new List<string>();

        // An unclosed fence runs to the end of the document (CommonMark, too)
        int k = i + 1;
        for (; k < lines.Length; k++)
        {
            if (Indent(lines[k]) < 4 && lines[k].TrimStart().StartsWith(fence, StringComparison.Ordinal))
            {
                k++;
                break;
            }

            // Remove up to the opening fence's indentation, never more
            code.Add(lines[k][Math.Min(indent, Indent(lines[k]))..]);
        }

        blocks.Add(new MdCode(string.Join('\n', code)));
        return k;
    }

    private static int ParseIndentedCode(string[] lines, int i, List<MdBlock> blocks)
    {
        var code = new List<string>();

        int k = i;
        while (k < lines.Length && (IsBlank(lines[k]) || Indent(lines[k]) >= 4))
        {
            code.Add(IsBlank(lines[k]) ? string.Empty : lines[k][4..]);
            k++;
        }

        // Blank lines inside the block belong to it; those at its end do not
        while (code.Count > 0 && code[^1].Length == 0)
            code.RemoveAt(code.Count - 1);

        blocks.Add(new MdCode(string.Join('\n', code)));
        return k;
    }

    #endregion

    #region *** Headings, quotes, paragraphs ***

    [GeneratedRegex(@"^ {0,3}(#{1,6})[ \t]+(.+?)(?:[ \t]+#+)?[ \t]*$")]
    private static partial Regex HeadingRegex();

    private static bool TryHeading(string line, out int level, out string text)
    {
        var m = HeadingRegex().Match(line);
        level = m.Success ? Math.Min(m.Groups[1].Length, 3) : 0;
        text = m.Success ? m.Groups[2].Value : string.Empty;
        return m.Success;
    }

    private static bool IsQuote(string line) => Indent(line) < 4 && line.TrimStart().StartsWith('>');

    private static int ParseQuote(string[] lines, int i, List<MdBlock> blocks)
    {
        var parts = new List<string>();

        int k = i;
        while (k < lines.Length && !IsBlank(lines[k]))
        {
            string t = lines[k].TrimStart();
            if (t.StartsWith('>'))
                t = t[1..].StartsWith(' ') ? t[2..] : t[1..];
            else if (StartsBlock(lines, k))
                break;          // not a lazy continuation, but the next block

            parts.Add(t.Trim());
            k++;
        }

        blocks.Add(new MdQuote(ParseInline(string.Join(' ', parts))));
        return k;
    }

    private static int ParseParagraph(string[] lines, int i, List<MdBlock> blocks)
    {
        // The first line is taken unconditionally: Parse sends us here because it starts no block
        var parts = new List<string> { lines[i].Trim() };

        int k = i + 1;
        while (k < lines.Length && !IsBlank(lines[k]) && !StartsBlock(lines, k))
        {
            parts.Add(lines[k].Trim());
            k++;
        }

        blocks.Add(new MdParagraph(ParseInline(string.Join(' ', parts))));
        return k;
    }

    #endregion

    #region *** Lists ***

    private readonly record struct ListMarker(int Indent, bool Ordered, int Number, string Content);

    [GeneratedRegex(@"^( *)([-*+]|\d{1,9}[.)])[ \t]+(.*)$")]
    private static partial Regex ListMarkerRegex();

    private static bool TryListMarker(string line, out ListMarker marker)
    {
        var m = ListMarkerRegex().Match(line);
        if (!m.Success)
        {
            marker = default;
            return false;
        }

        string bullet = m.Groups[2].Value;
        bool ordered = char.IsAsciiDigit(bullet[0]);
        marker = new ListMarker(
            m.Groups[1].Length,
            ordered,
            ordered ? int.Parse(bullet[..^1]) : 0,
            m.Groups[3].Value.Trim());
        return true;
    }

    private static int ParseList(string[] lines, int i, List<MdBlock> blocks)
    {
        TryListMarker(lines[i], out var first);
        int baseIndent = first.Indent;

        // Item texts grow with their continuation lines, so collect them in builders first
        var items = new List<(StringBuilder Text, int Level, int Number)>();

        int k = i;
        while (k < lines.Length)
        {
            string line = lines[k];

            if (IsBlank(line))
            {
                // A blank line ends the list — unless the list goes on right after it (a "loose" list)
                int next = k + 1;
                while (next < lines.Length && IsBlank(lines[next]))
                    next++;

                bool goesOn = next < lines.Length
                              && TryListMarker(lines[next], out var after)
                              && (after.Indent >= baseIndent + 2 || after.Ordered == first.Ordered);
                if (!goesOn)
                    break;

                k = next;
                continue;
            }

            if (TryListMarker(line, out var marker) && marker.Indent < baseIndent + 4)
            {
                // Two or more spaces deeper than the list's own items: nested. Deeper levels fold into 1.
                int level = marker.Indent >= baseIndent + 2 ? 1 : 0;

                // A top-level marker of the other kind starts a new list
                if (level == 0 && marker.Ordered != first.Ordered)
                    break;

                items.Add((new StringBuilder(marker.Content), level, marker.Number));
                k++;
                continue;
            }

            // Continuation: indented, or a "lazy" unindented line that starts no block of its own
            if (Indent(line) > 0 || !StartsBlock(lines, k))
            {
                items[^1].Text.Append(' ').Append(line.Trim());
                k++;
                continue;
            }

            break;
        }

        blocks.Add(new MdList(
            first.Ordered,
            items.Select(it => new MdListItem(ParseInline(it.Text.ToString()), it.Level, it.Number)).ToList()));
        return k;
    }

    #endregion

    #region *** Tables ***

    [GeneratedRegex(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$")]
    private static partial Regex TableSeparatorRegex();

    /// <summary>A header row (holding a <c>|</c>) directly followed by a separator row (<c>|---|:-:|</c>).</summary>
    private static bool IsTableStart(string[] lines, int i)
        => i + 1 < lines.Length
           && Indent(lines[i]) < 4
           && lines[i].Contains('|')
           && lines[i + 1].Contains('-')
           && TableSeparatorRegex().IsMatch(lines[i + 1]);

    private static int ParseTable(string[] lines, int i, List<MdBlock> blocks)
    {
        var header = SplitRow(lines[i]);
        int columns = header.Count;

        var align = SplitRow(lines[i + 1])
            .Select(cell => cell switch
            {
                _ when cell.StartsWith(':') && cell.EndsWith(':') => MdAlign.Center,
                _ when cell.EndsWith(':') => MdAlign.Right,
                _ => MdAlign.Left,
            })
            .Concat(Enumerable.Repeat(MdAlign.Left, columns))
            .Take(columns)
            .ToList();

        var rows = new List<IReadOnlyList<IReadOnlyList<MdSpan>>>();

        int k = i + 2;
        while (k < lines.Length && !IsBlank(lines[k]) && lines[k].Contains('|'))
        {
            // Pad short rows, cut long ones: renderers rely on exactly `columns` cells per row
            var cells = SplitRow(lines[k])
                .Concat(Enumerable.Repeat(string.Empty, columns))
                .Take(columns)
                .Select(ParseInline)
                .ToList();
            rows.Add(cells);
            k++;
        }

        blocks.Add(new MdTable(header.Select(ParseInline).ToList(), rows, align));
        return k;
    }

    /// <summary>
    /// Splits a table row at its unescaped pipes, without the outer ones. <c>\|</c> becomes a literal <c>|</c> —
    ///  inside code spans, too, as in GitHub Markdown: the pipe is escaped for the TABLE, before any inline parsing.
    /// </summary>
    private static List<string> SplitRow(string line)
    {
        string t = line.Trim();
        if (t.StartsWith('|'))
            t = t[1..];
        if (t.EndsWith('|') && !t.EndsWith("\\|", StringComparison.Ordinal))
            t = t[..^1];

        var cells = new List<string>();
        var cell = new StringBuilder();
        for (int p = 0; p < t.Length; p++)
        {
            if (t[p] == '\\' && p + 1 < t.Length && t[p + 1] == '|')
            {
                cell.Append('|');
                p++;
            }
            else if (t[p] == '|')
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else
                cell.Append(t[p]);
        }

        cells.Add(cell.ToString().Trim());
        return cells;
    }

    #endregion

    #region *** Inline ***

    /// <summary>
    /// Parses inline Markdown: <c>`code`</c>, <c>**bold**</c>, <c>*italic*</c>, backslash escapes. A marker without
    ///  its partner stays literal text — <c>*.docx</c> is a wildcard, not the start of italics.
    /// </summary>
    public static IReadOnlyList<MdSpan> ParseInline(string text)
    {
        var spans = new List<MdSpan>();
        var plain = new StringBuilder();

        void Flush()
        {
            if (plain.Length == 0)
                return;
            spans.Add(new MdSpan(MdSpanKind.Text, plain.ToString()));
            plain.Clear();
        }

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            // Backslash escape: the next ASCII punctuation character is literal
            if (c == '\\' && i + 1 < text.Length && IsAsciiPunctuation(text[i + 1]))
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }

            // Code span: closed by a backtick run of the SAME length; its content is literal — no escapes inside
            if (c == '`')
            {
                int run = RunLength(text, i, '`');
                int close = FindRun(text, i + run, '`', run);
                if (close < 0)
                {
                    plain.Append('`', run);
                    i += run;
                    continue;
                }

                string code = text[(i + run)..close];
                if (code.Length >= 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0)
                    code = code[1..^1];      // `` `x` `` — the padding spaces exist only to hold the backticks apart

                Flush();
                spans.Add(new MdSpan(MdSpanKind.Code, code));
                i = close + run;
                continue;
            }

            if (c == '*')
            {
                int run = RunLength(text, i, '*');
                int width = run >= 2 ? 2 : 1;

                if (TryEmphasis(text, i, width, out string inner, out int end))
                {
                    Flush();
                    spans.Add(new MdSpan(width == 2 ? MdSpanKind.Bold : MdSpanKind.Italic, inner));
                    i = end;
                    continue;
                }

                plain.Append('*', run);
                i += run;
                continue;
            }

            plain.Append(c);
            i++;
        }

        Flush();
        return spans;
    }

    private static bool IsAsciiPunctuation(char c)
        => c is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~';

    private static int RunLength(string text, int start, char c)
    {
        int n = 0;
        while (start + n < text.Length && text[start + n] == c)
            n++;
        return n;
    }

    /// <summary>Index of the next run of exactly <paramref name="length"/> × <paramref name="c"/>, or -1.</summary>
    private static int FindRun(string text, int from, char c, int length)
    {
        for (int p = from; p < text.Length; p++)
        {
            if (text[p] != c)
                continue;

            int run = RunLength(text, p, c);
            if (run == length)
                return p;
            p += run - 1;
        }

        return -1;
    }

    /// <summary>
    /// Emphasis opened by <paramref name="width"/> stars at <paramref name="start"/>: the opener must precede a
    ///  non-space, the closer (the same number of stars) must follow one. The inner text keeps its words only —
    ///  any nested markers are parsed and dropped, since spans do not nest.
    /// </summary>
    private static bool TryEmphasis(string text, int start, int width, out string inner, out int end)
    {
        inner = string.Empty;
        end = start;

        int open = start + width;
        if (open >= text.Length || char.IsWhiteSpace(text[open]))
            return false;

        for (int p = open + 1; p < text.Length; p++)
        {
            if (text[p] != '*')
                continue;

            int run = RunLength(text, p, '*');
            bool fits = width == 2 ? run >= 2 : run == 1;
            if (fits && !char.IsWhiteSpace(text[p - 1]))
            {
                inner = string.Concat(ParseInline(text[open..p]).Select(s => s.Text));
                end = p + width;
                return true;
            }

            p += run - 1;
        }

        return false;
    }

    #endregion
}
