namespace TapeConNET.Ux;

/// <summary>
/// A bounded progress scope returned by <see cref="IConsoleUx.BeginProgress"/>.
/// Disposing the scope finalizes the progress display (clears the in-place bar
/// and writes a completion line if the scope was not explicitly completed).
/// </summary>
/// <remarks>
/// Implementations must be safe to call from any thread; the Spectre-backed
/// implementation marshals updates to the live display.
/// </remarks>
public interface IProgressScope : IDisposable
{
    /// <summary>Set the absolute completion percentage in <c>[0, 100]</c>.</summary>
    /// <param name="percent">Absolute percentage (clamped to [0, 100]).</param>
    /// <param name="status">Optional status text (e.g. current file name).</param>
    void Report(double percent, string? status = null);

    /// <summary>Advance the progress by <paramref name="deltaPercent"/> percent.</summary>
    void Increment(double deltaPercent, string? status = null);

    /// <summary>Mark the scope as successfully completed (100%).</summary>
    void Complete(string? status = null);
}
