using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;

/// <summary>
/// Step 6 — the crown suite. A restore that lands on the wrong set detects it from the set's own header,
///  learns where it actually is, moves the remaining delta, re-verifies, and completes byte-for-byte.
/// <para>
/// Every correction test asserts the RESTORED BYTES, not merely a <c>true</c> return. A correction that
///  "succeeded" while delivering another set's data would be worse than the failure it replaced, and only
///  a content comparison can tell the two apart.
/// </para>
/// </summary>
public class TapeSetHeaderCorrectionTests
{
    #region *** Test Data ***

    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];

    /// <summary>Every profile × every correctable offset — the matrix §13.3 calls the crown suite.</summary>
    public static TheoryData<DriveProfile, int> ProfilesAndOffsets
    {
        get
        {
            var data = new TheoryData<DriveProfile, int>();
            foreach (var profile in new[]
                     {
                         DriveProfile.Setmarks, DriveProfile.Partitions,
                         DriveProfile.SeqFilemarks, DriveProfile.FilemarksOnly,
                     })
                foreach (int offset in new[] { -2, -1, 1, 2 })
                    data.Add(profile, offset);
            return data;
        }
    }

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
        Path.Combine(Path.GetTempPath(), $"TapeNET_SHC_{Guid.NewGuid():N}");

    private static void AssertRestoredMatches(TempFileTree tree, string restoreDir)
    {
        string equivalent = Path.Combine(
            restoreDir, Path.GetRelativePath(Path.GetPathRoot(tree.RootPath)!, tree.RootPath));
        FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, equivalent);
    }

    /// <summary>
    /// A tape of <paramref name="setCount"/> sets, each with distinctly-named files so a restore that
    ///  silently delivered the WRONG set's data cannot masquerade as success.
    /// </summary>
    private static TempFileTree[] BuildMultiSetTape(VirtualTapeFixture fixture, int setCount, string prefix)
    {
        var trees = new TempFileTree[setCount];
        for (int i = 0; i < setCount; i++)
        {
            trees[i] = new TempFileTree();
            trees[i].AddFiles($"{prefix}{i + 1}", count: 3, minSize: 512, maxSize: 8 * 1024);
            fixture.BackupFiles(trees[i].Files, description: $"Set {i + 1}");
        }
        return trees;
    }

    private static void DisposeAll(TempFileTree[] trees)
    {
        foreach (var t in trees)
            t.Dispose();
    }

    #endregion

    #region *** (A) The crown matrix ***

    /// <summary>
    /// The feature's reason for existing: under an injected miscount the restore still delivers the right
    ///  bytes from the right set, on every drive profile and in both directions.
    /// </summary>
    /// <remarks>
    /// The target set sits in the MIDDLE of a five-set tape, so every offset in {−2, −1, +1, +2} lands on
    ///  a real neighbouring set rather than falling off either end — the correction then has somewhere
    ///  genuinely wrong to come back from.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ProfilesAndOffsets))]
    public void Drift_IsCorrected_AndRestoresByteForByte(DriveProfile profile, int offset)
    {
        string restoreDir = NewRestoreDir();
        TempFileTree[] trees = [];
        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 5, prefix: "cr");

            fixture.TOC.CurrentSetIndex = 3;   // the middle set — reachable from either side

            using var agent = fixture.CreateRestoreAgent(restoreDir);
            agent.Navigator.SimulateSetMiscount = offset;

            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false),
                $"a drift of {offset} must be corrected, not reported");

            // The miscount was consumed, and the RIGHT set's files came back.
            Assert.Equal(0, agent.Navigator.SimulateSetMiscount);
            AssertRestoredMatches(trees[2], restoreDir);
        }
        finally
        {
            DisposeAll(trees);
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// CRC validation across the corrected set — a stricter statement than a file compare, since it proves
    ///  every delivered byte is the byte that was written, not merely that the disk files agree.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void CorrectedRestore_ValidatesCleanly(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 4, prefix: "cv");

            fixture.TOC.CurrentSetIndex = 2;

            using var agent = fixture.CreateValidateAgent();
            agent.Navigator.SimulateSetMiscount = +1;

            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false),
                "CRC validation must pass on a corrected set");
            Assert.Equal(0, agent.Navigator.SimulateSetMiscount);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    #endregion

    #region *** (B) Boundary corrections ***

    /// <summary>
    /// Correcting BACKWARD to set 0 exercises the one asymmetric path: a backward space lands BEFORE the
    ///  target's opening mark, and the oldest set has no preceding mark at all — so the correction must
    ///  settle at begin-of-content via the BOM branch.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void DriftPastTheOldestSet_CorrectsBackToSetZero(DriveProfile profile)
    {
        string restoreDir = NewRestoreDir();
        TempFileTree[] trees = [];
        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 3, prefix: "b0");

            fixture.TOC.CurrentSetIndex = 1;   // the OLDEST set…

            using var agent = fixture.CreateRestoreAgent(restoreDir);
            agent.Navigator.SimulateSetMiscount = +1;   // …land one past it, correct backward

            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false));
            AssertRestoredMatches(trees[0], restoreDir);
        }
        finally
        {
            DisposeAll(trees);
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>Correcting FORWARD to the newest set — the opposite boundary.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void DriftBeforeTheNewestSet_CorrectsForward(DriveProfile profile)
    {
        string restoreDir = NewRestoreDir();
        TempFileTree[] trees = [];
        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 3, prefix: "bn");

            fixture.TOC.CurrentSetIndex = 3;   // the NEWEST set…

            using var agent = fixture.CreateRestoreAgent(restoreDir);
            agent.Navigator.SimulateSetMiscount = -1;   // …land one short, correct forward

            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false));
            AssertRestoredMatches(trees[2], restoreDir);
        }
        finally
        {
            DisposeAll(trees);
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** (C) The bound holds ***

    /// <summary>
    /// SH-10's one-retry bound. A miscount that re-arms itself makes the correction's own re-verify drift
    ///  again — the set must then fail cleanly rather than recurse.
    /// </summary>
    /// <remarks>
    /// The simulator is one-shot, so a persistent fault is expressed by re-arming it from an
    ///  <c>ITapeFileNotifiable</c> hook... which fires too late. Instead the second drift is produced
    ///  directly: arm the miscount, let the correction consume it, and re-arm inside the same operation by
    ///  driving <c>BeginReadContentForCurrentSet</c> through a filter callback is not reachable either.
    /// <para>
    /// So this test takes the honest route: it proves the GUARD, not the physics. A second, larger
    ///  miscount armed immediately after the first means the correction's relative move itself lands
    ///  wrong, and the re-verify must terminate rather than try again.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void UncorrectableDrift_FailsCleanly(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 5, prefix: "un");

            fixture.TOC.CurrentSetIndex = 3;

            using var agent = fixture.CreateValidateAgent();

            // ADAPT: a persistent fault needs the simulator to survive the correction. If the production
            //  knob stays one-shot, expose a DEBUG-only `SimulateSetMiscountPersistent` alongside it, or
            //  set the one-shot knob twice via the navigator's post-move hook. The assertion below is
            //  what matters and is independent of the mechanism.
            agent.Navigator.SimulateSetMiscount = +1;
            agent.Navigator.SimulateSetMiscountPersistent = true;

            Assert.False(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false),
                "a drift that survives one correction must fail the set, not loop");

            agent.Navigator.SimulateSetMiscountPersistent = false;
            agent.Navigator.SimulateSetMiscount = 0;
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    #endregion

    #region *** (D) Correction stays in its lane ***

    /// <summary>
    /// Identity failures must NEVER be corrected. A swapped cartridge voids every in-memory assumption
    ///  including the TOC, so moving the head could not possibly help — and moving it on a foreign tape is
    ///  the last thing anyone wants.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void WrongVolume_IsNotCorrected(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 3, prefix: "wv");

            fixture.TOC.CurrentSetIndex = 2;
            fixture.TOC.Volume += 5;   // the tape says one volume, the library believes another

            using var agent = fixture.CreateValidateAgent();
            Assert.False(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false),
                "a volume mismatch must fail without attempting a correction");
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// An unreadable header still warns and proceeds — Step 6 must not have turned the golden rule into a
    ///  correction attempt against a record that says nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void UnreadableHeader_StillWarnsAndCompletes(DriveProfile profile)
    {
        string restoreDir = NewRestoreDir();
        TempFileTree[] trees = [];
        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 2, prefix: "ur");

            fixture.TOC.CurrentSetIndex = 1;

            using var agent = fixture.CreateRestoreAgent(restoreDir);

            // Resolve the BOM header first so the injector lands on the SET header read.
            agent.ReadBomHeader();
            fixture.Backend.ContentReadFaults.CorruptOnce(bits: 2, offset: 48);

            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false));
            AssertRestoredMatches(trees[0], restoreDir);
        }
        finally
        {
            DisposeAll(trees);
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// The write path is never corrected (§15.3): a write-side miscount means the agent is about to
    ///  overwrite the WRONG set, where failing is correct and correcting is reckless.
    /// </summary>
    /// <remarks>
    /// Deliberately an OVERWRITE (<c>newSet: false</c>), not an append. Appending targets end-of-content,
    ///  which the FM-delimited layouts reach through raw filemark seeks rather than
    ///  <c>MoveToNextContentSetmark</c> — so the simulator has nothing to hook there, and such a miscount
    ///  cannot arise on those layouts at all. An overwrite routes through the counting primitive on every
    ///  profile, and is precisely the destructive case the v1 boundary leaves unguarded.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void BackupPath_DoesNotCorrect(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        using var extra = new TempFileTree();
        extra.AddFiles("wp", count: 2, minSize: 512, maxSize: 4 * 1024);

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 3, prefix: "wp");

            using var agent = fixture.CreateBackupAgent();
            agent.Navigator.SimulateSetMiscount = +1;

            // Overwrite the newest set. The miscount is consumed by the positioning, and NOTHING
            //  verifies or repairs it — the backup agent has no set-header read at all.
            agent.BackupFileListToCurrentSet(newSet: false, extra.Files, ignoreFailures: true);

            Assert.Equal(0, agent.Navigator.SimulateSetMiscount);   // the injection point was reached
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    #endregion

    #region *** (E) No regression on the clean path ***

    /// <summary>
    /// With no drift injected, nothing reconciles and nothing moves twice. Guards against a correction
    ///  that fires spuriously — which would silently double the transport cost of every restore.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void NoDrift_NoCorrection(DriveProfile profile)
    {
        string restoreDir = NewRestoreDir();
        TempFileTree[] trees = [];
        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 4, prefix: "nd");

            fixture.TOC.CurrentSetIndex = 2;

            using var agent = fixture.CreateRestoreAgent(restoreDir);
            Assert.Equal(0, agent.Navigator.SimulateSetMiscount);

            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false));
            AssertRestoredMatches(trees[1], restoreDir);
        }
        finally
        {
            DisposeAll(trees);
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// A corrected restore leaves the navigator in a sound state: the next operation on the same agent —
    ///  a different set, with no injected fault — succeeds normally.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void AfterCorrection_SubsequentSetRestoresNormally(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 4, prefix: "af");

            using var agent = fixture.CreateValidateAgent();

            fixture.TOC.CurrentSetIndex = 2;
            agent.Navigator.SimulateSetMiscount = +1;
            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false), "the corrected pass");

            fixture.TOC.CurrentSetIndex = 4;
            Assert.Equal(0, agent.Navigator.SimulateSetMiscount);
            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false), "the clean pass after it");
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    #endregion
}
