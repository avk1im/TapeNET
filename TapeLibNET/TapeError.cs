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
    private List<string>? m_trail;

    /// <summary>The Win32 error code that caused this exception (typed accessor for <see cref="Exception.HResult"/>).</summary>
    public uint Error => (uint)HResult;

    /// <summary>The Win32 error code as <see cref="WIN32_ERROR"/> for internal switch/comparison use.</summary>
    internal WIN32_ERROR ErrorWin32 => (WIN32_ERROR)HResult;

    /// <summary>The Win32 error description (accessor for <see cref="Exception.Message"/>).</summary>
    public string ErrorMessage => base.Message;

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

    /// <summary>
    /// Creates a TapeIOException from an <see cref="IErrorManageable"/> source, adding an initial
    /// trail entry from the caller's context. Combines construction and first breadcrumb in one call.
    /// </summary>
    public TapeIOException(IErrorManageable source, object caller, string message,
        [CallerMemberName] string methodName = "", Exception? inner = null)
        : this(source.LastError, source.LastErrorMessage, inner)
    {
        AddTrail(caller, message, methodName);
    }

    /// <summary>
    /// Creates a TapeIOException from an error code and message, adding an initial trail entry.
    /// </summary>
    public TapeIOException(uint errorCode, string errorMessage, object caller, string message,
        [CallerMemberName] string methodName = "", Exception? inner = null)
        : this(errorCode, errorMessage, inner)
    {
        AddTrail(caller, message, methodName);
    }

    /* // Handled below in a more general form that can extract from any Exception, not just IOException
    /// <summary>Wraps an existing IOException (e.g. from the framework) as a TapeIOException, retaining it as inner.</summary>
    public TapeIOException(IOException inner, string? message = null)
        : this((uint)inner.HResult, message ?? inner.Message, inner)
    {
    }
    */

    /// <summary>Wraps an existing Exception as a TapeIOException: attempts to extract the error code if possible.</summary>
    public TapeIOException(Exception ex, string? message = null)
        : this(ex is IOException ioex ? (uint)ioex.HResult :
              ex is Win32Exception w32ex ? (uint)w32ex.NativeErrorCode :
              (uint)WIN32_ERROR.ERROR_UNHANDLED_EXCEPTION,
              message ?? ex.Message, ex)
    {
    }

    #endregion

    #region *** Error Classification ***

    /// <summary>True when the error indicates end-of-media (tape full or no more data).</summary>
    public bool IsEOM => ErrorWin32 is WIN32_ERROR.ERROR_END_OF_MEDIA or WIN32_ERROR.ERROR_NO_DATA_DETECTED;

    /// <summary>True when the error indicates invalid or corrupted data.</summary>
    public bool IsInvalidData => ErrorWin32 is WIN32_ERROR.ERROR_INVALID_DATA;

    /// <summary>True when the error indicates a CRC / hash verification failure.</summary>
    public bool IsCRC => ErrorWin32 is WIN32_ERROR.ERROR_CRC;

    /// <summary>True when the error indicates an invalid handle or device state precondition.</summary>
    public bool IsDeviceNotReady => ErrorWin32 is WIN32_ERROR.ERROR_INVALID_HANDLE or WIN32_ERROR.ERROR_NO_MEDIA_IN_DRIVE;

    #endregion

    #region *** Breadcrumb Trail ***

    /// <summary>The diagnostic breadcrumb trail, ordered from origin outward.</summary>
    public IReadOnlyList<string> Trail => m_trail ?? (IReadOnlyList<string>)[];

    /// <summary>
    /// Appends a breadcrumb entry with explicit class and method names.
    /// Returns <c>this</c> for fluent use in throw expressions.
    /// </summary>
    public TapeIOException AddTrail(string className, string message, [CallerMemberName] string methodName = "")
    {
        m_trail ??= [];
        m_trail.Add($"{className}.{methodName}: {message}");
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
            if (m_trail == null || m_trail.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            for (int i = 0; i < m_trail.Count; i++)
            {
                if (i > 0) sb.Append(" → ");
                sb.Append(m_trail[i]);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Compact trail summary for user-facing display: includes the origin and outermost
    /// breadcrumb (if different), omitting class/method names.
    /// Returns empty string if no trail entries exist.
    /// </summary>
    public string TrailSummary
    {
        get
        {
            if (m_trail == null || m_trail.Count == 0)
                return string.Empty;

            // Extract just the message portion after "ClassName.MethodName: "
            static string ExtractMessage(string entry)
            {
                int colonPos = entry.IndexOf(": ", StringComparison.Ordinal);
                return colonPos >= 0 ? entry[(colonPos + 2)..] : entry;
            }

            string first = ExtractMessage(m_trail[0]);
            if (m_trail.Count == 1)
                return first;

            string last = ExtractMessage(m_trail[^1]);
            return $"{first} → {last}";
        }
    }

    #endregion

    #region *** Formatting ***

    /// <summary>
    /// User-facing message: the error description plus a compact trail summary
    /// (origin and outermost context, without class/method details).
    /// </summary>
    public override string Message
    {
        get
        {
            string summary = TrailSummary;
            return summary.Length > 0
                ? $"{base.Message} [{summary}]"
                : base.Message;
        }
    }

    /// <summary>
    /// Diagnostic representation including error description, full trail, and stack trace.
    /// Suitable for logging and debugging.
    /// </summary>
    public override string ToString()
    {
        string trail = TrailText;
        return trail.Length > 0
            ? $"{base.Message} [Trail: {trail}]{Environment.NewLine}{StackTrace}"
            : base.ToString()!;
    }

    /// <summary>
    /// Intermediate-detail representation for structured logging: error code, message, and full trail
    /// without stack trace.
    /// </summary>
    public string ToLogString()
    {
        string trail = TrailText;
        return trail.Length > 0
            ? $"0x{Error:X8}: {base.Message} [Trail: {trail}]"
            : $"0x{Error:X8}: {base.Message}";
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

    /// <summary>True when the current error indicates end-of-media (tape full or no more data).</summary>
    public bool IsEOM => m_errorOwn is WIN32_ERROR.ERROR_END_OF_MEDIA or WIN32_ERROR.ERROR_NO_DATA_DETECTED;

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

    /// <summary>
    /// Logs a <see cref="TapeIOException"/> at Warning level using its intermediate-detail
    ///  <see cref="TapeIOException.ToLogString"/> representation (error code + message + trail, no stack trace).
    ///  Falls back to standard exception logging for other exception types.
    /// </summary>
    protected void LogTapeException(Exception ex, string context, [CallerMemberName] string methodName = "")
    {
        if (ex is TapeIOException tioex)
            m_logger.LogWarning("{Prefix}: {Context} in {Method}: {Detail}",
                LogPrefix, context, methodName, tioex.ToLogString());
        else
            m_logger.LogWarning("{Prefix}: {Context} in {Method}: {Exception}",
                LogPrefix, context, methodName, ex.Message);
    }

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
public class TapeDriveHolder<T>(TapeDrive drive)
    : ErrorManageableBase(drive.LoggerFactory.CreateLogger<T>())
{

    #region *** Constructor ***

    #endregion

    #region *** Properties ***

    public TapeDrive Drive { get; } = drive;
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
