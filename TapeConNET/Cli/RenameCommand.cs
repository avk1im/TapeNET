using System.CommandLine;
using TapeConNET.Infrastructure;
using TapeConNET.Ux;

namespace TapeConNET.Cli;

/// <summary>
/// <c>tapecon rename-media</c>   — renames the loaded tape media.
/// <c>tapecon rename-set INDEX</c> — renames the backup set at <c>INDEX</c>.
/// </summary>
/// <remarks>
/// <para>
/// Scriptable: given the new text (<c>--media-description</c> / <c>--description</c>, both alias <c>--name</c>), neither
///  verb asks.
/// </para>
/// <para>
/// <c>rename-media</c> also SAVES the TOC — both copies, where the layout keeps them. That makes it the repair verb for a
///  TOC that is damaged or was found only by searching: with <c>--import-toc FILE</c> and no new name, it writes the
///  imported TOC back under its current name. A later volume's TOC is read-only: refused by the service.
/// </para>
/// </remarks>
internal static class RenameCommand
{
    public static Command CreateRenameMedia(IConsoleUx ux)
    {
        var cmd = new Command("rename-media",
            "Rename the loaded media, and save its TOC. With `--import-toc` and no new name: write the imported TOC " +
            "to the media under its current name.");

        GlobalOptions.Attach(cmd);
        TocOptions.Attach(cmd);

        var mediaDescOption = MediaOptions.Description("The new media description. Absent: asked — " +
            "or, with `--import-toc`, kept.");
        cmd.Options.Add(mediaDescOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            ux.WriteBanner();

            string? mediaDesc = parseResult.GetValue(mediaDescOption);
            var importToc = parseResult.GetValue(TocOptions.ImportToc);

            using var service = VerbHost.BuildAndOpen(parseResult, ux, VerbHost.LifecycleSteps.Full, ct,
                tocHint: VerbHost.TocRecoveryHint, importToc: importToc);

            // An imported TOC is re-saved as it is, unless a new name is given
            if (mediaDesc is null && importToc is not null)
                mediaDesc = service.TOC!.Description ?? string.Empty;

            var ok = mediaDesc is null
                ? await service.RenameMediaAsync()
                : await service.RenameMediaAsync(mediaDesc);
            return ok ? (int)TapeConExitCode.Ok : (int)TapeConExitCode.OperationFailed;
        });

        return cmd;
    }

    public static Command CreateRenameSet(IConsoleUx ux)
    {
        var cmd = new Command("rename-set",
            "Rename a backup set on the loaded tape, and/or change the password of an encrypted set.");

        GlobalOptions.Attach(cmd);
        PasswordOptions.Attach(cmd, withNew: true);

        var indexArg = new Argument<int>("index")
        {
            Description = "Standard (1-based) set index to rename.",
        };
        // The same name as backup's set description; --name, natural for a rename, as an alias
        var descOption = new Option<string?>("--description", "--desc", "-D", "--name", "-n")
        {
            Description = "The new set description. Absent: asked (unless only the password changes).",
            HelpName = "TEXT",
        };
        var changePasswordOption = new Option<bool>("--change-password")
        {
            Description = "Change the password of an encrypted set — asked twice, masked. Unattended: use " +
                          "`--new-password-file` or `--new-password-env`. Older TOC exports and other volumes' TOC " +
                          "copies keep the old password.",
        };

        cmd.Arguments.Add(indexArg);
        cmd.Options.Add(descOption);
        cmd.Options.Add(changePasswordOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            ux.WriteBanner();

            int setIndex = parseResult.GetValue(indexArg);
            string? name = parseResult.GetValue(descOption);
            bool changePassword = parseResult.GetValue(changePasswordOption);
            char[]? password = PasswordOptions.Resolve(parseResult);
            char[]? newPassword = PasswordOptions.ResolveNew(parseResult);

            if (changePassword && newPassword is not null)
                throw new TapeConException(TapeConExitCode.UsageError,
                    "--change-password asks for the new password; it cannot be combined with --new-password-file/-env.");

            using var service = VerbHost.BuildAndOpen(parseResult, ux, VerbHost.LifecycleSteps.Full, ct);

            if (setIndex < 1 || setIndex > service.TOC!.Count)
                throw new TapeConException(TapeConExitCode.UsageError, $"No backup set #{setIndex}.");

            if (changePassword)
            {
                // Open the set first (the request's password, else asked): its name is hidden while locked.
                if (service.TOC[setIndex].IsLocked && !await service.UnlockSetsAsync([setIndex], password))
                    return (int)TapeConExitCode.PasswordRequired;
                password = null;                                        // cleared by UnlockSetsAsync
                newPassword = PasswordOptions.AskNew(ux, service.TOC[setIndex].DisplayName);
                if (newPassword is null)
                    return (int)TapeConExitCode.Cancelled;
            }

            bool ok;
            if (name is null && newPassword is null)
            {
                // The classic path: ask for the name. A locked set is opened first — by the request's password, or asked.
                if (password is not null && !await service.UnlockSetsAsync([setIndex], password))
                    return (int)TapeConExitCode.PasswordRequired;
                ok = await service.RenameBackupSetAsync(setIndex);
            }
            else
                ok = await service.RenameBackupSetAsync(setIndex, name, password, newPassword);

            if (ok)
                return (int)TapeConExitCode.Ok;
            // Note: RenameBackupSetAsync(int) returns false when the user keeps the name
            //  — the exit then stays OperationFailed
            return service.TOC[setIndex].IsLocked ? (int)TapeConExitCode.PasswordRequired
                                                 : (int)TapeConExitCode.OperationFailed;
        });

        return cmd;
    }
}
