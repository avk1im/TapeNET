using TapeLibNET.Drive;     // TapeDrive
using TapeLibNET.Virtual;   // VirtualTapeDriveBackend, VirtualTapeDriveCapabilities
using TapeLibNET.Agents;
using TapeLibNET.Services;
using TapeLibNET.Tests.Helpers; // TempFileTree, FileComparer, TempVirtualMedia, MultiVolumeTapeServiceHost
using TapeLibNET.Toc;

namespace TapeLibNET.Tests.Services;

/// <summary>
/// A backup that writes no file leaves no trace: the agent erases the empty set it began (rewrites the mark before it), the
///  TOC drops it, and the next append lands exactly where the previous content ended — on every drive layout.
/// </summary>
/// <remarks>
/// <para>
/// <b>The originating defect:</b> a backup whose only file vanished wrote the set header and its closing mark over the old
///  TOC, then dropped the set from the TOC it saved behind them. The next append was refused (RecordedEndDisputed). The
///  interim fix kept the empty set; the current one erases it (<c>TapeNavigator.EraseContentFrom</c>).
/// </para>
/// <para>
/// <b>The vanishing file</b> is deleted from the progress handler's <c>BeforeFileProcessed</c> hook — after the selection
///  was resolved and the set begun, before the agent opens it: exactly the "File not found" the user met, deterministic.
/// </para>
/// <para>
/// <b>Tape-level proof</b> without internals: the number of marks in the content partition is the same before and after
///  the failed backup. A kept set adds its closing mark; an erased one does not.
/// </para>
/// <para>
/// Every request carries <c>ProceedOnMediaMismatch</c>: several backups run in one session, where the service's cached
///  header would otherwise prompt after the first overwrite (see <see cref="ServiceSetAnomalyTests"/>, §Helpers).
/// </para>
/// </remarks>
public class ServiceEmptySetTests : ServiceTestBase
{
    #region *** Data and helpers ***

    private static readonly TimeSpan NoHang = TimeSpan.FromMinutes(2);

    public static TheoryData<DriveProfile> AllProfiles =>
        [DriveProfile.Setmarks, DriveProfile.Partitions, DriveProfile.SeqFilemarks, DriveProfile.FilemarksOnly];

    private static TempVirtualMedia NewMedia(DriveProfile profile)
        => new(withInitiator: profile == DriveProfile.Partitions, ContentCapacity, InitiatorCapacity);

    private static TempFileTree Tree(string name, int count = 3)
    {
        var tree = new TempFileTree();
        tree.AddFiles(name, count, minSize: 512, maxSize: 4_096);
        return tree;
    }

    /// <summary>A backup request that never prompts about the media identity (see the class remarks).</summary>
    private static BackupRequest Req(TestTapeService svc, string root, string description, bool append = true)
        => MakeBackupRequest(svc, root, description, append: append) with { ProceedOnMediaMismatch = true };

    /// <summary>
    /// Backs up a one-file tree whose file disappears just before the agent opens it: the set is begun, no file lands.
    /// </summary>
    private static async Task<BackupResult> BackupVanishingFileAsync(TestTapeService svc, BackupRequest? request = null)
    {
        using var gone = Tree("gone", count: 1);
        string path = gone.Files[0];
        svc.ConfigureBackupHandler = h => h.BeforeFileProcessed = (_, _) =>
        {
            if (File.Exists(path))
                File.Delete(path);
            return true;                                    // process it — and fail to open it
        };
        try
        {
            var result = await svc.ExecuteBackupAsync(request is null
                ? Req(svc, gone.RootPath, "Vanishing")
                : request with { FileList = [gone.RootPath] }).WaitAsync(NoHang);
            Assert.Equal(0, result.FilesSucceeded);
            Assert.Equal(1, result.FilesFailed);
            return result;
        }
        finally
        {
            svc.ConfigureBackupHandler = null;
        }
    }

    /// <summary>The marks in the content partition — opened alone, between operations.</summary>
    private async Task<int> CountContentMarksAsync(TempVirtualMedia media, DriveProfile profile)
    {
        var (svc, _) = CreateService();
        using (svc)
        {
            Assert.True(await svc.OpenVirtualDriveAsync(CapsFor(media, profile), VmdFor(media), FileMode.Open), svc.LastError);
            Assert.True(await svc.LoadMediaAsync(), svc.LastError);
            int marks = svc.VirtualBackend!.ContentMedia!.Layout.Count(b => b.IsMark);
            await svc.CloseAsync();
            return marks;
        }
    }

    private static RestoreRequest RestoreSet(int setIndex, string targetDir) => new(
        Mode:                  RestoreMode.Restore,
        CheckedFilesBySet:     new Dictionary<int, IReadOnlyList<TapeFileInfo>?> { [setIndex] = null },
        Incremental:           false,
        TargetDirectory:       targetDir,
        RecurseSubdirectories: true,
        HandleExisting:        TapeHowToHandleExisting.Overwrite,
        SkipAllErrors:         false,
        EjectWhenDone:         false);

    private static string RestoreDir(TempVirtualMedia media, string name)
    {
        string dir = Path.Combine(media.Root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Restores <paramref name="setIndex"/> and compares it byte for byte with <paramref name="tree"/>.</summary>
    private static async Task AssertSetRestoresAsync(TestTapeService svc, TestTapeServiceHost host, TempVirtualMedia media,
        int setIndex, TempFileTree tree, string dirName)
    {
        string target = RestoreDir(media, dirName);
        var result = await svc.ExecuteRestoreAsync(RestoreSet(setIndex, target)).WaitAsync(NoHang);
        Assert.True(result.Success, $"{result.Message}\n{host.DumpReports()}");
        Assert.Equal(tree.Files.Count, result.FilesSucceeded);
        Assert.False(result.HasSetAnomalies, host.DumpReports());
        FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, FindRestoredRoot(target, tree.RootPath));
    }

    /// <summary>No set on tape the TOC does not know of, and none in the TOC the tape does not hold.</summary>
    private static void AssertNoEmptySets(TapeTOC toc)
        => Assert.DoesNotContain(toc, s => s.Count == 0);

    #endregion

    #region *** Straight append: the originating scenario ***

    /// <summary>
    /// File → vanishing file → file, each in a session of its own: the failed backup leaves the tape as it found it (same
    ///  marks), the TOC lists one set, and the next append lands at the recorded end — no dispute, set #2 restores.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public async Task Append_FileVanishes_EmptySetErased_NextAppendLands(DriveProfile profile)
    {
        using var media = NewMedia(profile);
        using var first = Tree("first");
        using var third = Tree("third");

        var (seed, seedHost) = await OpenAndFormatAsync(media, profile);
        using (seed)
            Assert.True((await seed.ExecuteBackupAsync(Req(seed, first.RootPath, "First", append: false))).Success,
                seedHost.DumpReports());
        int marksBefore = await CountContentMarksAsync(media, profile);

        // ── The failed backup ──
        var (svc, host) = await ReopenAsync(media, profile: profile);
        using (svc)
        {
            var result = await BackupVanishingFileAsync(svc);
            Assert.False(result.HasTocAnomalies, host.DumpReports());
            Assert.Equal(1, svc.TOC!.Count);
            Assert.False(host.ContainsMessage("empty backup set"), host.DumpReports());
            // With the TOC in its own partition nothing needs saving: the content partition is back where it was
            Assert.Equal(profile != DriveProfile.Partitions, host.ContainsMessage("Backing up TOC"));
        }
        Assert.Equal(marksBefore, await CountContentMarksAsync(media, profile));

        // ── The tape agrees: one set, and the next append is accepted ──
        var (svc3, host3) = await ReopenAsync(media, profile: profile);
        using (svc3)
        {
            Assert.Equal(1, svc3.TOC!.Count);
            var result = await svc3.ExecuteBackupAsync(Req(svc3, third.RootPath, "Third")).WaitAsync(NoHang);
            Assert.True(result.Success, $"{result.Message}\n{host3.DumpReports()}");
            Assert.False(result.HasTocAnomalies, host3.DumpReports());
            Assert.Equal(2, svc3.TOC.Count);
        }

        var (check, checkHost) = await ReopenAsync(media, profile: profile);
        using (check)
        {
            Assert.Equal(2, check.TOC!.Count);
            AssertNoEmptySets(check.TOC);
            await AssertSetRestoresAsync(check, checkHost, media, check.TOC.FirstSetOnVolume, first, "r1");
            await AssertSetRestoresAsync(check, checkHost, media, check.TOC.LastSetOnVolume, third, "r2");
        }
    }

    /// <summary>
    /// The same three backups in ONE session, as the user ran them in TapeWin: the in-memory TOC never disagrees with the
    ///  tape — reloading it changes nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public async Task Append_FileVanishes_SameSession_InMemoryTocMatchesTape(DriveProfile profile)
    {
        using var media = NewMedia(profile);
        using var first = Tree("first");
        using var third = Tree("third");

        var (svc, host) = await OpenAndFormatAsync(media, profile);
        using (svc)
        {
            Assert.True((await svc.ExecuteBackupAsync(Req(svc, first.RootPath, "First", append: false))).Success,
                host.DumpReports());
            await BackupVanishingFileAsync(svc);
            Assert.Equal(1, svc.TOC!.Count);

            var result = await svc.ExecuteBackupAsync(Req(svc, third.RootPath, "Third")).WaitAsync(NoHang);
            Assert.True(result.Success, $"{result.Message}\n{host.DumpReports()}");
            Assert.False(result.HasTocAnomalies, host.DumpReports());
            Assert.Equal(2, svc.TOC.Count);

            Assert.True(await svc.RestoreTOCAsync(), svc.LastError);       // reload: the tape says the same
            Assert.Equal(2, svc.TOC!.Count);
            AssertNoEmptySets(svc.TOC);
        }
    }

    /// <summary>Two failed backups in a row: still nothing on tape, still one set.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public async Task Append_FileVanishesTwice_TapeUnchanged(DriveProfile profile)
    {
        using var media = NewMedia(profile);
        using var first = Tree("first");

        var (seed, seedHost) = await OpenAndFormatAsync(media, profile);
        using (seed)
            Assert.True((await seed.ExecuteBackupAsync(Req(seed, first.RootPath, "First", append: false))).Success,
                seedHost.DumpReports());
        int marksBefore = await CountContentMarksAsync(media, profile);

        var (svc, _) = await ReopenAsync(media, profile: profile);
        using (svc)
        {
            await BackupVanishingFileAsync(svc);
            await BackupVanishingFileAsync(svc);
            Assert.Equal(1, svc.TOC!.Count);
        }
        Assert.Equal(marksBefore, await CountContentMarksAsync(media, profile));

        var (check, checkHost) = await ReopenAsync(media, profile: profile);
        using (check)
        {
            Assert.Equal(1, check.TOC!.Count);
            await AssertSetRestoresAsync(check, checkHost, media, check.TOC.LastSetOnVolume, first, "r");
        }
    }

    #endregion

    #region *** Overwrite: the first set after a format, and append-after-set ***

    /// <summary>
    /// The very first backup after a format finds no file: the set — the first on the volume — is erased behind the media
    ///  header, which stays. The TOC holds no set, identifies the cartridge, and the next backup becomes set #1.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public async Task FirstSetAfterFormat_FileVanishes_HeaderKept_NoSet(DriveProfile profile)
    {
        using var media = NewMedia(profile);
        using var next = Tree("next");

        var (svc, host) = await OpenAndFormatAsync(media, profile);
        using (svc)
        {
            await BackupVanishingFileAsync(svc, Req(svc, "", "Vanishing", append: false));
            Assert.Equal(0, svc.TOC!.Count);
        }

        var (check, checkHost) = await ReopenAsync(media, profile: profile);
        using (check)
        {
            Assert.Equal(0, check.TOC!.Count);
            Assert.NotNull(check.LoadedMediaHeader);
            Assert.Equal(check.TOC.MediaId, check.LoadedMediaHeader!.MediaId);

            var result = await check.ExecuteBackupAsync(Req(check, next.RootPath, "Next")).WaitAsync(NoHang);
            Assert.True(result.Success, $"{result.Message}\n{checkHost.DumpReports()}");
            Assert.False(result.HasTocAnomalies, checkHost.DumpReports());
        }

        var (final, finalHost) = await ReopenAsync(media, profile: profile);
        using (final)
        {
            Assert.Equal(1, final.TOC!.Count);
            await AssertSetRestoresAsync(final, finalHost, media, final.TOC.LastSetOnVolume, next, "r");
        }
    }

    /// <summary>
    /// Append after set #1 of three (mode 1) finds no file: the sets after #1 were given up by the request, the empty set is
    ///  erased behind #1 — the media now ends after it, and the next append lands there.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public async Task AppendAfterSet_FileVanishes_MediaEndsAfterThatSet(DriveProfile profile)
    {
        using var media = NewMedia(profile);
        using var t1 = Tree("t1");
        using var t2 = Tree("t2");
        using var t3 = Tree("t3");
        using var next = Tree("next");

        var (seed, seedHost) = await OpenAndFormatAsync(media, profile);
        using (seed)
        {
            Assert.True((await seed.ExecuteBackupAsync(Req(seed, t1.RootPath, "S1", append: false))).Success, seedHost.DumpReports());
            Assert.True((await seed.ExecuteBackupAsync(Req(seed, t2.RootPath, "S2"))).Success, seedHost.DumpReports());
            Assert.True((await seed.ExecuteBackupAsync(Req(seed, t3.RootPath, "S3"))).Success, seedHost.DumpReports());
        }

        var (svc, host) = await ReopenAsync(media, profile: profile);
        using (svc)
        {
            var request = Req(svc, "", "Vanishing") with { AppendAfterSetIndex = svc.TOC!.FirstSetOnVolume };
            await BackupVanishingFileAsync(svc, request);
            Assert.Equal(1, svc.TOC!.Count);
            Assert.True(host.ContainsMessage("now ends after set"), host.DumpReports());
        }

        var (check, checkHost) = await ReopenAsync(media, profile: profile);
        using (check)
        {
            Assert.Equal(1, check.TOC!.Count);
            var result = await check.ExecuteBackupAsync(Req(check, next.RootPath, "Next")).WaitAsync(NoHang);
            Assert.True(result.Success, $"{result.Message}\n{checkHost.DumpReports()}");
            Assert.False(result.HasTocAnomalies, checkHost.DumpReports());
        }

        var (final, finalHost) = await ReopenAsync(media, profile: profile);
        using (final)
        {
            Assert.Equal(2, final.TOC!.Count);
            AssertNoEmptySets(final.TOC);
            await AssertSetRestoresAsync(final, finalHost, media, final.TOC.FirstSetOnVolume, t1, "r1");
            await AssertSetRestoresAsync(final, finalHost, media, final.TOC.LastSetOnVolume, next, "r2");
        }
    }

    #endregion

    #region *** Multi-volume: a set whose part on the full volume holds no file ***

    // Two volumes of 3 MiB. Set #1 fills about 60% of volume 1; set #2 begins with a file larger than what remains, so
    //  end-of-media strikes on its FIRST file: the file restarts on volume 2, and set #2's part on volume 1 holds none.
    private const long MvCapacity = 3L * 1024 * 1024;
    private const long MvSeedFileSize = 350L * 1024;
    private const int MvSeedFileCount = 5;                      // ≈ 1.7 MiB
    private const long MvBigFileSize = 2L * 1024 * 1024;        // > what remains on volume 1, < a fresh volume

    private static (TempVirtualMedia, TempVirtualMedia) MakeVolumes(DriveProfile profile)
    {
        bool partitions = profile == DriveProfile.Partitions;
        return (new TempVirtualMedia(partitions, MvCapacity), new TempVirtualMedia(partitions, MvCapacity));
    }

    /// <summary>
    /// The parked case (the continuation policy rules, the erase stays out): end-of-media on the first file of a set. Pins
    ///  what must hold whatever the policy decides — no TOC record of an empty part that is not on tape, the series restores
    ///  byte for byte, and set #1 on the full volume is still found by counting.
    /// </summary>
    /// <remarks>
    /// If this test fails on the first assertion block, the volume's TOC records an empty part that the tape does not
    ///  hold, or drops one it does — the decision still open from the empty-set work.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public async Task MultiVolume_EomOnFirstFileOfSet_EmptyPartConsistent_SeriesRestores(DriveProfile profile)
    {
        var (vol1, vol2) = MakeVolumes(profile);
        using (vol1) using (vol2)
        {
            IReadOnlyList<TempVirtualMedia> volumes = [vol1, vol2];
            using var seedTree = new TempFileTree();
            for (int i = 0; i < MvSeedFileCount; i++)
                seedTree.AddFile($"seed_{i:D2}.bin", MvSeedFileSize);
            using var bigTree = new TempFileTree();
            bigTree.AddFile("a_big.bin", MvBigFileSize);            // sorts first: the set's first file
            bigTree.AddFile("b_small.bin", 4 * 1024);

            var (svc, host) = await OpenAndFormatMultiVolumeAsync(volumes, profile);
            using (svc)
            {
                var r1 = await svc.ExecuteBackupAsync(Req(svc, seedTree.RootPath, "Seed", append: false)).WaitAsync(NoHang);
                Assert.True(r1.Success, host.DumpReports());
                Assert.Equal(0, host.VolumesInserted);

                var r2 = await svc.ExecuteBackupAsync(Req(svc, bigTree.RootPath, "Spanning")).WaitAsync(NoHang);
                Assert.True(r2.Success, $"{r2.Message}\n{host.DumpReports()}");
                Assert.Equal(bigTree.Files.Count, r2.FilesSucceeded);
                Assert.True(host.VolumesInserted >= 1, "the second set must continue on volume 2");
            }

            // ── Volume 1 alone: its TOC agrees with its tape ──
            var (v1, v1Host) = await ReopenMultiVolumeAsync((IReadOnlyList<TempVirtualMedia>)[vol1], profile);
            using (v1)
            {
                var toc = v1.TOC!;
                Assert.Equal(1, toc.Volume);
                Assert.DoesNotContain(toc, s => s.Volume == 1 && s.Count == 0 && !s.IsOnTape);
                int seedSet = toc.FirstSetOnVolume;
                Assert.Equal(MvSeedFileCount, toc[seedSet].Count);
                await AssertSetRestoresAsync(v1, v1Host, vol1, seedSet, seedTree, "r_seed_vol1");
            }

            // ── The series, opened on its last volume: everything restores ──
            var (series, seriesHost) = await ReopenMultiVolumeAsync(volumes, profile);
            using (series)
            {
                var toc = series.TOC!;
                Assert.Equal(2, toc.Volume);
                Assert.DoesNotContain(toc, s => s.Volume == 1 && s.Count == 0 && !s.IsOnTape);
                await AssertSetRestoresAsync(series, seriesHost, vol1, toc.LastSetOnVolume, bigTree, "r_spanning");
            }
        }
    }

    /// <summary>
    /// A set whose only file does not fit: end-of-media before it commits. Single volume (no multi-volume): the empty set
    ///  is erased — the TOC lists one set, the tape's marks are as before, and a small append lands behind set #1.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public async Task FileDoesNotFit_NoMultivolume_EmptySetErased_SpaceRecovered(DriveProfile profile)
    {
        const long Capacity = 3L * 1024 * 1024;
        using var media = new TempVirtualMedia(profile == DriveProfile.Partitions, Capacity);
        using var big = new TempFileTree();
        big.AddFile("big.bin", 1_700L * 1024);                  // fits once, not twice
        using var small = Tree("small");

        var (seed, seedHost) = await OpenAndFormatAsync(media, profile);
        using (seed)
            Assert.True((await seed.ExecuteBackupAsync(Req(seed, big.RootPath, "Big", append: false))).Success,
                seedHost.DumpReports());
        int marksBefore = await CountContentMarksAsync(media, profile);

        var (svc, host) = await ReopenAsync(media, profile: profile);
        using (svc)
        {
            var result = await svc.ExecuteBackupAsync(Req(svc, big.RootPath, "Again") with { NoMultivolume = true })
                .WaitAsync(NoHang);
            Assert.Equal(0, result.FilesSucceeded);
            Assert.Equal(1, svc.TOC!.Count);
        }
        Assert.Equal(marksBefore, await CountContentMarksAsync(media, profile));

        var (check, checkHost) = await ReopenAsync(media, profile: profile);
        using (check)
        {
            Assert.Equal(1, check.TOC!.Count);
            var result = await check.ExecuteBackupAsync(Req(check, small.RootPath, "Small")).WaitAsync(NoHang);
            Assert.True(result.Success, $"{result.Message}\n{checkHost.DumpReports()}");
            Assert.False(result.HasTocAnomalies, checkHost.DumpReports());
            Assert.Equal(2, check.TOC.Count);
        }
    }

    #endregion

    #region *** Header-less volume: the first set ***

    /// <summary>The two layouts that differ here: TOC in set (the save overwrites the remains) vs TOC in partition.</summary>
    public static TheoryData<DriveProfile> HeaderlessProfiles => [DriveProfile.Setmarks, DriveProfile.Partitions];

    /// <summary>Opens <paramref name="media"/> on a raw drive — no service, no header logic.</summary>
    private static TapeDrive OpenRawDrive(TempVirtualMedia media, DriveProfile profile, FileMode mode)
    {
        bool partitions = profile == DriveProfile.Partitions;
        var backend = VirtualTapeDriveBackend.CreateFileBacked(
            TestLoggerFactory.Default, media.ContentPath, media.ContentCapacity,
            initiatorFilePath: partitions ? media.InitiatorPath : null,
            initiatorCapacity: partitions ? media.InitiatorCapacity : 0,
            partitions ? VirtualTapeDriveCapabilities.WithPartitions : VirtualTapeDriveCapabilities.WithSetmarks,
            mode);
        var drive = new TapeDrive(TestLoggerFactory.Default, backend);
        Assert.True(drive.ReopenDrive(0), "raw: ReopenDrive");
        Assert.True(drive.ReloadMedia(), "raw: ReloadMedia");
        Assert.True(drive.PrepareMedia(), "raw: PrepareMedia");
        return drive;
    }

    /// <summary>
    /// Manufactures a HEADER-LESS volume holding no set — only an empty TOC, where the layout keeps it.
    /// </summary>
    /// <remarks>
    /// Partitions: <c>FormatMedia</c> needs the initiator size — with −1 the drive formats ONE partition, and the TOC lands
    ///  in the content partition, where only the full walk finds it.
    /// </remarks>
    private static void ManufactureHeaderlessEmptyMedia(TempVirtualMedia media, DriveProfile profile)
    {
        using var drive = OpenRawDrive(media, profile, FileMode.Create);
        Assert.True(drive.FormatMedia(profile == DriveProfile.Partitions ? media.InitiatorCapacity : -1L),
            "headerless: FormatMedia");
        using var agent = new TapeFileBackupAgent(drive, new TapeTOC("Header-less media")) { WritesMediaHeader = false };
        Assert.True((bool)agent.BackupTOC(), "headerless: BackupTOC");
    }

    /// <summary>
    /// SERVICE: a header-less volume holding no set is HEADED by the first backup, like a blank cartridge — so its first set
    ///  stands behind the header's filemark after all: the set that takes no file is erased behind it, the header stays.
    ///  The TOC lists no set; the next append becomes set #1 and restores.
    /// </summary>
    /// <remarks>
    /// Pins why the "no mark before the first set" branch of <c>EraseContentFrom</c> is unreachable through the service: a
    ///  header-less volume WITH sets gets no header, but then its new set is not the first.
    /// </remarks>
    [Theory]
    [MemberData(nameof(HeaderlessProfiles))]
    public async Task HeaderlessEmptyVolume_FirstSetTakesNoFile_VolumeHeaded_SetErased(DriveProfile profile)
    {
        using var media = NewMedia(profile);
        using var next = Tree("next");
        ManufactureHeaderlessEmptyMedia(media, profile);

        var (svc, host) = await ReopenAsync(media, profile: profile);
        using (svc)
        {
            Assert.Null(svc.LoadedMediaHeader);                          // precondition: genuinely header-less
            Assert.Equal(0, svc.TOC!.Count);
            AssertNoMediaPrompts(host);                                  // the TOC reads where the layout keeps it

            var result = await BackupVanishingFileAsync(svc);
            Assert.False(result.HasTocAnomalies, host.DumpReports());
            Assert.Equal(0, svc.TOC!.Count);
        }

        var (svc2, host2) = await ReopenAsync(media, profile: profile);
        using (svc2)
        {
            Assert.NotNull(svc2.LoadedMediaHeader);                      // headed on the way
            Assert.Equal(svc2.TOC!.MediaId, svc2.LoadedMediaHeader!.MediaId);
            Assert.Equal(0, svc2.TOC.Count);
            var result = await svc2.ExecuteBackupAsync(Req(svc2, next.RootPath, "Next")).WaitAsync(NoHang);
            Assert.True(result.Success, $"{result.Message}\n{host2.DumpReports()}");
            Assert.False(result.HasTocAnomalies, host2.DumpReports());
        }

        var (check, checkHost) = await ReopenAsync(media, profile: profile);
        using (check)
        {
            Assert.Equal(1, check.TOC!.Count);
            await AssertSetRestoresAsync(check, checkHost, media, check.TOC.LastSetOnVolume, next, "r");
        }
    }

    /// <summary>
    /// AGENT (the only way to reach the branch): a raw agent that writes no media header backs up a vanished file as the
    ///  first set of a header-less volume. No mark precedes the set:
    /// <list type="bullet">
    ///  <item><b>TOC in set</b> — the erase settles at begin-of-content; the TOC save overwrites the remains.</item>
    ///  <item><b>TOC in partition</b> — the erase is REFUSED: the set stays on tape, recorded with its end; the TOC saved
    ///   behind it matches the tape, and a later service append lands behind it.</item>
    /// <item>No set header either way: the "set headers" flag lives in the media header, which this volume lacks — the set is
    ///  just its closing mark at block 0.</item>
    /// </list>
    /// </summary>
    [Theory]
    [MemberData(nameof(HeaderlessProfiles))]
    public async Task HeaderlessVolume_RawAgent_FirstSetTakesNoFile_ErasedInSet_KeptInPartition(DriveProfile profile)
    {
        bool partitions = profile == DriveProfile.Partitions;
        using var media = NewMedia(profile);
        using var next = Tree("next");
        using var gone = Tree("gone", count: 1);
        ManufactureHeaderlessEmptyMedia(media, profile);
        int marksBefore = await CountContentMarksAsync(media, profile);

        // ── The failed backup, raw: the source is gone before the agent opens it ──
        using (var drive = OpenRawDrive(media, profile, FileMode.Open))
        using (var agent = new TapeFileBackupAgent(drive, new TapeTOC("Header-less media")) { WritesMediaHeader = false })
        {
            Assert.True((bool)agent.RestoreTOC(), "raw: RestoreTOC");
            var toc = agent.TOC;
            toc.AddNewSetTOC(0);
            toc.CurrentSetTOC.Description = "Vanishing";
            toc.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc32;
            toc.CurrentSetTOC.BlockSize = drive.DefaultBlockSize > 0 ? drive.DefaultBlockSize : FallbackBlockSize;
            string path = gone.Files[0];
            File.Delete(path);

            agent.BackupFileListToCurrentSet(newSet: true, [path], ignoreFailures: true);

            Assert.False(agent.SetHeaderStamped, "header-less media carries no set headers");
            Assert.Empty(toc.CurrentSetTOC);
            Assert.Equal(!partitions, agent.EmptySetErased);
            Assert.Equal(partitions, toc.CurrentSetTOC.IsOnTape);
            if (!partitions)
                toc.RemoveLastEmptySet();                                 // as the service does for an erased set
            Assert.True((bool)agent.BackupTOC(), "raw: BackupTOC");
        }
        Assert.Equal(partitions ? marksBefore + 1 : marksBefore, await CountContentMarksAsync(media, profile));

        // ── The service agrees with the tape, and the next append lands ──
        var (svc, host) = await ReopenAsync(media, profile: profile);
        using (svc)
        {
            Assert.Null(svc.LoadedMediaHeader);
            AssertNoMediaPrompts(host);
            Assert.Equal(partitions ? 1 : 0, svc.TOC!.Count);
            var result = await svc.ExecuteBackupAsync(Req(svc, next.RootPath, "Next")).WaitAsync(NoHang);
            Assert.True(result.Success, $"{result.Message}\n{host.DumpReports()}");
            Assert.False(result.HasTocAnomalies, host.DumpReports());
        }

        var (check, checkHost) = await ReopenAsync(media, profile: profile);
        using (check)
        {
            Assert.Equal(partitions ? 2 : 1, check.TOC!.Count);
            await AssertSetRestoresAsync(check, checkHost, media, check.TOC.LastSetOnVolume, next, "r");
        }
    }

    #endregion
}
