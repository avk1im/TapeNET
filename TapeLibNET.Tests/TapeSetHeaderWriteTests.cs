using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;

/// <summary>
/// Step 4 coverage for the set-header WRITE path.
/// <para>
/// The organising claim under test is that Step 4 is <b>independently revertible</b>: headed tapes carry
///  set headers, and the restore path — entirely unmodified — neither reads them nor trips over them,
///  because every restore positions absolutely at <c>tfi.Address</c>, which the packer stamps one block
///  past the header.
/// </para>
/// </summary>
public class TapeSetHeaderWriteTests
{
    #region *** Test Data ***

    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];

    #endregion

    #region *** Helpers ***

    /// <summary>Best-effort cleanup of a temporary restore directory.</summary>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return;

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                var attrs = File.GetAttributes(file);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            }
            Directory.Delete(path, recursive: true);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Positions at the given on-volume set and reads its first block as a set header, returning
    ///  <see langword="null"/> when the block does not classify as one.
    /// </summary>
    /// <remarks>
    /// Deliberately built from the Step 3 manager primitives rather than any Step 5 agent code — these
    ///  tests must not depend on the verification path they precede.
    /// </remarks>
    private static TapeSetHeader? ReadSetHeaderOnVolume(VirtualTapeFixture fixture, int setIndexOnVolume)
    {
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);

        agent.ReadBomHeader();                       // resolves presence; parks the navigator AtBomHeader
        Assert.True(agent.Manager.EndReadWrite());

        agent.Navigator.TargetContentSet = setIndexOnVolume;
        Assert.True(agent.Manager.BeginReadContent(), $"Failed to position at set {setIndexOnVolume}");

        var buffer = new byte[TapeHeaderBlock.Size];
        int read = agent.Manager.ReadSetHeaderBlock(buffer);
        if (read <= 0)
            return null;

        return TapeHeaderBlock.Classify(buffer, read) as TapeSetHeader;
    }

    /// <summary>Reads the media header back from BOM.</summary>
    private static TapeMediaHeader? ReadMediaHeader(VirtualTapeFixture fixture)
    {
        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);
        return agent.ReadBomHeader() as TapeMediaHeader;
    }

    /// <summary>Backs up one small set and saves the TOC, using the fixture's configured header flags.</summary>
    private static void BackupOneSet(VirtualTapeFixture fixture, TempFileTree tree, string description)
    {
        fixture.BackupFiles(tree.Files, description: description);
    }

    #endregion

    #region *** (A) The header lands, and says the right thing ***

    /// <summary>
    /// The core of Step 4: a headed set carries a set header at its very first block, and that header
    ///  round-trips with the indices the TOC would compute.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void SetHeader_WrittenAtSetStart_RoundTrips(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("sh", count: 3, minSize: 512, maxSize: 4 * 1024);

        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        BackupOneSet(fixture, tree, "Headed set");

        var header = ReadSetHeaderOnVolume(fixture, setIndexOnVolume: 0);

        Assert.NotNull(header);
        Assert.Equal(fixture.TOC.MediaId, header!.MediaId);
        Assert.Equal(fixture.TOC.Volume, header.Volume);
        Assert.Equal(0, header.VolumeSetIndex);
        Assert.Equal(fixture.TOC.CurrentSetIndex, header.GlobalSetIndex);
        Assert.Equal(fixture.TOC.CurrentSetTOC.BlockSize, header.SetBlockSize);
        Assert.Equal("Headed set", header.Description);
    }

    /// <summary>
    /// The media header must DECLARE what the volume carries (SH-1) — otherwise a reader would have to
    ///  probe, which the whole presence model exists to avoid.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MediaHeader_DeclaresSetHeaderPresence(DriveProfile profile)
    {
        using var headed = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        using var mediaOnly = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: false);

        Assert.True(ReadMediaHeader(headed)?.HasSetHeaders);
        Assert.False(ReadMediaHeader(mediaOnly)?.HasSetHeaders);
    }

    /// <summary>
    /// The navigator's cached expectation must match the tape, since Step 5 gates its read on it.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void NavigatorExpectation_MatchesTheMedia(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);

        using var agent = new TapeFileAgent(fixture.Drive, fixture.TOC);
        agent.ReadBomHeader();

        Assert.Equal(TapeHeaderPresence.Present, agent.Navigator.MediaHeaderPresence);
        Assert.True(agent.Navigator.SetHeadersExpected);
    }

    #endregion

    #region *** (B) Addressing — the header's block cost lands where it should ***

    /// <summary>
    /// The first file sits exactly ONE block past the set start.
    /// </summary>
    /// <remarks>
    /// This is simultaneously the SH-4 assertion. <c>EnsurePackerCreated</c> anchors on
    ///  <c>Drive.CurrentBlock</c>, so a redundant second positioning between the header write and the
    ///  packer's construction would move the anchor and shift every address in the set. Getting the
    ///  expected block exactly right therefore proves the hoisted <c>MoveToTargetContentSet</c> made the
    ///  manager's later call a genuine no-op.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void FirstFileAddress_SitsOneBlockPastSetStart(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("addr", count: 2, minSize: 256, maxSize: 2 * 1024);

        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        BackupOneSet(fixture, tree, "Addressing");

        var first = fixture.TOC.CurrentSetTOC[0];
        Assert.NotNull(first);

        Assert.Equal(fixture.FirstContentBlock + 1, first!.Address.Block);
        Assert.Equal(fixture.FirstFileBlock, first.Address.Block);   // fixture arithmetic agrees
        Assert.Equal(0u, first.Address.Offset);
    }

    /// <summary>The un-headed counterpart: without a set header the first file starts AT the set start.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void WithoutSetHeaders_FirstFileAddress_IsAtSetStart(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("addr0", count: 2, minSize: 256, maxSize: 2 * 1024);

        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: false);
        BackupOneSet(fixture, tree, "No set header");

        var first = fixture.TOC.CurrentSetTOC[0];
        Assert.NotNull(first);
        Assert.Equal(fixture.FirstContentBlock, first!.Address.Block);
    }

    #endregion

    #region *** (C) Restore is untouched — the revertibility claim ***

    /// <summary>
    /// The claim Step 4 rests on: a headed tape restores byte-for-byte through completely unmodified
    ///  restore code. The header block is never addressed, because every restore seeks absolutely to
    ///  <c>tfi.Address</c>, which sits past it.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void HeadedBackup_RestoresByteForByte(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("rt", count: 6, minSize: 512, maxSize: 16 * 1024);

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_SHRt_{Guid.NewGuid():N}");
        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            BackupOneSet(fixture, tree, "Round trip");

            using var agent = fixture.CreateRestoreAgent(restoreDir);
            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false),
                "Headed restore must succeed with the restore path unmodified");

            string restoreEquivalent = Path.Combine(
                restoreDir, Path.GetRelativePath(Path.GetPathRoot(tree.RootPath)!, tree.RootPath));
            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, restoreEquivalent);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// CRC validation over the whole set — a stricter check than a file compare, since it proves every
    ///  delivered byte is the byte that was written, not merely that the files match on disk.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void HeadedBackup_ValidatesCleanly(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddEdgeCases();

        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        BackupOneSet(fixture, tree, "Edge cases");

        using var agent = fixture.CreateValidateAgent();
        Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false),
            "CRC validation must pass on a headed set");
    }

    #endregion

    #region *** (D) Multi-set ***

    /// <summary>
    /// Every set carries its own header with its own on-volume index — the property the whole
    ///  verified-navigation feature will rest on.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MultiSet_EachSetCarriesItsOwnHeader(DriveProfile profile)
    {
        using var tree1 = new TempFileTree();
        tree1.AddFiles("s1", count: 2, minSize: 256, maxSize: 2 * 1024);
        using var tree2 = new TempFileTree();
        tree2.AddFiles("s2", count: 3, minSize: 256, maxSize: 2 * 1024);
        using var tree3 = new TempFileTree();
        tree3.AddFiles("s3", count: 2, minSize: 256, maxSize: 2 * 1024);

        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        BackupOneSet(fixture, tree1, "Set one");
        BackupOneSet(fixture, tree2, "Set two");
        BackupOneSet(fixture, tree3, "Set three");

        Assert.Equal(3, fixture.TOC.Count);

        var descriptions = new[] { "Set one", "Set two", "Set three" };
        for (int onVolume = 0; onVolume < 3; onVolume++)
        {
            var header = ReadSetHeaderOnVolume(fixture, onVolume);

            Assert.NotNull(header);
            Assert.Equal(onVolume, header!.VolumeSetIndex);
            Assert.Equal(onVolume + 1, header.GlobalSetIndex);       // 1-based standard index
            Assert.Equal(fixture.TOC.MediaId, header.MediaId);
            Assert.Equal(descriptions[onVolume], header.Description);
        }
    }

    /// <summary>A multi-set headed tape must still restore every set byte-for-byte.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MultiSet_RestoresByteForByte(DriveProfile profile)
    {
        using var tree1 = new TempFileTree();
        tree1.AddFiles("m1", count: 3, minSize: 512, maxSize: 8 * 1024);
        using var tree2 = new TempFileTree();
        tree2.AddFiles("m2", count: 3, minSize: 512, maxSize: 8 * 1024);

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_SHMs_{Guid.NewGuid():N}");
        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            BackupOneSet(fixture, tree1, "First");
            BackupOneSet(fixture, tree2, "Second");

            // Restore the OLDEST set — forces a positioning that crosses the newer set's header.
            fixture.TOC.CurrentSetIndex = 1;

            using var agent = fixture.CreateRestoreAgent(restoreDir);
            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false));

            string restoreEquivalent = Path.Combine(
                restoreDir, Path.GetRelativePath(Path.GetPathRoot(tree1.RootPath)!, tree1.RootPath));
            FileComparer.AssertFilesMatch(tree1.RootPath, tree1.Files, restoreEquivalent);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** (E) Negative cases — no header where none may go ***

    /// <summary>
    /// SH-1's write half: a legacy volume carries no media header, so nothing could declare a set header
    ///  — and none is written, whatever the agent's flag says. The set's first block must therefore be
    ///  file content, not a classifiable header.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void LegacyMedia_WritesNoSetHeader_EvenWhenRequested(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("legacy", count: 2, minSize: 256, maxSize: 2 * 1024);

        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: false);

        // Configure the set exactly as the fixture would, but drive the agent directly so the flag can
        //  be forced ON against header-less media — the combination the fixture itself rejects.
        fixture.TOC.AddNewSetTOC(0, incremental: false);
        fixture.TOC.CurrentSetTOC.Description = "Legacy set";
        fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;

        using (var agent = new TapeFileBackupAgent(fixture.Drive, fixture.TOC)
        {
            WritesMediaHeader = false,
            WritesSetHeaders = true,          // asks for one — must be refused by the presence gate
        })
        {
            Assert.True(agent.BackupFileListToCurrentSet(newSet: true, tree.Files, ignoreFailures: false));
            Assert.True(agent.BackupTOC());
        }

        // The first file must start at block 0: no media header, no set header, nothing skipped.
        var first = fixture.TOC.CurrentSetTOC[0];
        Assert.NotNull(first);
        Assert.Equal(0L, first!.Address.Block);
    }

    /// <summary>
    /// The <c>_MediaOnly</c> shape — media written by a release that heads the cartridge but writes no
    ///  set headers. It must restore silently, which is the entire justification for the
    ///  <c>HasSetHeaders</c> flag.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MediaOnly_RestoresSilently(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("mo", count: 4, minSize: 512, maxSize: 8 * 1024);

        string restoreDir = Path.Combine(Path.GetTempPath(), $"TapeNET_SHMo_{Guid.NewGuid():N}");
        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: false);
            BackupOneSet(fixture, tree, "Media only");

            // Nothing classifiable sits at the set start — the first block is file content.
            Assert.Null(ReadSetHeaderOnVolume(fixture, setIndexOnVolume: 0));

            using var agent = fixture.CreateRestoreAgent(restoreDir);
            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false));

            string restoreEquivalent = Path.Combine(
                restoreDir, Path.GetRelativePath(Path.GetPathRoot(tree.RootPath)!, tree.RootPath));
            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, restoreEquivalent);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// SH-1 rejects the impossible combination at construction rather than downgrading it silently —
    ///  a fixture that quietly dropped the request would make Step 8's matrix lie.
    /// </summary>
    [Fact]
    public void Fixture_SetHeadersWithoutMediaHeader_Throws()
        => Assert.Throws<ArgumentException>(() =>
            new VirtualTapeFixture(DriveProfile.Setmarks, withMediaHeader: false, withSetHeaders: true));

    #endregion

    #region *** (F) Multi-volume ***

    /// <summary>
    /// A continuation set is the FIRST set on its volume, so its header must record
    ///  <c>VolumeSetIndex == 0</c> while the global index keeps climbing — the arithmetic Step 2 pinned
    ///  in memory, now proven on tape.
    /// </summary>
    [Fact]
    public void MultiVolume_ContinuationSet_HeaderRecordsOnVolumeIndexZero()
    {
        using var tree = new TempFileTree();
        tree.AddFiles("mv", count: 24, minSize: 8 * 1024, maxSize: 24 * 1024); // must exceed 1 volume of 256KiB default capacity

        using var fixture = new MultiVolumeVirtualTapeFixture(
            DriveProfile.Setmarks,
            headerMode: VolumeHeaderMode.All);

        fixture.BackupFiles(tree.Files, description: "Spanning");

        Assert.True(fixture.TotalVolumes > 1, "the fixture must actually have spanned volumes");

        // The set now loaded is the continuation set on the final volume.
        var setTOC = fixture.TOC.CurrentSetTOC;
        Assert.True(setTOC.ContinuedFromPrevVolume, "expected a continuation set");
        Assert.Equal(0, fixture.TOC.CurrentSetIndexOnVolume);
        Assert.True(fixture.TOC.CurrentSetIndex > 1, "global index must keep climbing");
    }

    #endregion
}
