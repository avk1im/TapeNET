using FclNET;

namespace TapeConNET.Tests;

/// <summary>
/// <see cref="FclSizeLiteral"/> as the CLI uses it: no group separators, binary units, KB and KiB alike.
/// </summary>
public class SizeLiteralTests
{
    [Theory]
    [InlineData("512", 512L)]
    [InlineData("512B", 512L)]
    [InlineData("64KB", 65_536L)]
    [InlineData("64KiB", 65_536L)]
    [InlineData("64kb", 65_536L)]
    [InlineData("2 MB", 2_097_152L)]
    [InlineData("2MiB", 2_097_152L)]
    [InlineData("1.5GB", 1_610_612_736L)]
    [InlineData("1GiB", 1_073_741_824L)]
    [InlineData("1TB", 1_099_511_627_776L)]
    [InlineData("1tib", 1_099_511_627_776L)]
    [InlineData("  16KB  ", 16_384L)]
    public void TryParse_Accepts(string text, long expected)
    {
        Assert.True(FclSizeLiteral.TryParse(text, allowGroupSeparators: false, out long bytes, out string? error), error);
        Assert.Equal(expected, bytes);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("MB")]
    [InlineData("10XB")]
    [InlineData("-5MB")]
    [InlineData("1.5.5MB")]
    [InlineData("1,5GB")]          // German decimal comma must not become 15 GB
    [InlineData("1_000KB")]
    [InlineData("99999999TB")]     // overflow
    public void TryParse_Rejects(string text)
    {
        Assert.False(FclSizeLiteral.TryParse(text, allowGroupSeparators: false, out _, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void TryParse_GroupSeparators_WhenAllowed()
    {
        Assert.True(FclSizeLiteral.TryParse("1,000KB", allowGroupSeparators: true, out long bytes, out _));
        Assert.Equal(1_024_000L, bytes);
    }
}
