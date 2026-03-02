namespace TapeWinNET.Models;

/// <summary>
/// Represents a property-value pair for display in the ListView.
/// Used for Drive Information and Media Information views.
/// </summary>
public class PropertyItem(string property, string value, bool isHighlighted = false)
{
    public string Property { get; } = property;
    public string Value { get; } = value;

    /// <summary>
    /// When true, the row is displayed in warning color (e.g. red foreground).
    /// Used for TOC-from-file indicator and similar warnings.
    /// </summary>
    public bool IsHighlighted { get; } = isHighlighted;
}