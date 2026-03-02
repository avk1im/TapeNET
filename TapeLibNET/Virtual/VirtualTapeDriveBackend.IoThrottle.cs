using Microsoft.Extensions.Logging;
using Windows.Win32.System.SystemServices;

namespace TapeLibNET.Virtual;

/// <summary>
/// IO speed and movement throttling for the virtual tape drive backend.
/// Simulates the relatively slow data transfer rate and tape movement times of physical tape drives
/// using a cumulative time-debt approach with high-precision stopwatches.
/// </summary>
public partial class VirtualTapeDriveBackend
{
    #region *** IO Throttle Fields ***

    private long m_ioRateBytesPerSecond;
    private readonly Stopwatch m_ioStopwatch = new();
    private long m_ioDebtMicroseconds;

    /// <summary>Minimum sleep threshold in microseconds to avoid sub-granularity sleeps.</summary>
    private const long IoThrottleSleepThresholdUs = 1000; // 1 ms

    #endregion

    #region *** Movement Throttle Fields ***

    private long m_locateRateBytesPerSecond;
    private long m_searchRateBytesPerSecond;
    private int m_seekOverheadMs;
    private readonly Stopwatch m_seekStopwatch = new();
    private long m_seekDebtMicroseconds;

    #endregion

    #region *** IO Throttle Properties ***

    /// <summary>
    /// Simulated IO speed in bytes per second. 0 = unlimited (no throttling).
    /// Changing this property resets the accumulated time debt.
    /// </summary>
    public long IoRateBytesPerSecond
    {
        get => m_ioRateBytesPerSecond;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "IO rate must be non-negative (0 = unlimited).");

            m_ioRateBytesPerSecond = value;
            ResetIoThrottle();

            m_logger.LogTrace("{Prefix}: IO rate set to {Rate}",
                LogPrefix, value == 0 ? "unlimited" : $"{Helpers.BytesToString(value)}/s");
        }
    }

    /// <summary>
    /// Whether IO throttling is active.
    /// </summary>
    public bool IsIoThrottled => m_ioRateBytesPerSecond > 0;

    #endregion

    #region *** Movement Throttle Properties ***

    /// <summary>
    /// Simulated blind-seek (locate) speed in tape-equivalent bytes per second.
    /// Used for Rewind, SeekToBlock, SeekToEnd. 0 = unlimited.
    /// </summary>
    public long LocateRateBytesPerSecond
    {
        get => m_locateRateBytesPerSecond;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Locate rate must be non-negative (0 = unlimited).");

            m_locateRateBytesPerSecond = value;
            ResetSeekThrottle();
            SyncOdometerEnabled(m_contentMedia);
            SyncOdometerEnabled(m_initiatorMedia);

            m_logger.LogTrace("{Prefix}: Locate rate set to {Rate}",
                LogPrefix, value == 0 ? "unlimited" : $"{Helpers.BytesToString(value)}/s");
        }
    }

    /// <summary>
    /// Simulated mark-scanning (search) speed in tape-equivalent bytes per second.
    /// Used for SpaceFilemarks, SpaceSetmarks. 0 = unlimited.
    /// </summary>
    public long SearchRateBytesPerSecond
    {
        get => m_searchRateBytesPerSecond;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Search rate must be non-negative (0 = unlimited).");

            m_searchRateBytesPerSecond = value;
            ResetSeekThrottle();
            SyncOdometerEnabled(m_contentMedia);
            SyncOdometerEnabled(m_initiatorMedia);

            m_logger.LogTrace("{Prefix}: Search rate set to {Rate}",
                LogPrefix, value == 0 ? "unlimited" : $"{Helpers.BytesToString(value)}/s");
        }
    }

    /// <summary>
    /// Fixed mechanical overhead per movement operation in milliseconds.
    /// Models acceleration, deceleration, and servo settle time. 0 = no overhead.
    /// </summary>
    public int SeekOverheadMs
    {
        get => m_seekOverheadMs;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Seek overhead must be non-negative.");

            m_seekOverheadMs = value;
        }
    }

    /// <summary>
    /// Whether movement throttling is active (either locate or search).
    /// </summary>
    public bool IsMovementThrottled => m_locateRateBytesPerSecond > 0 || m_searchRateBytesPerSecond > 0;

    #endregion

    #region *** IO Throttle Methods ***

    /// <summary>
    /// Resets the IO throttle state (stopwatch and accumulated debt).
    /// Called when the IO rate changes to avoid operating with stale values.
    /// </summary>
    private void ResetIoThrottle()
    {
        m_ioStopwatch.Reset();
        m_ioDebtMicroseconds = 0;
    }

    /// <summary>
    /// Applies IO throttling delay based on the number of bytes transferred.
    /// Uses cumulative time-debt: tracks how much time the drive "should" have spent,
    /// and sleeps when real time falls behind. Self-corrects over multiple calls
    /// even when individual transfers are too small for Sleep granularity.
    /// </summary>
    /// <param name="bytesTransferred">Number of bytes just transferred in the Read/Write operation.</param>
    private void ThrottleIo(int bytesTransferred)
    {
        if (bytesTransferred <= 0 || m_ioRateBytesPerSecond <= 0)
            return;

        // Start stopwatch on first throttled IO
        if (!m_ioStopwatch.IsRunning)
            m_ioStopwatch.Start();

        // Accumulate time debt: how long this transfer should have taken
        //  debt_us = bytes * 1,000,000 / rate
        m_ioDebtMicroseconds += bytesTransferred * 1_000_000L / m_ioRateBytesPerSecond;

        // Check if real time has fallen behind the debt
        long elapsedUs = m_ioStopwatch.ElapsedMicroseconds;
        long behindUs = m_ioDebtMicroseconds - elapsedUs;

        if (behindUs > IoThrottleSleepThresholdUs)
        {
            // Sleep for the deficit (coarse but self-correcting over time)
            int sleepMs = (int)(behindUs / 1000);
            if (sleepMs > 0)
                Thread.Sleep(sleepMs);
        }
    }

    #endregion

    #region *** Movement Throttle Methods ***

    /// <summary>
    /// Resets the seek/movement throttle state.
    /// </summary>
    private void ResetSeekThrottle()
    {
        m_seekStopwatch.Reset();
        m_seekDebtMicroseconds = 0;
    }

    /// <summary>
    /// Applies movement throttling delay based on the tape distance traveled.
    /// Uses the same cumulative time-debt approach as IO throttling.
    /// </summary>
    /// <param name="distanceBytes">Tape-equivalent distance in bytes (from odometer).</param>
    /// <param name="rateBytesPerSecond">Applicable rate (locate or search).</param>
    private void ThrottleMovement(long distanceBytes, long rateBytesPerSecond)
    {
        if (distanceBytes <= 0 || rateBytesPerSecond <= 0)
            return;

        if (!m_seekStopwatch.IsRunning)
            m_seekStopwatch.Start();

        m_seekDebtMicroseconds += distanceBytes * 1_000_000L / rateBytesPerSecond;

        long elapsedUs = m_seekStopwatch.ElapsedMicroseconds;
        long behindUs = m_seekDebtMicroseconds - elapsedUs;

        if (behindUs > IoThrottleSleepThresholdUs)
        {
            int sleepMs = (int)(behindUs / 1000);
            if (sleepMs > 0)
                Thread.Sleep(sleepMs);
        }
    }

    /// <summary>
    /// Reads the odometer from the current media, applies fixed seek overhead,
    /// throttles at the given rate, and resets the odometer.
    /// Convenience method for use in backend positioning operations.
    /// <para>
    /// Pauses the IO stopwatch during movement sleeps to prevent seek time
    /// from being counted as "free" IO elapsed time. Without this, the hundreds
    /// of milliseconds spent in seek sleeps would create IO credit that absorbs
    /// future IO debt, effectively disabling the IO throttle.
    /// </para>
    /// </summary>
    private void ThrottleMovementFromOdometer(long rateBytesPerSecond)
    {
        if (m_currentMedia == null)
            return;

        long distance = m_currentMedia.OdometerBytes;
        if (distance <= 0)
            return;

        // Pause IO stopwatch so that movement sleep time does not
        // accumulate as "free" elapsed IO time (Stop/Start preserves accumulated value)
        bool ioWasRunning = m_ioStopwatch.IsRunning;
        if (ioWasRunning)
            m_ioStopwatch.Stop();

        // Apply fixed mechanical overhead (accel + decel + settle)
        if (m_seekOverheadMs > 0)
            Thread.Sleep(m_seekOverheadMs);

        // Apply distance-based transport time
        if (rateBytesPerSecond > 0)
            ThrottleMovement(distance, rateBytesPerSecond);

        m_currentMedia.ResetOdometer();

        if (ioWasRunning)
            m_ioStopwatch.Start();
    }

    /// <summary>
    /// Enables/disables the odometer on the given media based on current throttle state.
    /// </summary>
    private void SyncOdometerEnabled(VirtualTapeMedia? media)
    {
        if (media != null)
            media.OdometerEnabled = IsMovementThrottled;
    }

    #endregion
}
