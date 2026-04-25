using System.Diagnostics;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using TapeConNET.Ux;

namespace TapeConNET.Logging;

/// <summary>
/// Builds the <see cref="ILoggerFactory"/> used by <c>TapeService</c> and
/// <c>TapeLibNET</c>. Honors the <c>--log-level</c> flag and bridges
/// every emitted <see cref="LogLevel"/> entry into the supplied
/// <see cref="IConsoleUx"/> via the <see cref="ConsoleUxLoggerProvider"/>.
/// </summary>
/// <remarks>
/// Mirrors the legacy 1.x setup:
/// <list type="bullet">
///   <item><b>Debug build</b>: <see cref="DebugLoggerFactoryExtensions.AddDebug"/> at <see cref="LogLevel.Trace"/>.</item>
///   <item><b>Release build</b>: Debug provider only when a debugger is attached.</item>
/// </list>
/// On top of that, when the user passes <c>--log-level</c> the entries are
/// also surfaced in the console via <see cref="IConsoleUx"/>.
/// </remarks>
public static class LoggerFactoryBuilder
{
    /// <summary>
    /// Build a logger factory honoring the supplied minimum
    /// <paramref name="consoleLevel"/> for console output.
    /// </summary>
    /// <param name="ux">Console UX sink. Required.</param>
    /// <param name="consoleLevel">
    /// Minimum severity surfaced to the console. <see cref="LogLevel.None"/>
    /// disables the console bridge entirely (the default).
    /// </param>
    public static ILoggerFactory Build(IConsoleUx ux, LogLevel consoleLevel = LogLevel.None)
    {
        ArgumentNullException.ThrowIfNull(ux);

#if DEBUG
        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace).AddDebug();
            if (consoleLevel != LogLevel.None)
                builder.AddProvider(new ConsoleUxLoggerProvider(ux, consoleLevel));
        });
#else
        if (consoleLevel == LogLevel.None && !Debugger.IsAttached)
            return NullLoggerFactory.Instance;

        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            if (Debugger.IsAttached)
                builder.AddDebug();
            if (consoleLevel != LogLevel.None)
                builder.AddProvider(new ConsoleUxLoggerProvider(ux, consoleLevel));
        });
#endif
    }
}
