using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// Mutable <see cref="ITestOutputHelper"/> proxy for collection-scoped fixtures.
/// <para>
/// xUnit provides <see cref="ITestOutputHelper"/> per test method, but collection
/// fixtures are created once for the entire collection. This wrapper lets the
/// fixture's <see cref="ILoggerFactory"/> be created at fixture-init time while
/// each test method redirects trace output to its own <see cref="ITestOutputHelper"/>.
/// </para>
/// <para>
/// Thread-safe via <c>volatile</c> — physical tests run sequentially so there is
/// at most one active writer at a time.
/// </para>
/// </summary>
public sealed class RedirectableTestOutput : ITestOutputHelper
{
    private volatile ITestOutputHelper? _current;

    /// <summary>Points the output at the current test's helper (or null to mute).</summary>
    public void SetOutput(ITestOutputHelper? output) => _current = output;

    public void WriteLine(string message)
    {
        try { _current?.WriteLine(message); }
        catch (InvalidOperationException) { /* test already finished */ }
    }

    public void WriteLine(string format, params object[] args)
    {
        try { _current?.WriteLine(format, args); }
        catch (InvalidOperationException) { /* test already finished */ }
    }
}

/// <summary>
/// Minimal <see cref="ILoggerFactory"/> that forwards all log messages to xUnit's
/// <see cref="ITestOutputHelper"/>, allowing TapeNavigator (and other library code)
/// trace output to appear in test results.
/// </summary>
public sealed class XunitLoggerFactory(ITestOutputHelper output, LogLevel minLevel = LogLevel.Trace)
    : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new XunitLogger(output, categoryName, minLevel);
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }

    private sealed class XunitLogger(ITestOutputHelper output, string category, LogLevel minLevel)
        : ILogger
    {
        // Short category: use only the last segment (e.g. "TapeNavigator" from "TapeLibNET.TapeNavigator")
        private readonly string _shortCategory = category.Contains('.')
            ? category[(category.LastIndexOf('.') + 1)..]
            : category;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            string levelTag = logLevel switch
            {
                LogLevel.Trace => "TRC",
                LogLevel.Debug => "DBG",
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                LogLevel.Critical => "CRT",
                _ => "???"
            };

            try
            {
                output.WriteLine($"[{levelTag}] {_shortCategory}: {formatter(state, exception)}");
                if (exception is not null)
                    output.WriteLine($"        {exception}");
            }
            catch (InvalidOperationException)
            {
                // ITestOutputHelper throws if test has already completed — ignore silently
            }
        }
    }
}
