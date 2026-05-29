using System.Windows;

namespace TapeWinNET.Help;

/// <summary>
/// Attached property <c>help:Help.TopicId</c> that tags a UI element with a
/// help-topic identifier.  Used by <see cref="GlobalF1HelpBehavior"/> to resolve
/// F1 presses to the nearest relevant topic.
/// </summary>
public static class HelpTopicIdAttachedProperty
{
    /// <summary>
    /// Identifies the <c>TopicId</c> attached property.
    /// </summary>
    public static readonly DependencyProperty TopicIdProperty =
        DependencyProperty.RegisterAttached(
            "TopicId",
            typeof(string),
            typeof(HelpTopicIdAttachedProperty),
            new PropertyMetadata(null));

    /// <summary>Gets the topic id assigned to <paramref name="element"/>.</summary>
    public static string? GetTopicId(DependencyObject element)
        => (string?)element.GetValue(TopicIdProperty);

    /// <summary>Sets the topic id on <paramref name="element"/>.</summary>
    public static void SetTopicId(DependencyObject element, string? value)
        => element.SetValue(TopicIdProperty, value);
}
