using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TapeLibNET;
using Xunit.Abstractions;

namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// Environment variable names for physical tape test configuration.
/// </summary>
public static class PhysicalTestEnv
{
    /// <summary>
    /// Comma-separated list of drive numbers to test (e.g., "0,1,2").
    /// If not set, the fixture probes drives 0–9 and uses the first one found.
    /// </summary>
    public const string DriveNumbers = "TAPELIBNET_PHYSICAL_DRIVES";

    /// <summary>
    /// Operation timeout in seconds for physical tape operations.
    /// Defaults to <see cref="PhysicalTapeFixture.DefaultTimeoutSeconds"/> if not set.
    /// </summary>
    public const string TimeoutSeconds = "TAPELIBNET_PHYSICAL_TIMEOUT";

    /// <summary>
    /// When set to any non-empty value (e.g. "1"), forces formatting <em>without</em>
    /// an initiator partition even on drives that support one. This exercises the
    /// <c>TapeNavigatorTOCInSet</c> code path on partition-capable hardware.
    /// </summary>
    public const string ForceNoPartition = "TAPELIBNET_PHYSICAL_NO_PARTITION";

    /// <summary>
    /// Maximum number of drives to probe when <see cref="DriveNumbers"/> is not set.
    /// </summary>
    public const int MaxProbeNumber = 9;
}

/// <summary>
/// Reusable test fixture that manages a physical tape drive lifecycle for
/// integration testing against real Win32 hardware.
/// <para>
/// Analogous to <see cref="VirtualTapeFixture"/> but backed by
/// <see cref="TapeDriveWin32Backend"/> and a real tape in the drive.
/// </para>
/// <para>
/// Each physical drive gets its own fixture instance, shared via xUnit's
/// <c>[Collection]</c> mechanism so tests within a drive run sequentially.
/// The fixture formats the tape once at construction and provides recovery
/// helpers for resilience when individual tests fail.
/// </para>
/// </summary>
public sealed class PhysicalTapeFixture : IDisposable
{
    #region *** Constants ***

    /// <summary>Default operation timeout in seconds (5 minutes).</summary>
    public const int DefaultTimeoutSeconds = 300;

    #endregion

    #region *** Properties ***

    /// <summary>The TapeDrive wrapping the Win32 backend.</summary>
    public TapeDrive Drive { get; }

    /// <summary>Current TOC for the test session.</summary>
    public TapeTOC TOC { get; private set; }

    /// <summary>Logger factory for test output.</summary>
    public ILoggerFactory LoggerFactory { get; }

    /// <summary>Drive number (0-based) of the physical drive.</summary>
    public uint DriveNumber { get; }

    /// <summary>Capabilities read from the physical drive.</summary>
    public DriveCapabilities Capabilities { get; }

    /// <summary>Media parameters read from the physical tape.</summary>
    public MediaParameters MediaParams { get; private set; }

    /// <summary>All drive profiles applicable to this drive.</summary>
    public IReadOnlyList<DriveProfile> Profiles { get; }

    /// <summary>
    /// Whether the fixture is in a healthy state (drive open, media loaded).
    /// Tests should check this before proceeding; if false, the fixture
    /// attempted recovery in <see cref="RecoverDrive"/> and failed.
    /// </summary>
    public bool IsHealthy => Drive.IsDriveOpen && Drive.IsMediaLoaded;

    /// <summary>
    /// Set to true when recovery fails irrecoverably. Remaining tests should skip.
    /// </summary>
    public bool IsFailed { get; private set; }

    /// <summary>Human-readable description of the drive for test output.</summary>
    public string DriveDescription { get; }

    /// <summary>
    /// Whether the tape is formatted with an initiator partition.
    /// <c>true</c> when the drive supports partitions and
    /// <see cref="PhysicalTestEnv.ForceNoPartition"/> is not set.
    /// </summary>
    public bool UsesPartition { get; }

    #endregion

    #region *** Construction ***

    /// <summary>
    /// Creates a fully ready physical tape fixture: drive opened, media loaded,
    /// formatted, and prepared.
    /// </summary>
    /// <param name="driveNumber">Physical drive number (0-based).</param>
    /// <param name="format">Whether to format the tape at construction (default true).</param>
    /// <param name="loggerFactory">Optional logger factory (defaults to <see cref="NullLoggerFactory"/>).</param>
    /// <param name="mediaDescription">Description for the initial TOC.</param>
    public PhysicalTapeFixture(
        uint driveNumber,
        bool format = true,
        ILoggerFactory? loggerFactory = null,
        string mediaDescription = "Physical Test Media")
    {
        LoggerFactory = loggerFactory ?? TestLoggerFactory.Default;
        DriveNumber = driveNumber;

        Drive = TapeDrive.CreateWin32(LoggerFactory);

        // Configure timeout from environment or default
        Drive.OperationTimeout = GetConfiguredTimeout();

        // Open drive (retry with delay — tape driver may need time to release after a prior process)
        const int maxOpenRetries = 3;
        const int openRetryDelayMs = 5000;
        bool opened = false;
        for (int attempt = 1; attempt <= maxOpenRetries; attempt++)
        {
            opened = Drive.ReopenDrive(driveNumber);
            if (opened) break;
            if (attempt < maxOpenRetries)
                System.Threading.Thread.Sleep(openRetryDelayMs);
        }
        if (!opened)
            throw new InvalidOperationException($"Failed to open physical tape drive #{driveNumber}");

        // Load media
        if (!Drive.ReloadMedia())
            throw new InvalidOperationException($"Failed to load media in drive #{driveNumber}. Is a tape inserted?");

        // Query capabilities
        Drive.Backend.FillDriveCapabilities(out var caps);
        Capabilities = caps;
        Profiles = DriveProfileDetector.Detect(in caps);
        DriveDescription = $"Drive #{driveNumber}: {DriveProfileDetector.Describe(in caps)}";

        if (Profiles.Count == 0)
            throw new InvalidOperationException(
                $"Drive #{driveNumber} doesn't match any known DriveProfile. {DriveDescription}");

        // Determine partition mode: use partition unless the env var forces it off
        bool forceNoPartition = !string.IsNullOrEmpty(
            Environment.GetEnvironmentVariable(PhysicalTestEnv.ForceNoPartition));
        UsesPartition = Capabilities.SupportsInitiatorPartition && !forceNoPartition;

        // Format media
        if (format)
        {
            if (!FormatTape())
                throw new InvalidOperationException($"Failed to format tape in drive #{driveNumber}");
        }

        // Prepare media (sets optimal block size, etc.)
        if (!Drive.PrepareMedia())
            throw new InvalidOperationException($"Failed to prepare media in drive #{driveNumber}");

        // Read media parameters
        Drive.Backend.FillMediaParameters(out var mediaParams);
        MediaParams = mediaParams;

        // Create initial TOC
        TOC = new TapeTOC(mediaDescription);
    }

    #endregion

    #region *** Drive Discovery ***

    /// <summary>
    /// Discovers available physical tape drives. Returns the drive numbers found.
    /// Checks the <see cref="PhysicalTestEnv.DriveNumbers"/> environment variable first;
    /// if not set, probes drives 0–<see cref="PhysicalTestEnv.MaxProbeNumber"/>.
    /// </summary>
    public static List<uint> DiscoverDrives()
    {
        // Check environment variable first
        string? envDrives = Environment.GetEnvironmentVariable(PhysicalTestEnv.DriveNumbers);
        if (!string.IsNullOrWhiteSpace(envDrives))
        {
            return [.. envDrives.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => uint.TryParse(s, out uint n) ? (uint?)n : null)
                .Where(n => n.HasValue)
                .Select(n => n!.Value)];
        }

        // Probe drives 0–MaxProbeNumber
        var found = new List<uint>();
        for (uint i = 0; i <= PhysicalTestEnv.MaxProbeNumber; i++)
        {
            if (TapeDrive.ProbeWin32(i))
                found.Add(i);
        }

        return found;
    }

    /// <summary>
    /// Returns the configured operation timeout, from environment or default.
    /// </summary>
    private static TimeSpan GetConfiguredTimeout()
    {
        string? envTimeout = Environment.GetEnvironmentVariable(PhysicalTestEnv.TimeoutSeconds);
        if (!string.IsNullOrWhiteSpace(envTimeout) && int.TryParse(envTimeout, out int seconds) && seconds > 0)
            return TimeSpan.FromSeconds(seconds);

        return TimeSpan.FromSeconds(DefaultTimeoutSeconds);
    }

    #endregion

    #region *** Recovery ***

    /// <summary>
    /// Attempts to recover the drive to a known-good state after a test failure.
    /// Recovery escalation: Rewind → Close handle + Reopen → UnloadMedia (physical eject,
    ///  absolute last resort since it requires the user to re-insert the tape).
    /// </summary>
    /// <returns>True if recovery succeeded and the drive is healthy.</returns>
    public bool RecoverDrive()
    {
        if (IsFailed)
            return false;

        // 1. Cheapest: try rewind (keeps handle and media loaded)
        try
        {
            if (Drive.IsMediaLoaded && Drive.Rewind())
                return true;
        }
        catch { /* fall through to next level */ }

        // 2. Close the handle and reopen (resets driver state without ejecting)
        try
        {
            Drive.CloseDrive();
            if (Drive.ReopenDrive(DriveNumber) && Drive.ReloadMedia())
            {
                Drive.PrepareMedia();
                return IsHealthy;
            }
        }
        catch { /* fall through to last resort */ }

        // 3. Last resort: UnloadMedia physically ejects the tape — requires user
        //    to re-insert it. Only try this when everything else has failed.
        try
        {
            Drive.Backend.UnloadMedia();
            if (Drive.ReopenDrive(DriveNumber) && Drive.ReloadMedia())
            {
                Drive.PrepareMedia();
                return IsHealthy;
            }
        }
        catch { /* recovery itself failed */ }

        IsFailed = true;
        return false;
    }

    /// <summary>
    /// Recovers the drive and reformats the tape for a clean start.
    /// Use when a previous test may have corrupted the tape layout.
    /// </summary>
    /// <returns>True if reformat succeeded.</returns>
    public bool RecoverAndReformat(string mediaDescription = "Physical Test Media")
    {
        if (!RecoverDrive())
            return false;

        if (!FormatTape())
        {
            IsFailed = true;
            return false;
        }

        Drive.PrepareMedia();
        TOC = new TapeTOC(mediaDescription);

        // Refresh media parameters
        Drive.Backend.FillMediaParameters(out var mediaParams);
        MediaParams = mediaParams;

        return IsHealthy;
    }

    /// <summary>
    /// Formats the tape according to <see cref="UsesPartition"/>.
    /// Creates a 4 MB initiator partition when enabled, plain format otherwise.
    /// </summary>
    private bool FormatTape()
    {
        return UsesPartition
            ? Drive.FormatMedia(4L * 1024 * 1024)
            : Drive.FormatMedia();
    }

    #endregion

    #region *** Agent Factories ***

    /// <summary>Creates a backup agent bound to this fixture's drive and TOC.</summary>
    public TapeFileBackupAgent CreateBackupAgent()
    {
        return new TapeFileBackupAgent(Drive, TOC);
    }

    /// <summary>
    /// Creates an extended restore agent with target directory and existing-file handling.
    /// </summary>
    public TapeFileRestoreAgentEx CreateRestoreAgent(
        string targetDir,
        bool recurseSubdirs = true,
        TapeHowToHandleExisting handleExisting = TapeHowToHandleExisting.Overwrite)
    {
        return new TapeFileRestoreAgentEx(Drive, targetDir, recurseSubdirs, handleExisting, TOC);
    }

    /// <summary>Creates a CRC-only validation agent (no disk writes).</summary>
    public TapeFileValidateAgent CreateValidateAgent()
    {
        return new TapeFileValidateAgent(Drive, TOC);
    }

    /// <summary>Creates a byte-for-byte verify agent (compares tape against disk files).</summary>
    public TapeFileVerifyAgent CreateVerifyAgent()
    {
        return new TapeFileVerifyAgent(Drive, TOC);
    }

    #endregion

    #region *** TOC Helpers ***

    /// <summary>
    /// Writes the TOC to tape (via agent) and asserts success.
    /// </summary>
    public void SaveTOC()
    {
        using var agent = new TapeFileAgent(Drive, TOC);
        Assert.True(agent.BackupTOC(enforce: true), "Failed to save TOC to tape");
    }

    /// <summary>
    /// Reads the TOC from tape (via agent) and replaces the fixture's TOC.
    /// Asserts success.
    /// </summary>
    public void LoadTOC()
    {
        using var agent = new TapeFileAgent(Drive, TOC);
        Assert.True(agent.RestoreTOC(), "Failed to restore TOC from tape");
        TOC = agent.TOC;
    }

    /// <summary>
    /// Performs a full TOC round-trip: save → restore → return the loaded TOC.
    /// </summary>
    public TapeTOC SaveAndReloadTOC()
    {
        SaveTOC();
        LoadTOC();
        return TOC;
    }

    #endregion

    #region *** Backup Convenience ***

    /// <summary>
    /// Backs up a list of files as a new set with sensible defaults, saves the TOC,
    /// and returns the backup agent's final statistics snapshot.
    /// </summary>
    /// <param name="fileList">Full paths to back up.</param>
    /// <param name="description">Backup set description.</param>
    /// <param name="incremental">Whether the set is incremental.</param>
    /// <param name="hashAlgorithm">Hash algorithm for integrity checking.</param>
    /// <param name="blockSize">Block size (0 = drive default).</param>
    /// <param name="notifiable">Optional callback handler.</param>
    /// <returns>Statistics snapshot after backup completes.</returns>
    public TapeFileStatistics BackupFiles(
        List<string> fileList,
        string description = "Test Set",
        bool incremental = false,
        TapeHashAlgorithm hashAlgorithm = TapeHashAlgorithm.Crc64,
        uint blockSize = 0,
        ITapeFileNotifiable? notifiable = null)
    {
        // Configure the set
        TOC.AddNewSetTOC(0, incremental);
        TOC.CurrentSetTOC.Description = description;
        TOC.CurrentSetTOC.HashAlgorithm = hashAlgorithm;
        TOC.CurrentSetTOC.BlockSize = blockSize == 0 ? Drive.DefaultBlockSize : blockSize;

        using var agent = CreateBackupAgent();

        bool success = agent.BackupFileListToCurrentSet(
            newSet: true,
            fileList,
            ignoreFailures: true,
            fileNotify: notifiable);

        Assert.True(success, "Backup failed");

        // Save TOC after successful backup
        Assert.True(agent.BackupTOC(), "Failed to save TOC after backup");

        return agent.Statistics;
    }

    #endregion

    #region *** Skip Helpers ***

    /// <summary>
    /// Skips the test if the fixture is irrecoverably failed or unhealthy.
    /// Call at the start of each test — if the fixture is dead, the test is skipped.
    /// </summary>
    public void AssertHealthyOrSkip()
    {
        Skip.If(IsFailed, "Physical tape fixture is in an irrecoverable state");
        Skip.IfNot(IsHealthy, "Physical tape fixture is not healthy. Call RecoverDrive() first.");
    }

    /// <summary>
    /// Skips the test if the drive does not support the given profile.
    /// </summary>
    public void RequireProfileOrSkip(DriveProfile profile)
    {
        Skip.IfNot(Profiles.Contains(profile),
            $"Drive #{DriveNumber} does not support profile {profile}. " +
            $"Available: [{string.Join(", ", Profiles)}]");
    }

    #endregion

    #region *** Dispose ***

    public void Dispose()
    {
        // Best-effort rewind before closing — leave the tape in a known position
        try
        {
            if (Drive.IsMediaLoaded)
                Drive.Rewind();
        }
        catch { /* best-effort */ }

        Drive.Dispose();
    }

    #endregion
}
