using HelpNET.Content;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TapeWinNET.Help.Overlays;

/// <summary>
/// Walkthrough overlay: shows numbered blue outlines on all control-step targets
/// and an amber spotlight on the current step's control.
/// <para>
/// Unlike <see cref="RevealOverlay"/>, this overlay is <em>non-interactive</em>:
/// controls remain fully operational (no hit-test capture) and no click-to-activate
/// event is wired.  Tour navigation is driven entirely by the ViewModel via
/// <see cref="SetCurrentStep"/>.
/// </para>
/// </summary>
internal sealed class WalkthroughOverlay : HelpOverlayBase
{
    private readonly IHelpPaneHost _host;

    // Current tour steps; pushed by HelpPaneViewModel via SetTour / SetCurrentStep.
    private IReadOnlyList<WalkthroughStep> _steps = [];
    private int _currentStepIndex = -1;

    public WalkthroughOverlay(FrameworkElement overlayRoot, IHelpPaneHost host,
        FrameworkElement? excludedElement = null)
        : base(overlayRoot, excludedElement)
    {
        _host = host;

        // Walkthrough adorner: no dimming, no fill — clean outlines + step badges only.
        Adorner.DimInactive      = false;
        Adorner.FillTargets      = false;
        Adorner.AutolabelTargets = false; // we compute labels explicitly per step
    }

    // ── Non-interactive: controls stay live ──────────────────────────────────
    protected override bool EnableHitTestCapture => false;

    // ── Tour state ────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the tour steps and current cursor index, then refreshes the adorner.
    /// Call after constructing (or when the tour changes).
    /// </summary>
    public void SetTour(IReadOnlyList<WalkthroughStep> steps, int currentStepIndex)
    {
        _steps            = steps;
        _currentStepIndex = currentStepIndex;
        RefreshTargets();
    }

    /// <summary>
    /// Moves the amber spotlight to a different step without re-enumerating targets.
    /// Faster than <see cref="SetTour"/> when only the step cursor moved.
    /// </summary>
    public void SetCurrentStep(int stepIndex)
    {
        _currentStepIndex = stepIndex;
        RefreshTargets();
    }

    // ── Target enumeration ────────────────────────────────────────────────────

    /// <summary>
    /// Resolves every control step in the tour by looking up its <c>Target</c> slug via 
    /// <see cref="HelpControlNameAttachedProperty.GetControlName"/>.
    /// Action steps and unresolvable / invisible controls are skipped.
    /// </summary>
    protected override IReadOnlyList<FrameworkElement> EnumerateTargets()
    {
        var results = new List<FrameworkElement>(_steps.Count);
        foreach (var step in _steps)
        {
            if (step.IsActionStep || string.IsNullOrEmpty(step.Target)) continue;

            var el = FindTargetElement(step.Target);
            if (el is not null && el.IsLoaded && el.IsVisible
                && el.ActualWidth > 0 && el.ActualHeight > 0)
            {
                results.Add(el);
            }
        }
        return results.AsReadOnly();
    }

    // Find the first FrameworkElement in the visual tree whose HelpControlName matches the given target slug
    private static FrameworkElement? FindTargetElement(DependencyObject parent, string target)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        target = HelpSlug.From(target);

        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is FrameworkElement fe)
            {
                var name = HelpControlNameAttachedProperty.GetControlName(fe);
                if (name is not null && HelpSlug.From(name) == target)
                    return fe;
            }

            var result = FindTargetElement(child, target);
            if (result is not null)
                return result;
        }
        return null;
    }

    private FrameworkElement? FindTargetElement(string target)
        => FindTargetElement(OverlayRoot, target);

    // ── Adorner update (override base to set spotlight + labels) ─────────────

    protected override void UpdateAdorner(
        IReadOnlyList<FrameworkElement> targets,
        IReadOnlyList<Rect> rects)
    {
        // Build ordered step-number labels for each resolved target (1-based).
        var labels   = new List<string?>(targets.Count);
        var elements = (IList<FrameworkElement>)targets;
        int stepNum  = 1;

        foreach (var step in _steps)
        {
            if (step.IsActionStep || string.IsNullOrEmpty(step.Target))
                continue;

            var el = FindTargetElement(step.Target);
            if (el is not null && elements.Contains(el))
                labels.Add($"{stepNum}");
            // Always increment so step numbers are consistent regardless of resolved-or-not.
            stepNum++;
        }
        // Trim / pad to the actual target count (safety net against edge cases).
        while (labels.Count > targets.Count) labels.RemoveAt(labels.Count - 1);
        while (labels.Count < targets.Count) labels.Add(null);

        // Spotlight rect: the current step's control if it is resolved.
        Rect? spotlight = null;
        if (_currentStepIndex >= 0 && _currentStepIndex < _steps.Count)
        {
            var cur = _steps[_currentStepIndex];
            if (!cur.IsActionStep && !string.IsNullOrEmpty(cur.Target))
            {
                var el = FindTargetElement(cur.Target);
                if (el is not null)
                {
                    int idx = elements.IndexOf(el);
                    if (idx >= 0 && idx < rects.Count && !rects[idx].IsEmpty)
                        spotlight = rects[idx];
                }
            }
        }

        Adorner.SetTargetsAndSpotlight(
            [.. rects.Where(r => !r.IsEmpty)],
            spotlight,
            labels.AsReadOnly());
    }

    // ── Input overrides ───────────────────────────────────────────────────────

    // Walkthrough is non-interactive: clicks pass through to the underlying controls.
    protected override void HandleMouseDownOnTarget(FrameworkElement target, MouseButtonEventArgs e)
    {
        // Intentionally do nothing — walkthrough does not consume control clicks.
    }

    // Override the base mouse-down dispatcher so clicks outside targets do NOT
    // deactivate the tour. In walkthrough mode all clicks just pass through.
    protected override void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Deliberately do nothing: the overlay is non-interactive; only Esc / Exit Guide ends the tour.
    }

    // No hand cursor in walkthrough mode; controls show their natural cursor.
    protected override void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        // No override needed for cursor.
    }
}
