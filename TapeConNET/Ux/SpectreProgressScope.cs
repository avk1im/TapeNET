using Spectre.Console;

namespace TapeConNET.Ux;

/// <summary>
/// Imperative wrapper around Spectre.Console's callback-based <see cref="Progress"/> API. The scope spins the live
///  progress display on a background thread so callers can keep their normal control flow:
/// <code>
/// using var scope = ux.BeginProgress("Backing up");
/// scope.Report(25, "file1.txt");
/// scope.Complete();
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// <b>Suspendable.</b> Spectre's live displays and its prompts must never run at the same time: the display redraws its
///  region on every refresh and overwrites the prompt's lines, so a prompt's first line repeats until the user answers.
///  <see cref="Suspend"/> ends the live display and returns a token. Disposing the token starts a new one with the same
///  value and status. <see cref="SpectreConsoleUx"/> suspends around every prompt.
/// </para>
/// <para>
/// Value and status live in the scope, not in the Spectre task, so they survive a suspension and reports made while
///  suspended are not lost.
/// </para>
/// </remarks>
internal sealed class SpectreProgressScope : IProgressScope
{
    private readonly IAnsiConsole _ansi;
    private readonly string _title;
    private readonly bool _quiet;
    private readonly Action<SpectreProgressScope>? _onDisposed;
    private readonly object _gate = new();

    // The running live display; all null while suspended (or quiet)
    private ProgressTask? _task;
    private ManualResetEventSlim? _done;
    private Task? _runner;

    // The state, kept across suspensions
    private double _value;
    private string? _status;
    private int _suspensions;
    private bool _disposed;

    public SpectreProgressScope(IAnsiConsole ansi, string title, bool quiet, Action<SpectreProgressScope>? onDisposed = null)
    {
        _ansi = ansi;
        _title = title;
        _quiet = quiet;
        _onDisposed = onDisposed;

        // Quiet mode: no live UI; the scope is a silent sink
        if (!quiet)
            Start();
    }

    public void Report(double percent, string? status = null)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _value = Math.Clamp(percent, 0d, 100d);
            if (status is not null)
                _status = status;
            Apply();
        }
    }

    public void Increment(double deltaPercent, string? status = null)
        => Report(_value + deltaPercent, status);

    public void Complete(string? status = null) => Report(100d, status);

    /// <summary>
    /// Ends the live display until the returned token is disposed: for a prompt, which must not share the console with
    ///  a live display. Nests: the display resumes when the last token is disposed. A no-op token when quiet or disposed.
    /// </summary>
    public IDisposable Suspend()
    {
        lock (_gate)
        {
            if (_quiet || _disposed)
                return NoopToken.Instance;
            if (_suspensions++ == 0)
                Stop();
        }
        return new ResumeToken(this);
    }

    private void Resume()
    {
        lock (_gate)
        {
            if (--_suspensions == 0 && !_disposed)
                Start();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_task is not null && !_task.IsFinished)
                _task.Value = _task.MaxValue;
            Stop();
        }
        _onDisposed?.Invoke(this);
    }

    #region *** Live display ***

    /// <summary>Starts a live display showing the current value and status. Caller holds <see cref="_gate"/>.</summary>
    private void Start()
    {
        var ready = new ManualResetEventSlim(false);
        var done = new ManualResetEventSlim(false);
        ProgressTask? captured = null;

        _runner = Task.Run(() =>
        {
            _ansi.Progress()
                // Cleared when it ends: a suspended display leaves no stale bar above the prompt
                .AutoClear(true)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new ElapsedTimeColumn(),
                    new SpinnerColumn())
                .Start(ctx =>
                {
                    captured = ctx.AddTask(Markup.Escape(_title), maxValue: 100d);
                    ready.Set();
                    while (!done.IsSet)
                        Thread.Sleep(50);
                });
        });

        ready.Wait();
        _task = captured;
        _done = done;
        Apply();
    }

    /// <summary>Ends the live display and waits until it has released the console. Caller holds <see cref="_gate"/>.</summary>
    private void Stop()
    {
        _done?.Set();
        try { _runner?.Wait(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }
        _done?.Dispose();
        _done = null;
        _runner = null;
        _task = null;
    }

    /// <summary>Pushes the current value and status into the running display, if any. Caller holds <see cref="_gate"/>.</summary>
    private void Apply()
    {
        if (_task is null) return;
        _task.Value = _value;
        if (_status is not null)
            _task.Description = Markup.Escape(_status);
    }

    #endregion

    private sealed class ResumeToken(SpectreProgressScope scope) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                scope.Resume();
        }
    }

    private sealed class NoopToken : IDisposable
    {
        public static readonly NoopToken Instance = new();
        public void Dispose() { }
    }
}
