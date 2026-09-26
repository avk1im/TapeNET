using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Win32.Foundation;

namespace TapeLibNET;

/// <summary>
/// Result type for compound tape operations that cross the Agent → Service boundary.
/// Carries error context as a value, immune to later state resets on Drive/Manager.
/// <para>
/// The internal layer (Drive, Navigator, Manager) continues to use <c>bool</c> + state;
///  <see cref="TapeResult"/> is applied at the public Agent API surface only.
/// </para>
/// </summary>
public readonly record struct TapeResult(bool Success, uint ErrorCode = 0, string ErrorMessage = "")
{
    /// <summary>Successful result with no error.</summary>
    public static TapeResult OK => new(true);

    /// <summary>Creates a failure result from an explicit error code and message.</summary>
    public static TapeResult Fail(uint code, string msg) => new(false, code, msg);

    /// <summary>Creates a failure result by capturing the current error state of an <see cref="IErrorManageable"/>.</summary>
    public static TapeResult Fail(IErrorManageable source) => new(false, source.LastError, source.LastErrorMessage);

    /// <summary>Creates a failure result from an exception, extracting HResult/NativeErrorCode where available.</summary>
    public static TapeResult Fail(Exception ex)
    {
        uint code = ex switch
        {
            // Catches our TapeAbortRequestedException (derived from OperationCanceledException)
            TapeAbortRequestedException or OperationCanceledException => (uint)WIN32_ERROR.ERROR_CANCELLED,
            IOException ioex => (uint)ioex.HResult,
            Win32Exception w32ex => (uint)w32ex.NativeErrorCode,
            _ => (uint)WIN32_ERROR.ERROR_UNHANDLED_EXCEPTION
        };
        return new(false, code, ex.Message);
    }

    /// <summary>
    /// Creates a failure result from a <see cref="TapeIOException"/>, preserving
    /// the trail text in the error message for downstream display.
    /// </summary>
    public static TapeResult Fail(TapeIOException ex) =>
        new(false, ex.Error, ex.TrailText.Length > 0
            ? $"{ex.ErrorMessage} [Trail: {ex.TrailText}]"
            : ex.ErrorMessage);

    /// <summary>Allows <c>if (result)</c> and <c>if (!result)</c> usage, preserving existing call-site patterns.</summary>
    public static implicit operator bool(TapeResult r) => r.Success;
}

/// <summary>
/// Helper class for compound operations that span multiple steps, each of which may fail.
///  Latches the FIRST failure encountered -- usually the most relevant, so that the operation can return
///  a meaningful result even if later steps succeed and reset the live error state.
/// </summary>
/// <remarks>
/// <para>
/// A compound operation spans many steps; a step that fails under <c>ignoreFailures</c> is followed by
///  steps that succeed, and every success calls <see cref="IErrorManageable.ResetError"/>.
///  The live error state is therefore a poor witness by the time the operation returns — it reflects
///  the last thing that happened, not the thing that went wrong.
/// </para>
/// <para>
/// FIRST rather than last, deliberately: later failures are usually consequences (a wrong position
///  produces a CRC failure, which produces a skipped file). The earliest one names the cause.
/// </para>
/// </remarks>
/// <param name="owner">The error-manageable owner whose error state is tracked.</param>
public class TapeResultBuilder(IErrorManageable owner)
{
    protected IErrorManageable m_owner = owner;

    /// <summary>
    /// The FIRST failure encountered during the current operation, retained verbatim.
    /// </summary>
    private TapeResult m_firstFailure = TapeResult.OK;

    /// <summary>The first failure of the current operation, or <see cref="TapeResult.OK"/> if none.</summary>
    public TapeResult FirstFailure => m_firstFailure;

    /// <summary>
    /// Honest status: the latched first failure, else the owner's live error, else
    ///  <see cref="TapeResult.OK"/>. A clean owner means exactly that — no failure.
    /// </summary>
    public TapeResult Result
        => !m_firstFailure.Success ? m_firstFailure : TapeResult.Fail(m_owner) is { ErrorCode: not 0 } live
            ? live : TapeResult.OK;

    /// <summary>
    /// The result to return from a path the caller has ALREADY determined failed. Guarantees
    ///  <c><see cref="TapeResult.Success"/> == <see langword="false"/></c>, synthesizing
    ///  <paramref name="fallbackCode"/> when neither the latch nor the owner carries a diagnosis.
    /// </summary>
    /// <remarks>
    /// The clean-clean case is reachable and legitimate: e.g. a caller-requested abort deliberately does not
    ///  latch (it is not a fault) and <see cref="TapeAgentBase.ThrowIfAbortRequested"/> sets no error.
    ///  Without a fallback the operation would report failure with code 0 and an empty message — the empty
    ///  diagnosis this class exists to prevent.
    ///  The CALLER supplies the code because only it knows why it decided to fail.
    /// </remarks>
    public TapeResult BuildFailure(uint fallbackCode, string fallbackMessage)
    {
        var result = Result;
        return result.Success ? TapeResult.Fail(fallbackCode, fallbackMessage) : result;
    }

    /// <summary>Records <paramref name="result"/> as the operation's failure, if none has been recorded yet.</summary>
    public void LatchFailure(TapeResult result)
    {
        if (m_firstFailure.Success && !result.Success)
            m_firstFailure = result;
    }

    /// <summary>Latches the agent's CURRENT error state. Call at the moment of failure, never later.</summary>
    public void LatchFailure() => LatchFailure(TapeResult.Fail(m_owner));

    /// <summary>Clears the latch. Called wherever e.g. <see cref="TapeAgentBase.ResetStatistics"/>
    ///  starts a fresh operation.</summary>
    public void Reset() => m_firstFailure = TapeResult.OK;
}
