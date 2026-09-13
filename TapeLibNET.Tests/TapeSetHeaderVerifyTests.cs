using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;

/// <summary>
/// Step 5 coverage for set-header VERIFICATION on the restore path.
/// <para>
/// The feature's promise is that a headed restore is <b>checked, not trusted</b>. These tests therefore
///  come in pairs: each verdict is exercised for what it does to the restore (proceed / fail), and the
///  happy path is exercised for what it must NOT do — consume a block, move the head, or slow anything
///  down.
/// </para>
/// </summary>
public class TapeSetHeaderVerifyTests
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

    private static string NewRestoreDir() =>
        Path.Combine(Path.GetTempPath(), $"TapeNET_SHV_{Guid.NewGuid():N}");

    private static void AssertRestoredMatches(TempFileTree tree, string restoreDir)
    {
        string equivalent = Path.Combine(
            restoreDir, Path.GetRelativePath(Path.GetPathRoot(tree.RootPath)!, tree.RootPath));
        FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, equivalent);
    }

    /// <summary>
    /// Resolves the media header up front, so a subsequently armed read-fault injector lands on the SET
    ///  header read rather than on the BOM header read that <c>EnsureMediaHeaderResolved</c> performs.
    /// </summary>
    private static void PreResolveMediaHeader(TapeFileAgent agent)
    {
        agent.ReadBomHeader();
        Assert.Equal(TapeHeaderPresence.Present, agent.Navigator.MediaHeaderPresence);
        Assert.True(agent.Navigator.SetHeadersExpected);
    }

    /// <summary>A set header carrying whatever the caller wants to disagree about.</summary>
    private static TapeSetHeader MakeHeader(
        Guid mediaId, int volume = 1, int volumeSetIndex = 0, int globalSetIndex = 1,
        uint blockSize = 64 * 1024) =>
        new()
        {
            MediaId = mediaId,
            CreatedUtc = new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc),
            SetBlockSize = blockSize,
            Volume = volume,
            VolumeSetIndex = volumeSetIndex,
            GlobalSetIndex = globalSetIndex,
            Description = "Verify",
        };

    #endregion

    #region *** (A) The ladder, in memory ***

    //  ClassifySetHeader is pure, so every verdict is reachable here without fabricating a foreign
    //   cartridge on tape — including the two that describe a tape which is not the tape we think it is.

    [Fact]
    public void Classify_MatchingHeader_IsMatch()
    {
        using var tree = new TempFileTree();
        tree.AddFiles("cls", count: 2, minSize: 256, maxSize: 2 * 1024);

        using var fixture = new VirtualTapeFixture(DriveProfile.Setmarks, withMediaHeader: true, withSetHeaders: true);
        fixture.BackupFiles(tree.Files, description: "Classify");

        using var agent = fixture.CreateValidateAgent();
        var header = fixture.TOC.CreateSetHeaderForCurrentSet();

        Assert.Equal(TapeSetHeaderVerdict.Match, agent.ClassifySetHeader(header));
    }

    [Fact]
    public void Classify_NullHeader_IsUnreadable()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.Setmarks, withMediaHeader: true, withSetHeaders: true);
        using var agent = fixture.CreateValidateAgent();

        Assert.Equal(TapeSetHeaderVerdict.Unreadable, agent.ClassifySetHeader(null));
    }

    /// <summary>
    /// A foreign media id outranks every positional check: a swapped cartridge must never be mistaken for
    ///  a navigation miscount, because correcting the navigation could not possibly help.
    /// </summary>
    [Fact]
    public void Classify_ForeignMediaId_IsWrongMedia()
    {
        using var tree = new TempFileTree();
        tree.AddFiles("wm", count: 2, minSize: 256, maxSize: 2 * 1024);

        using var fixture = new VirtualTapeFixture(DriveProfile.Setmarks, withMediaHeader: true, withSetHeaders: true);
        fixture.BackupFiles(tree.Files, description: "Wrong media");

        using var agent = fixture.CreateValidateAgent();

        // Wrong on EVERY axis — the verdict must still name the identity failure.
        var foreign = MakeHeader(Guid.NewGuid(), volume: 99, volumeSetIndex: 7, globalSetIndex: 7);

        Assert.Equal(TapeSetHeaderVerdict.WrongMedia, agent.ClassifySetHeader(foreign));
    }

    [Fact]
    public void Classify_WrongVolume_IsWrongVolume()
    {
        using var tree = new TempFileTree();
        tree.AddFiles("wv", count: 2, minSize: 256, maxSize: 2 * 1024);

        using var fixture = new VirtualTapeFixture(DriveProfile.Setmarks, withMediaHeader: true, withSetHeaders: true);
        fixture.BackupFiles(tree.Files, description: "Wrong volume");

        using var agent = fixture.CreateValidateAgent();
        var header = fixture.TOC.CreateSetHeaderForCurrentSet() with { Volume = fixture.TOC.Volume + 5 };

        Assert.Equal(TapeSetHeaderVerdict.WrongVolume, agent.ClassifySetHeader(header));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(-1)]
    public void Classify_WrongOnVolumeIndex_IsSetIndexDrift(int delta)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("dr", count: 2, minSize: 256, maxSize: 2 * 1024);

        using var fixture = new VirtualTapeFixture(DriveProfile.Setmarks, withMediaHeader: true, withSetHeaders: true);
        fixture.BackupFiles(tree.Files, description: "Drift");

        using var agent = fixture.CreateValidateAgent();
        var header = fixture.TOC.CreateSetHeaderForCurrentSet();
        var drifted = header with { VolumeSetIndex = header.VolumeSetIndex + delta };

        Assert.Equal(TapeSetHeaderVerdict.SetIndexDrift, agent.ClassifySetHeader(drifted));
    }

    /// <summary>
    /// Advisory fields never gate (SH-11): a header disagreeing on attribution or block size still
    ///  matches, because neither drives navigation and both can legitimately diverge after a TOC import.
    /// </summary>
    [Fact]
    public void Classify_AdvisoryFieldsDiffer_StillMatches()
    {
        using var tree = new TempFileTree();
        tree.AddFiles("adv", count: 2, minSize: 256, maxSize: 2 * 1024);

        using var fixture = new VirtualTapeFixture(DriveProfile.Setmarks, withMediaHeader: true, withSetHeaders: true);
        fixture.BackupFiles(tree.Files, description: "Advisory");

        using var agent = fixture.CreateValidateAgent();
        var header = fixture.TOC.CreateSetHeaderForCurrentSet() with
        {
            GlobalSetIndex = 42,
            SetBlockSize = 256 * 1024,
        };

        Assert.Equal(TapeSetHeaderVerdict.Match, agent.ClassifySetHeader(header));
    }

    /// <summary>
    /// A TOC with no media id predates identity stamping — i.e. legacy media, which by definition carries
    ///  no media header either. Skipping the identity check keeps such media restorable; the positional
    ///  checks, the ones that carry the feature, still apply.
    /// </summary>
    /// <remarks>
    /// Deliberately a HEADER-LESS fixture: writing a media header mints the id via
    ///  <c>TapeTOC.EnsureMediaId</c> — rightly so, since headed media must be identifiable — which would
    ///  destroy the very precondition under test.
    /// </remarks>
    [Fact]
    public void Classify_TocWithoutMediaId_SkipsIdentityCheck()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.Setmarks, withMediaHeader: false);
        using var agent = fixture.CreateValidateAgent();

        Assert.Equal(Guid.Empty, agent.TOC.MediaId);   // never minted: no header written, no TOC backed up
        agent.TOC.AddNewSetTOC(0, incremental: false);

        // Any media id at all — with no expectation to compare against, identity cannot be judged.
        var header = MakeHeader(Guid.NewGuid(), volume: agent.TOC.Volume, volumeSetIndex: 0, globalSetIndex: 1);

        Assert.Equal(TapeSetHeaderVerdict.Match, agent.ClassifySetHeader(header));
    }

    #endregion

    #region *** (B) The happy path, end to end ***

    /// <summary>
    /// Verification must be invisible when it succeeds: the restore delivers exactly the same bytes as
    ///  before Step 5 existed.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void VerifiedRestore_RestoresByteForByte(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("ok", count: 6, minSize: 512, maxSize: 16 * 1024);

        string restoreDir = NewRestoreDir();
        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            fixture.BackupFiles(tree.Files, description: "Verified");

            using var agent = fixture.CreateRestoreAgent(restoreDir);
            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false),
                "a matching set header must not disturb the restore");

            AssertRestoredMatches(tree, restoreDir);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>Multi-set: every set is verified against its own header, and all restore cleanly.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void VerifiedRestore_MultiSet_EachSetVerifies(DriveProfile profile)
    {
        using var tree1 = new TempFileTree();
        tree1.AddFiles("m1", count: 3, minSize: 512, maxSize: 8 * 1024);
        using var tree2 = new TempFileTree();
        tree2.AddFiles("m2", count: 3, minSize: 512, maxSize: 8 * 1024);
        using var tree3 = new TempFileTree();
        tree3.AddFiles("m3", count: 3, minSize: 512, maxSize: 8 * 1024);

        string restoreDir = NewRestoreDir();
        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            fixture.BackupFiles(tree1.Files, description: "One");
            fixture.BackupFiles(tree2.Files, description: "Two");
            fixture.BackupFiles(tree3.Files, description: "Three");

            // Oldest set — the positioning crosses both newer sets' headers on the way.
            fixture.TOC.CurrentSetIndex = 1;

            using var agent = fixture.CreateRestoreAgent(restoreDir);
            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false));

            AssertRestoredMatches(tree1, restoreDir);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// SH-8, the regression that matters most: a second restore of the SAME set must not read a second
    ///  header. <c>BeginReadContent</c> early-returns without moving, so the head sits mid-set — reading
    ///  there would swallow a content block and corrupt the first file.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void SecondRestoreOfSameSet_ConsumesNoBlock(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("sh8", count: 4, minSize: 512, maxSize: 8 * 1024);

        string restoreDir1 = NewRestoreDir();
        string restoreDir2 = NewRestoreDir();
        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            fixture.BackupFiles(tree.Files, description: "Twice");

            using (var agent = fixture.CreateRestoreAgent(restoreDir1))
            {
                Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false));
                AssertRestoredMatches(tree, restoreDir1);
            }

            // Same agent instance would keep the read session; a fresh one re-positions. Both must work,
            //  and the SECOND restore is the one that would break if SH-8 were violated.
            using (var agent = fixture.CreateRestoreAgent(restoreDir2))
            {
                Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false));
                AssertRestoredMatches(tree, restoreDir2);
            }
        }
        finally
        {
            TryDeleteDirectory(restoreDir1);
            TryDeleteDirectory(restoreDir2);
        }
    }

    /// <summary>
    /// Two restore calls on ONE agent, same set: the second call finds the manager still in
    ///  <c>ReadingContent</c> at the same set, so no positioning happens and no header may be read.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TwoRestoresOnOneAgent_SameSet_Succeed(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("sh8b", count: 3, minSize: 512, maxSize: 4 * 1024);

        string restoreDir = NewRestoreDir();
        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            fixture.BackupFiles(tree.Files, description: "One agent twice");

            using var agent = fixture.CreateValidateAgent();

            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false), "first pass");
            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false), "second pass");
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** (C) NotExpected — header-less media stays first class ***

    /// <summary>
    /// The <c>_MediaOnly</c> shape: headed cartridge, no set headers. No read is attempted at all, which
    ///  is the entire justification for declaring presence in the media header rather than probing.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MediaOnly_NoVerificationAttempted(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("mo", count: 4, minSize: 512, maxSize: 8 * 1024);

        string restoreDir = NewRestoreDir();
        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: false);
            fixture.BackupFiles(tree.Files, description: "Media only");

            using var agent = fixture.CreateRestoreAgent(restoreDir);
            Assert.False(agent.Navigator.SetHeadersExpected || agent.ReadBomHeader() is null,
                "presence must resolve to Present-without-set-headers");
            Assert.False(agent.Navigator.SetHeadersExpected);

            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false));
            AssertRestoredMatches(tree, restoreDir);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>Fully legacy media: no media header, therefore no set headers (SH-1), therefore no read.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void LegacyMedia_NoVerificationAttempted(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("lg", count: 4, minSize: 512, maxSize: 8 * 1024);

        string restoreDir = NewRestoreDir();
        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: false);
            fixture.BackupFiles(tree.Files, description: "Legacy");

            using var agent = fixture.CreateRestoreAgent(restoreDir);
            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false));
            AssertRestoredMatches(tree, restoreDir);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** (D) Unreadable — warn and proceed ***

    /// <summary>
    /// Host-path corruption: the header block reads back full-size with no I/O error, and only the CRC
    ///  knows it is wrong. The restore must complete byte-for-byte — an unverifiable record removes a
    ///  safety net, not the tape's data.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void CorruptedSetHeader_WarnsAndCompletes(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("cor", count: 5, minSize: 512, maxSize: 8 * 1024);

        string restoreDir = NewRestoreDir();
        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            fixture.BackupFiles(tree.Files, description: "Corrupt header");

            using var agent = fixture.CreateRestoreAgent(restoreDir);

            // Resolve the BOM header FIRST, so the injector fires on the SET header read.
            PreResolveMediaHeader(agent);
            fixture.Backend.ContentReadFaults.CorruptOnce(bits: 2, offset: 48);

            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false),
                "an unverifiable set header must not block the restore");
            Assert.Equal(1, fixture.Backend.ContentReadFaults.Occurrences);

            AssertRestoredMatches(tree, restoreDir);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// A genuine read fault on the header block. Beyond "warn and proceed", this pins §0b: the failed
    ///  read reset the content position (SH-6), and the agent must re-anchor — otherwise the later
    ///  set-boundary advance would walk off <c>UnknownSet</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ReadFaultOnSetHeader_ReanchorsAndCompletes(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("rf", count: 5, minSize: 512, maxSize: 8 * 1024);

        string restoreDir = NewRestoreDir();
        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            fixture.BackupFiles(tree.Files, description: "Read fault");

            using var agent = fixture.CreateRestoreAgent(restoreDir);

            PreResolveMediaHeader(agent);
            fixture.Backend.ContentReadFaults.FailOnce();

            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false));
            Assert.Equal(1, fixture.Backend.ContentReadFaults.Occurrences);

            // The re-anchor restored a known position — not the UnknownSet sentinel.
            Assert.NotEqual(TapeNavigator.UnknownSet, agent.Navigator.CurrentContentSet);

            AssertRestoredMatches(tree, restoreDir);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** (E) Failing verdicts ***

    /// <summary>
    /// A volume mismatch fails the set before any file byte is delivered. File addresses are
    ///  physical-per-volume, so proceeding would resolve every address to garbage.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void WrongVolume_FailsTheSet(DriveProfile profile)
    {
        using var tree = new TempFileTree();
        tree.AddFiles("wv", count: 3, minSize: 512, maxSize: 4 * 1024);

        string restoreDir = NewRestoreDir();
        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            fixture.BackupFiles(tree.Files, description: "Volume mismatch");

            // The tape says volume N; make the library believe it loaded volume N + 5.
            fixture.TOC.Volume += 5;

            using var agent = fixture.CreateValidateAgent();
            Assert.False(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false),
                "a volume mismatch must fail the set");
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// The crown scenario for Step 5: the navigator lands on the wrong set while believing it arrived.
    ///  Detected from the set's own header and reported — the correction arrives in Step 6.
    /// </summary>
    /// <remarks>
    /// Disables autocorrection (Step 6) to verify the detect-and-report contract (Step 5).
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void SetIndexDrift_IsDetectedAndFails(DriveProfile profile)
    {
        using var tree1 = new TempFileTree();
        tree1.AddFiles("d1", count: 2, minSize: 512, maxSize: 4 * 1024);
        using var tree2 = new TempFileTree();
        tree2.AddFiles("d2", count: 2, minSize: 512, maxSize: 4 * 1024);
        using var tree3 = new TempFileTree();
        tree3.AddFiles("d3", count: 2, minSize: 512, maxSize: 4 * 1024);

        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        fixture.BackupFiles(tree1.Files, description: "D1");
        fixture.BackupFiles(tree2.Files, description: "D2");
        fixture.BackupFiles(tree3.Files, description: "D3");

        fixture.TOC.CurrentSetIndex = 2;   // aim at the middle set…

        using var agent = fixture.CreateValidateAgent();
        agent.Navigator.SimulateSetMiscount = +1;   // …and land on the third instead
        agent.CorrectsSetNavigation = false; // disable autocorrection (Step 6) to verify detect-and-report contract (Step 5)

        Assert.False(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false),
            "drift must be detected and must fail the set in Step 5");

        // One-shot: the simulator disarmed itself, so a later operation is clean.
        Assert.Equal(0, agent.Navigator.SimulateSetMiscount);
    }

    /// <summary>Backward drift is equally detectable — the header simply reports a lower index.</summary>
    /// <remarks>
    /// Disables autocorrection (Step 6) to verify the detect-and-report contract (Step 5).
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BackwardSetIndexDrift_IsDetectedAndFails(DriveProfile profile)
    {
        using var tree1 = new TempFileTree();
        tree1.AddFiles("b1", count: 2, minSize: 512, maxSize: 4 * 1024);
        using var tree2 = new TempFileTree();
        tree2.AddFiles("b2", count: 2, minSize: 512, maxSize: 4 * 1024);
        using var tree3 = new TempFileTree();
        tree3.AddFiles("b3", count: 2, minSize: 512, maxSize: 4 * 1024);

        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
        fixture.BackupFiles(tree1.Files, description: "B1");
        fixture.BackupFiles(tree2.Files, description: "B2");
        fixture.BackupFiles(tree3.Files, description: "B3");

        fixture.TOC.CurrentSetIndex = 3;   // aim at the newest set…

        using var agent = fixture.CreateValidateAgent();
        agent.Navigator.SimulateSetMiscount = -1;   // …and land one set short
        agent.CorrectsSetNavigation = false; // disable autocorrection (Step 6) to verify detect-and-report contract (Step 5)

        Assert.False(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false));
    }

    /// <summary>
    /// Without set headers the very same miscount goes completely undetected — the restore proceeds and
    ///  silently delivers the wrong set's bytes. This is the failure mode the feature exists to end, and
    ///  the sharpest possible statement of its value.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void WithoutSetHeaders_TheSameDriftGoesUndetected(DriveProfile profile)
    {
        using var tree1 = new TempFileTree();
        tree1.AddFiles("u1", count: 2, minSize: 512, maxSize: 4 * 1024);
        using var tree2 = new TempFileTree();
        tree2.AddFiles("u2", count: 2, minSize: 512, maxSize: 4 * 1024);
        using var tree3 = new TempFileTree();
        tree3.AddFiles("u3", count: 2, minSize: 512, maxSize: 4 * 1024);

        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: false);
        fixture.BackupFiles(tree1.Files, description: "U1");
        fixture.BackupFiles(tree2.Files, description: "U2");
        fixture.BackupFiles(tree3.Files, description: "U3");

        fixture.TOC.CurrentSetIndex = 2;

        using var agent = fixture.CreateValidateAgent();
        agent.Navigator.SimulateSetMiscount = +1;

        // No verdict ladder runs. Whether the CRC happens to catch it downstream is incidental — nothing
        //  reports the actual fault, which is that the head is at the wrong set.
        agent.RestoreAllFilesFromCurrentSet(ignoreFailures: true);

        Assert.Equal(0, agent.Navigator.SimulateSetMiscount);   // the miscount did happen
    }

    #endregion
}
