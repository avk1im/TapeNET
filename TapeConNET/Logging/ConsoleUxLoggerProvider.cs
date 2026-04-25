using Microsoft.Extensions.Logging;

using TapeConNET.Ux;

namespace TapeConNET.Logging;

/// <summary>
/// <see cref="ILoggerProvider"/> that forwards every emitted log entry to an
/// <see cref="IConsoleUx"/> instance. Used by
/// <see cref="LoggerFactoryBuilder.Build"/> to surface
/// <c>TapeLibNET</c> diagnostics in the console when the user opts in via
/// <c>--log-level</c>.
/// </summary>
internal sealed class ConsoleUxLoggerProvider(IConsoleUx ux, LogLevel minLevel) : ILoggerProvider
{
    private readonly IConsoleUx _ux = ux ?? throw new ArgumentNullException(nameof(ux));
    private readonly LogLevel _minLevel = minLevel;

    public ILogger CreateLogger(string categoryName) => new ConsoleUxLogger(_ux, categoryName, _minLevel);

    public void Dispose() { /* nothing to release */ }

    private sealed class ConsoleUxLogger(IConsoleUx ux, string category, LogLevel minLevel) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
            => logLevel != LogLevel.None && logLevel >= minLevel;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var warn = logLevel switch
            {
                LogLevel.Trace or LogLevel.Debug => WarningLevel.None,
                LogLevel.Information             => WarningLevel.Info,
                LogLevel.Warning                 => WarningLevel.Warning,
                LogLevel.Error                   => WarningLevel.Failed,
                LogLevel.Critical                => WarningLevel.Error,
                _                                => WarningLevel.None,
            };

            var msg = formatter(state, exception);
            if (exception is not null)
                msg = $"{msg} — {exception.Message}";

            // Short category tail: keep the last segment of the dotted name.
            var dot = category.LastIndexOf('.');
            var tag = dot >= 0 ? category[(dot + 1)..] : category;

            ux.Log(warn, $"[{tag}] {msg}");
        }
    }
}
