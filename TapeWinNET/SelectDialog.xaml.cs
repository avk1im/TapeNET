using System.Windows;
using System.Windows.Input;

namespace TapeWinNET;

/// <summary>
/// Minimal selection dialog presenting a list of string choices.
/// Returns the chosen index via <see cref="SelectedIndex"/> when
///  <see cref="Window.DialogResult"/> is <see langword="true"/>.
/// </summary>
public partial class SelectDialog : Window
{
    /// <summary>Zero-based index of the selected item, or -1 if nothing was selected.</summary>
    public int SelectedIndex => ChoicesBox.SelectedIndex;

    public SelectDialog(string title, string question, IReadOnlyList<string> choices, int defaultIndex = 0)
    {
        InitializeComponent();
        Title = title;
        QuestionText.Text = question;
        foreach (var choice in choices)
            ChoicesBox.Items.Add(choice);
        ChoicesBox.SelectedIndex = defaultIndex >= 0 && defaultIndex < choices.Count ? defaultIndex : 0;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (ChoicesBox.SelectedIndex >= 0)
            DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void ChoicesBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ChoicesBox.SelectedIndex >= 0)
            DialogResult = true;
    }
}
