#if DEBUG
namespace TapeLibNET;

/// <summary>
/// Reusable DEBUG-only failure simulator. Tracks a call counter and triggers a
/// simulated failure every <see cref="EveryNth"/> invocation when <see cref="Enabled"/>.
/// <para>
/// Shared across agent-level (file backup/restore) and backend-level (I/O, timeout)
/// simulation — each consumer creates its own instance.
/// </para>
/// </summary>
public class FailureSimulator
{
    private bool m_enabled;

    /// <summary>
    /// Enables or disables the simulator. Setting resets <see cref="Counter"/> to 0
    /// so that a fresh simulation starts cleanly.
    /// </summary>
    public bool Enabled
    {
        get => m_enabled;
        set { m_enabled = value; Counter = 0; }
    }

    /// <summary>
    /// Number of calls to <see cref="ShouldFailNow"/> since the last enable/reset.
    /// Can be set externally to offset the failure pattern (e.g., for test scenarios).
    /// </summary>
    public int Counter { get; set; }

    /// <summary>
    /// Failure frequency: every Nth call to <see cref="ShouldFailNow"/> returns true.
    /// Default is 2 (every other call).
    /// </summary>
    public int EveryNth { get; set; } = 2;

    /// <summary>
    /// If <see langword="true"/>, the simulator disables itself after the first simulated failure.
    /// </summary>
    public bool Once { get; set; } = false;

    /// <summary>
    /// Enables the simulator for a single simulated failure on the next call to <see cref="ShouldFailNow"/>.
    /// </summary>
    public void EnableOnce()
    {
        Enabled = true;
        EveryNth = 1;
        Once = true;
    }

    public void EnableAlways(int everyNth = 1)
    {
        Enabled = true;
        EveryNth = everyNth;
        Once = false;
    }

    /// <summary>
    /// Increments <see cref="Counter"/> and returns true when a simulated failure
    /// should occur (<see cref="Enabled"/> and counter is a multiple of <see cref="EveryNth"/>).
    /// </summary>
    public bool ShouldFailNow()
    { 
        bool now = Enabled && ++Counter % EveryNth == 0;
        if (now && Once)
            Enabled = false;
        return now;
    }
}
#endif
