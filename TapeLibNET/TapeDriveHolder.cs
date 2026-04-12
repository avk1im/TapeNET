using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace TapeLibNET;

// In public methods and properties, expose error values as uint - for callers not using Windows.Win32.Foundation
public interface IErrorManageable
{
    public uint LastError { get; }
    public string LastErrorMessage { get; }
    void ResetError();
}

/// <summary>
/// Base class providing common error handling and logging for tape-related classes.
/// </summary>
public abstract class ErrorManageableBase(ILogger logger) : IErrorManageable
{
    #region *** Private Fields ***

    private WIN32_ERROR m_errorOwn = WIN32_ERROR.NO_ERROR;
    private string m_errorMessageOwn = string.Empty;

    #endregion

    #region *** Protected Fields ***

    protected readonly ILogger m_logger = logger;

    #endregion

    #region *** IErrorManageable Implementation ***

    public virtual uint LastError => (uint)LastErrorWin32;
    public virtual string LastErrorMessage => m_errorMessageOwn;

    public virtual void ResetError()
    {
        m_errorOwn = WIN32_ERROR.NO_ERROR;
        m_errorMessageOwn = string.Empty;
    }

    #endregion

    #region *** Error Helpers ***

    internal WIN32_ERROR LastErrorWin32
    {
        get => m_errorOwn;
        set => SetError(value);
    }

    public virtual bool WentOK => m_errorOwn == WIN32_ERROR.NO_ERROR;
    public virtual bool WentBad => !WentOK;

    internal void SetError(WIN32_ERROR error, string? message = null)
    {
        m_errorOwn = error;
        m_errorMessageOwn = string.IsNullOrEmpty(message)
            ? Marshal.GetPInvokeErrorMessage((int)error)
            : message;
    }

    internal void SetError(uint error, string? message = null) =>
        SetError((WIN32_ERROR)error, message);

    internal void SetError(Exception ex, string? message = null)
    {
        WIN32_ERROR error = ex is IOException ioex ? (WIN32_ERROR)ioex.HResult :
            ex is Win32Exception w32ex ? (WIN32_ERROR)w32ex.NativeErrorCode :
                WIN32_ERROR.ERROR_UNHANDLED_EXCEPTION;

        SetError(error, message ?? ex.Message);
    }

    protected void SetErrorFromPInvoke() =>
        SetError((uint)Marshal.GetLastWin32Error());

    /// <summary>Copies error state from another error source.</summary>
    protected void SyncErrorFrom(IErrorManageable source) =>
        SetError(source.LastError, source.LastErrorMessage);

    #endregion

    #region *** Protected Logging Helpers ***

    protected delegate void LogMethod(string? message, params object?[] args);

    protected abstract string LogPrefix { get; }

    protected void LogError(LogMethod logMethod, string message, string methodName)
    {
        if (string.IsNullOrEmpty(methodName))
            logMethod("{Prefix}: {Message}: error: 0x{Error:X8} >{ErrorMessage}<",
                LogPrefix, message, LastError, LastErrorMessage);
        else
            logMethod("{Prefix}: {Message} in {Method}: error: 0x{Error:X8} >{ErrorMessage}<",
                LogPrefix, message, methodName, LastError, LastErrorMessage);
    }

    protected void LogErrorAsInfo(string message, [CallerMemberName] string methodName = "") =>
        LogError(m_logger.LogInformation, message, methodName);

    protected void LogErrorAsTrace(string message, [CallerMemberName] string methodName = "") =>
        LogError(m_logger.LogTrace, message, methodName);

    protected void LogErrorAsWarning(string message, [CallerMemberName] string methodName = "") =>
        LogError(m_logger.LogWarning, message, methodName);

    protected void LogErrorAsDebug(string message, [CallerMemberName] string methodName = "") =>
        LogError(m_logger.LogDebug, message, methodName);

    protected void LogErrorAsError(string message, [CallerMemberName] string methodName = "") =>
        LogError(m_logger.LogError, message, methodName);

    #endregion
}

/// <summary>
/// Base class for tape components that hold a <see cref="TapeDrive"/> reference.
/// Provides a two-tier error model: own error (set explicitly via <see cref="ErrorManageableBase.SetError"/>)
/// takes precedence, with the shared <see cref="Drive"/> as fallback.
/// <para>
/// <see cref="ResetError"/> clears both own and Drive error state, ensuring a clean
/// slate before each operation. Subclasses that need to surface errors from intermediate
/// layers (e.g., Agent surfacing Manager errors) should use explicit
/// <see cref="ErrorManageableBase.SyncErrorFrom"/> calls in failure paths.
/// </para>
/// </summary>
public class TapeDriveHolder<T> : ErrorManageableBase
{
    #region *** Constructor ***

    public TapeDriveHolder(TapeDrive drive)
        : base(drive.LoggerFactory.CreateLogger<T>())
    {
        Drive = drive;
    }

    #endregion

    #region *** Properties ***

    public TapeDrive Drive { get; }
    public uint DriveNumber => Drive.DriveNumber;
    protected override string LogPrefix => $"Drive #{DriveNumber}";

    #endregion

    #region *** Error Handling — Own + Drive Fallback ***

    /// <summary>Own error if set, otherwise Drive error.</summary>
    public override uint LastError => base.LastError != 0 ? base.LastError : Drive.LastError;

    /// <summary>Own error message if own error is set, otherwise Drive error message.</summary>
    public override string LastErrorMessage => base.LastError != 0 ? base.LastErrorMessage : Drive.LastErrorMessage;

    public override bool WentOK => LastError == 0;
    public override bool WentBad => !WentOK;

    /// <summary>Resets own error and Drive error state.</summary>
    public override void ResetError()
    {
        base.ResetError();
        Drive.ResetError();
    }

    #endregion
}
