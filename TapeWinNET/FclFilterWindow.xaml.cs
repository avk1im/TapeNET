using System.ComponentModel;
using System.Windows;

using TapeWinNET.ViewModels;

namespace TapeWinNET;

/// <summary>
/// Advanced FCL filter editor dialog.
/// Provides a visual DNF condition editor (left pane) and an expandable
/// FCL program text editor (right pane) with bidirectional sync.
/// </summary>
public partial class FclFilterWindow : Window
{
    /// <summary>Width added when the program pane opens.</summary>
    private const double ProgramPaneWidth = 340;

    private readonly FclFilterWindowVM _viewModel;

    public FclFilterWindow(FclFilterWindowVM viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // Listen for program pane toggle to resize the window
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FclFilterWindowVM.IsProgramPaneOpen))
            return;

        if (_viewModel.IsProgramPaneOpen)
        {
            // Expand: grow the window and set the program column width
            Width += ProgramPaneWidth;
            ProgramColumn.Width = new GridLength(ProgramPaneWidth);
        }
        else
        {
            // Collapse: shrink the window and hide the program column
            ProgramColumn.Width = new GridLength(0);
            Width -= ProgramPaneWidth;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        base.OnClosed(e);
    }
}
