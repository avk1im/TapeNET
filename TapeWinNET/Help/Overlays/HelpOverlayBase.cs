using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Collections.ObjectModel;

namespace TapeWinNET.Help.Overlays;

/// <summary>
/// Base class for help overlays (Reveal, Walkthrough).
/// <para>
/// Manages:
/// <list type="bullet">
///   <item>Adorner lifecycle — adds/removes <see cref="HelpHighlightAdorner"/> on the
///         overlay root's <see cref="AdornerLayer"/>.</item>
///   <item>Input capture — hooks tunneling <c>Preview*</c> events on the <b>parent
///         Window</b> so that <em>all</em> window areas (menu bar, toolbar, log pane,
///         empty content regions) are intercepted, not just the overlay-root subtree.
///         An optional <see cref="_excludedElement"/> (e.g. the HelpPane UserControl)
///         is exempt from capture so its buttons stay fully interactive.</item>
///   <item>Esc exit — handles PreviewKeyDown at window level.</item>
///   <item>Live geometry — re-computes target rectangles on <c>LayoutUpdated</c> so
///         highlights track window resizes and splitter drags.</item>
///   <item>Hand cursor — shown when hovering over a tagged target; default elsewhere.</item>
/// </list>
/// </para>
/// </summary>
internal abstract class HelpOverlayBase : IHelpOverlay
{
    // ── Fields ────────────────────────────────────────────────────────────────

    /// <summary>The host's content-area root (Column-0 grid) on which we adorn.</summary>
    protected FrameworkElement OverlayRoot { get; }

    /// <summary>The adorner layer of <see cref="OverlayRoot"/>.</summary>
    protected AdornerLayer Layer { get; }

    /// <summary>The visual-only adorner drawn on the layer.</summary>
    internal HelpHighlightAdorner Adorner { get; }

    // Element whose clicks/moves are never intercepted (e.g. the HelpPane UserControl).
    //  Null means no exclusion.  Set by the subclass constructor.
    private readonly FrameworkElement? _excludedElement;

    // Parent window found during Activate.  Input events are subscribed here so that
    //  ALL areas of the window — including menu bar, toolbar, log pane, and empty grids
    //  that have no Background brush — receive the PreviewMouse* handlers.
    private Window? _window;

    // Current enumerated targets (updated on LayoutUpdated / Activate).
    private IReadOnlyList<FrameworkElement> _targets = [];
    // Pre-computed bounds in OverlayRoot coordinates (parallel to _targets).
    private ReadOnlyCollection<Rect> _targetRects = new(Array.Empty<Rect>());

    // ── Construction ──────────────────────────────────────────────────────────

    /// <param name="overlayRoot">The host content root that the adorner is placed on.</param>
    /// <param name="excludedElement">Optional element (e.g. the HelpPane) whose input
    ///  events are never captured, keeping its own UI fully interactive.</param>
    protected HelpOverlayBase(FrameworkElement overlayRoot, FrameworkElement? excludedElement = null)
    {
        OverlayRoot      = overlayRoot;
        _excludedElement = excludedElement;

        var layer = AdornerLayer.GetAdornerLayer(overlayRoot)
            ?? throw new InvalidOperationException(
                $"No AdornerLayer found on '{overlayRoot.GetType().Name}'. " +
                 "Ensure the element is part of a visual tree that includes an AdornerDecorator.");
        Layer   = layer;
        Adorner = new HelpHighlightAdorner(overlayRoot, excludedElement);
    }

    // ── IHelpOverlay ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public bool IsActive { get; private set; }

    /// <inheritdoc/>
    public event EventHandler? Deactivated;

    /// <inheritdoc/>
    public void Activate()
    {
        if (IsActive) return;
        IsActive = true;

        // Subscribe at the parent-window level so events from ALL areas of the window
        //  (menu bar, toolbar, log pane, empty content grids with no Background brush)
        //  are intercepted.  LayoutUpdated stays on OverlayRoot — it only needs to
        //  track layout changes within the content area.
        _window = Window.GetWindow(OverlayRoot);
        if (_window is not null)
        {
            _window.PreviewMouseDown += OnPreviewMouseDown;
            _window.PreviewMouseMove += OnPreviewMouseMove;
            _window.PreviewKeyDown   += OnPreviewKeyDown;
        }
        else
        {
            // Fallback (should not occur in a live window): subscribe on OverlayRoot only.
            OverlayRoot.PreviewMouseDown += OnPreviewMouseDown;
            OverlayRoot.PreviewMouseMove += OnPreviewMouseMove;
            OverlayRoot.PreviewKeyDown   += OnPreviewKeyDown;
        }
        OverlayRoot.LayoutUpdated += OnLayoutUpdated;

        // Add the adorner and draw the initial targets.
        Layer.Add(Adorner);
        // Make the adorner hit-testable: it now forms a capture surface over OverlayRoot.
        //  Mouse events land on the adorner (the topmost visual) rather than on the
        //  underlying controls, so those controls are never in the event routing path
        //  and their class handlers (e.g. Button.Click) cannot fire.
        Adorner.IsHitTestVisible = true;
        RefreshTargets();
    }

    /// <inheritdoc/>
    public void Deactivate()
    {
        if (!IsActive) return;
        IsActive = false;

        // Unhook events from wherever we subscribed them.
        if (_window is not null)
        {
            _window.PreviewMouseDown -= OnPreviewMouseDown;
            _window.PreviewMouseMove -= OnPreviewMouseMove;
            _window.PreviewKeyDown   -= OnPreviewKeyDown;
            _window = null;
        }
        else
        {
            OverlayRoot.PreviewMouseDown -= OnPreviewMouseDown;
            OverlayRoot.PreviewMouseMove -= OnPreviewMouseMove;
            OverlayRoot.PreviewKeyDown   -= OnPreviewKeyDown;
        }
        OverlayRoot.LayoutUpdated -= OnLayoutUpdated;

        // Release the capture surface, remove the adorner, and restore cursor.
        Adorner.IsHitTestVisible = false;
        Layer.Remove(Adorner);
        Mouse.OverrideCursor = null;

        Deactivated?.Invoke(this, EventArgs.Empty);
    }

    // ── Abstract hook ─────────────────────────────────────────────────────────

    /// <summary>
    /// Enumerates the elements this overlay should highlight.
    /// Called on every <c>LayoutUpdated</c> to keep highlights in sync with
    /// window resizes and visibility changes.
    /// </summary>
    protected abstract IReadOnlyList<FrameworkElement> EnumerateTargets();

    // ── Input handlers (virtual — subclasses override OnPreviewMouseDown) ─────

    protected virtual void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Clicks within the excluded element (e.g. HelpPane) are never intercepted —
        //  the pane's own buttons (Close, Exit Reveal, etc.) must stay interactive.
        if (IsInExcludedElement(e)) return;

        // Any click outside a tagged target deactivates Reveal and consumes the event
        //  so the underlying control (menu item, toolbar button, list item, etc.) does
        //  not also fire.
        if (HitTestTargets(e.GetPosition(OverlayRoot)) is int targetIndex
            && GetTarget(targetIndex) is FrameworkElement target)
        {
            HandleMouseDownOnTarget(target, e);
        }
        else
        {
            Deactivate();
            e.Handled = true;
        }
    }

    protected abstract void HandleMouseDownOnTarget(FrameworkElement target, MouseButtonEventArgs e);

    protected virtual void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        // Restore the default cursor when over the excluded element (HelpPane manages
        //  its own cursor) or over any part of the window outside OverlayRoot (menu bar,
        //  toolbar, log pane, status bar, etc.).
        if (IsInExcludedElement(e) || !IsInsideOverlayRoot(e))
        {
            Mouse.OverrideCursor = null;
            return;
        }
        var hit = HitTestTargets(e.GetPosition(OverlayRoot));
        Mouse.OverrideCursor = hit.HasValue ? Cursors.Hand : null;
    }

    protected virtual void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Deactivate();
            e.Handled = true;
        }
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
        => RefreshTargets();

    // ── Geometry helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Re-enumerates targets and recomputes their bounds in OverlayRoot coordinates.
    /// Called automatically on LayoutUpdated; also called at Activate time.
    /// </summary>
    protected void RefreshTargets()
    {
        _targets = EnumerateTargets();

        var rects = new List<Rect>(_targets.Count);
        foreach (var el in _targets)
        {
            if (!el.IsVisible || el.ActualWidth <= 0 || el.ActualHeight <= 0)
            {
                rects.Add(Rect.Empty); // placeholder to keep indexes aligned
                continue;
            }
            try
            {
                var transform = el.TransformToAncestor(OverlayRoot);
                var bounds    = transform.TransformBounds(new Rect(el.RenderSize));
                rects.Add(bounds);
            }
            catch (InvalidOperationException)
            {
                // Element is no longer connected to the same visual tree.
                rects.Add(Rect.Empty);
            }
        }

        _targetRects    = rects.AsReadOnly();
        Adorner.Targets = [.. rects.Where(r => !r.IsEmpty)];
    }

    /// <summary>
    /// Returns the index of the innermost target rect that contains <paramref name="point"/>,
    /// or <c>null</c> if no target is hit.
    /// </summary>
    private int? HitTestTargets(Point point)
    {
        List<Rect> hitRects = [];
        for (int i = 0; i < _targetRects.Count; i++)
        {
            if (!_targetRects[i].IsEmpty && _targetRects[i].Contains(point))
                hitRects.Add(_targetRects[i]);
        }

        if (hitRects.Count == 1)
            return _targetRects.IndexOf(hitRects[0]);
        else if (hitRects.Count > 1)
        {
            // Multiple targets stacked on top of each other — return the innermost one, the one
            //  contained inside the others. Enumerate backwards since the last one in the list
            //  should be the topmost in the visual tree.
            for (int i = hitRects.Count - 1; i >= 0; i--)
            {
                bool isInnermost = false;
                for (int j = 0; j < hitRects.Count; j++)
                {
                    if (i != j && hitRects[j].Contains(hitRects[i]))
                    {
                        isInnermost = true;
                        break;
                    }
                }
                if (isInnermost)
                    return _targetRects.IndexOf(hitRects[i]);
            }
            // No innermost found -> return the last one (should be the topmost in the visual tree,
            //  since targets are enumerated in visual tree order)
            return _targetRects.IndexOf(hitRects.Last());
        }
        else
            return null;
    }

    /// <summary>Returns the target element at the given list index.</summary>
    private FrameworkElement? GetTarget(int index)
        => index >= 0 && index < _targets.Count ? _targets[index] : null;

    // ── Exclusion / bounds helpers ────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> if the mouse event is within the bounds of the excluded element
    /// (e.g. the HelpPane UserControl), which must remain fully interactive.
    /// </summary>
    protected bool IsInExcludedElement(MouseEventArgs e)
    {
        if (_excludedElement is null || !_excludedElement.IsVisible) return false;
        var pt = e.GetPosition(_excludedElement);
        return new Rect(_excludedElement.RenderSize).Contains(pt);
    }

    /// <summary>
    /// Returns <c>true</c> if the mouse event is within the bounds of
    /// <see cref="OverlayRoot"/> — distinguishes between the empty content area
    /// (still inside the root, cursor should show null/default) and entirely different
    /// UI regions such as the menu bar, toolbar, and log pane (cursor should also be
    /// null/default but for a different conceptual reason).
    /// </summary>
    private bool IsInsideOverlayRoot(MouseEventArgs e)
    {
        var pt = e.GetPosition(OverlayRoot);
        return pt.X >= 0 && pt.Y >= 0
               && pt.X <= OverlayRoot.ActualWidth
               && pt.Y <= OverlayRoot.ActualHeight;
    }
}
