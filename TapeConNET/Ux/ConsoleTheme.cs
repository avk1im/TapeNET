using Spectre.Console;

namespace TapeConNET.Ux;

/// <summary>
/// Single source of truth for mapping <see cref="WarningLevel"/> to
/// Spectre.Console <see cref="Style"/> and to a short text icon shown in
/// front of non-sub log lines.
/// </summary>
public static class ConsoleTheme
{
    public static Style StyleFor(WarningLevel level) => level switch
    {
        WarningLevel.None      => Style.Plain,
        WarningLevel.Completed => new Style(foreground: Color.Green),
        WarningLevel.Info      => new Style(foreground: Color.Blue),
        WarningLevel.Warning   => new Style(foreground: Color.Yellow),
        WarningLevel.Failed    => new Style(foreground: Color.Orange1),
        WarningLevel.Error     => new Style(foreground: Color.Red, decoration: Decoration.Bold),
        _                      => Style.Plain,
    };

    public static string IconFor(WarningLevel level) => level switch
    {
        WarningLevel.None      => "  ",
        WarningLevel.Completed => "✓ ",
        WarningLevel.Info      => "ℹ ",
        WarningLevel.Warning   => "⚠ ",
        WarningLevel.Failed    => "✗ ",
        WarningLevel.Error     => "✗ ",
        _                      => "  ",
    };
}
