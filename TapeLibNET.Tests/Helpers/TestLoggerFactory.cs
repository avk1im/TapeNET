using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Debug;

namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// Shared <see cref="ILoggerFactory"/> for test runs.
/// <para>
/// Routes all <c>m_logger</c> output from the TapeLibNET / TapeServiceNET
///  classes to the <see cref="DebugLoggerProvider"/>, which writes via
///  <see cref="System.Diagnostics.Debug.WriteLine(string)"/>. When tests are
///  launched in debug mode from VS Test Explorer, the messages show up under
///  the Debug Output window — matching the experience of debug runs of the
///  TapeConNET / TapeWinNET apps (which use the same provider).
/// </para>
/// <para>
/// A singleton instance is exposed via <see cref="Default"/> so all fixtures
///  can share the same factory without per-test allocation.
/// </para>
/// </summary>
internal static class TestLoggerFactory
{
    /// <summary>
    /// Minimum log level captured by the shared factory. Can be overridden via
    ///  the <c>TAPENET_TEST_LOG_LEVEL</c> environment variable (e.g. <c>Debug</c>,
    ///  <c>Information</c>, <c>Warning</c>). Defaults to <see cref="LogLevel.Trace"/>
    ///  to mirror the verbosity of debug app runs.
    /// </summary>
    public static LogLevel MinLevel { get; } = ResolveMinLevel();

    /// <summary>
    ///  Optional file path captured from the <c>TAPENET_TEST_LOG_FILE</c> environment variable.
    ///  When set, log output is additionally appended to this file — handy for capturing
    ///  verbose traces from a failing test outside the IDE Debug window.
    /// </summary>
    public static string? FilePath { get; } = Environment.GetEnvironmentVariable("TAPENET_TEST_LOG_FILE");

    /// <summary>Singleton logger factory shared by all test fixtures.</summary>
    public static ILoggerFactory Default { get; } = LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(MinLevel);
        builder.AddDebug();
        if (!string.IsNullOrWhiteSpace(FilePath))
            builder.AddProvider(new FileLoggerProvider(FilePath!, MinLevel));
    });

    private static LogLevel ResolveMinLevel()
    {
        var env = Environment.GetEnvironmentVariable("TAPENET_TEST_LOG_LEVEL");
        if (!string.IsNullOrWhiteSpace(env) && Enum.TryParse<LogLevel>(env, ignoreCase: true, out var parsed))
            return parsed;
        return LogLevel.Trace;
    }
}

/// <summary>
///  Minimal thread-safe file logger provider used only when
///  <c>TAPENET_TEST_LOG_FILE</c> is set. Appends one line per log entry.
/// </summary>
internal sealed class FileLoggerProvider(string path, LogLevel minLevel) : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer = new(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
    {
        AutoFlush = true
    };

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, minLevel, _gate, _writer);

    public void Dispose()
    {
        lock (_gate) _writer.Dispose();
    }

    private sealed class FileLogger(string category, LogLevel minLevel, object gate, StreamWriter writer) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var msg = formatter(state, exception);
            lock (gate)
            {
                writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{logLevel}] {category}: {msg}");
                if (exception != null) writer.WriteLine(exception);
            }
        }
    }
}
