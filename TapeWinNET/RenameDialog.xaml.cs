using System.Windows;
using System.Windows.Input;

namespace TapeWinNET;

/// <summary>
/// Simple rename dialog with a prompt, pre-populated text box, and OK/Cancel buttons.
/// Returns the new name via <see cref="NewName"/> when <see cref="Window.DialogResult"/> is true.
/// </summary>
public partial class RenameDialog : Window
{
    public string NewName => NameTextBox.Text.Trim();

    public RenameDialog(string title, string prompt, string currentName)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        NameTextBox.Text = currentName;
        NameTextBox.SelectAll();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(NameTextBox.Text))
            DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void NameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && !string.IsNullOrWhiteSpace(NameTextBox.Text))
            DialogResult = true;
    }
}
