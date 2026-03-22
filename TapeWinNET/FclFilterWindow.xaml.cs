using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

using TapeWinNET.ViewModels;

namespace TapeWinNET;

/// <summary>
/// Advanced FCL filter editor dialog.
/// Provides a visual DNF condition editor (left pane) and an expandable
/// FCL program text editor (right pane) with bidirectional sync.
/// </summary>
public partial class FclFilterWindow : Window
{
    /// <summary>Width added when the program pane opens via toggle.</summary>
    private const double DefaultProgramPaneWidth = 340;

    private readonly FclFilterWindowVM _viewModel;

    /// <summary>
    /// Suppresses the <see cref="OnProgramPaneToggled"/> window resize
    /// while restoring a saved layout that already includes the pane width.
    /// </summary>
    private bool _suppressProgramPaneToggle;

    public FclFilterWindow(FclFilterWindowVM viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // Listen for program pane toggle to resize the window
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    /// <summary>
    /// The current width of the program column, for persisting the
    /// splitter position across dialog re-opens.
    /// </summary>
    public double ProgramColumnWidth => ProgramColumn.Width.Value;

    /// <summary>
    /// Restores the program pane column to a saved width without adjusting
    /// the window width. Used when the saved <see cref="FclFilterWindowState.Width"/>
    /// already includes the program pane, so <see cref="OnProgramPaneToggled"/>
    /// must not add width a second time.
    /// </summary>
    public void RestoreProgramPaneOpen(double columnWidth)
    {
        ProgramColumn.Width = new GridLength(columnWidth);
        _suppressProgramPaneToggle = true;
        _viewModel.IsProgramPaneOpen = true;
        _suppressProgramPaneToggle = false;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(FclFilterWindowVM.IsProgramPaneOpen):
                OnProgramPaneToggled();
                break;

            case nameof(FclFilterWindowVM.SelectedDiagnosticSpan):
                OnDiagnosticSpanChanged();
                break;
        }
    }

    /// <summary>
    /// Expands or collapses the program pane by adjusting the window width.
    /// </summary>
    private void OnProgramPaneToggled()
    {
        if (_suppressProgramPaneToggle)
            return;

        if (_viewModel.IsProgramPaneOpen)
        {
            Width += DefaultProgramPaneWidth;
            ProgramColumn.Width = new GridLength(DefaultProgramPaneWidth);
        }
        else
        {
            var currentWidth = ProgramColumn.Width.Value;
            ProgramColumn.Width = new GridLength(0);
            Width -= currentWidth;
        }
    }

    /// <summary>
    /// Selects the text span in the program TextBox corresponding to the
    /// selected diagnostic. Best-effort: the user may have edited the text
    /// since the diagnostic was produced, so offsets could be stale.
    /// </summary>
    private void OnDiagnosticSpanChanged()
    {
        var span = _viewModel.SelectedDiagnosticSpan;
        if (span is not { } s)
            return;

        var text = ProgramTextBox.Text;
        if (s.Start >= text.Length)
            return;

        // Clamp to text length
        var length = Math.Min(s.Length, text.Length - s.Start);

        ProgramTextBox.Select(s.Start, length);

        // Scroll the selection into view
        ProgramTextBox.ScrollToLine(ProgramTextBox.GetLineIndexFromCharacterIndex(s.Start));

        ProgramTextBox.Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        base.OnClosed(e);
    }
}
