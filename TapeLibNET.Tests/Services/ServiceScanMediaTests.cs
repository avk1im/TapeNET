using TapeLibNET.Agents;
using TapeLibNET.Calibration;
using TapeLibNET.Drive;
using TapeLibNET.Headers;
using TapeLibNET.Media;
using TapeLibNET.Scan;
using TapeLibNET.Services;
using TapeLibNET.Tests.Helpers;
using TapeLibNET.Toc;
using TapeLibNET.Virtual;
using Windows.Win32.Foundation;

namespace TapeLibNET.Tests.Services;


/// <summary>
/// Scan Media Phase 4: <see cref="TapeServiceBase.ScanMediaAsync"/>, <see cref="TapeServiceBase.RecoverTocAsync"/>,
///  and the pure judgement behind their reporting.
/// </summary>
/// <remarks>
/// <para>
/// Scanner behaviour itself is covered at the agent level; here the subject is the SERVICE: defaults,
///  advice, reporting, cancellation, the cartridge-swap guards, and adoption through the import path.
///  <see cref="ServiceTestBase"/>'s teardown asserts every un-examined host stayed prompt-free.
/// </para>
/// <para>
/// <b>Every tape-backed test runs on both media layouts</b> (<c>withInitiator</c> false / true): TOC in the content
///  partition, and TOC in its own partition (SM-14). The service must behave identically on both; only WHERE a TOC copy
///  stands may differ. Exceptions: the pure tests (no drive) and the calibration cartridge (its own single-partition
///  drive).
/// </para>
/// </remarks>
public class ServiceScanMediaTests : ServiceTestBase
{
    #region *** Helpers ***

    /// <summary>Formats a cartridge — partitioned or not, as <paramref name="media"/> says — and backs up sets on it.</summary>
    private async Task<(TestTapeService svc, TestTapeServiceHost host, List<TempFileTree> trees)>
        SeedAsync(TempVirtualMedia media, int sets = 2)
    {
        var (svc, host) = await OpenAndFormatAsync(media);
        var trees = new List<TempFileTree>();
        for (int i = 1; i <= sets; i++)
        {
            var tree = new TempFileTree();
            tree.AddFiles($"scan{i}", count: 3, minSize: 512, maxSize: 4_096);
            trees.Add(tree);
            var result = await svc.ExecuteBackupAsync(
                MakeBackupRequest(svc, tree.RootPath, $"Set-{i}", append: i > 1));
            Assert.True(result.Success, $"seeding backup #{i} failed: {result.Message}");
        }
        return (svc, host, trees);
    }

    private static void DisposeAll(List<TempFileTree> trees)
    {
        foreach (var t in trees)
            t.Dispose();
    }

    private static TempVirtualMedia NewMedia(bool withInitiator = false)
        => new(withInitiator, ContentCapacity, InitiatorCapacity);

    private static int FirstTocOrdinal(MediaScanMap map)
        => map.Fragments.First(f => f.Kind == FragmentKind.TOC).Ordinal;

    /// <summary>Where the layout keeps its TOC — and so where the scan must find the copies.</summary>
    private static MediaPartition TocPartition(bool withInitiator)
        => withInitiator ? MediaPartition.Initiator : MediaPartition.Content;

    // ── Hand-built maps for the pure tests ──

    private static readonly TapeMediaLayout s_layout = new(
        nameof(TapeNavigatorTOCInSetWithSmks), UseSmks: true, TocInPartition: false, HasTocMark: false, MediaLoaded: true);

    private static readonly TapeMediaLayout s_partitionedLayout = new(
        nameof(TapeNavigatorTOCInPartition), UseSmks: true, TocInPartition: true, HasTocMark: false, MediaLoaded: true);

    private static readonly Guid s_id = Guid.NewGuid();

    private static MediaScanMap Map(ScannedMediaKind kind, params TapeMediaFragment[] fragments)
        => new()
        {
            Layout = s_layout,
            Kind = kind,
            Fragments = [.. fragments.Select((f, i) => f with { Ordinal = i })],
            ScannedUtc = DateTime.UtcNow,
            TerminatorWin32 = (uint)WIN32_ERROR.ERROR_NO_DATA_DETECTED,
        };

    private static TapeMediaFragment Media() => new()
    { Ordinal = 0, StartBlock = 0, Kind = FragmentKind.MediaHeader, ClosedBySeparator = true, Id = s_id, Volume = 1, Description = "M" };

    private static TapeMediaFragment Set(long block, int vsi, bool closed = true) => new()
    { Ordinal = 0, StartBlock = block, Kind = FragmentKind.SetHeader, ClosedBySeparator = closed, Id = s_id, Volume = 1, VolumeSetIndex = vsi, Description = $"S{vsi}" };

    private static TapeMediaFragment Toc(long block, bool recovered = false) => new()
    { Ordinal = 0, StartBlock = block, Kind = FragmentKind.TOC, ClosedBySeparator = true, Id = s_id, HarvestedToc = recovered ? new TapeTOC("R") : null };

    #endregion

    #region *** (A) Pure judgement — no drive ***

    [Fact]
    public void Verbalize_SoundCartridge_IsCompleted()
    {
        var (level, headline, _) = TapeServiceBase.VerbalizeScan(
            Map(ScannedMediaKind.Backup, Media(), Set(2, 0), Set(5, 1), Toc(8)));

        Assert.Equal(ServiceReportLevel.Completed, level);
        Assert.Contains("2 backup set(s)", headline);
        Assert.Contains("sound", headline);
    }

    [Fact]
    public void Verbalize_UnclosedLastSet_NeedsAttention_AndNamesIt()
    {
        var (level, headline, details) = TapeServiceBase.VerbalizeScan(
            Map(ScannedMediaKind.Backup, Media(), Set(2, 0), Set(5, 1, closed: false)));

        Assert.Equal(ServiceReportLevel.Warning, level);
        Assert.Contains("needs attention", headline);
        Assert.Contains(details, d => d.Contains("never completed") && d.Contains(">S1<"));
    }

    [Fact]
    public void Verbalize_IndexGap_IsReportedOneBased()
    {
        var (_, _, details) = TapeServiceBase.VerbalizeScan(
            Map(ScannedMediaKind.Backup, Media(), Set(2, 0), Set(5, 2)));

        Assert.Contains(details, d => d.Contains("#2"));
    }

    [Fact]
    public void Verbalize_Blank_Calibration_Foreign_HaveTheirOwnHeadlines()
    {
        Assert.Contains("Blank", TapeServiceBase.VerbalizeScan(Map(ScannedMediaKind.Blank)).Headline);
        Assert.Contains("Calibration", TapeServiceBase.VerbalizeScan(Map(ScannedMediaKind.CalibrationCartridge)).Headline);

        var (Level, _ /*Headline*/, _ /*Details*/) = TapeServiceBase.VerbalizeScan(Map(ScannedMediaKind.Foreign,
            new TapeMediaFragment { Ordinal = 0, StartBlock = 0, Kind = FragmentKind.Unknown }));
        Assert.Equal(ServiceReportLevel.Warning, Level);
    }

    /// <summary>An aborted scan says so — at Failed, the user-abort level — never "sound".</summary>
    [Fact]
    public void Verbalize_AbortedScan_IsFailed_NotSound()
    {
        var map = Map(ScannedMediaKind.Backup, Media(), Set(2, 0)) with
        {
            Truncated = true,
            TerminatorWin32 = (uint)WIN32_ERROR.ERROR_CANCELLED,
        };

        var (level, headline, _) = TapeServiceBase.VerbalizeScan(map);
        Assert.Equal(ServiceReportLevel.Failed, level);
        Assert.Contains("aborted", headline);
    }

    /// <summary>
    /// The TOC partition, reported for what it is (SM-14): EMPTY — walked, nothing there — and NOT SCANNED must never
    ///  read the same.
    /// </summary>
    [Fact]
    public void Verbalize_TocPartition_EmptyAndNotScanned_ReadDifferently()
    {
        static MediaScanMap Partitioned(bool truncated) => Map(ScannedMediaKind.Backup, Media(), Set(2, 0)) with
        {
            Layout = s_partitionedLayout,
            Truncated = truncated,
            TerminatorWin32 = truncated ? (uint)WIN32_ERROR.ERROR_INVALID_DATA : (uint)WIN32_ERROR.ERROR_NO_DATA_DETECTED,
            Partitions =
            [
                new ScannedPartition(MediaPartition.Content, 2, Truncated: false, (uint)WIN32_ERROR.ERROR_NO_DATA_DETECTED),
                new ScannedPartition(MediaPartition.Initiator, 0, Truncated: truncated,
                    truncated ? (uint)WIN32_ERROR.ERROR_INVALID_DATA : (uint)WIN32_ERROR.ERROR_NO_DATA_DETECTED),
            ],
        };

        var (_, _, empty) = TapeServiceBase.VerbalizeScan(Partitioned(truncated: false));
        var (_, _, unscanned) = TapeServiceBase.VerbalizeScan(Partitioned(truncated: true));

        Assert.Contains(empty, d => d.Contains("TOC partition is empty"));
        Assert.DoesNotContain(empty, d => d.Contains("not scanned"));
        Assert.Contains(unscanned, d => d.Contains("not scanned"));
        Assert.DoesNotContain(unscanned, d => d.Contains("TOC partition is empty"));
    }

    [Fact]
    public void Advise_Calibration_OnlyInspect()
        => Assert.Equal([ScanAdvice.InspectCalibrationCartridge],
            TapeServiceBase.AdviseOnScan(Map(ScannedMediaKind.CalibrationCartridge)));

    /// <summary>Adopt and recover exclude each other — once a copy is recovered, "recover one" is noise.</summary>
    [Fact]
    public void Advise_RecoveredCopy_OffersAdopt_NotRecover()
    {
        var advice = TapeServiceBase.AdviseOnScan(
            Map(ScannedMediaKind.Backup, Media(), Set(2, 0), Toc(5, recovered: false), Toc(7, recovered: true)));

        Assert.Contains(ScanAdvice.AdoptRecoveredToc, advice);
        Assert.DoesNotContain(ScanAdvice.RecoverTocFromCopy, advice);
    }

    [Fact]
    public void Advise_CopiesNotRecovered_AndUnclosedSet()
    {
        var advice = TapeServiceBase.AdviseOnScan(
            Map(ScannedMediaKind.Backup, Media(), Set(2, 0, closed: false), Toc(5)));

        Assert.Equal([ScanAdvice.RecoverTocFromCopy, ScanAdvice.ReviewUnclosedSet], advice);
    }

    [Fact]
    public void Advise_SoundCartridgeWithoutCopies_IsEmpty()
        => Assert.Empty(TapeServiceBase.AdviseOnScan(Map(ScannedMediaKind.Backup, Media(), Set(2, 0))));

    #endregion

    #region *** (B) ScanMediaAsync ***

    /// <summary>
    /// The default service scan: sets found, TOC copies recovered — where the layout keeps them — the right advice, and
    ///  silence on the identity channel.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scan_HealthyBackup_FindsSetsAndRecoversEveryCopy(bool withInitiator)
    {
        using var media = NewMedia(withInitiator);
        var (svc, host, trees) = await SeedAsync(media);
        try
        {
            using (svc)
            {
                var result = await svc.ScanMediaAsync(new ScanMediaRequest());

                Assert.True(result.Success, $"{result.Message}\n{host.DumpReports()}");
                Assert.Equal(ServiceReportLevel.Completed, result.Outcome);
                Assert.Equal(ScannedMediaKind.Backup, result.MediaKind);
                Assert.Equal(2, result.SetsFound);
                Assert.True(result.TocCopiesFound >= 2, ScanMapAssert.Describe(result.Map));
                Assert.Equal(result.TocCopiesFound, result.TocsRecovered);
                Assert.Contains(ScanAdvice.AdoptRecoveredToc, result.Advice);
                Assert.True(host.ContainsMessage("looks sound"), host.DumpReports());

                MediaScanMap map = result.Map!;
                ScanMapAssert.WellFormed(map);
                ScanMapAssert.Complete(map);
                ScanMapAssert.TocCopiesWhereTheLayoutPutsThem(map);
                ScanMapAssert.NothingOutOfPlace(map);
                Assert.Equal(withInitiator ? 2 : 1, map.Partitions.Count);
            }
            AssertNoMediaPrompts(host);
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scan_WithoutRecovery_AdvisesRecoverFromCopy(bool withInitiator)
    {
        using var media = NewMedia(withInitiator);
        var (svc, _, trees) = await SeedAsync(media, sets: 1);
        try
        {
            using (svc)
            {
                var result = await svc.ScanMediaAsync(new ScanMediaRequest { RecoverTocCopies = false });

                Assert.True(result.Success);
                Assert.True(result.TocCopiesFound >= 1, ScanMapAssert.Describe(result.Map));
                Assert.Equal(0, result.TocsRecovered);
                Assert.Contains(ScanAdvice.RecoverTocFromCopy, result.Advice);
            }
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>The scan informs; it never replaces the loaded TOC.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scan_DoesNotMutateLiveToc(bool withInitiator)
    {
        using var media = NewMedia(withInitiator);
        var (svc, _, trees) = await SeedAsync(media);
        try
        {
            using (svc)
            {
                TapeTOC? before = svc.TOC;
                int count = before!.Count;

                await svc.ScanMediaAsync(new ScanMediaRequest());

                Assert.Same(before, svc.TOC);
                Assert.Equal(count, svc.TOC!.Count);
            }
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// A calibration cartridge is identified and advised on — never inspected by the scan: the calibration
    ///  UI owns that answer.
    /// </summary>
    /// <remarks>A Fact, not a Theory: calibration runs on its own single-partition, large-block drive.</remarks>
    [Fact]
    public async Task Scan_CalibrationCartridge_AdvisesInspect_AndDoesNotInspect()
    {
        const long capacity = 64L * 1024 * 1024;
        var (svc, _ /*host*/) = CreateService();
        using (svc)
        {
            var vmd = new VirtualMediaDescriptor("memory-calibration", capacity, null, 0, InMemory: true);
            Assert.True(await svc.OpenVirtualDriveAsync(VirtualTapeDriveCapabilities.WithFilemarksOnlyLargeBlocks,
                vmd, ewProfile: VirtualTapeEwProfile.EmulatedOverreport(capacity)), svc.LastError);
            Assert.True(await svc.LoadMediaAsync(), svc.LastError);
            var cal = await svc.ExecuteCalibrateAsync(new CalibrateRequest(EjectWhenDone: false,
                Options: new TapeCalibrationOptions { SampleCount = 40, NumCheckpoints = 16 }));
            Assert.True(cal.Success, cal.Message);

            var result = await svc.ScanMediaAsync(new ScanMediaRequest());

            Assert.True(result.Success, result.Message);
            Assert.Equal(ScannedMediaKind.CalibrationCartridge, result.MediaKind);
            Assert.Equal([ScanAdvice.InspectCalibrationCartridge], result.Advice);
            Assert.Null(result.Map!.CalibrationInfo);
            Assert.Null(svc.CalibrationInfo);
            Assert.IsType<TapeCalibrationHeader>(svc.LoadedHeader);   // identity refreshed by the scan
        }
    }

    /// <summary>
    /// The request's token aborts the scan cleanly: a truncated map is still returned, classified as the
    ///  user's decision — not a fault. On partitioned media the abort comes during the content walk, so the TOC
    ///  partition is reported NOT SCANNED — never blank.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scan_Cancellation_ViaRequestToken_YieldsAbortedTruncatedMap(bool withInitiator)
    {
        using var media = NewMedia(withInitiator);
        var (svc, host, trees) = await SeedAsync(media);
        try
        {
            using (svc)
            {
                using var cts = new CancellationTokenSource();
                svc.OnScanProgress = p => { if (p.Fragment is not null) cts.Cancel(); };

                var result = await svc.ScanMediaAsync(new ScanMediaRequest { Cancellation = cts.Token });

                Assert.False(result.Success);
                Assert.True(result.WasAborted, $"outcome {result.Outcome}");
                Assert.Equal((uint)WIN32_ERROR.ERROR_CANCELLED, result.ErrorCode);
                Assert.NotNull(result.Map);
                Assert.True(result.Map!.Truncated, ScanMapAssert.Describe(result.Map));
                Assert.True(host.ContainsMessage("aborted"), host.DumpReports());
                if (withInitiator)
                    ScanMapAssert.PartitionNotScanned(result.Map, MediaPartition.Initiator);
            }
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scan_MapExport_WritesReloadableJson(bool withInitiator)
    {
        using var media = NewMedia(withInitiator);
        var (svc, _, trees) = await SeedAsync(media);
        try
        {
            using (svc)
            {
                string folder = Path.Combine(media.Root, "scans");
                var result = await svc.ScanMediaAsync(new ScanMediaRequest { MapExportFolder = folder });

                Assert.NotNull(result.MapExportPath);
                Assert.True(File.Exists(result.MapExportPath));
                MediaScanMap? back = MediaScanMap.FromJson(File.ReadAllText(result.MapExportPath!));
                Assert.NotNull(back);
                Assert.Equal(result.Map!.Fragments.Count, back!.Fragments.Count);
                Assert.Equal(result.SetsFound, back.SetCount);
                // The partition survives the round trip — per fragment and per partition.
                Assert.Equal(result.Map.Fragments.Select(f => f.Partition), back.Fragments.Select(f => f.Partition));
                Assert.Equal(result.Map.Partitions, back.Partitions);
            }
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    #endregion

    #region *** (C) RecoverTocAsync ***

    /// <summary>A copy recovered during the scan is used as is — no tape I/O at all, in either partition.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recover_FromMap_UsesTheHarvest_WithoutTapeIo(bool withInitiator)
    {
        using var media = NewMedia(withInitiator);
        var (svc, _, trees) = await SeedAsync(media);
        try
        {
            using (svc)
            {
                MediaScanMap map = (await svc.ScanMediaAsync(new ScanMediaRequest())).Map!;

                // Fail EVERY read from here on, in BOTH partitions: any tape access would show up as an occurrence —
                //  on partitioned media the copy stands in the TOC partition, which the content injector never sees.
                var backend = svc.VirtualBackend!;
                foreach (var faults in new[] { backend.ContentReadFaults, backend.InitiatorReadFaults })
                {
                    faults.Enabled = true;
                    faults.EveryNth = 1;
                }

                var result = await svc.RecoverTocAsync(new RecoverTocRequest(map, FirstTocOrdinal(map)));

                Assert.Equal(0, backend.ContentReadFaults.Occurrences + backend.InitiatorReadFaults.Occurrences);
                backend.ContentReadFaults.Reset();
                backend.InitiatorReadFaults.Reset();
                Assert.True(result.Success, result.Message);
                Assert.True(result.FromMap);
                Assert.False(result.Adopted);
                Assert.Equal(2, result.RecoveredTOC!.Count);
            }
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// A copy the scan only detected is read from tape by its block — in the partition it stands in. On partitioned
    ///  media that is the TOC partition (the former <c>Recover_FromTape_InTheTocPartition</c>).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recover_FromTape_WhenTheScanDidNotRecover(bool withInitiator)
    {
        using var media = NewMedia(withInitiator);
        var (svc, _, trees) = await SeedAsync(media);
        try
        {
            using (svc)
            {
                MediaScanMap map = (await svc.ScanMediaAsync(new ScanMediaRequest { RecoverTocCopies = false })).Map!;
                var copy = map.Fragments.First(f => f.Kind == FragmentKind.TOC);
                Assert.Equal(TocPartition(withInitiator), copy.Partition);

                var result = await svc.RecoverTocAsync(new RecoverTocRequest(map, copy.Ordinal));

                Assert.True(result.Success, result.Message);
                Assert.False(result.FromMap);
                Assert.Equal(svc.TOC!.Count, result.RecoveredTOC!.Count);
                Assert.Equal(svc.TOC.MediaId, result.RecoveredTOC.MediaId);
            }
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>
    /// The guard the feature must never lose: a map of cartridge A never reads a TOC off cartridge B.
    /// </summary>
    /// <remarks>
    /// Same layout on both cartridges: the media-identity guard refuses. A partitioned map against a single-partition
    ///  cartridge is refused even earlier, by Guard 0 (§8.4) — see the third row.
    /// </remarks>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Recover_AfterCartridgeSwap_Refuses(bool aWithInitiator, bool bWithInitiator)
    {
        using var mediaA = NewMedia(aWithInitiator);
        using var mediaB = NewMedia(bWithInitiator);
        var (svcB, _) = await OpenAndFormatAsync(mediaB);   // B: a different series
        svcB.Dispose();

        var (svc, _, trees) = await SeedAsync(mediaA);
        try
        {
            using (svc)
            {
                MediaScanMap mapA = (await svc.ScanMediaAsync(new ScanMediaRequest { RecoverTocCopies = false })).Map!;

                Assert.True(svc.InsertVirtualMedia(new VirtualMediaDescriptor(
                    mediaB.ContentPath, mediaB.ContentCapacity,
                    mediaB.InitiatorPath, bWithInitiator ? InitiatorCapacity : 0), FileMode.Open), svc.LastError);
                Assert.True(await svc.LoadMediaAsync(), svc.LastError);

                var result = await svc.RecoverTocAsync(new RecoverTocRequest(mapA, FirstTocOrdinal(mapA)) { Adopt = true });

                Assert.False(result.Success);
                Assert.Null(result.RecoveredTOC);
                Assert.False(result.Adopted);
                Assert.Equal((uint)WIN32_ERROR.ERROR_MEDIA_CHANGED, result.ErrorCode);
            }
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    /// <summary>Adoption goes through the import path: identity-checked, flagged as not navigator-located.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recover_Adopt_ReplacesTheToc_ThroughTheImportPath(bool withInitiator)
    {
        using var media = NewMedia(withInitiator);
        var (svc, host, trees) = await SeedAsync(media);
        try
        {
            using (svc)
            {
                MediaScanMap map = (await svc.ScanMediaAsync(new ScanMediaRequest())).Map!;
                TapeTOC? before = svc.TOC;

                var result = await svc.RecoverTocAsync(new RecoverTocRequest(map, FirstTocOrdinal(map)) { Adopt = true });

                Assert.True(result.Success, result.Message);
                Assert.True(result.Adopted);
                Assert.NotSame(before, svc.TOC);
                Assert.Equal(2, svc.TOC!.Count);
                Assert.True(svc.TOCIsFrom is TOCSource.Recovered, "a recovered TOC must be marked as one");
                Assert.Contains(host.StateChanges, c => c.HasFlag(ServiceStateChange.TocChanged));
            }
            AssertNoMediaPrompts(host);   // same cartridge ⇒ Match ⇒ silent
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recover_SaveToFile_RoundTripsThroughImport(bool withInitiator)
    {
        using var media = NewMedia(withInitiator);
        var (svc, _, trees) = await SeedAsync(media);
        try
        {
            using (svc)
            {
                MediaScanMap map = (await svc.ScanMediaAsync(new ScanMediaRequest())).Map!;
                string path = Path.Combine(media.Root, "recovered" + TapeAgentBase.TOCFileExtension);

                var result = await svc.RecoverTocAsync(new RecoverTocRequest(map, FirstTocOrdinal(map)) { SaveToFilePath = path });

                Assert.Equal(path, result.SavedPath);
                Assert.True(await svc.ImportTOCFromFileAsync(path), svc.LastError);
                Assert.Equal(2, svc.TOC!.Count);
            }
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recover_NonTocFragment_IsRejected(bool withInitiator)
    {
        using var media = NewMedia(withInitiator);
        var (svc, _, trees) = await SeedAsync(media, sets: 1);
        try
        {
            using (svc)
            {
                MediaScanMap map = (await svc.ScanMediaAsync(new ScanMediaRequest())).Map!;
                int setOrdinal = map.Fragments.First(f => f.Kind == FragmentKind.SetHeader).Ordinal;

                var result = await svc.RecoverTocAsync(new RecoverTocRequest(map, setOrdinal));

                Assert.False(result.Success);
                Assert.Equal((uint)WIN32_ERROR.ERROR_INVALID_PARAMETER, result.ErrorCode);
            }
        }
        finally
        {
            DisposeAll(trees);
        }
    }

    #endregion
}
