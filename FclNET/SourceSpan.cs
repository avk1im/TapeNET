namespace FclNET;

/// <summary>
/// Represents a span in the original FCL source text.
/// Used by all AST nodes and diagnostics to reference exact positions.
/// </summary>
/// <param name="Start">Zero-based character offset in the source string.</param>
/// <param name="Length">Number of characters in the span.</param>
public readonly record struct SourceSpan(int Start, int Length)
{
    /// <summary>Exclusive end position.</summary>
    public int End => Start + Length;

    /// <summary>A zero-length span (used for synthetic nodes with no source representation).</summary>
    public static readonly SourceSpan None = new(0, 0);

    /// <summary>Creates a span covering from <paramref name="first"/> through <paramref name="last"/>.</summary>
    public static SourceSpan Covering(SourceSpan first, SourceSpan last) =>
        new(first.Start, last.End - first.Start);

    public override string ToString() => $"[{Start}..{End})";
}
