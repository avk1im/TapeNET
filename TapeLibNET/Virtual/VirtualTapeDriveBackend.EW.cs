namespace TapeLibNET.Virtual;

/// <summary>
/// Early-warning emulation surface for <see cref="VirtualTapeDriveBackend"/>.
/// <para>
/// The backend owns the emulation profile applied to newly created/loaded media (mirroring how content
/// capacity is applied), and surfaces the physical early-warning signals through the widened
/// <see cref="Write"/> signature. Programmable Early Warning (<c>pew</c>) is a Phase-2 concern and stays a
/// stub here.
/// </para>
/// </summary>
public partial class VirtualTapeDriveBackend
{
    #region *** Early Warning Fields ***

    // Emulation profile to apply to newly created / loaded content media (physical cartridge property).
    private VirtualTapeEwProfile? m_ewProfileForNew;

    // Whether the caller asked the backend to SURFACE the physical early warning (like a real ReportEarlyWarning).
    private bool m_reportEarlyWarning;

    #endregion

    #region *** Early Warning Configuration ***

    /// <summary>
    /// Opt-in emulation profile reproducing an imprecise <see cref="Remaining"/> report and a built-in
    /// early-warning zone. <see langword="null"/> (the default) preserves exact legacy behavior. Assigning
    /// applies the profile to the currently loaded content media as well as any media loaded afterwards.
    /// </summary>
    public VirtualTapeEwProfile? EmulatedEarlyWarning
    {
        get => m_ewProfileForNew;
        set
        {
            m_ewProfileForNew = value;
            ApplyEwProfileToMedia();
        }
    }

    /// <summary>Pushes the configured emulation profile onto the content media (no-op if none loaded).</summary>
    internal void ApplyEwProfileToMedia()
    {
        if (m_contentMedia != null)
            m_contentMedia.EwProfile = m_ewProfileForNew;
    }

    #endregion

    #region *** Early Warning Overrides ***

    /// <inheritdoc/>
    public override bool ReportsExactRemaining => m_ewProfileForNew is null;
    
    /// <inheritdoc/>
    public override EarlyWarningMechanism EarlyWarningMechanism
        => m_ewProfileForNew is { EarlyWarningZone: > 0 }
            ? EarlyWarningMechanism.HardwareEarlyWarning
            : EarlyWarningMechanism.None;

    /// <inheritdoc/>
    public override bool ReportsEarlyWarning
        => m_reportEarlyWarning && m_ewProfileForNew is { EarlyWarningZone: > 0 };

    /// <inheritdoc/>
    public override bool ReportEarlyWarning(bool report)
    {
        m_reportEarlyWarning = report;
        // Honored only when an EW zone is actually being emulated.
        return m_ewProfileForNew is { EarlyWarningZone: > 0 };
    }

    #endregion
}
