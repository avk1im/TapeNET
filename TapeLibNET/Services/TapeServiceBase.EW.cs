using Windows.Win32.Foundation;
using Windows.Win32.System.SystemServices; // Helpers, Stopwatch

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
                   CapacityReported = calibration?.CapacityReported ?? _drive?.Capacity ?? 0,
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
}
