// TapeConNET 2.0 — entry point.
//  Builds the System.CommandLine verb tree, hooks Ctrl+C cancellation, runs
//  the parsed command, and translates any TapeConException into the
//  appropriate TapeConExitCode for the OS.

using System.CommandLine;
using TapeConNET.Cli;
using TapeConNET.Infrastructure;
using TapeConNET.Ux;

try
{
    // Ensure Spectre's box-drawing / icon glyphs render correctly on cmd.exe and
    //  legacy code-page consoles. Safe no-op on a UTF-8 console.
    System.Console.OutputEncoding = System.Text.Encoding.UTF8;

    using var cancellation = new CancellationHooks();
    var ux = new SpectreConsoleUx();

    // Pre-scan args for --quiet / --no-color so the banner and any startup
    //  diagnostics already honor them. System.CommandLine will also bind these
    //  once the parsed command runs.
    foreach (var a in args)
    {
        if (a is "--quiet" or "-q") ux.QuietMode = true;
        else if (a is "--no-color") ux.NoColor = true;
    }

    var root = RootCommandFactory.Create(ux);

    // System.CommandLine's DEFAULT exception handler must stay off: it catches every exception a verb action throws —
    //  TapeConException included — prints it as "Unhandled exception" with a stack trace and returns 1, so the catch
    //  blocks below never ran and the exit codes were lost. TapeConHost (tests) has always switched it off, which is
    //  why no test saw the crash.
    var invocation = new InvocationConfiguration { EnableDefaultExceptionHandler = false };

    // System.CommandLine 2.0: ParseResult.InvokeAsync returns the int exit code
    //  produced by the matched command's SetAction handler.
    var parseResult = root.Parse(args);
    return await parseResult.InvokeAsync(invocation, cancellation.Token);
}
catch (TapeConException tcex)
{
    System.Console.Error.WriteLine($"tapecon: {tcex.Message}");
    return (int)tcex.ExitCode;
}
catch (OperationCanceledException)
{
    System.Console.Error.WriteLine("tapecon: cancelled.");
    return (int)TapeConExitCode.Cancelled;
}
catch (Exception ex)
{
    System.Console.Error.WriteLine($"tapecon: fatal: {ex.Message}");
#if DEBUG
    System.Console.Error.WriteLine(ex);
#endif
    return (int)TapeConExitCode.FatalError;
}
