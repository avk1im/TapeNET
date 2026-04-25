namespace TapeLibNET.Services;

/// <summary>
/// Information required to create or open a virtual media (file-backed or in-memory).
/// </summary>
public record VirtualMediaDescriptor(
    string ContentPath,
    long ContentCapacity,
    string? InitiatorPath,
    long InitiatorPartitionCapacity,
    bool InMemory = false);
