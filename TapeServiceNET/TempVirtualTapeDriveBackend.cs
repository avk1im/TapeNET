using TapeLibNET.Virtual;
using Microsoft.Extensions.Logging;

namespace TapeServiceNET;

/// <summary>
/// Wraps a file-backed <see cref="VirtualTapeDriveBackend"/> and deletes the backing
/// temp files (content data + metadata) when the backend is disposed.
/// <para>
/// Used by <see cref="TapeDriveGrpcService.CreateTempVirtual"/> for named temporary
/// drives so that the server's temp folder is cleaned up automatically on session close
/// (or idle-timeout reap).
/// </para>
/// </summary>
internal sealed class TempVirtualTapeDriveBackend(
    VirtualTapeDriveBackend inner,
    string contentFilePath,
    string? initiatorFilePath) : TapeLibNET.TapeDriveBackend(inner.LoggerFactory)
{
    private readonly VirtualTapeDriveBackend _inner = inner;
    private readonly string _contentFilePath = contentFilePath;
    private readonly string? _initiatorFilePath = initiatorFilePath;

    // ── Forward all backend members to the inner backend ────────────────────

    public override bool IsOpen => _inner.IsOpen;
    public override bool HasMedia => _inner.HasMedia;
    public override string DeviceName => _inner.DeviceName;
    public override uint DriveNumber => _inner.DriveNumber;
    public override string Vendor => _inner.Vendor;
    public override string Product => _inner.Product;
    public override uint BlockSize => _inner.BlockSize;
    public override uint MinBlockSize => _inner.MinBlockSize;
    public override uint MaxBlockSize => _inner.MaxBlockSize;
    public override uint DefaultBlockSize => _inner.DefaultBlockSize;
    public override long Capacity => _inner.Capacity;
    public override long Remaining => _inner.Remaining;
    public override long Position => _inner.Position;
    public override bool SupportsInitiatorPartition => _inner.SupportsInitiatorPartition;
    public override bool HasInitiatorPartition => _inner.HasInitiatorPartition;
    public override bool SupportsSetmarks => _inner.SupportsSetmarks;
    public override bool SupportsSeqFilemarks => _inner.SupportsSeqFilemarks;

    public override bool Open(uint driveNumber) => _inner.Open(driveNumber);
    public override void Close() => _inner.Close();
    public override bool SetDriveParameters(bool compression, bool ecc, bool dataPadding,
        bool reportSetmarks, uint eotWarningZoneSize)
        => _inner.SetDriveParameters(compression, ecc, dataPadding, reportSetmarks, eotWarningZoneSize);

    public override bool LoadMedia() => _inner.LoadMedia();
    public override bool UnloadMedia() => _inner.UnloadMedia();
    public override bool SetBlockSize(uint size) => _inner.SetBlockSize(size);
    public override bool FormatMedia(long initiatorPartitionSize = -1) => _inner.FormatMedia(initiatorPartitionSize);

    public override int Read(byte[] buffer, int offset, int count, out bool tapemark, out bool eof)
        => _inner.Read(buffer, offset, count, out tapemark, out eof);
    public override int Write(byte[] buffer, int offset, int count, out bool tapemark, out bool eof)
        => _inner.Write(buffer, offset, count, out tapemark, out eof);

    public override bool SetPosition(long block) => _inner.SetPosition(block);
    public override bool SetPositionToPartition(TapeLibNET.MediaPartition partition, long block)
        => _inner.SetPositionToPartition(partition, block);
    public override long GetPosition() => _inner.GetPosition();
    public override TapeLibNET.MediaPartition GetCurrentPartition() => _inner.GetCurrentPartition();
    public override bool Rewind() => _inner.Rewind();
    public override bool SeekToEnd(TapeLibNET.MediaPartition partition) => _inner.SeekToEnd(partition);
    public override bool SpaceFilemarks(int count) => _inner.SpaceFilemarks(count);
    public override bool SpaceSetmarks(int count) => _inner.SpaceSetmarks(count);
    public override bool SpaceSequentialFilemarks(int count) => _inner.SpaceSequentialFilemarks(count);

    public override bool WriteFilemarks(uint count) => _inner.WriteFilemarks(count);
    public override bool WriteSetmarks(uint count) => _inner.WriteSetmarks(count);

    public override void FillDriveCapabilities(out TapeLibNET.DriveCapabilities parameters)
        => _inner.FillDriveCapabilities(out parameters);
    public override void FillMediaParameters(out TapeLibNET.MediaParameters parameters)
        => _inner.FillMediaParameters(out parameters);

    // ── Dispose: close inner backend then delete temp files ─────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            DeleteTempFiles();
        }
        base.Dispose(disposing);
    }

    private void DeleteTempFiles()
    {
        TryDelete(_contentFilePath);
        TryDelete(_contentFilePath + VirtualTapeDriveBackend.MetadataExtension);

        if (_initiatorFilePath != null)
        {
            TryDelete(_initiatorFilePath);
            TryDelete(_initiatorFilePath + VirtualTapeDriveBackend.MetadataExtension);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            // Non-fatal — log and continue; the OS temp folder will eventually purge stale files.
            m_logger.LogWarning(ex, "Could not delete temp virtual drive file: {Path}", path);
        }
    }
}
