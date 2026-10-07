using TapeLibNET.Virtual;
using TapeLibNET.Services;

namespace TapeLibNET.Remote;


/// <summary>
/// Describes one named (file-backed) temporary virtual volume that exists in the
///  current remote session, as returned by
///  <see cref="RemoteTapeDriveBackend.ListSessionVolumes"/>.
/// In-memory drives are never catalogued and do not appear in this list.
/// </summary>
/// <param name="Name">
/// Volume name as passed to <c>CreateTempVirtualAsync</c>, for example
/// <c>"MyTemp_vol02"</c>.
/// </param>
/// <param name="Media">
/// Descriptor for the server-side temp files backing this volume.
/// </param>
/// <param name="Capabilities">
/// Drive capabilities that were in effect when the volume was created.
/// </param>
/// <param name="BlockSize">
/// Effective block size at the time of creation.
/// </param>
/// <param name="BytesWritten">
/// Approximate number of bytes written. Updated on
/// <c>Close</c> and <c>InsertMedia</c>.
/// </param>
/// <param name="CreatedUtc">
/// UTC timestamp indicating when the volume was created on the server.
/// </param>
/// <param name="IsCurrent">
/// <see langword="true"/> when this is the session's currently mounted volume.
/// </param>
/// <param name="IsLatest">
/// <see langword="true"/> when this is the most recently created volume in the
///  session, but the active drive is currently in-memory and therefore this
///  volume is not actually mounted.
/// <para>This value is set by the caller when populating UI pickers and is never
/// stored server-side.</para>
/// </param>
public sealed record RemoteVirtualVolumeInfo(
    string Name,
    VirtualMediaDescriptor Media,
    VirtualTapeDriveCapabilities Capabilities,
    uint BlockSize,
    long BytesWritten,
    DateTime CreatedUtc,
    bool IsCurrent,
    bool IsLatest = false)
{
    /// <summary>
    /// Display text for UI pickers.
    /// <list type="bullet">
    ///  <item><c>IsCurrent</c>: name only with "(current)" — written size is omitted because
    ///   it may be stale after subsequent backups were added.</item>
    ///  <item><c>IsLatest</c>: name + written size + "(latest)" — accurate because the volume
    ///   was measured when it was last swapped out.</item>
    ///  <item>Otherwise: name + written size.</item>
    /// </list>
    /// </summary>
    public string DisplayText
    {
        get
        {
            if (IsCurrent)
                return $"{Name} (current)";

            var mb     = BytesWritten / (1024.0 * 1024.0);
            var suffix = IsLatest ? " (latest)" : string.Empty;
            return $"{Name} — {mb:N0} MB written{suffix}";
        }
    }
}
