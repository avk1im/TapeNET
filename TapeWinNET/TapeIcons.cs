using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using System.Collections.Concurrent;

using Windows.Win32;
using Windows.Win32.UI.Shell;

using System.Runtime.InteropServices;
using Windows.Win32.System.SystemServices;


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

// helpers to extract tape drive related icons
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
}
