using System.Windows;
using TapeLibNET;

namespace TapeWinNET;

/// <summary>
/// Dialog for handling file backup errors with Skip/Retry/Skip All/Abort options.
/// </summary>
public partial class FileErrorDialog : Window
{
    public FileFailedAction Result { get; private set; } = FileFailedAction.Skip;
    public bool SkipAllErrors { get; private set; }

    public FileErrorDialog(string filePath, string errorMessage)
    {
        InitializeComponent();

        FilePathTextBox.Text = filePath;
        ErrorMessageTextBox.Text = errorMessage;

        // Set window icon
        var icon = TapeIcons.GetTapeFileIcon(large: true);
        if (icon != null)
        {
            icon.Freeze();
            Icon = icon;
        }
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        Result = FileFailedAction.Skip;
        DialogResult = true;
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        Result = FileFailedAction.Retry;
        DialogResult = true;
    }

    private void SkipAllButton_Click(object sender, RoutedEventArgs e)
    {
        Result = FileFailedAction.Skip;
        SkipAllErrors = true;
        DialogResult = true;
    }

    private void AbortButton_Click(object sender, RoutedEventArgs e)
    {
        Result = FileFailedAction.Abort;
        DialogResult = true;
    }
}