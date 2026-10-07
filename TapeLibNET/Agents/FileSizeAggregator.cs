using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TapeLibNET;

/// <summary>
/// Aggregates the total size of a collection of files, including both local and remote files.
/// <para>Performs asynchronous aggregation (for remote files, using a thread pool) and can be canceled.</para>
/// </summary>
/// <param name="files">The collection of file paths to aggregate.</param>
public sealed class FileSizeAggregator(IEnumerable<string?> files) : IDisposable
{
    private readonly IEnumerable<string?> _files = files
        ?? throw new ArgumentNullException(nameof(files));
    private readonly CancellationTokenSource _cts = new();

    private Task? _worker;
    private BlockingCollection<string>? _remoteQueue;
    private const int RemoteQueueCapacity = 64;
    private Task? _remoteConsumer;

    private bool _disposed;
    private bool _cleanedUp;

    private long _totalSize = 0L;
    /// <summary>
    /// The accumulated total size of the files processed so far, in bytes.
    /// <para>This value is updated as files are processed and can be accessed at any time.</para>
    /// </summary>
    public long TotalSize => _totalSize;

    /// <summary>
    /// Indicates whether the aggregation process is currently running.
    /// </summary>
    public bool IsRunning => _worker is { IsCompleted: false };
    /// <summary>
    /// Indicates whether the aggregation process has completed.
    /// </summary>
    public bool IsCompleted => _worker is { IsCompleted: true } && !_cts.IsCancellationRequested;
    /// <summary>
    /// Indicates whether the aggregation process has been canceled.
    /// </summary>
    public bool IsCanceled => _cts.IsCancellationRequested;

    /// <summary>
    /// Starts the aggregation process asynchronously. This method initializes the necessary resources and begins processing the provided file paths.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if this instance has been disposed.</exception>
    public void Start()
    {
        ThrowIfDisposed();

        if (_worker != null)
            return;

        _worker = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>
    /// Cancels the aggregation process. This method signals the cancellation token, which will cause the ongoing processing to stop as soon as possible.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if this instance has been disposed.</exception>
    public void Cancel()
    {
        ThrowIfDisposed();
        _cts.Cancel();
    }

    private async Task RunAsync(CancellationToken token)
    {
        // Initialize remote queue and consumer
        _remoteQueue = new BlockingCollection<string>(boundedCapacity: RemoteQueueCapacity);

        var remoteOptions = new ParallelOptions
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2)
        };

        _remoteConsumer = Task.Run(() =>
        {
            try
            {
                Parallel.ForEach(_remoteQueue.GetConsumingEnumerable(token), remoteOptions, file =>
                {
                    if (token.IsCancellationRequested)
                        return;

                    try
                    {
                        var size = TryGetRemoteFileSize(file);
                        if (size.HasValue)
                            Interlocked.Add(ref _totalSize, size.Value);
                    }
                    catch
                    {
                        // ignore
                    }
                });
            }
            catch
            {
                // ignore
            }
        }, token);

        try
        {
            foreach (var file in _files)
            {
                if (token.IsCancellationRequested)
                    break;

                if (file is null)
                {
                    _cts.Cancel();
                    break;
                }

                // Relative paths → always local
                if (!Path.IsPathFullyQualified(file))
                {
                    ProcessLocal(file);
                    continue;
                }

                // Absolute path → classify
                if (IsRemotePath(file))
                {
                    _remoteQueue.Add(file, token);
                }
                else
                {
                    ProcessLocal(file);
                }
            }
        }
        finally
        {
            CleanUp(internalCall: true);
        }
    }

    private void ProcessLocal(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (info.Exists)
                _totalSize += info.Length; // no need for Interlocked.Add here since called synrchonously
        }
        catch
        {
            // ignore
        }
    }

    private static bool IsRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        // 1. Extended-length UNC: \\?\UNC\server\share
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return true;

        // 2. Extended-length local path: \\?\C:\...
        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            return false;

        // 3. Regular UNC: \\server\share
        if (path.StartsWith(@"\\"))
            return true;

        // 4. Mapped network drives / DFS
        try
        {
            var root = Path.GetPathRoot(path);
            if (root != null)
            {
                var drive = new DriveInfo(root);
                return drive.DriveType == DriveType.Network;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static long? TryGetRemoteFileSize(string file)
    {
        try
        {
            using var fs = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1,
                FileOptions.Asynchronous);

            return fs.Length;
        }
        catch
        {
            return null;
        }
    }

    // -------------------------
    // Unified cleanup path
    // -------------------------

    // interbalCall: true if called from within the RunAsync method,
    //  false if called externally (e.g., from Dispose)
    private void CleanUp(bool internalCall)
    {
        if (_cleanedUp)
            return;

        try { _cts.Cancel(); } catch { }

        try { _remoteQueue?.CompleteAdding(); } catch { }

        if (!internalCall)
        {
            // Only if external cleanup it's safe to wait! Otherwise risk deadlock!
            try { _remoteConsumer?.Wait(); } catch { }
            try { _worker?.Wait(); } catch { }
        }

        try { _cts.Dispose(); } catch { }

        _cleanedUp = true;
    }

    // -------------------------
    // IDisposable implementation
    // -------------------------

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
            CleanUp(internalCall: false);

        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(FileSizeAggregator));
    }
}
