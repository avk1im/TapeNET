
namespace TapeLibNET.Packer;


/// <summary>
/// The TOC entries a buffer write puts on tape, by how far the write gets (Design-TocReserve §4.2): for each file whose
///  first byte lies in or before the buffer, the number of the buffer's blocks that must be written to reach that byte
///  (0 when it began in an earlier buffer) and the file's TOC entry estimate. Immutable; built on the agent thread,
///  read on the writing (sink) thread.
/// </summary>
internal sealed class TocCommitPlan
{
    private readonly int[] m_blocks;            // ascending
    private readonly long[] m_cumulative;       // m_cumulative[i] = Σ entries 0..i

    private TocCommitPlan(int[] blocks, long[] cumulative)
    {
        m_blocks = blocks;
        m_cumulative = cumulative;
    }

    /// <summary>Number of files in the plan.</summary>
    public int Count => m_blocks.Length;

    /// <summary>Total entry bytes of the plan — what a complete write adds.</summary>
    public long TotalTocBytes => m_cumulative.Length == 0 ? 0L : m_cumulative[^1];

    /// <summary>The entry bytes a write of <paramref name="blocks"/> blocks puts on tape: every entry due by then.</summary>
    public long TocBytesFor(int blocks)
    {
        // Upper bound: the first index whose block number exceeds 'blocks'
        int lo = 0, hi = m_blocks.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (m_blocks[mid] <= blocks)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo == 0 ? 0L : m_cumulative[lo - 1];
    }

    /// <summary>Builds a plan; <see langword="null"/> when there is nothing to count.</summary>
    public static TocCommitPlan? Build(List<(int Blocks, long TocBytes)> items)
    {
        if (items.Count == 0)
            return null;
        items.Sort(static (a, b) => a.Blocks.CompareTo(b.Blocks));
        var blocks = new int[items.Count];
        var cumulative = new long[items.Count];
        long sum = 0;
        for (int i = 0; i < items.Count; i++)
        {
            blocks[i] = Math.Max(0, items[i].Blocks);
            sum += Math.Max(0L, items[i].TocBytes);
            cumulative[i] = sum;
        }
        return new TocCommitPlan(blocks, cumulative);
    }
}