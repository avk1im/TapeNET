using HelpNET.Content;
using Xunit;

// Access internal HelpSlug via InternalsVisibleTo in HelpNET.csproj.

namespace HelpNET.Tests;

/// <summary>
/// Tests for <see cref="HelpSlug.From"/> slug-generation rules.
/// </summary>
public class HelpSlugTests
{
    [Theory]
    [InlineData("Backup sets list",     "backup-sets-list")]
    [InlineData("Restore to",           "restore-to")]
    [InlineData("Include incremental chain", "include-incremental-chain")]
    [InlineData("Start button",         "start-button")]
    [InlineData("FCL (File Conditions Language)", "fcl-file-conditions-language")]
    [InlineData("TOC (Table of Contents)", "toc-table-of-contents")]
    [InlineData("multi-volume",         "multi-volume")]
    [InlineData("  leading spaces ",    "leading-spaces")]
    [InlineData("A/B option",           "a-b-option")]
    [InlineData("UPPERCASE TERM",       "uppercase-term")]
    public void From_DisplayName_ProducesExpectedSlug(string input, string expected)
        => Assert.Equal(expected, HelpSlug.From(input));

    [Fact]
    public void From_EmptyString_ReturnsEmpty()
        => Assert.Equal(string.Empty, HelpSlug.From(string.Empty));

    [Fact]
    public void From_OnlySpaces_ReturnsEmpty()
        => Assert.Equal(string.Empty, HelpSlug.From("   "));

    [Fact]
    public void From_SlashSeparator_ProducesHyphen()
        => Assert.Equal("setmark-filemark", HelpSlug.From("Setmark / Filemark"));
}
