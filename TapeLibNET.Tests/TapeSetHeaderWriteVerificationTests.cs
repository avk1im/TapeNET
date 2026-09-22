using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;
using Windows.Win32.Foundation;
//using static Google.Protobuf.Compiler.CodeGeneratorResponse.Types; // how did that make it in??

namespace TapeLibNET.Tests;

/// <summary>
/// Step 2 — verified overwrite (SH-13). A destructive write confirms the set header standing at its
///  target before destroying it.
/// <para>
/// Every refusal test asserts the TAPE IS INTACT, not merely that the call returned false. A version
///  that refused AFTER clobbering the set header would pass a return-value check and fail the user.
/// </para>
/// </summary>
public class TapeSetHeaderWriteVerificationTests
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
        Path.Combine(Path.GetTempPath(), $"TapeNET_SHW_{Guid.NewGuid():N}");

    private static void AssertRestoredMatches(TempFileTree tree, string restoreDir)
    {
        string equivalent = Path.Combine(
            restoreDir, Path.GetRelativePath(Path.GetPathRoot(tree.RootPath)!, tree.RootPath));
        FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, equivalent);
    }

    /// <summary>Distinctly-named files per set, so a wrong-set write cannot masquerade as success.</summary>
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
                $"set #{setIndex} must still restore after a refused overwrite");
            AssertRestoredMatches(tree, restoreDir);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// Remove the current set TOC and replace it with a new empty one with the same parameters. Useful
    ///  when completely replacing the content of a set.
    /// </summary>
    /// <remarks>
    /// The agent only appends to <c>CurrentSetTOC</c>, so the caller must clear it first -- otherwise the stale
    ///  entries survive ahead of the new ones, still carrying addresses the new files now occupy, and restore
    ///  meets the wrong file's <c>UID</c>. (<see cref="TapeServiceBase"/> does this; agent-level callers must too.)
    /// </remarks>
    /// <param name="fixture">The virtual tape fixture.</param>
    /// <param name="newCapacity">The capacity of the new set TOC.</param>
    private static void EmptyCurrentSetTOC(VirtualTapeFixture fixture, int newCapacity)
    {
        var setParams = fixture.TOC.CurrentSetTOC.ToParams();
        fixture.TOC.ReplaceCurrentSetTOC(capacity: newCapacity);
        fixture.TOC.CurrentSetTOC.BlockSize = setParams.BlockSize;
        fixture.TOC.CurrentSetTOC.HashAlgorithm = setParams.HashAlgorithm;
        fixture.TOC.CurrentSetTOC.Description = setParams.Description;
    }

    #endregion

    #region *** (A) Drift blocks the overwrite ***

    /// <summary>
    /// The feature's reason for existing on the write side: a miscount that would have overwritten the
    ///  WRONG set is caught by that set's own header, and nothing is destroyed.
    /// </summary>
    /// <remarks>
    /// The target sits mid-tape so the injected drift lands on a real neighbouring set — a drift that
    ///  fell off the end could be refused for the wrong reason.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Overwrite_WithDrift_IsRefused_AndTapeIntact(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        var notify = new TestNotifiable { SetAnomalyAction = SetAnomalyAction.Abort }; // same as null, but just for the test
        using var replacement = new TempFileTree();
        replacement.AddFiles("rep", count: 2, minSize: 512, maxSize: 4 * 1024);

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 4, prefix: "dr");

            fixture.TOC.CurrentSetIndex = 3;
            using (var agent = fixture.CreateBackupAgent())
            {
                agent.Navigator.SimulateSetMiscount = +1;
                Assert.False(agent.BackupFileListToCurrentSet(newSet: false, replacement.Files, ignoreFailures: false, fileNotify: notify),
                    "an overwrite at a drifted position must be refused");
                Assert.Equal(0, agent.Navigator.SimulateSetMiscount);   // the injection point WAS reached
            }

            // The decisive assertions: every pre-existing set survives byte-for-byte.
            for (int i = 0; i < trees.Length; i++)
                AssertSetStillRestores(fixture, i + 1, trees[i]);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// Overwriting a set in the middle of a volume used to rewrite the media header at BOM
    ///  before anything else — and a BOM write truncates everything past it, so the verification would
    ///  have read the wreckage of the tape it was meant to protect.
    /// </summary>
    /// <remarks>
    /// Asserts BOTH halves: the surviving sets still restore, and the media header is still there. A fix
    ///  that merely reordered the calls without deferring the write would fail the first; one that
    ///  dropped the write entirely would fail the second.
    /// </remarks>

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Overwrite_WithDriftMidVolume_IsRefused_AndTapeIntact(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        using var replacement = new TempFileTree();
        replacement.AddFiles("f1", count: 2, minSize: 512, maxSize: 4 * 1024);

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 4, prefix: "fs");

            fixture.TOC.CurrentSetIndex = 2;        // the SECOND set on the volume
            using (var agent = fixture.CreateBackupAgent())
            {
                agent.Navigator.SimulateSetMiscount = +1;
                Assert.False(agent.BackupFileListToCurrentSet(newSet: false, replacement.Files, ignoreFailures: false));
            }

            // Nothing was destroyed on the way to the refusal…
            AssertSetStillRestores(fixture, 1, trees[0]);
            AssertSetStillRestores(fixture, 2, trees[1]);
            AssertSetStillRestores(fixture, 3, trees[2]);
            AssertSetStillRestores(fixture, 4, trees[3]);

            // …and the media header is still on tape.
            using var probe = new TapeFileAgent(fixture.Drive, fixture.TOC);
            var media = probe.ReadBomHeader() as TapeMediaHeader;
            Assert.NotNull(media);
            Assert.True(media!.HasSetHeaders);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// §3.1 directly. Overwriting the FIRST set on a volume used to rewrite the media header at BOM
    ///  before anything else — and a BOM write truncates everything past it, so the verification would
    ///  have read the wreckage of the tape it was meant to protect.
    /// </summary>
    /// <remarks>
    /// Asserts BOTH halves: the surviving sets still restore, and the media header is still there. A fix
    ///  that merely reordered the calls without deferring the write would fail the first; one that
    ///  dropped the write entirely would fail the second.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Overwrite_OfFirstSetOnVolume_DoesNotClobberBeforeVerifying(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        using var replacement = new TempFileTree();
        replacement.AddFiles("f1", count: 2, minSize: 512, maxSize: 4 * 1024);

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 3, prefix: "fs");

            fixture.TOC.CurrentSetIndex = 1;        // the FIRST set on the volume
            using (var agent = fixture.CreateBackupAgent())
            {
                if (profile is DriveProfile.FilemarksOnly or DriveProfile.SeqFilemarks)
                {
                    // the FM-counting profiles can be simulated to miscount to the first set
                    agent.Navigator.SimulateSetMiscount = +1;
                }
                else
                {
                    // Partitions and Setmarks don't count FMs to for the first set, hence need a different simulation
                    //  ...namely the read-fault injector for the set header read (NOT the media header!)
                    fixture.Backend.ContentReadFaults.CorruptOnce(bits: 2, offset: 48);
                    fixture.Backend.ContentReadFaults.SkipN = 1; // don't corrupt the media header read
                }

                var result = agent.BackupFileListToCurrentSet(newSet: false, replacement.Files, ignoreFailures: false);
                Assert.False(result);
                Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_DATA, result.ErrorCode);   // the verdict, not some other fault
                // ERROR_INVALID_DATA is set by both Unreadable block and the SetIndexDrift block set, so one assertion covers both arms
            }

            // Nothing was destroyed on the way to the refusal…
            AssertSetStillRestores(fixture, 1, trees[0]);
            AssertSetStillRestores(fixture, 2, trees[1]);
            AssertSetStillRestores(fixture, 3, trees[2]);

            // …and the media header is still on tape.
            using var probe = new TapeFileAgent(fixture.Drive, fixture.TOC);
            var media = probe.ReadBomHeader() as TapeMediaHeader;
            Assert.NotNull(media);
            Assert.True(media!.HasSetHeaders);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>Identity failures are refused without attempting anything positional.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Overwrite_WrongVolume_IsRefused(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        using var replacement = new TempFileTree();
        replacement.AddFiles("wv", count: 2, minSize: 512, maxSize: 4 * 1024);

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 3, prefix: "wv");

            fixture.TOC.CurrentSetIndex = 2;
            int realVolume = fixture.TOC.Volume;
            fixture.TOC.Volume += 5;                 // the tape says one volume, the library believes another

            using (var agent = fixture.CreateBackupAgent())
            {
                Assert.False(agent.BackupFileListToCurrentSet(newSet: false, replacement.Files, ignoreFailures: false),
                    "a volume mismatch must refuse the overwrite");
            }

            fixture.TOC.Volume = realVolume;         // restore the truth before checking the tape
            AssertSetStillRestores(fixture, 2, trees[1]);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

#if DEBUG
    /// <summary>
    /// The inverted golden rule (§4.3): on the write side an unclassifiable block at the set start is
    ///  the miscount's signature, not a lost safety net — so it BLOCKS, where the read path proceeds.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Overwrite_OnUnreadableSetHeader_IsRefused(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        using var replacement = new TempFileTree();
        replacement.AddFiles("ur", count: 2, minSize: 512, maxSize: 4 * 1024);

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 3, prefix: "ur");

            fixture.TOC.CurrentSetIndex = 2;
            using (var agent = fixture.CreateBackupAgent())
            {
                // Resolve the BOM header first so the injector lands on the SET header read.
                agent.ReadBomHeader();
                fixture.Backend.ContentReadFaults.CorruptOnce(bits: 2, offset: 48);

                Assert.False(agent.BackupFileListToCurrentSet(newSet: false, replacement.Files, ignoreFailures: false),
                    "an unverifiable set header must block a destructive write");
            }

            AssertSetStillRestores(fixture, 2, trees[1]);
        }
        finally
        {
            DisposeAll(trees);
        }
    }
#endif // DEBUG

    #endregion

    #region *** (B) The clean paths stay clean ***

    /// <summary>
    /// The happy path: an overwrite verifies, proceeds, and the replacement restores. Crucially the
    ///  first file still sits EXACTLY one block past the set start — simultaneously the SH-4 assertion
    ///  (no redundant re-positioning moved the packer's anchor) and the SH-15 one (the verifying read's
    ///  one-block advance was undone before the set header was stamped).
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Overwrite_Clean_Succeeds_AndFirstFileSitsOneBlockPastSetStart(DriveProfile profile)
    {
        string restoreDir = NewRestoreDir();
        TempFileTree[] trees = [];
        using var replacement = new TempFileTree();
        replacement.AddFiles("ok", count: 3, minSize: 512, maxSize: 4 * 1024);

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 3, prefix: "ok");

            // Capture where set 3's data began BEFORE the overwrite: the header sat one block ahead of it.
            long firstFileBlockBefore = fixture.TOC[3][0].Address.Block;

            fixture.TOC.CurrentSetIndex = 3;

            EmptyCurrentSetTOC(fixture, newCapacity: replacement.Files.Count);

            using (var agent = fixture.CreateBackupAgent())
            {
                Assert.True(agent.BackupFileListToCurrentSet(newSet: false, replacement.Files, ignoreFailures: false),
                    "a clean overwrite must not be disturbed by verification");
                Assert.True(agent.BackupTOC());
            }

            // The rewritten set begins exactly where the old one did — one block past its header.
            Assert.Equal(firstFileBlockBefore, fixture.TOC[3][0].Address.Block);

            fixture.TOC.CurrentSetIndex = 3;
            using var restore = fixture.CreateRestoreAgent(restoreDir);
            Assert.True(restore.RestoreAllFilesFromCurrentSet(ignoreFailures: false));
            AssertRestoredMatches(replacement, restoreDir);
        }
        finally
        {
            DisposeAll(trees);
            TryDeleteDirectory(restoreDir);
        }
    }

#if DEBUG
    /// <summary>
    /// §3's performance claim, asserted rather than assumed: appending at end-of-data reads NOTHING.
    ///  There is no predecessor record where the append will write, so verification must not arm — and
    ///  the read-fault injector proves it by going unconsumed.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Append_AtEod_PerformsNoVerification(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        using var appended = new TempFileTree();
        appended.AddFiles("ap", count: 2, minSize: 512, maxSize: 4 * 1024);

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 2, prefix: "ap");

            using var agent = fixture.CreateBackupAgent();
            agent.ReadBomHeader();                       // resolve BOM first, so the injector targets a SET read
            fixture.Backend.ContentReadFaults.FailOnce();

            fixture.TOC.AddNewSetTOC();
            Assert.True(agent.BackupFileListToCurrentSet(newSet: true, appended.Files, ignoreFailures: false));

            Assert.Equal(0, fixture.Backend.ContentReadFaults.Occurrences);   // nothing was read at all
        }
        finally
        {
            DisposeAll(trees);
        }
    }
#endif // DEBUG

    /// <summary>
    /// SH-1's write-side half: a legacy volume declares no set headers, so there is nothing to verify
    ///  and the overwrite must proceed exactly as it did before the feature existed. Blocking here would
    ///  make the feature a regression for every pre-header cartridge.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Overwrite_OnLegacyVolume_Proceeds(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        using var replacement = new TempFileTree();
        replacement.AddFiles("lg", count: 2, minSize: 512, maxSize: 4 * 1024);

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: false);
            trees = BuildMultiSetTape(fixture, setCount: 2, prefix: "lg");

            fixture.TOC.CurrentSetIndex = 2;

            EmptyCurrentSetTOC(fixture, newCapacity: replacement.Files.Count);

            using var agent = fixture.CreateBackupAgent();
            Assert.False(agent.Navigator.SetHeadersExpected);

            Assert.True(agent.BackupFileListToCurrentSet(newSet: false, replacement.Files, ignoreFailures: false),
                "a header-less volume has nothing to verify and must not be blocked");
            Assert.True(agent.BackupTOC());
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// The opt-out disables the CHECK, not a prompt: with <c>VerifiesSetHeader = false</c> a drifted
    ///  overwrite proceeds — which is exactly why it exists, and exactly why it is not a default.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void VerifiesSetHeaderFalse_LetsTheOverwriteThrough(DriveProfile profile)
    {
        TempFileTree[] trees = [];
        using var replacement = new TempFileTree();
        replacement.AddFiles("of", count: 2, minSize: 512, maxSize: 4 * 1024);

        try
        {
            using var fixture = new VirtualTapeFixture(profile, withMediaHeader: true, withSetHeaders: true);
            trees = BuildMultiSetTape(fixture, setCount: 3, prefix: "of");

            fixture.TOC.CurrentSetIndex = 2;

            EmptyCurrentSetTOC(fixture, newCapacity: replacement.Files.Count);

            using var agent = fixture.CreateBackupAgent();
            agent.VerifiesSetHeader = false;

            Assert.True(agent.BackupFileListToCurrentSet(newSet: false, replacement.Files, ignoreFailures: true),
                "with verification disabled the write proceeds unchecked");
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    #endregion
}