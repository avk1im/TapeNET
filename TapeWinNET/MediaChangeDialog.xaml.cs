using System.Windows;

namespace TapeWinNET;

/// <summary>
/// Dialog for prompting user to change media during multi-volume backup.
/// </summary>
public partial class MediaChangeDialog : Window
{
    public bool ContinueBackup { get; private set; }

    public MediaChangeDialog(int currentVolume, int nextVolume, int filesBackedUp, int totalFiles, long bytesBackedUp)
    {
        InitializeComponent();

        StatusTextBlock.Text = $"Volume #{currentVolume} is full.\n" +
                               $"Backed up: {filesBackedUp:N0} of {totalFiles:N0} files (~{Windows.Win32.System.SystemServices.Helpers.BytesToString(bytesBackedUp)})";

        InstructionsTextBlock.Text = $"1. Remove the current media (Volume #{currentVolume})\n" +
                                     $"2. Insert a formatted media for Volume #{nextVolume}\n" +
                                     $"3. Click \"Continue\" when ready";

        ContinueButton.Content = $"Continue with Volume #{nextVolume}";

        // Set window icon
        var icon = TapeIcons.GetTapeMediaIcon(large: true);
        if (icon != null)
        {
            icon.Freeze();
            Icon = icon;
        }
    }

    private void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        ContinueBackup = true;
        DialogResult = true;
    }

    private void AbortButton_Click(object sender, RoutedEventArgs e)
    {
        ContinueBackup = false;
        DialogResult = true;
    }
}