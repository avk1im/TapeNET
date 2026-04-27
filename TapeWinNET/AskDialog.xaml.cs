using System.Windows;
using System.Windows.Input;

namespace TapeWinNET;

/// <summary>
/// Simple text-input dialog with a title, a question prompt, a pre-populated text box,
///  and OK/Cancel buttons.
/// Returns the entered value via <see cref="Answer"/> (alias <see cref="NewName"/>)
///  when <see cref="Window.DialogResult"/> is true.
/// </summary>
public partial class AskDialog : Window
{
    /// <summary>The trimmed text entered by the user.</summary>
    public string Answer => NameTextBox.Text.Trim();

    /// <summary>Alias for <see cref="Answer"/> — kept for callers that pre-date the rename.</summary>
    public string NewName => Answer;

    public AskDialog(string title, string question, string? defaultValue = null)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = question;
        NameTextBox.Text = defaultValue ?? string.Empty;
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
