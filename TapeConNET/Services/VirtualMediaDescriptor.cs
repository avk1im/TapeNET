namespace TapeConNET.Services;

/// <summary>
/// Information to create or open a virtual media (file-backed or in-memory).
/// Mirrors the WPF <c>TapeWinNET.Models.VirtualMediaDescriptor</c> exactly so
/// the same shape is used across both apps.
/// </summary>
public record VirtualMediaDescriptor(
    string ContentPath,
    long ContentCapacity,
    string? InitiatorPath,
    long InitiatorPartitionCapacity,
    bool InMemory = false);
