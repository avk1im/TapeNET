using System.Diagnostics.CodeAnalysis;

namespace TapeLibNET;

// ── Compression mode ─────────────────────────────────────────────────────────

/// <summary>
/// Per-set compression mode, mirroring <see cref="TapeHashAlgorithm"/>.
/// </summary>
public enum TapeCompression
{
    /// <summary>No compression. Hardware compression is disabled for the set.</summary>
    None,

    /// <summary>
    ///  Hardware compression (drive-native). The drive's built-in compressor is used;
    ///  the on-tape byte stream is not further processed by the software.
    /// </summary>
    Hardware,

    /// <summary>
    ///  Software compression via ZSTD (ZstdNet/libzstd). Hardware compression is
    ///  disabled for the set; each file body is compressed independently.
    /// </summary>
    Software,
}

// ── Level constants and presets ───────────────────────────────────────────────

/// <summary>
/// ZSTD compression level constants and preset helpers.
/// Levels 1–19 are valid; higher levels trade speed for ratio.
/// </summary>
public static class ZstdLevel
{
    /// <summary>Minimum ZSTD compression level.</summary>
    public const int Min = 1;

    /// <summary>Maximum ZSTD compression level.</summary>
    public const int Max = 19;

    /// <summary>Fast preset (level 3). Prioritises throughput.</summary>
    public const int Fast = 3;

    /// <summary>Balanced preset (level 5). Default for new sets.</summary>
    public const int Balanced = 5;

    /// <summary>High preset (level 9). Better ratio, slower.</summary>
    public const int High = 9;

    /// <summary>Default level applied when no explicit level is specified.</summary>
    public const int Default = Balanced;

    /// <summary>
    /// Clamps <paramref name="level"/> to the valid range [<see cref="Min"/>, <see cref="Max"/>].
    /// </summary>
    public static int Clamp(int level) => Math.Clamp(level, Min, Max);
}

// ── CompressionPreset — shared CLI/WPF parse helper ──────────────────────────

/// <summary>
/// Translates between human-readable compression specifiers used by the CLI and WPF
/// and the (<see cref="TapeCompression"/>, level) pair stored in the request/TOC.
/// </summary>
public static class CompressionPreset
{
    // ── Well-known keyword strings ────────────────────────────────────────────

    public const string KeyOff      = "off";
    public const string KeyNone     = "none";
    public const string KeyHardware = "hardware";
    public const string KeyLow      = "low";       // alias → Fast
    public const string KeyMedium   = "medium";    // alias → Balanced (default)
    public const string KeyHigh     = "high";      // alias → High

    // ── Parse ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a CLI/config specifier such as
    /// <c>off | none | hardware | low | medium | high | 1..19</c>
    /// into a (<see cref="TapeCompression"/>, level) pair.
    /// Returns <see langword="false"/> and an error message when the input is not recognised.
    /// </summary>
    public static bool TryParse(
        string?  specifier,
        out TapeCompression mode,
        out int  level,
        [NotNullWhen(false)] out string? errorMessage)
    {
        level        = ZstdLevel.Default;
        errorMessage = null;

        switch (specifier?.Trim().ToLowerInvariant())
        {
            case KeyOff:
            case KeyNone:
            case "":
            case null:
                mode = TapeCompression.None;
                return true;

            case KeyHardware:
                mode = TapeCompression.Hardware;
                return true;

            case KeyLow:
                mode  = TapeCompression.Software;
                level = ZstdLevel.Fast;
                return true;

            case KeyMedium:
                mode  = TapeCompression.Software;
                level = ZstdLevel.Balanced;
                return true;

            case KeyHigh:
                mode  = TapeCompression.Software;
                level = ZstdLevel.High;
                return true;

            default:
                if (int.TryParse(specifier?.Trim(), out int numericLevel))
                {
                    if (numericLevel < ZstdLevel.Min || numericLevel > ZstdLevel.Max)
                    {
                        mode         = TapeCompression.None;
                        errorMessage = $"Compression level {numericLevel} is out of range " +
                                       $"({ZstdLevel.Min}–{ZstdLevel.Max}).";
                        return false;
                    }
                    mode  = TapeCompression.Software;
                    level = numericLevel;
                    return true;
                }
                mode         = TapeCompression.None;
                errorMessage = $"Unrecognised compression specifier '{specifier}'. " +
                               $"Valid values: off, none, hardware, low, medium, high, or 1–19.";
                return false;
        }
    }

    /// <summary>
    /// Returns the display name for the given mode/level combination, e.g.
    /// "Software (Balanced, level 5)" or "Hardware" or "None".
    /// </summary>
    public static string DisplayName(TapeCompression mode, int level = ZstdLevel.Default) =>
        mode switch
        {
            TapeCompression.Hardware => "Hardware",
            TapeCompression.Software => $"Software ({PresetName(level)}, level {level})",
            _                        => "None",
        };

    /// <summary>
    /// Returns the preset name for a given level, e.g. "Fast", "Balanced", "High",
    /// or "Custom" for values that do not match a named preset.
    /// </summary>
    public static string PresetName(int level) => level switch
    {
        ZstdLevel.Fast     => "Fast",
        ZstdLevel.Balanced => "Balanced",
        ZstdLevel.High     => "High",
        _                  => "Custom",
    };

    /// <summary>
    /// Returns the canonical CLI/config string for a (mode, level) pair —
    /// the inverse of <see cref="TryParse"/>.
    /// </summary>
    public static string ToSpecifier(TapeCompression mode, int level = ZstdLevel.Default) =>
        mode switch
        {
            TapeCompression.Hardware => KeyHardware,
            TapeCompression.Software => level switch
            {
                ZstdLevel.Fast     => KeyLow,
                ZstdLevel.Balanced => KeyMedium,
                ZstdLevel.High     => KeyHigh,
                _                  => level.ToString(),
            },
            _ => KeyNone,
        };
}
