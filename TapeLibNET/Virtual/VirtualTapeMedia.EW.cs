namespace TapeLibNET.Virtual;

/// <summary>
/// Early-warning / imprecise-remaining emulation for <see cref="VirtualTapeMedia"/>.
/// <para>
/// The EW zone and reported-remaining model are physical properties of the emulated cartridge, so they
/// live here. Capacity enforcement always uses the TRUE remaining (<see cref="TrueRemaining"/>); only the
/// public <see cref="Remaining"/> figure is passed through the (optionally optimistic) model — mirroring
/// how a real drive over-reports free space while still hitting hard EOM at the true capacity.
/// </para>
/// </summary>
public partial class VirtualTapeMedia
{
    #region *** Early Warning Emulation ***

    /// <summary>
    /// Optional emulation profile. When <see langword="null"/>, the medium behaves exactly as before:
    /// <see cref="Remaining"/> equals <see cref="TrueRemaining"/> and no early warning is ever surfaced.
    /// </summary>
    internal VirtualTapeEwProfile? EwProfile { get; set; }

    /// <summary>
    /// The TRUE bytes still writable before hard EOM (<c>capacity − bytesWritten</c>, floored at zero).
    /// This is the authoritative figure for capacity enforcement, independent of any reporting model.
    /// </summary>
    public long TrueRemaining => System.Math.Max(0L, m_capacity - m_bytesWritten);

    /// <summary>
    /// The remaining figure the emulated driver reports — the (optionally optimistic) model value when an
    /// <see cref="EwProfile"/> is configured, otherwise the exact <see cref="TrueRemaining"/>.
    /// </summary>
    private long ReportedRemaining()
        => EwProfile?.ReportedRemaining(m_bytesWritten, m_capacity) ?? TrueRemaining;

    /// <summary>
    /// Whether the current true position lies within the configured early-warning zone. False when no
    /// profile (or a zero-width zone) is configured. Monotonic: once entered, stays true up to hard EOM.
    /// </summary>
    public bool IsInEarlyWarningZone
        => EwProfile?.IsInEarlyWarningZone(m_bytesWritten, m_capacity) ?? false;

    #endregion
}
