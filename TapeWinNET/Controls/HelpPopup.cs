using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace TapeWinNET.Controls;

/// <summary>
/// Reusable info popup used by both the glossary link handler (§6.8a) and the
/// Reveal overlay (Phase 8a).
/// <para>
/// Shows a short plain-text definition near the mouse cursor, with an optional
/// footer link, and manages the <c>StaysOpen</c>-deferral timer that prevents
/// the opening click's <c>MouseUp</c> from immediately dismissing the popup.
/// </para>
/// </summary>
public sealed class HelpPopup
{
    // ── Visual elements ───────────────────────────────────────────────────────

    private readonly Popup       _popup;
    private readonly TextBlock   _textBlock;
    private readonly Border      _border;
    private readonly TextBlock   _footerBlock;
    private readonly DispatcherTimer _timer;

    // Optional footer-link click action, updated by SetFooter.
    private Action? _footerAction;

    // ── Construction ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new <see cref="HelpPopup"/> anchored to <paramref name="placementTarget"/>.
    /// </summary>
    public HelpPopup(UIElement placementTarget)
    {
        _textBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize     = 12,
            FontFamily   = new FontFamily("Segoe UI"),
            Foreground   = new SolidColorBrush(Color.FromRgb(0x00, 0x33, 0x66)),
        };

        var footerLink = new Hyperlink(new Run("View details…"))
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x66, 0x99)),
        };
        footerLink.Click += FooterLink_Click;

        _footerBlock = new TextBlock
        {
            Margin    = new Thickness(0, 5, 0, 0),
            FontSize  = 11,
            FontStyle = FontStyles.Italic,
            Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x66, 0x99)),
            Visibility = Visibility.Collapsed,
        };
        _footerBlock.Inlines.Add(footerLink);

        _border = new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(0xCC, 0xE5, 0xFF)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0xCC)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(10, 7, 10, 7),
            MaxWidth        = 300,
            Child           = new StackPanel
            {
                MaxWidth = 300,
                Children = { _textBlock, _footerBlock },
            },
        };

        _popup = new Popup
        {
            Child              = _border,
            Placement          = PlacementMode.Mouse,
            StaysOpen          = false,
            AllowsTransparency = true,
            MaxWidth           = 320,
            PlacementTarget    = placementTarget,
        };

        // Single-shot timer: sets StaysOpen back to false after 500 ms, preventing
        //  the opening click's MouseUp from immediately closing the popup.
        _timer = new DispatcherTimer
        {
            Interval  = TimeSpan.FromMilliseconds(500),
            IsEnabled = false,
        };
        _timer.Tick += (_, _) =>
        {
            _popup.StaysOpen  = false;
            _timer.IsEnabled  = false; // single-shot
        };
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary><c>true</c> while the popup is visible.</summary>
    public bool IsOpen => _popup.IsOpen;

    /// <summary>
    /// Sets the optional footer link. Pass <c>null</c> for both arguments to hide
    /// the footer entirely.
    /// </summary>
    /// <param name="text">Footer link label (e.g. "View full glossary…").</param>
    /// <param name="onClick">Action invoked when the footer link is clicked.</param>
    public void SetFooter(string? text, Action? onClick)
    {
        _footerAction = onClick;

        if (text is null || onClick is null)
        {
            _footerBlock.Visibility = Visibility.Collapsed;
            return;
        }

        // Update the single Hyperlink's Run text.
        if (_footerBlock.Inlines.FirstInline is Hyperlink hl
            && hl.Inlines.FirstInline is Run run)
        {
            run.Text = text;
        }
        _footerBlock.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Shows the popup with <paramref name="text"/> near the current mouse position.
    /// Applies the <c>StaysOpen</c> deferral timer so the opening click's
    /// <c>MouseUp</c> does not immediately dismiss the popup.
    /// </summary>
    public void Show(string text)
    {
        // Close any currently-open instance first.
        _popup.IsOpen = false;

        // Strip markdown bold markers for plain display.
        _textBlock.Text = text.Replace("**", string.Empty);

        // Force layout so the popup resizes correctly before opening.
        _border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _border.Arrange(new Rect(_border.DesiredSize));

        // Open with StaysOpen=true so the opening click's MouseUp does not close it.
        _popup.StaysOpen = true;
        _popup.IsOpen    = true;
        _popup.Child.UpdateLayout();

        // Arm the single-shot timer; after 500 ms it flips StaysOpen back to false.
        _timer.IsEnabled = true;
    }

    /// <summary>Closes the popup immediately.</summary>
    public void Close()
    {
        _timer.IsEnabled = false;
        _popup.StaysOpen = false;
        _popup.IsOpen    = false;
    }

    // ── Event handling ────────────────────────────────────────────────────────

    private void FooterLink_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _footerAction?.Invoke();
    }
}
