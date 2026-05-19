using TapeLibNET.Services;
using TapeLibNET.Virtual;

namespace TapeLibNET.Remote;

/// <summary>
/// Describes one named (file-backed) temporary virtual volume that exists in the current
/// remote session, as returned by <see cref="RemoteTapeDriveBackend.ListSessionVolumes"/>.
/// In-memory drives are never catalogued and will not appear in this list.
/// </summary>
public sealed record RemoteVirtualVolumeInfo(
    /// <summary>Volume name as passed to <c>CreateTempVirtualAsync</c>, e.g. <c>"MyTemp_vol02"</c>.</summary>
    string Name,
    /// <summary>Descriptor for the server-side temp files backing this volume.</summary>
    VirtualMediaDescriptor Media,
    /// <summary>Drive capabilities that were in effect when the volume was created.</summary>
    VirtualTapeDriveCapabilities Capabilities,
    /// <summary>Effective block size at the time of creation.</summary>
    uint BlockSize,
    /// <summary>Approximate bytes written (updated on <c>Close</c> / <c>InsertMedia</c>).</summary>
    long BytesWritten,
    /// <summary>UTC timestamp when the volume was created on the server.</summary>
    DateTime CreatedUtc,
    /// <summary>True when this is the session's currently active (mounted) volume.</summary>
    bool IsCurrent,
    /// <summary>
    /// True when this is the most-recently-created volume in the session but the session's
    /// active drive is currently in-memory (so the volume is not literally mounted).
    /// Set by the caller when populating UI pickers; never stored server-side.
    /// </summary>
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
