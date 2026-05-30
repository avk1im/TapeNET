using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using TapeWinNET.Services;

namespace TapeWinNET;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Drive number to open on startup (can be set via command line).
    /// </summary>
    public static int StartupDriveNumber { get; private set; } = 0;

    /// <summary>Cached application icon for all windows.</summary>
    public static ImageSource? ApplicationIcon { get; private set; }

    /// <summary>
    /// Process-wide logger factory writing to the Visual Studio debug output pane.
    /// <list type="bullet">
    ///  <item>DEBUG builds: Trace level — all messages from all libraries.</item>
    ///  <item>RELEASE with debugger attached: Information level.</item>
    ///  <item>RELEASE without debugger: null logger (zero overhead).</item>
    /// </list>
    /// Shared by <see cref="Services.TapeService"/>, <see cref="AppAiSessionHost"/>,
    ///  and any other library that accepts an <see cref="ILogger"/> or
    ///  <see cref="ILoggerFactory"/>.
    /// </summary>
    public static ILoggerFactory LoggerFactory { get; } = BuildLoggerFactory();

#if DEBUG
    private static ILoggerFactory BuildLoggerFactory() =>
        Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
            builder.AddDebug().SetMinimumLevel(LogLevel.Trace));
#else
    private static ILoggerFactory BuildLoggerFactory() =>
        Debugger.IsAttached
            ? Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
                builder.AddDebug().SetMinimumLevel(LogLevel.Information))
            : NullLoggerFactory.Instance;
#endif

    /// <summary>
    /// Process-wide AI session host (lazy — session built on first <c>EnsureAsync</c> call).
    /// </summary>
    public static AppAiSessionHost AiSessionHost { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Parse command line arguments for drive number
        if (e.Args.Length > 0)
        {
            foreach (var arg in e.Args)
            {
                if (arg.StartsWith("-drive:", StringComparison.OrdinalIgnoreCase) ||
                    arg.StartsWith("-d:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = arg.Split(':');
                    if (parts.Length > 1 && int.TryParse(parts[1], out int driveNum))
                        StartupDriveNumber = driveNum;
                }
                else if (int.TryParse(arg, out int driveNum))
                {
                    StartupDriveNumber = driveNum;
                }
            }
        }

        // Create and cache the application icon
        ApplicationIcon = TapeIcons.GetTapeDriveIcon(large: true);
        ApplicationIcon?.Freeze();

        // Set default icon for all windows via EventManager
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await AiSessionHost.DisposeAsync();
        base.OnExit(e);
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window && window.Icon == null && ApplicationIcon != null)
            window.Icon = ApplicationIcon;
    }
}
