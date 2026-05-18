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
    bool IsCurrent)
{
    /// <summary>Display text for UI pickers, e.g. "MyTemp_vol02 — 124 MB written (current)".</summary>
    public string DisplayText
    {
        get
        {
            var mb = BytesWritten / (1024.0 * 1024.0);
            var current = IsCurrent ? " (current)" : string.Empty;
            return $"{Name} — {mb:N0} MB written{current}";
        }
    }
}
