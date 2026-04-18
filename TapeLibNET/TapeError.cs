using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using System.ComponentModel;
using System.Text;
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
/// IOException subclass for tape I/O errors that carries a Win32 error code and a
/// diagnostic breadcrumb trail. Each layer that catches and rethrows can append
/// context via <see cref="AddTrail"/> without losing the original cause.
/// <para>
/// The trail is a list of entries in the form <c>"ClassName.MethodName: message"</c>,
/// ordered from the origination point outward. Use <see cref="TrailText"/> for a
/// single-string representation suitable for logging.
/// </para>
/// </summary>
public class TapeIOException : IOException
{
    private List<string>? _trail;

    /// <summary>The Win32 error code that caused this exception (typed accessor for <see cref="Exception.HResult"/>).</summary>
    public uint Error => (uint)HResult;

    /// <summary>The Win32 error description (accessor for <see cref="Exception.Message"/>).</summary>
    public string ErrorMessage => Message;

    #region *** Constructors ***

    /// <summary>Creates a TapeIOException from a Win32 error code and its description.</summary>
    public TapeIOException(uint errorCode, string errorMessage, Exception? inner = null)
        : base(errorMessage, inner)
    {
        HResult = (int)errorCode;
    }

    /// <summary>Creates a TapeIOException from a Win32 error code, looking up the description.</summary>
    public TapeIOException(uint errorCode, Exception? inner = null)
        : this(errorCode, Marshal.GetPInvokeErrorMessage((int)errorCode), inner)
    {
    }

    /// <summary>Creates a TapeIOException by capturing the current error state of an <see cref="IErrorManageable"/>.</summary>
    public TapeIOException(IErrorManageable source, Exception? inner = null)
        : this(source.LastError, source.LastErrorMessage, inner)
    {
    }

    /// <summary>Wraps an existing IOException (e.g. from the framework) as a TapeIOException, retaining it as inner.</summary>
    public TapeIOException(IOException inner, string? message = null)
        : this((uint)inner.HResult, message ?? inner.Message, inner)
    {
    }

    #endregion

    #region *** Breadcrumb Trail ***

    /// <summary>The diagnostic breadcrumb trail, ordered from origin outward.</summary>
    public IReadOnlyList<string> Trail => _trail ?? (IReadOnlyList<string>)[];

    /// <summary>
    /// Appends a breadcrumb entry with explicit class and method names.
    /// Returns <c>this</c> for fluent use in throw expressions.
    /// </summary>
    public TapeIOException AddTrail(string className, string message, [CallerMemberName] string methodName = "")
    {
        _trail ??= [];
        _trail.Add($"{className}.{methodName}: {message}");
        return this;
    }

    /// <summary>
    /// Appends a breadcrumb entry, deriving the class name from <paramref name="caller"/>'s runtime type.
    /// Returns <c>this</c> for fluent use in throw expressions.
    /// </summary>
    public TapeIOException AddTrail(object caller, string message, [CallerMemberName] string methodName = "")
        => AddTrail(caller.GetType().Name, message, methodName);

    /// <summary>Single-string representation of the trail, one entry per line.</summary>
    public string TrailText
    {
        get
        {
            if (_trail == null || _trail.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            for (int i = 0; i < _trail.Count; i++)
            {
                if (i > 0) sb.Append(" → ");
                sb.Append(_trail[i]);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Returns a message combining the error description and trail (if any).
    /// </summary>
    public override string ToString()
    {
        string trail = TrailText;
        return trail.Length > 0
            ? $"{Message} [Trail: {trail}]"
            : base.ToString()!;
    }

    #endregion
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
