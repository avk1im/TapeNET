using System.CommandLine;

using TapeConNET.Infrastructure;
using TapeConNET.Ux;
using TapeLibNET.Services;

namespace TapeConNET.Cli;

/// <summary>
/// <c>tapecon info</c> — opens the selected drive and prints information at the
/// requested detail level (default: drive + media + compact backup-sets table).
/// </summary>
/// <remarks>
/// Detail-level flags (combinable; the highest level present wins):
/// <list type="bullet">
///  <item><c>--drive</c>  — drive hardware properties only; no media access.</item>
///  <item><c>--media</c>  — drive + media capacity info; no TOC restore.
///   Supersedes <c>--drive</c>.</item>
///  <item><c>--full</c>   — drive + media + compact backup-sets table (TOC restored).
///   Supersedes both. This is the default when no flag is supplied.</item>
/// </list>
/// For the full per-file listing use <c>tapecon list</c>.
/// </remarks>
internal static class InfoCommand
{
    public static Command Create(IConsoleUx ux)
    {
        var cmd = new Command("info",
            "Show drive, media, and backup-sets overview. " +
            "Use --drive or --media to limit the output depth.");
        GlobalOptions.Attach(cmd);

        var driveOption = new Option<bool>("--drive")
        {
            Description = "Show drive hardware properties only (no media access). " +
                          "Superseded by --media or --full.",
        };
        var mediaOption = new Option<bool>("--media")
        {
            Description = "Show drive + media info only (no TOC restore). " +
                          "Supersedes --drive; superseded by --full.",
        };
        var fullOption = new Option<bool>("--full")
        {
            Description = "Show drive + media + compact backup-sets table (TOC restored). " +
                          "This is the default when no flag is supplied.",
        };

        cmd.Options.Add(driveOption);
        cmd.Options.Add(mediaOption);
        cmd.Options.Add(fullOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            await Task.Yield();
            ux.WriteBanner();

            var wantDrive = parseResult.GetValue(driveOption);
            var wantMedia = parseResult.GetValue(mediaOption);
            var wantFull  = parseResult.GetValue(fullOption);

            // Highest level present wins; default (no flags) → full.
            var (steps, depth) = (wantFull || (!wantDrive && !wantMedia), wantMedia) switch
            {
                (true,  _)  => (VerbHost.LifecycleSteps.Full,  ListDepth.SetsOverview),
                (false, true)  => (VerbHost.LifecycleSteps.Media, ListDepth.DriveAndMedia),
                _           => (VerbHost.LifecycleSteps.Drive, ListDepth.Drive),
            };

            using var service = VerbHost.BuildAndOpen(parseResult, ux, steps, ct);
            var result = await service.ListContentsAsync(new ListRequest(Depth: depth));

            return !result.Success
                ? (int)TapeConExitCode.OperationFailed
                : (int)TapeConExitCode.Ok;
        });

        return cmd;
    }
}
