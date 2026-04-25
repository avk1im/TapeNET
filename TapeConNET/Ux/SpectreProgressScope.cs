using Spectre.Console;

namespace TapeConNET.Ux;

/// <summary>
/// Imperative wrapper around Spectre.Console's callback-based
/// <see cref="Progress"/> API. The scope spins the live progress display on a
/// background thread so callers can keep their normal control flow:
/// <code>
/// using var scope = ux.BeginProgress("Backing up");
/// scope.Report(25, "file1.txt");
/// scope.Complete();
/// </code>
/// </summary>
internal sealed class SpectreProgressScope : IProgressScope
{
    private readonly ProgressTask? _task;
    private readonly ManualResetEventSlim? _done;
    private readonly Task? _runner;
    private readonly bool _quiet;
    private bool _disposed;

    public SpectreProgressScope(IAnsiConsole ansi, string title, bool quiet)
    {
        _quiet = quiet;
        if (quiet)
        {
            // Quiet mode: no live UI; scope becomes a silent sink.
            return;
        }

        var ready = new ManualResetEventSlim(false);
        _done = new ManualResetEventSlim(false);
        ProgressTask? captured = null;

        _runner = Task.Run(() =>
        {
            ansi.Progress()
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new ElapsedTimeColumn(),
                    new SpinnerColumn())
                .Start(ctx =>
                {
                    captured = ctx.AddTask(Markup.Escape(title), maxValue: 100d);
                    ready.Set();
                    while (!_done!.IsSet)
                        Thread.Sleep(50);
                });
        });

        ready.Wait();
        _task = captured;
    }

    public void Report(double percent, string? status = null)
    {
        if (_disposed || _task is null) return;
        _task.Value = Math.Clamp(percent, 0d, 100d);
        if (status is not null)
            _task.Description = Markup.Escape(status);
    }

    public void Increment(double deltaPercent, string? status = null)
    {
        if (_disposed || _task is null) return;
        _task.Increment(deltaPercent);
        if (status is not null)
            _task.Description = Markup.Escape(status);
    }

    public void Complete(string? status = null)
    {
        if (_disposed || _task is null) return;
        _task.Value = 100d;
        if (status is not null)
            _task.Description = Markup.Escape(status);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_quiet) return;

        if (_task is not null && !_task.IsFinished)
            _task.Value = _task.MaxValue;

        _done?.Set();
        try { _runner?.Wait(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }
        _done?.Dispose();
    }
}
