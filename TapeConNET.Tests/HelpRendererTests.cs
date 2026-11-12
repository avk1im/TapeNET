using System.CommandLine;
using Spectre.Console;
using TapeConNET.Cli;
using TapeConNET.Cli.Help;
using TapeConNET.Ux;

namespace TapeConNET.Tests;

/// <summary>
/// Design-CliPresentation Step 4: the coloured help. The rich form must be the plain form plus styles — same lines,
///  same breaks — and every run must carry the theme style §5.2 gives it.
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

        Assert.Equal(ConsoleTheme.Heading, Assert.Single(layout[0]).Style);                     // title
        Assert.Equal(ConsoleTheme.Emphasis, RunStarting(layout, "Usage:").Style);
        Assert.Equal(ConsoleTheme.Heading, RunStarting(layout, "Encryption").Style);           // section title
        Assert.Equal(ConsoleTheme.Dimmed, RunStarting(layout, "Without these").Style);          // intro

        Assert.Equal(ConsoleTheme.OptionName, RunStarting(layout, "--hash, -H").Style);
        Assert.Equal(ConsoleTheme.Placeholder, RunStarting(layout, "<None|").Style);
        Assert.Equal(ConsoleTheme.Dimmed, RunStarting(layout, "[default:").Style);
        Assert.Equal(ConsoleTheme.OptionName, RunStarting(layout, "<files>").Style);             // argument

        // Summary: names themed, pointer dimmed
        Assert.Equal(ConsoleTheme.OptionName, RunStarting(layout, "--capacity").Style);
        Assert.Equal(ConsoleTheme.Dimmed, RunStarting(layout, "Details:").Style);
    }

    [Fact]
    public void Layout_Root_CommandsThemed_FooterDimmed()
    {
        var layout = HelpRenderer.Layout(ModelOf(""), 80);

        Assert.Equal(ConsoleTheme.OptionName, RunStarting(layout, "backup").Style);
        Assert.Equal(ConsoleTheme.Dimmed, RunStarting(layout, "Docs:").Style);
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
}
