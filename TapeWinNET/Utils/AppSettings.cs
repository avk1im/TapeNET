using AiNET;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.TextFormatting;

namespace TapeWinNET.Utils;

#region Window Placement
/// <summary>
/// Struct representing the position and size of a window, plus whether it's maximized.
/// </summary>
/// <param name="Bounds">The bounds of the window.</param>
/// <param name="IsMaximized">Whether the window is maximized.</param>
public readonly record struct WindowPlacement(
    Rect Bounds,
    bool IsMaximized,
    (double X, double Y)? Offset = null
)
{
    [JsonIgnore] public double Left => Bounds.Left;
    [JsonIgnore] public double Top => Bounds.Top;
    [JsonIgnore] public double Width => Bounds.Width;
    [JsonIgnore] public double Height => Bounds.Height;

    public WindowPlacement WithBounds(Rect bounds) =>
        this with { Bounds = bounds };
    public WindowPlacement WithIsMaximized(bool isMaximized) =>
        this with { IsMaximized = isMaximized };
    public WindowPlacement WithOffset(double x, double y) =>
        this with { Offset = (x, y) };
}


/// <summary>
/// Extension methods for working with nullable <see cref="WindowPlacement"/> instances.
/// <para>Example usage:</para>
/// <code>
/// WindowPlacement? placement = null;
/// placement.SetBounds(new Rect(100, 200, 800, 600));
/// placement.SetMaximized(true);
/// </code>
/// </summary>
public static class WindowPlacementExtensions
{
    public static void SetBounds(this ref WindowPlacement? placement, Rect bounds)
    {
        placement = (placement ?? new WindowPlacement(default, false))
            .WithBounds(bounds);
    }
    public static void SetMaximized(this ref WindowPlacement? placement, bool isMaximized)
    {
        placement = (placement ?? new WindowPlacement(default, false))
            .WithIsMaximized(isMaximized);
    }
}

/// <summary>
/// Custom JSON converter for <see cref="WindowPlacement"/> to control the serialization format.
/// <para>This prevents output explosion due to the many properties of <see cref="Rect"/>.</para>
/// </summary>
public sealed class WindowPlacementJsonConverter : JsonConverter<WindowPlacement>
{
    public override WindowPlacement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        double x = 0, y = 0, width = 0, height = 0;
        bool isMaximized = false;
        double? offsetX = null, offsetY = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            string prop = reader.GetString()!;
            reader.Read();

            switch (prop)
            {
                case "X": x = reader.GetDouble(); break;
                case "Y": y = reader.GetDouble(); break;
                case "Width": width = reader.GetDouble(); break;
                case "Height": height = reader.GetDouble(); break;
                case "IsMaximized": isMaximized = reader.GetBoolean(); break;
                case "OffsetX": offsetX = reader.GetDouble(); break;
                case "OffsetY": offsetY = reader.GetDouble(); break;
            }
        }

        var placement = new WindowPlacement(new Rect(x, y, width, height), isMaximized);

        if (offsetX.HasValue && offsetY.HasValue)
            placement = placement.WithOffset(offsetX.Value, offsetY.Value);

        return placement;
    }

    public override void Write(Utf8JsonWriter writer, WindowPlacement value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WriteNumber("X", value.Bounds.X);
        writer.WriteNumber("Y", value.Bounds.Y);
        writer.WriteNumber("Width", value.Bounds.Width);
        writer.WriteNumber("Height", value.Bounds.Height);
        writer.WriteBoolean("IsMaximized", value.IsMaximized);

        if (value.Offset is { } o)
        {
            writer.WriteNumber("OffsetX", o.X);
            writer.WriteNumber("OffsetY", o.Y);
        }

        writer.WriteEndObject();
    }
}

/// <summary>
/// Manager for capturing and restoring window placements (position, size, maximized state).
/// </summary>
/// <param name="settings"></param>
public sealed class WindowPlacementManager(AppSettings settings)
{
    private readonly AppSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private const double MinVisible = 100;

    private static string GetKey(Window window) =>
        window == Application.Current.MainWindow
            ? "__MainWindow__"
            : window.GetType().FullName ?? window.GetType().Name;

    #region Capture

    public void Capture(Window window, bool enforceMain = false)
    {
        var bounds = window.WindowState == WindowState.Maximized
            ? window.RestoreBounds
            : new Rect(window.Left, window.Top, window.Width, window.Height);

        var isMaximized = window.WindowState == WindowState.Maximized;

        if (enforceMain || window == Application.Current.MainWindow)
        {
            // absolute
            var placement = new WindowPlacement(bounds, isMaximized);
            _settings.MainWindowPlacement = placement;
        }
        else
        {
            // relative
            var offset = GetOffsetToMain(bounds);
            _settings.DialogPlacements[GetKey(window)] =
                new WindowPlacement(bounds, isMaximized, offset);
        }
    }

    private static (double, double)? GetOffsetToMain(Rect bounds)
    {
        var main = Application.Current.MainWindow;
        if (main == null)
            return null;

        var offsetX = bounds.Left - main.Left;
        var offsetY = bounds.Top - main.Top;
        return (offsetX, offsetY);
    }

    #endregion // Capture

    #region Restore

    public void Restore(Window window, bool enforceMain = false)
    {
        WindowPlacement placement;

        if (enforceMain || window == Application.Current.MainWindow)
        {
            if (!_settings.MainWindowPlacement.HasValue)
                return;

            placement = _settings.MainWindowPlacement.Value;
        }
        else
        {
            if (!_settings.DialogPlacements.TryGetValue(GetKey(window), out placement))
                return;
        }

        ApplyPlacement(window, placement);
    }

    private static void ApplyPlacement(Window window, WindowPlacement placement)
    {
        var bounds = ApplyOffset(placement);
        bounds = ClampToVirtualScreen(bounds, MinVisible);

        window.WindowStartupLocation = WindowStartupLocation.Manual;

        bool isResizable =
            window.ResizeMode == ResizeMode.CanResize ||
            window.ResizeMode == ResizeMode.CanResizeWithGrip;

        // Restore size only if resizable
        if (isResizable)
        {
            window.Width = bounds.Width;
            window.Height = bounds.Height;
        }

        // Always restore position
        window.Left = bounds.X;
        window.Top = bounds.Y;

        // Restore maximized state
        if (placement.IsMaximized && isResizable)
            window.WindowState = WindowState.Maximized;
    }

    private static Rect ApplyOffset(WindowPlacement placement)
    {
        if (Application.Current.MainWindow is { } main && placement.Offset is { } o)
            return new Rect(main.Left + o.X, main.Top + o.Y, placement.Width, placement.Height);
        return placement.Bounds;
    }

    private static Rect ClampToVirtualScreen(Rect rect, double minVisible)
    {
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;

        if (rect.Right > screenLeft + minVisible &&
            rect.Left < screenRight - minVisible &&
            rect.Bottom > screenTop + minVisible &&
            rect.Top < screenBottom - minVisible)
        {
            return rect;
        }

        var x = Math.Min(Math.Max(rect.X, screenLeft), screenRight - minVisible);
        var y = Math.Min(Math.Max(rect.Y, screenTop), screenBottom - minVisible);

        return new Rect(x, y, rect.Width, rect.Height);
    }

    #endregion // Restore

    public void ResetAll(bool mainWindowToo)
    {
        // Clear all dialog placements
        _settings.DialogPlacements.Clear();

        // Optionally clear main window placement
        if (mainWindowToo)
            _settings.MainWindowPlacement = null;
    }
}

/// <summary>
/// Helper class to attach to windows and automatically capture/restore their placement
/// <para>Usage example:</para>
/// <code>
/// public partial class MyDialog : Window
/// {
///     public MyDialog()
///     {
///         InitializeComponent();
///         WindowPlacementApplicator.Attach(this);
///     }
/// }
/// </code>
/// </summary>
public static class WindowPlacementApplicator
{
    private static readonly HashSet<Window> Attached = [];

    public static void Attach(Window window)
    {
        if (window == null) ArgumentNullException.ThrowIfNull(window);
        if (Attached.Contains(window)) return;

        Attached.Add(window);

        window.SourceInitialized += OnSourceInitialized;
        window.Loaded += OnLoaded;
        window.Closing += OnClosing;
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;

        var visibility = window.Visibility;
        window.Visibility = Visibility.Hidden;
        
        var manager = new WindowPlacementManager(App.Settings);
        manager.Restore(window);
        
        window.Visibility = visibility;
    }

    private static void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Window window) return;

        // Ensure that our OnClosing handler is last, to capture the
        //  very last placement of the dialog, e.g. when the Help pane
        //  has been closed already.
        //  By now all the other handlers must've been installed...
        window.Closing -= OnClosing; // ...so remove us first, then...
        window.Closing += OnClosing; // ...re-add -- now we're the last!
    }

    private static void OnClosing(object? sender, CancelEventArgs e)
    {
        if (sender is not Window window) return;

        var manager = new WindowPlacementManager(App.Settings);
        manager.Capture(window);

        // Cleanup
        window.Loaded -= OnLoaded;
        window.Closing -= OnClosing;
        Attached.Remove(window);
    }
}

#endregion // Window Placement

/// <summary>
/// Persisted application settings (window layout, last drive, etc.).
/// Serialized as JSON to a <see cref="Stream"/>; the default file lives in
/// <c>%LocalAppData%\TapeWinNET\Settings.json</c>.
/// </summary>
public class AppSettings
{
    private static readonly string DefaultFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TapeWinNET", "Settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new WindowPlacementJsonConverter() },
    };

    #region Window Placements

    public WindowPlacement? MainWindowPlacement { get; set; }
    public Dictionary<string, WindowPlacement> DialogPlacements { get; set; } = [];

    public void ResetWindowPlacements(bool mainWindowToo)
    {
        var placementManager = new WindowPlacementManager(this);
        placementManager.ResetAll(mainWindowToo);
    }

    #endregion // Window Placements

    #region Main Window Layout

    public double? TreePaneWidth { get; set; }
    public double? LogPaneHeight { get; set; }
    public double? PropertiesPaneHeight { get; set; }

    public void ResetMainWindowLayout()
    {
        TreePaneWidth = null;
        LogPaneHeight = null;
        PropertiesPaneHeight = null;

        LogFilterPaneWidth = null;
    }

    #endregion // Main Window Layout

    #region Last Drive

    public int? LastDriveNumber { get; set; }
    public bool LastDriveWasVirtual { get; set; }

    #endregion

    #region Log Pane

    public bool ShowTimestamps { get; set; } = true;

    // Severity filter checkboxes (all visible by default)
    public bool ShowLogInfo { get; set; } = true;
    public bool ShowLogCompleted { get; set; } = true;
    public bool ShowLogWarning { get; set; } = true;
    public bool ShowLogError { get; set; } = true;
    public bool ShowLogDetails { get; set; } = true;

    public double? LogFilterPaneWidth { get; set; }

    #endregion

    #region View Options

    /// <summary>Whether the media usage bar is shown below the Media Properties list.</summary>
    public bool ShowUsageBar { get; set; } = true;

    #endregion

    #region Remote Host

    /// <summary>Hostname or IP address from the last successful remote connection.</summary>
    public string? LastRemoteHost { get; set; }

    /// <summary>Port from the last successful remote connection.</summary>
    public int? LastRemotePort { get; set; }

    /// <summary>Whether TLS was used in the last successful remote connection.</summary>
    public bool LastRemoteUseTls { get; set; }

    /// <summary>Whether "Use local host" was checked in the last connect dialog.</summary>
    public bool LastRemoteUseLocalHost { get; set; }

    #endregion

    #region Help Pane

    /// <summary>
    /// Width (pixels) of the HelpPane column, keyed by host window name (e.g. "MainWindow").
    /// Enables each host to remember its own preferred pane width independently.
    /// </summary>
    [JsonPropertyName("helpPaneWidthPerHost")]
    public Dictionary<string, double>? HelpPaneWidthPerHost { get; set; }

    /// <summary>
    /// Height (pixels) of the chat sub-pane inside the HelpPane.
    /// Shared across all host windows (one splitter position for the whole app).
    /// </summary>
    [JsonPropertyName("helpPaneChatHeight")]
    public double? HelpPaneChatHeight { get; set; }

    /// <summary>
    /// Last-open topic id per host window name, keyed by host name (e.g. "MainWindow").
    /// Enables restoring the last-viewed topic when the pane is reopened.
    /// </summary>
    [JsonPropertyName("helpPaneLastTopicPerHost")]
    public Dictionary<string, string>? HelpPaneLastTopicPerHost { get; set; }

    public void ResetHelpPaneLayout()
    {
        HelpPaneWidthPerHost = null;
        HelpPaneChatHeight = null;
    }

    #endregion

    #region AI

    public AiProviderPreferences? AIProviderPrefs { get; set; }

    #endregion

    #region Stream-level API

    public void Save(Stream stream)
    {
        JsonSerializer.Serialize(stream, this, JsonOptions);
        stream.Flush();
    }

    public static AppSettings Load(Stream stream)
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions) ?? new();
        }
        catch
        {
            return new();
        }
    }

    #endregion

    #region File-level convenience API

    public void SaveToFile(string? path = null)
    {
        path ??= DefaultFilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            Save(stream);
        }
        catch
        {
            // Best effort
        }
    }

    public static AppSettings LoadFromFile(string? path = null)
    {
        path ??= DefaultFilePath;
        try
        {
            if (!File.Exists(path))
                return new();

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Load(stream);
        }
        catch
        {
            return new();
        }
    }

    #endregion
}
