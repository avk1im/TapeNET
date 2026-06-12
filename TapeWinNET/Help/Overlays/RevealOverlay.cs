using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TapeWinNET.Help.Overlays;

/// <summary>
/// Reveal overlay: highlights every control on the active window/dialog that
/// carries a <c>help:Help.ControlName</c> attached property, and raises
/// <see cref="TargetActivated"/> when the user clicks one.
/// <para>
/// The overlay stays active after a click so users can inspect multiple controls
/// in sequence. It deactivates on:
/// <list type="bullet">
///   <item>A click that does not land on any tagged control (outside-click).</item>
///   <item>Pressing <c>Esc</c>.</item>
///   <item>An explicit <see cref="HelpOverlayBase.Deactivate"/> call.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class RevealOverlay : HelpOverlayBase
{
    private readonly IHelpPaneHost _host;

    /// <summary>Exposes the overlay root so callers can detect root changes.</summary>
    public FrameworkElement OverlayRootElement => OverlayRoot;

    /// <summary>
    /// Raised when a tagged control is clicked.
    /// The argument carries the element and its resolved control name.
    /// </summary>
    public event EventHandler<RevealTarget>? TargetActivated;

    public RevealOverlay(FrameworkElement overlayRoot, IHelpPaneHost host)
        : base(overlayRoot)
    {
        _host = host;
    }

    // ── Target enumeration ────────────────────────────────────────────────────

    /// <summary>
    /// Walks the visual tree under <see cref="HelpOverlayBase.OverlayRoot"/> and
    /// collects every loaded, visible, non-zero-size element that carries a
    /// non-empty <see cref="HelpControlNameAttachedProperty.ControlNameProperty"/>.
    /// </summary>
    protected override IReadOnlyList<FrameworkElement> EnumerateTargets()
    {
        var results = new List<FrameworkElement>();
        CollectTaggedElements(OverlayRoot, results);
        return results.AsReadOnly();
    }

    private static void CollectTaggedElements(DependencyObject parent, List<FrameworkElement> results)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is FrameworkElement fe)
            {
                var name = HelpControlNameAttachedProperty.GetControlName(fe);
                if (!string.IsNullOrWhiteSpace(name)
                    && fe.IsLoaded
                    && fe.IsVisible
                    && fe.ActualWidth  > 0
                    && fe.ActualHeight > 0)
                {
                    results.Add(fe);
                }
            }

            CollectTaggedElements(child, results);
        }
    }

    // ── Input ─────────────────────────────────────────────────────────────────

    protected override void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        var hitIndex = HitTestTargets(e.GetPosition(OverlayRoot));
        if (hitIndex.HasValue)
        {
            var element     = GetTarget(hitIndex.Value)!;
            var controlName = HelpControlNameAttachedProperty.GetControlName(element)!;
            TargetActivated?.Invoke(this, new RevealTarget(element, controlName));
            // Mark handled so the underlying control doesn't actuate.
            e.Handled = true;
            // Overlay stays active — user can click another control.
        }
        else
        {
            // Miss: deactivate and swallow the click.
            Deactivate();
            e.Handled = true;
        }
    }
}
