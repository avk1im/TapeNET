using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.TextFormatting;
using TapeWinNET.Models;
using TapeWinNET.Utils;
using Windows.Win32;
using Windows.Win32.System.SystemServices;
using Windows.Win32.UI.Shell;


namespace TapeWinNET;

internal static class IconLoader
{
    public static BitmapSource? LoadStockIcon(SHSTOCKICONID id, bool large)
    {
        var info = new SHSTOCKICONINFO();
        info.cbSize = (uint)Marshal.SizeOf(info);

        SHGSI_FLAGS flags = SHGSI_FLAGS.SHGSI_ICON |
                    (large ? SHGSI_FLAGS.SHGSI_LARGEICON : SHGSI_FLAGS.SHGSI_SMALLICON);

        var hr = PInvoke.SHGetStockIconInfo(id, flags, ref info);
        if (hr.Failed)
            return null;

        return CreateBitmapSourceFromHIcon(info.hIcon);
    }

    public static BitmapSource? LoadDeviceClassIcon(Guid classGuid, bool large)
    {
        unsafe
        {
            if (!PInvoke.SetupDiLoadClassIcon(classGuid, out DestroyIconSafeHandle hIcon, out int _))
                return null;

            var img = CreateImageSourceFromHIcon(hIcon);

            // SetupDiLoadClassIcon always returns a large icon, so resize if needed
            if (!large && img != null)
                img = ResizeImageSource(img, 16, 16);

            return img;
        }
    }

    private static BitmapSource? CreateBitmapSourceFromHIcon(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero)
            return null;

        return Imaging.CreateBitmapSourceFromHIcon(
            hIcon,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
    }

    private static BitmapSource? CreateImageSourceFromHIcon(DestroyIconSafeHandle hIcon)
    {
        if (hIcon.IsInvalid)
            return null;

        return CreateBitmapSourceFromHIcon(hIcon.DangerousGetHandle());
    }

    private static RenderTargetBitmap ResizeImageSource(BitmapSource source, int width, int height)
    {
        var group = new DrawingGroup();
        group.Children.Add(new ImageDrawing(source, new Rect(0, 0, width, height)));
        var drawingImage = new DrawingImage(group);

        // Render the DrawingImage to a BitmapSource
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        
        using (var context = visual.RenderOpen())
            context.DrawImage(drawingImage, new Rect(0, 0, width, height));
        
        target.Render(visual);

        return target;
    }
}

/// <summary>Helpers to extract tape drive related icons</summary>
public static class TapeIcons
{
    private static readonly Guid GUID_DEVCLASS_TAPEDRIVE =
        new("6D807884-7D21-11CF-801C-08002BE10318");

    public static BitmapSource? GetTapeDriveIcon(bool large)
    {
        return IconLoader.LoadDeviceClassIcon(GUID_DEVCLASS_TAPEDRIVE, large);
    }

    public static BitmapSource? GetTapeMediaIcon(bool large)
    {
        return IconLoader.LoadStockIcon(SHSTOCKICONID.SIID_DRIVEREMOVE, large);
    }

    public static BitmapSource? GetBackupSetIcon(bool large)
    {
        return IconLoader.LoadStockIcon(SHSTOCKICONID.SIID_FOLDER, large);
    }

    public static BitmapSource? GetTapeFileIcon(bool large)
    {
        return IconLoader.LoadStockIcon(SHSTOCKICONID.SIID_DOCNOASSOC, large);
    }

    // Caches key on (number, large): the two sizes render different bitmaps
    private static readonly Dictionary<(int number, bool large), ImageSource> _numberedDriveIcons = [];
    private static readonly Dictionary<(int number, bool large), ImageSource> _numberedRemoteDriveIcons = [];
    private static readonly Dictionary<bool, ImageSource> _remoteDriveIcons = [];

    /// <summary>Tape drive with a drive number badge (subscript, bottom-right).</summary>
    public static ImageSource? GetNumberedTapeDriveIcon(int number, bool large = false)
    {
        if (_numberedDriveIcons.TryGetValue((number, large), out var cached))
            return cached;

        var baseIcon = GetTapeDriveIcon(large);
        if (baseIcon is null)
            return null;

        var badge = IconComposer.RenderDigitBadge(number);   // already frozen
        var composed = IconComposer.ComposeWithOverlay(baseIcon, badge); // freezes result
        _numberedDriveIcons[(number, large)] = composed;
        return composed;
    }

    // Globe overlay — blue glyph on a white disc, rendered once for legibility
    private static BitmapSource? _globeOverlay;

    private static BitmapSource? GlobeOverlay =>
        _globeOverlay ??= CreateGlobeOverlay();

    private static BitmapSource? CreateGlobeOverlay()
    {
        var overlay = IconComposer.RenderGlyph(
            ToolbarIconHelper.GlyphConnectRemote,
            pixelSize: 32,                       // 4× target for a crisp downscale
            color: Color.FromRgb(0, 80, 160),
            backgroundColor: Colors.White);
        overlay?.Freeze();
        return overlay;
    }

    /// <summary>Tape drive with a globe badge (superscript, top-right).</summary>
    public static ImageSource? GetRemoteTapeDriveIcon(bool large = false)
    {
        if (_remoteDriveIcons.TryGetValue(large, out var cached))
            return cached;

        var baseIcon = GetTapeDriveIcon(large);
        if (baseIcon is null || GlobeOverlay is null)
            return null;

        // Globe rides top-right, leaving the bottom-right slot free for a number
        var composed = IconComposer.ComposeWithOverlay(
            baseIcon, GlobeOverlay, OverlayCorner.TopRight);
        _remoteDriveIcons[large] = composed;
        return composed;
    }

    /// <summary>Tape drive with globe (superscript) + drive number (subscript).</summary>
    public static ImageSource? GetNumberedRemoteTapeDriveIcon(int number, bool large = false)
    {
        if (_numberedRemoteDriveIcons.TryGetValue((number, large), out var cached))
            return cached;

        var baseIcon = GetTapeDriveIcon(large);
        if (baseIcon is null || GlobeOverlay is null)
            return null;

        // 1. Globe superscript (top-right)
        var withGlobe = IconComposer.ComposeWithOverlay(
            baseIcon, GlobeOverlay, OverlayCorner.TopRight);

        // 2. Number subscript (bottom-right)
        var badge = IconComposer.RenderDigitBadge(number);
        var composed = IconComposer.ComposeWithOverlay(
            withGlobe, badge, OverlayCorner.BottomRight);

        _numberedRemoteDriveIcons[(number, large)] = composed;
        return composed;
    }
}

/// <summary>Corner of the output canvas where an overlay badge sits.</summary>
public enum OverlayCorner { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>
/// Utilities for composing toolbar icons from a main image and a small overlay badge.
/// </summary>
internal static class IconComposer
{
    private static readonly FontFamily SegoeAssets = new("Segoe MDL2 Assets");

    /// <summary>
    /// Renders a single Segoe MDL2 Assets glyph into a square <see cref="BitmapSource"/>
    ///  of <paramref name="pixelSize"/> × <paramref name="pixelSize"/> at 96 DPI.
    /// Returns <see langword="null"/> if rendering fails.
    /// </summary>
    /// <param name="glyph">Unicode glyph string (one character).</param>
    /// <param name="pixelSize">Target square bitmap size in device-independent pixels.
    ///  Use a multiple of the final display size for crisp downscaling (e.g. 4×).</param>
    /// <param name="color">Fill color for the glyph.</param>
    /// <param name="backgroundColor">
    ///  When provided, a filled circle of this color is drawn behind the glyph.
    ///  Useful for badge overlays that must remain legible against any icon background.
    /// </param>
    public static BitmapSource? RenderGlyph(string glyph, int pixelSize, Color color,
        Color? backgroundColor = null)
    {
        try
        {
            var typeface = new Typeface(SegoeAssets, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            // Use a font size that fills most of the square; 0.8× leaves a small margin
            double fontSize = pixelSize * 0.8;

            var text = new FormattedText(
                glyph,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                new SolidColorBrush(color),
                pixelsPerDip: 1.0);

            var target = new RenderTargetBitmap(pixelSize, pixelSize, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();

            using (var ctx = visual.RenderOpen())
            {
                // Optionally fill a circular disc as badge background
                if (backgroundColor.HasValue)
                {
                    double r = pixelSize / 2.0;
                    ctx.DrawEllipse(
                        new SolidColorBrush(backgroundColor.Value),
                        null,
                        new Point(r, r), r, r);
                }

                // Center the glyph within the square
                double x = (pixelSize - text.Width) / 2.0;
                double y = (pixelSize - text.Height) / 2.0;
                ctx.DrawText(text, new Point(x, y));
            }

            target.Render(visual);
            return target;
        }
        catch
        {
            return null;
        }
    }

    public static BitmapSource RenderDigitBadge(
    int digit,
    int pixelSize = 32,
    Color? discColor = null,
    Color? textColor = null)
    {
        discColor ??= Color.FromRgb(0, 80, 160);   // same blue as the globe
        textColor ??= Colors.White;

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            double r = pixelSize / 2.0;
            var center = new Point(r, r);

            // Filled disc backdrop — reads as a count badge, legible on any icon
            dc.DrawEllipse(new SolidColorBrush(discColor.Value), null, center, r, r);

            var ft = new FormattedText(
                digit.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"),
                             FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                pixelSize * 0.72,           // fill most of the disc
                new SolidColorBrush(textColor.Value),
                1.0)
            { TextAlignment = TextAlignment.Center };

            // x = r centers horizontally; shift up by half the glyph height
            dc.DrawText(ft, new Point(r, r - ft.Height / 2.0));
        }

        var rtb = new RenderTargetBitmap(pixelSize, pixelSize, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>
    /// Composes a <paramref name="main"/> icon with a smaller <paramref name="overlay"/>
    ///  badge placed in the specified <paramref name="corner"/> (default lower-right).
    /// </summary>
    /// <param name="main">The base icon.</param>
    /// <param name="overlay">The badge icon, scaled to <paramref name="overlayFraction"/>
    ///  of the output canvas.</param>
    /// <param name="corner">Which corner receives the badge. Defaults to
    ///  <see cref="OverlayCorner.BottomRight"/> to preserve existing single-badge behavior.</param>
    /// <param name="overlayFraction">Fraction of the canvas size used for the overlay (default 0.5).</param>
    /// <param name="outputSize">
    ///  Pixel dimensions of the output bitmap (default 32). Rendering at 2× the display size
    ///  (the <see cref="Image"/> element will use <c>Width/Height=16</c>) gives WPF enough
    ///  pixels to downscale both the main icon and the overlay badge sharply.
    /// </param>
    /// <returns>
    ///  A new frozen <see cref="BitmapSource"/> with the overlay composited onto the main icon,
    ///  or <paramref name="main"/> unchanged if <paramref name="overlay"/> is <see langword="null"/>.
    /// </returns>
    public static BitmapSource ComposeWithOverlay(BitmapSource main, BitmapSource? overlay,
        OverlayCorner corner = OverlayCorner.BottomRight,
        double overlayFraction = 0.5, int outputSize = 32)
    {
        if (overlay is null)
            return main;

        // Overlay size, then position it in the requested corner of the output canvas
        double oSize = Math.Round(outputSize * overlayFraction);

        double oX = corner is OverlayCorner.TopRight or OverlayCorner.BottomRight
            ? outputSize - oSize
            : 0;
        double oY = corner is OverlayCorner.BottomLeft or OverlayCorner.BottomRight
            ? outputSize - oSize
            : 0;

        var target = new RenderTargetBitmap(outputSize, outputSize, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();

        using (var ctx = visual.RenderOpen())
        {
            // Scale main icon to fill the entire canvas (2× for a 16px source → crisp downscale)
            ctx.DrawImage(main, new Rect(0, 0, outputSize, outputSize));
            ctx.DrawImage(overlay, new Rect(oX, oY, oSize, oSize));
        }

        target.Render(visual);
        target.Freeze();
        return target;
    }
}

/// <summary>
/// Icons for backup source entries (Files to Backup list).
/// </summary>
public static class BackupSourceIcons
{
    private static BitmapSource? _singleFileIcon;
    private static BitmapSource? _singleFolderIcon;
    private static BitmapSource? _filePatternIcon;
    private static BitmapSource? _backupSetIcon;
    private static bool _iconsLoaded;

    static BackupSourceIcons()
    {
        LoadIcons();
    }

    private static void LoadIcons()
    {
        if (_iconsLoaded)
            return;

        try
        {
            // Single file: document icon (same as TapeIcons.GetTapeFileIcon)
            _singleFileIcon = TapeIcons.GetTapeFileIcon(large: false);
            _singleFileIcon?.Freeze();

            // Single folder: folder icon (loaded separately for potential future customization)
            _singleFolderIcon = IconLoader.LoadStockIcon(SHSTOCKICONID.SIID_FOLDER, large: false);
            _singleFolderIcon?.Freeze();

            // File pattern: stack icon for multiple files
            _filePatternIcon = IconLoader.LoadStockIcon(SHSTOCKICONID.SIID_STACK, large: false);
            _filePatternIcon?.Freeze();

            // Files from a previous backup set: tape media icon
            _backupSetIcon = TapeIcons.GetTapeMediaIcon(large: false);
            _backupSetIcon?.Freeze();
        }
        catch
        {
            // If icon loading fails, icons will be null
        }

        _iconsLoaded = true;
    }

    /// <summary>
    /// Gets the appropriate icon for the given backup source type.
    /// </summary>
    public static BitmapSource? GetIcon(BackupSourceType type) => type switch
    {
        BackupSourceType.SingleFile => _singleFileIcon,
        BackupSourceType.SingleFolder => _singleFolderIcon,
        BackupSourceType.FilePattern => _filePatternIcon,
        BackupSourceType.FilesFromBackupSet => _backupSetIcon,
        _ => _singleFileIcon
    };
}
