using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;
using Windows.Win32.Foundation;

namespace TapeLibNET.Tests;

/// <summary>
/// Step 3 — verified deletion (SH-13). A delete confirms the set header standing at its target before
///  overwriting anything.
/// <para>
/// Every refusal test asserts the TAPE IS INTACT, not merely that the call returned false. A version
///  that refused AFTER rewriting the setmark — or after writing a TOC over the media header — would pass
///  a return-value check and lose the user's data.
/// </para>
/// <para>
/// Deliberately separate from <c>DeleteSetsTests</c>, whose subject is deletion MECHANICS on default
///  fixtures (TOC counts, set survival). These need explicit headed media, fault injection, and
///  byte-level survival assertions — a different shape entirely.
/// </para>
/// </summary>
public class TapeSetDeleteVerificationTests
{
    #region *** Test Data ***

    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];

    /// <summary>Non-partition profiles only — the delete-ALL branch requires TOC-in-set.</summary>
    public static TheoryData<DriveProfile> TOCInSetProfiles =>
    [
        DriveProfile.Setmarks,
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
        Path.Combine(Path.GetTempPath(), $"TapeNET_SDV_{Guid.NewGuid():N}");

    private static void AssertRestoredMatches(TempFileTree tree, string restoreDir)
    {
        string equivalent = Path.Combine(
            restoreDir, Path.GetRelativePath(Path.GetPathRoot(tree.RootPath)!, tree.RootPath));
        FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, equivalent);
    }

    /// <summary>Distinctly-named files per set, so a wrong-set delete cannot masquerade as success.</summary>
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

    /// <summary>
    /// Restores <paramref name="setIndex"/> with a FRESH agent and asserts byte equality — the only
    ///  statement that distinguishes "refused" from "refused after damage".
    /// </summary>
    private static void AssertSetStillRestores(VirtualTapeFixture fixture, int setIndex, TempFileTree tree)
    {
        string restoreDir = NewRestoreDir();
        try
        {
            fixture.TOC.CurrentSetIndex = setIndex;
            using var agent = fixture.CreateRestoreAgent(restoreDir);
            Assert.True(agent.RestoreAllFilesFromCurrentSet(ignoreFailures: false),
                $"set #{setIndex} must still restore after a refused delete");
            AssertRestoredMatches(tree, restoreDir);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>Reads the media header back with a fresh agent; null means it is gone.</summary>
    private static TapeMediaHeader? ReadMediaHeader(VirtualTapeFixture fixture)
    {
        using var probe = new TapeFileAgent(fixture.Drive, fixture.TOC);
        return probe.ReadBomHeader() as TapeMediaHeader;
    }

    #endregion

    #region *** (A) The trailing branch ***

#if DEBUG
    /// <summary>
    /// The originating scenario in miniature: navigation to the set to delete typically counts BACKWARD
    ///  from end-of-content, across the very region a failed backup would have damaged. A miscount there
    ///  would rewrite the setmark at the wrong place and silently destroy a set the user meant to keep.
    /// </summary>
    /// <remarks>
    /// The target sits mid-tape so the injected drift lands on a real neighbouring set — a drift that
    ///  fell off either end could be refused for the wrong reason.
    /// <para>
    /// Notice we need at least five sets, not four: on setmark layouts the injector is consumed by
    ///  the -1 settle inside MoveToEndOfContentCore, so the tape must be long enough that the cancelled
    ///  settle still lands inside content!
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TrailingDelete_WithDrift_IsRefused_AndTapeIntact(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        var notify = new TestNotifiable();

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 5, prefix: "td"); // 5 sets at least if delete set 3 ff.

            fixture.TOC.CurrentSetIndex = 3;        // delete sets 3..5, keep 1..2
            using (var agent = new TapeSetAgent(fixture.Drive, fixture.TOC))
            {
                agent.Navigator.SimulateSetMiscount = +1;
                agent.Navigator.SimulateSetMiscountPersistent = true; // keep miscounting to prevent agent's self-correction! (v2)

                var result = agent.DeleteSetsFromCurrentSetUp(fileNotify: notify);
                Assert.False(result, "a delete at a drifted position must be refused");
                Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_DATA, result.ErrorCode);   // the VERDICT, not some other fault
                // Do NOT Assert.Equal(0, agent.Navigator.SimulateSetMiscount) since we keep injecting (Persistent = true).
                //  No worries, we'll have enough checks to verify! :-)

                var sets = agent.Statistics.Sets;
                Assert.Equal(1, sets.AnomaliesDetected);
                Assert.Equal(0, sets.AnomaliesRecovered);
                Assert.Equal(0, sets.AnomaliesRecoveredFromBom);
                Assert.True(sets.SetWriteBlocked);

                // The set hit an anomaly, so it is NOT counted among the clean ones.
                Assert.Equal(3, sets.SetsProcessed); // we attempted to delete 3 sets (3..5)
                Assert.Equal(0, sets.SetsSucceeded); // we failed
            }

            // The decisive assertions: EVERY set survives — including the ones that were to be deleted,
            //  since a refused delete must change nothing at all.
            for (int i = 0; i < trees.Length; i++)
                AssertSetStillRestores(fixture, i + 1, trees[i]);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// The inverted golden rule (§4.3) on the delete path: an unclassifiable block at the set start is
    ///  the miscount's signature, not a lost safety net — so it BLOCKS, where the read path proceeds.
    /// </summary>
    /// <remarks>
    /// This is what <c>m_verifyingDestructiveWrite</c> exists for: <see cref="TapeFileAgent"/> itself
    ///  reports <c>BlocksOnUnverifiableSet == false</c>, and only the in-flight delete flips it.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TrailingDelete_OnUnreadableSetHeader_IsRefused(DriveProfile profile)
    {
        TempFileTree[] trees = [];

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 3, prefix: "tu");

            fixture.TOC.CurrentSetIndex = 3;
            using (var agent = new TapeSetAgent(fixture.Drive, fixture.TOC))
            {
                // Resolve the BOM header first, so the injector lands on the SET header read.
                agent.ReadBomHeader();
                fixture.Backend.ContentReadFaults.CorruptOnce(bits: 2, offset: 48);

                var result = agent.DeleteSetsFromCurrentSetUp();
                Assert.False(result, "an unverifiable set header must block a destructive delete");
                Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_DATA, result.ErrorCode);
                Assert.Equal(1, fixture.Backend.ContentReadFaults.Occurrences);   // the SET read was the one corrupted
            }

            for (int i = 0; i < trees.Length; i++)
                AssertSetStillRestores(fixture, i + 1, trees[i]);
        }
        finally
        {
            DisposeAll(trees);
        }
    }
#endif // DEBUG

    /// <summary>
    /// The happy path must stay exactly as it was: the named sets go, the retained ones restore
    ///  byte-for-byte, and the TOC on tape agrees. Guards against a verification that fires spuriously —
    ///  which would make every delete on headed media impossible.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TrailingDelete_Clean_Succeeds_AndRetainedSetsRestore(DriveProfile profile)
    {
        TempFileTree[] trees = [];

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 3, prefix: "tc");

            fixture.TOC.CurrentSetIndex = 2;        // delete 2..3, keep 1
            using (var agent = new TapeSetAgent(fixture.Drive, fixture.TOC))
            {
                var result = agent.DeleteSetsFromCurrentSetUp();
                Assert.True(result, $"a clean delete must not be disturbed by verification: {result.ErrorMessage}");
                Assert.False(agent.Statistics.Sets.HasAnomalies, "No set anomalies should've occurred");
            }

            fixture.LoadTOC();
            Assert.Equal(1, fixture.TOC.Count);
            Assert.Equal("Set 1", fixture.TOC[1].Description);

            AssertSetStillRestores(fixture, 1, trees[0]);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// SH-1's delete-side half: a legacy volume declares no set headers, so there is nothing to verify
    ///  and the delete must proceed exactly as it did before the feature existed.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TrailingDelete_OnLegacyVolume_Proceeds(DriveProfile profile)
    {
        TempFileTree[] trees = [];

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: false);
            trees = BuildMultiSetTape(fixture, setCount: 2, prefix: "tl");

            fixture.TOC.CurrentSetIndex = 2;
            using (var agent = new TapeSetAgent(fixture.Drive, fixture.TOC))
            {
                Assert.False(agent.Navigator.SetHeadersExpected);
                Assert.True(agent.DeleteSetsFromCurrentSetUp(),
                    "a header-less volume has nothing to verify and must not be blocked");
            }

            fixture.LoadTOC();
            Assert.Equal(1, fixture.TOC.Count);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

#if DEBUG
    /// <summary>
    /// The opt-out disables the CHECK, not a prompt — which is exactly why it exists. Repairing a
    ///  cartridge whose set headers are themselves damaged needs the delete to proceed at a position the
    ///  verification would refuse.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void VerifiesSetHeaderFalse_LetsTheDeleteThrough(DriveProfile profile)
    {
        TempFileTree[] trees = [];

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 3, prefix: "tf");

            fixture.TOC.CurrentSetIndex = 3;
            using var agent = new TapeSetAgent(fixture.Drive, fixture.TOC);
            agent.VerifiesSetHeader = false;

            agent.ReadBomHeader();
            fixture.Backend.ContentReadFaults.CorruptOnce(bits: 2, offset: 48);

            Assert.True(agent.DeleteSetsFromCurrentSetUp(),
                "with verification disabled the delete proceeds unchecked");

            // Nothing read it, so the injected fault is still armed.
            Assert.Equal(0, fixture.Backend.ContentReadFaults.Occurrences);
        }
        finally
        {
            DisposeAll(trees);
        }
    }
#endif // DEBUG

    #endregion

    #region *** (B) The delete-ALL branch ***

    /// <summary>
    /// §4.4: the delete-ALL branch lands at a deterministic block, so there is nothing to miscount — but
    ///  the header found there must be the volume's FIRST set, confirming the head cleared the media
    ///  header rather than standing on it.
    /// </summary>
    /// <remarks>
    /// Reading the media header back afterwards is the point: this branch writes a fresh initial TOC at
    ///  begin-of-content, and a one-block positioning error would put that TOC on top of
    ///  <see cref="TapeMediaHeader"/> (INV-4). Without this assertion such a regression is silent — the
    ///  delete would report success and the cartridge would lose its identity.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TOCInSetProfiles))]
    public void DeleteAll_VerifiesSetZero_AndPreservesMediaHeader(DriveProfile profile)
    {
        TempFileTree[] trees = [];

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 2, prefix: "da");

            var before = ReadMediaHeader(fixture);
            Assert.NotNull(before);

            fixture.TOC.CurrentSetIndex = fixture.TOC.FirstSetOnVolume;
            using (var agent = new TapeSetAgent(fixture.Drive, fixture.TOC))
            {
                var result = agent.DeleteSetsFromCurrentSetUp();
                Assert.True(result, $"delete-all must pass its positional assertion: {result.ErrorMessage}");
            }

            fixture.LoadTOC();
            Assert.True(fixture.TOC.IsEmpty);

            // The header survived, identity and declaration intact.
            var after = ReadMediaHeader(fixture);
            Assert.NotNull(after);
            Assert.Equal(before!.MediaId, after!.MediaId);
            Assert.True(after.HasSetHeaders);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

#if DEBUG
    /// <summary>
    /// The branch that must never write a TOC over the media header. If the block at begin-of-content
    ///  cannot be confirmed as this volume's first set header, the head's position is not established —
    ///  and writing a TOC from an unestablished position at BOM is precisely how INV-4 gets violated.
    /// </summary>
    [Theory]
    [MemberData(nameof(TOCInSetProfiles))]
    public void DeleteAll_OnUnreadableSetZero_IsRefused(DriveProfile profile)
    {
        TempFileTree[] trees = [];

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 2, prefix: "du");

            var before = ReadMediaHeader(fixture);
            Assert.NotNull(before);

            fixture.TOC.CurrentSetIndex = fixture.TOC.FirstSetOnVolume;
            using (var agent = new TapeSetAgent(fixture.Drive, fixture.TOC))
            {
                agent.ReadBomHeader();
                fixture.Backend.ContentReadFaults.CorruptOnce(bits: 2, offset: 48);

                var result = agent.DeleteSetsFromCurrentSetUp();
                Assert.False(result, "delete-all must not write a TOC from an unconfirmed position");
                Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_DATA, result.ErrorCode);
            }

            // Both halves: the header is still there, and so is the content it describes.
            var after = ReadMediaHeader(fixture);
            Assert.NotNull(after);
            Assert.Equal(before!.MediaId, after!.MediaId);

            for (int i = 0; i < trees.Length; i++)
                AssertSetStillRestores(fixture, i + 1, trees[i]);
        }
        finally
        {
            DisposeAll(trees);
        }
    }
#endif // DEBUG

    #endregion
}
