using Spectre.Console;
using TapeConNET.Ux;

namespace TapeConNET.Tests;

/// <summary>
/// Design-CliPresentation Step 5: user colours (<c>TAPECON_COLORS</c>) and the <c>NO_COLOR</c> convention. The parser is
///  pure and tested directly; the static constructor that applies it reads the real environment once and is not.
/// </summary>
public class ConsoleThemeTests
{
    #region *** TAPECON_COLORS ***

    [Fact]
    public void ParseColors_NullOrBlank_NoOverrides_NoWarnings()
    {
        var warnings = new List<string>();

        Assert.Empty(ConsoleTheme.ParseColors(null, warnings));
        Assert.Empty(ConsoleTheme.ParseColors("  ", warnings));
        Assert.Empty(warnings);
    }

    [Fact]
    public void ParseColors_ValidEntries_ParsedAsSpectreStyles()
    {
        var warnings = new List<string>();

        var result = ConsoleTheme.ParseColors("heading=bold dodgerblue1; mention = darkcyan ;error=#ff0000;", warnings);

        Assert.Empty(warnings);
        Assert.Equal(3, result.Count);
        Assert.Equal(Style.Parse("bold dodgerblue1"), result["heading"]);
        Assert.Equal(Style.Parse("darkcyan"), result["mention"]);
        Assert.Equal(Style.Parse("#ff0000"), result["error"]);
    }

    [Fact]
    public void ParseColors_Keys_CaseInsensitive_LastOneWins()
    {
        var result = ConsoleTheme.ParseColors("Heading=red;HEADING=green", []);

        Assert.Equal(Style.Parse("green"), Assert.Single(result).Value);
        Assert.True(result.ContainsKey("heading"));
    }

    [Theory]
    [InlineData("heading")]                     // no '='
    [InlineData("=red")]                        // no key
    [InlineData("titles=red")]                  // unknown key
    [InlineData("heading=notacolour")]          // no style
    [InlineData("heading=")]                    // empty style
    public void ParseColors_BadEntry_Skipped_WithWarning(string spec)
    {
        var warnings = new List<string>();

        var result = ConsoleTheme.ParseColors(spec, warnings);

        Assert.Empty(result);
        Assert.Single(warnings);
        Assert.StartsWith(ConsoleTheme.ColorsVariable, warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ParseColors_BadEntry_DoesNotSpoilTheGoodOnes()
    {
        var warnings = new List<string>();

        var result = ConsoleTheme.ParseColors("heading=red;bogus=blue;mention=green", warnings);

        Assert.Equal(new[] { "heading", "mention" }, result.Keys.Order(StringComparer.Ordinal));
        Assert.Single(warnings);
    }

    [Fact]
    public void ColorKeys_EveryKey_AcceptsAStyle()
    {
        // Each documented key parses — the list and the parser cannot drift apart
        string spec = string.Join(';', ConsoleTheme.ColorKeys.Select(k => $"{k}=red"));
        var warnings = new List<string>();

        Assert.Equal(ConsoleTheme.ColorKeys.Count, ConsoleTheme.ParseColors(spec, warnings).Count);
        Assert.Empty(warnings);
    }

    #endregion

    #region *** NO_COLOR ***

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("1", true)]
    [InlineData("0", true)]                     // presence counts, not the value
    [InlineData("false", true)]
    public void NoColorRequested_ByPresence(string? value, bool expected)
        => Assert.Equal(expected, ConsoleCapabilities.NoColorRequested(value));

    #endregion
}
