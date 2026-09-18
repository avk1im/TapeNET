using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TapeLibNET.Services;

namespace TapeLibNET.Tests.Helpers;

internal sealed class TestServiceCalibrateProgressHandler(TestTapeService svc,
    ITapeServiceHost host, TapeCalibrator calibrator, long capacityReported)
        : ServiceCalibrateProgressHandler(host, calibrator, capacityReported)
{
    protected override void ReportProgress(TapeCalibrationProgress progress)
    {
        svc.OnCalibrationProgress?.Invoke(progress);
        base.ReportProgress(progress);
    }
}

/// <summary>
/// Test service that exposes the live agent at the deterministic moment the operation creates it.
/// </summary>
/// <remarks>
/// Replaces polling <c>svc.Agent</c> after <c>ExecuteBackupAsync</c> returns. That polling races the
///  worker thread three ways: a debugger pause burns the wall-clock deadline, a fast machine can finish
///  the whole operation before the first poll, and a slow one can arm the simulator after the loop has
///  passed the files it was meant to affect. The progress-handler factory runs INSIDE the operation,
///  after the agent exists and before any file is touched — so a hook here cannot race anything.
/// </remarks>
public sealed class TestTapeService(ILoggerFactory lf, ITapeServiceHost host) : TapeServiceBase(lf, host)
{
    /// <summary>Invoked with the live agent, before the first file is processed.</summary>
    public Action<TapeFileAgent>? OnAgentReady { get; set; }

    /// <summary>Invoked with the live backup agent, before the first file is processed.</summary>
    public Action<TapeFileBackupAgent>? OnBackupAgentReady { get; set; }

    /// <summary>Invoked with the live restore agent, before the first file is processed.</summary>
    public Action<TapeFileRestoreBaseAgent>? OnRestoreAgentReady { get; set; }

    /// <summary>Invoked with the live calibrator, before the first file is processed.</summary>
    public Action<TapeCalibrator>? OnCalibratorReady { get; set; }

    /// <summary>Set before starting a backup; applied to the handler the operation creates.</summary>
    public Action<HookedBackupProgressHandler>? ConfigureBackupHandler { get; set; }

    /// <summary>Invoked with the progress of a calibration operation.</summary>
    public Action<TapeCalibrationProgress>? OnCalibrationProgress { get; set; }

    protected override ServiceBackupProgressHandler CreateBackupProgressHandler(
        TapeFileBackupAgent agent, bool skipAllErrors, ITapeFileFilter? filter)
    {
        OnAgentReady?.Invoke(agent);
        OnBackupAgentReady?.Invoke(agent);
        var handler = new HookedBackupProgressHandler(_host, agent, skipAllErrors, filter);
        ConfigureBackupHandler?.Invoke(handler);
        return handler;
    }

    protected override ServiceRestoreProgressHandler CreateRestoreProgressHandler(
        TapeFileRestoreBaseAgent agent, int totalFiles, RestoreMode mode, bool skipAllErrors)
    {
        OnAgentReady?.Invoke(agent);
        OnRestoreAgentReady?.Invoke(agent);
        return base.CreateRestoreProgressHandler(agent, totalFiles, mode, skipAllErrors);
    }

    protected override ServiceCalibrateProgressHandler CreateCalibrateProgressHandler(
        TapeCalibrator calibrator, CalibrateRequest request, long capacityReported)
    { 
        OnCalibratorReady?.Invoke(calibrator);
        return new TestServiceCalibrateProgressHandler(this,
            _host, calibrator, capacityReported);
    }
}