using System.CommandLine;
using TapeConNET.Cli.Help;
using TapeConNET.Filtering;
using TapeConNET.Infrastructure;
using TapeConNET.Ux;
using TapeLibNET.Compression;
using TapeLibNET.Encryption;
using TapeLibNET.Services;
using TapeLibNET.Toc;

namespace TapeConNET.Cli;

/// <summary>
/// <c>tapecon backup</c> — back up files (and folders, optionally recursively)
/// to the loaded media. Mirrors legacy <c>HandleBackup</c> + the dozen flags
/// that controlled it (block size, hash algorithm, append/incremental,
/// filemarks, error handling).
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the new set goes</b> — as in TapeWin: after the last set by default (<c>--append</c>), after a given set
///  (<c>--append-after</c>), or over the entire media (<c>--overwrite-media</c>), which also writes a new media header
///  and TOC, described by <c>--media-description</c>. Appending keeps the media header and TOC, so it needs a TapeNET
///  media: formatted, or holding sets. New virtual media (<c>--virtual-create</c>) is written over from its start.
/// </para>
/// <para>
/// <c>--overwrite-media</c> is the user's consent to erase, nothing more: it does not answer the service's media
///  identity questions in advance. Those keep guarding the rest of the run — above all the continuation check of a
///  multi-volume backup, which must not take volume 1 of the same series for a fresh cartridge.
/// </para>
/// <para>
/// Help (Design-CliPresentation §4.3): the local <c>--encrypt</c> / <c>--encryption</c> join the shared password options
///  in <i>Encryption</i>. They are attached BEFORE the password options, so the section opens with the switch that
///  turns encryption on, followed by where its password comes from.
/// </para>
/// <para>
/// Descriptions leave choices and defaults to the help where it shows them itself: an enum's choices in the placeholder
///  (<c>--hash</c>), a default in <c>[default: …]</c> (<c>--hash</c>, <c>--compression</c>).
/// </para>
/// </remarks>
internal static class BackupCommand
{
    public static Command Create(IConsoleUx ux)
    {
        var cmd = new Command("backup",
            "Back up files/folders to the loaded media, after the last set on it. " +
            "Each positional argument is a file path, folder path, or wildcard pattern.");

        GlobalOptions.Attach(cmd);
        FilterOptions.Attach(cmd);

        var filesArg = new Argument<string[]>("files")
        {
            Description = "Files, folders, or wildcard patterns to back up. " +
                          "Bare FCL expressions or paths to `.fcl` files are also accepted as filters.",
            Arity = ArgumentArity.OneOrMore,
        };
        var descOption = new Option<string?>("--description", "--desc", "-D")
        {
            Description = "Backup-set description written into the TOC.",
        };
        var subdirsOption = new Option<bool>("--subdirs", "-s")
        {
            Description = "Recurse into subdirectories when expanding folders/patterns.",
        };
        var incrementalOption = new Option<bool>("--incremental", "-i")
        {
            Description = "Create an incremental backup set (only files changed since the last full set).",
        };
        var blockSizeOption = SizeOptions.BlockSize("--block-size",
            "Block size (default: drive's preferred size; clamped to drive limits).", "-b");
        var hashOption = new Option<TapeHashAlgorithm>("--hash", "-H")
        {
            Description = "Per-file hash algorithm.",
            DefaultValueFactory = _ => TapeHashAlgorithm.Crc32,
        };
        var compressionOption = new Option<string>("--compression", "-c")
        {
            Description = "Per-file compression: `off`/`none`, `hardware` | `drive`, `low`, `medium`, `high`, " +
                          "or a ZSTD level `1`-`19`.",
            DefaultValueFactory = _ => CompressionPreset.KeyMedium,
            HelpName = "LEVEL",
        };
        var encryptOption = new Option<bool>("--encrypt", "-e")
        {
            Description = "Encrypt the backup set (software, AES-256-GCM). The password comes from `--password-file` / " +
                          "`--password-env`, else it is asked (twice). An incremental on an encrypted set is encrypted anyway.",
        };
        var encryptionOption = new Option<string?>("--encryption")
        {
            Description = $"Encryption scheme: {SchemeWords.EncryptionChoices}. `hardware` (`drive`) is not supported " +
                          "in this version. Software encryption rules out hardware compression.",
            HelpName = "SCHEME",
        };
        // Verb-local, yet shown with the shared password options — see the remarks
        HelpGroups.Assign(HelpGroups.Encryption, encryptOption, encryptionOption);

        var appendOption = new Option<bool>("--append", "-a")
        {
            Description = "Append the new set after the last set on the media — the default. " +
                          "Excludes `--overwrite-media`.",
        };
        var appendAfterOption = new Option<int?>("--append-after")
        {
            Description = "Append after a specific set; the sets after it are overwritten. " +
                          "Set index follows the dual convention (positive = oldest-up, `0`/negative = latest-down). " +
                          "Excludes `--overwrite-media`.",
            HelpName = "SET",
        };
        var overwriteMediaOption = new Option<bool>("--overwrite-media")
        {
            Description = "Overwrite the ENTIRE media: every set, the TOC and the media header are replaced. Works on " +
                          "blank or foreign media, too. Not the same as `--overwrite-virtual`, which replaces existing " +
                          "virtual media FILES under `--virtual-create`.",
        };
        // No --name alias here: a backup names both the set and the media (MediaOptions)
        var mediaDescOption = MediaOptions.Description(
            "Description of the new media, with `--overwrite-media` or `--virtual-create` " +
            "(default: 'Media created <date time>'). Ignored otherwise.",
            withNameAliases: false);
        var skipErrorsOption = new Option<bool>("--skip-errors")
        {
            Description = "Skip per-file errors automatically without prompting.",
        };
        var noMultivolumeOption = new Option<bool>("--no-multivolume")
        {
            Description = "Stop the backup when the tape is full instead of continuing to a new volume.",
        };
        var ejectWhenDoneOption = new Option<bool>("--eject-when-done")
        {
            Description = "Eject the media automatically when the backup is complete.",
        };
        var emergencyTocOption = new Option<string?>("--emergency-toc")
        {
            Description = "Folder for the emergency TOC export if writing the TOC to tape fails.",
            HelpName = "PATH",
        };
        var skipSetCorrectionOption = new Option<bool>("--skip-set-correction")
        {
            Description = "Do not attempt to correct backup set navigation errors.",
        };
        var skipSetVerifyOption = new Option<bool>("--skip-set-verify")
        {
            Description = "Skip backup set verification before overwriting (repair mode). " +
                          "Not a performance option: an append at end-of-data performs no verification at all, " +
                          "so there is no time to save; use ONLY to repair media whose set markers are damaged.",
        };

        cmd.Arguments.Add(filesArg);
        cmd.Options.Add(descOption);
        cmd.Options.Add(subdirsOption);
        cmd.Options.Add(incrementalOption);
        cmd.Options.Add(blockSizeOption);
        cmd.Options.Add(hashOption);
        cmd.Options.Add(compressionOption);
        cmd.Options.Add(encryptOption);
        cmd.Options.Add(encryptionOption);
        PasswordOptions.Attach(cmd);
        cmd.Options.Add(appendOption);
        cmd.Options.Add(appendAfterOption);
        cmd.Options.Add(overwriteMediaOption);
        cmd.Options.Add(mediaDescOption);
        cmd.Options.Add(skipErrorsOption);
        cmd.Options.Add(noMultivolumeOption);
        cmd.Options.Add(ejectWhenDoneOption);
        cmd.Options.Add(emergencyTocOption);
        cmd.Options.Add(skipSetCorrectionOption);
        cmd.Options.Add(skipSetVerifyOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            ux.WriteBanner();

            var files       = parseResult.GetValue(filesArg) ?? [];
            var description = parseResult.GetValue(descOption) ?? $"Backup created {DateTime.Now:yyyy-MM-dd HH:mm}";
            var subdirs     = parseResult.GetValue(subdirsOption);
            var incremental = parseResult.GetValue(incrementalOption);
            var blockSize   = parseResult.GetValue(blockSizeOption);
            var hash        = parseResult.GetValue(hashOption);
            var compressionSpec = parseResult.GetValue(compressionOption);
            var appendGiven = parseResult.GetValue(appendOption);
            var appendAfter = parseResult.GetValue(appendAfterOption);
            var overwriteMedia = parseResult.GetValue(overwriteMediaOption);
            var mediaDesc   = parseResult.GetValue(mediaDescOption);
            var skipErrors  = parseResult.GetValue(skipErrorsOption);
            var noMultivolume = parseResult.GetValue(noMultivolumeOption);
            var ejectWhenDone = parseResult.GetValue(ejectWhenDoneOption);
            var emergency   = parseResult.GetValue(emergencyTocOption);
            var skipSetCorrection = parseResult.GetValue(skipSetCorrectionOption);
            var skipSetVerify = parseResult.GetValue(skipSetVerifyOption);
            var filterFcl   = parseResult.GetValue(FilterOptions.Filter);
            var filterFile  = parseResult.GetValue(FilterOptions.FilterFile);

            // Auto-classify the positional arguments. Path/dir/wildcard items
            //  become backup sources; FCL files or inline FCL expressions
            //  (and the explicit --filter / --filter-file) become the
            //  selection filter applied during backup.
            var resolved = FilterResolver.ResolveForBackup(files, filterFcl, filterFile);
            if (resolved.Sources.Count == 0)
                throw new TapeConException(TapeConExitCode.UsageError,
                    "No backup sources specified — at least one path, folder or wildcard pattern is required.");

            if (!CompressionPreset.TryParse(SchemeWords.NormalizeCompression(compressionSpec),
                    out var compression, out var compressionLevel, out var compressionError))
                throw new TapeConException(TapeConExitCode.UsageError, compressionError);

            // Design-Encryption §11.2 — the scheme, before the drive opens: a refusal costs no tape motion.
            TapeEncryption encryption = parseResult.GetValue(encryptOption) ? TapeEncryption.Software : TapeEncryption.None;
            if (parseResult.GetValue(encryptionOption) is { } word)
            {
                // The choices carry help mention markers — an error message is no help text: plain
                if (!SchemeWords.TryParseEncryption(word, out var named))
                    throw new TapeConException(TapeConExitCode.UsageError,
                        $"Unknown encryption >{word}<. Choices: {HelpRenderer.PlainText(SchemeWords.EncryptionChoices)}.");
                if (encryption != TapeEncryption.None && named != encryption)
                    throw new TapeConException(TapeConExitCode.UsageError, "--encrypt and --encryption disagree.");
                encryption = named;
            }
            if (encryption == TapeEncryption.Hardware)
                throw new TapeConException(TapeConExitCode.UsageError,
                    "Hardware (drive) encryption is not supported in this version — use --encrypt (software).");
            if (encryption != TapeEncryption.None && compression == TapeCompression.Hardware)
                throw new TapeConException(TapeConExitCode.UsageError,
                    "Software encryption rules out hardware compression — encrypted data does not compress. " +
                    "Use software compression (low / medium / high), or none.");

            // ── Where the new set goes (see the remarks) ──
            if (overwriteMedia && (appendGiven || appendAfter.HasValue || incremental))
                throw new TapeConException(TapeConExitCode.UsageError,
                    "--overwrite-media starts the media over — it excludes --append, --append-after and --incremental.");

            bool creating = VerbHost.IsCreatingVirtualMedia(parseResult);
            if (creating && (appendAfter.HasValue || incremental))
                throw new TapeConException(TapeConExitCode.UsageError,
                    "--append-after and --incremental need existing sets — not available with " +
                    "--virtual-create, which starts blank media.");

            // New virtual media is written over from its start, as an overwrite: same result, and no TOC to read first
            bool append = !overwriteMedia && !creating;

            if (mediaDesc is not null && append)
                ux.Log(WarningLevel.Warning,
                    "--media-description ignored: it describes NEW media (--overwrite-media, --virtual-create). " +
                    "To rename the media, use 'tapecon rename-media'.");

            char[]? password = PasswordOptions.Resolve(parseResult);

            // Append needs the existing TOC; an overwrite only needs the media
            using var service = append
                ? VerbHost.BuildAndOpen(parseResult, ux, VerbHost.LifecycleSteps.Full, ct,
                    tocHint: "A backup appends to TapeNET media. Format it first ('tapecon format'), " +
                             "or overwrite it with --overwrite-media")
                : VerbHost.BuildAndOpen(parseResult, ux, VerbHost.LifecycleSteps.Media, ct,
                    mediaName: mediaDesc);

            // If user didn't pick a block size, fall back to the drive's max (LTO) or default size
            uint effectiveBlockSize = blockSize ?? (service.IsLtoDrive ? service.MaximumBlockSize : service.DefaultBlockSize);
            if (effectiveBlockSize == 0) effectiveBlockSize = 64 * 1024;

            var options = new BackupRequest(
                FileList:               [.. resolved.Sources],
                ListContainsPatterns:   true,
                Description:            description,
                IncludeSubdirectories:  subdirs,
                Incremental:            incremental,
                BlockSize:              effectiveBlockSize,
                HashAlgorithm:          hash,
                AppendMode:             append,
                AppendAfterSetIndex:    appendAfter ?? 0,
                SkipAllErrors:          skipErrors,
                EjectWhenDone:          ejectWhenDone,
                EmergencyTocFolder:     emergency,
                Filter:                 resolved.Filter,
                MediaName:              append ? null : mediaDesc,
                Compression:            compression,
                CompressionLevel:       compressionLevel)
            {
                NoMultivolume = noMultivolume,
                CorrectSetNavigation = !skipSetCorrection,
                VerifySetHeader = !skipSetVerify,
                Encryption = encryption,
                Password = password,        // also opens an encrypted base of an incremental; cleared by the service
            };

            var result = await service.ExecuteBackupAsync(options);
            return (int)VerbHost.ToExitCode(result.WasAborted, result.HasFailed || result.FilesFailed > 0,
                result.SetStats.SetWriteBlocked, passwordRequired: result.ErrorCode == VerbHost.ErrorInvalidPassword);
        });

        return cmd;
    }
}
