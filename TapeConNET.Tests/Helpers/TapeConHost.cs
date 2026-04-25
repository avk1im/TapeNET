using System.CommandLine;

using TapeConNET.Cli;
using TapeConNET.Infrastructure;
using TapeConNET.Ux;

namespace TapeConNET.Tests.Helpers;

/// <summary>
/// End-to-end test runner: invokes the full <see cref="RootCommandFactory"/>
/// verb tree against a capturing <see cref="SilentConsoleUx"/> and returns
/// the exit code together with all log entries produced by the verb action.
/// </summary>
/// <remarks>
/// Mirrors what <c>Program.Main</c> does but stays in-process so the test
/// can assert on captured <see cref="LogEntry"/> values rather than parsing
/// stdout. <see cref="TapeConException"/> / <see cref="OperationCanceledException"/>
/// are translated to exit codes the same way <c>Program</c> does.
/// </remarks>
internal static class TapeConHost
{
    public sealed record RunResult(int ExitCode, IReadOnlyList<LogEntry> Entries, string Output)
    {
        public TapeConExitCode Exit => (TapeConExitCode)ExitCode;

        public bool HasError => Entries.Any(e =>
            e.Level is WarningLevel.Error or WarningLevel.Failed);

        public IEnumerable<LogEntry> AtLevel(WarningLevel level) =>
            Entries.Where(e => e.Level == level);

        public bool ContainsMessage(string fragment, StringComparison cmp = StringComparison.OrdinalIgnoreCase) =>
            Entries.Any(e => e.Message.Contains(fragment, cmp));
    }

    /// <summary>
    /// Runs <c>tapecon &lt;args&gt;</c> in-process. Always uses
    /// <see cref="SilentConsoleUx"/> with <see cref="StringWriter"/> capture so
    /// test output is fully assertable and the test runner stays clean.
    /// </summary>
    public static async Task<RunResult> RunAsync(
        params string[] args)
    {
        return await RunAsync(args, CancellationToken.None);
    }

    public static async Task<RunResult> RunAsync(
        string[] args, CancellationToken ct)
    {
        var output = new StringWriter();
        var ux = new SilentConsoleUx(output);

        var root = RootCommandFactory.Create(ux);
        // Disable System.CommandLine's built-in exception handler so the
        //  test runner can observe TapeConException directly and translate
        //  it to the proper TapeConExitCode (matching Program.Main).
        var invocationConfig = new System.CommandLine.InvocationConfiguration
        {
            EnableDefaultExceptionHandler = false,
        };
        int exitCode;
        try
        {
            var parseResult = root.Parse(args);
            exitCode = await parseResult.InvokeAsync(invocationConfig, ct);
        }
        catch (TapeConException tcex)
        {
            ux.Log(WarningLevel.Error, tcex.Message);
            exitCode = (int)tcex.ExitCode;
        }
        catch (OperationCanceledException)
        {
            exitCode = (int)TapeConExitCode.Cancelled;
        }
        catch (Exception ex)
        {
            ux.Log(WarningLevel.Error, $"fatal: {ex.Message}");
            exitCode = (int)TapeConExitCode.FatalError;
        }

        return new RunResult(exitCode, [.. ux.Entries], output.ToString());
    }
}
