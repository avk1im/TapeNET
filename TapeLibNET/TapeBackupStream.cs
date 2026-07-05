using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace TapeLibNET;

/// <summary>
/// Abstract base for <see cref="TapeBackupSourceStream"/> and <see cref="TapeBackupTargetStream"/>.
/// <para>Both subclasses are pure pass-throughs: Win32 BackupRead/BackupWrite handle all
///  WIN32_STREAM_ID framing internally. The caller sees an opaque byte blob.</para>
/// </summary>
internal abstract class TapeBackupStreamBase(SafeFileHandle handle, ILogger logger) : Stream
{
    protected readonly SafeFileHandle _handle = handle ?? throw new ArgumentNullException(nameof(handle));
    protected readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    protected unsafe void* _context = null;
    protected bool _disposed = false;
    protected long _position = 0L;

    public override bool CanSeek => false;
    public override long Length => _position;
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException("TapeBackupStream does not support seeking.");
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("TapeBackupStream does not support seeking.");

    public override void SetLength(long value) =>
        throw new NotSupportedException("TapeBackupStream does not support SetLength.");

    public override void Flush()
    {
        // No-op; BackupRead/BackupWrite do not require explicit flush
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                // Release the BackupRead/BackupWrite context with bAbort=TRUE
                unsafe
                {
                    if (_context != null)
                    {
                        try
                        {
                            ReleaseContext();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Exception while releasing backup context during dispose");
                        }
                    }
                }

                _handle?.Dispose();
            }

            _disposed = true;
        }

        base.Dispose(disposing);
    }

    /// <summary>Calls BackupRead or BackupWrite with bAbort=TRUE to release the Win32 context.</summary>
    protected abstract void ReleaseContext();

    /// <summary>Returns a raw HANDLE suitable for passing to BackupRead/BackupWrite.</summary>
    protected HANDLE RawHandle => (HANDLE)_handle.DangerousGetHandle();

    /// <summary>
    /// Converts a normal Win32 path into an extended-length NT path (\\?\ or \\?\UNC\).
    /// <para>This allows CreateFileW to open paths longer than MAX_PATH.</para>
    /// </summary>
    public static string NormalizeFullPathWin32(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        // 1. Already NT-style?
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path;

        // 2. UNC path?
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // \\server\share → \\?\UNC\server\share
            return @"\\?\UNC\" + path[2..];
        }

        // 3. Absolute local path? (C:\...)
        if (IsAbsoluteLocalPath(path))
        {
            return @"\\?\" + path;
        }

        // 4. Relative path → expand first
        string fullPath = Path.GetFullPath(path);

        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + fullPath[2..];

        return @"\\?\" + fullPath;
    }

    private static bool IsAbsoluteLocalPath(string path)
    {
        return path.Length >= 3 &&
               char.IsLetter(path[0]) &&
               path[1] == ':' &&
               path[2] == '\\';
    }

}

/// <summary>
/// Read-only stream wrapper over Win32 BackupRead. Opens a file with FILE_FLAG_BACKUP_SEMANTICS
///  and reads all NTFS streams (main data, DACL, ADS, EA, reparse, etc.) as an opaque byte blob.
/// <para>This is a pure pass-through: BackupRead writes its full framed output (WIN32_STREAM_ID
///  headers + data) directly into the caller-supplied buffer. The caller should treat the bytes as
///  opaque and feed them verbatim into <see cref="TapeBackupTargetStream.Write"/> during restore.</para>
/// </summary>
internal sealed class TapeBackupSourceStream : TapeBackupStreamBase
{
    private TapeBackupSourceStream(SafeFileHandle handle, ILogger logger)
        : base(handle, logger)
    {
    }

    /// <summary>
    /// Opens a file for backup reading with FILE_FLAG_BACKUP_SEMANTICS and required security access.
    /// </summary>
    public static TapeBackupSourceStream Open(FileInfo fileInfo, ILogger logger)
    {
        var handle = PInvoke.CreateFile(
            NormalizeFullPathWin32(fileInfo.FullName),
            (uint)FILE_ACCESS_RIGHTS.FILE_GENERIC_READ | (uint)FILE_ACCESS_RIGHTS.READ_CONTROL,
            // Note: ACCESS_SYSTEM_SECURITY (0x01000000) is intentionally omitted — it requires SeSecurityPrivilege
            //  which is not available in unprivileged processes. SACL will simply be absent from the blob.
            //  DACL is covered by READ_CONTROL.
            FILE_SHARE_MODE.FILE_SHARE_READ,
            null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
            null);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            throw new TapeIOException((uint)error, $"Failed to open file for backup reading: {fileInfo.FullName}");
        }

        return new TapeBackupSourceStream(handle, logger);
    }

    public override bool CanRead => true;
    public override bool CanWrite => false;

    /// <summary>
    /// Reads raw BackupRead output bytes (WIN32_STREAM_ID-framed blob) into the buffer.
    ///  Returns 0 at end-of-backup (BackupRead returns 0 bytes with bAbort=FALSE).
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + count, buffer.Length);

        if (count == 0)
            return 0;

        uint bytesRead = 0;

        unsafe
        {
            fixed (byte* pBuffer = &buffer[offset])
            {
                void* ctx = _context;
                BOOL success = PInvoke.BackupRead(
                    RawHandle,
                    pBuffer,
                    (uint)count,
                    &bytesRead,
                    false,  // bAbort
                    false,  // bProcessSecurity=false — consistent across all calls for this handle's context
                    &ctx);
                _context = ctx;

                if (!success)
                {
                    var error = (uint)Marshal.GetLastWin32Error();
                    throw new TapeIOException(error, "BackupRead failed");
                }
            }
        }

        _position += bytesRead;
        return (int)bytesRead; // 0 signals end-of-backup to the caller
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("TapeBackupSourceStream is read-only.");

    protected override void ReleaseContext()
    {
        unsafe
        {
            if (_context != null)
            {
                uint dummy = 0;
                void* ctx = _context;
                PInvoke.BackupRead(RawHandle, null, 0, &dummy, true, false, &ctx); // bAbort=true frees the context
                _context = null;
            }
        }
    }
}

/// <summary>
/// Write-only stream wrapper over Win32 BackupWrite. Creates a file with FILE_FLAG_BACKUP_SEMANTICS
///  and feeds all incoming bytes directly into BackupWrite.
/// <para>This is a pure pass-through: the caller must supply the opaque blob produced by
///  <see cref="TapeBackupSourceStream.Read"/> (WIN32_STREAM_ID-framed bytes). BackupWrite
///  interprets the framing and restores all NTFS streams it is permitted to write.</para>
/// </summary>
internal sealed class TapeBackupTargetStream : TapeBackupStreamBase
{
    private TapeBackupTargetStream(SafeFileHandle handle, ILogger logger)
        : base(handle, logger)
    {
    }

    /// <summary>
    /// Creates (or truncates) a file for backup writing with FILE_FLAG_BACKUP_SEMANTICS.
    ///  The parent directory is created if it does not already exist.
    /// </summary>
    public static TapeBackupTargetStream Create(FileInfo fileInfo, ILogger logger)
    {
        // Ensure the parent directory exists before BackupWrite tries to restore into it
        fileInfo.Directory?.Create();

        var handle = PInvoke.CreateFile(
            NormalizeFullPathWin32(fileInfo.FullName),
            (uint)FILE_ACCESS_RIGHTS.FILE_GENERIC_WRITE | (uint)FILE_ACCESS_RIGHTS.WRITE_DAC,
            // Note: WRITE_OWNER (requires SeRestorePrivilege) and ACCESS_SYSTEM_SECURITY (requires
            //  SeSecurityPrivilege) are intentionally omitted - not available in unprivileged processes.
            //  Ownership and SACL will not be restored; DACL restoration is covered by WRITE_DAC.
            FILE_SHARE_MODE.FILE_SHARE_NONE,
            null,
            FILE_CREATION_DISPOSITION.CREATE_ALWAYS,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
            null);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            throw new TapeIOException((uint)error, $"Failed to create file for backup writing: {fileInfo.FullName}");
        }

        return new TapeBackupTargetStream(handle, logger);
    }

    public override bool CanRead => false;
    public override bool CanWrite => true;

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("TapeBackupTargetStream is write-only.");

    /// <summary>
    /// Writes raw BackupRead bytes (WIN32_STREAM_ID-framed blob) to the file via BackupWrite.
    ///  BackupWrite interprets the framing and restores main data, DACL, ADS, etc. as permitted.
    /// </summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + count, buffer.Length);

        if (count == 0)
            return;

        uint bytesWritten = 0;

        unsafe
        {
            fixed (byte* pBuffer = &buffer[offset])
            {
                void* ctx = _context;
                BOOL success = PInvoke.BackupWrite(
                    RawHandle,
                    pBuffer,
                    (uint)count,
                    &bytesWritten,
                    false, // bAbort
                    false, // bProcessSecurity=false - consistent with source side; security streams are absent
                    &ctx);
                _context = ctx;

                if (!success || bytesWritten != count)
                {
                    var error = (uint)Marshal.GetLastWin32Error();
                    _logger.LogTrace("BackupWrite failed: error={Error}, requested={Count}, written={Written}",
                        error, count, bytesWritten);
                    throw new TapeIOException(error,
                        $"BackupWrite failed: requested={count}, written={bytesWritten}");
                }
            }
        }

        _position += bytesWritten;
    }

    protected override void ReleaseContext()
    {
        unsafe
        {
            if (_context != null)
            {
                uint dummy = 0;
                void* ctx = _context;
                PInvoke.BackupWrite(RawHandle, null, 0, &dummy, true, false, &ctx); // bAbort=true frees the context
                _context = null;
            }
        }
    }
}
