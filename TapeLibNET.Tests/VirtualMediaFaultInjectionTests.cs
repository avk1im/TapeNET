using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;

#if DEBUG

/// <summary>
/// Proves the block-level fault injector itself, independently of any feature that uses it.
/// <para>
/// The organising idea across all four modes is the GAP between what the caller is told and what is
///  actually true on the medium. <c>Fail</c> and <c>Partial</c> leave no gap — the caller is correctly
///  informed. <c>Torn</c> and <c>Corrupt</c> open one, and those are the states in which correct-looking
///  library code proceeds on a false premise. Most of what follows tests the gap, not the error code.
/// </para>
/// </summary>
public class VirtualMediaFaultInjectionTests
{
    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];

    #region *** Helpers ***

    /// <summary>
    /// Blocks filled with a varying, position-dependent pattern.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT a constant fill: a flipped bit in constant data can coincide with a neighbouring
    ///  byte's value, so "which byte moved" becomes ambiguous. A varying pattern makes every corruption
    ///  locatable.
    /// </remarks>
    private static byte[] MakeBlocks(TapeDrive drive, int blockCount, byte salt = 0)
    {
        var buffer = new byte[(int)drive.BlockSize * blockCount];
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = (byte)(i * 7 + salt);
        return buffer;
    }

    /// <summary>Reads back from BOM and returns exactly what the medium delivered.</summary>
    private static byte[] ReadBackFromStart(TapeDrive drive, int byteCount, out int read)
    {
        Assert.True(drive.Rewind());
        var buffer = new byte[byteCount];
        read = drive.ReadDirect(buffer, 0, buffer.Length, out _, out _);
        return buffer;
    }

    private static int CountDifferingBytes(byte[] a, byte[] b)
    {
        int diff = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (a[i] != b[i])
                diff++;
        return diff;
    }

    /// <summary>A framed set-header block — the real payload whose CRC the Corrupt mode must break.</summary>
    private static byte[] FramedSetHeader() =>
        TapeHeaderBlock.Frame(new TapeSetHeader
        {
            MediaId        = Guid.Parse("7E1C4A88-2B15-4C9E-9F3A-1D5E8B0C6A24"),
            CreatedUtc     = new DateTime(2026, 9, 12, 10, 30, 0, DateTimeKind.Utc),
            SetBlockSize   = 64 * 1024,
            Volume         = 2,
            VolumeSetIndex = 1,
            GlobalSetIndex = 3,
            Description    = "Weekly full",
        })!;

    #endregion

    #region *** (A) Fail — nothing written, caller correctly informed ***

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void FailOnce_WriteFails_ThenNextWriteSucceeds(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;
        byte[] data = MakeBlocks(drive, 1);

        fixture.Backend.ContentWriteFaults.FailOnce();

        Assert.Equal(0, drive.WriteDirect(data, 0, data.Length));
        Assert.True(drive.WentBad, "an injected fault must surface as a drive error");
        Assert.Equal(1, fixture.Backend.ContentWriteFaults.Occurrences);

        // AutoDisable: the very next write goes through untouched.
        Assert.Equal(data.Length, drive.WriteDirect(data, 0, data.Length));
        Assert.False(fixture.Backend.ContentWriteFaults.Enabled);
    }

    /// <summary>Nothing was written, so the medium must be exactly as it was: empty.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void FailOnce_LeavesTheMediumUntouched(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;
        byte[] data = MakeBlocks(drive, 1);

        fixture.Backend.ContentWriteFaults.FailOnce();
        Assert.Equal(0, drive.WriteDirect(data, 0, data.Length));

        ReadBackFromStart(drive, data.Length, out int read);
        Assert.Equal(0, read);          // end of data — nothing ever landed
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void FailOnce_ReadFails_ThenNextReadSucceeds(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;
        byte[] data = MakeBlocks(drive, 1);

        Assert.Equal(data.Length, drive.WriteDirect(data, 0, data.Length));
        Assert.True(drive.Rewind());

        fixture.Backend.ContentReadFaults.FailOnce();

        var buffer = new byte[data.Length];
        Assert.Equal(0, drive.ReadDirect(buffer, 0, buffer.Length, out _, out _));
        Assert.True(drive.WentBad);
        Assert.Equal(1, fixture.Backend.ContentReadFaults.Occurrences);

        // The data itself was never harmed — a read fault is transient.
        byte[] again = ReadBackFromStart(drive, data.Length, out int read);
        Assert.Equal(data.Length, read);
        Assert.Equal(data, again);
    }

    #endregion

    #region *** (B) Partial — prefix written, caller correctly informed ***

    /// <summary>
    /// The prefix goes through the normal path, so it is fully registered and fully readable; only the
    ///  remainder is lost. Crucially the prefix is VERBATIM — <c>Partial</c> answers "how far did the
    ///  medium get", never "are the bytes right".
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void PartialOnce_WritesThePrefixVerbatim_ThenFaults(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;
        int blockSize = (int)drive.BlockSize;
        byte[] data = MakeBlocks(drive, 4);

        fixture.Backend.ContentWriteFaults.PartialOnce(blocks: 2);

        int written = drive.WriteDirect(data, 0, data.Length);

        Assert.Equal(2 * blockSize, written);
        Assert.True(drive.WentBad, "the fault must still be reported after the partial transfer");

        // The returned count is the truth: exactly that prefix is on tape, byte for byte.
        byte[] back = ReadBackFromStart(drive, 2 * blockSize, out int read);
        Assert.Equal(2 * blockSize, read);
        Assert.Equal([.. data.Take(2 * blockSize)], back);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void PartialOnce_WithZeroBlocks_DegeneratesToFail(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;
        byte[] data = MakeBlocks(drive, 2);

        fixture.Backend.ContentWriteFaults.PartialOnce(blocks: 0);

        Assert.Equal(0, drive.WriteDirect(data, 0, data.Length));
        Assert.True(drive.WentBad);
    }

    /// <summary>Read-side <c>Partial</c>: the prefix is delivered intact, then the fault is reported.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void PartialOnce_Read_DeliversThePrefix_ThenFaults(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;
        int blockSize = (int)drive.BlockSize;
        byte[] data = MakeBlocks(drive, 3);

        Assert.Equal(data.Length, drive.WriteDirect(data, 0, data.Length));
        Assert.True(drive.Rewind());

        fixture.Backend.ContentReadFaults.PartialOnce(blocks: 1);

        var buffer = new byte[data.Length];
        int read = drive.ReadDirect(buffer, 0, buffer.Length, out _, out _);

        Assert.Equal(blockSize, read);
        Assert.True(drive.WentBad);
        Assert.Equal([.. data.Take(blockSize)], [.. buffer.Take(blockSize)]);
    }

    #endregion

    #region *** (C) Torn — block committed, caller told it failed ***

    /// <summary>
    /// The defining property, and the only thing <c>Torn</c> models that <c>Fail</c> does not: the caller
    ///  is told zero bytes were written, while the medium has genuinely advanced by a full block.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TearOnce_ReportsFailure_ButTheMediumAdvanced(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;
        byte[] data = MakeBlocks(drive, 1);

        long blockBefore = drive.GetCurrentBlock();

        fixture.Backend.ContentWriteFaults.TearOnce();

        Assert.Equal(0, drive.WriteDirect(data, 0, data.Length));
        Assert.True(drive.WentBad, "the caller must be told the write failed");

        // …yet the device itself reports a new position. THAT is the divergence being modelled.
        Assert.Equal(blockBefore + 1, drive.GetCurrentBlock());
    }

    /// <summary>
    /// A torn block is READABLE — full size, no end-of-data. It is not silence; it is plausible-looking
    ///  garbage, which is exactly why a structural check one layer up is the only defence.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TearOnce_LeavesAFullSizedReadableBlock(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;
        byte[] data = MakeBlocks(drive, 1);

        fixture.Backend.ContentWriteFaults.TearOnce();
        Assert.Equal(0, drive.WriteDirect(data, 0, data.Length));

        byte[] back = ReadBackFromStart(drive, data.Length, out int read);

        Assert.Equal(data.Length, read);      // a whole block — NOT end of data
        Assert.NotEqual(data, back);          // but not the data the caller offered
    }

    /// <summary>
    /// The payload is truncated at exactly <c>TornBytes</c>: real data up to the cut, zero padding after.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TearOnce_KeepsThePrefix_AndPadsTheRest(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;
        byte[] data = MakeBlocks(drive, 1);
        const int goodBytes = 512;

        fixture.Backend.ContentWriteFaults.TearOnce(bytes: goodBytes);
        Assert.Equal(0, drive.WriteDirect(data, 0, data.Length));

        byte[] back = ReadBackFromStart(drive, data.Length, out int read);

        Assert.Equal(data.Length, read);
        Assert.Equal([.. data.Take(goodBytes)], [.. back.Take(goodBytes)]);
        Assert.All(back.Skip(goodBytes), b => Assert.Equal(0, b));
    }

    /// <summary>Length of the framed record proper, before the block's zero padding.</summary>
    private static int FramedRecordLength(byte[] framed)
    {
        int end = framed.Length;
        while (end > 0 && framed[end - 1] == 0)
            end--;
        return end;
    }

    /// <summary>
    /// The payoff: a torn framed record is full-sized and reads back cleanly, yet fails classification.
    ///  The genuine <c>Unreadable</c> input the set-header verdict ladder needs — no hand-corrupted
    ///  fixture anywhere.
    /// </summary>
    /// <remarks>
    /// The tear point is derived from the record, NOT left at the half-block default: a header frame
    ///  occupies only tens of bytes of a 16 KiB block, so the default cut lands deep in the zero padding
    ///  and truncates nothing that matters. See <see cref="TearOnce_InThePadding_LeavesTheRecordIntact"/>.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TearOnce_FramedRecord_ReadsBackButFailsClassification(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;
        byte[] framed = FramedSetHeader();

        int recordLength = FramedRecordLength(framed);
        int tearAt = recordLength / 2;                   // squarely INSIDE the record
        Assert.InRange(tearAt, 1, recordLength - 1);

        Assert.True(drive.SetBlockSize(TapeHeaderBlock.Size));

        fixture.Backend.ContentWriteFaults.TearOnce(bytes: tearAt);
        Assert.Equal(0, drive.WriteDirect(framed, 0, framed.Length));

        byte[] back = ReadBackFromStart(drive, framed.Length, out int read);

        Assert.Equal(TapeHeaderBlock.Size, read);                    // the manager's read SUCCEEDS…
        Assert.Null(TapeHeaderBlock.Classify(back, read));           // …and the agent rejects it
    }

    /// <summary>
    /// A tear landing in the block's zero padding damages NOTHING: the frame is byte-identical and the
    ///  CRC still checks out.
    /// </summary>
    /// <remarks>
    /// Reassuring rather than alarming, and it bounds how often a torn header write is actually
    ///  detectable: a 16 KiB block carrying a ~70-byte record is almost entirely padding, so most tear
    ///  points are benign. Relevant to the set-header verdict ladder — <c>Unreadable</c> from a torn write
    ///  is the exception, not the rule.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TearOnce_InThePadding_LeavesTheRecordIntact(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;
        byte[] framed = FramedSetHeader();

        int recordLength = FramedRecordLength(framed);
        Assert.True(recordLength < TapeHeaderBlock.Size / 2,
            "the record must be small enough that a half-block tear falls in the padding");

        Assert.True(drive.SetBlockSize(TapeHeaderBlock.Size));

        fixture.Backend.ContentWriteFaults.TearOnce();               // default: half a block
        Assert.Equal(0, drive.WriteDirect(framed, 0, framed.Length));

        byte[] back = ReadBackFromStart(drive, framed.Length, out int read);

        Assert.Equal(TapeHeaderBlock.Size, read);
        var header = Assert.IsType<TapeSetHeader>(TapeHeaderBlock.Classify(back, read));
        Assert.Equal(3, header.GlobalSetIndex);                      // fully intact, CRC and all
    }

    /// <summary>
    /// Recovery: a torn write costs nothing permanently. Re-positioning and re-writing produces a clean
    ///  block — the committed garbage is simply overwritten.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TearOnce_ThenRewrite_ProducesACleanBlock(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;
        byte[] data = MakeBlocks(drive, 1);

        fixture.Backend.ContentWriteFaults.TearOnce();      // AutoDisable — the retry is clean
        Assert.Equal(0, drive.WriteDirect(data, 0, data.Length));

        Assert.True(drive.Rewind());
        Assert.Equal(data.Length, drive.WriteDirect(data, 0, data.Length));

        byte[] back = ReadBackFromStart(drive, data.Length, out int read);
        Assert.Equal(data.Length, read);
        Assert.Equal(data, back);
    }

    /// <summary>The damage is confined to the block being written.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TearOnce_LeavesEarlierBlocksIntact(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;
        byte[] first = MakeBlocks(drive, 1, salt: 0x10);
        byte[] torn  = MakeBlocks(drive, 1, salt: 0x20);

        Assert.Equal(first.Length, drive.WriteDirect(first, 0, first.Length));

        fixture.Backend.ContentWriteFaults.TearOnce();
        Assert.Equal(0, drive.WriteDirect(torn, 0, torn.Length));

        byte[] back = ReadBackFromStart(drive, first.Length, out int read);
        Assert.Equal(first.Length, read);
        Assert.Equal(first, back);
    }

    /// <summary><c>Torn</c> has no read-side meaning and must degrade to a plain failure.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TearOnce_OnRead_BehavesAsFail(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;
        byte[] data = MakeBlocks(drive, 1);

        Assert.Equal(data.Length, drive.WriteDirect(data, 0, data.Length));
        Assert.True(drive.Rewind());

        fixture.Backend.ContentReadFaults.TearOnce();

        var buffer = new byte[data.Length];
        Assert.Equal(0, drive.ReadDirect(buffer, 0, buffer.Length, out _, out _));
        Assert.True(drive.WentBad);
    }

    #endregion

    #region *** (D) Corrupt — everything succeeds, the bytes are wrong ***

    /// <summary>
    /// The defining property: every observable says success. No error, full byte count, normal head
    ///  advance. Only the content betrays it.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void CorruptOnce_ReportsCompleteSuccess_ButTheDataIsWrong(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;
        byte[] data = MakeBlocks(drive, 1);

        fixture.Backend.ContentWriteFaults.CorruptOnce(bits: 2, offset: 16);

        Assert.Equal(data.Length, drive.WriteDirect(data, 0, data.Length));
        Assert.True(drive.WentOK, "corruption must be SILENT — that is the entire point");
        Assert.Equal(1, fixture.Backend.ContentWriteFaults.Occurrences);

        byte[] back = ReadBackFromStart(drive, data.Length, out int read);
        Assert.Equal(data.Length, read);
        Assert.NotEqual(data, back);
    }

    /// <summary>
    /// Corruption is surgical: at most <c>CorruptBits</c> bytes differ, everything else is verbatim. A mode
    ///  that mangled whole blocks would be indistinguishable from <c>Torn</c> and would prove nothing about
    ///  CRC sensitivity.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void CorruptOnce_TouchesOnlyTheFlippedBytes(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;
        byte[] data = MakeBlocks(drive, 1);

        fixture.Backend.ContentWriteFaults.CorruptOnce(bits: 2, offset: 100);

        Assert.Equal(data.Length, drive.WriteDirect(data, 0, data.Length));
        byte[] back = ReadBackFromStart(drive, data.Length, out _);

        int differing = CountDifferingBytes(data, back);
        Assert.InRange(differing, 1, 2);
    }

    /// <summary>An explicit offset targets exactly that byte — needed to aim at a specific header field.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void CorruptOnce_AtExactOffset_HitsThatByte(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;
        byte[] data = MakeBlocks(drive, 1);
        const int target = 64;

        fixture.Backend.ContentWriteFaults.CorruptOnce(bits: 1, offset: target);

        Assert.Equal(data.Length, drive.WriteDirect(data, 0, data.Length));
        byte[] back = ReadBackFromStart(drive, data.Length, out _);

        Assert.NotEqual(data[target], back[target]);
        Assert.Equal(1, CountDifferingBytes(data, back));
    }

    /// <summary>
    /// The caller's buffer must never be mutated: <c>TapeHeaderBlock</c> and the packer both own and reuse
    ///  their arrays, so corrupting in place would poison the caller's own copy.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void CorruptOnce_DoesNotMutateTheCallersBuffer(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;
        byte[] data = MakeBlocks(drive, 1);
        byte[] pristine = (byte[])data.Clone();

        fixture.Backend.ContentWriteFaults.CorruptOnce(bits: 3, offset: 32);
        Assert.Equal(data.Length, drive.WriteDirect(data, 0, data.Length));

        Assert.Equal(pristine, data);
    }

    /// <summary>
    /// Same seed, same flips — every time. An unreproducible failing test is worse than no test.
    /// </summary>
    [Fact]
    public void CorruptOnce_IsDeterministicAcrossRuns()
    {
        static byte[] RunOnce()
        {
            using var fixture = new VirtualTapeFixture(DriveProfile.Setmarks);
            var drive = fixture.Drive;
            byte[] data = MakeBlocks(drive, 1);

            fixture.Backend.ContentWriteFaults.CorruptOnce(bits: 3);   // random placement, fixed seed
            Assert.Equal(data.Length, drive.WriteDirect(data, 0, data.Length));

            return ReadBackFromStart(drive, data.Length, out _);
        }

        Assert.Equal(RunOnce(), RunOnce());
    }

    /// <summary>
    /// The payoff for <c>Corrupt</c>: a structurally perfect, correctly sized framed record that the CRC
    ///  rejects. This is the first end-to-end proof that the framing checksum earns its keep on real tape
    ///  traffic rather than in a serialization unit test.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void CorruptOnce_FramedRecord_IsRejectedByTheCrc(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;
        byte[] framed = FramedSetHeader();

        Assert.True(drive.SetBlockSize(TapeHeaderBlock.Size));

        // Aim into the record's body — the preamble and payload live at the front, the tail is padding.
        fixture.Backend.ContentWriteFaults.CorruptOnce(bits: 2, offset: 48);

        Assert.Equal(framed.Length, drive.WriteDirect(framed, 0, framed.Length));
        Assert.True(drive.WentOK);

        byte[] back = ReadBackFromStart(drive, framed.Length, out int read);

        Assert.Equal(TapeHeaderBlock.Size, read);                    // full block, no I/O error…
        Assert.Null(TapeHeaderBlock.Classify(back, read));           // …rejected on CRC alone
    }

    /// <summary>
    /// Read-side corruption is TRANSIENT — the tape is fine, only the return path lied. A re-read after
    ///  re-positioning yields clean data. This is what makes "the medium is bad" and "the path is bad"
    ///  separately testable, a distinction no error code ever surfaces.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void CorruptOnce_OnRead_IsTransient(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;
        byte[] data = MakeBlocks(drive, 1);

        Assert.Equal(data.Length, drive.WriteDirect(data, 0, data.Length));

        fixture.Backend.ContentReadFaults.CorruptOnce(bits: 2, offset: 24);

        byte[] bad = ReadBackFromStart(drive, data.Length, out int read);
        Assert.Equal(data.Length, read);
        Assert.True(drive.WentOK, "read corruption must be silent too");
        Assert.NotEqual(data, bad);

        // The medium never changed: re-read and everything is correct.
        byte[] good = ReadBackFromStart(drive, data.Length, out int readAgain);
        Assert.Equal(data.Length, readAgain);
        Assert.Equal(data, good);
    }

    #endregion

    #region *** (E) Scheduling ***

    /// <summary>
    /// <c>EveryNth</c> fires on the Nth operation, exactly as <c>SimulateFileFailures</c> does — the
    ///  familiar shape matters more than the specific arithmetic.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void EveryNth_FiresOnTheNthOperation(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;
        byte[] data = MakeBlocks(drive, 1);

        var faults = fixture.Backend.ContentWriteFaults;
        faults.Enabled = true;
        faults.EveryNth = 3;

        Assert.Equal(data.Length, drive.WriteDirect(data, 0, data.Length));   // 1
        Assert.Equal(data.Length, drive.WriteDirect(data, 0, data.Length));   // 2
        Assert.Equal(0,           drive.WriteDirect(data, 0, data.Length));   // 3 — fires

        Assert.Equal(1, faults.Occurrences);

        faults.Reset();
    }

    /// <summary>Pre-seeding the counter shifts the phase so the very next operation faults.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void PreSeededCounter_FiresImmediately(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);
        var drive = fixture.Drive;
        byte[] data = MakeBlocks(drive, 1);

        var faults = fixture.Backend.ContentWriteFaults;
        faults.Enabled = true;
        faults.EveryNth = 4;
        faults.Counter = 3;

        Assert.Equal(0, drive.WriteDirect(data, 0, data.Length));
        Assert.Equal(1, faults.Occurrences);

        faults.Reset();
    }

    /// <summary>
    /// Settings survive a volume swap: the medium object is rebuilt by <c>LoadMedia</c>, but the injector
    ///  lives on the BACKEND and is re-wired on every load. Without this, any multi-volume fault test
    ///  would silently stop injecting after the first swap.
    /// </summary>
    [Fact]
    public void Injector_SurvivesMediaReload()
    {
        using var fixture = new MultiVolumeVirtualTapeFixture(DriveProfile.Setmarks);
        var drive = fixture.Drive;

        fixture.Backend.ContentWriteFaults.FailOnce();

        fixture.SwapToNewVolume();   // ejects, inserts, reloads — a brand-new VirtualTapeMedia

        byte[] data = MakeBlocks(drive, 1);
        Assert.Equal(0, drive.WriteDirect(data, 0, data.Length));
        Assert.Equal(1, fixture.Backend.ContentWriteFaults.Occurrences);
    }

    /// <summary>
    /// <c>Reset</c> must clear every mode-specific knob, not merely the master switch: a leaked
    ///  <c>CorruptOffset</c> or <c>Mode</c> would silently retarget a later test on the same drive.
    /// </summary>
    [Fact]
    public void Reset_ClearsEveryKnob()
    {
        using var fixture = new VirtualTapeFixture(DriveProfile.Setmarks);
        var faults = fixture.Backend.ContentWriteFaults;

        faults.CorruptOnce(bits: 5, offset: 999, span: 1234);
        faults.Reset();

        Assert.False(faults.Enabled);
        Assert.Equal(VirtualFaultMode.Fail, faults.Mode);
        Assert.Equal(0, faults.Counter);
        Assert.Equal(0, faults.Occurrences);
        Assert.Equal(-1, faults.CorruptOffset);
        Assert.Equal(0, faults.CorruptSpan);

        byte[] data = MakeBlocks(fixture.Drive, 1);
        Assert.Equal(data.Length, fixture.Drive.WriteDirect(data, 0, data.Length));

        byte[] back = ReadBackFromStart(fixture.Drive, data.Length, out _);
        Assert.Equal(data, back);
    }

    #endregion
}

#endif // DEBUG
