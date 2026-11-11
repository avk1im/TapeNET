namespace TapeConNET.Ux;

/// <summary>
/// The two presentation decisions every <see cref="ConsoleDocument"/> needs — plain or rich, and the width — as pure
///  functions, so both console implementations decide alike and the tests pin the rules (Design-CliPresentation §6).
/// </summary>
/// <remarks>
/// The inputs come from the console implementation: <see cref="SpectreConsoleUx"/> asks its Spectre profile;
///  <see cref="SilentConsoleUx"/> always counts as redirected.
/// </remarks>
public static class ConsoleCapabilities
{
    /// <summary>Narrowest width a document is laid out for; a narrower console wraps the result itself.</summary>
    public const int MinWidth = 60;

    /// <summary>Widest width a document is laid out for — longer lines read badly, even on a wide console.</summary>
    public const int MaxWidth = 120;

    /// <summary>The width for redirected output (file, pipe) and for an unknown console width.</summary>
    public const int RedirectedWidth = 80;

    /// <summary>
    /// True when the plain form applies: no colour asked for, quiet (script-friendly) output, or output that is not a
    ///  terminal — a file or a pipe must receive neither ANSI codes nor box drawing.
    /// </summary>
    public static bool UsePlain(bool noColor, bool quiet, bool redirected)
        => noColor || quiet || redirected;

    /// <summary>
    /// The layout width: <see cref="RedirectedWidth"/> when redirected or when the console reports no width (≤ 0);
    ///  otherwise <paramref name="consoleWidth"/> clamped to [<see cref="MinWidth"/>, <see cref="MaxWidth"/>].
    /// </summary>
    public static int ClampWidth(int consoleWidth, bool redirected)
        => redirected || consoleWidth <= 0
            ? RedirectedWidth
            : Math.Clamp(consoleWidth, MinWidth, MaxWidth);
}
