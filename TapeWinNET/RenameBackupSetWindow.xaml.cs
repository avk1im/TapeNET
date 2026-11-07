using System.Windows;
using System.Windows.Input;
using TapeWinNET.ViewModels;

namespace TapeWinNET;

/// <summary>
/// Rename a backup set — and, for an encrypted set, check its current password and optionally change it
///  (Design-Encryption §11.1.6). Replaces the plain name prompt for every set.
/// </summary>
public partial class RenameBackupSetWindow : Window
{
    private readonly RenameBackupSetViewModel _viewModel;

    public RenameBackupSetWindow(RenameBackupSetViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        viewModel.CloseRequested += ok => DialogResult = ok;
        Loaded += (_, _) =>
        {
            // An encrypted set: the current password comes first (it may unlock the name); else the name, selected
            if (viewModel.IsEncrypted)
                CurrentPasswordInput.FocusBox();
            else
            {
                NameTextBox.Focus();
                NameTextBox.SelectAll();
            }
        };
    }

    /// <summary>Checks the current password when its field loses focus — a typo shows under the field (W3).</summary>
    private async void CurrentPasswordInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (CurrentPasswordInput.IsKeyboardFocusWithin)
            return;
        if (await _viewModel.VerifyCurrentPasswordAsync() && NameTextBox.IsEnabled && !NameTextBox.IsKeyboardFocusWithin)
        {
            NameTextBox.Focus();
            NameTextBox.SelectAll();
        }
    }
}
