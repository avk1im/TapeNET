using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;


namespace TapeNET
{
    public interface IErrorMangeable
    {
        public uint LastError { get; }

        public string LastErrorMessage { get; }

        void ResetError();
    }

    public class TapeDriveHolder<T>(TapeDrive drive) : IErrorMangeable
    {
        #region *** Private fields ***

        protected readonly ILogger<T> m_logger = drive.LoggerFactory.CreateLogger<T>();

        private WIN32_ERROR m_errorOwn = WIN32_ERROR.NO_ERROR;
        private string m_errorMessageOwn = string.Empty;

        #endregion // Private fields


        #region *** Constants ***

        #endregion // Constants


        #region *** Constructors ***

        #endregion // Constructors


        #region *** Properties ***

        public TapeDrive Drive => drive;
        public uint DriveNumber => Drive.DriveNumber;
        private List<IErrorMangeable> ErrorSources { get; init; } = [ drive ];

        #endregion // Properties


        #region *** Error handling ***
        // give priority to own error
        internal WIN32_ERROR LastErrorWin32
        {
            get => m_errorOwn != WIN32_ERROR.NO_ERROR ? m_errorOwn : GetErrorFromSources();
            set => SetError(value);
        }
        public uint LastError => (uint)LastErrorWin32;
        public string LastErrorMessage => m_errorOwn != WIN32_ERROR.NO_ERROR ? m_errorMessageOwn : GetErrorMessageFromSources();

        public bool WentOK => LastErrorWin32 == WIN32_ERROR.NO_ERROR;
        public bool WentBad => !WentOK;

        public void ResetError()
        {
            m_errorOwn = WIN32_ERROR.NO_ERROR;
            m_errorMessageOwn = string.Empty;

            foreach (var source in ErrorSources)
                source.ResetError();
        }
        internal void SetError(WIN32_ERROR error, string? message = null)
        {
            m_errorOwn = error;
            m_errorMessageOwn = string.IsNullOrEmpty(message) ? Marshal.GetPInvokeErrorMessage((int)error) : message;
        }
        internal void SetError(Exception ex, string? message = null)
        {
            WIN32_ERROR error = (ex is IOException ioex) ? (WIN32_ERROR)ioex.HResult :
                (ex is Win32Exception w32ex) ? (WIN32_ERROR)w32ex.NativeErrorCode :
                    WIN32_ERROR.ERROR_UNHANDLED_EXCEPTION;

            SetError(error, message ?? ex.Message);
        }

        protected void AddErrorSource(IErrorMangeable source) // add only if not present yet
        {
            if (ErrorSources.Contains(source))
                ErrorSources.Add(source);
        }
        protected void RemoveErrorSource(IErrorMangeable source)
        {
            ErrorSources.Remove(source);
        }
        private WIN32_ERROR GetErrorFromSources()
        {
            // scan error sources in reverse order
            for (int i = ErrorSources.Count - 1; i >= 0; i--)
            {
                var error = (WIN32_ERROR)ErrorSources[i].LastError;
                if (error != WIN32_ERROR.NO_ERROR)
                    return error;
            }
            return WIN32_ERROR.NO_ERROR;
        }
        private string GetErrorMessageFromSources()
        {
            // scan error sources in reverse order
            for (int i = ErrorSources.Count - 1; i >= 0; i--)
            {
                var error = (WIN32_ERROR)ErrorSources[i].LastError;
                if (error != WIN32_ERROR.NO_ERROR)
                    return ErrorSources[i].LastErrorMessage;
            }
            return string.Empty;
        }

        #endregion // Error handling


        #region *** Logging facilities ***

        protected delegate void LogMethod(string? message, params object?[] args);
        protected void LogError(LogMethod logMethod, string message, string methodName)
        {
            // Log the message, the last error code as hex, and error message
            if (string.IsNullOrEmpty(methodName))
                logMethod("Drive #{Drive}: {Message}: error: 0x{Error:X8} >{ErrorMessage}<",
                    DriveNumber, message, LastError, LastErrorMessage);
            else
                logMethod("Drive #{Drive}: {Message} in {Method}: error: 0x{Error:X8} >{ErrorMessage}<",
                    DriveNumber, message, methodName, LastError, LastErrorMessage);
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

        #endregion // Logging facilities

    } // TapeDriveHolder

} // namespace TapeNET
