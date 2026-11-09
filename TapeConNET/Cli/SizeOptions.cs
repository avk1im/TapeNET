using System.CommandLine;
using System.CommandLine.Parsing;
using FclNET;

namespace TapeConNET.Cli;

/// <summary>
/// Factory for options that take a byte size: accepts <c>65536</c>, <c>64KB</c>, <c>64KiB</c>, <c>1.5GB</c>, <c>2 MB</c>
///  — the FCL size-literal grammar (<see cref="FclSizeLiteral"/>), so <c>--capacity</c> and <c>size &gt; 10MB</c> read
///  alike. Units are binary (1 KB = 1 KiB = 1,024 B).
/// </summary>
internal static class SizeOptions
{
    /// <summary>Appended to every size option's description.</summary>
    public const string UnitsHint = "Accepts " + FclSizeLiteral.UnitList + " (binary: 1 KB = 1024 B), e.g. 64KB, 1.5GB.";

    private const string SizeHelpName = "SIZE";

    /// <summary>A positive byte count up to <see cref="long.MaxValue"/> — capacities.</summary>
    public static Option<long?> Bytes(string name, string description, params string[] aliases)
        => new(name, aliases)
        {
            Description = $"{description} {UnitsHint}",
            HelpName = SizeHelpName,
            CustomParser = result => Parse(result, name, long.MaxValue),
        };

    /// <summary>A positive byte count up to <see cref="uint.MaxValue"/> — block sizes.</summary>
    public static Option<uint?> BlockSize(string name, string description, params string[] aliases)
        => new(name, aliases)
        {
            Description = $"{description} {UnitsHint}",
            HelpName = SizeHelpName,
            CustomParser = result => (uint?)Parse(result, name, uint.MaxValue),
        };

    private static long? Parse(ArgumentResult result, string optionName, long max)
    {
        if (result.Tokens.Count == 0)
            return null;

        string text = result.Tokens[0].Value;
        if (!FclSizeLiteral.TryParse(text, allowGroupSeparators: false, out long bytes, out string? error))
        {
            result.AddError($"{optionName}: invalid size '{text}'. {error}");
            return null;
        }
        if (bytes <= 0 || bytes > max)
        {
            result.AddError($"{optionName}: size '{text}' must be between 1 B and {max:N0} B.");
            return null;
        }
        return bytes;
    }
}
