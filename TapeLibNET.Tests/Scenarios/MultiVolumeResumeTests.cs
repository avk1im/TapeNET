using TapeLibNET.Agents;
using TapeLibNET.Toc;
using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Scenarios;

#if TEST_HOOKS
/// <summary>
/// Regression suite for the multi-volume continuation state machine (agent level): a volume that cannot be used is
///  REJECTED — the agent no longer asks for it, so a caller looping on <c>CanResume…</c> stops instead of re-offering the
///  same cartridge forever — and the caller may retry, skip the volume (restore), or end. Skipping is also possible
///  without any error. The operation's earliest failure survives every volume change.
/// </summary>
public class MultiVolumeResumeTests
{
    #region *** Data and helpers ***

    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];

    private const int FileCount = 16;
    private const uint ErrorWrongDisk = 34;

    private static TempFileTree SpanningTree()
    {
        var tree = new TempFileTree();
        tree.AddFiles("mv_resume", count: FileCount, minSize: 16 * 1024, maxSize: 32 * 1024);
        return tree;
    }

    private static string MakeRestoreDir() =>
        Path.Combine(Path.GetTempPath(), $"TapeNET_MVResume_{Guid.NewGuid():N}");

    private static string RestoreEquivalentRoot(string restoreDir, string originalRoot)
        => Path.Combine(restoreDir, Path.GetRelativePath(Path.GetPathRoot(originalRoot)!, originalRoot));

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { /* best effort */ }
    }

    /// <summary>Resumes with the cartridge in the drive, then follows every further volume request to the end.</summary>
    private static TapeResult ResumeToTheEnd(MultiVolumeVirtualTapeFixture fixture, TapeFileRestoreBaseAgent agent)
    {
        TapeResult result = agent.ResumeRestoreFromAnotherVolume();
        while (!result.Success && agent.CanResumeFromAnotherVolume)
        {
            fixture.SwapToVolume(agent.VolumeToResumeFrom);
            result = agent.ResumeRestoreFromAnotherVolume();
        }
        return result;
    }

    /// <summary>A spanning backup; returns the restore agent positioned on its first volume request.</summary>
    private static TapeFileRestoreAgentEx StartSpanningRestore(MultiVolumeVirtualTapeFixture fixture, TempFileTree tree,
        string restoreDir)
    {
        fixture.BackupFiles(tree.Files, "Span");
        Assert.True(fixture.TotalVolumes >= 2, $"the set must span (got {fixture.TotalVolumes} volume(s))");

        fixture.TOC.MakeLastSetCurrent();
        var agent = fixture.CreateRestoreAgent(restoreDir);
        TapeResult first = agent.RestoreAllFilesFromCurrentSet();
        Assert.False(first.Success);
        Assert.True(agent.CanResumeFromAnotherVolume, "the restore must ask for an earlier volume first");
        return agent;
    }

    /// <summary>Names of the files in the parts of the logical set that ends at the last set, NOT on <paramref name="volume"/>.</summary>
    private static HashSet<string> FilesNotOnVolume(TapeTOC toc, int volume)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int s = toc.Count; s >= 1; s--)
        {
            if (toc[s].Volume != volume)
                foreach (TapeFileInfo tfi in toc[s])
                    names.Add(tfi.FileDescr.FullName);
            if (!toc[s].ContinuedFromPrevVolume)
                break;
        }
        return names;
    }

    private static void AssertExactlyRestored(TempFileTree tree, string restoreDir, HashSet<string> expectedNames)
    {
        string root = RestoreEquivalentRoot(restoreDir, tree.RootPath);
        FileComparer.AssertFilesMatch(tree.RootPath, [.. tree.Files.Where(expectedNames.Contains)], root);
        foreach (string missing in tree.Files.Where(f => !expectedNames.Contains(f)))
            Assert.False(File.Exists(Path.Combine(root, Path.GetRelativePath(tree.RootPath, missing))));
    }

    /// <summary>
    /// Makes every block of the loaded cartridge unidentifiable — media header, TOC copies, the set's first block — so the
    ///  agent cannot identify the volume at all (Design-TocLocation §7) and must reject it. A merely unreadable TOC is no
    ///  longer enough: the volume would be identified by its header or its set, and restored with the series TOC.
    /// </summary>
    private static void SetCartridgeUnreadable(MultiVolumeVirtualTapeFixture fixture, bool unreadable)
    {
        foreach (var faults in new[] { fixture.Backend.ContentReadFaults, fixture.Backend.InitiatorReadFaults })
        {
            if (unreadable)
                faults.CorruptAlways(bits: 2, offset: 0);   // the magic: nothing identifies, nothing parses
            else
                faults.Reset(); // also disables
        }
    }

    #endregion

    #region *** Restore: rejection instead of an endless loop ***

    /// <summary>
    /// The originating defect: neither TOC nor any identifiable header on the requested cartridge
    ///  can be read. Before the fix the agent kept asking for the same volume, and every caller
    ///  looping on <see cref="TapeFileRestoreBaseAgent.CanResumeFromAnotherVolume"/> spun forever.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Restore_UnidentifiableVolume_IsRejected_ThenRetrySucceeds(DriveProfile profile)
    {
        using var tree = SpanningTree();
        using var fixture = new MultiVolumeVirtualTapeFixture(profile);
        string restoreDir = MakeRestoreDir();
        try
        {
            using var agent = StartSpanningRestore(fixture, tree, restoreDir);
            int volume = agent.VolumeToResumeFrom;
            fixture.SwapToVolume(volume);

            // INSUFFICIENT: agent.SimulateTOCFailureMask = 7;                          // every TOC copy of this cartridge fails
            SetCartridgeUnreadable(fixture, true);                    // nothing on this cartridge identifies
            
            TapeResult rejected = agent.ResumeRestoreFromAnotherVolume();

            Assert.False(rejected.Success);
            Assert.True(agent.IsVolumeRejected);
            Assert.False(agent.CanResumeFromAnotherVolume);            // a CanResume… loop now ends
            Assert.True(agent.IsMultiVolumePending);
            Assert.Equal(volume, agent.VolumeToResumeFrom);            // still the volume it needs
            Assert.False(string.IsNullOrWhiteSpace(rejected.ErrorMessage));

            // INSUFFICIENT: agent.SimulateTOCFailureMask = 0;                          // "the user cleaned the cartridge"
            SetCartridgeUnreadable(fixture, false);                   // "the user cleaned the cartridge"
            
            TapeResult done = ResumeToTheEnd(fixture, agent);

            Assert.True(done.Success, done.ErrorMessage);
            Assert.True(agent.LastResult.Success, "the rejected attempt must not linger in the diagnosis");
            Assert.False(agent.IsMultiVolumePending);
            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// Design-TocLocation §7: a cartridge whose own TOC does not read is NOT rejected when
    ///  its set identifies it — the series TOC addresses every file.
    ///  The unreadable TOC is recorded, not fatal.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Restore_UnreadableTocOnRequestedVolume_IdentifiedBySet_Restores(DriveProfile profile)
    {
        using var tree = SpanningTree();
        using var fixture = new MultiVolumeVirtualTapeFixture(profile);
        string restoreDir = MakeRestoreDir();
        try
        {
            using var agent = StartSpanningRestore(fixture, tree, restoreDir);
            int volume = agent.VolumeToResumeFrom;
            fixture.SwapToVolume(volume);
            agent.SimulateTOCFailureMask = -1;                         // no TOC copy of this cartridge reads

            TapeResult result = agent.ResumeRestoreFromAnotherVolume();
            Assert.False(agent.IsVolumeRejected);
            Assert.Contains(agent.TocAnomalies,
                a => a.Kind == TapeTocAnomalyKind.NoReadableCopy && a.Volume == volume && !a.IsRecovery);

            agent.SimulateTOCFailureMask = 0;
            while (!result.Success && agent.CanResumeFromAnotherVolume)
            {
                fixture.SwapToVolume(agent.VolumeToResumeFrom);
                result = agent.ResumeRestoreFromAnotherVolume();
            }
            Assert.True(result.Success, result.ErrorMessage);
            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Restore_WrongVolumeInserted_IsRejected_ThenTheRightOneSucceeds(DriveProfile profile)
    {
        using var tree = SpanningTree();
        using var fixture = new MultiVolumeVirtualTapeFixture(profile);
        string restoreDir = MakeRestoreDir();
        try
        {
            using var agent = StartSpanningRestore(fixture, tree, restoreDir);
            int volume = agent.VolumeToResumeFrom;
            int wrong = volume == fixture.TotalVolumes ? 1 : fixture.TotalVolumes;
            fixture.SwapToVolume(wrong);

            TapeResult rejected = agent.ResumeRestoreFromAnotherVolume();

            Assert.False(rejected.Success);
            Assert.Equal(ErrorWrongDisk, rejected.ErrorCode);
            Assert.True(agent.IsVolumeRejected);
            Assert.Equal(volume, agent.VolumeToResumeFrom);

            fixture.SwapToVolume(volume);
            TapeResult done = ResumeToTheEnd(fixture, agent);

            Assert.True(done.Success, done.ErrorMessage);
            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** Restore: skipping a volume ***

    /// <summary>
    /// The user puts up with an unusable volume: its sets are given up and counted as FAILED, the agent asks for the
    ///  next volume, the rest restores, and the diagnosis names the skipped volume.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Restore_RejectedVolumeSkipped_CountsAsFailed_TheRestRestores(DriveProfile profile)
    {
        using var tree = SpanningTree();
        using var fixture = new MultiVolumeVirtualTapeFixture(profile);
        string restoreDir = MakeRestoreDir();
        try
        {
            using var agent = StartSpanningRestore(fixture, tree, restoreDir);
            int skippedVolume = agent.VolumeToResumeFrom;
            fixture.SwapToVolume(skippedVolume);

            // INSUFFICIENT: agent.SimulateTOCFailureMask = 7;                          // every TOC copy of this cartridge fails
            SetCartridgeUnreadable(fixture, true);                    // nothing on this cartridge identifies

            Assert.False(agent.ResumeRestoreFromAnotherVolume().Success);

            // INSUFFICIENT: agent.SimulateTOCFailureMask = 0;                          // "the user cleaned the cartridge"
            SetCartridgeUnreadable(fixture, false);                   // "the user cleaned the cartridge"

            TapeResult result = agent.SkipRequestedVolume(out int sets, out int files);
            Assert.True(sets >= 1);
            while (!result.Success && agent.CanResumeFromAnotherVolume)
            {
                Assert.NotEqual(skippedVolume, agent.VolumeToResumeFrom);
                fixture.SwapToVolume(agent.VolumeToResumeFrom);
                result = agent.ResumeRestoreFromAnotherVolume();
            }

            Assert.False(result.Success);
            Assert.Contains($"Volume #{skippedVolume} skipped", result.ErrorMessage);
            Assert.False(agent.IsMultiVolumePending);
            Assert.Equal(files, agent.Statistics.FilesFailed);
            Assert.Equal(FileCount, agent.Statistics.FilesProcessed);
            Assert.Equal(agent.Statistics.FilesProcessed,
                agent.Statistics.FilesSucceeded + agent.Statistics.FilesFailed + agent.Statistics.FilesSkipped);

            AssertExactlyRestored(tree, restoreDir, FilesNotOnVolume(fixture.TOC, skippedVolume));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// The user skips a volume WITHOUT any error (e.g. the cartridge is not at hand): its files are counted as SKIPPED,
    ///  nothing is latched, and the operation still succeeds for what it restored.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Restore_VolumeSkippedVoluntarily_CountsAsSkipped_NoFailure(DriveProfile profile)
    {
        using var tree = SpanningTree();
        using var fixture = new MultiVolumeVirtualTapeFixture(profile);
        string restoreDir = MakeRestoreDir();
        try
        {
            using var agent = StartSpanningRestore(fixture, tree, restoreDir);
            int skippedVolume = agent.VolumeToResumeFrom;

            TapeResult result = agent.SkipRequestedVolume(out _, out int files);
            while (!result.Success && agent.CanResumeFromAnotherVolume)
            {
                fixture.SwapToVolume(agent.VolumeToResumeFrom);
                result = agent.ResumeRestoreFromAnotherVolume();
            }

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(agent.LastResult.Success);
            Assert.Equal(files, agent.Statistics.FilesSkipped);
            Assert.Equal(0, agent.Statistics.FilesFailed);

            AssertExactlyRestored(tree, restoreDir, FilesNotOnVolume(fixture.TOC, skippedVolume));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// A file failure on the first volume restored must still be the operation's diagnosis after later volume changes.
    ///  Before the fix it was lost twice over: failures on a resumed volume were not latched, and the TOC check of every
    ///  further volume reset the latch.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Restore_EarlyFileFailure_SurvivesVolumeChanges(DriveProfile profile)
    {
        using var tree = SpanningTree();
        using var fixture = new MultiVolumeVirtualTapeFixture(profile);
        string restoreDir = MakeRestoreDir();
        try
        {
            using var agent = StartSpanningRestore(fixture, tree, restoreDir);
            agent.SimulateFileFailures.Enabled = true;
            agent.SimulateFileFailures.EveryNth = 1000;
            agent.SimulateFileFailures.Counter = 999;                  // the very first file restored fails

            fixture.SwapToVolume(agent.VolumeToResumeFrom);
            TapeResult result = ResumeToTheEnd(fixture, agent);

            Assert.False(result.Success);
            Assert.Contains("Simulated", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(FileCount - 1, agent.Statistics.FilesSucceeded);
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// Design-TocLocation §7, step 3 on a HEADED volume: the media header at BOM is damaged and no TOC copy reads. The set
    ///  header past the header's filemark still identifies the volume, and the restore completes.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Restore_DamagedMediaHeaderAndUnreadableToc_IdentifiedBySetHeader_Restores(DriveProfile profile)
    {
        using var tree = SpanningTree();
        using var fixture = new MultiVolumeVirtualTapeFixture(profile, headerMode: VolumeHeaderMode.All);
        string restoreDir = MakeRestoreDir();
        try
        {
            using var agent = StartSpanningRestore(fixture, tree, restoreDir);
            int volume = agent.VolumeToResumeFrom;
            fixture.SwapToVolume(volume);
            fixture.Backend.ContentReadFaults.CorruptOnce(bits: 2, offset: 0);  // the first read: the BOM header
            agent.SimulateTOCFailureMask = -1;                                   // and no TOC copy reads

            TapeResult result = agent.ResumeRestoreFromAnotherVolume();
            Assert.False(agent.IsVolumeRejected, result.ErrorMessage);
            Assert.Contains(agent.TocAnomalies,
                a => a.Kind == TapeTocAnomalyKind.NoReadableCopy && a.Volume == volume && !a.IsRecovery);

            agent.SimulateTOCFailureMask = 0;
            while (!result.Success && agent.CanResumeFromAnotherVolume)
            {
                fixture.SwapToVolume(agent.VolumeToResumeFrom);
                result = agent.ResumeRestoreFromAnotherVolume();
            }
            Assert.True(result.Success, result.ErrorMessage);
            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    #endregion

    #region *** Backup ***

    /// <summary>
    /// The continuation cartridge refuses the media header write. The agent rejects it, rolls the TOC back to the previous
    ///  volume (nothing of the backup is on the rejected cartridge), and a retry finishes the backup; everything restores.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_ContinuationCartridgeRefusesTheHeader_IsRejected_ThenRetrySucceeds(DriveProfile profile)
    {
        using var tree = SpanningTree();
        using var fixture = new MultiVolumeVirtualTapeFixture(profile, headerMode: VolumeHeaderMode.All);
        string restoreDir = MakeRestoreDir();
        try
        {
            fixture.TOC.AddNewSetTOC();
            fixture.TOC.CurrentSetTOC.Description = "Rejected continuation";
            fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
            fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;

            using (var agent = fixture.CreateBackupAgent())
            {
                TapeResult result = agent.BackupFileListToCurrentSet(newSet: true, tree.Files);
                Assert.True(agent.CanResumeToNextVolume, "the backup must span");
                Assert.True(agent.BackupTOC() || agent.BackupTOC(enforce: true));
                int volumesBefore = fixture.TOC.Volume;
                int setsBefore = fixture.TOC.Count;

                fixture.SwapToNewVolume();
                fixture.Backend.ContentWriteFaults.FailOnce();          // the fresh cartridge refuses the media header
                result = agent.ResumeBackupToNextVolume();

                Assert.False(result.Success);
                Assert.Equal(1, fixture.Backend.ContentWriteFaults.Occurrences);
                Assert.True(agent.IsVolumeRejected);
                Assert.False(agent.CanResumeToNextVolume);             // a CanResume… loop now ends
                Assert.True(agent.IsMultiVolumePending);
                Assert.Equal(volumesBefore, fixture.TOC.Volume);       // rolled back: nothing reached the cartridge
                Assert.Equal(setsBefore, fixture.TOC.Count);
                Assert.True(fixture.TOC.ContinuedOnNextVolume);

                // Retry on the same cartridge — the fault was transient
                result = agent.ResumeBackupToNextVolume();
                while (!result.Success && agent.CanResumeToNextVolume)
                {
                    if (fixture.TOC.CurrentSetTOC.Count == 0)
                        fixture.TOC.RemoveLastEmptySet();
                    Assert.True(agent.BackupTOC() || agent.BackupTOC(enforce: true));
                    fixture.SwapToNewVolume();
                    result = agent.ResumeBackupToNextVolume();
                }
                Assert.True(result.Success, result.ErrorMessage);
                Assert.True(agent.LastResult.Success, "the rejected attempt must not linger in the diagnosis");
                Assert.True(agent.BackupTOC() || agent.BackupTOC(enforce: true));
                Assert.Equal(FileCount, agent.Statistics.FilesSucceeded);
            }

            fixture.TOC.MakeLastSetCurrent();
            var restored = fixture.RestoreAllFilesFromCurrentSet(restoreDir);
            Assert.Equal(FileCount, restored.FilesSucceeded);
            FileComparer.AssertFilesMatch(tree.RootPath, tree.Files, RestoreEquivalentRoot(restoreDir, tree.RootPath));
        }
        finally
        {
            TryDeleteDirectory(restoreDir);
        }
    }

    /// <summary>
    /// A file failure on volume 1 must survive the TOC save between volumes (the public <c>BackupTOC</c> resets the
    ///  latch) and still be the operation's diagnosis at the end.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Backup_EarlyFileFailure_SurvivesTheTocSaveBetweenVolumes(DriveProfile profile)
    {
        using var tree = SpanningTree();
        using var fixture = new MultiVolumeVirtualTapeFixture(profile);

        fixture.TOC.AddNewSetTOC();
        fixture.TOC.CurrentSetTOC.HashAlgorithm = TapeHashAlgorithm.Crc64;
        fixture.TOC.CurrentSetTOC.BlockSize = fixture.Drive.DefaultBlockSize;

        using var agent = fixture.CreateBackupAgent();
        agent.SimulateFileFailures.Enabled = true;
        agent.SimulateFileFailures.EveryNth = 1000;
        agent.SimulateFileFailures.Counter = 999;                      // the very first file fails (Skip by default)

        TapeResult result = agent.BackupFileListToCurrentSet(newSet: true, tree.Files, ignoreFailures: true);
        while (!result.Success && agent.CanResumeToNextVolume)
        {
            if (fixture.TOC.CurrentSetTOC.Count == 0)
                fixture.TOC.RemoveLastEmptySet();
            Assert.True(agent.BackupTOC() || agent.BackupTOC(enforce: true));
            fixture.SwapToNewVolume();
            result = agent.ResumeBackupToNextVolume();
        }

        Assert.True(fixture.TotalVolumes >= 2, "the backup must span");
        Assert.False(result.Success);
        Assert.Contains("Simulated", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FileCount - 1, agent.Statistics.FilesSucceeded);
    }

    #endregion
}
#endif
