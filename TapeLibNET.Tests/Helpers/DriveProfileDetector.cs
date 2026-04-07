using TapeLibNET;

namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// Maps physical <see cref="DriveCapabilities"/> to one or more <see cref="DriveProfile"/>
/// values, so the physical test suite can automatically select the correct virtual-equivalent
/// profile(s) for any connected tape drive.
/// </summary>
public static class DriveProfileDetector
{
    /// <summary>
    /// Detects all applicable <see cref="DriveProfile"/> values for the given capabilities.
    /// A drive may support multiple profiles (e.g., an AIT drive supports both
    /// <see cref="DriveProfile.Setmarks"/> and <see cref="DriveProfile.Partitions"/>).
    /// </summary>
    /// <param name="caps">Drive capabilities queried from <see cref="TapeDriveBackend.FillDriveCapabilities"/>.</param>
    /// <returns>
    /// List of applicable profiles, ordered from most specific to least.
    /// Empty if the drive doesn't match any known profile.
    /// </returns>
    public static List<DriveProfile> Detect(in DriveCapabilities caps)
    {
        var profiles = new List<DriveProfile>(3);

        // Partitions profile: setmarks + initiator partition (AIT-style)
        if (caps.SupportsSetmarks && caps.SupportsInitiatorPartition)
            profiles.Add(DriveProfile.Partitions);

        // Setmarks profile: setmarks without requiring partitions (AIT, DAT-style)
        if (caps.SupportsSetmarks)
            profiles.Add(DriveProfile.Setmarks);

        // SeqFilemarks profile: sequential filemarks (DLT/SDLT-style)
        if (caps.SupportsSeqFilemarks)
            profiles.Add(DriveProfile.SeqFilemarks);

        return profiles;
    }

    /// <summary>
    /// Returns a human-readable summary of the drive's capabilities for test output.
    /// </summary>
    public static string Describe(in DriveCapabilities caps)
    {
        var features = new List<string>(6);

        if (caps.SupportsSetmarks) features.Add("Setmarks");
        if (caps.SupportsSeqFilemarks) features.Add("SeqFilemarks");
        if (caps.SupportsInitiatorPartition) features.Add("InitiatorPartition");
        if (caps.SupportsCompression) features.Add("Compression");
        if (caps.SupportsEcc) features.Add("ECC");
        if (caps.SupportsPadding) features.Add("Padding");

        string featureList = features.Count > 0 ? string.Join(", ", features) : "none";
        return $"Block: {caps.MinimumBlockSize}–{caps.MaximumBlockSize} (default {caps.DefaultBlockSize}), " +
               $"Features: [{featureList}]";
    }
}
