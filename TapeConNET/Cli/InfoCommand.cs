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
///  <item><c>--drive-only</c> — drive hardware properties only; no media access.</item>
///  <item><c>--media</c>  — drive + media capacity info; no TOC restore.
///   Supersedes <c>--drive-only</c>.</item>
///  <item><c>--full</c>   — drive + media + compact backup-sets table (TOC restored).
///   Supersedes both. This is the default when no flag is supplied.</item>
/// </list>
/// For the full per-file listing, use <c>tapecon list</c>.
/// <para>
/// The depth flag is <c>--drive-only</c>, not <c>--drive</c>: <see cref="GlobalOptions.Attach"/> already gives every
///  drive verb <c>--drive N</c>, the physical drive selector. Two options of one name on one command leave the parser
///  to bind the token to just one of them — <c>info --drive</c> could never mean both "drive 0" and "drive info only".
/// </para>
/// <para>
/// <c>--password-file</c> / <c>--password-env</c> open the encrypted sets the password fits before the table is printed
///  (Design-Encryption §11.2); without them the table shows encrypted sets as <c>[Locked]</c>, and nothing is asked —
///  an overview never prompts. Ignored at <c>--drive-only</c> / <c>--media</c> depth: no sets are shown there.
/// </para>
/// <para>
/// Depth-by-depth behavior for a calibration cartridge:
/// <code>
/// Command            Lifecycle          Identify             Presenter              Result
/// info --drive-only  Drive              none, no media       none                   drive info only
/// info --media       Media              none                 ListContentsAsync §2   detailed calibration
/// info / --full      FullOrCalibration  brief one-liner §1   ListContentsAsync §2   one-liner + details
/// list               FullOrCalibration  brief one-liner §1   ListContentsAsync §2   one-liner + details
/// </code>
/// </para>
/// </remarks>
internal static class InfoCommand
{
    public static Command Create(IConsoleUx ux)
    {
        var cmd = new Command("info",
            "Show drive, media, and backup-sets overview. " +
            "Use --drive-only or --media to limit the output depth.");

        GlobalOptions.Attach(cmd);
        PasswordOptions.Attach(cmd);

        var driveOnlyOption = new Option<bool>("--drive-only")
        {
            Description = "Show drive hardware properties only (no media access). " +
                          "Superseded by --media or --full.",
        };

        var mediaOption = new Option<bool>("--media")
        {
            Description = "Show drive + media info only (no TOC restore). " +
                          "Supersedes --drive-only; superseded by --full.",
        };

        var fullOption = new Option<bool>("--full")
        {
            Description = "Show drive + media + compact backup-sets table (TOC restored). " +
                          "This is the default when no flag is supplied.",
        };

        cmd.Options.Add(driveOnlyOption);
        cmd.Options.Add(mediaOption);
        cmd.Options.Add(fullOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            await Task.Yield();
            ux.WriteBanner();

            var wantDrive = parseResult.GetValue(driveOnlyOption);
            var wantMedia = parseResult.GetValue(mediaOption);
            var wantFull  = parseResult.GetValue(fullOption);

            // Resolved before the drive opens: a bad password source is a usage error, not a tape operation.
            char[]? password = PasswordOptions.Resolve(parseResult);

            // Highest level present wins; default (no flags) → full.
            var (steps, depth) = (wantFull || (!wantDrive && !wantMedia), wantMedia) switch
            {
                (true, _) => (VerbHost.LifecycleSteps.FullOrCalibration, ListDepth.SetsOverview),
                (false, true) => (VerbHost.LifecycleSteps.Media, ListDepth.DriveAndMedia),
                _ => (VerbHost.LifecycleSteps.Drive, ListDepth.Drive),
            };

            using var service = VerbHost.BuildAndOpen(parseResult, ux, steps, ct);
            var result = await service.ListContentsAsync(new ListRequest(Depth: depth) { Password = password });
                // ListContentsAsync() presents a calibration cartridge or the sets overview, and returns `Ok` either way

            return !result.Success
                ? (int)TapeConExitCode.OperationFailed
                : (int)TapeConExitCode.Ok;
        });

        return cmd;
    }
}
