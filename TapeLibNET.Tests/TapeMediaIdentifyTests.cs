using System.Text;

using TapeLibNET.Scan;

using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests;

/// <summary>
/// Scan Media Phase 0, pure tier: <see cref="TapeHeaderBlock.TryIdentifyHeaderBlock"/>,
///  <see cref="TapeHeaderBlock.CarriesRecordSignature"/>, and the JSON round-trip of the scan records.
/// </summary>
/// <remarks>
/// <para>
/// No drive, no tape, no fixture. These pin the TOC-LESS half of identification (SM-2): every assertion
///  below is reachable with nothing but bytes, which is precisely what lets the scanner survey a cartridge
///  it has no table of contents for.
/// </para>
/// <para>
/// <b>Totality is the property under test.</b> The scanner reads whatever a damaged cartridge hands it and
///  must classify it or shrug — never throw. A single escaping exception on one bad block would abandon the
///  map for every fragment behind it, which on a damaged tail is exactly where the interesting ones are.
/// </para>
/// </remarks>
public class TapeMediaIdentifyTests
{
    #region *** Helpers ***

    private static readonly Guid s_mediaId = Guid.Parse("5C0D1E2F-3A4B-4C5D-8E9F-0A1B2C3D4E5F");
    private static readonly DateTime s_created = new(2026, 9, 27, 14, 0, 0, DateTimeKind.Utc);

    private static TapeMediaHeader MakeMediaHeader(bool hasSetHeaders = true) =>
        new()
        {
            MediaId       = s_mediaId,
            CreatedUtc    = s_created,
            TocBlockSize  = TapeHeader.FixedHeaderBlockSize,
            Volume        = 2,
            Partition     = MediaPartition.Content,
            TocPlacement  = TapeTocPlacement.InSet,
            OriginalName  = "Scan Subject",
            HasSetHeaders = hasSetHeaders,
        };

    private static TapeSetHeader MakeSetHeader(int volumeSetIndex = 1, int globalSetIndex = 3) =>
        new()
        {
            MediaId        = s_mediaId,
            CreatedUtc     = s_created,
            SetBlockSize   = 64 * 1024,
            Volume         = 2,
            VolumeSetIndex = volumeSetIndex,
            GlobalSetIndex = globalSetIndex,
            Description    = "Monthly — September",
        };

    private static TapeCalibrationHeader MakeCalibrationHeader() =>
        TapeCalibrationHeader.CreateHeader(
            runId: Guid.NewGuid(), profileKey: "VEND|PROD|REV|64MB",
            capacityReportedAtBom: 64L * 1024 * 1024, blockSize: 1024 * 1024,
            startedUtc: s_created,
            plan: new TapeCalibrationPlan(
                SampleCount: 40, BodySampleCount: 24, TailSampleCount: 16,
                BlockSize: 1024 * 1024, BlocksPerChunk: 4, ChunkSize: 4 << 20,
                TailBlocksPerChunk: 1, TailChunkSize: 1 << 20,
                TailCapacityFraction: 0.05, NumCheckpoints: 16));

    /// <summary>Frames a header into a full standard block, exactly as the on-tape path does.</summary>
    private static byte[] Block(TapeHeader header)
    {
        byte[]? block = TapeHeaderBlock.Frame(header);
        Assert.NotNull(block);
        return block!;
    }

    private static byte[] RandomBlock(int seed)
    {
        var block = new byte[TapeHeaderBlock.Size];
        new Random(seed).NextBytes(block);
        return block;
    }

    #endregion

    #region *** (A) The three header kinds ***

    /// <summary>
    /// The core claim of §4: ONE call identifies all three kinds, because
    ///  <see cref="TapeHeader.ConstructFrom"/> dispatches on the kind byte. The scanner never needs to know
    ///  the kinds apart before parsing — only after.
    /// </summary>
    [Fact]
    public void TryIdentify_MediaHeader_YieldsMediaHeaderAndItsFields()
    {
        byte[] block = Block(MakeMediaHeader());

        Assert.True(TapeHeaderBlock.TryIdentifyHeaderBlock(block, block.Length, out TapeHeader? header));

        var media = Assert.IsType<TapeMediaHeader>(header);
        Assert.Equal(s_mediaId, media.MediaId);
        Assert.Equal(2, media.Volume);
        Assert.True(media.HasSetHeaders);
        Assert.Equal("Scan Subject", media.OriginalName);
    }

    [Fact]
    public void TryIdentify_SetHeader_YieldsSetHeaderAndItsIndices()
    {
        byte[] block = Block(MakeSetHeader(volumeSetIndex: 4, globalSetIndex: 9));

        Assert.True(TapeHeaderBlock.TryIdentifyHeaderBlock(block, block.Length, out TapeHeader? header));

        var set = Assert.IsType<TapeSetHeader>(header);
        Assert.Equal(4, set.VolumeSetIndex);
        Assert.Equal(9, set.GlobalSetIndex);
        Assert.Equal("Monthly — September", set.Description);
    }

    /// <summary>
    /// SM-7's parse half: a calibration cartridge is a fully identified, LEGITIMATE result — never alien
    ///  media. The shared <see cref="TapeHeader"/> grammar exists precisely so one BOM read classifies a
    ///  cartridge as media, set, or calibration.
    /// </summary>
    [Fact]
    public void TryIdentify_CalibrationHeader_YieldsCalibrationHeader()
    {
        var original = MakeCalibrationHeader();
        byte[] block = Block(original);

        Assert.True(TapeHeaderBlock.TryIdentifyHeaderBlock(block, block.Length, out TapeHeader? header));

        var cal = Assert.IsType<TapeCalibrationHeader>(header);
        Assert.Equal(original.RunId, cal.RunId);
        Assert.Equal(original.ProfileKey, cal.ProfileKey);
    }

    /// <summary>
    /// The kinds must not alias. A set header misread as a media header would make the scanner report a
    ///  second volume header mid-tape; the reverse would lose the volume's identity entirely.
    /// </summary>
    [Fact]
    public void TryIdentify_NeverConfusesTheThreeHeaderKinds()
    {
        (TapeHeader Header, Type Expected)[] cases =
        [
            (MakeMediaHeader(),       typeof(TapeMediaHeader)),
            (MakeSetHeader(),         typeof(TapeSetHeader)),
            (MakeCalibrationHeader(), typeof(TapeCalibrationHeader)),
        ];

        foreach (var (header, expected) in cases)
        {
            byte[] block = Block(header);

            Assert.True(TapeHeaderBlock.TryIdentifyHeaderBlock(block, block.Length, out TapeHeader? read));
            Assert.IsType(expected, read);
        }
    }

    #endregion

    #region *** (B) Totality — every unidentifiable input yields false, never a throw ***

    /// <summary>
    /// The property the scanner leans on hardest (SM-4). Each input below is something a real damaged
    ///  cartridge can hand back; none may escape as an exception.
    /// </summary>
    public static TheoryData<string, byte[], int> UnidentifiableBlocks
    {
        get
        {
            var data = new TheoryData<string, byte[], int>
            {
                { "all zeros — blank tape", new byte[TapeHeaderBlock.Size], TapeHeaderBlock.Size },
                { "random bytes — foreign or calibration padding", RandomBlock(7), TapeHeaderBlock.Size },
                {
                    "all 0xFF — erased media on some drives",
                    [.. Enumerable.Repeat((byte)0xFF, TapeHeaderBlock.Size)],
                    TapeHeaderBlock.Size
                },
                { "empty", [], 0 },
                { "one byte — far too short to hold a signature", [0x42], 1 }
            };

            // A frame whose length prefix is implausibly large: the framer must reject it rather than
            //  attempt the allocation.
            var absurdLength = new byte[TapeHeaderBlock.Size];
            BitConverter.GetBytes(int.MaxValue).CopyTo(absurdLength, 0);
            data.Add("absurd length prefix", absurdLength, TapeHeaderBlock.Size);

            var negativeLength = new byte[TapeHeaderBlock.Size];
            BitConverter.GetBytes(-1).CopyTo(negativeLength, 0);
            data.Add("negative length prefix", negativeLength, TapeHeaderBlock.Size);

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(UnidentifiableBlocks))]
    public void TryIdentify_UnidentifiableInput_ReturnsFalseWithoutThrowing(
        string because, byte[] block, int length)
    {
        Assert.False(TapeHeaderBlock.TryIdentifyHeaderBlock(block, length, out TapeHeader? header), because);
        Assert.Null(header);
    }

    /// <summary>
    /// A CRC-corrupted header: the block is structurally OURS — right signature, right length prefix — and
    ///  only the CRC knows it is wrong. This is what a torn header write physically leaves behind, and the
    ///  scanner must map it to <see cref="FragmentKind.Unknown"/> rather than to a plausible-looking header.
    /// </summary>
    [Fact]
    public void TryIdentify_CorruptedCrc_ReturnsFalse()
    {
        byte[] block = Block(MakeSetHeader());

        // Flip a bit INSIDE the payload, past the 4-byte length prefix — the same technique
        //  CalibrationResumeTests uses to prove the framer's CRC actually guards the record.
        block[8] ^= 0xFF;

        Assert.False(TapeHeaderBlock.TryIdentifyHeaderBlock(block, block.Length, out TapeHeader? header));
        Assert.Null(header);
    }

    /// <summary>
    /// A header truncated mid-record — the shape a torn write leaves when the tear falls inside the frame.
    /// </summary>
    [Fact]
    public void TryIdentify_TruncatedRecord_ReturnsFalse()
    {
        byte[] block = Block(MakeSetHeader());

        // Cut squarely inside the record proper, not in the block's zero padding: a header frame occupies
        //  only tens of bytes of 16 KiB, so a naive half-block cut would damage nothing at all.
        int recordLength = block.Length;
        while (recordLength > 0 && block[recordLength - 1] == 0)
            recordLength--;

        Assert.False(TapeHeaderBlock.TryIdentifyHeaderBlock(block, recordLength / 2, out TapeHeader? header));
        Assert.Null(header);
    }

    /// <summary>
    /// Legacy content at block 0 — raw legacy <see cref="TapeFileInfo"/> bytes, not a framed record. Pre-header
    ///  media legitimately begins this way, and must map to "unidentified", never to a false header.
    /// </summary>
    [Fact]
    public void TryIdentify_LegacyContentBytes_ReturnsFalse()
    {
        var tfi = new TapeFileInfo(42UL, TapeAddress.Zero,
            new TapeFileDescriptor(@"C:\data\file.dat") { Length = 100 });
        var block = new byte[TapeHeaderBlock.Size];
        LegacyFormatWriter.FileEntryBytes(tfi).CopyTo(block, 0);

        Assert.False(TapeHeaderBlock.TryIdentifyHeaderBlock(block, block.Length, out TapeHeader? header));
        Assert.Null(header);
    }

    /// <summary>A length beyond the buffer is a caller error, and is refused rather than read past.</summary>
    [Fact]
    public void TryIdentify_LengthBeyondBuffer_ReturnsFalse()
    {
        byte[] block = Block(MakeSetHeader());

        Assert.False(TapeHeaderBlock.TryIdentifyHeaderBlock(block, block.Length + 1, out _));
    }

    /// <summary>The span overload agrees with the array overload on every input.</summary>
    [Fact]
    public void TryIdentify_SpanOverload_AgreesWithArrayOverload()
    {
        byte[] good = Block(MakeMediaHeader());
        byte[] bad = RandomBlock(11);

        Assert.True(TapeHeaderBlock.TryIdentifyHeaderBlock(good.AsSpan(), out TapeHeader? fromSpan));
        Assert.IsType<TapeMediaHeader>(fromSpan);

        Assert.False(TapeHeaderBlock.TryIdentifyHeaderBlock(bad.AsSpan(), out _));
    }

    #endregion

    #region *** (C) The signature probe — the cheap half of the TOC test ***

    /// <summary>
    /// A header carries the library's signature, so the probe answers TRUE for it. That is not a defect:
    ///  the probe answers "is this one of ours?", and the TOC-copy test (§4.3) is the CONJUNCTION —
    ///  signature present AND <see cref="TapeHeaderBlock.TryIdentifyHeaderBlock"/> failing.
    /// </summary>
    [Fact]
    public void CarriesRecordSignature_TrueForEveryHeaderKind()
    {
        foreach (TapeHeader header in new TapeHeader[]
                 { MakeMediaHeader(), MakeSetHeader(), MakeCalibrationHeader() })
        {
            byte[] block = Block(header);
            Assert.True(TapeHeaderBlock.CarriesRecordSignature(block, block.Length));
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void CarriesRecordSignature_FalseForRandomBytes(int seed)
    {
        byte[] block = RandomBlock(seed);
        Assert.False(TapeHeaderBlock.CarriesRecordSignature(block, block.Length));
    }

    [Fact]
    public void CarriesRecordSignature_FalseForBlankAndEmpty()
    {
        Assert.False(TapeHeaderBlock.CarriesRecordSignature(new byte[TapeHeaderBlock.Size], TapeHeaderBlock.Size));
        Assert.False(TapeHeaderBlock.CarriesRecordSignature(new byte[TapeHeaderBlock.Size], 0));
        Assert.False(TapeHeaderBlock.CarriesRecordSignature([], 0));
    }

    /// <summary>
    /// The probe is total too — a block too short to hold even a signature is simply not ours, never a throw.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CarriesRecordSignature_TinyBlock_ReturnsFalseWithoutThrowing(int length)
    {
        Assert.False(TapeHeaderBlock.CarriesRecordSignature(new byte[length], length));
    }

    #endregion

    #region *** (C2) IdentifyBlock — positive identification ***

    /// <summary>Serializes a TOC the way BackupTOCCore does, into the front of one standard block.</summary>
    private static byte[] TocBlock(TapeTOC toc)
    {
        using var ms = new MemoryStream();
        toc.SaveTo(ms);

        var block = new byte[TapeHeaderBlock.Size];
        byte[] bytes = ms.ToArray();
        Array.Copy(bytes, block, Math.Min(bytes.Length, block.Length));
        return block;
    }

    /// <summary>A TOC carrying a media id and one set — the shape of every current on-tape copy.</summary>
    /// <remarks>
    /// Built through the public surface only. <c>CreateHeader</c> mints the media id as a side effect,
    ///  exactly as the first durable write does. One set suffices: what is tested is the NESTED set
    ///  signature, which an empty set carries as well as a full one.
    /// </remarks>
    private static TapeTOC MakeToc()
    {
        var toc = new TapeTOC("Scan Subject");
        toc.CreateHeader(TapeHeader.FixedHeaderBlockSize, TapeTocPlacement.InSet);

        toc.AddNewSetTOC();
        toc.CurrentSetTOC.Description = "Set 1";

        return toc;
    }

    // ── Headers ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IdentifyBlock_IntactHeaders_AreHeaders()
    {
        foreach (TapeHeader original in new TapeHeader[]
                 { MakeMediaHeader(), MakeSetHeader(), MakeCalibrationHeader() })
        {
            byte[] block = Block(original);

            IdentifiedBlock id = TapeHeaderBlock.IdentifyBlock(block, block.Length);

            Assert.Equal(HeaderBlockIdentity.Header, id.Kind);
            Assert.Equal(TapeFramer.FrameStatus.Ok, id.FrameStatus);
            Assert.IsType(original.GetType(), id.Header);
        }
    }

    /// <summary>
    /// The first regression: a header damaged BEHIND its signature is our record, damaged — never a TOC.
    /// </summary>
    [Fact]
    public void IdentifyBlock_CrcDamagedHeader_IsDamagedRecord_NeverTocCopy()
    {
        byte[] block = Block(MakeSetHeader());
        block[48] ^= 0x03;          // the spot the scanner test corrupts: inside the payload

        IdentifiedBlock id = TapeHeaderBlock.IdentifyBlock(block, block.Length);

        Assert.Equal(HeaderBlockIdentity.DamagedRecord, id.Kind);
        Assert.Equal(TapeFramer.FrameStatus.CrcMismatch, id.FrameStatus);
        Assert.Null(id.Header);
    }

    /// <summary>A damaged LENGTH PREFIX leaves the framed signature intact: still ours, still damaged.</summary>
    [Fact]
    public void IdentifyBlock_DamagedLengthPrefix_IsDamagedRecord()
    {
        byte[] block = Block(MakeSetHeader());
        BitConverter.GetBytes(-1).CopyTo(block, 0);

        IdentifiedBlock id = TapeHeaderBlock.IdentifyBlock(block, block.Length);

        Assert.Equal(HeaderBlockIdentity.DamagedRecord, id.Kind);
        Assert.Equal(TapeFramer.FrameStatus.NotFramed, id.FrameStatus);
    }

    /// <summary>A damaged header SIGNATURE leaves nothing to recognize: foreign. The honest limit.</summary>
    [Fact]
    public void IdentifyBlock_DamagedHeaderSignature_IsForeign()
    {
        byte[] block = Block(MakeSetHeader());
        block[4] ^= 0xFF;           // first signature byte, just past the length prefix

        Assert.Equal(HeaderBlockIdentity.Foreign, TapeHeaderBlock.IdentifyBlock(block, block.Length).Kind);
    }

    // ── TOC copies ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A 2.1 TOC copy is recognized positively, with its version and the series it describes.
    /// </summary>
    [Fact]
    public void IdentifyBlock_TocCopy_IsRecognized_WithVersionAndMediaId()
    {
        TapeTOC toc = MakeToc();

        IdentifiedBlock id = TapeHeaderBlock.IdentifyBlock(TocBlock(toc), TapeHeaderBlock.Size);

        Assert.Equal(HeaderBlockIdentity.TocCopy, id.Kind);
        Assert.Equal(TapeTOC.TocVersion, id.TocVersion);
        Assert.NotEqual(Guid.Empty, id.TocMediaId);
        Assert.Equal(toc.MediaId, id.TocMediaId);
    }

    /// <summary>An empty TOC — no set record to anchor on — is recognized by its tail instead.</summary>
    [Fact]
    public void IdentifyBlock_EmptyToc_IsRecognized()
    {
        var toc = new TapeTOC("Freshly formatted");

        Assert.Equal(HeaderBlockIdentity.TocCopy,
            TapeHeaderBlock.IdentifyBlock(TocBlock(toc), TapeHeaderBlock.Size).Kind);
    }

    /// <summary>
    /// A legacy file record at block address zero opens with the same bytes a v0x0101 TOC does — and its
    ///  zero words pass for "no sets, empty description". The tail check is what rejects it.
    /// </summary>
    [Fact]
    public void IdentifyBlock_LegacyFileRecordAtAddressZero_IsNotATocCopy()
    {
        var tfi = new TapeFileInfo(42UL, TapeAddress.Zero,
            new TapeFileDescriptor(@"C:\data\file.dat") { Length = 100 });
        var block = new byte[TapeHeaderBlock.Size];
        LegacyFormatWriter.FileEntryBytes(tfi).CopyTo(block, 0);

        Assert.Equal(HeaderBlockIdentity.Foreign, TapeHeaderBlock.IdentifyBlock(block, block.Length).Kind);
    }

    /// <summary>The same record as in <see cref="IdentifyBlock_LegacyFileRecordAtAddressZero_IsNotATocCopy"/>
    /// at a NON-zero address fails earlier — on the nested set signature.</summary>
    [Fact]
    public void IdentifyBlock_LegacyFileRecordAtNonZeroAddress_IsNotATocCopy()
    {
        var tfi = new TapeFileInfo(42UL, new TapeAddress(1234, 0),
            new TapeFileDescriptor(@"C:\data\file.dat") { Length = 100 });
        var block = new byte[TapeHeaderBlock.Size];
        LegacyFormatWriter.FileEntryBytes(tfi).CopyTo(block, 0);

        Assert.Equal(HeaderBlockIdentity.Foreign, TapeHeaderBlock.IdentifyBlock(block, block.Length).Kind);
    }

    /// <summary>A damaged TOC signature leaves nothing to recognize: foreign, never a header.</summary>
    [Fact]
    public void IdentifyBlock_DamagedTocSignature_IsForeign()
    {
        byte[] block = TocBlock(MakeToc());
        block[0] ^= 0xFF;

        Assert.Equal(HeaderBlockIdentity.Foreign, TapeHeaderBlock.IdentifyBlock(block, block.Length).Kind);
    }

    // ── Totality and contracts ───────────────────────────────────────────────────────────────────────

    /// <summary>Every input from the totality theory is foreign — and never throws.</summary>
    [Theory]
    [MemberData(nameof(UnidentifiableBlocks))]
    public void IdentifyBlock_UnidentifiableInput_IsForeign(string because, byte[] block, int length)
    {
        IdentifiedBlock id = TapeHeaderBlock.IdentifyBlock(block, length);

        Assert.True(id.Kind == HeaderBlockIdentity.Foreign, because);
        Assert.Null(id.Header);
    }

    /// <summary><c>Unpack</c> keeps its contract: null for every outcome except Ok.</summary>
    [Fact]
    public void Unpack_StillReturnsNullForADamagedFrame()
    {
        byte[] block = Block(MakeSetHeader());
        block[48] ^= 0x03;

        Assert.Null(TapeFramer.Unpack<TapeHeader>(block, block.Length));
    }

    /// <summary><c>TryUnpack</c> and <c>Unpack</c> agree on an intact frame.</summary>
    [Fact]
    public void TryUnpack_IntactFrame_IsOk_AndMatchesUnpack()
    {
        byte[] block = Block(MakeSetHeader());

        Assert.Equal(TapeFramer.FrameStatus.Ok, TapeFramer.TryUnpack(block, block.Length, out TapeHeader? header));
        Assert.IsType<TapeSetHeader>(header);
        Assert.NotNull(TapeFramer.Unpack<TapeHeader>(block, block.Length));
    }

    #endregion

    #region *** (D) TapeMediaFragment ***

    /// <summary>
    /// <c>default(TapeResult)</c> means FAILURE, so an un-set <see cref="TapeMediaFragment.Diagnosis"/>
    ///  would report every healthy fragment as broken. Pins the explicit initializer.
    /// </summary>
    [Fact]
    public void Fragment_DefaultDiagnosis_IsOk()
    {
        var fragment = new TapeMediaFragment
        {
            Ordinal = 0,
            StartBlock = 0,
            Kind = FragmentKind.SetHeader,
        };

        Assert.True(fragment.Diagnosis.Success);
    }

    [Fact]
    public void Fragment_MakeFingerprint_IsHexAndBounded()
    {
        byte[] block = RandomBlock(13);

        string fingerprint = TapeMediaFragment.MakeFingerprint(block, block.Length);

        Assert.Equal(TapeMediaFragment.FingerprintBytes * 2, fingerprint.Length);   // two hex chars per byte
        Assert.All(fingerprint, c => Assert.True(Uri.IsHexDigit(c)));
    }

    [Fact]
    public void Fragment_MakeFingerprint_ShortBlock_TakesWhatThereIs()
    {
        byte[] block = [0xDE, 0xAD, 0xBE, 0xEF];

        Assert.Equal("DEADBEEF", TapeMediaFragment.MakeFingerprint(block, block.Length));
        Assert.Equal(string.Empty, TapeMediaFragment.MakeFingerprint(block, 0));
        Assert.Equal(string.Empty, TapeMediaFragment.MakeFingerprint([], 0));
    }

    [Theory]
    [InlineData(FragmentKind.MediaHeader, true)]
    [InlineData(FragmentKind.SetHeader, true)]
    [InlineData(FragmentKind.CalibrationHeader, true)]
    [InlineData(FragmentKind.TOC, false)]
    [InlineData(FragmentKind.MarkRun, false)]
    [InlineData(FragmentKind.TocMark, false)]
    [InlineData(FragmentKind.Unknown, false)]
    public void Fragment_IsHeader_CoversExactlyTheThreeHeaderKinds(FragmentKind kind, bool expected)
    {
        var fragment = new TapeMediaFragment { Ordinal = 0, StartBlock = 0, Kind = kind };

        Assert.Equal(expected, fragment.IsHeader);
    }

    /// <summary>
    /// <see cref="TapeMediaFragment.DisplayName"/> is never blank — it reaches log lines and the viewer,
    ///  where an empty cell says nothing about a cartridge the user is trying to diagnose.
    /// </summary>
    [Theory]
    [InlineData(FragmentKind.MediaHeader)]
    [InlineData(FragmentKind.SetHeader)]
    [InlineData(FragmentKind.CalibrationHeader)]
    [InlineData(FragmentKind.TOC)]
    [InlineData(FragmentKind.MarkRun)]
    [InlineData(FragmentKind.TocMark)]
    [InlineData(FragmentKind.Unknown)]
    public void Fragment_DisplayName_IsNeverBlank_EvenWithNoDescription(FragmentKind kind)
    {
        var fragment = new TapeMediaFragment
        {
            Ordinal = 1,
            StartBlock = 100,
            Kind = kind,
            Id = Guid.NewGuid(),
            VolumeSetIndex = 2,
            MarkCount = 3,
        };

        Assert.False(string.IsNullOrWhiteSpace(fragment.DisplayName));
    }

    #endregion

    #region *** (E) MediaScanMap — derived views ***

    private static TapeMediaFragment SetFragment(
        int ordinal, long block, int volumeSetIndex, Guid? mediaId = null, bool closed = true) =>
        new()
        {
            Ordinal = ordinal,
            StartBlock = block,
            Kind = FragmentKind.SetHeader,
            ClosedBySeparator = closed,
            Id = mediaId ?? s_mediaId,
            Volume = 1,
            VolumeSetIndex = volumeSetIndex,
            Description = $"Set {volumeSetIndex}",
        };

    private static MediaScanMap MakeMap(params TapeMediaFragment[] fragments) =>
        new()
        {
            Layout = new TapeMediaLayout(
                nameof(TapeNavigatorTOCInSetWithSmks), UseSmks: true,
                TocInPartition: false, HasTocMark: false, MediaLoaded: true),
            Kind = ScannedMediaKind.Backup,
            Fragments = fragments,
            ScannedUtc = s_created,
        };

    private static TapeMediaFragment MediaFragment(Guid? mediaId = null) =>
        new()
        {
            Ordinal = 0,
            StartBlock = 0,
            Kind = FragmentKind.MediaHeader,
            ClosedBySeparator = true,
            Id = mediaId ?? s_mediaId,
            Volume = 1,
            Description = "Scan Subject",
        };

    [Fact]
    public void Map_CountsSetsAndTocCopies_FromTheTapeAlone()
    {
        var map = MakeMap(
            MediaFragment(),
            SetFragment(1, 1, 0),
            SetFragment(2, 100, 1),
            SetFragment(3, 200, 2),
            new TapeMediaFragment { Ordinal = 4, StartBlock = 300, Kind = FragmentKind.TOC },
            new TapeMediaFragment { Ordinal = 5, StartBlock = 400, Kind = FragmentKind.TOC });

        Assert.Equal(3, map.SetCount);
        Assert.Equal(2, map.TocCopyCount);
        Assert.Equal(s_mediaId, map.MediaId);
        Assert.Equal(1, map.Volume);
    }

    /// <summary>
    /// The dominant real-world fault, detected with NO TOC whatsoever. Note the set's HEADER is perfectly
    ///  healthy — it is written at the set's start, before the first file — so only the missing closing
    ///  separator proves the backup never completed.
    /// </summary>
    [Fact]
    public void Map_LastSetUnclosed_DetectsTheDamagedTail()
    {
        var damaged = MakeMap(
            MediaFragment(),
            SetFragment(1, 1, 0),
            SetFragment(2, 100, 1, closed: false));

        Assert.True(damaged.LastSetUnclosed);

        var sound = MakeMap(MediaFragment(), SetFragment(1, 1, 0), SetFragment(2, 100, 1));

        Assert.False(sound.LastSetUnclosed);
    }

    /// <summary>
    /// A TOC copy following the last set must not mask an unclosed set: the property looks at the last
    ///  SET HEADER, not the last fragment. Without that distinction every damaged filemark-layout cartridge
    ///  carrying a stale TOC copy would read as healthy. A TOC copy after an unclosed set is a realistic case.
    /// </summary>
    [Fact]
    public void Map_LastSetUnclosed_IgnoresTrailingNonSetFragments()
    {
        var map = MakeMap(
            MediaFragment(),
            SetFragment(1, 1, 0),
            SetFragment(2, 100, 1, closed: false),
            new TapeMediaFragment { Ordinal = 3, StartBlock = 200, Kind = FragmentKind.TOC });

        Assert.True(map.LastSetUnclosed);
    }

    /// <summary>
    /// Another series has physically overwritten part of this cartridge. Reported as an OBSERVATION, never
    ///  a verdict (SM-3) — what it MEANS needs a TOC, and belongs to the comparison phase.
    /// </summary>
    [Fact]
    public void Map_MixedIdentity_NamesTheForeignSets()
    {
        var foreign = Guid.NewGuid();

        var map = MakeMap(
            MediaFragment(),
            SetFragment(1, 1, 0),
            SetFragment(2, 100, 1, mediaId: foreign),
            SetFragment(3, 200, 2));

        var mixed = map.MixedIdentityFragments;

        Assert.Single(mixed);
        Assert.Equal(foreign, mixed[0].Id);
    }

    /// <summary>
    /// With no media header there is nothing to disagree WITH, so the comparison is declined rather than
    ///  guessed — on legacy media every set would otherwise read as foreign.
    /// </summary>
    [Fact]
    public void Map_MixedIdentity_EmptyWithoutAMediaHeader()
    {
        var map = MakeMap(SetFragment(0, 0, 0, mediaId: Guid.NewGuid()));

        Assert.Empty(map.MixedIdentityFragments);
        Assert.Null(map.MediaId);
    }

    [Fact]
    public void Map_SetIndexGaps_NamesTheMissingIndices()
    {
        var map = MakeMap(
            MediaFragment(),
            SetFragment(1, 1, 0),
            SetFragment(2, 100, 1),
            SetFragment(3, 200, 4));       // 2 and 3 are missing from the middle

        Assert.Equal([2, 3], map.SetIndexGaps);
    }

    [Fact]
    public void Map_SetIndexGaps_EmptyWhenContiguous()
    {
        var map = MakeMap(MediaFragment(), SetFragment(1, 1, 0), SetFragment(2, 100, 1), SetFragment(3, 200, 2));

        Assert.Empty(map.SetIndexGaps);
    }

    /// <summary>
    /// A repeated or backwards index means something stranger than a missing set, and is deliberately NOT
    ///  reported as a gap — interpreting it belongs to the comparison phase, which has a TOC to judge by.
    /// </summary>
    [Fact]
    public void Map_SetIndexGaps_IgnoresRepeatsAndBackwardsSteps()
    {
        var map = MakeMap(
            MediaFragment(),
            SetFragment(1, 1, 5),
            SetFragment(2, 100, 5),
            SetFragment(3, 200, 2));

        Assert.Empty(map.SetIndexGaps);
    }

    /// <summary>SM-6: blank media is a clean, successful finding — never a failure.</summary>
    [Fact]
    public void Map_BlankMedia_IsEmptyAndUntruncated()
    {
        var map = MakeMap() with { Kind = ScannedMediaKind.Blank };

        Assert.Empty(map.Fragments);
        Assert.False(map.Truncated);
        Assert.Equal(0, map.SetCount);
        Assert.Null(map.MediaId);
        Assert.False(map.LastSetUnclosed);
    }

    #endregion

    #region *** (F) MediaScanMap — JSON round-trip ***

    /// <summary>
    /// Serializability is what buys "scan once, compare many": the comparison phase need not re-scan, a
    ///  user can attach a map to a support request, and a pre-repair scan becomes the before-picture.
    /// </summary>
    [Fact]
    public void Map_Json_RoundTripsEveryFragmentField()
    {
        var original = MakeMap(
            MediaFragment(),
            SetFragment(1, 1, 0),
            new TapeMediaFragment
            {
                Ordinal = 2,
                StartBlock = 2_455,
                Kind = FragmentKind.Unknown,
                BlockSpan = 17,
                ClosedBySeparator = true,
                Fingerprint = "4F120000",
                Diagnosis = TapeResult.Fail(13, "data error"),
            },
            new TapeMediaFragment
            {
                Ordinal = 3,
                StartBlock = 4_196,
                Kind = FragmentKind.TOC,
                TocVersion = 0x0102,
            },
            new TapeMediaFragment
            {
                Ordinal = 4,
                StartBlock = 4_600,
                Kind = FragmentKind.MarkRun,
                MarkCount = 3,
            }) with
        {
            Truncated = true,
            TerminatorWin32 = 1100,   // ERROR_END_OF_MEDIA
        };

        MediaScanMap? back = MediaScanMap.FromJson(original.ToJson());

        Assert.NotNull(back);
        Assert.Equal(original.Kind, back!.Kind);
        Assert.Equal(original.ScannedUtc, back.ScannedUtc);
        Assert.Equal(original.Truncated, back.Truncated);
        Assert.Equal(original.TerminatorWin32, back.TerminatorWin32);
        Assert.Equal(original.Layout, back.Layout);
        Assert.Equal(original.Fragments.Count, back.Fragments.Count);

        for (int i = 0; i < original.Fragments.Count; i++)
        {
            TapeMediaFragment a = original.Fragments[i], b = back.Fragments[i];

            Assert.Equal(a.Ordinal, b.Ordinal);
            Assert.Equal(a.StartBlock, b.StartBlock);
            Assert.Equal(a.Kind, b.Kind);
            Assert.Equal(a.BlockSpan, b.BlockSpan);
            Assert.Equal(a.ClosedBySeparator, b.ClosedBySeparator);
            Assert.Equal(a.Id, b.Id);
            Assert.Equal(a.Volume, b.Volume);
            Assert.Equal(a.VolumeSetIndex, b.VolumeSetIndex);
            Assert.Equal(a.GlobalSetIndex, b.GlobalSetIndex);
            Assert.Equal(a.Description, b.Description);
            Assert.Equal(a.CreatedUtc, b.CreatedUtc);
            Assert.Equal(a.TocVersion, b.TocVersion);
            Assert.Equal(a.MarkCount, b.MarkCount);
            Assert.Equal(a.Fingerprint, b.Fingerprint);
            Assert.Equal(a.Diagnosis, b.Diagnosis);
        }
    }

    /// <summary>The derived views survive the round-trip, since they are computed from the fragments.</summary>
    [Fact]
    public void Map_Json_PreservesTheDerivedViews()
    {
        var original = MakeMap(
            MediaFragment(),
            SetFragment(1, 1, 0),
            SetFragment(2, 100, 2, closed: false));

        MediaScanMap back = MediaScanMap.FromJson(original.ToJson())!;

        Assert.Equal(original.SetCount, back.SetCount);
        Assert.Equal(original.MediaId, back.MediaId);
        Assert.Equal(original.LastSetUnclosed, back.LastSetUnclosed);
        Assert.Equal(original.SetIndexGaps, back.SetIndexGaps);
    }

    /// <summary>
    /// A harvested TOC is excluded from JSON by design — it is saved beside the map as its own
    ///  <c>.tapetoc</c>, feeding the existing import path rather than bloating a diagnostic artifact.
    /// </summary>
    [Fact]
    public void Map_Json_ExcludesTheHarvestedToc()
    {
        var map = MakeMap(new TapeMediaFragment
        {
            Ordinal = 0,
            StartBlock = 4_196,
            Kind = FragmentKind.TOC,
            TocVersion = 0x0102,
            HarvestedToc = new TapeTOC("Harvested"),
        });

        string json = map.ToJson();
        Assert.DoesNotContain("Harvested", json, StringComparison.Ordinal);

        MediaScanMap back = MediaScanMap.FromJson(json)!;
        Assert.Null(back.Fragments[0].HarvestedToc);
        Assert.Equal((ushort)0x0102, back.Fragments[0].TocVersion);   // the cheap evidence still survives
    }

    /// <summary>A file that will not parse is not an exceptional condition — it is simply not a map.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{ \"Fragments\": ")]
    [InlineData("[1, 2, 3]")]
    public void Map_FromJson_Garbage_ReturnsNullWithoutThrowing(string json)
    {
        Assert.Null(MediaScanMap.FromJson(json));
    }

    #endregion
}
