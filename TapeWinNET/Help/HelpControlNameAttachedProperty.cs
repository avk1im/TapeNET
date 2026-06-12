using System.Windows;

namespace TapeWinNET.Help;

/// <summary>
/// Attached property <c>help:Help.ControlName</c> that tags a UI element with
/// its Reveal/Walkthrough help name.
/// <para>
/// The value is matched (slugified via <see cref="HelpNET.Content.HelpSlug.From"/>)
/// against the <c>## Controls</c> chapter of the host's help topic to retrieve an
/// inline definition for the Reveal overlay.  The attached property value may be
/// provided either as a display name (<c>"Backup sets list"</c>) or as a pre-slug
/// (<c>"backup-sets-list"</c>) — both resolve to the same cache entry.
/// </para>
/// <para>
/// A control may carry both <c>help:Help.ControlName</c> (for Reveal) and
/// <c>help:Help.TopicId</c> (for F1 deep-link navigation); they are independent.
/// Only controls tagged with <c>ControlName</c> are enumerated by Reveal.
/// </para>
/// </summary>
public static class HelpControlNameAttachedProperty
{
    /// <summary>
    /// Identifies the <c>ControlName</c> attached property.
    /// </summary>
    public static readonly DependencyProperty ControlNameProperty =
        DependencyProperty.RegisterAttached(
            "ControlName",
            typeof(string),
            typeof(HelpControlNameAttachedProperty),
            new PropertyMetadata(null));

    /// <summary>Gets the control-name value assigned to <paramref name="element"/>.</summary>
    public static string? GetControlName(DependencyObject element)
        => (string?)element.GetValue(ControlNameProperty);

    /// <summary>Sets the control-name value on <paramref name="element"/>.</summary>
    public static void SetControlName(DependencyObject element, string? value)
        => element.SetValue(ControlNameProperty, value);
}
