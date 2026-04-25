using System.CommandLine;

using TapeLibNET;

using TapeConNET.Infrastructure;
using TapeConNET.Services;
using TapeConNET.Ux;
using TapeConNET.Filtering;

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
        var filemarksOption = new Option<bool>("--filemarks", "-f")
        {
            Description = "Use filemarks between files (slower seek, more compatible). Default: blob mode.",
        };
        var skipErrorsOption = new Option<bool>("--skip-errors")
        {
            Description = "Skip per-file errors automatically without prompting.",
        };
        var emergencyTocOption = new Option<string?>("--emergency-toc")
        {
            Description = "Folder for the emergency TOC export if writing the TOC to tape fails.",
        };

        cmd.Arguments.Add(filesArg);
        cmd.Options.Add(descOption);
        cmd.Options.Add(subdirsOption);
        cmd.Options.Add(incrementalOption);
        cmd.Options.Add(blockSizeOption);
        cmd.Options.Add(hashOption);
        cmd.Options.Add(appendOption);
        cmd.Options.Add(appendAfterOption);
        cmd.Options.Add(filemarksOption);
        cmd.Options.Add(skipErrorsOption);
        cmd.Options.Add(emergencyTocOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            ux.WriteBanner();

            var files       = parseResult.GetValue(filesArg) ?? [];
            var description = parseResult.GetValue(descOption) ?? $"Backup created {DateTime.Now:yyyy-MM-dd HH:mm}";
            var subdirs     = parseResult.GetValue(subdirsOption);
            var incremental = parseResult.GetValue(incrementalOption);
            var blockSize   = parseResult.GetValue(blockSizeOption);
            var hash        = parseResult.GetValue(hashOption);
            var append      = parseResult.GetValue(appendOption);
            var appendAfter = parseResult.GetValue(appendAfterOption);
            var filemarks   = parseResult.GetValue(filemarksOption);
            var skipErrors  = parseResult.GetValue(skipErrorsOption);
            var emergency   = parseResult.GetValue(emergencyTocOption);
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

            // Append-after implies append
            if (appendAfter.HasValue) append = true;
            // Incremental implies append (cannot be incremental of nothing)
            if (incremental) append = true;

            // Append needs the existing TOC; full backup only needs the media
            var steps = append ? VerbHost.LifecycleSteps.Full : VerbHost.LifecycleSteps.Media;
            using var service = VerbHost.BuildAndOpen(parseResult, ux, steps, ct);

            // If user didn't pick a block size, fall back to the drive's default
            uint effectiveBlockSize = blockSize ?? service.DefaultBlockSize;
            if (effectiveBlockSize == 0) effectiveBlockSize = 64 * 1024;

            var options = new BackupOptions(
                FileList:               [.. resolved.Sources],
                ListContainsPatterns:   true,
                Description:            description,
                IncludeSubdirectories:  subdirs,
                Incremental:            incremental,
                BlockSize:              effectiveBlockSize,
                HashAlgorithm:          hash,
                AppendMode:             append,
                AppendAfterSetIndex:    appendAfter ?? 0,
                UseFilemarks:           filemarks,
                SkipAllErrors:          skipErrors,
                EmergencyTocFolder:     emergency,
                Filter:                 resolved.Filter);

            var result = await service.ExecuteBackupAsync(options);
            return (int)VerbHost.ToExitCode(result.WasAborted, result.HasFailed || result.FilesFailed > 0);
        });

        return cmd;
    }
}
