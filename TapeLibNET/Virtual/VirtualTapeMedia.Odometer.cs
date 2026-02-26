namespace TapeLibNET.Virtual;

/// <summary>
/// Odometer functionality for VirtualTapeMedia.
/// Accumulates absolute "tape distance" (in equivalent data bytes) during movement operations.
/// The caller (VirtualTapeDriveBackend) can query and reset the odometer to compute
/// throttle delays for simulated seek/rewind/spacing operations.
/// </summary>
public partial class VirtualTapeMedia
{
    #region *** Odometer Constants ***

    /// <summary>
    /// Tape-equivalent length of a single tape mark (filemark or setmark) in bytes.
    /// Real marks are physically ~1-2 inches of tape; we approximate as 1 block size.
    /// Computed as the current default block size at query time via property.
    /// </summary>
    private long TapeLengthOfMark => m_defaultBlockSize;

    #endregion

    #region *** Odometer Fields ***

    private bool m_odometerEnabled;
    private long m_odometerBytes;

    #endregion

    #region *** Odometer Properties ***

    /// <summary>
    /// Enables or disables the odometer. When disabled, movement operations
    /// do not accumulate distance (zero overhead).
    /// </summary>
    public bool OdometerEnabled
    {
        get => m_odometerEnabled;
        set => m_odometerEnabled = value;
    }

    /// <summary>
    /// Accumulated absolute tape distance in equivalent data bytes since last reset.
    /// Always non-negative (forward and backward movement both add to the total).
    /// </summary>
    public long OdometerBytes => m_odometerBytes;

    #endregion

    #region *** Odometer Methods ***

    /// <summary>
    /// Resets the odometer to zero. Call before a movement operation to measure its distance.
    /// </summary>
    public void ResetOdometer()
    {
        m_odometerBytes = 0;
    }

    /// <summary>
    /// Accumulates the tape distance between two logical block positions.
    /// Computes the sum of tape-equivalent lengths of all virtual blocks (or fractions thereof)
    /// between <paramref name="fromBlock"/> and <paramref name="toBlock"/>.
    /// Direction-agnostic: always adds absolute distance.
    /// </summary>
    private void AccumulateOdometer(long fromBlock, long toBlock)
    {
        if (!m_odometerEnabled || fromBlock == toBlock)
            return;

        // Ensure from < to for the scan
        long lo = Math.Min(fromBlock, toBlock);
        long hi = Math.Max(fromBlock, toBlock);

        long distance = 0;

        // Find the first virtual block that overlaps with [lo, hi)
        int startIdx = FindVirtualBlockIndex(lo);

        for (int i = startIdx; i < m_virtualBlocks.Count; i++)
        {
            var vb = m_virtualBlocks[i];

            // Past the range - done
            if (vb.BeginAtBlock >= hi)
                break;

            if (vb.IsMark)
            {
                // Mark is within range if its block position is in [lo, hi)
                if (vb.BeginAtBlock >= lo && vb.BeginAtBlock < hi)
                    distance += TapeLengthOfMark;
            }
            else
            {
                // Data block: compute overlap with [lo, hi) in logical blocks
                long overlapStart = Math.Max(lo, vb.BeginAtBlock);
                long overlapEnd = Math.Min(hi, vb.EndBlock);

                if (overlapEnd > overlapStart)
                    distance += (overlapEnd - overlapStart) * vb.BlockSize;
            }
        }

        // If we're past all virtual blocks but still have range (empty tape region),
        // estimate using default block size
        long lastDataEnd = m_virtualBlocks.Count > 0 ? m_virtualBlocks[^1].EndBlock : 0;
        if (hi > lastDataEnd && lo < hi)
        {
            long emptyStart = Math.Max(lo, lastDataEnd);
            long emptyBlocks = hi - emptyStart;
            distance += emptyBlocks * m_defaultBlockSize;
        }

        m_odometerBytes += distance;
    }

    #endregion
}
