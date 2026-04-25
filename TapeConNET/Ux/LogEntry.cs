namespace TapeConNET.Ux;

/// <summary>
/// Structured log entry. Mirrors the TapeWinNET <c>LogEntry</c> record so
/// that service-level code can be lifted between the two apps unchanged.
/// </summary>
/// <param name="Level">Severity classification.</param>
/// <param name="Message">Human-readable message.</param>
/// <param name="IsSub">If <c>true</c>, the entry is a sub-step of the previous
/// non-sub entry (rendered without an icon and slightly indented).</param>
/// <param name="Timestamp">When the entry was created.</param>
public sealed record LogEntry(
    WarningLevel Level,
    string Message,
    bool IsSub = false,
    DateTime Timestamp = default)
{
    public DateTime Timestamp { get; init; } =
        Timestamp == default ? DateTime.Now : Timestamp;
}
