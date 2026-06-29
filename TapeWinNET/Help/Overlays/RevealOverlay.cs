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
/// <param name="overlayRoot">The host content root (Column-0 grid).</param>
/// <param name="host">The <see cref="IHelpPaneHost"/> that opened this pane.</param>
/// <param name="excludedElement">Element (e.g. the HelpPane UserControl) whose
///  clicks/moves are never captured — its own buttons stay fully interactive.</param>
internal sealed class RevealOverlay(FrameworkElement overlayRoot, IHelpPaneHost host,
    FrameworkElement? excludedElement = null) : HelpOverlayBase(overlayRoot, excludedElement)
{
    private readonly IHelpPaneHost _host = host;

    // Moved to HelpOveralyBase
    // /// <summary>Exposes the overlay root so callers can detect root changes.</summary>
    // public FrameworkElement OverlayRootElement => OverlayRoot;

    /// <summary>
    /// Raised when a tagged control is clicked.
    /// The argument carries the target and its resolved control name.
    /// </summary>
    public event EventHandler<RevealTarget>? TargetActivated;

    // ── Target enumeration ────────────────────────────────────────────────────

    /// <summary>
    /// Walks the visual tree under <see cref="HelpOverlayBase.OverlayRoot"/> and
    /// collects every loaded, visible, non-zero-size target that carries a
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

    /*
    protected override void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Clicks within the excluded target (HelpPane) are never intercepted.
        if (IsInExcludedElement(e)) return;

        if (e.ChangedButton == MouseButton.Left)
        {
            var hitIndex = HitTestTargets(e.GetPosition(OverlayRoot));
            if (hitIndex.HasValue)
            {
                var target     = GetTarget(hitIndex.Value)!;
                var controlName = HelpControlNameAttachedProperty.GetControlName(target)!;
                TargetActivated?.Invoke(this, new RevealTarget(target, controlName));
                // Mark handled so the underlying control doesn't actuate.
                e.Handled = true;
                // Overlay stays active — user can click another control.
                return;
            }
        }

        // Any click (left miss, right, middle) outside a tagged target deactivates Reveal.
        Deactivate();
        e.Handled = true;
    }
    */

    protected override void HandleMouseDownOnTarget(FrameworkElement target, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            var controlName = HelpControlNameAttachedProperty.GetControlName(target);
            if (controlName is not null)
            {
                Adorner.Spotlight = RectOfElement(target);
                TargetActivated?.Invoke(this, new RevealTarget(target, controlName));
            }
        }
        // Mark handled so the underlying control doesn't actuate.
        e.Handled = true;
    }
}
