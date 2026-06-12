using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace TapeWinNET.Help.Overlays;

/// <summary>
/// Base class for help overlays (Reveal, Walkthrough).
/// <para>
/// Manages:
/// <list type="bullet">
///   <item>Adorner lifecycle — adds/removes <see cref="HelpHighlightAdorner"/> on the
///         overlay root's <see cref="AdornerLayer"/>.</item>
///   <item>Input routing — hooks tunneling <c>Preview*</c> events on the overlay root
///         so the adorner never needs to be hit-testable.</item>
///   <item>Esc exit — focuses the overlay root on activate and handles PreviewKeyDown.</item>
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

    // Current enumerated targets (updated on LayoutUpdated / Activate).
    private IReadOnlyList<FrameworkElement> _targets = [];
    // Pre-computed bounds in OverlayRoot coordinates (parallel to _targets).
    private IReadOnlyList<Rect> _targetRects = [];

    // ── Construction ──────────────────────────────────────────────────────────

    protected HelpOverlayBase(FrameworkElement overlayRoot)
    {
        OverlayRoot = overlayRoot;

        var layer = AdornerLayer.GetAdornerLayer(overlayRoot);
        if (layer is null)
            throw new InvalidOperationException(
                $"No AdornerLayer found on '{overlayRoot.GetType().Name}'. " +
                 "Ensure the element is part of a visual tree that includes an AdornerDecorator.");

        Layer   = layer;
        Adorner = new HelpHighlightAdorner(overlayRoot);
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

        // Wire input on the overlay root (not on the adorner).
        OverlayRoot.PreviewMouseDown += OnPreviewMouseDown;
        OverlayRoot.PreviewMouseMove += OnPreviewMouseMove;
        OverlayRoot.PreviewKeyDown   += OnPreviewKeyDown;
        OverlayRoot.LayoutUpdated    += OnLayoutUpdated;

        // Add the adorner and draw the initial targets.
        Layer.Add(Adorner);
        RefreshTargets();

        // Focus the overlay root so Esc key events reach us.
        OverlayRoot.Focusable = true;
        OverlayRoot.Focus();
    }

    /// <inheritdoc/>
    public void Deactivate()
    {
        if (!IsActive) return;
        IsActive = false;

        // Unhook events
        OverlayRoot.PreviewMouseDown -= OnPreviewMouseDown;
        OverlayRoot.PreviewMouseMove -= OnPreviewMouseMove;
        OverlayRoot.PreviewKeyDown   -= OnPreviewKeyDown;
        OverlayRoot.LayoutUpdated    -= OnLayoutUpdated;

        // Remove the adorner and restore cursor
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
        // Default: outside-click deactivates.
        if (!HitTestTargets(e.GetPosition(OverlayRoot)).HasValue)
        {
            Deactivate();
            e.Handled = true;
        }
    }

    protected virtual void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
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

        _targetRects   = rects.AsReadOnly();
        Adorner.Targets = [.. rects.Where(r => !r.IsEmpty)];
    }

    /// <summary>
    /// Returns the index of the target rect that contains <paramref name="point"/>,
    /// or <c>null</c> if no target is hit.
    /// </summary>
    protected int? HitTestTargets(Point point)
    {
        for (int i = 0; i < _targetRects.Count; i++)
        {
            if (!_targetRects[i].IsEmpty && _targetRects[i].Contains(point))
                return i;
        }
        return null;
    }

    /// <summary>Returns the target element at the given list index.</summary>
    protected FrameworkElement? GetTarget(int index)
        => index >= 0 && index < _targets.Count ? _targets[index] : null;
}
