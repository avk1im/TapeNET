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
    /// The TRUE bytes still writable before hard EOM, EOD-based (<c>capacity − m_bytesWritten</c>), floored
    /// at zero. This is the OFFICIAL figure the backend/TapeDrive consume — matching real LTO hardware,
    /// whose Remaining is <c>capacity − end-of-data</c> and does NOT shrink to a head-relative value after a
    /// backward seek. Capacity enforcement stays correct because <see cref="WriteBlocks"/>/<see cref="WriteMark"/>
    /// truncate from the current position FIRST, after which m_bytesWritten equals the head position anyway.
    /// </summary>
    public long TrueRemaining => Math.Max(0L, m_capacity - m_bytesWritten);

    /// <summary>
    /// DEBUG/diagnostic only: what a (hypothetical) head-position-based drive WOULD report — i.e.
    /// <c>capacity − current_position_bytes</c>. Diverges from <see cref="TrueRemaining"/> exactly after a
    /// backward seek. Retained to reason about the difference; NOT used by the backend.
    /// </summary>
    public long TrueRemainingFromCurrentPosition => Math.Max(0L, m_capacity - CurrentPositionBytes());

    /// <summary>
    /// The remaining figure the emulated driver reports — the (optionally optimistic) model value when an
    /// <see cref="EwProfile"/> is configured, otherwise the exact EOD-based <see cref="TrueRemaining"/>.
    /// EOD-based (feeds <c>m_bytesWritten</c> as the "actual written" axis) to match real hardware.
    /// </summary>
    private long ReportedRemaining()
        => EwProfile?.ReportedRemaining(m_bytesWritten, m_capacity) ?? TrueRemaining;

    /// <summary>
    /// Whether the current HEAD POSITION lies within the configured early-warning zone. Position-based (NOT
    /// EOD-based): the drive's physical EW tracks where the head is, so after a backward seek out of the tail
    /// this reads false, and true again once the head re-enters — even though <see cref="TrueRemaining"/>
    /// (EOD-based) may still read tiny. That divergence is faithful to real hardware.
    /// </summary>
    public bool IsInEarlyWarningZone
        => EwProfile?.IsInEarlyWarningZone(CurrentPositionBytes(), m_capacity) ?? false;

    #endregion
}
