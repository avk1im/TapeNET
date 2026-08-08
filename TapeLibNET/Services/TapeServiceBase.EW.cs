using Windows.Win32.Foundation;
using Windows.Win32.System.SystemServices; // Helpers, Stopwatch

using TapeLibNET.Virtual;

using Stopwatch = Windows.Win32.System.SystemServices.Stopwatch;

namespace TapeLibNET.Services;

public partial class TapeServiceBase
{
    // ── Calibration ───────────────────────────────────────────────────────────

    /// <summary>
    /// Executes a destructive calibration run against the currently loaded medium.
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
            Exception? error = null)
            => progressHandler?.GenerateResult(
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

        if (_drive is null || !_drive.IsMediaLoaded)
        {
            LastError = "Media not loaded";
            throw new InvalidOperationException("Media not loaded");
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

            timer.Restart();
            ITapeCalibration? calibration = calibrator.Run(progressHandler);
            timer.Stop();

            if (calibration is null)
            {
                LastError = calibrator.LastErrorMessage;
                if (calibrator.LastError == (uint)WIN32_ERROR.ERROR_CANCELLED || calibrator.IsAbortRequested)
                {
                    OnStatusUpdate("Calibration aborted");
                    LogFail("Calibration aborted");
                    return MakeResult(aborted: true, message: "Calibration aborted");
                }

                OnStatusUpdate("Calibration failed");
                LogErr($"Calibration failed: {LastError}");
                return MakeResult(failed: true, message: LastError);
            }

            OnStatusUpdate("Calibration complete");
            LogInfo("Calibration summary:");
            LogInfoSub($"Actual capacity: {Helpers.BytesToStringLong(calibration.CapacityActual)}");
            if (calibration.EarlyWarning is { } ew)
            {
                LogInfoSub($"EW landmark: reported {Helpers.BytesToStringLong(ew.ReportedRemaining)}, " +
                           $"actual remaining {Helpers.BytesToStringLong(ew.ActualRemaining)}");
                LogInfoSub($"EW→EOM distance: {Helpers.BytesToStringLong(calibration.EwToEomDistance)}");
            }
            else
            {
                LogInfoSub("EW landmark: not observed during calibration");
            }
            LogInfoSub($"Curve points: {calibration.Curve.Count:N0}");
            LogOk("Calibration completed successfully");

            progressHandler.CompleteProgress();
            return MakeResult(calibration, message: "Calibration completed");
        }
        catch (TapeAbortRequestedException)
        {
            timer.Stop();
            LastError = "Calibration aborted";
            OnStatusUpdate("Calibration aborted");
            LogFail("Calibration aborted");
            return MakeResult(aborted: true, message: LastError);
        }
        catch (Exception ex)
        {
            timer.Stop();
            LastError = ex.Message;
            OnStatusUpdate("Calibration failed");
            LogErr($"Calibration failed: {ex.Message}");
            return MakeResult(failed: true, message: ex.Message, error: ex);
        }
        finally
        {
            progressHandler?.DisposeProgress();
        }
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

        try
        {
            var calibrations = CalibrationStore.LoadAll();
            if (calibrations.Count == 0)
                return 0;

            foreach (var cal in calibrations)
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
