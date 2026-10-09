using TapeLibNET.Agents;
using TapeLibNET.Headers;
using TapeLibNET.Scan;
using TapeLibNET.Toc;
using Windows.Win32.Foundation;

namespace TapeLibNET.Services;


public partial class TapeServiceBase
{
    #region *** Scan Media ***

    /// <summary>
    /// Surveys the loaded cartridge from BOM to EOD with no TOC assumed, and — by default — recovers every
    ///  table-of-contents copy it finds. READ-ONLY: nothing on the tape changes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No identity prompt.</b> A scan has no expectation to violate: it reports whatever cartridge is
    ///  loaded. It refreshes <see cref="LoadedHeader"/> on the way, since a scan may be the first thing to
    ///  establish what the cartridge is.
    /// </para>
    /// <para>
    /// <b>Never touches <see cref="TOC"/>.</b> A recovered TOC stays inside the map until the user adopts it
    ///  via <see cref="RecoverTocAsync"/> — the scan informs, the user decides.
    /// </para>
    /// <para>
    /// <b>Calibration cartridges are identified, never inspected here.</b> The result advises
    ///  <see cref="ScanAdvice.InspectCalibrationCartridge"/> instead: the calibration UI owns that answer
    ///  and can act on it (Resume, Recalibrate).
    /// </para>
    /// </remarks>
    public Task<ScanMediaResult> ScanMediaAsync(ScanMediaRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        _host.OnServiceStateChanged(ServiceStateChange.OperationStarted);

        return Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                return ScanMediaCore(request);
            }
            finally
            {
                _operationLock.Release();
                _host.OnServiceStateChanged(ServiceStateChange.OperationEnded);
            }
        });
    }

    private ScanMediaResult ScanMediaCore(ScanMediaRequest request)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        ServiceScanProgressHandler? progressHandler = null;

        ScanMediaResult Failed(TapeResult diagnosis, ServiceReportLevel outcome = ServiceReportLevel.Error,
                               MediaScanMap? map = null, Exception? ex = null)
            => new()
            {
                Diagnosis = diagnosis,
                Success = false,
                Outcome = outcome,
                Map = map,
                MediaKind = map?.Kind ?? ScannedMediaKind.Blank,
                Duration = timer.Elapsed,
                ErrorException = ex,
            };

        if (_drive is null || !_drive.IsMediaLoaded)
        {
            LastError = "Media not loaded";
            return Failed(TapeResult.Fail((uint)WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE, LastError));
        }

        try
        {
            if (!_drive.PrepareMedia())
            {
                LastError = _drive.LastErrorMessage;
                LogErr($"Couldn't prepare media. Error: {LastError}");
                return Failed(TapeResult.Fail(_drive));
            }

            // A scan may be the first thing to establish what this cartridge is — keep the identity current.
            RefreshLoadedHeader();

            LogInfo("Scanning media...");
            OnStatusUpdate("Scanning media...");

            // Calibration inspection is deliberately OFF: the service advises the calibration UI instead.
            var scanner = new TapeScanner(_drive)
            {
                Options = new ScanMediaOptions
                {
                    HarvestTocCopies = request.RecoverTocCopies,
                    InspectCalibrationTrail = false,
                },
            };

            progressHandler = CreateScanProgressHandler(scanner, request);

            // Both tokens map onto the scanner's ONE cooperative abort channel, as for calibration.
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                OperationCancellationToken, request.Cancellation);
            using var ctReg = linkedCancellation.Token.Register(() => scanner.IsAbortRequested = true);

            MediaScanMap? map = scanner.Scan(progressHandler);
            timer.Stop();

            if (map is null)
            {
                TapeResult diag = scanner.LastResult;
                LastError = diag.ErrorMessage;
                LogErr($"Scan failed: {diag.ErrorMessage}");
                OnStatusUpdate("Scan failed");
                return Failed(diag);
            }

            // A map came back — possibly truncated. Tell an abort from a fault: both truncate, only one is
            //  the user's decision.
            bool aborted = map.TerminatorWin32 == (uint)WIN32_ERROR.ERROR_CANCELLED;
            bool faulted = !aborted && !scanner.LastResult.Success;

            var (level, headline, details) = VerbalizeScan(map);

            _host.Report(level, headline);
            foreach (string line in details)
                _host.Report(level == ServiceReportLevel.Completed ? ServiceReportLevel.Info : level,
                    line, isSubEntry: true);

            IReadOnlyList<ScanAdvice> advice = AdviseOnScan(map);
            foreach (ScanAdvice a in advice)
                LogInfoSub(ScanAdviceText(a));

            string? exportPath = request.MapExportFolder is { } folder ? ExportScanMap(map, folder) : null;

            OnStatusUpdate(aborted ? "Scan aborted" : "Scan complete");
            progressHandler.CompleteProgress();

            return new ScanMediaResult
            {
                Diagnosis = aborted ? TapeResult.Fail((uint)WIN32_ERROR.ERROR_CANCELLED, "Scan aborted by user request")
                          : faulted ? scanner.LastResult
                          : TapeResult.OK,
                Success = !aborted && !faulted,
                Outcome = aborted ? ServiceReportLevel.Failed
                        : faulted ? ServiceReportLevel.Error
                        : level,
                Duration = timer.Elapsed,
                Map = map,
                MediaKind = map.Kind,
                SetsFound = map.SetCount,
                TocCopiesFound = map.TocCopyCount,
                TocsRecovered = CountRecoveredTocs(map),
                UnknownFragments = map.UnknownCount,
                LastSetUnclosed = map.LastSetUnclosed,
                Advice = advice,
                MapExportPath = exportPath,
                Summary = headline,
            };
        }
        catch (Exception ex)
        {
            timer.Stop();
            LastError = ex.Message;
            LogErr($"Scan failed: {ex.Message}");
            OnStatusUpdate("Scan failed");
            return Failed(TapeResult.Fail(ex), ex: ex);
        }
        finally
        {
            progressHandler?.DisposeProgress();
        }
    }

    /// <summary>Creates the progress handler for a scan. Override to add a progress display.</summary>
    protected virtual ServiceScanProgressHandler CreateScanProgressHandler(
        TapeScanner scanner, ScanMediaRequest request)
        => new(_host, scanner);

    /// <summary>
    /// Writes the map as JSON into <paramref name="folder"/>. Best-effort: a failed export is logged and
    ///  returns null — the scan itself succeeded, and the map is still in the result.
    /// </summary>
    private string? ExportScanMap(MediaScanMap map, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);

            string id = map.MediaId is { } g ? g.ToString("N")[..8] : "unidentified";
            string path = Path.Combine(folder,
                $"Scan_{id}_{map.ScannedUtc:yyyyMMdd-HHmmss}{MediaScanMap.MapFileExtension}");

            File.WriteAllText(path, map.ToJson());
            LogInfoSub($"Scan map saved: {path}");
            return path;
        }
        catch (Exception ex)
        {
            LogWarnSub($"Couldn't save the scan map: {ex.Message}");
            return null;
        }
    }

    private static int CountRecoveredTocs(MediaScanMap map)
        => map.Fragments.Count(f => f.Kind == FragmentKind.TOC && f.HarvestedToc is not null);

    #endregion

    #region *** Recover TOC ***

    /// <summary>
    /// Recovers the table of contents from one TOC-copy fragment of a scan map, and optionally saves it
    ///  and/or adopts it as the current <see cref="TOC"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No tape I/O when the scan already recovered it</b> (<see cref="TapeMediaFragment.HarvestedToc"/>):
    ///  the copy is taken from the map. This is what backs the UI's "use this TOC" button.
    /// </para>
    /// <para>
    /// <b>Otherwise read from tape, guarded twice.</b> Between scan and recovery the user may have swapped
    ///  cartridges, and reading — then adopting — a TOC off the wrong one is the worst outcome this feature
    ///  could produce:
    ///  <list type="number">
    ///   <item>BEFORE reading: the loaded cartridge's media id must be the scanned one.</item>
    ///   <item>AFTER reading: the recovered TOC's media id must be the one the scan peeked at that block.</item>
    ///  </list>
    ///  The second check verifies the CRC-validated result rather than peeking the block first — stronger,
    ///  and a read is harmless: nothing is adopted until both checks pass.
    /// </para>
    /// <para>
    /// <b>Adoption goes through the import path</b> — the same identity verdict and prompt as
    ///  <see cref="ImportTOCFromFileAsync"/>, never implicit (SM-13).
    /// </para>
    /// </remarks>
    public Task<RecoverTocResult> RecoverTocAsync(RecoverTocRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        _host.OnServiceStateChanged(ServiceStateChange.OperationStarted);

        return Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                return RecoverTocCore(request);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogErr($"TOC recovery failed: {ex.Message}");
                return RecoverTocResult.Failed(TapeResult.Fail(ex)) with { ErrorException = ex };
            }
            finally
            {
                _operationLock.Release();
                _host.OnServiceStateChanged(ServiceStateChange.OperationEnded);
            }
        });
    }

    private RecoverTocResult RecoverTocCore(RecoverTocRequest request)
    {
        RecoverTocResult Fail(WIN32_ERROR code, string message)
        {
            LastError = message;
            LogErr(message);
            return RecoverTocResult.Failed(TapeResult.Fail((uint)code, message));
        }

        MediaScanMap map = request.Map;

        if (request.FragmentOrdinal < 0 || request.FragmentOrdinal >= map.Fragments.Count
            || map.Fragments[request.FragmentOrdinal] is not { Kind: FragmentKind.TOC } copy)
        {
            return Fail(WIN32_ERROR.ERROR_INVALID_PARAMETER,
                $"Fragment #{request.FragmentOrdinal} is not a table-of-contents copy");
        }

        TapeTOC toc;
        bool fromMap;

        if (copy.HarvestedToc is { } harvested)
        {
            // A private copy: adopting must not alias the map's object, which the caller still holds.
            toc = new TapeTOC(harvested);
            fromMap = true;
            LogInfo($"Using the table of contents recovered by the scan at block {copy.StartBlock}");
        }
        else
        {
            if (_drive is null || !_drive.IsMediaLoaded)
                return Fail(WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE, "Media not loaded");

            if (!_drive.PrepareMedia())
                return Fail(WIN32_ERROR.ERROR_NOT_READY, $"Couldn't prepare media: {_drive.LastErrorMessage}");

            // Guard 1 — is this the scanned cartridge? Cheap: one BOM block, no content read.
            RefreshLoadedHeader();

            if (map.MediaId is { } scannedId && LoadedMediaHeader?.MediaId != scannedId)
                return Fail(WIN32_ERROR.ERROR_MEDIA_CHANGED,
                    "The loaded cartridge is not the one that was scanned — refusing to read its table of contents");

            LogInfo($"Recovering the table of contents at block {copy.StartBlock}...");
            OnStatusUpdate("Recovering TOC...");

            toc = new TapeTOC();
            using var probe = new TapeAgentBase(_drive, toc);

            using var ctReg = OperationCancellationToken.Register(() => probe.IsAbortRequested = true);
            using var reqReg = request.Cancellation.Register(() => probe.IsAbortRequested = true);

            TapeResult read = probe.RestoreTOCAt(copy.StartBlock);

            if (!read)
            {
                LastError = read.ErrorMessage;
                LogErr($"Couldn't recover the table of contents at block {copy.StartBlock}: {read.ErrorMessage}");
                return RecoverTocResult.Failed(read);
            }

            // Guard 2 — is what we read the copy the scan identified? Checked on the CRC-validated result.
            if (copy.Id is { } peekedId && toc.MediaId != peekedId)
                return Fail(WIN32_ERROR.ERROR_MEDIA_CHANGED,
                    $"The table of contents at block {copy.StartBlock} belongs to a different series than the " +
                    "scanned copy — not recovered");

            fromMap = false;
        }

        LogOk($"Table of contents recovered: {toc.Count} backup set(s)");

        // ── Save (best-effort: the recovery itself already succeeded) ──
        string? savedPath = null;
        bool saveFailed = false;

        if (request.SaveToFilePath is { } path)
        {
            if (_drive is null || !_drive.IsMediaLoaded)
            {
                LogWarnSub("Couldn't save the recovered TOC: saving needs the drive open and media loaded");
                saveFailed = true;
            }
            else
            {
                using var saver = new TapeAgentBase(_drive, toc);
                TapeResult saved = saver.SaveTOCToFile(path);

                if (saved)
                {
                    savedPath = path;
                    LogInfoSub($"Recovered TOC saved: {path}");
                }
                else
                {
                    LogWarnSub($"Couldn't save the recovered TOC: {saved.ErrorMessage}");
                    saveFailed = true;
                }
            }
        }

        // ── Adopt ──
        bool adopted = request.Adopt && AdoptRecoveredToc(toc, request.ProceedOnMediaMismatch, savedPath);

        if (request.Adopt && !adopted)
            LogWarn("The recovered table of contents was NOT adopted");

        OnStatusUpdate(adopted ? $"TOC recovered: {toc.Count} backup set(s)" : "TOC recovered");

        return new RecoverTocResult
        {
            Diagnosis = TapeResult.OK,
            Success = true,
            Outcome = saveFailed || (request.Adopt && !adopted)
                ? ServiceReportLevel.Warning
                : ServiceReportLevel.Completed,
            Toc = toc,
            FromMap = fromMap,
            Adopted = adopted,
            SavedPath = savedPath,
        };
    }

    /// <summary>
    /// Makes a recovered TOC the current one, through the same identity check as an import.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mirrors <see cref="ImportTOCFromFileAsync"/> step for step: verdict against the loaded header, a
    ///  one-off prompt (no ProceedAlways), and on Proceed the mounted volume's number is adopted — never
    ///  its MediaId (§10.8).
    /// </para>
    /// <para>
    /// <b>Sets <see cref="TOCIsFrom"/> to <see cref="TOCSource.Recovered"/>.</b> The navigator did not
    ///  locate this TOC, and a copy found mid-tape may be older than the cartridge. Every consumer that
    ///  treats an imported TOC with caution — e.g. a delete navigating from begin-of-content — must treat
    ///  this one the same way.
    /// </para>
    /// </remarks>
    private bool AdoptRecoveredToc(TapeTOC toc, bool suppressPrompt, string? savedPath)
    {
        if (_drive?.IsMediaLoaded == true)
        {
            RefreshLoadedHeader();

            var choice = PresentVerdict(
                EvaluateLoadedHeader(expectedSeriesId: toc.MediaId, expectedVolume: toc.Volume),
                MediaPromptContext.ImportToc, suppress: suppressPrompt, allowProceedAlways: false);

            if (choice == MediaMismatchChoice.Abort)
                return false;

            if (_loadedHeader is TapeMediaHeader mh)
                toc.Volume = mh.Volume;
        }

        _agent?.Dispose();
        _agent = null;
        _toc = toc;
        TOCIsFrom = TOCSource.Recovered;
        TOCFilePath = savedPath;

        LogOk($"Recovered table of contents adopted: {toc.Count} backup set(s)");
        LogTOCInfo();

        LogWarn("Using recovered TOC - on-tape TOC may be missing or corrupt");
        OnImportTOCExtra();
        OnStatusUpdate($"TOC recovered: {_toc.Count} backup set(s)");

        _host.OnServiceStateChanged(ServiceStateChange.TocChanged);
        return true;
    }

    #endregion

    #region *** Scan judgement & wording (pure) ***

    /// <summary>
    /// Renders a scan map as a headline, its severity, and detail lines. Pure — the scan counterpart of
    ///  <see cref="VerbalizeFileOperation"/>.
    /// </summary>
    /// <remarks>
    /// Says what the tape SHOWS, never what the user should conclude: "the last set was never completed"
    ///  is a finding; whether to repair is the advice's job (<see cref="AdviseOnScan"/>).
    /// </remarks>
    public static (ServiceReportLevel Level, string Headline, IReadOnlyList<string> Details)
        VerbalizeScan(MediaScanMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        int damaged = map.Fragments.Count(f => f.Kind == FragmentKind.Unknown && !f.Diagnosis.Success);
        int unidentified = map.UnknownCount - damaged;
        int recovered = CountRecoveredTocs(map);
        int mixed = map.MixedIdentityFragments.Count;

        var details = new List<string>();

        int headerless = map.Fragments.Count(f => f.Kind == FragmentKind.SetContent);
        if (headerless > 0)
            details.Add($"{headerless:N0} backup set(s) identified by their first file — written without set headers, " +
                        "so their order and completeness cannot be checked from the tape alone, without TOC");

        if (map.LastSetUnclosed)
        {
            string? name = map.Fragments.LastOrDefault(f => f.Kind == FragmentKind.SetHeader)?.Description;
            details.Add($"The last backup set{(name is null ? "" : $" >{name}<")} was never completed");
        }

        if (map.SetIndexGaps.Count > 0)
            details.Add($"Backup set(s) missing from the middle: {string.Join(", ", map.SetIndexGaps.Select(g => $"#{g + 1}"))}");

        if (mixed > 0)
            details.Add($"{mixed:N0} backup set(s) belong to a different media series");

        if (damaged > 0)
            details.Add($"{damaged:N0} damaged or unreadable block(s)");

        if (unidentified > 0 && map.Kind == ScannedMediaKind.Backup)
            details.Add($"{unidentified:N0} block(s) of unidentified data");

        if (map.TocCopyCount > 0)
            details.Add($"{map.TocCopyCount:N0} table-of-contents copy/copies found" +
                        (recovered > 0 ? $", {recovered:N0} recovered" : ""));

        // ── Headline ──
        if (map.Truncated)
        {
            return map.TerminatorWin32 == (uint)WIN32_ERROR.ERROR_CANCELLED
                ? (ServiceReportLevel.Failed,
                   $"Scan aborted — {map.Fragments.Count:N0} fragment(s) mapped before the abort", details)
                : (ServiceReportLevel.Warning,
                   $"Scan incomplete ({(WIN32_ERROR)map.TerminatorWin32}) — the map covers only the tape read so far",
                   details);
        }

        switch (map.Kind)
        {
            case ScannedMediaKind.Blank:
                return (ServiceReportLevel.Info, "Blank cartridge — no data found", details);

            case ScannedMediaKind.CalibrationCartridge:
                return (ServiceReportLevel.Info, "Calibration cartridge — it holds a calibration run, not backups", details);

            case ScannedMediaKind.Foreign:
                return (ServiceReportLevel.Warning, "No backup content recognized on this cartridge", details);
        }

        if (map.SetCount == 0)
        {
            // On a backup tape whose sets carry no identifiable start, the recovered TOC is the better witness:
            int listed = map.Fragments
                .Where(f => f.HarvestedToc is not null)
                .Select(f => f.HarvestedToc!.Count)
                .DefaultIfEmpty(0).Max();
            if (listed > 0)
                return (damaged > 0 ? ServiceReportLevel.Warning : ServiceReportLevel.Completed,
                        $"{listed:N0} backup set(s) per the recovered table of contents — they carry no headers to identify them by",
                        details);
            return (damaged > 0 ? ServiceReportLevel.Warning : ServiceReportLevel.Completed,
                    "No backup sets found — the cartridge is formatted but empty", details);
        }

        // When sets are found but the recovered TOC lists more (e.g. a mixed cartridge: 1 identified, 2 listed), say so.
        //  (This is not a defect by itself, hence doesn't influence needsAttention.)
        int tocListed = map.Fragments.Where(f => f.HarvestedToc is not null)
            .Select(f => f.HarvestedToc!.Count).DefaultIfEmpty(0).Max();
        if (tocListed > map.SetCount)
            details.Add($"{tocListed - map.SetCount:N0} more set(s) listed by the recovered table of contents " +
                        "carry no headers to identify them by (legacy, or written without set headers)");

        bool needsAttention = map.LastSetUnclosed || map.SetIndexGaps.Count > 0 || mixed > 0 || damaged > 0;

        return needsAttention
            ? (ServiceReportLevel.Warning, $"{map.SetCount:N0} backup set(s) found — the cartridge needs attention", details)
            : (ServiceReportLevel.Completed, $"{map.SetCount:N0} backup set(s) found — the cartridge looks sound", details);
    }

    /// <summary>
    /// Derives the follow-up actions a scan suggests, most useful first. Pure; drives the UI's buttons.
    /// </summary>
    /// <remarks>
    /// <see cref="ScanAdvice.AdoptRecoveredToc"/> and <see cref="ScanAdvice.RecoverTocFromCopy"/> exclude
    ///  each other: once one copy is recovered, offering to recover another only adds noise.
    /// </remarks>
    public static IReadOnlyList<ScanAdvice> AdviseOnScan(MediaScanMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var advice = new List<ScanAdvice>(3);

        if (map.Kind == ScannedMediaKind.CalibrationCartridge)
        {
            advice.Add(ScanAdvice.InspectCalibrationCartridge);
            return advice;
        }

        if (CountRecoveredTocs(map) > 0)
            advice.Add(ScanAdvice.AdoptRecoveredToc);
        else if (map.TocCopyCount > 0)
            advice.Add(ScanAdvice.RecoverTocFromCopy);

        if (map.LastSetUnclosed)
            advice.Add(ScanAdvice.ReviewUnclosedSet);

        return advice;
    }

    /// <summary>One actionable line per advice.</summary>
    public static string ScanAdviceText(ScanAdvice advice) => advice switch
    {
        ScanAdvice.InspectCalibrationCartridge =>
            "Use Calibrate | Inspect Media to see this calibration run's state",
        ScanAdvice.AdoptRecoveredToc =>
            "A table of contents was recovered from the tape — it can be adopted or saved as a file",
        ScanAdvice.RecoverTocFromCopy =>
            "Table-of-contents copies were found but not recovered — try recovering one",
        ScanAdvice.ReviewUnclosedSet =>
            "The last backup set was never completed — its files may be incomplete",
        _ => string.Empty,
    };

    #endregion
}
