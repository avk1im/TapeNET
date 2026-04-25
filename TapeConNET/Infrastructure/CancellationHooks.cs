namespace TapeConNET.Infrastructure;

/// <summary>
/// Wires <see cref="Console.CancelKeyPress"/> to a <see cref="CancellationTokenSource"/>
/// implementing the standard "two-strike" Ctrl+C protocol:
/// <list type="bullet">
///   <item>1st Ctrl+C: cooperative cancel — token is signaled, the running
///         operation is expected to wind down gracefully.</item>
///   <item>2nd Ctrl+C: hard exit — process is terminated with
///         <see cref="TapeConExitCode.Cancelled"/>.</item>
/// </list>
/// </summary>
public sealed class CancellationHooks : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private int _strikeCount;

    public CancellationToken Token => _cts.Token;

    public CancellationHooks()
    {
        Console.CancelKeyPress += OnCancelKeyPress;
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        var strike = Interlocked.Increment(ref _strikeCount);
        if (strike == 1)
        {
            // First Ctrl+C: cooperate. Don't let the runtime kill us — let the
            //  current operation observe the token and unwind cleanly.
            e.Cancel = true;
            _cts.Cancel();
        }
        else
        {
            // Second Ctrl+C: bail out hard. Allow the runtime's default handler
            //  to terminate the process so wedged native I/O cannot pin us.
            e.Cancel = false;
            Environment.ExitCode = (int)TapeConExitCode.Cancelled;
        }
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
        _cts.Dispose();
    }
}
