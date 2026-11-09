using System.Globalization;

namespace FclNET;

/// <summary>
/// The FCL size literal — <c>10MB</c>, <c>1.5 GiB</c>, <c>512</c> — as one reusable grammar: <see cref="FclParser"/>
///  parses its size values through these helpers, and command-line tools parse their size options with
///  <see cref="TryParse"/>, so both accept exactly the same spellings.
/// </summary>
/// <remarks>
/// Units are binary throughout: <c>KB</c> and <c>KiB</c> both mean 1,024 bytes (likewise MB/MiB, GB/GiB, TB/TiB).
///  Unit names are case-insensitive. A literal without a unit counts bytes.
/// </remarks>
public static class FclSizeLiteral
{
    /// <summary>The accepted unit spellings, for help and error texts.</summary>
    public const string UnitList = "B/byte(s), KB/KiB, MB/MiB, GB/GiB, TB/TiB";

    /// <summary>
    /// Parses a complete size literal such as <c>64KB</c>, <c>64 KiB</c>, <c>1.5GB</c> or <c>65536</c>.
    /// </summary>
    /// <param name="text">The literal; surrounding whitespace and a space before the unit are allowed.</param>
    /// <param name="allowGroupSeparators">
    /// Accept <c>,</c> and <c>_</c> as digit-group separators (FCL does). Command lines should pass
    ///  <see langword="false"/>: on a German keyboard <c>1,5GB</c> means 1.5 GB, and silently reading it as 15 GB
    ///  would be the worst possible outcome for a capacity.
    /// </param>
    /// <param name="bytes">The size in bytes (fractional bytes truncated, as in FCL).</param>
    /// <param name="error">Why the literal was refused; <see langword="null"/> on success.</param>
    public static bool TryParse(string? text, bool allowGroupSeparators, out long bytes, out string? error)
    {
        bytes = 0;
        error = null;
        string trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            error = "The size is empty.";
            return false;
        }

        SplitNumberAndSuffix(trimmed, out string numeric, out string suffix);
        numeric = numeric.Trim();   // "10 MB"

        if (numeric.Contains(',') || numeric.Contains('_'))
        {
            if (!allowGroupSeparators)
            {
                error = "Use '.' for decimals; ',' and '_' are not accepted.";
                return false;
            }
            numeric = numeric.Replace(",", "").Replace("_", "");
        }

        // No sign, no exponent: a size is a plain non-negative decimal.
        if (!double.TryParse(numeric, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value))
        {
            error = numeric.Length == 0 ? "The number is missing." : $"'{numeric}' is not a number.";
            return false;
        }

        FclSizeUnit unit = FclSizeUnit.Bytes;
        if (suffix.Length > 0 && !TryParseUnit(suffix, out unit))
        {
            error = $"Unknown size unit '{suffix}'. Expected {UnitList}.";
            return false;
        }

        double result = value * GetMultiplier(unit);
        if (result >= long.MaxValue)
        {
            error = "The size is too large.";
            return false;
        }

        bytes = (long)result;
        return true;
    }

    /// <summary>
    /// Splits <paramref name="text"/> at its first letter: <c>10MB</c> → <c>10</c> + <c>MB</c>. Also used by the
    ///  relative-date parser (<c>7d</c>).
    /// </summary>
    internal static void SplitNumberAndSuffix(string text, out string numeric, out string suffix)
    {
        int i = text.Length;
        for (int j = 0; j < text.Length; j++)
        {
            if (char.IsLetter(text[j]))
            {
                i = j;
                break;
            }
        }

        numeric = text[..i];
        suffix = text[i..];
    }

    /// <summary>Resolves a unit name, decimal-style (<c>KB</c>) or IEC-style (<c>KiB</c>), case-insensitively.</summary>
    internal static bool TryParseUnit(string text, out FclSizeUnit unit)
    {
        unit = text.ToUpperInvariant() switch
        {
            "B" or "BYTE" or "BYTES" => FclSizeUnit.Bytes,
            "KB" or "KIB" => FclSizeUnit.KB,
            "MB" or "MIB" => FclSizeUnit.MB,
            "GB" or "GIB" => FclSizeUnit.GB,
            "TB" or "TIB" => FclSizeUnit.TB,
            _ => (FclSizeUnit)(-1),
        };
        return unit >= FclSizeUnit.Bytes;
    }

    /// <summary>Bytes per <paramref name="unit"/> (binary).</summary>
    internal static long GetMultiplier(FclSizeUnit unit) => unit switch
    {
        FclSizeUnit.KB => 1L << 10,
        FclSizeUnit.MB => 1L << 20,
        FclSizeUnit.GB => 1L << 30,
        FclSizeUnit.TB => 1L << 40,
        _ => 1L,
    };
}
