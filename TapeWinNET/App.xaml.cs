using System.Windows;
using System.Windows.Media;

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
