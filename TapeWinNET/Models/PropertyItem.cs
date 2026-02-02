namespace TapeWinNET.Models;

/// <summary>
/// Represents a property-value pair for display in the ListView.
/// Used for Drive Information and Media Information views.
/// </summary>
public class PropertyItem(string property, string value)
{
    public string Property { get; } = property;
    public string Value { get; } = value;
}