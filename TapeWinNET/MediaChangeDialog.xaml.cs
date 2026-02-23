using System.Windows;

namespace TapeWinNET;

/// <summary>
/// Dialog for prompting user during multi-volume backup.
/// Reusable for both "volume full" confirmation and "insert new media" prompts.
/// </summary>
public partial class MediaChangeDialog : Window
{
    public bool ContinueBackup { get; private set; }

    public MediaChangeDialog(string title, string status, string instructions, 
        string continueButtonText, bool showWarning = false)
    {
        InitializeComponent();

        Title = title;
        TitleTextBlock.Text = title;
        StatusTextBlock.Text = status;
        InstructionsTextBlock.Text = instructions;
        ContinueButton.Content = continueButtonText;
        WarningBorder.Visibility = showWarning ? Visibility.Visible : Visibility.Collapsed;

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