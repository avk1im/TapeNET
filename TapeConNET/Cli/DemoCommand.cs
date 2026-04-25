using System.CommandLine;

using TapeConNET.Infrastructure;
using TapeConNET.Ux;

namespace TapeConNET.Cli;

/// <summary>
/// <c>tapecon demo</c> — exercises the Phase 2 console infrastructure end to
/// end (logs at every <see cref="WarningLevel"/>, a bounded progress scope,
/// and an optional confirmation prompt). Used to smoke-test
/// <see cref="SpectreConsoleUx"/> on a real terminal before the actual verbs
/// are wired up in Phase 4.
/// </summary>
internal static class DemoCommand
{
    public static Command Create(IConsoleUx ux)
    {
        var cmd = new Command("demo",
            "Demonstrate the tapecon console UX (logs, progress, prompts). Useful for smoke-testing.");

        var fastOption = new Option<bool>("--fast")
        {
            Description = "Run the simulated work in ~1 second instead of ~5 seconds.",
        };
        var promptOption = new Option<bool>("--prompt")
        {
            Description = "Also exercise a Yes/No confirmation prompt.",
        };
        cmd.Options.Add(fastOption);
        cmd.Options.Add(promptOption);

        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var fast = parseResult.GetValue(fastOption);
            var withPrompt = parseResult.GetValue(promptOption);

            ux.WriteBanner();

            ux.Log(WarningLevel.None,      "Plain message (WarningLevel.None).");
            ux.Log(WarningLevel.Info,      "Informational message.");
            ux.Log(WarningLevel.Completed, "Completed-style message.");
            ux.Log(WarningLevel.Warning,   "Warning-style message.");
            ux.Log(WarningLevel.Failed,    "Failed-style message (continuable).");
            ux.Log(WarningLevel.Error,     "Error-style message (fatal).");
            ux.Log(new LogEntry(WarningLevel.None, "Sub-entry (indented, no icon).", IsSub: true));

            var stepDelayMs = fast ? 100 : 500;

            using (var scope = ux.BeginProgress("Simulated tape backup"))
            {
                for (int i = 1; i <= 10; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(stepDelayMs, cancellationToken);
                    scope.Report(i * 10d, $"Simulated tape backup — file {i}/10");
                }
                scope.Complete("Simulated tape backup — done");
            }

            ux.Log(WarningLevel.Completed, "Progress scope finalized.");

            if (withPrompt)
            {
                var ok = ux.Confirm("Did the demo render correctly?", defaultAnswer: true);
                ux.Log(ok ? WarningLevel.Completed : WarningLevel.Warning,
                    ok ? "Glad to hear it." : "Please file an issue.");
            }

            return (int)TapeConExitCode.Ok;
        });

        return cmd;
    }
}
