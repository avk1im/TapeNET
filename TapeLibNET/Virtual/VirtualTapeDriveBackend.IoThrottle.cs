using Microsoft.Extensions.Logging;
using Windows.Win32.System.SystemServices;

namespace TapeLibNET.Virtual;

/// <summary>
/// Encapsulates all four IO throttle parameters for a virtual tape drive:
/// streaming IO rate, locate speed, search speed, and seek overhead.
/// Use the static presets for common drive classes, or build a custom instance.
/// </summary>
public readonly record struct VirtualTapeDriveIoRate
{
    /// <summary>Streaming read/write speed in bytes per second. 0 = unlimited.</summary>
    public long BytesPerSecond { get; init; }

    /// <summary>Blind-seek speed in tape-equivalent bytes per second (Rewind, SeekToBlock, SeekToEnd). 0 = unlimited.</summary>
    public long LocateBytesPerSecond { get; init; }

    /// <summary>Mark-scanning speed in tape-equivalent bytes per second (SpaceFilemarks, SpaceSetmarks). 0 = unlimited.</summary>
    public long SearchBytesPerSecond { get; init; }

    /// <summary>Fixed per-operation mechanical overhead in milliseconds (acceleration + deceleration + settle). 0 = none.</summary>
    public int SeekOverheadMs { get; init; }

    /// <summary>Is IO throttling enabled.</summary>
    public bool IsIoThrottled => BytesPerSecond > 0;

    /// <summary>Is movement throttling enabled (either locate or search).</summary>
    public bool IsMovementThrottled => LocateBytesPerSecond > 0 || SearchBytesPerSecond > 0;


    private const long MB = 1024 * 1024;
    private const long GB = 1024L * 1024 * 1024;

    // Fudge factors to provide more realistic speeds for testing/simulation
    private const int FuFa1 = 2;
    private const int FuFa2 = 3;

    /// <summary>Unlimited (no throttling).</summary>
    public static VirtualTapeDriveIoRate Unlimited => new();

    /// <summary>6 MB/s — AIT-1, DAT-160.</summary>
    public static VirtualTapeDriveIoRate Ait1 => new()
    {
        BytesPerSecond        =   6 * MB / FuFa1,
        LocateBytesPerSecond  = 1500 * MB / FuFa2,
        SearchBytesPerSecond  =   36 * MB / FuFa2,
        SeekOverheadMs        =  400 * FuFa2,
    };

    /// <summary>12 MB/s — AIT-3Ex, DLT-V4.</summary>
    public static VirtualTapeDriveIoRate Ait3Ex => new()
    {
        BytesPerSecond        =  12 * MB / FuFa1,
        LocateBytesPerSecond  =   3 * GB / FuFa2,
        SearchBytesPerSecond  =  72 * MB / FuFa2,
        SeekOverheadMs        = 350 * FuFa2,
    };

    /// <summary>24 MB/s — AIT-4/5.</summary>
    public static VirtualTapeDriveIoRate Ait4 => new()
    {
        BytesPerSecond        =  24 * MB / FuFa1,
        LocateBytesPerSecond  =   5 * GB / FuFa2,
        SearchBytesPerSecond  = 144 * MB / FuFa2,
        SeekOverheadMs        = 300 * FuFa2,
    };

    /// <summary>60 MB/s — LTO-3/4.</summary>
    public static VirtualTapeDriveIoRate Lto4 => new()
    {
        BytesPerSecond        =  60 * MB / FuFa1,
        LocateBytesPerSecond  =   8 * GB / FuFa2,
        SearchBytesPerSecond  = 300 * MB / FuFa2,
        SeekOverheadMs        = 250 * FuFa2,
    };

    /// <summary>160 MB/s — LTO-5/6.</summary>
    public static VirtualTapeDriveIoRate Lto6 => new()
    {
        BytesPerSecond        =  160 * MB / FuFa1,
        LocateBytesPerSecond  =   18 * GB / FuFa2,
        SearchBytesPerSecond  =  640 * MB / FuFa2,
        SeekOverheadMs        =  200 * FuFa2,
    };

    /// <summary>400 MB/s — LTO-8/9.</summary>
    public static VirtualTapeDriveIoRate Lto9 => new()
    {
        BytesPerSecond        =  400 * MB / FuFa1,
        LocateBytesPerSecond  =   80 * GB / FuFa2,
        SearchBytesPerSecond  = 1200 * MB / FuFa2,
        SeekOverheadMs        =  150 * FuFa2,
    };
}

/// <summary>
/// IO speed and movement throttling for the virtual tape drive backend.
/// Simulates the relatively slow data transfer rate and tape movement times of physical tape drives
/// using a cumulative time-debt approach with high-precision stopwatches.
/// </summary>
public partial class VirtualTapeDriveBackend
{
    #region *** IO Throttle Fields ***

    private VirtualTapeDriveIoRate m_ioRate;
    private readonly Stopwatch m_ioStopwatch = new();
    private long m_ioDebtMicroseconds;

    /// <summary>Minimum sleep threshold in microseconds to avoid sub-granularity sleeps.</summary>
    private const long c_ioThrottleSleepThresholdUs = 1000; // 1 ms

    #endregion

    #region *** Movement Throttle Fields ***

    private readonly Stopwatch m_seekStopwatch = new();
    private long m_seekDebtMicroseconds;

    #endregion

    #region *** IO Throttle Properties ***

    /// <summary>
    /// Simulated IO rate (streaming speed, locate speed, search speed, seek overhead).
    /// Set to <see cref="VirtualTapeDriveIoRate.Unlimited"/> to disable all throttling.
    /// Changing this property resets the accumulated time debt.
    /// </summary>
    public VirtualTapeDriveIoRate IoRate
    {
        get => m_ioRate;
        set
        {
            m_ioRate = value;
            ResetIoThrottle();
            ResetSeekThrottle();
            SyncOdometerEnabled(m_contentMedia);
            SyncOdometerEnabled(m_initiatorMedia);

            m_logger.LogTrace("{Prefix}: IO rate set — stream: {Stream}, locate: {Locate}, search: {Search}, seek overhead: {Overhead}",
                LogPrefix,
                value.BytesPerSecond == 0 ? "unlimited" : $"{Helpers.BytesToString(value.BytesPerSecond)}/s",
                value.LocateBytesPerSecond == 0 ? "unlimited" : $"{Helpers.BytesToString(value.LocateBytesPerSecond)}/s",
                value.SearchBytesPerSecond == 0 ? "unlimited" : $"{Helpers.BytesToString(value.SearchBytesPerSecond)}/s",
                value.SeekOverheadMs == 0 ? "none" : $"{value.SeekOverheadMs} ms");
        }
    }

    /// <summary>
    /// Whether IO throttling is active.
    /// </summary>
    public bool IsIoThrottled => m_ioRate.IsIoThrottled;

    #endregion

    #region *** Movement Throttle Properties ***

    /// <summary>
    /// Whether movement throttling is active (either locate or search).
    /// </summary>
    public bool IsMovementThrottled => m_ioRate.IsMovementThrottled;

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
        if (bytesTransferred <= 0 || m_ioRate.BytesPerSecond <= 0)
            return;

        // Start stopwatch on first throttled IO
        if (!m_ioStopwatch.IsRunning)
            m_ioStopwatch.Start();

        // Accumulate time debt: how long this transfer should have taken
        //  debt_us = bytes * 1,000,000 / rate
        m_ioDebtMicroseconds += bytesTransferred * 1_000_000L / m_ioRate.BytesPerSecond;

        // Check if real time has fallen behind the debt
        long elapsedUs = m_ioStopwatch.ElapsedMicroseconds;
        long behindUs = m_ioDebtMicroseconds - elapsedUs;

        if (behindUs > c_ioThrottleSleepThresholdUs)
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

        if (behindUs > c_ioThrottleSleepThresholdUs)
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
        if (m_ioRate.SeekOverheadMs > 0)
            Thread.Sleep(m_ioRate.SeekOverheadMs);

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
