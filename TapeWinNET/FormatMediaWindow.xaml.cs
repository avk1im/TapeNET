using System.Windows;

using TapeWinNET.ViewModels;

namespace TapeWinNET;

public partial class FormatMediaWindow : Window
{
    public FormatMediaWindow(FormatMediaViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        var icon = TapeIcons.GetTapeMediaIcon(large: true);
        if (icon != null)
        {
            icon.Freeze();
            Icon = icon;
        }
    }
}
