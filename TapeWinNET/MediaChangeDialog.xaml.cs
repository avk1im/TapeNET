using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TapeWinNET.Models;    // WarningLevelHelper

namespace TapeWinNET;

/// <summary>One button of a <see cref="MediaChangeDialog"/>, in display order.</summary>
/// <param name="Label">Button text.</param>
/// <param name="IsCancel">Escape / window close maps here; shown set apart, as the last, "end" choice.</param>
/// <param name="IsDefault">Enter maps here. Use only for a non-destructive choice.</param>
/// <param name="MinWidth">Minimum button width.</param>
public sealed record MediaPromptButton(string Label, bool IsCancel = false, bool IsDefault = false, double MinWidth = 150);

/// <summary>
/// The media prompt of TapeWinNET: glyph + headline, message and detail, a severity-graded notice pane, a monospace code
///  pane, and up to four buttons. Every pane collapses when empty. Returns the index of the button chosen
///  (<see cref="SelectedIndex"/>); the host maps it to its own result type.
/// </summary>
/// <remarks>
/// <para>
/// <b>Severity is the app's stock <see cref="WarningLevel"/></b> — the same glyph (<see cref="WarningLevelHelper.GetIcon"/>)
///  and brush (<see cref="WarningLevelHelper.GetBrush"/>) as the log pane. Use <see cref="WarningLevel.Info"/>,
///  <see cref="WarningLevel.Warning"/> or <see cref="WarningLevel.Error"/>.
/// </para>
/// <para>Closing the window without a button leaves <see cref="SelectedIndex"/> at −1: the caller's safe outcome.</para>
/// </remarks>
public partial class MediaChangeDialog : Window
{
    /// <summary>The index of the button chosen; −1 when the window was closed without one.</summary>
    public int SelectedIndex { get; private set; } = -1;

    /// <summary>Legacy: the first button ("continue") was chosen.</summary>
    public bool ContinueBackup => SelectedIndex == 0;

    /// <summary>
    /// Legacy two-button prompt (volume full, insert media): continue, or end the operation.
    /// </summary>
    /// <param name="title">Window title, also shown as the headline.</param>
    /// <param name="status">Semi-bold first paragraph.</param>
    /// <param name="instructions">Second paragraph.</param>
    /// <param name="continueButtonText">Label of the continue button.</param>
    /// <param name="showWarning">Show the "existing data will be erased" notice (error level).</param>
    /// <param name="endButtonText">Label of the end button — name the operation that ends.</param>
    public MediaChangeDialog(string title, string status, string instructions,
        string continueButtonText, bool showWarning = false, string endButtonText = "End Backup")
        : this(title, title, status, instructions,
            [new MediaPromptButton(continueButtonText, MinWidth: 180),
             new MediaPromptButton(endButtonText, IsCancel: true, MinWidth: 100)],
            notice: showWarning ? "WARNING: All existing data on the new media will be permanently erased!" : null,
            severity: showWarning ? WarningLevel.Error : WarningLevel.Warning)
    {
    }

    /// <summary>Creates a media prompt.</summary>
    /// <param name="title">Window title.</param>
    /// <param name="headline">Short bold summary next to the severity glyph.</param>
    /// <param name="message">Semi-bold first paragraph; null = none.</param>
    /// <param name="detail">Second paragraph; null = none.</param>
    /// <param name="buttons">The buttons, in display order (two to four).</param>
    /// <param name="notice">Text of the severity-graded notice pane; null = no pane.</param>
    /// <param name="severity">Glyph and notice colours: Info, Warning or Error.</param>
    /// <param name="code">Verbatim text for the monospace pane (header description, diagnosis); null = no pane.</param>
    public MediaChangeDialog(
        string title,
        string headline,
        string? message,
        string? detail,
        IReadOnlyList<MediaPromptButton> buttons,
        string? notice = null,
        WarningLevel severity = WarningLevel.Warning,
        string? code = null)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        InitializeComponent();

        Title = title;
        HeadlineTextBlock.Text = headline;
        SetOptional(MessageTextBlock, MessageTextBlock, message);
        SetOptional(DetailTextBlock, DetailTextBlock, detail);
        SetOptional(NoticeTextBlock, NoticeBorder, notice);
        SetOptional(CodeTextBlock, CodeBorder, code);
        ApplySeverity(severity);
        AddButtons(buttons);

        // Window icon — the shared tape-media icon
        var icon = TapeIcons.GetTapeMediaIcon(large: true);
        if (icon != null)
        {
            icon.Freeze();
            Icon = icon;
        }
    }

    private static void SetOptional(TextBlock text, FrameworkElement container, string? value)
    {
        text.Text = value ?? string.Empty;
        container.Visibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AddButtons(IReadOnlyList<MediaPromptButton> buttons)
    {
        for (int i = 0; i < buttons.Count; i++)
        {
            MediaPromptButton spec = buttons[i];
            int index = i;
            var button = new Button
            {
                Content = spec.Label,
                MinWidth = spec.MinWidth,
                Padding = new Thickness(10, 3, 10, 3),
                // The cancel ("end") choice stands apart from the others, as in the legacy dialogs
                Margin = spec.IsCancel ? new Thickness(24, 0, 0, 0) : new Thickness(0, 0, 8, 0),
                IsCancel = spec.IsCancel,
                IsDefault = spec.IsDefault,
            };
            button.Click += (_, _) =>
            {
                SelectedIndex = index;
                DialogResult = true;
            };
            ButtonPanel.Children.Add(button);
        }
    }

    /// <summary>
    /// Glyph and notice colours from the stock <see cref="WarningLevel"/> look: the level's own icon and brush; the
    ///  pane's background is a light tint of that brush.
    /// </summary>
    private void ApplySeverity(WarningLevel severity)
    {
        Brush brush = WarningLevelHelper.GetBrush(severity) ?? SystemColors.ControlTextBrush;

        GlyphTextBlock.Text = WarningLevelHelper.GetIcon(severity);
        GlyphTextBlock.Foreground = brush;
        NoticeBorder.BorderBrush = brush;
        NoticeBorder.Background = brush is SolidColorBrush solid
            ? new SolidColorBrush(Color.FromArgb(0x24, solid.Color.R, solid.Color.G, solid.Color.B))
            : SystemColors.ControlBrush;
    }
}
