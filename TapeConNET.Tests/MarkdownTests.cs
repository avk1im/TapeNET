using Spectre.Console;
using TapeConNET.Cli;
using TapeConNET.Ux.Markdown;

namespace TapeConNET.Tests;

/// <summary>
/// Design-CliPresentation Step 2: the Markdown subset parser (<see cref="MarkdownDocument"/>), both renderers
///  (<see cref="MarkdownRenderer"/>), and the contract that every embedded docs topic stays inside the subset.
/// </summary>
public class MarkdownTests
{
    #region *** Helpers ***

    /// <summary>Plain text of inline spans, markers removed.</summary>
    private static string Plain(IReadOnlyList<MdSpan> spans) => string.Concat(spans.Select(s => s.Text));

    private static T ParseSingle<T>(string markdown) where T : MdBlock
        => Assert.IsType<T>(Assert.Single(MarkdownDocument.Parse(markdown)));

    /// <summary>Renders the rich form into a colourless, ANSI-free Spectre console and returns the text.</summary>
    private static string RenderRich(IReadOnlyList<MdBlock> blocks, int width = 100)
    {
        var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(output),
        });
        console.Profile.Width = width;

        console.Write(new Rows(MarkdownRenderer.ToRenderables(blocks, width)));
        return output.ToString();
    }

    #endregion

    #region *** Blocks ***

    [Theory]
    [InlineData("# One", 1, "One")]
    [InlineData("## Two ##", 2, "Two")]
    [InlineData("### Three", 3, "Three")]
    [InlineData("#### Four", 3, "Four")]          // deeper levels fold into 3
    public void Heading_Levels(string markdown, int level, string text)
    {
        var heading = ParseSingle<MdHeading>(markdown);
        Assert.Equal(level, heading.Level);
        Assert.Equal(text, Plain(heading.Text));
    }

    [Fact]
    public void Paragraph_JoinsLines_AcrossLineBreakStyles()
    {
        var paragraph = ParseSingle<MdParagraph>("first line\r\nsecond line\nthird");
        Assert.Equal("first line second line third", Plain(paragraph.Text));
    }

    [Fact]
    public void BulletList_Continuation_And_Nesting()
    {
        var list = ParseSingle<MdList>("- one\n- two\n  continued\n  - nested\n* star");

        Assert.False(list.Ordered);
        Assert.Equal(["one", "two continued", "nested", "star"], list.Items.Select(i => Plain(i.Text)));
        Assert.Equal([0, 0, 1, 0], list.Items.Select(i => i.Level));
    }

    [Fact]
    public void OrderedList_Numbers_And_Continuation()
    {
        var list = ParseSingle<MdList>("1. first\n   more\n2. second");

        Assert.True(list.Ordered);
        Assert.Equal(["first more", "second"], list.Items.Select(i => Plain(i.Text)));
        Assert.Equal([1, 2], list.Items.Select(i => i.Number));
    }

    [Fact]
    public void Lists_OfDifferentKinds_AreSeparateBlocks()
    {
        var blocks = MarkdownDocument.Parse("- bullet\n\n1. number");
        Assert.Equal(2, blocks.Count);
        Assert.False(((MdList)blocks[0]).Ordered);
        Assert.True(((MdList)blocks[1]).Ordered);
    }

    [Fact]
    public void Paragraph_LineStartingWithOtherNumber_IsNotAList()
    {
        // Only "1." interrupts a paragraph — a wrapped line beginning with "2. " is text
        var paragraph = ParseSingle<MdParagraph>("tapecon\n2. not a list");
        Assert.Equal("tapecon 2. not a list", Plain(paragraph.Text));
    }

    [Fact]
    public void Table_Cells_Alignment_EscapedPipe()
    {
        var table = ParseSingle<MdTable>("| A | B | C |\n|:--|:-:|--:|\n| `x\\|y` | **b** |\n");

        Assert.Equal(["A", "B", "C"], table.Header.Select(Plain));
        Assert.Equal([MdAlign.Left, MdAlign.Center, MdAlign.Right], table.Align);

        // The short row is padded to the header's width; the escaped pipe is literal, even in code
        var row = Assert.Single(table.Rows);
        Assert.Equal(3, row.Count);
        Assert.Equal(new MdSpan(MdSpanKind.Code, "x|y"), Assert.Single(row[0]));
        Assert.Equal(new MdSpan(MdSpanKind.Bold, "b"), Assert.Single(row[1]));
        Assert.Empty(row[2]);
    }

    [Fact]
    public void IndentedCode_KeepsInnerBlankLines_DropsTrailingOnes()
    {
        var code = ParseSingle<MdCode>("    one\n      two\n\n    three\n\n");
        Assert.Equal("one\n  two\n\nthree", code.Text);
    }

    [Fact]
    public void FencedCode_IsLiteral()
    {
        var code = ParseSingle<MdCode>("```\n# not a heading\n**not bold**\n```");
        Assert.Equal("# not a heading\n**not bold**", code.Text);
    }

    [Fact]
    public void Quote_JoinsLines()
    {
        var quote = ParseSingle<MdQuote>("> first\n> second");
        Assert.Equal("first second", Plain(quote.Text));
    }

    [Theory]
    [InlineData("#nospace")]
    [InlineData("| a | b | without separator")]
    [InlineData("-not a bullet")]
    [InlineData("some <html> & [link](target)")]
    public void Unsupported_PassesThrough_AsParagraph(string markdown)
    {
        var paragraph = ParseSingle<MdParagraph>(markdown);
        Assert.Equal(markdown, Plain(paragraph.Text));
    }

    #endregion

    #region *** Inline ***

    [Fact]
    public void Inline_AllKinds()
    {
        var spans = MarkdownDocument.ParseInline("a `b|c` **d e** *f* \\*g\\* *.docx 2 * 3");

        Assert.Equal(
        [
            new MdSpan(MdSpanKind.Text, "a "),
            new MdSpan(MdSpanKind.Code, "b|c"),
            new MdSpan(MdSpanKind.Text, " "),
            new MdSpan(MdSpanKind.Bold, "d e"),
            new MdSpan(MdSpanKind.Text, " "),
            new MdSpan(MdSpanKind.Italic, "f"),
            new MdSpan(MdSpanKind.Text, " *g* *.docx 2 * 3"),
        ], spans);
    }

    [Theory]
    [InlineData("a `b", "a `b")]                 // unclosed code
    [InlineData("**open", "**open")]             // unclosed bold
    [InlineData("``a`b``", "a`b")]               // longer fence holds a backtick
    [InlineData("`\\\\.\\Tape0`", "\\\\.\\Tape0")] // no escapes inside code
    public void Inline_EdgeCases(string markdown, string plain)
        => Assert.Equal(plain, Plain(MarkdownDocument.ParseInline(markdown)));

    #endregion

    #region *** Plain renderer ***

    private const string Fixture =
        "# Title\n\n" +
        "Some *text* with `code` and **bold**.\n\n" +
        "- one\n- two\n  continued\n  - nested\n\n" +
        "1. first\n2. second\n\n" +
        "| A | B |\n|---|--:|\n| x | 1 |\n| yy | 22 |\n\n" +
        "    code line\n\n" +
        "> quoted\n";

    [Fact]
    public void Plain_Fixture_LineByLine()
    {
        var lines = MarkdownRenderer.ToPlainLines(MarkdownDocument.Parse(Fixture), 80);

        Assert.Equal(
        [
            "Title",
            "═════",
            "",
            "Some text with code and bold.",
            "",
            "- one",
            "- two continued",
            "  - nested",
            "",
            "1. first",
            "2. second",
            "",
            "A    B",
            "──  ──",
            "x    1",
            "yy  22",
            "",
            "    code line",
            "",
            "  quoted",
        ], lines);
    }

    [Fact]
    public void Plain_Wraps_WithHangingIndent()
    {
        var blocks = MarkdownDocument.Parse("aaaa bbbb cccc dddd eeee\n\n- aaaa bbbb cccc dddd eeee");

        // List text width = 22 - 2 (bullet) = 20 — the same break as the paragraph at 22
        var lines = MarkdownRenderer.ToPlainLines(blocks, 22);

        Assert.Equal(
        [
            "aaaa bbbb cccc dddd",
            "eeee",
            "",
            "- aaaa bbbb cccc dddd",
            "  eeee",
        ], lines);
    }

    [Fact]
    public void Plain_WideTable_ShrinksToWidth()
    {
        string longText = string.Join(' ', Enumerable.Repeat("word", 30));
        var blocks = MarkdownDocument.Parse($"| Code | Meaning |\n|---|---|\n| 0 | {longText} |");

        var lines = MarkdownRenderer.ToPlainLines(blocks, 60);

        Assert.All(lines, l => Assert.True(l.Length <= 60, $"{l.Length}: {l}"));
        Assert.True(lines.Count > 4, "the long cell wraps onto several lines");
        Assert.StartsWith("0     word", lines[2]);
    }

    #endregion

    #region *** Rich renderer ***

    [Fact]
    public void Rich_Fixture_Renders_TextWithoutMarkers()
    {
        string text = RenderRich(MarkdownDocument.Parse(Fixture));

        Assert.Contains("Title", text);
        Assert.Contains("Some text with code and bold.", text);
        Assert.Contains("• two continued", text);
        Assert.Contains("code line", text);
        Assert.DoesNotContain("**", text);
        Assert.DoesNotContain("`", text);
    }

    [Fact]
    public void Rich_SquareBrackets_AreEscaped_NotMarkup()
    {
        // Unescaped, "[Locked]" is a Spectre markup error — the render would throw
        string text = RenderRich(MarkdownDocument.Parse("## Listed as [Locked]\n\nA set `[Locked]` stays **[x]**."));

        Assert.Contains("[Locked]", text);
        Assert.Contains("[x]", text);
    }

    #endregion

    #region *** Embedded topics stay inside the subset (§3.5) ***

    public static TheoryData<string> Topics
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var topic in DocsCommand.TopicNames)
                data.Add(topic);
            return data;
        }
    }

    /// <summary>
    /// Every embedded topic, rendered plain, shows no Markdown syntax — a doc edit that leaves the subset fails here.
    ///  Code blocks are exempt: their content is literal, and may hold <c>**</c> globs or backticks on purpose.
    /// </summary>
    [Theory]
    [MemberData(nameof(Topics))]
    public void EmbeddedTopic_PlainOutput_ShowsNoMarkdownSyntax(string topic)
    {
        string? markdown = DocsCommand.LoadTopic(topic);
        Assert.NotNull(markdown);

        var prose = MarkdownDocument.Parse(markdown).Where(b => b is not MdCode).ToList();
        var lines = MarkdownRenderer.ToPlainLines(prose, 80);

        Assert.All(lines, line =>
        {
            Assert.DoesNotContain("|---", line);
            Assert.DoesNotContain("**", line);
            Assert.DoesNotContain("`", line);
            Assert.False(line.StartsWith('#'), $"heading syntax survived: {line}");
            Assert.False(line.StartsWith('|'), $"table syntax survived: {line}");
        });
    }

    [Theory]
    [MemberData(nameof(Topics))]
    public void EmbeddedTopic_RichRendering_DoesNotThrow(string topic)
    {
        string? markdown = DocsCommand.LoadTopic(topic);
        Assert.NotNull(markdown);

        Assert.NotEmpty(RenderRich(MarkdownDocument.Parse(markdown)));
    }

    #endregion
}
