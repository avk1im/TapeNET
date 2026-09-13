using Windows.Win32.Foundation;

namespace TapeLibNET.Virtual;

#if DEBUG

/// <summary>
/// How an injected fault manifests at the medium.
/// </summary>
public enum VirtualFaultMode
{
    /// <summary>
    /// The operation fails outright: nothing transfers, the head does not move, the error is reported.
    ///  The medium is left exactly as it was.
    /// </summary>
    Fail,

    /// <summary>
    /// A prefix of the request succeeds through the NORMAL path — fully written, fully described,
    ///  head advanced by what survived — and then the operation reports the fault.
    /// </summary>
    /// <remarks>
    /// Models a drive that transfers some blocks and errors on a later one. The caller is correctly
    ///  informed: the returned byte count says exactly how far the medium got. The corruption knobs
    ///  (<see cref="VirtualMediaFaultInjector.CorruptBits"/> and friends) do NOT apply here — the prefix
    ///  that lands is verbatim. Use <see cref="Corrupt"/> when the bytes themselves must be wrong.
    /// </remarks>
    Partial,

    /// <summary>
    /// <b>Write only.</b> A full block IS committed and reads back, but its content is incomplete — so it
    ///  fails framing/CRC — while the caller is told the write failed.
    /// </summary>
    /// <remarks>
    /// The hazard is the divergence, not debris: the medium advanced and the library believes it did not.
    ///  Models SCSI deferred-error semantics, where a buffered write is acknowledged and the media fault
    ///  surfaces on a later command. Mapped to <see cref="Fail"/> on the read side, where nothing is
    ///  committed and the concept has no referent.
    /// </remarks>
    Torn,

    /// <summary>
    /// The operation SUCCEEDS in every observable way — full byte count, no error, head advanced — but a
    ///  few bits of the data are wrong.
    /// </summary>
    /// <remarks>
    /// The most realistic mode of the four. Models host-path corruption (RAM, HBA, driver, cable) landing
    ///  BEFORE the drive computes ECC: the drive then faithfully stores the wrong bytes with valid ECC and
    ///  reads them back flawlessly forever. Nothing below the application can detect it — which is exactly
    ///  what an application-level CRC exists for, and what this mode finally exercises end to end.
    /// </remarks>
    Corrupt,
}

/// <summary>
/// Injects deterministic block-level I/O faults into a <see cref="VirtualTapeMedia"/>, so recovery paths
///  can be exercised against a REAL error rather than a rejected request.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the established <c>FailureSimulator</c> / <c>SimulateTOCFailureMask</c> pattern: instance-level
///  (so parallel tests never interfere), <c>Enabled</c>/<c>EveryNth</c>/<c>Counter</c> shaped, and
///  <c>#if DEBUG</c>-only so it can never reach Release.
/// </para>
/// <para>
/// Owned by <see cref="VirtualTapeDriveBackend"/> and shared BY REFERENCE with the medium, so settings
///  survive <c>LoadMedia()</c> — which constructs a fresh <see cref="VirtualTapeMedia"/> on every volume
///  swap. Configure once, before or after loading; it takes effect either way.
/// </para>
/// <para>
/// <b>Everything here is deterministic.</b> Bit positions come from a seeded
///  <see cref="Random"/> (<see cref="CorruptSeed"/>), never from ambient randomness — an unreproducible
///  failing test is worse than no test at all.
/// </para>
/// </remarks>
public sealed class VirtualMediaFaultInjector
{
    #region *** Scheduling ***

    /// <summary>Master switch. Nothing is injected while false.</summary>
    public bool Enabled { get; set; }

    /// <summary>What the injected fault does — see <see cref="VirtualFaultMode"/>.</summary>
    public VirtualFaultMode Mode { get; set; } = VirtualFaultMode.Fail;

    /// <summary>Fire on every Nth qualifying operation. 1 = every operation.</summary>
    public int EveryNth { get; set; } = 1;

    /// <summary>
    /// Operations counted so far. Pre-seed it to shift the firing phase — e.g.
    ///  <c>Counter = EveryNth - 1</c> makes the very NEXT operation fault.
    /// </summary>
    public int Counter { get; set; }

    /// <summary>
    /// Disarm automatically after firing once, so the operation that follows succeeds. The usual choice
    ///  when the point of the test is what happens AFTER the fault.
    /// </summary>
    public bool AutoDisable { get; set; }

    /// <summary>The error the medium reports. Ignored by <see cref="VirtualFaultMode.Corrupt"/>, which reports none.</summary>
    public uint Error { get => (uint)ErrorWin32; set => ErrorWin32 = (WIN32_ERROR)value; }
    internal WIN32_ERROR ErrorWin32 { get; set; } = WIN32_ERROR.ERROR_WRITE_FAULT;

    /// <summary>How many times a fault has actually fired — assert on this to prove injection happened.</summary>
    public int Occurrences { get; private set; }

    #endregion

    #region *** Mode-specific knobs ***

    /// <summary>
    /// <see cref="VirtualFaultMode.Partial"/>: how many complete blocks succeed before the fault.
    ///  Zero degenerates to <see cref="VirtualFaultMode.Fail"/>.
    /// </summary>
    public int PartialBlocks { get; set; }

    /// <summary>
    /// <see cref="VirtualFaultMode.Torn"/>: how many bytes of the committed block hold real data, the rest
    ///  being padding. Negative means half the block.
    /// </summary>
    public int TornBytes { get; set; } = -1;

    /// <summary>
    /// <see cref="VirtualFaultMode.Corrupt"/>: how many bits to flip. Two is plenty — a single flip is
    ///  equally undetectable below the application, and a handful proves nothing more.
    /// </summary>
    public int CorruptBits { get; set; } = 2;

    /// <summary>
    /// <see cref="VirtualFaultMode.Corrupt"/>: the leading byte span within which bits may flip.
    ///  Zero (default) means the first block.
    /// </summary>
    /// <remarks>
    /// Confining flips to the FRONT matters: on a framed record the signature, preamble and body all sit
    ///  at the head of the block while the tail is zero padding, so a flip landing late would corrupt
    ///  nothing any reader inspects — and the test would pass for the wrong reason.
    /// </remarks>
    public int CorruptSpan { get; set; }

    /// <summary>
    /// <see cref="VirtualFaultMode.Corrupt"/>: exact byte offset of the first flip, or −1 (default) to
    ///  place flips pseudo-randomly within <see cref="CorruptSpan"/>. Set it to target a specific field —
    ///  the CRC, a length prefix, the signature.
    /// </summary>
    public int CorruptOffset { get; set; } = -1;

    /// <summary>
    /// Seed for flip placement. Matches <c>TempFileTree.DefaultSeed</c> so the whole suite shares one
    ///  reproducibility convention.
    /// </summary>
    public int CorruptSeed { get; set; } = 42;

    #endregion

    #region *** Firing ***

    /// <summary>
    /// Advances the counter and reports whether this operation should be faulted. Call exactly once per
    ///  qualifying operation.
    /// </summary>
    /// <remarks>
    /// Named "inject" rather than "fail" deliberately: <see cref="VirtualFaultMode.Corrupt"/> injects
    ///  without failing anything.
    /// </remarks>
    public bool ShouldInjectNow()
    {
        if (!Enabled)
            return false;

        Counter++;
        if (EveryNth <= 0 || Counter % EveryNth != 0)
            return false;

        Occurrences++;
        if (AutoDisable)
            Enabled = false;

        return true;
    }

    /// <summary>
    /// Flips <see cref="CorruptBits"/> bits in <paramref name="data"/>, in place and deterministically.
    ///  Lives here rather than in the medium because the injector owns every knob it reads.
    /// </summary>
    /// <param name="data">A PRIVATE copy of the caller's buffer — never the caller's own array.</param>
    /// <param name="blockSize">Current logical block size, the default span.</param>
    /// <returns>The byte offsets actually flipped, for diagnostics.</returns>
    public IReadOnlyList<int> ApplyCorruption(byte[] data, int blockSize)
    {
        if (data.Length == 0 || CorruptBits <= 0)
            return [];

        int span = CorruptSpan > 0 ? CorruptSpan : blockSize;
        span = Math.Clamp(span, 1, data.Length);

        var rng = new Random(CorruptSeed);
        var flipped = new List<int>(CorruptBits);

        for (int i = 0; i < CorruptBits; i++)
        {
            int at = CorruptOffset >= 0
                ? Math.Min(CorruptOffset + i, data.Length - 1)   // exact placement, walking forward
                : rng.Next(span);                                // anywhere in the front region

            data[at] ^= (byte)(1 << rng.Next(8));
            flipped.Add(at);
        }

        return flipped;
    }

    #endregion

    #region *** One-line arrangements ***

    /// <summary>Fails the next operation outright, then disarms.</summary>
    public VirtualMediaFaultInjector FailOnce(uint? error = null)
        => Arm(VirtualFaultMode.Fail, error.HasValue ? (WIN32_ERROR)error.Value : (WIN32_ERROR?)null);

    /// <summary>Lets <paramref name="blocks"/> complete blocks through, then faults — and disarms.</summary>
    public VirtualMediaFaultInjector PartialOnce(int blocks, uint? error = null)
    {
        PartialBlocks = blocks;
        return Arm(VirtualFaultMode.Partial, error.HasValue ? (WIN32_ERROR)error.Value : (WIN32_ERROR?)null);
    }

    /// <summary>
    /// Tears the next write — a full block is committed with only <paramref name="bytes"/> of real data,
    ///  while the caller is told the write failed — then disarms.
    /// </summary>
    public VirtualMediaFaultInjector TearOnce(int bytes = -1, uint? error = null)
    {
        TornBytes = bytes;
        return Arm(VirtualFaultMode.Torn, error.HasValue ? (WIN32_ERROR)error.Value : (WIN32_ERROR?)null);
    }

    /// <summary>
    /// Corrupts the next operation silently — full success reported, <paramref name="bits"/> bits wrong —
    ///  then disarms.
    /// </summary>
    /// <param name="bits">How many bits to flip.</param>
    /// <param name="offset">Exact byte offset of the first flip, or −1 for pseudo-random placement.</param>
    /// <param name="span">Leading byte span for random placement; 0 means the first block.</param>
    public VirtualMediaFaultInjector CorruptOnce(int bits = 2, int offset = -1, int span = 0)
    {
        CorruptBits = bits;
        CorruptOffset = offset;
        CorruptSpan = span;
        return Arm(VirtualFaultMode.Corrupt, error: null);
    }

    private VirtualMediaFaultInjector Arm(VirtualFaultMode mode, WIN32_ERROR? error)
    {
        Mode = mode;
        if (error.HasValue)
            ErrorWin32 = error.Value;
        EveryNth = 1;
        Counter = 0;
        AutoDisable = true;
        Enabled = true;
        return this;
    }

    #endregion

    /// <summary>
    /// Returns to the disarmed state, clearing every mode-specific knob. Worth calling in a test teardown:
    ///  a leaked <see cref="Enabled"/> would silently corrupt every subsequent operation on the same drive.
    /// </summary>
    public void Reset()
    {
        Enabled = false;
        Mode = VirtualFaultMode.Fail;
        EveryNth = 1;
        Counter = 0;
        AutoDisable = false;
        PartialBlocks = 0;
        TornBytes = -1;
        CorruptBits = 2;
        CorruptSpan = 0;
        CorruptOffset = -1;
        CorruptSeed = 42;
        Occurrences = 0;
    }

    public override string ToString()
    {
        if (!Enabled)
            return $"disarmed (fired {Occurrences})";

        string detail = Mode switch
        {
            VirtualFaultMode.Partial => $", {PartialBlocks} block(s) first",
            VirtualFaultMode.Torn    => $", {(TornBytes >= 0 ? $"{TornBytes} B" : "half a block")} valid",
            VirtualFaultMode.Corrupt => $", {CorruptBits} bit(s) @ {(CorruptOffset >= 0 ? $"offset {CorruptOffset}" : $"span {(CorruptSpan > 0 ? CorruptSpan : 0)}")}",
            _ => string.Empty,
        };

        string error = Mode == VirtualFaultMode.Corrupt ? "silent" : Error.ToString();
        return $"{Mode} every {EveryNth}{detail} (counter {Counter}, fired {Occurrences}, {error})";
    }
}

#endif // DEBUG
