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

    /// <summary>Singleton logger factory shared by all test fixtures.</summary>
    public static ILoggerFactory Default { get; } = LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(MinLevel);
        builder.AddDebug();
    });

    private static LogLevel ResolveMinLevel()
    {
        var env = Environment.GetEnvironmentVariable("TAPENET_TEST_LOG_LEVEL");
        if (!string.IsNullOrWhiteSpace(env) && Enum.TryParse<LogLevel>(env, ignoreCase: true, out var parsed))
            return parsed;
        return LogLevel.Trace;
    }
}
