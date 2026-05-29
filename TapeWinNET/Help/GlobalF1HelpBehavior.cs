using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TapeWinNET.Help;

/// <summary>
/// App-level F1 handler.  When F1 is pressed, walks the visual tree upward
/// from the currently focused element to find the nearest ancestor (or self)
/// that carries a <see cref="HelpTopicIdAttachedProperty.TopicIdProperty"/>,
/// then asks the nearest <see cref="IHelpPaneHost"/> window to open the pane
/// for that topic.
/// </summary>
public static class GlobalF1HelpBehavior
{
    // Default topic shown when no TopicId is found on the focused element's ancestors
    private const string FallbackTopicId = "home";

    /// <summary>
    /// Called by the App-level F1 KeyBinding.  Resolves the focused element's
    /// topic and opens the HelpPane on the relevant host window.
    /// </summary>
    public static void HandleF1()
    {
        var focused = FocusManager.GetFocusedElement(
            Application.Current.MainWindow) as DependencyObject
            ?? Application.Current.MainWindow;

        string? topicId = ResolveTopicId(focused);

        // Walk up the window hierarchy to find the nearest IHelpPaneHost
        var window = Window.GetWindow((DependencyObject)focused)
                     ?? Application.Current.MainWindow;

        if (window is IHelpPaneHost host)
        {
            OpenHelpPane(host, topicId ?? FallbackTopicId);
        }
        else if (Application.Current.MainWindow is IHelpPaneHost mainHost)
        {
            OpenHelpPane(mainHost, topicId ?? FallbackTopicId);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Walks up the visual tree from <paramref name="element"/> and returns the
    /// first <c>help:Help.TopicId</c> value found, or <c>null</c> if none exists.
    /// </summary>
    public static string? ResolveTopicId(DependencyObject? element)
    {
        var current = element;
        while (current != null)
        {
            var topicId = HelpTopicIdAttachedProperty.GetTopicId(current);
            if (!string.IsNullOrEmpty(topicId))
                return topicId;

            current = VisualTreeHelper.GetParent(current)
                      ?? LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    /// <summary>
    /// Asks a host window to open (or navigate) the HelpPane to <paramref name="topicId"/>.
    /// The host must expose an <c>OpenHelpPane(string topicId)</c> method; this
    /// helper calls it via the <see cref="IHelpPaneHost"/> contract extended in
    /// each window's partial class.
    /// </summary>
    private static void OpenHelpPane(IHelpPaneHost host, string topicId)
        => host.OpenHelpPane(topicId);
}
