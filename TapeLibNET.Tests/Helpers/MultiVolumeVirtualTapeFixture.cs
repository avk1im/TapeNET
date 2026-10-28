using Microsoft.Extensions.Logging;
using TapeLibNET.Agents;
using TapeLibNET.Drive;
using TapeLibNET.Encryption;
using TapeLibNET.Headers;
using TapeLibNET.Media;
using TapeLibNET.Toc;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests.Helpers;


#region *** Media Header ***

/// <summary>How the volumes of a multi-volume series receive headers.</summary>
/// <remarks>
/// One axis, not two: set headers require a media header (SH-1), so the meaningful combinations are
///  few enough to enumerate. A separate <c>withSetHeaders</c> flag would permit
///  <c>(None, true)</c> — rejected at construction anyway — and would leave the reader to work out
///  which pairs are real.
/// </remarks>
public enum VolumeHeaderMode
{
    /// <summary>No volume is headed — a fully legacy series.</summary>
    None,

    /// <summary>
    /// Every volume carries a media header, but NO set headers — the shape written by the release that
    ///  shipped media headers alone. SH-1's middle state, and the only one that proves
    ///  <c>HasSetHeaders = false</c> is honoured rather than inferred from presence.
    /// </summary>
    MediaOnly,

    /// <summary>Every volume carries a media header AND per-set headers — a fully modern series.</summary>
    All,

    /// <summary>
    /// Volume 1 legacy (header-less), volumes 2+ fully headed — the mixed series of design §10.4.
    /// </summary>
    /// <remarks>
    /// Deliberately two-way, not three-way. A three-way mix would depend on how many volumes a given
    ///  test actually spans — which varies by profile and capacity — so it could not state what it
    ///  covers. Two-way pins the property that matters: per-volume re-resolution of
    ///  <see cref="TapeNavigator.SetHeadersExpected"/>, the §10 regression guard.
    /// </remarks>
    Mixed,
}

#endregion

/// <summary>
/// Test fixture for multi-volume tape scenarios.
/// Manages virtual tape volume lifecycle with snapshot-based volume swapping:
/// backup across volume boundaries, restore from multiple volumes, and volume re-insertion.
/// <para>
/// Each test creates its own instance for isolation. The fixture uses memory-backed
/// virtual tapes with a deliberately small capacity to trigger volume boundaries
/// with a small number of files.
/// </para>
/// </summary>
public sealed class MultiVolumeVirtualTapeFixture : IDisposable
{
    #region *** Constants ***

    /// <summary>
    /// Default content capacity per volume — small enough to force multi-volume
    /// with a modest number of files, large enough for the TOC and a few files.
    /// 256 KB with 16 KB block size ≈ 16 blocks of content.
    /// </summary>
    public const long DefaultContentCapacity = 256L * 1024;

    /// <summary>Default initiator partition capacity for Partitions profile.</summary>
    public const long DefaultInitiatorCapacity = 64L * 1024;

    /// <summary>The size of one TOC copy in these tests: one header block — the test TOCs stay well below 16 KiB.</summary>
    private const long TestTocCopyBytes = 16L * 1024;

    /// <summary>Room for the marks that close the TOC area, so a full volume keeps BOTH copies, closing marks included.</summary>
    private const long TocMarkAllowance = 16L * 1024;

    /// <summary>
    /// The reserve of the earlier fixture: exactly two one-block copies, no room for the TOC mark's gap file nor for copy
    ///  2's closing filemark. For tests that WANT a full volume to keep one copy only, or no closing mark.
    /// </summary>
    public const long SingleCopyTOCCapacity = 32L * 1024;
    #endregion

    #region *** Properties ***

    public TapeDrive Drive { get; }
    public TapeTOC TOC { get; private set; }
    public ILoggerFactory LoggerFactory { get; }
    public VirtualTapeDriveCapabilities Capabilities { get; }
    public VirtualTapeDriveBackend Backend { get; }

    /// <summary>Current volume number (1-based), mirrors <see cref="TapeTOC.Volume"/>.</summary>
    public int CurrentVolume => TOC.Volume;

    /// <summary>Number of volume snapshots saved (equals swaps performed).</summary>
    public int VolumeCount => _volumeSnapshots.Count;

    /// <summary>Highest volume number in use — snapshots plus whatever is loaded right now.</summary>
    /// <remarks>
    /// A MAX, not a sum: once a volume has been both snapshotted and re-loaded it appears in both, so
    ///  adding them would over-count after any <see cref="SwapToVolume"/>.
    /// </remarks>
    public int TotalVolumes => Math.Max(
        _volumeSnapshots.Count == 0 ? 0 : _volumeSnapshots.Keys.Max(),
        Backend.ContentMedia != null ? _loadedVolume : 0);

    /// <summary>Content capacity per volume in bytes.</summary>
    public long ContentCapacity { get; }

    /// <summary>
    /// TOC capacity override applied to each agent's navigator; <see langword="null"/> (default) =
    ///  what this layout needs to write two one-block copies in full: the navigator's own footprint
    ///  (copies, plus the TOC mark's gap file where the layout has one) and an allowance for
    ///  the closing marks.
    /// </summary>
    /// <remarks>
    /// Layout-aware, so every profile is tested under the same condition: a full volume holds both
    ///  TOC copies, each closed by its filemark (guarded by <c>Fixture_EveryVolumeHoldsBothTocCopies</c>).
    ///  Set it — e.g. to <see cref="SingleCopyTOCCapacity"/> — to test a reserve that is too small on purpose.
    /// </remarks>
    public long? TOCCapacityOverride { get; set; }

    /// <summary>
    /// When <see langword="true"/> and <see cref="TOCCapacityOverride"/> is not set, the agents run with the LIVE TOC
    ///  reserve (Design-TocReserve): sized from the TOC and raised as the set grows, never below the floor (capped at
    ///  1/32nd of the volume). Default <see langword="false"/>: the fixed 5d parity reserve.
    /// </summary>
    public bool UseLiveTocReserve { get; init; }

    /// <summary>
    /// The TOC reserve the fixture's agents run with on the loaded medium — for diagnostics and assertion messages.
    ///  Live:the reserve a set starting now would arm.
    /// </summary>
    public long EffectiveTOCCapacity
    {
        get
        {
            using var probe = new TapeAgentBase(Drive, TOC);
            return UseLiveTocReserve && TOCCapacityOverride is null
                ? probe.Navigator.TocReserveFor(TOC.EstimateSerializedSize())
                : TocReserveFor(probe.Navigator);
        }
    }

    #endregion

    #region *** Media Header and Set Headers ***

    VolumeHeaderMode HeaderMode { get; init; }

    /// <summary>Whether <paramref name="volumeNumber"/> carries a media header.</summary>
    private bool ShouldHeadVolume(int volumeNumber) => HeaderMode switch
    {
        VolumeHeaderMode.All or VolumeHeaderMode.MediaOnly => true,
        VolumeHeaderMode.Mixed => volumeNumber >= 2,   // legacy vol 1, modern vol 2+ (design §10.4)
        _ => false,
    };

    /// <summary>
    /// Whether <paramref name="volumeNumber"/> carries per-set headers. Implies a media header (SH-1),
    ///  so this is always a subset of <see cref="ShouldHeadVolume"/>.
    /// </summary>
    private bool ShouldWriteSetHeaders(int volumeNumber) => HeaderMode switch
    {
        VolumeHeaderMode.All => true,
        VolumeHeaderMode.Mixed => volumeNumber >= 2,
        _ => false,   // None and MediaOnly both write no set headers
    };

    /// <summary>
    /// Applies the header flags that <paramref name="volumeNumber"/> must carry under this fixture's
    ///  <see cref="HeaderMode"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Needed because ONE agent spans every volume of a backup — <c>ResumeBackupToNextVolume</c> continues
    ///  the same instance — so the flags captured at construction describe volume 1 only. In the field
    ///  that is correct and immutable: a real session heads every volume alike.
    /// </para>
    /// <para>
    /// A <see cref="VolumeHeaderMode.Mixed"/> series is therefore inherently CROSS-SESSION — volume 1
    ///  written by a release that predates media headers, volumes 2+ by a later one (design §10.4). No
    ///  single agent can produce it honestly, so the fixture re-stamps the flags at each swap. This
    ///  fabricates a data shape; it does not model a production flow.
    /// </para>
    /// <para>
    /// The window matters: this must run AFTER the new volume is inserted and BEFORE
    ///  <c>ResumeBackupToNextVolume</c>, which is where the new volume's media header is written — and
    ///  which is what stamps <see cref="TapeMediaHeader.HasSetHeaders"/> from
    ///  <see cref="TapeAgentBase.WritesSetHeaders"/>.
    /// </para>
    /// </remarks>
    private void ApplyHeaderFlagsForVolume(TapeFileBackupAgent agent, int volumeNumber)
    {
        agent.WritesMediaHeader = ShouldHeadVolume(volumeNumber);
        agent.WritesSetHeaders = ShouldWriteSetHeaders(volumeNumber);
        agent.RecordEmitter = EmitterFor(volumeNumber);   // the build that writes THIS volume
    }

    /// <summary>
    /// The record emitter per volume — e.g. legacy for volume 1 and 2.1 for the rest, a mixed series
    ///  written by two builds (Design-FormatV21 §9.3). <see langword="null"/>, or a function returning
    ///  <see langword="null"/>, = the agent's own V2.1 emitter.
    /// </summary>
    internal Func<int, ITapeRecordEmitter?>? EmitterForVolume { get; init; }

    private ITapeRecordEmitter? EmitterFor(int volumeNumber)
        => EmitterForVolume?.Invoke(volumeNumber);

    #endregion

    #region *** Volume Snapshot Management ***

    /// <summary>
    /// Volume number of the media PHYSICALLY loaded in the drive right now.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT <see cref="CurrentVolume"/> (= <c>TOC.Volume</c>), which is a LOGICAL property of
    ///  the backup series. The two coincide only if <see cref="TapeFileRestoreBaseAgent.ResumeRestoreFromAnotherVolume"/>
    ///  immediately following <see cref="SwapToVolume(int)"/> assigns <c>TOC.Volume</c> right after each
    ///  swap — a side effect, not a guarantee. A caller that swaps WITHOUT resuming (e.g. inspecting each
    ///  volume in turn) breaks the coincidence, and the outgoing snapshot would then overwrite another
    ///  volume's entry.
    /// </remarks>
    private int _loadedVolume = 1;


    /// <summary>
    /// Saved volume snapshots keyed by 1-based volume number.
    /// Used to re-insert previously used volumes for restore operations.
    /// </summary>
    private readonly Dictionary<int, VirtualTapeDriveBackend.MemoryMediaSnapshot> _volumeSnapshots = [];

    /// <summary>
    /// Captures the current volume's state and stores it in the snapshot dictionary.
    /// Must be called while media is loaded (before <see cref="SwapToNewVolume"/>
    /// or <see cref="SwapToVolume"/>).
    /// </summary>
    private void SaveCurrentVolumeSnapshot()
    {
        var snapshot = Backend.CaptureMemorySnapshot();
        Assert.NotNull(snapshot);
        _volumeSnapshots[_loadedVolume] = snapshot; // Note: _loadedVolume, NOT CurrentVolume!
    }

    /// <summary>
    /// Saves the current volume snapshot, ejects it, and inserts a fresh
    /// blank volume with the next volume number. Prepares the drive for writing.
    /// </summary>
    /// <returns>The new volume number.</returns>
    public int SwapToNewVolume()
    {
        SaveCurrentVolumeSnapshot();

        int newVolume = _volumeSnapshots.Count + 1;

        long initCap = Capabilities.SupportsInitiatorPartition
            ? DefaultInitiatorCapacity : 0;

        var capacity = ContentCapacity;
        if (!Drive.HasInitiatorPartition)
            capacity += TapeNavigator.DefaultTOCCapacity(Drive); // ensure enough space for TOC in set

        Backend.InsertMemoryMedia(capacity, initCap);
        Assert.True(Drive.ReloadMedia(), $"Failed to load new volume #{newVolume}");
        Assert.True(Drive.PrepareMedia(), $"Failed to prepare new volume #{newVolume}");

        _loadedVolume = newVolume; // important!
        return newVolume;
    }

    /// <summary>
    /// Saves the current volume snapshot (if loaded), ejects it, and re-inserts
    /// a previously saved volume by number.
    /// </summary>
    /// <param name="volumeNumber">1-based volume number to re-insert.</param>
    public void SwapToVolume(int volumeNumber)
    {
        // Save current state if media is still loaded
        var currentSnapshot = Backend.CaptureMemorySnapshot();
        if (currentSnapshot != null)
            _volumeSnapshots[_loadedVolume] = currentSnapshot; // Note: _loadedVolume, NOT CurrentVolume!

        Assert.True(_volumeSnapshots.ContainsKey(volumeNumber),
            $"No snapshot for volume #{volumeNumber}");

        Backend.InsertMemoryMedia(_volumeSnapshots[volumeNumber]);
        Assert.True(Drive.ReloadMedia(), $"Failed to reload volume #{volumeNumber}");
        Assert.True(Drive.PrepareMedia(), $"Failed to prepare volume #{volumeNumber}");

        _loadedVolume = volumeNumber; // important!
    }

    #endregion

    #region *** Construction ***

    /// <summary>
    /// Creates a multi-volume fixture with a small content capacity to trigger
    ///  volume boundaries. The first volume is already loaded and prepared.
    /// </summary>
    /// <param name="profile">Drive capability profile to emulate.</param>
    /// <param name="contentCapacity">Content partition capacity per volume.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    /// <param name="mediaDescription">Description for the initial TOC.</param>
    /// <param name="headerMode">How the volumes of the series receive media and set headers.</param>
    /// <param name="sealPadding">
    /// Whether encrypted sets are sealed with production padding (64 KiB units, Design-Encryption §5.4). Off by default:
    ///  the small volumes here could not hold a padded TOC within the TOC reserve.
    /// </param>
    public MultiVolumeVirtualTapeFixture(
        DriveProfile profile = DriveProfile.Setmarks,
        long contentCapacity = DefaultContentCapacity,
        ILoggerFactory? loggerFactory = null,
        string mediaDescription = "Multi-Volume Test Media",
        VolumeHeaderMode headerMode = VolumeHeaderMode.None,
        bool sealPadding = false)
    {
        ContentCapacity = contentCapacity;
        LoggerFactory = loggerFactory ?? TestLoggerFactory.Default;
        Capabilities = VirtualTapeFixture.ProfileToCapabilities(profile);
        HeaderMode = headerMode;
        long initCap = Capabilities.SupportsInitiatorPartition
            ? DefaultInitiatorCapacity : 0;

        // The following is NOT needed since we override the default (really large, 32MB) TOC capacity in Navigator,
        //  s. MultiVolumeVirtualTapeFixture.CreateBackupAgent() and .CreateRestoreAgent()
        //if (!Capabilities.SupportsInitiatorPartition)
        //    contentCapacity += TapeNavigator.DefaultTOCCapacity(null); // ensure enough space for TOC in set

        Backend = VirtualTapeDriveBackend.CreateMemoryBacked(
            LoggerFactory, Capabilities, contentCapacity, initCap);

        // Disable IO throttling for test speed
        Backend.IoRate = VirtualTapeDriveIoRate.Unlimited;

        Drive = new TapeDrive(LoggerFactory, Backend);

        // Full lifecycle: open → load → prepare
        Assert.True(Drive.ReopenDrive(0), "Failed to open virtual drive");
        Assert.True(Drive.ReloadMedia(), "Failed to load virtual media");
        Assert.True(Drive.PrepareMedia(), "Failed to prepare virtual media");

        // Create initial TOC — volume 1
        TOC = new TapeTOC(mediaDescription);
#if TEST_HOOKS
        if (!sealPadding)
            TOC.SealPadUnit = 1;    // small virtual volumes: an unpadded sealed set costs about what a plain one does
#endif
    }

    #endregion

    #region *** Agent Factories ***

    /// <summary>Creates a backup agent bound to this fixture's drive and TOC.</summary>
    public TapeFileBackupAgent CreateBackupAgent()
    {
        var agent = new TapeFileBackupAgent(Drive, TOC);
        ApplyHeaderFlagsForVolume(agent, TOC.Volume);
        ApplyTocCapacity(agent.Navigator);
        return agent;
    }

    /// <summary>Creates a restore agent with target directory.</summary>
    public TapeFileRestoreAgentEx CreateRestoreAgent(
        string targetDir,
        bool recurseSubdirs = true,
        TapeHowToHandleExisting handleExisting = TapeHowToHandleExisting.Overwrite)
    {
        var agent = new TapeFileRestoreAgentEx(Drive, targetDir, recurseSubdirs, handleExisting, TOC);
        ApplyTocCapacity(agent.Navigator);
        return agent;
    }

    #endregion

    #region *** TOC Helpers ***

    /// <summary>
    /// The TOC reserve this fixture arms on <paramref name="navigator"/>: the override, or the layout-aware default — two
    ///  one-block copies (plus the TOC mark's gap file where the layout has one) and room for the closing marks.
    /// </summary>
    private long TocReserveFor(TapeNavigator navigator)
        => TOCCapacityOverride ?? navigator.TOCFootprint(TestTocCopyBytes) + TocMarkAllowance;

    /// <summary>
    /// Applies the TOC reserve to <paramref name="navigator"/> — the override, the default, or none (live).
    /// </summary>
    private void ApplyTocCapacity(TapeNavigator navigator)
        => navigator.TocReserveOverride = UseLiveTocReserve && TOCCapacityOverride is null
            ? null
            : TocReserveFor(navigator);

    /// <summary>
    /// Writes the TOC to tape (via agent) and asserts success.
    /// </summary>
    public void SaveTOC()
    {
        using var agent = new TapeAgentBase(Drive, TOC)
            { RecordEmitter = EmitterFor(_loadedVolume) };
        ApplyTocCapacity(agent.Navigator);
        if (!agent.BackupTOC())
            Assert.True(agent.BackupTOC(enforce: true), "Failed to save TOC to tape (even with enforce)");
    }

    /// <summary>
    /// Reads the TOC from tape (via agent) and replaces the fixture's TOC.
    /// </summary>
    public void LoadTOC()
    {
        using var agent = new TapeAgentBase(Drive, TOC);
        ApplyTocCapacity(agent.Navigator);
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

    #region *** Multi-Volume Backup ***

    /// <summary>
    /// Backs up files with automatic multi-volume handling.
    ///  When the current volume fills up, the method saves the TOC, swaps to a new
    ///  volume, and resumes the backup — mirroring the real-world console app flow.
    /// </summary>
    /// <param name="fileList">Full paths to back up.</param>
    /// <param name="description">Backup set description.</param>
    /// <param name="incremental">Whether the set is incremental.</param>
    /// <param name="hashAlgorithm">Hash algorithm for integrity checking.</param>
    /// <param name="blockSize">Block size (0 = drive default).</param>
    /// <param name="notifiable">
    /// Optional callback handler — for the files, AND for the TOC saves on every volume
    ///  (to register TOC anomalies).
    /// </param>
    /// <param name="password">
    /// Encrypts the set (Design-Encryption §15.7): a new chain — or, for an incremental on
    ///  an encrypted previous set, that chain's key. <see langword="null"/> = a plain set.
    ///  Continuation parts inherit it.
    /// </param>
    /// <param name="encryption">The scheme, when <paramref name="password"/> is given.</param>
    /// <returns>
    /// Statistics snapshot after the entire backup completes (across all volumes).
    /// </returns>
    public TapeFileStatistics BackupFiles(
        List<string> fileList,
        string description = "Test Set",
        bool incremental = false,
        TapeHashAlgorithm hashAlgorithm = TapeHashAlgorithm.Crc64,
        uint blockSize = 0,
        ITapeFileNotifiable? notifiable = null,
        string? password = null,
        TapeEncryption encryption = TapeEncryption.Software)
    {
        // Configure the set
        TOC.AddNewSetTOC(0, incremental);
        TOC.CurrentSetTOC.Description = description;
        TOC.CurrentSetTOC.HashAlgorithm = hashAlgorithm;
        TOC.CurrentSetTOC.BlockSize = blockSize == 0 ? Drive.DefaultBlockSize : blockSize;
        if (password is not null)
            EncryptCurrentSet(password, encryption);

        using var agent = CreateBackupAgent();

        // Saves the TOC on the current volume: normal first, then enforced (mirrors the console app flow). The
        //  notifiable sees the TOC anomalies — the same channel the service uses.
        void SaveToc(string when)
        {
            if (!agent.BackupTOC(tocNotify: notifiable))
                Assert.True(agent.BackupTOC(enforce: true, tocNotify: notifiable),
                    $"Failed to save TOC {when} (even with enforce)");
        }

        TapeResult result = agent.BackupFileListToCurrentSet(
            newSet: true,
            fileList,
            ignoreFailures: true,
            fileNotify: notifiable);

        // Multi-volume loop: backup → TOC → swap → resume. A REJECTED continuation cartridge makes
        //  CanResumeToNextVolume false: the loop ends and the assertion reports why.
        while (!result.Success && agent.CanResumeToNextVolume)
        {
            // If the empty continuation set has no files, clean it up
            if (TOC.CurrentSetTOC.Count == 0)
                TOC.RemoveLastEmptySet();

            SaveToc("before volume swap");

            // Swap to a fresh volume, then re-stamp the header flags for it — see
            //  ApplyHeaderFlagsForVolume() for why one agent cannot carry them across volumes.
            int newVolume = SwapToNewVolume();
            ApplyHeaderFlagsForVolume(agent, newVolume);

            // Resume backup on the new volume
            result = agent.ResumeBackupToNextVolume();
        }

        Assert.True(result.Success, $"Multi-volume backup failed: {result.ErrorMessage}" +
            (agent.IsVolumeRejected
                ? $" (continuation volume #{TOC.Volume + 1} rejected)" : string.Empty));

        SaveToc("after backup");
        return agent.Statistics;
    }

    #endregion

    #region *** Multi-Volume Restore ***

    /// <summary>
    /// Restores all files from the current set (non-incremental) with automatic
    /// multi-volume handling. When the restore agent needs a different volume,
    /// the method swaps to it and resumes.
    /// </summary>
    /// <param name="restoreDir">Target directory for restored files.</param>
    /// <param name="notifiable">Optional callback handler.</param>
    /// <param name="configure">Optional configuration action for the restore agent.</param>
    /// <returns>Statistics snapshot after the entire restore completes.</returns>
    public TapeFileStatistics RestoreAllFilesFromCurrentSet(
        string restoreDir,
        ITapeFileNotifiable? notifiable = null,
        Action<TapeFileRestoreAgentEx>? configure = null)
    {
        using var agent = CreateRestoreAgent(restoreDir);
        configure?.Invoke(agent);

        TapeResult result = agent.RestoreAllFilesFromCurrentSet(
            ignoreFailures: true, fileNotify: notifiable);

        // Multi-volume loop. A REJECTED volume (e.g. its TOC unreadable) makes CanResumeFromAnotherVolume false:
        //  the loop ends and the assertion reports why, instead of re-offering the same cartridge forever.
        while (!result.Success && agent.CanResumeFromAnotherVolume)
        {
            int volumeNeeded = agent.VolumeToResumeFrom;
            SwapToVolume(volumeNeeded);
            result = agent.ResumeRestoreFromAnotherVolume();
        }

        Assert.True(result.Success, $"Multi-volume restore failed: {result.ErrorMessage}" +
            (agent.IsVolumeRejected ? $" (volume #{agent.VolumeToResumeFrom} rejected)" : ""));

        return agent.Statistics;
    }

    /// <summary>
    /// Restores files from the current set's incremental chain with automatic
    /// multi-volume handling.
    /// </summary>
    /// <param name="restoreDir">Target directory for restored files.</param>
    /// <param name="notifiable">Optional callback handler.</param>
    /// <returns>Statistics snapshot after the entire restore completes.</returns>
    public TapeFileStatistics RestoreFilesFromCurrentSetInc(
        string restoreDir,
        ITapeFileNotifiable? notifiable = null)
    {
        using var agent = CreateRestoreAgent(restoreDir);

        TapeResult result = agent.RestoreFilesFromCurrentSetInc(
            null, ignoreFailures: true, fileNotify: notifiable);

        // Multi-volume loop — see RestoreAllFilesFromCurrentSet
        while (!result.Success && agent.CanResumeFromAnotherVolume)
        {
            int volumeNeeded = agent.VolumeToResumeFrom;
            SwapToVolume(volumeNeeded);
            result = agent.ResumeRestoreFromAnotherVolume();
        }

        Assert.True(result.Success, $"Multi-volume incremental restore failed: {result.ErrorMessage}" +
            (agent.IsVolumeRejected ? $" (volume #{agent.VolumeToResumeFrom} rejected)" : ""));

        return agent.Statistics;
    }

    #endregion

    #region *** Encryption ***

    /// <summary>PBKDF2 work factor of every set the fixture encrypts — low, so the suite stays fast.</summary>
    public const int TestIterations = 1_000;

    // The fixture's key ring: holds every chain key it created or unwrapped; disposed with the fixture.
    private readonly TapeKeyRing m_ring = new();

    /// <summary>
    /// Unlocks every locked set of <see cref="TOC"/> — by trial with the keys already held, else with
    ///  <paramref name="password"/> (asserted). Returns how many sets were unlocked.
    /// </summary>
    public int UnlockAll(string password)
    {
        int lockedBefore = TOC.Count(s => s.IsLocked);
        m_ring.UnlockAll(TOC);
        for (int i = 1; i <= TOC.Count; i++)
        {
            if (!TOC[i].IsLocked || !TOC[i].IsUnlockable)
                continue;
            TapeResult result = m_ring.Unlock(TOC, i, password);
            Assert.True(result.Success, $"Fixture: unlocking set #{i} failed: {result.ErrorMessage}");
        }
        return lockedBefore - TOC.Count(s => s.IsLocked);
    }

    /// <summary>
    /// Makes the CURRENT (new, empty) set encrypted: a new chain, or — for an incremental on an encrypted previous set —
    ///  the previous set's key under a fresh envelope (Design-Encryption §8.2). Continuation parts on later volumes
    ///  inherit scheme, envelope and key through <c>ToParams</c> (§8.3) — nothing to do here.
    /// </summary>
    public void EncryptCurrentSet(string password, TapeEncryption encryption = TapeEncryption.Software)
    {
        using var _ = TapeCrypto.UseIterations(TestIterations);
        TapeSetTOC set = TOC.CurrentSetTOC;
        TapeSetTOC? previous = TOC.CurrentSetIndex > 1 ? TOC[TOC.CurrentSetIndex - 1] : null;

        if (set.Incremental && previous is { IsEncrypted: true })
        {
            TapeSetKey key = previous.Key
                ?? throw new InvalidOperationException("Fixture: the incremental's base is locked — UnlockAll first");
            set.EnableEncryption(previous.Encryption, TapeSetEncryption.Wrap(key, password), key);
            return;
        }

        var (envelope, fresh) = TapeSetEncryption.CreateChain(password);
        set.EnableEncryption(encryption, envelope, m_ring.Add(fresh));
    }

    #endregion

    #region *** Dispose ***

    public void Dispose()
    {
        Drive.Dispose();
        m_ring.Dispose();
    }

    #endregion
}
