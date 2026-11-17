using System.CommandLine;
using Spectre.Console;
using TapeConNET.Cli;
using TapeConNET.Cli.Help;
using TapeConNET.Ux;

namespace TapeConNET.Tests;

/// <summary>
/// Design-CliPresentation Step 4: the coloured help. The rich form must be the plain form plus styles — same lines,
///  same breaks — and every run must carry the theme style §5.2 gives it. Step 5: mentions (<c>`--option`</c>).
/// </summary>
public class HelpRendererTests
{
    #region *** Helpers ***

    private static RootCommand BuildRoot() => RootCommandFactory.Create(new SilentConsoleUx(new StringWriter()));

    private static IEnumerable<Command> AllCommands(Command cmd)
        => cmd.Subcommands.SelectMany(AllCommands).Prepend(cmd);

    public static TheoryData<string> CommandNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var cmd in AllCommands(BuildRoot()))
                data.Add(cmd is RootCommand ? "" : cmd.Name);
            return data;
        }
    }

    private static HelpModel ModelOf(string name)
    {
        var root = BuildRoot();
        var cmd = name.Length == 0 ? root : AllCommands(root).First(c => c is not RootCommand && c.Name == name);
        return HelpModel.Build(cmd);
    }

    /// <summary>Renders the rich form into a test console — wider than the layout, so Spectre never re-wraps.</summary>
    private static string RenderRich(HelpModel model, int width, bool colours)
    {
        var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = colours ? AnsiSupport.Yes : AnsiSupport.No,
            ColorSystem = colours ? ColorSystemSupport.TrueColor : ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(output),
        });
        console.Profile.Width = 200;

        console.Write(new Rows(HelpRenderer.ToRenderables(model, width)));
        return output.ToString();
    }

    /// <summary>Lines without trailing blanks; the final line break yields no extra line.</summary>
    private static string[] Lines(string text)
    {
        text = text.ReplaceLineEndings("\n");
        if (text.EndsWith('\n'))
            text = text[..^1];
        return [.. text.Split('\n').Select(l => l.TrimEnd())];
    }

    /// <summary>The run whose text starts with <paramref name="prefix"/>, anywhere in the layout.</summary>
    private static HelpRun RunStarting(List<List<HelpRun>> layout, string prefix)
        => layout.SelectMany(l => l).First(r => r.Text.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>Shorthands for the expected runs of <see cref="HelpRenderer.Words"/>.</summary>
    private static HelpRun P(string text, bool keep = false) => new(text, Style.Plain, keep);
    private static HelpRun M(string text, bool keep = false) => new(text, ConsoleTheme.Mention, keep);

    #endregion

    #region *** Rich = plain + styles ***

    [Theory]
    [MemberData(nameof(CommandNames))]
    public void Rich_WithoutColours_EqualsPlain(string name)
    {
        var model = ModelOf(name);

        var plain = HelpRenderer.ToPlainLines(model, 80).Select(l => l.TrimEnd()).ToArray();
        var rich = Lines(RenderRich(model, 80, colours: false));

        Assert.Equal(plain, rich);
    }

    [Fact]
    public void Rich_WithColours_EmitsAnsi_AndEscapesBrackets()
    {
        // Unescaped, "[default: Crc32]" is a markup tag — the render would throw
        string text = RenderRich(ModelOf("backup"), 80, colours: true);

        Assert.Contains("\u001b[", text, StringComparison.Ordinal);
        Assert.Contains("[default:", text, StringComparison.Ordinal);
    }

    #endregion

    #region *** Styles (§5.2) ***

    [Fact]
    public void Layout_StylesFollowTheTheme()
    {
        var model = ModelOf("backup");
        var layout = HelpRenderer.Layout(model, 80);
        Assert.Equal(ConsoleTheme.Heading, Assert.Single(layout[0]).Style);                    // title
        Assert.Equal(ConsoleTheme.Emphasis, RunStarting(layout, "Usage:").Style);
        Assert.Equal(ConsoleTheme.Heading, RunStarting(layout, "Encryption").Style);           // section title
        Assert.Equal(ConsoleTheme.Dimmed, RunStarting(layout, "Without these").Style);         // intro
        Assert.Equal(ConsoleTheme.OptionName, RunStarting(layout, "--hash, -H").Style);
        Assert.Equal(ConsoleTheme.Placeholder, RunStarting(layout, "<None|").Style);
        Assert.Equal(ConsoleTheme.Dimmed, RunStarting(layout, "[default:").Style);
        Assert.Equal(ConsoleTheme.OptionName, RunStarting(layout, "<files>").Style);           // argument
        // Summary: names themed, pointer dimmed. Searched from its title on — Drive selection's descriptions mention
        //  `--capacity` earlier, as a Mention
        var summary = layout.SkipWhile(l => !(l.Count == 1 && l[0].Text == "New virtual media")).ToList();
        Assert.NotEmpty(summary);
        Assert.Equal(ConsoleTheme.OptionName, RunStarting(summary, "--capacity").Style);
        Assert.Equal(ConsoleTheme.Dimmed, RunStarting(summary, "Details:").Style);
    }

    [Fact]
    public void Layout_Root_CommandsThemed_FooterDimmed()
    {
        var layout = HelpRenderer.Layout(ModelOf(""), 80);

        Assert.Equal(ConsoleTheme.OptionName, RunStarting(layout, "backup").Style);
        Assert.Equal(ConsoleTheme.Dimmed, RunStarting(layout, "Start:").Style);
    }

    [Fact]
    public void Layout_NeighbouringRuns_NeverShareAStyle()
    {
        // Merged: one run per styled phrase — compact markup
        var layout = HelpRenderer.Layout(ModelOf("backup"), 80);

        Assert.All(layout, line =>
        {
            for (int i = 1; i < line.Count; i++)
                Assert.NotEqual(line[i - 1].Style, line[i].Style);
            Assert.All(line, r => Assert.NotEmpty(r.Text));
        });
    }

    #endregion

    #region *** Mentions (§5.2) ***

    [Fact]
    public void Words_Mention_InParentheses_KeepsPunctuationAttached()
        => Assert.Equal(
            new[] { P("some"), P("text"), P("("), M("--drive", keep: true), P(")", keep: true), P("some"), P("text") },
            HelpRenderer.Words("some text (`--drive`) some text", Style.Plain));

    [Fact]
    public void Words_Mention_BeforeComma_KeepsCommaAttached()
        => Assert.Equal(
            new[] { P("some"), P("text"), M("--drive"), P(",", keep: true), P("then"), P("more") },
            HelpRenderer.Words("some text `--drive`, then more", Style.Plain));

    [Fact]
    public void Words_Mention_WithSpaces_BecomesSeveralMentionWords()
        => Assert.Equal(
            new[] { P("See"), M("tapecon"), M("docs"), M("faq"), P(".", keep: true) },
            HelpRenderer.Words("See `tapecon docs faq`.", Style.Plain));

    [Fact]
    public void Words_Mention_InStyledText_KeepsTheSurroundingStyle()
        => Assert.Equal(
            new[] { new HelpRun("Shape", ConsoleTheme.Dimmed), M("--virtual-create"), new HelpRun(".", ConsoleTheme.Dimmed, true) },
            HelpRenderer.Words("Shape `--virtual-create`.", ConsoleTheme.Dimmed));

    [Theory]
    [InlineData("a `b", "a `b")]                          // a single marker
    [InlineData("`a` and `b", "a and `b")]                // the last, unpaired one
    [InlineData("an empty `` pair", "an empty `` pair")]  // an empty pair is no mention
    [InlineData("``a`", "`a")]                            // ... but its second marker may open one
    [InlineData("no markers at all", "no markers at all")]
    public void PlainText_UnpairedMarkers_StayLiteral(string text, string plain)
        => Assert.Equal(plain, HelpRenderer.PlainText(text));

    [Fact]
    public void Words_UnpairedMarker_IsPlainText()
        => Assert.Equal(
            new[] { M("a"), P("and"), P("`b") },
            HelpRenderer.Words("`a` and `b", Style.Plain));

    [Fact]
    public void Wrap_NeverSeparatesAMention_FromItsPunctuation()
    {
        // "aaaa aaaa aaaa" fills 14 of 20 columns; "(--drive)," (10) moves to the next line as a whole
        var lines = HelpRenderer.Wrap(HelpRenderer.Words("aaaa aaaa aaaa (`--drive`), x", Style.Plain), 20);

        Assert.Equal(["aaaa aaaa aaaa", "(--drive), x"], lines.Select(l => string.Concat(l.Select(r => r.Text))));
    }

    [Theory]
    [MemberData(nameof(CommandNames))]
    public void Plain_ShowsNoMentionMarkers(string name)
    {
        // Every help text uses paired markers only — none may reach the plain output (§5.2)
        var lines = HelpRenderer.ToPlainLines(ModelOf(name), 80);
        Assert.All(lines, l => Assert.DoesNotContain("`", l));
    }

    [Fact]
    public void Layout_Mentions_InDescriptions_IntroAndFooter()
    {
        var backup = HelpRenderer.Layout(ModelOf("backup"), 80).SelectMany(l => l).ToList();

        // --encrypt's description names --password-file: a mention, distinct from the option's own row (OptionName)
        Assert.Contains(backup, r => r.Text == "--password-file" && r.Style.Equals(ConsoleTheme.Mention));
        Assert.Contains(backup, r => r.Text.StartsWith("--password-file", StringComparison.Ordinal)
                                     && r.Style.Equals(ConsoleTheme.OptionName));

        // The New virtual media intro and pointer
        Assert.Contains(backup, r => r.Text == "--virtual-create" && r.Style.Equals(ConsoleTheme.Mention));
        Assert.Contains(backup, r => r.Text == "tapecon create-virtual --help" && r.Style.Equals(ConsoleTheme.Mention));

        // The root's footer
        var root = HelpRenderer.Layout(ModelOf(""), 80).SelectMany(l => l);
        Assert.Contains(root, r => r.Text == "tapecon docs getting-started" && r.Style.Equals(ConsoleTheme.Mention));
    }

    [Fact]
    public void Rich_WithColours_Mention_RendersWithoutMarkers()
    {
        string text = RenderRich(ModelOf("backup"), 80, colours: true);

        Assert.DoesNotContain("`", text, StringComparison.Ordinal);
        Assert.Contains("--password-file", text, StringComparison.Ordinal);
    }

    #endregion
}
