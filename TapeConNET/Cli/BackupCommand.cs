using System.CommandLine;
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
internal static class BackupCommand
{
    public static Command Create(IConsoleUx ux)
    {
        var cmd = new Command("backup",
            "Back up files/folders to the loaded media. " +
            "Each positional argument is a file path, folder path, or wildcard pattern.");
        GlobalOptions.Attach(cmd);
        FilterOptions.Attach(cmd);
        PasswordOptions.Attach(cmd);

        var filesArg = new Argument<string[]>("files")
        {
            Description = "Files, folders, or wildcard patterns to back up. " +
                          "Bare FCL expressions or paths to .fcl files are also accepted as filters.",
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
            Description = "Create an incremental backup set (only changed files since last full set).",
        };
        var blockSizeOption = new Option<uint?>("--block-size", "-b")
        {
            Description = "Block size in bytes (default: drive's preferred size; clamped to drive limits).",
        };
        var hashOption = new Option<TapeHashAlgorithm>("--hash", "-H")
        {
            Description = "Per-file hash algorithm. Choices: None, Crc32, Crc64, XxHash32, XxHash3, XxHash64, XxHash128.",
            DefaultValueFactory = _ => TapeHashAlgorithm.Crc32,
        };
        var compressionOption = new Option<string>("--compression", "-c")
        {
            Description = "Per-file compression. Choices: off/none, hardware | drive, low, medium (default), high, or 1-19 (ZSTD level).",
            DefaultValueFactory = _ => CompressionPreset.KeyMedium,
        };
        var encryptOption = new Option<bool>("--encrypt", "-e")
        {
            Description = "Encrypt the backup set (software, AES-256-GCM). The password comes from --password-file / " +
                  "--password-env, else it is asked (twice). An incremental on an encrypted set is encrypted anyway.",
        };
        var encryptionOption = new Option<string?>("--encryption")
        {
            Description = $"Encryption scheme: {SchemeWords.EncryptionChoices}. 'hardware' (drive) is not supported " +
                          "in this version. Software encryption rules out hardware compression.",
        };
        var appendOption = new Option<bool>("--append", "-a")
        {
            Description = "Append a new set to existing media (default: replace all).",
        };
        var appendAfterOption = new Option<int?>("--append-after")
        {
            Description = "Append after a specific set (replaces all sets after the given index). " +
                          "Set index follows the dual convention (positive=oldest-up, 0/negative=latest-down). " +
                          "Implies --append.",
        };
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
        };
        var overwriteMediaOption = new Option<bool>("--overwrite-media")
        {
            Description = "Overwrite media carrying data without prompt.",
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
        cmd.Options.Add(appendOption);
        cmd.Options.Add(appendAfterOption);
        cmd.Options.Add(skipErrorsOption);
        cmd.Options.Add(noMultivolumeOption);
        cmd.Options.Add(ejectWhenDoneOption);
        cmd.Options.Add(emergencyTocOption);
        cmd.Options.Add(overwriteMediaOption);
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
            var append      = parseResult.GetValue(appendOption);
            var appendAfter = parseResult.GetValue(appendAfterOption);
            var skipErrors  = parseResult.GetValue(skipErrorsOption);
            var noMultivolume = parseResult.GetValue(noMultivolumeOption);
            var ejectWhenDone = parseResult.GetValue(ejectWhenDoneOption);
            var emergency   = parseResult.GetValue(emergencyTocOption);
            var overwriteMedia = parseResult.GetValue(overwriteMediaOption);
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
                if (!SchemeWords.TryParseEncryption(word, out var named))
                    throw new TapeConException(TapeConExitCode.UsageError,
                        $"Unknown encryption >{word}<. Choices: {SchemeWords.EncryptionChoices}.");
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
            char[]? password = PasswordOptions.Resolve(parseResult);

            // Append-after implies append
            if (appendAfter.HasValue) append = true;
            // Incremental implies append (cannot be incremental of nothing)
            if (incremental) append = true;

            // Append needs the existing TOC; full backup only needs the media
            var steps = append ? VerbHost.LifecycleSteps.Full : VerbHost.LifecycleSteps.Media;
            using var service = VerbHost.BuildAndOpen(parseResult, ux, steps, ct);

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
                Compression:            compression,
                CompressionLevel:       compressionLevel)
            {
                NoMultivolume = noMultivolume,
                ProceedOnMediaMismatch = overwriteMedia,
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
