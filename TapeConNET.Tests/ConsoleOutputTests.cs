using Spectre.Console;
using TapeConNET.Ux;

namespace TapeConNET.Tests;

/// <summary>
/// Design-CliPresentation Step 1: the document output path — <see cref="IConsoleUx.Write"/> on both consoles, the
///  plain/width decisions of <see cref="ConsoleCapabilities"/>, and the level icons of <see cref="ConsoleTheme"/>.
/// </summary>
public class ConsoleOutputTests
{
    #region *** Helpers ***

    /// <summary>The lines written to <paramref name="writer"/>, without the final line break's empty tail.</summary>
    private static string[] Lines(StringWriter writer)
    {
        string text = writer.ToString().ReplaceLineEndings("\n");
        if (text.EndsWith('\n'))
            text = text[..^1];

        return text.Length == 0 ? [] : text.Split('\n');
    }

    /// <summary>A document whose rich form must never render — proves the plain path was taken.</summary>
    private static ConsoleDocument PlainOnly(Func<int, IReadOnlyList<string>> plain)
        => new(Rich: _ => throw new InvalidOperationException("The rich form was rendered."), Plain: plain);

    /// <summary>A Spectre console over a <see cref="StringWriter"/> — not a terminal, hence "redirected".</summary>
    private static SpectreConsoleUx RedirectedSpectre(StringWriter writer)
        => new(AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        }));

    #endregion

    #region *** SilentConsoleUx.Write ***

    [Fact]
    public void Silent_Write_WritesPlainLines_NoTimestamp_NoIcon_NoMarkup()
    {
        var output = new StringWriter();
        var ux = new SilentConsoleUx(output);

        ux.Write(ConsoleDocument.FromLines("Title", "", "  body [not markup]"));

        Assert.Equal(new[] { "Title", "", "  body [not markup]" }, Lines(output));
    }

    [Fact]
    public void Silent_Write_IsNotSuppressed_ByQuietMode()
    {
        var output = new StringWriter();
        var ux = new SilentConsoleUx(output) { QuietMode = true };

        ux.Write(ConsoleDocument.FromLines("asked for explicitly"));

        Assert.Equal(new[] { "asked for explicitly" }, Lines(output));
    }

    [Fact]
    public void Silent_Write_AddsNoLogEntry()
    {
        var ux = new SilentConsoleUx(new StringWriter());

        ux.Write(ConsoleDocument.FromLines("a document"));

        Assert.Empty(ux.Entries);
    }

    [Theory]
    [InlineData(null, ConsoleCapabilities.RedirectedWidth)]
    [InlineData(100, 100)]
    public void Silent_Write_RendersPlainOnly_AtItsWidth(int? width, int expected)
    {
        var output = new StringWriter();
        var ux = width is { } w ? new SilentConsoleUx(output) { Width = w } : new SilentConsoleUx(output);

        ux.Write(PlainOnly(cols => [$"width={cols}"]));

        Assert.Equal(new[] { $"width={expected}" }, Lines(output));
    }

    [Fact]
    public void Silent_Write_And_Log_Interleave_InCallOrder()
    {
        var output = new StringWriter();
        var ux = new SilentConsoleUx(output);

        ux.Log(WarningLevel.Info, "before");
        ux.Write(ConsoleDocument.FromLines("document"));
        ux.Log(WarningLevel.Info, "after");

        string[] lines = Lines(output);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith("before", lines[0]);
        Assert.Equal("document", lines[1]);
        Assert.EndsWith("after", lines[2]);
    }

    #endregion

    #region *** SpectreConsoleUx.Write ***

    [Fact]
    public void Spectre_Write_Redirected_WritesPlainForm_AtRedirectedWidth()
    {
        var output = new StringWriter();
        var ux = RedirectedSpectre(output);

        ux.Write(PlainOnly(cols => ["first [x]", $"width={cols}"]));

        Assert.Equal(new[] { "first [x]", $"width={ConsoleCapabilities.RedirectedWidth}" }, Lines(output));
    }

    [Fact]
    public void Spectre_Write_Quiet_IsNotSuppressed()
    {
        var output = new StringWriter();
        var ux = RedirectedSpectre(output);
        ux.QuietMode = true;

        ux.Write(ConsoleDocument.FromLines("help text"));

        Assert.Equal(new[] { "help text" }, Lines(output));
    }

    #endregion

    #region *** ConsoleCapabilities ***

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true,  false, false, true)]
    [InlineData(false, true,  false, true)]
    [InlineData(false, false, true,  true)]
    public void UsePlain_AnyReason_MakesItPlain(bool noColor, bool quiet, bool redirected, bool expected)
        => Assert.Equal(expected, ConsoleCapabilities.UsePlain(noColor, quiet, redirected));

    [Theory]
    [InlineData(40,  false, ConsoleCapabilities.MinWidth)]
    [InlineData(100, false, 100)]
    [InlineData(200, false, ConsoleCapabilities.MaxWidth)]
    [InlineData(200, true,  ConsoleCapabilities.RedirectedWidth)]
    [InlineData(0,   false, ConsoleCapabilities.RedirectedWidth)]     // console reports no width
    public void ClampWidth_Rules(int consoleWidth, bool redirected, int expected)
        => Assert.Equal(expected, ConsoleCapabilities.ClampWidth(consoleWidth, redirected));

    #endregion

    #region *** ConsoleTheme ***

    [Fact]
    public void Icons_Error_HeavyStraightCross_Failed_SlantedCross()
    {
        Assert.Equal("✖ ", ConsoleTheme.IconFor(WarningLevel.Error));
        Assert.Equal("✗ ", ConsoleTheme.IconFor(WarningLevel.Failed));
    }

    #endregion
}
