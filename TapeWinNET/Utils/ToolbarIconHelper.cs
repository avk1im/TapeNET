using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TapeWinNET.Utils;

/// <summary>
/// Provides Segoe MDL2 Assets glyph constants and a factory for toolbar icon elements.
/// All toolbar button icons are defined here to keep icon definitions out of XAML.
/// </summary>
internal static class ToolbarIconHelper
{
    // ── Segoe MDL2 Assets glyph constants ────────────────────────────────────

    /// <summary>OpenFile glyph — used for "Open Virtual Drive".</summary>
    public const string GlyphOpenVirtualDrive = "\uE8DA";

    /// <summary>Globe glyph — used for "Connect to Remote Host / Open Remote".</summary>
    public const string GlyphConnectRemote = "\uE774";

    /// <summary>Sync glyph — used for "Re-read TOC".</summary>
    public const string GlyphRereadToc = "\uE895";

    /// <summary>Eject glyph — used for "Eject".</summary>
    public const string GlyphEject = "\uF413";

    /// <summary>Save glyph — used for "New Backup".</summary>
    public const string GlyphNewBackup = "\uE74E";

    /// <summary>Download glyph — used for "Restore".</summary>
    public const string GlyphRestore = "\uE896";

    /// <summary>CheckList glyph — used for "Validate".</summary>
    public const string GlyphValidate = "\uE9D5";

    /// <summary>CheckMark/Accept glyph — used for "Verify".</summary>
    public const string GlyphVerify = "\uE8FB";

    /// <summary>Refresh glyph — used for "Refresh".</summary>
    public const string GlyphRefresh = "\uE72C";

    /// <summary>Filter glyph — used for "Show Incremental Sets".</summary>
    public const string GlyphShowIncremental = "\uE71C";

    /// <summary>Help glyph — used for "About / Help".</summary>
    public const string GlyphHelp = "\uE897";

    // ── Factory ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="TextBlock"/> that renders a Segoe MDL2 Assets glyph,
    ///  suitable for use as the <c>Content</c> of a toolbar <see cref="Button"/> or
    ///  <see cref="System.Windows.Controls.Primitives.ToggleButton"/>.
    /// <para>
    ///  <c>IsHitTestVisible</c> is set to <see langword="false"/> so pointer events
    ///  pass through to the parent button rather than being swallowed by the TextBlock.
    /// </para>
    /// </summary>
    /// <param name="glyph">One of the <c>Glyph*</c> constants defined in this class.</param>
    /// <param name="fontSize">Icon font size in points (default 14).</param>
    public static TextBlock CreateGlyphBlock(string glyph, double fontSize = 14) =>
        new()
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = fontSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
}
