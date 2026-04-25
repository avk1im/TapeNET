using System.Collections.Concurrent;

using TapeLibNET.Services;

namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// In-memory <see cref="ITapeServiceHost"/> for integration tests.
/// <para>
/// Records every <see cref="Report"/> call and every
///  <see cref="OnServiceStateChanged"/> notification. Prompt methods
///  (<see cref="Confirm"/>, <see cref="Select"/>, <see cref="Ask"/>) dequeue
///  from pre-loaded answer queues; when a queue is empty they return the
///  supplied defaults so tests that do not expect a prompt still work.
/// </para>
/// </summary>
/// <remarks>
/// Analogous to <c>TapeLibNET.Tests.Helpers.TestNotifiable</c> at the agent
///  layer — a recording stub that lets round-trip tests assert on log output
///  without any UI dependency.
/// </remarks>
public sealed class TestTapeServiceHost : ITapeServiceHost
{
    // ── Recorded data ─────────────────────────────────────────────────────────

    /// <summary>
    /// Every entry emitted via <see cref="Report"/>, in emission order.
    /// Thread-safe — uses <see cref="ConcurrentQueue{T}"/>.
    /// </summary>
    public ConcurrentQueue<ReportEntry> Reports { get; } = new();

    /// <summary>
    /// Every <see cref="ServiceStateChange"/> notification received, in order.
    /// </summary>
    public ConcurrentQueue<ServiceStateChange> StateChanges { get; } = new();

    // ── Canned prompt answers ─────────────────────────────────────────────────

    /// <summary>
    /// Queued <see cref="bool"/> answers consumed one-by-one by
    ///  <see cref="Confirm"/>. When empty, <paramref name="defaultAnswer"/> is
    ///  returned (non-interactive / safe-default behaviour).
    /// </summary>
    public Queue<bool> ConfirmAnswers { get; } = new();

    /// <summary>
    /// Queued index answers consumed one-by-one by <see cref="Select"/>.
    /// When empty, <paramref name="defaultIndex"/> is returned.
    /// </summary>
    public Queue<int> SelectAnswers { get; } = new();

    /// <summary>
    /// Queued string answers consumed one-by-one by <see cref="Ask"/>.
    /// When empty, <paramref name="defaultValue"/> is returned.
    /// </summary>
    public Queue<string?> AskAnswers { get; } = new();

    // ── Convenience assertions ────────────────────────────────────────────────

    /// <summary>
    /// <see langword="true"/> when at least one entry at
    ///  <see cref="ServiceReportLevel.Error"/> or
    ///  <see cref="ServiceReportLevel.Failed"/> has been recorded.
    /// </summary>
    public bool HasErrors => Reports.Any(r =>
        r.Level is ServiceReportLevel.Error or ServiceReportLevel.Failed);

    /// <summary>
    /// <see langword="true"/> when at least one entry at
    ///  <see cref="ServiceReportLevel.Completed"/> has been recorded.
    /// </summary>
    public bool HasCompleted => Reports.Any(r => r.Level == ServiceReportLevel.Completed);

    /// <summary>
    /// Returns all recorded messages that contain <paramref name="fragment"/>
    ///  (case-insensitive by default).
    /// </summary>
    public IEnumerable<ReportEntry> FindMessages(
        string fragment,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
        => Reports.Where(r => r.Message.Contains(fragment, comparison));

    /// <summary>
    /// <see langword="true"/> when any recorded message contains
    ///  <paramref name="fragment"/> (case-insensitive by default).
    /// </summary>
    public bool ContainsMessage(
        string fragment,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
        => Reports.Any(r => r.Message.Contains(fragment, comparison));

    // ── ITapeServiceHost — Logging ────────────────────────────────────────────

    /// <inheritdoc/>
    public void Report(ServiceReportLevel level, string message, bool isSubEntry = false)
        => Reports.Enqueue(new ReportEntry(level, message, isSubEntry, DateTime.Now));

    // ── ITapeServiceHost — Prompts ────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Returns the next queued answer, or <paramref name="defaultAnswer"/> when
    ///  the queue is empty.
    /// </remarks>
    public bool Confirm(string question, bool defaultAnswer = false)
        => ConfirmAnswers.Count > 0 ? ConfirmAnswers.Dequeue() : defaultAnswer;

    /// <inheritdoc/>
    /// <remarks>
    /// Returns the next queued index, or <paramref name="defaultIndex"/> when
    ///  the queue is empty.
    /// </remarks>
    public int Select(string question, IReadOnlyList<string> choices, int defaultIndex = 0)
        => SelectAnswers.Count > 0 ? SelectAnswers.Dequeue() : defaultIndex;

    /// <inheritdoc/>
    /// <remarks>
    /// Returns the next queued string, or <paramref name="defaultValue"/> when
    ///  the queue is empty.
    /// </remarks>
    public string? Ask(string question, string? defaultValue = null)
        => AskAnswers.Count > 0 ? AskAnswers.Dequeue() : defaultValue;

    // ── ITapeServiceHost — State notification ─────────────────────────────────

    /// <inheritdoc/>
    public void OnServiceStateChanged(ServiceStateChange change)
        => StateChanges.Enqueue(change);

    // ── Reset ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Clears all recorded entries and pending prompt answers. Useful when
    ///  a single host instance is reused across multiple test phases.
    /// </summary>
    public void Reset()
    {
        while (Reports.TryDequeue(out _)) { }
        while (StateChanges.TryDequeue(out _)) { }
        ConfirmAnswers.Clear();
        SelectAnswers.Clear();
        AskAnswers.Clear();
    }

    // ── Inner types ───────────────────────────────────────────────────────────

    /// <summary>
    /// Snapshot of a single <see cref="Report"/> invocation.
    /// </summary>
    public record ReportEntry(
        ServiceReportLevel Level,
        string Message,
        bool IsSubEntry,
        DateTime Timestamp);
}
