using Windows.Win32.Foundation;
using Windows.Win32.System.SystemServices; // Helpers, Stopwatch
using TapeLibNET.Virtual;
using Stopwatch = Windows.Win32.System.SystemServices.Stopwatch;

namespace TapeLibNET.Services;

public partial class TapeServiceBase
{
    // ── Recalibration verdict thresholds ──────────────────────────────────────
    // Beyond these SIGNED relative shifts (new vs. old) the reassessed calibration is deemed no longer
    //  trustworthy and a full re-run is advised. EW→EOM distance is the most critical figure, so it and
    //  capacity use a tight 1% band; the phantom figure is coarser and less critical, so 5%.
    //  These are POLICY constants — the calibrator itself stays verdict-free.
    private const double c_recalEwShiftTolerance = 0.01;        // 1%
    private const double c_recalCapacityShiftTolerance = 0.01;  // 1%
    private const double c_recalPhantomShiftTolerance = 0.05;   // 5%

    // ── Calibration ───────────────────────────────────────────────────────────
    /// <summary>
    /// Executes a destructive calibration run against the currently loaded medium. The
    /// <see cref="CalibrateRequest.Mode"/> selects a fresh run, a resume of an interrupted run, or a
    /// fast recalibration of a complete calibration cartridge.
    /// </summary>
    public Task<CalibrateResult> ExecuteCalibrateAsync(CalibrateRequest request)
    {
        _host.OnServiceStateChanged(ServiceStateChange.OperationStarted);

        return Task.Run(async () =>
        {
            await _operationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var result = ExecuteCalibrateCore(request);

                if (request.EjectWhenDone)
                {
                    LogInfo("Ejecting media after calibration...");
                    EjectMediaCore();
                }

                return result;
            }
            finally
            {
                _operationLock.Release();
                _host.OnServiceStateChanged(ServiceStateChange.OperationEnded);
            }
        });
    }

    private CalibrateResult ExecuteCalibrateCore(CalibrateRequest request)
    {
        ServiceCalibrateProgressHandler? progressHandler = null;
        TapeCalibrator? calibrator = null;
        var timer = new Stopwatch();

        CalibrateResult MakeResult(
            ITapeCalibration? calibration = null,
            bool aborted = false,
            bool failed = false,
            string? message = null,
            Exception? error = null,
            CalibrationMode mode = CalibrationMode.New,
            TapeRecalibrationDelta? delta = null,
            RecalibrationVerdict? verdict = null)
        {
            CalibrateResult baseResult = progressHandler?.GenerateResult(
                    calibration,
                    aborted: aborted,
                    failed: failed,
                    duration: timer.ElapsedTimeSpan,
                    message: message,
                    error: error)
               ?? new CalibrateResult
               {
                   Calibration      = calibration,
                   ProfileKey       = calibration?.ProfileKey ?? _drive?.DriveProfileKey ?? string.Empty,
                   ReportedCapacityAtBom = calibration?.ReportedCapacityAtBom ?? _drive?.Capacity ?? 0,
                   PhantomFreeAtEom = calibration?.PhantomFreeAtEom ?? 0,
                   CapacityActual   = calibration?.CapacityActual ?? 0,
                   EarlyWarning     = calibration?.EarlyWarning,
                   EwToEomDistance  = calibration?.EwToEomDistance ?? 0,
                   BytesTotal       = _drive?.Capacity ?? 0,
                   BytesProcessed   = calibration?.CapacityActual ?? 0,
                   WasAborted       = aborted,
                   HasFailed        = failed,
                   Success          = !aborted && !failed && calibration is not null,
                   Outcome          = aborted ? ServiceReportLevel.Failed
                                    : failed  ? ServiceReportLevel.Error
                                    :           ServiceReportLevel.Completed,
                   Duration         = timer.ElapsedTimeSpan,
                   Message          = message,
                   Error            = error,
               };

            // Tag the mode/recalibration fields uniformly, regardless of which branch built baseResult.
            return baseResult with
            {
                Mode                 = mode,
                RecalibrationDelta   = delta,
                RecalibrationVerdict = verdict,
            };
        }

        if (_drive is null || !_drive.IsMediaLoaded)
        {
            LastError = "Media not loaded";
            throw new InvalidOperationException("Media not loaded");
        }

        // If the drive has multiple partitions, check with the user and break if negative
        if (_drive.HasInitiatorPartition)
        {
            if (!host.Confirm(
                    "Calibrating a multi-partition media will have no effect.\nWould you still like to continue?",
                    defaultAnswer: false))
                return MakeResult(aborted: true, message: "For calibration, use a single-partition media", mode: request.Mode);
        }

        try
        {
            LogWarn("Calibration is destructive — use a scratch cartridge only");
            LogInfo("Preparing media for calibration...");
            OnStatusUpdate("Preparing calibration...");

            if (!_drive.PrepareMedia())
            {
                LastError = _drive.LastErrorMessage;
                throw new InvalidOperationException($"Couldn't prepare media: {LastError}");
            }

            // Calibration overwrites the medium from this point on regardless of the outcome
            //  (success, abort, or failure), so the cached TOC is no longer valid. Drop it now
            //  so the service/UI reflect "media loaded, no TOC" instead of a stale TOC.
            ClearTocState();
            _host.OnServiceStateChanged(ServiceStateChange.TocChanged);

            calibrator = new TapeCalibrator(_drive)
            {
                Options = request.Options,
            };
            progressHandler = CreateCalibrateProgressHandler(calibrator, request, _drive.Capacity);

            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                OperationCancellationToken,
                request.Cancellation);
            using var ctReg = linkedCancellation.Token.Register(() =>
            {
                if (calibrator is not null)
                    calibrator.IsAbortRequested = true;
            });

            LogInfo($"Calibration profile: >{_drive.DriveProfileKey}<");
            LogInfoSub($"Reported capacity: {Helpers.BytesToStringLong(_drive.Capacity)}");

            // --- Dispatch the requested mode. New/Resume both yield an ITapeCalibration?; Recalibrate
            //     additionally yields a delta versus the supplied (or resolved) existing calibration. ---
            ITapeCalibration? calibration;
            TapeRecalibrationDelta? recalDelta = null;

            timer.Restart();
            switch (request.Mode)
            {
                case CalibrationMode.Resume:
                    LogInfo("Resuming calibration from the last checkpoint on the cartridge...");
                    OnStatusUpdate("Resuming calibration...");
                    calibration = calibrator.Resume(progressHandler);
                    break;

                case CalibrationMode.Recalibrate:
                {
                    // Resolve the baseline to compare against: explicit → drive's active match → store.
                    ITapeCalibration? existing = request.ExistingCalibration
                        ?? _drive.Calibration
                        ?? CalibrationStore.LoadLatest(_drive.DriveProfileKey);

                    if (existing is null)
                    {
                        timer.Stop();
                        LastError = "Recalibrate needs an existing calibration to compare against";
                        LogErr(LastError);
                        OnStatusUpdate("Recalibration failed");
                        return MakeResult(failed: true, message: LastError, mode: CalibrationMode.Recalibrate);
                    }

                    LogInfo("Recalibrating: re-measuring the tail against the existing calibration...");
                    OnStatusUpdate("Recalibrating...");

                    (calibration, TapeRecalibrationDelta delta) = calibrator.Recalibrate(existing, progressHandler);
                    if (calibration is not null)
                        recalDelta = delta;
                    break;
                }

                case CalibrationMode.New:
                default:
                    OnStatusUpdate("Calibrating...");
                    calibration = calibrator.Run(progressHandler);
                    break;
            }
            timer.Stop();

            if (calibration is null)
            {
                LastError = calibrator.LastErrorMessage;

                if (calibrator.LastError == (uint)WIN32_ERROR.ERROR_CANCELLED || calibrator.IsAbortRequested)
                {
                    OnStatusUpdate("Calibration aborted");
                    LogFail("Calibration aborted");
                    return MakeResult(aborted: true, message: "Calibration aborted", mode: request.Mode);
                }

                // Resume/Recalibrate can legitimately fail to find a resumable trail on the cartridge;
                //  surface a mode-appropriate message so the caller can offer a fresh run instead.
                string failMsg = request.Mode switch
                {
                    CalibrationMode.Resume      => $"Resume failed: no resumable run found on this cartridge ({LastError})",
                    CalibrationMode.Recalibrate => $"Recalibration failed: no calibration trail on this cartridge ({LastError})",
                    _                           => LastError,
                };

                OnStatusUpdate("Calibration failed");
                LogErr($"Calibration failed: {failMsg}");
                return MakeResult(failed: true, message: failMsg, mode: request.Mode);
            }

            OnStatusUpdate("Calibration complete");
            LogInfo("Calibration summary:");
            LogInfoSub($"Actual capacity: {Helpers.BytesToStringLong(calibration.CapacityActual)}");

            if (calibration.EarlyWarning is { } ew)
            {
                LogInfoSub($"EW landmark: reported {Helpers.BytesToStringLong(ew.ReportedRemaining)}, " +
                           $"actual remaining {Helpers.BytesToStringLong(ew.ActualRemaining)}");
                LogInfoSub($"EW→EOM distance: {Helpers.BytesToStringLong(calibration.EwToEomDistance)}" +
                    (calibration.EomInferred ? " (EOM inferred)" : string.Empty));
            }
            else
            {
                LogInfoSub("EW landmark: not observed during calibration");
            }

            LogInfoSub($"Curve points: {calibration.Curve.Count:N0}");

            // --- Recalibration: judge the shift, log it, and (if advised) offer a full re-run via host. ---
            if (request.Mode == CalibrationMode.Recalibrate && recalDelta is { } d)
            {
                RecalibrationVerdict verdict = JudgeRecalibration(d);
                LogRecalibrationDelta(d, verdict);

                if (verdict == RecalibrationVerdict.FullRecalibrationAdvised)
                {
                    if (!request.ConfirmFullRecalibrationInline)
                    {
                        // The host presents its own verdict/delta UI and lets the user trigger a follow-up
                        //  run itself (e.g. TapeWinNET's CalibrationResultViewModel banner) — do not prompt
                        //  or chain here, just surface the reassessed result and verdict below.
                        LogWarn("Full recalibration advised — deferring the decision");
                    }
                    else
                    {
                        // Ask the host to confirm a destructive full re-run. Non-interactive hosts return the
                        //  default (false), so a quiet/CLI host never launches a multi-hour run unattended.
                        bool runFull = _host.Confirm(
                            "The drive's remaining-space behavior has shifted beyond tolerance since the last " +
                            "calibration. Run a FULL recalibration now? This is destructive and may take a long time.",
                            defaultAnswer: false);

                        if (runFull)
                        {
                            LogInfo("Full recalibration confirmed — running a fresh calibration from BOM...");

                            // Chain into the New path (fresh progress handler, timer, logging) with zero
                            //  duplication, then re-tag the result as a recalibration outcome so the caller
                            //  still sees the delta/verdict that triggered the re-run.
                            CalibrateResult full = ExecuteCalibrateCore(request with { Mode = CalibrationMode.New });
                            return full with
                            {
                                Mode                 = CalibrationMode.Recalibrate,
                                RecalibrationDelta   = recalDelta,
                                RecalibrationVerdict = verdict,
                            };
                        }

                        LogWarn("Full recalibration declined — keeping the reassessed calibration; treat with caution");
                    }
                }

                LogOk("Recalibration completed");
                progressHandler.CompleteProgress();
                return MakeResult(calibration, message: "Recalibration completed",
                    mode: CalibrationMode.Recalibrate, delta: recalDelta, verdict: verdict);
            }

            LogOk("Calibration completed successfully");
            progressHandler.CompleteProgress();
            return MakeResult(calibration, message: "Calibration completed", mode: request.Mode);
        }
        catch (TapeAbortRequestedException)
        {
            timer.Stop();
            LastError = "Calibration aborted";
            OnStatusUpdate("Calibration aborted");
            LogFail("Calibration aborted");
            return MakeResult(aborted: true, message: LastError, mode: request.Mode);
        }
        catch (Exception ex)
        {
            timer.Stop();
            LastError = ex.Message;
            OnStatusUpdate("Calibration failed");
            LogErr($"Calibration failed: {ex.Message}");
            return MakeResult(failed: true, message: ex.Message, error: ex, mode: request.Mode);
        }
        finally
        {
            progressHandler?.DisposeProgress();
        }
    }

    // ── Recalibration judgment (policy) ───────────────────────────────────────
    /// <summary>
    /// Threshold-based verdict on a recalibration delta: whether the existing calibration still holds or
    /// a full re-run is advised. Kept in the SERVICE layer (not the calibrator) because it is policy;
    /// the thresholds are the <c>c_recal*</c> constants above.
    /// </summary>
    private static RecalibrationVerdict JudgeRecalibration(in TapeRecalibrationDelta delta)
        => Math.Abs(delta.EwShiftFraction) > c_recalEwShiftTolerance
           || Math.Abs(delta.CapacityShiftFraction) > c_recalCapacityShiftTolerance
           || Math.Abs(delta.PhantomShiftFraction) > c_recalPhantomShiftTolerance
            ? RecalibrationVerdict.FullRecalibrationAdvised
            : RecalibrationVerdict.Holds;

    /// <summary>Logs the before/after figures and the verdict of a recalibration to the host log pane.</summary>
    private void LogRecalibrationDelta(in TapeRecalibrationDelta d, RecalibrationVerdict verdict)
    {
        LogInfo("Recalibration assessment:");
        LogInfoSub($"EW→EOM distance: {Helpers.BytesToStringLong(d.OldEwToEomDistance)} → " +
                   $"{Helpers.BytesToStringLong(d.NewEwToEomDistance)} ({d.EwShiftFraction:+0.0%;-0.0%})");
        LogInfoSub($"Actual capacity: {Helpers.BytesToStringLong(d.OldCapacityActual)} → " +
                   $"{Helpers.BytesToStringLong(d.NewCapacityActual)} ({d.CapacityShiftFraction:+0.0%;-0.0%})");
        LogInfoSub($"Phantom @ EOM: {Helpers.BytesToStringLong(d.OldPhantomFreeAtEom)} → " +
                   $"{Helpers.BytesToStringLong(d.NewPhantomFreeAtEom)} ({d.PhantomShiftFraction:+0.0%;-0.0%})");

        if (verdict == RecalibrationVerdict.Holds)
            LogOk("Recalibration verdict: the existing calibration still holds");
        else
            LogWarn("Recalibration verdict: a full recalibration is advised");
    }

    /// <summary>
    /// Creates the progress handler for a calibration run.
    /// </summary>
    protected virtual ServiceCalibrateProgressHandler CreateCalibrateProgressHandler(
        TapeCalibrator calibrator,
        CalibrateRequest request,
        long capacityReported)
        => new(_host, calibrator, capacityReported);

    /// <summary>The current media profile key, or empty when no drive/media is available.</summary>
    public string DriveProfileKey => _drive?.DriveProfileKey ?? string.Empty;

    /// <summary>The active, matching calibration for the current media, or null.</summary>
    public ITapeCalibration? Calibration => _drive?.Calibration;

    /// <summary>Adds a calibration profile to the current drive. Returns whether it matches now.</summary>
    public bool AddCalibration(ITapeCalibration calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        if (_drive is null)
            return false;
        return _drive.AddCalibration(calibration);
    }

    // ── Media inspection (read-only, optional convenience) ──────────────────────────────────────
    /// <summary>
    /// Non-destructively probes the loaded cartridge for an existing calibration trail, combining the
    /// on-tape header/checkpoint (<see cref="TapeCalibrator.InspectMedia"/>) with a
    /// <see cref="CalibrationStore"/> lookup to recommend a <see cref="CalibrationMode"/>.
    /// This is a pure convenience for the UI — it doesn't gate New/Resume/Recalibrate, which all remain
    /// available regardless of the result, since Resume/Recalibrate will fail gracefully if the cartridge
    /// is unsuitable.
    /// <remarks>
    /// <para>
    /// Recommendation logic. Resume AND Recalibrate both require a valid ON-TAPE checkpoint — no stored
    /// profile can substitute for a checkpoint that is not physically on the cartridge. Recalibrate
    /// additionally needs a COMPLETE run plus a baseline to compare against.
    /// </para>
    /// <code>
    ///  Cartridge state            | IsResumable | AppearsComplete | hasBaseline | Recommend
    ///  ---------------------------+-------------+-----------------+-------------+-----------
    ///  No header (blank/foreign)  |     —       |       —         |     —       | New
    ///  Header only, no checkpoint |   false     |       —         |     —       | New
    ///  Interrupted run            |   true      |     false       |     —       | Resume
    ///  Complete run, no baseline  |   true      |     true        |   false     | Resume
    ///  Complete run + baseline    |   true      |     true        |   true      | Recalibrate
    /// </code>
    /// </remarks>
    /// </summary>
    public Task<InspectCalibrationMediaResult> ExecuteInspectCalibrationMediaAsync()
    {
        _host.OnServiceStateChanged(ServiceStateChange.OperationStarted);

        return Task.Run(() =>
        {
            try
            {
                LogInfo("Starting media inspection for recalibration...");

                if (_drive is null || !_drive.IsMediaLoaded)
                {
                    LastError = "Media not loaded";
                    return new InspectCalibrationMediaResult
                    {
                        Success = false,
                        Outcome = ServiceReportLevel.Error,
                        Message = LastError,
                    };
                }

                // If the drive has multiple partitions, check with the user and break if negative
                if (_drive.HasInitiatorPartition)
                {
                    if (!host.Confirm(
                            "Calibrating a multi-partition media will have no effect.\nWould you still like to continue?",
                            defaultAnswer: false))
                        return new InspectCalibrationMediaResult
                        {
                            Success = false,
                            Outcome = ServiceReportLevel.Warning,
                            Message = "For calibration, use a single-partition media",
                        };
                }

                var calibrator = new TapeCalibrator(_drive);
                TapeCalibrationMediaInfo? info = calibrator.InspectMedia();

                if (info is null)
                {
                    LogInfo("Media inspection: no calibration trail found on this cartridge");
                    return new InspectCalibrationMediaResult
                    {
                        Success = true,
                        Outcome = ServiceReportLevel.Info,
                        HasRunHeader = false,
                        RecommendedMode = CalibrationMode.New,
                        Summary = "No calibration trail found on this cartridge — a New run is required.",
                    };
                }

                bool matchesDrive = string.Equals(info.ProfileKey, _drive.DriveProfileKey, StringComparison.Ordinal);

                // The baseline that makes Recalibrate MEANINGFUL is resolved exactly as ExecuteCalibrateCore
                //  does — the drive's active calibration, else the store keyed by the CURRENT drive
                //  (NOT the trail's recorded key, which may differ if the cartridge came from another drive).
                bool hasBaseline = _drive.Calibration is not null
                                || CalibrationStore.Exists(_drive.DriveProfileKey);

                // Gate on the actual ON-TAPE state first (IsResumable), then completeness + baseline.
                //  See the table in the method summary.
                CalibrationMode recommended;
                string summary;

                if (!info.IsResumable)
                {
                    // Header present but no valid checkpoint (run died before the first checkpoint, or all
                    //  checkpoints are torn) — nothing to resume from and nothing to recalibrate.
                    recommended = CalibrationMode.New;
                    summary = "A calibration header is present, but no valid checkpoint could be read from "
                            + "this cartridge — it cannot be resumed or recalibrated. Run a New calibration.";
                }
                else if (info.AppearsComplete && hasBaseline)
                {
                    recommended = CalibrationMode.Recalibrate;
                    summary = $"A completed calibration is on this cartridge (started {info.StartedUtc:u}). "
                            + "Recalibrate quickly re-measures the tail and compares it against the existing profile.";
                }
                else if (info.AppearsComplete)
                {
                    // Complete trail, but no stored profile to compare — resuming re-measures the tail into
                    //  a fresh profile (equivalent work; there is simply nothing to diff against).
                    recommended = CalibrationMode.Resume;
                    summary = $"A completed calibration run is on this cartridge (started {info.StartedUtc:u}), "
                            + "but no stored profile to compare against. Resume re-measures the tail into a fresh profile.";
                }
                else
                {
                    recommended = CalibrationMode.Resume;
                    summary = $"An interrupted run was found ({info.ProgressFraction:P0} written, started "
                            + $"{info.StartedUtc:u}). Resume continues it to completion.";
                }

                if (!matchesDrive)
                    summary += " Note: this trail was recorded on a different drive/media profile.";

                LogInfo("Media inspection:");
                LogInfoSub($">{info.ProfileKey}<");
                LogInfoSub($"Started: {info.StartedUtc:u}, resumable: {info.IsResumable}, " +
                           $"complete: {info.AppearsComplete}, baseline: {hasBaseline}");
                LogInfoSub($"Recommended mode: {recommended}");

                return new InspectCalibrationMediaResult
                {
                    Success = true,
                    Outcome = ServiceReportLevel.Completed,
                    HasRunHeader = true,
                    ProfileKey = info.ProfileKey,
                    StartedUtc = info.StartedUtc,
                    CapacityReportedAtBom = info.CapacityReportedAtBom,
                    HasCheckpoint = info.IsResumable,
                    BytesWritten = info.CheckpointedBytes,
                    ProgressFraction = info.ProgressFraction,
                    MatchesCurrentDrive = matchesDrive,
                    HasStoredCalibration = hasBaseline,
                    RecommendedMode = recommended,
                    Summary = summary,
                };
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogErr($"Media inspection failed: {ex.Message}");
                return new InspectCalibrationMediaResult
                {
                    Success = false,
                    Outcome = ServiceReportLevel.Error,
                    Message = ex.Message,
                    Error = ex,
                };
            }
            finally
            {
                _host.OnServiceStateChanged(ServiceStateChange.OperationEnded);
            }
        });
    }

    // ── Calibration autoload ──────────────────────────────────────────────────
    private TapeCalibrationStore? _calibrationStore;

    /// <summary>
    /// The shared, library-scoped calibration store (<c>%LocalAppData%\TapeLibNET\Calibrations</c>),
    ///  created on first use. Every TapeLibNET consumer sees the same profiles.
    /// </summary>
    public TapeCalibrationStore CalibrationStore => _calibrationStore ??= new(_loggerFactory);

    /// <summary>
    /// Feeds every stored calibration profile to the drive, so the one matching this drive+media
    ///  activates itself without any user action — a measured profile is worthless if the user has to
    ///  remember to apply it. The drive matches on <see cref="TapeDrive.DriveProfileKey"/> and silently
    ///  keeps the non-matching ones for when other media is loaded.
    /// <para>
    /// For a <see cref="VirtualTapeDriveBackend"/>, autoload only runs when EOM behavior emulation
    ///  (<see cref="VirtualTapeDriveBackend.EmulatedEarlyWarning"/>) is actually active: a non-emulated
    ///  virtual drive is truthful by construction, so applying a calibration measured against real (or
    ///  differently emulated) hardware would misrepresent it. Physical and remote drives are unaffected.
    /// </para>
    /// <para>
    /// Non-throwing and non-fatal: a store that cannot be read simply leaves the drive uncalibrated,
    ///  falling back to the a-priori estimate. Call after the drive is open AND media is loaded, since
    ///  the profile key includes the media capacity bucket.
    /// </para>
    /// </summary>
    /// <returns>The number of profiles offered, or 0 when none were available or autoload was skipped.</returns>
    protected int AutoLoadCalibrations()
    {
        if (_drive is null)
            return 0;

        // Virtual drives only warrant autoload when EOM behavior emulation is switched on for them.
        if (_drive.Backend is VirtualTapeDriveBackend { EmulatedEarlyWarning: not { EarlyWarningZone: > 0 } })
        {
            LogInfoSub("Calibration autoload skipped: EOM behavior emulation is not active for this virtual drive");
            return 0;
        }

        if (_drive.HasInitiatorPartition)
        {
            LogInfoSub("Calibration autoload skipped: multi-partition media");
            return 0;
        }

        try
        {
            var calibrations = CalibrationStore.LoadAll();
            if (calibrations.Count == 0)
                return 0;

            // Feed oldest-first: TapeDrive.AddCalibration replaces same-ProfileKey entries (last wins),
            //  so ordering ascending by MeasuredUtc (nulls/legacy first) makes the newest version of
            //  each profile key the one that ends up active — "newest auto-wins" per the store contract.
            foreach (var cal in calibrations.OrderBy(c => c.MeasuredUtc ?? DateTime.MinValue))
                _drive.AddCalibration(cal);

            if (_drive.Calibration is { } matched)
                LogOkSub($"Calibration applied: {matched.ProfileKey}");
            else
                LogInfoSub($"Calibration: {calibrations.Count} profile(s) loaded, none matching " +
                           $"'{_drive.DriveProfileKey}' — using the a-priori estimate");

            return calibrations.Count;
        }
        catch (Exception ex)
        {
            // Never let a calibration-store problem break opening a drive or loading media.
            LogInfoSub($"Calibration profiles unavailable: {ex.Message}");
            return 0;
        }
    }
}
