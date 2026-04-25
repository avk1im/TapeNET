using TapeLibNET.Virtual;

namespace TapeLibNET.Services;

/// <summary>
/// Result of probing a virtual drive for existing media.
/// Stateless: obtained by opening the backend, reading info, then disposing.
/// </summary>
public record VirtualDriveProbeResult(
    bool Success,
    VirtualMediaDescriptor? Media,
    string? MediaName,
    int? BackupSetCount,
    VirtualTapeDriveCapabilities? DetectedCapabilities,
    string? ErrorMessage);
