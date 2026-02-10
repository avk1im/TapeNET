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
    private WIN32_ERROR m_stickyError = WIN32_ERROR.NO_ERROR;
    private string m_errorMessageOwn = string.Empty;

    #endregion

    #region *** Protected Fields ***

    protected readonly ILogger m_logger = logger;

    #endregion
    #region *** Constructor ***

    #endregion

    #region *** IErrorManageable Implementation ***

    public uint LastError => (uint)LastErrorWin32;
    public string LastErrorMessage => m_errorMessageOwn;

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

    /// <summary>Previous significant error (before last reset).</summary>
    internal WIN32_ERROR LastStickyErrorWin32 => m_stickyError;
    internal uint LastStickyError => (uint)LastStickyErrorWin32;

    /// <summary>Returns last error if set, otherwise the sticky error.</summary>
    internal WIN32_ERROR LastSignificantErrorWin32 =>
        LastErrorWin32 == WIN32_ERROR.NO_ERROR ? LastStickyErrorWin32 : LastErrorWin32;
    public uint LastSignificantError => (uint)LastSignificantErrorWin32;
    public string LastSignificantErrorMessage => Marshal.GetPInvokeErrorMessage((int)LastSignificantError);

    public bool WentOK => m_errorOwn == WIN32_ERROR.NO_ERROR;
    public bool WentBad => !WentOK;

    internal void SetError(WIN32_ERROR error, string? message = null)
    {
        if (m_errorOwn != WIN32_ERROR.NO_ERROR)
            m_stickyError = m_errorOwn;

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

public class TapeDriveHolder<T> : ErrorManageableBase
{
    #region *** Private Fields ***

    private readonly List<IErrorManageable> m_errorSources;

    #endregion

    #region *** Constructor ***

    public TapeDriveHolder(TapeDrive drive)
        : base(drive.LoggerFactory.CreateLogger<T>())
    {
        Drive = drive;
        m_errorSources = [drive];
    }

    #endregion

    #region *** Properties ***

    public TapeDrive Drive { get; }
    public uint DriveNumber => Drive.DriveNumber;
    protected override string LogPrefix => $"Drive #{DriveNumber}";

    #endregion

    #region *** Error Handling - Extended ***

    public new uint LastError => base.LastError != 0 ? base.LastError : (uint)GetErrorFromSources();
    public new string LastErrorMessage => base.LastError != 0 ? base.LastErrorMessage : GetErrorMessageFromSources();

    public new bool WentOK => LastError == 0;
    public new bool WentBad => !WentOK;

    public override void ResetError()
    {
        base.ResetError();
        foreach (var source in m_errorSources)
            source.ResetError();
    }

    protected void AddErrorSource(IErrorManageable source)
    {
        if (!m_errorSources.Contains(source))
            m_errorSources.Add(source);
    }

    protected void RemoveErrorSource(IErrorManageable source)
    {
        m_errorSources.Remove(source);
    }

    private WIN32_ERROR GetErrorFromSources()
    {
        for (int i = m_errorSources.Count - 1; i >= 0; i--)
        {
            var error = (WIN32_ERROR)m_errorSources[i].LastError;
            if (error != WIN32_ERROR.NO_ERROR)
                return error;
        }
        return WIN32_ERROR.NO_ERROR;
    }

    private string GetErrorMessageFromSources()
    {
        for (int i = m_errorSources.Count - 1; i >= 0; i--)
        {
            var error = (WIN32_ERROR)m_errorSources[i].LastError;
            if (error != WIN32_ERROR.NO_ERROR)
                return m_errorSources[i].LastErrorMessage;
        }
        return string.Empty;
    }

    #endregion
}
