using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Media;

namespace TapeWinNET;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public static int StartupDriveNumber { get; private set; } = 0;

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
                    {
                        StartupDriveNumber = driveNum;
                    }
                }
                else if (int.TryParse(arg, out int driveNum))
                {
                    StartupDriveNumber = driveNum;
                }
            }
        }
    }
}
