namespace TapeConNET.Infrastructure;

/// <summary>
/// Typed exception raised by tapecon when an operation must abort with a
/// specific <see cref="TapeConExitCode"/>. Caught at the top of
/// <c>Program.Main</c> so that <c>finally</c> blocks (drive dispose, TOC
/// flush) run before the process exits.
/// </summary>
public sealed class TapeConException(
    TapeConExitCode exitCode,
    string message,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public TapeConExitCode ExitCode { get; } = exitCode;
}
