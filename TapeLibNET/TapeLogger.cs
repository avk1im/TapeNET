using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TapeConNET
{
    /*
    public class AsyncFileLoggerFactory : ILoggerFactory
    {
        private readonly string _logFilePath;

        public AsyncFileLoggerFactory(string logFilePath)
        {
            _logFilePath = logFilePath;
        }

        public void Dispose()
        {
            // Clean up any resources if needed
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new AsyncFileLogger(_logFilePath);
        }

        public void AddProvider(ILoggerProvider provider)
        {
            // Not needed for file logging
        }
    }
    */

    /*
    public class AsyncFileLogger : ILogger
    {
        private readonly string _logFilePath;

        public AsyncFileLogger(string logFilePath)
        {
            _logFilePath = logFilePath;
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return null; // Not needed for file logging
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true; // Always log everything
        }

        public async Task LogAsync<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            var logMessage = formatter(state, exception);
            await WriteLogAsync($"{DateTime.Now} [{logLevel}] {logMessage}{Environment.NewLine}");
        }

        private async Task WriteLogAsync(string logEntry)
        {
            using var writer = new StreamWriter(_logFilePath, append: true);

            await writer.WriteAsync(logEntry);
        }
    } // class AsyncFielLogger
    */

    ////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// 
    /// 
    /// </summary>
    /// 

    public struct LogMessage
    {
        public DateTimeOffset Timestamp { get; set; }
        public string Message { get; set; }
    }


    /// <summary>
    /// Holds the information for a single log entry.
    /// </summary>
    /// <remarks>
    /// Initializes an instance of the LogEntry struct.
    /// </remarks>
    /// <param name="timestamp">The time the event occured</param>
    /// <param name="logLevel">The log level.</param>
    /// <param name="category">The category name for the log.</param>
    /// <param name="eventId">The log event Id.</param>
    /// <param name="state">The state for which log is being written.</param>
    /// <param name="exception">The log exception.</param>
    /// <param name="formatter">The formatter.</param>
    public readonly struct LogEntry<TState>(DateTimeOffset timestamp, LogLevel logLevel,
        string category, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {

        /// <summary>
        /// Gets the time the event occured
        /// </summary>
        public DateTimeOffset Timestamp { get; } = timestamp;

        /// <summary>
        /// Gets the LogLevel
        /// </summary>
        public LogLevel LogLevel { get; } = logLevel;

        /// <summary>
        /// Gets the log category
        /// </summary>
        public string Category { get; } = category;

        /// <summary>
        /// Gets the log EventId
        /// </summary>
        public EventId EventId { get; } = eventId;

        /// <summary>
        /// Gets the TState
        /// </summary>
        public TState State { get; } = state;

        /// <summary>
        /// Gets the log exception
        /// </summary>
        public Exception? Exception { get; } = exception;

        /// <summary>
        /// Gets the formatter
        /// </summary>
        public Func<TState, Exception?, string> Formatter { get; } = formatter;
    }

    public enum PeriodicityOptions
    {
        Daily,
        Hourly,
        Minutely,
        Monthly
    }


    public interface ILogFormatter
    {
        /// <summary>
        /// Gets the name of the formatter
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Writes the log message to the specified StringBuilder.
        /// </summary>
        /// <param name="logEntry">The log entry.</param>
        /// <param name="scopeProvider">The provider of scope data.</param>
        /// <param name="stringBuilder">The string builder for building the message to write to the log file.</param>
        /// <typeparam name="TState">The type of the object to be written.</typeparam>
        void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, StringBuilder stringBuilder);
    }

    /// <summary>
    /// A simple formatter for log messages
    /// </summary>
    public class SimpleLogFormatter : ILogFormatter
    {
        public string Name => "simple";

        /// <inheritdoc />
        public void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, StringBuilder builder)
        {
            builder.Append(logEntry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
            builder.Append(" [");
            builder.Append(logEntry.LogLevel.ToString());
            builder.Append("] ");
            builder.Append(logEntry.Category);

            if (scopeProvider != null)
            {
                scopeProvider.ForEachScope((scope, stringBuilder) =>
                {
                    stringBuilder.Append(" => ").Append(scope);
                }, builder);

                builder.Append(':').AppendLine();
            }
            else
            {
                builder.Append(": ");
            }

            builder.AppendLine(logEntry.Formatter(logEntry.State, logEntry.Exception));

            if (logEntry.Exception != null)
            {
                builder.AppendLine(logEntry.Exception.ToString());
            }
        }
    } // class SimpleLogFornatter

    public class BatchingLoggerOptions
    {
        private int? _batchSize;
        private int _backgroundQueueSize = 1000;
        private TimeSpan _flushPeriod = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Gets or sets the period after which logs will be flushed to the store.
        /// </summary>
        public TimeSpan FlushPeriod
        {
            get { return _flushPeriod; }
            set
            {
                if (value <= TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), $"{nameof(FlushPeriod)} must be positive.");
                }
                _flushPeriod = value;
            }
        }

        /// <summary>
        /// Gets or sets the maximum size of the background log message queue or null for no limit.
        /// After maximum queue size is reached log event sink would start blocking.
        /// Defaults to <c>1000</c>.
        /// </summary>
        public int BackgroundQueueSize
        {
            get { return _backgroundQueueSize; }
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(BackgroundQueueSize));
                _backgroundQueueSize = value;
            }
        }

        /// <summary>
        /// Gets or sets a maximum number of events to include in a single batch or null for no limit.
        /// </summary>
        /// Defaults to <c>null</c>.
        public int? BatchSize
        {
            get { return _batchSize; }
            set
            {
                if (value != null)
                    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value.Value, nameof(BatchSize));
                _batchSize = value;
            }
        }

        /// <summary>
        /// Gets or sets value indicating if logger accepts and queues writes.
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether scopes should be included in the message.
        /// Defaults to <c>false</c>.
        /// </summary>
        public bool IncludeScopes { get; set; } = false;

        /// <summary>
        /// Gets of sets the name of the log message formatter to use.
        /// Defaults to "simple" />.
        /// </summary>
        public string FormatterName { get; set; } = "simple";
    } // class BatchingLoggerOptions


    public abstract class BatchingLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private readonly List<LogMessage> _currentBatch = [];
        private readonly TimeSpan _interval;
        private readonly int? _queueSize;
        private readonly int? _batchSize;
        private readonly IDisposable? _optionsChangeToken;

        private BlockingCollection<LogMessage>? _messageQueue = null;
        private Task? _outputTask;
        private CancellationTokenSource? _cancellationTokenSource;

        private bool _includeScopes;
        private IExternalScopeProvider? _scopeProvider = null;
        private readonly ILogFormatter _formatter;

        internal IExternalScopeProvider? ScopeProvider => _includeScopes ? _scopeProvider : null;

        protected BatchingLoggerProvider(IOptionsMonitor<BatchingLoggerOptions> options, IEnumerable<ILogFormatter> formatters)
        {
            // NOTE: Only IsEnabled and IncludeScopes are monitored

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.CurrentValue.BatchSize ?? 1, nameof(options.CurrentValue.BatchSize));
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.CurrentValue.FlushPeriod, TimeSpan.Zero, nameof(options.CurrentValue.FlushPeriod));
            var loggerOptions = options.CurrentValue;

            var formatterName = (string.IsNullOrEmpty(loggerOptions.FormatterName)
                ? "simple"
                : loggerOptions.FormatterName).ToLowerInvariant();
            var formatter = formatters.FirstOrDefault(x => x.Name == formatterName);

            ArgumentNullException.ThrowIfNull(formatter, nameof(loggerOptions.FormatterName));
                // $"Unknown formatter name {formatterName} - ensure custom formatters are registered correctly with the DI container"

            _formatter = formatter;
            _interval = loggerOptions.FlushPeriod;
            _batchSize = loggerOptions.BatchSize;
            _queueSize = loggerOptions.BackgroundQueueSize;

            _optionsChangeToken = options.OnChange(UpdateOptions);
            UpdateOptions(options.CurrentValue);
        }

        public bool IsEnabled { get; private set; }

        private void UpdateOptions(BatchingLoggerOptions options)
        {
            var oldIsEnabled = IsEnabled;
            IsEnabled = options.IsEnabled;
            _includeScopes = options.IncludeScopes;

            if (oldIsEnabled != IsEnabled)
            {
                if (IsEnabled)
                {
                    Start();
                }
                else
                {
                    Stop();
                }
            }

        }

        protected abstract Task WriteMessagesAsync(IEnumerable<LogMessage> messages, CancellationToken token);

        private async Task ProcessLogQueue()
        {
            if (!IsRunning)
                return;
            Debug.Assert(_messageQueue != null);
            Debug.Assert(_cancellationTokenSource != null);

            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                var limit = _batchSize ?? int.MaxValue;

                while (limit > 0 && _messageQueue.TryTake(out var message))
                {
                    _currentBatch.Add(message);
                    limit--;
                }

                if (_currentBatch.Count > 0)
                {
                    try
                    {
                        await WriteMessagesAsync(_currentBatch, _cancellationTokenSource.Token);
                    }
                    catch
                    {
                        // ignored
                    }

                    _currentBatch.Clear();
                }

                await IntervalAsync(_interval, _cancellationTokenSource.Token);
            }
        }

        protected virtual Task IntervalAsync(TimeSpan interval, CancellationToken cancellationToken)
        {
            return Task.Delay(interval, cancellationToken);
        }

        internal void AddMessage(DateTimeOffset timestamp, string message)
        {
            if (!IsRunning)
                return;
            Debug.Assert(_messageQueue != null);
            Debug.Assert(_cancellationTokenSource != null);

            if (!_messageQueue.IsAddingCompleted)
            {
                try
                {
                    _messageQueue.Add(new LogMessage { Message = message, Timestamp = timestamp }, _cancellationTokenSource.Token);
                }
                catch
                {
                    //cancellation token canceled or CompleteAdding called
                }
            }
        }

        private void Start()
        {
            _messageQueue = _queueSize == null ?
                new (new ConcurrentQueue<LogMessage>()) :
                new (new ConcurrentQueue<LogMessage>(), _queueSize.Value);

            _cancellationTokenSource = new CancellationTokenSource();
            _outputTask = Task.Run(ProcessLogQueue);
        }

        private bool IsRunning => _messageQueue != null && _cancellationTokenSource != null && _outputTask != null;

        private void Stop()
        {
            if (!IsRunning)
                return;
            Debug.Assert(_cancellationTokenSource != null);
            Debug.Assert(_messageQueue != null);
            Debug.Assert(_outputTask != null);

            _cancellationTokenSource.Cancel();
            _messageQueue.CompleteAdding();

            try
            {
                _outputTask.Wait(_interval);
            }
            catch (TaskCanceledException)
            {
            }
            catch (AggregateException ex) when (ex.InnerExceptions.Count == 1 && ex.InnerExceptions[0] is TaskCanceledException)
            {
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _optionsChangeToken?.Dispose();
                if (IsEnabled)
                    Stop();
            }
        }


        public ILogger CreateLogger(string categoryName)
        {
            return new BatchingLogger(this, categoryName, _formatter);
        }

        void ISupportExternalScope.SetScopeProvider(IExternalScopeProvider scopeProvider)
        {
            _scopeProvider = scopeProvider;
        }
    } // class BatchingLoggerProvider


    /// <summary>
    /// Options for file logging.
    /// </summary>
    public class FileLoggerOptions : BatchingLoggerOptions
    {
        private int? _fileSizeLimit = 10 * 1024 * 1024;
        private int? _retainedFileCountLimit = 2;
        private int? _filesPerPeriodicityLimit = 10;
        private string _fileName = "logs-";
        private string _extension = "txt";


        /// <summary>
        /// Gets or sets a strictly positive value representing the maximum log size in bytes or null for no limit.
        /// Once the log is full, no more messages will be appended, unless <see cref="FilesPerPeriodicityLimit"/>
        /// is greater than 1. Defaults to <c>10MB</c>.
        /// </summary>
        public int? FileSizeLimit
        {
            get { return _fileSizeLimit; }
            set
            {
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), $"{nameof(FileSizeLimit)} must be positive.");
                }
                _fileSizeLimit = value;
            }
        }

        /// <summary>
        /// Gets or sets a value representing the maximum number of files allowed for a given <see cref="Periodicity"/> .
        /// Once the specified number of logs per periodicity are created, no more log files will be created. Note that these extra files
        /// do not count towards the RetrainedFileCountLimit. Defaults to <c>1</c>.
        /// </summary>
        public int? FilesPerPeriodicityLimit
        {
            get { return _filesPerPeriodicityLimit; }
            set
            {
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), $"{nameof(FilesPerPeriodicityLimit)} must be greater than 0.");
                }
                _filesPerPeriodicityLimit = value;
            }
        }

        /// <summary>
        /// Gets or sets a strictly positive value representing the maximum retained file count or null for no limit.
        /// Defaults to <c>2</c>.
        /// </summary>
        public int? RetainedFileCountLimit
        {
            get { return _retainedFileCountLimit; }
            set
            {
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), $"{nameof(RetainedFileCountLimit)} must be positive.");
                }
                _retainedFileCountLimit = value;
            }
        }

        /// <summary>
        /// Gets or sets the filename prefix to use for log files.
        /// Defaults to <c>logs-</c>.
        /// </summary>
        public string FileName
        {
            get { return _fileName; }
            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    throw new ArgumentException("File name may not be null or empty", nameof(value));
                }
                _fileName = value;
            }
        }

        /// <summary>
        /// Gets or sets the filename extension to use for log files.
        /// Defaults to <c>txt</c>.
        /// Will strip any prefixed .
        /// </summary>
        public string Extension
        {
            get { return _extension; }
            set { _extension = value?.TrimStart('.') ?? string.Empty; }
        }

        /// <summary>
        /// Gets or sets the periodicity for rolling over log files.
        /// </summary>
        public PeriodicityOptions Periodicity { get; set; } = PeriodicityOptions.Daily;

        /// <summary>
        /// The directory in which log files will be written, relative to the app process.
        /// Default to <c>Logs</c>
        /// </summary>
        /// <returns></returns>
        public string LogDirectory { get; set; } = "Logs";
    } // class FileLoggerOptions

    public class FileLoggerProvider : BatchingLoggerProvider
    {
        private readonly string _path;
        private readonly string _fileName;
        private readonly string? _extension;
        private readonly int? _maxFileSize;
        private readonly int? _maxRetainedFiles;
        private readonly int _maxFileCountPerPeriodicity;
        private readonly PeriodicityOptions _periodicity;

        /// <summary>
        /// Creates an instance of the <see cref="FileLoggerProvider" />
        /// </summary>
        /// <param name="options">The options object controlling the logger</param>
        /// <param name="formatter"></param>
        public FileLoggerProvider(IOptionsMonitor<FileLoggerOptions> options, IEnumerable<ILogFormatter> formatter) : base(options, formatter)
        {
            var loggerOptions = options.CurrentValue;
            _path = loggerOptions.LogDirectory;
            _fileName = loggerOptions.FileName;
            _extension = string.IsNullOrEmpty(loggerOptions.Extension) ? null : "." + loggerOptions.Extension;
            _maxFileSize = loggerOptions.FileSizeLimit;
            _maxRetainedFiles = loggerOptions.RetainedFileCountLimit;
            _maxFileCountPerPeriodicity = loggerOptions.FilesPerPeriodicityLimit ?? 1;
            _periodicity = loggerOptions.Periodicity;
        }


        /// <inheritdoc />
        protected override async Task WriteMessagesAsync(IEnumerable<LogMessage> messages, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(_path);

            foreach (var group in messages.GroupBy(GetGrouping))
            {
                var baseName = GetBaseName(group.Key);
                var fullName = GetLogFilePath(baseName, group.Key);

                if (fullName == null)
                    return;

                using var streamWriter = File.AppendText(fullName);
                foreach (var item in group)
                    await streamWriter.WriteAsync(item.Message);
            }

            RollFiles();
        }

        private string? GetLogFilePath(string baseName, (int Year, int Month, int Day, int Hour, int Minute) fileNameGrouping)
        {
            if (_maxFileCountPerPeriodicity == 1)
            {
                var fullPath = Path.Combine(_path, $"{baseName}{_extension}");
                return IsAvailable(fullPath) ? fullPath : null;
            }

            var counter = GetCurrentCounter(baseName);

            while (counter < _maxFileCountPerPeriodicity)
            {
                var fullName = Path.Combine(_path, $"{baseName}.{counter}{_extension}");
                if (!IsAvailable(fullName))
                {
                    counter++;
                    continue;
                }

                return fullName;
            }

            return null;

            bool IsAvailable(string filename)
            {
                var fileInfo = new FileInfo(filename);
                return !(_maxFileSize > 0 && fileInfo.Exists && fileInfo.Length > _maxFileSize);
            }
        }

        private int GetCurrentCounter(string baseName)
        {
            try
            {
                var files = Directory.GetFiles(_path, $"{baseName}.*{_extension}");
                if (files.Length == 0)
                {
                    // No rolling file currently exists with the base name as pattern
                    return 0;
                }

                // Get file with highest counter
                var latestFile = files.OrderByDescending(file => file).First();

                var baseNameLength = Path.Combine(_path, baseName).Length + 1;
                var fileWithoutPrefix = latestFile.AsSpan()[baseNameLength..];
                var indexOfPeriod = fileWithoutPrefix.IndexOf('.');
                if (indexOfPeriod < 0)
                {
                    // No additional dot could be found
                    return 0;
                }

                var counterSpan = fileWithoutPrefix[..indexOfPeriod];
                if (int.TryParse(counterSpan.ToString(), out var counter))
                {
                    return counter;
                }

                return 0;
            }
            catch (Exception)
            {
                return 0;
            }

        }

        private string GetBaseName((int Year, int Month, int Day, int Hour, int Minute) group)
        {
            return _periodicity switch
            {
                PeriodicityOptions.Minutely => $"{_fileName}{group.Year:0000}{group.Month:00}{group.Day:00}{group.Hour:00}{group.Minute:00}",
                PeriodicityOptions.Hourly => $"{_fileName}{group.Year:0000}{group.Month:00}{group.Day:00}{group.Hour:00}",
                PeriodicityOptions.Daily => $"{_fileName}{group.Year:0000}{group.Month:00}{group.Day:00}",
                PeriodicityOptions.Monthly => $"{_fileName}{group.Year:0000}{group.Month:00}",
                _ => throw new InvalidDataException("Invalid periodicity"),
            };
        }

        private (int Year, int Month, int Day, int Hour, int Minute) GetGrouping(LogMessage message)
        {
            return (message.Timestamp.Year, message.Timestamp.Month, message.Timestamp.Day, message.Timestamp.Hour, message.Timestamp.Minute);
        }

        /// <summary>
        /// Deletes old log files, keeping a number of files defined by <see cref="FileLoggerOptions.RetainedFileCountLimit" />
        /// </summary>
        protected void RollFiles()
        {
            if (_maxRetainedFiles > 0)
            {
                var groupsToDelete = new DirectoryInfo(_path)
                    .GetFiles(_fileName + "*")
                    .GroupBy(file => GetFilenameForGrouping(file.Name))
                    .OrderByDescending(f => f.Key)
                    .Skip(_maxRetainedFiles.Value);

                foreach (var groupToDelete in groupsToDelete)
                {
                    foreach (var fileToDelete in groupToDelete)
                    {
                        fileToDelete.Delete();
                    }
                }
            }

            string GetFilenameForGrouping(string filename)
            {
                var hasExtension = !string.IsNullOrEmpty(_extension);
                var isMultiFile = _maxFileCountPerPeriodicity > 1;
                return (isMultiFile, hasExtension) switch
                {
                    (false, false) => filename,
                    (false, true) => Path.GetFileNameWithoutExtension(filename),
                    (true, false) => Path.GetFileNameWithoutExtension(filename),
                    (true, true) => Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(filename)),
                };
            }
        }
    } // class FileLoggerProvider


    public class BatchingLogger(BatchingLoggerProvider loggerProvider, string categoryName, ILogFormatter logFormatter) : ILogger
    {

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            // NOTE: Differs from source
            return loggerProvider.ScopeProvider?.Push(state);
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return loggerProvider.IsEnabled;
        }

        public void Log<TState>(DateTimeOffset timestamp, LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var logEntry = new LogEntry<TState>(timestamp, logLevel, categoryName, eventId, state, exception, formatter);
            var builder = new StringBuilder();
            logFormatter.Write(in logEntry, loggerProvider.ScopeProvider, builder);
            loggerProvider.AddMessage(timestamp, builder.ToString());
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Log(DateTimeOffset.Now, logLevel, eventId, state, exception, formatter);
        }
    } // class BatchingLogger
}
