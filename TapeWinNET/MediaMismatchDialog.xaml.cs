using System.Windows;
using System.Windows.Media;
using TapeLibNET.Services;

namespace TapeWinNET;

/// <summary>
/// Severity of a media-identity prompt, driving the detail pane's colour and glyph. Distinct from the
///  outcome — a Warning-severity <see cref="TapeMediaVerdict.WrongVolume"/> and an Error-severity
///  <see cref="TapeMediaVerdict.WrongKind"/> both still ask the same three/four questions.
/// </summary>
public enum MediaMismatchSeverity
{
    /// <summary>Informational — rare here; a benign discrepancy the user just confirms.</summary>
    Info,

    /// <summary>Recoverable / probably-wrong-media — the common case (wrong volume, verify mismatch).</summary>
    Warning,

    /// <summary>Destructive or clearly wrong — a stern warning (overwriting real content, wrong kind).</summary>
    Error,
}

/// <summary>
/// Modal dialog presenting a media-identity mismatch and its courses of action
///  (<see cref="MediaMismatchChoice"/>). Purpose-built companion to <see cref="MediaChangeDialog"/>:
///  where that is a 2-choice / bool media-swap prompt, this returns the richer choice enum and grades
///  its warning pane by <see cref="MediaMismatchSeverity"/>.
/// </summary>
/// <remarks>
/// The host (<see cref="TapeWinNET.Services.WpfServiceHost"/>) builds the localized headline / detail /
///  proceed-label and the allowed-choice set; this dialog is pure presentation and returns the
///  <see cref="Result"/> the user picked.
/// </remarks>
public partial class MediaMismatchDialog : Window
{
    /// <summary>The user's choice. Defaults to <see cref="MediaMismatchChoice.Abort"/> (the safe outcome).</summary>
    public MediaMismatchChoice Result { get; private set; } = MediaMismatchChoice.Abort;

    /// <summary>
    /// Creates a media-mismatch prompt.
    /// </summary>
    /// <param name="title">Window title (e.g. "Overwrite media?").</param>
    /// <param name="headline">Short bold summary shown next to the severity glyph.</param>
    /// <param name="detail">The explanation and its implications (already localized).</param>
    /// <param name="headerDescription">The offending header's own description (its <c>ToString()</c>), or "Unidentified media".</param>
    /// <param name="severity">Colour/glyph grade of the detail pane.</param>
    /// <param name="proceedLabel">Label for the single-Proceed button (e.g. "Proceed &amp; overwrite" vs "Proceed").</param>
    /// <param name="allowRetry">Show the Retry ("Insert different media") button — only where insert machinery exists.</param>
    /// <param name="allowProceedAlways">Show the "Always proceed" button — hidden for one-off operations (e.g. import).</param>
    public MediaMismatchDialog(
        string title,
        string headline,
        string detail,
        string headerDescription,
        MediaMismatchSeverity severity,
        string proceedLabel,
        bool allowRetry,
        bool allowProceedAlways)
    {
        InitializeComponent();

        Title = title;
        HeadlineTextBlock.Text = headline;
        DetailTextBlock.Text = detail;
        HeaderDescriptionTextBlock.Text = headerDescription;

        ApplySeverity(severity);

        // Buttons: Abort and Proceed are always present; Retry / ProceedAlways are conditional.
        ProceedButton.Content = proceedLabel;
        AbortButton.Content = "Abort";

        RetryButton.Content = "Insert different media";
        RetryButton.Visibility = allowRetry ? Visibility.Visible : Visibility.Collapsed;

        ProceedAlwaysButton.Content = "Always proceed";
        ProceedAlwaysButton.Visibility = allowProceedAlways ? Visibility.Visible : Visibility.Collapsed;

        // Window icon — reuse the shared tape-media icon (matches MediaChangeDialog).
        var icon = TapeIcons.GetTapeMediaIcon(large: true);
        if (icon != null)
        {
            icon.Freeze();
            Icon = icon;
        }
    }

    /// <summary>Sets the detail pane's glyph and colours to match the severity.</summary>
    private void ApplySeverity(MediaMismatchSeverity severity)
    {
        // (glyph, background, border) per grade — mirrors the app's info/warning/error palette.
        (string glyph, Color bg, Color border) = severity switch
        {
            MediaMismatchSeverity.Info  => ("\u2139", Color.FromRgb(0xE6, 0xF2, 0xFF), Color.FromRgb(0x33, 0x99, 0xFF)), // ℹ
            MediaMismatchSeverity.Error => ("\u26D4", Color.FromRgb(0xFF, 0xCC, 0xCC), Color.FromRgb(0xCC, 0x00, 0x00)), // ⛔
            _                           => ("\u26A0", Color.FromRgb(0xFF, 0xF4, 0xCE), Color.FromRgb(0xCC, 0x99, 0x00)), // ⚠ (Warning)
        };

        GlyphTextBlock.Text = glyph;
        GlyphTextBlock.Foreground = new SolidColorBrush(border);
        DetailBorder.Background = new SolidColorBrush(bg);
        DetailBorder.BorderBrush = new SolidColorBrush(border);
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)         => Close(MediaMismatchChoice.Retry);
    private void ProceedButton_Click(object sender, RoutedEventArgs e)       => Close(MediaMismatchChoice.Proceed);
    private void ProceedAlwaysButton_Click(object sender, RoutedEventArgs e) => Close(MediaMismatchChoice.ProceedAlways);
    private void AbortButton_Click(object sender, RoutedEventArgs e)         => Close(MediaMismatchChoice.Abort);

    /// <summary>Records the choice and closes with a positive dialog result.</summary>
    private void Close(MediaMismatchChoice choice)
    {
        Result = choice;
        DialogResult = true;
    }
}
