using System.Buffers.Binary;
using System.IO.Hashing;

using TapeLibNET.Format;

namespace TapeLibNET.Tests;


/// <summary>Phase 1 (Design-Format-v2 §11.2): the format core in <c>TapeLibNET/Format/</c>.</summary>
public class FormatCoreTests
{
    private enum Mode : byte { Alpha = 0, Beta = 1 }

    // A small record exercising every schema shape.
    private sealed class Sample : ITapeRecord<Sample>
    {
        public Guid Id;
        public DateTime When;
        public uint Count = 5;
        public bool Flag;
        public Mode Mode = Mode.Alpha;
        public string Name = "";
        public string? Note;
        public List<long> Items = [];

        private static readonly TapeSchema<Sample> s_schema = new(TapeRecordKind.TocSet)
        {
            { 1, s => s.Id, (s, v) => s.Id = v, FieldFlags.Required },
            { 2, s => s.When, (s, v) => s.When = v, FieldFlags.Required },
            { 3, s => s.Count, (s, v) => s.Count = v, 5U },
            { 4, s => s.Flag, (s, v) => s.Flag = v },
            { 6, s => s.Mode, (s, v) => s.Mode = v, Mode.Alpha, FieldFlags.Critical },
            { 32, s => s.Name, (s, v) => s.Name = v, FieldFlags.Required },
            { 33, s => s.Note, (s, v) => s.Note = v },
        };

        static Sample()
        {
            // Repeated group 48 holding one varint (field 1) per item
            s_schema.AddCustom(48, FieldShape.Group,
                (w, s) =>
                {
                    foreach (var i in s.Items)
                    {
                        var g = w.BeginGroup(48);
                        g.WriteInt(1, i);
                        w.EndGroup(g);
                    }
                },
                (f, s) =>
                {
                    var g = f.ReadGroup();
                    Assert.True(g.MoveNext());
                    s.Items.Add(g.ReadInt());
                },
                flags: FieldFlags.Repeated);
        }

        public TapeRecordKind RecordKind => TapeRecordKind.TocSet;
        public void WriteBody(TapeFieldWriter fields) => s_schema.Write(fields, this);
        public static bool Accepts(TapeRecordKind kind) => kind == TapeRecordKind.TocSet;
        public static Sample ReadBody(TapeFieldReader fields) => s_schema.Read(fields, new Sample());
    }

    private static Sample MakeSample() => new()
    {
        Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        When = new DateTime(2024, 3, 1, 10, 0, 0, DateTimeKind.Utc),
        Count = 7,
        Flag = true,
        Mode = Mode.Beta,
        Name = "n\u00FCme \U0001F600",
        Note = "x",
        Items = [1, -2, 300_000_000_000],
    };

    private static byte[] Bytes(Action<TapeRecordWriter> write)
    {
        using var ms = new MemoryStream();
        using var w = new TapeRecordWriter(ms);
        write(w);
        return ms.ToArray();
    }

    private static byte[] RecordWith(Action<TapeFieldWriter> fill, TapeRecordKind kind = TapeRecordKind.TocSet)
        => Bytes(w => w.Write(kind, fill));

    // Raw record bytes with arbitrary kind / major (the writer refuses unregistered kinds).
    private static byte[] Raw(ushort kind, byte major, byte[] body)
    {
        var prologue = new byte[16];
        "TpN#"u8.CopyTo(prologue);
        BinaryPrimitives.WriteUInt16LittleEndian(prologue.AsSpan(4), kind);
        prologue[6] = major;
        prologue[7] = TapeFormat.Minor;
        int n = 8 + TapePrimitives.WriteVarUInt(prologue.AsSpan(8), (ulong)body.Length);
        return [.. prologue[..n], .. body];
    }

    private static Sample ReadOne(byte[] bytes) => TapeRecordReader.Parse(bytes, out _).Read<Sample>();

    private static void AssertRefused(FormatErrorKind kind, Action act)
        => Assert.Equal(kind, Assert.Throws<TapeFormatException>(act).Kind);

    #region *** Primitives ***

    [Theory]
    [InlineData(0UL)]
    [InlineData(127UL)]
    [InlineData(128UL)]
    [InlineData(16383UL)]
    [InlineData(16384UL)]
    [InlineData(uint.MaxValue)]
    [InlineData(ulong.MaxValue)]
    public void VarUInt_RoundTrips(ulong value)
    {
        Span<byte> buf = stackalloc byte[TapeFormat.MaxVarUIntBytes];
        int n = TapePrimitives.WriteVarUInt(buf, value);
        Assert.Equal(TapePrimitives.VarUIntSize(value), n);
        Assert.Equal(value, TapePrimitives.ReadVarUInt(buf[..n], out int consumed));
        Assert.Equal(n, consumed);
    }

    [Fact]
    public void VarUInt_MaxValue_Takes10Bytes()
        => Assert.Equal(10, TapePrimitives.VarUIntSize(ulong.MaxValue));

    [Fact]
    public void VarUInt_Overlong_Refused()
    {
        AssertRefused(FormatErrorKind.BadValue, () => TapePrimitives.ReadVarUInt([0x80, 0x00], out _));            // 0 in two bytes
        AssertRefused(FormatErrorKind.BadValue, () => TapePrimitives.ReadVarUInt([0xFF, 0x80, 0x00], out _));
    }

    [Fact]
    public void VarUInt_MoreThan64Bits_Refused()
    {
        byte[] eleven = [.. Enumerable.Repeat((byte)0xFF, 10), 0x01];
        AssertRefused(FormatErrorKind.BadValue, () => TapePrimitives.ReadVarUInt(eleven, out _));
        byte[] tenthTooBig = [.. Enumerable.Repeat((byte)0xFF, 9), 0x02];
        AssertRefused(FormatErrorKind.BadValue, () => TapePrimitives.ReadVarUInt(tenthTooBig, out _));
    }

    [Fact]
    public void VarUInt_Truncated_Refused()
        => AssertRefused(FormatErrorKind.Truncated, () => TapePrimitives.ReadVarUInt([0x80], out _));

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(1L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void VarInt_ZigZag_RoundTrips(long value)
    {
        Span<byte> buf = stackalloc byte[TapeFormat.MaxVarUIntBytes];
        int n = TapePrimitives.WriteVarInt(buf, value);
        Assert.Equal(value, TapePrimitives.ReadVarInt(buf[..n], out _));
    }

    [Fact]
    public void ZigZag_SmallMagnitudesStaySmall()
    {
        Assert.Equal(0UL, TapePrimitives.ZigZagEncode(0));
        Assert.Equal(1UL, TapePrimitives.ZigZagEncode(-1));
        Assert.Equal(2UL, TapePrimitives.ZigZagEncode(1));
    }

    [Fact]
    public void Bool_Strict()
    {
        Assert.False(TapePrimitives.DecodeBool([0]));
        Assert.True(TapePrimitives.DecodeBool([1]));
        AssertRefused(FormatErrorKind.BadValue, () => TapePrimitives.DecodeBool([2]));
        AssertRefused(FormatErrorKind.Underrun, () => TapePrimitives.DecodeBool([1, 1]));
        AssertRefused(FormatErrorKind.Truncated, () => TapePrimitives.DecodeBool([]));
    }

    [Fact]
    public void F64_RoundTrips_LittleEndian()
    {
        byte[] bytes = RecordWith(f => f.WriteF64(1, 0.05));
        var reader = TapeRecordReader.Parse(bytes, out _).Fields;
        Assert.True(reader.MoveNext());
        Assert.Equal(0.05, reader.ReadF64());
        Assert.Equal(BitConverter.GetBytes(0.05), reader.Value.ToArray());   // x86/x64 hosts are little-endian
    }

    [Fact]
    public void String_InvalidUtf8_Refused()
        => AssertRefused(FormatErrorKind.BadValue, () => TapePrimitives.DecodeString([0xC3, 0x28]));

    [Fact]
    public void String_LoneSurrogate_RefusedOnWrite()
        => AssertRefused(FormatErrorKind.BadValue, () => RecordWith(f => f.WriteString(32, "\uD800")));

    [Fact]
    public void Timestamp_ReadsAsUtc_WritersConvertToUniversal()
    {
        var local = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Local);
        byte[] bytes = RecordWith(f => f.WriteTimestamp(2, local));
        var r = TapeRecordReader.Parse(bytes, out _).Fields;
        Assert.True(r.MoveNext());
        var back = r.ReadTimestamp();
        Assert.Equal(DateTimeKind.Utc, back.Kind);
        Assert.Equal(local.ToUniversalTime().Ticks, back.Ticks);
    }

    [Fact]
    public void Guid_RoundTrips()
    {
        var g = Guid.NewGuid();
        var r = TapeRecordReader.Parse(RecordWith(f => f.WriteGuid(1, g)), out _).Fields;
        Assert.True(r.MoveNext());
        Assert.Equal(g, r.ReadGuid());
    }

    [Fact]
    public void ClampToUtf8Bytes_NeverSplitsSurrogatePair()
    {
        string s = "ab\U0001F600cd";   // 'a','b', 4-byte emoji, 'c','d'
        Assert.Equal("ab", TapePrimitives.ClampToUtf8Bytes(s, 5));
        Assert.Equal("ab\U0001F600", TapePrimitives.ClampToUtf8Bytes(s, 6));
        Assert.Equal(s, TapePrimitives.ClampToUtf8Bytes(s, 100));
        Assert.Equal("", TapePrimitives.ClampToUtf8Bytes(null, 10));
    }

    #endregion

    #region *** Prologue ***

    [Fact]
    public void Prologue_StartsWithMagicKindAndVersion()
    {
        byte[] bytes = RecordWith(f => f.WriteUInt(1, 1));
        Assert.True(TapeFormat.IsV2(bytes));
        Assert.Equal((ushort)TapeRecordKind.TocSet, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(2, bytes[6]);
        Assert.Equal(1, bytes[7]);
        Assert.Equal(bytes.Length - 9, bytes[8]);   // single-byte BodyLength for a tiny body
    }

    [Fact]
    public void Prologue_NoMagic_Refused()
        => AssertRefused(FormatErrorKind.BadMagic, () => TapeRecordReader.Parse("TF\0\0\0\0\0\0\0"u8, out _));

    [Fact]
    public void Prologue_UnknownKind_Refused()
        => AssertRefused(FormatErrorKind.UnknownKind, () => TapeRecordReader.Parse(Raw(0x0999, 2, []), out _));

    [Fact]
    public void Prologue_UnknownSkippableKind_IsSkipped()
    {
        byte[] skippable = Raw(0x8999, 2, [1, 2, 3]);
        byte[] real = RecordWith(f => f.WriteString(32, "x"));
        var record = TapeRecordReader.Parse([.. skippable, .. real], out int consumed);
        Assert.Equal(TapeRecordKind.TocSet, record.Kind);
        Assert.Equal(skippable.Length + real.Length, consumed);

        // The stream reader skips it too.
        using var ms = new MemoryStream([.. skippable, .. real]);
        Assert.Equal(TapeRecordKind.TocSet, new TapeRecordReader(ms).ReadRecord()!.Kind);
    }

    [Fact]
    public void Prologue_NewerMajor_Refused()
        => AssertRefused(FormatErrorKind.NewerMajor, () => TapeRecordReader.Parse(Raw((ushort)TapeRecordKind.TocSet, 3, []), out _));

    [Fact]
    public void Prologue_BodyLengthPastData_Truncated()
    {
        byte[] bytes = Raw((ushort)TapeRecordKind.TocSet, 2, new byte[10]);
        AssertRefused(FormatErrorKind.Truncated, () => TapeRecordReader.Parse(bytes.AsSpan(0, bytes.Length - 1), out _));
        using var ms = new MemoryStream(bytes, 0, bytes.Length - 1);
        AssertRefused(FormatErrorKind.Truncated, () => new TapeRecordReader(ms).ReadRecord());
    }

    [Fact]
    public void Prologue_BodyAboveLimit_Refused_WithoutAllocating()
    {
        var prologue = new byte[16];
        "TpN#"u8.CopyTo(prologue);
        BinaryPrimitives.WriteUInt16LittleEndian(prologue.AsSpan(4), (ushort)TapeRecordKind.TocSet);
        prologue[6] = 2;
        prologue[7] = 1;
        int n = 8 + TapePrimitives.WriteVarUInt(prologue.AsSpan(8), (ulong)TapeFormat.MaxRecordBody + 1);
        using var ms = new MemoryStream(prologue[..n]);
        AssertRefused(FormatErrorKind.LimitExceeded, () => new TapeRecordReader(ms).ReadRecord());
    }

    [Fact]
    public void Prologue_CleanEndOfStream_ReturnsNull()
    {
        using var ms = new MemoryStream();
        Assert.Null(new TapeRecordReader(ms).ReadRecord());
    }

    [Fact]
    public void Reader_NeverReadsAheadOfTheRecord()
    {
        byte[] record = RecordWith(f => f.WriteString(32, "x"));
        byte[] trailer = [9, 9, 9, 9];
        using var ms = new MemoryStream([.. record, .. trailer]);
        new TapeRecordReader(ms).ReadRecord();
        Assert.Equal(record.Length, ms.Position);
    }

    [Fact]
    public void FieldOverrun_Refused()
    {
        // Field 1 declares 5 value bytes but only 1 follows.
        byte[] body = [(1 << 1), 5, 0xFF];
        AssertRefused(FormatErrorKind.Overrun, () =>
        {
            var f = TapeRecordReader.Parse(Raw((ushort)TapeRecordKind.TocSet, 2, body), out _).Fields;
            f.MoveNext();
        });
    }

    [Fact]
    public void ValueUnderrun_Refused()
    {
        // A bool field of two bytes.
        byte[] body = [(4 << 1), 2, 1, 1];
        AssertRefused(FormatErrorKind.Underrun, () =>
        {
            var f = TapeRecordReader.Parse(Raw((ushort)TapeRecordKind.TocSet, 2, body), out _).Fields;
            f.MoveNext();
            f.ReadBool();
        });
    }

    #endregion

    #region *** Fields and schema ***

    [Fact]
    public void Schema_RoundTrips_AllShapes()
    {
        var original = MakeSample();
        var back = ReadOne(Bytes(w => w.Write(original)));

        Assert.Equal(original.Id, back.Id);
        Assert.Equal(original.When, back.When);
        Assert.Equal(DateTimeKind.Utc, back.When.Kind);
        Assert.Equal(7U, back.Count);
        Assert.True(back.Flag);
        Assert.Equal(Mode.Beta, back.Mode);
        Assert.Equal(original.Name, back.Name);
        Assert.Equal("x", back.Note);
        Assert.Equal(original.Items, back.Items);
    }

    [Fact]
    public void Schema_EmitsAscendingTags()
    {
        var r = TapeRecordReader.Parse(Bytes(w => w.Write(MakeSample())), out _).Fields;
        int last = 0;
        while (r.MoveNext())
        {
            Assert.True(r.Number >= last, $"field {r.Number} after {last}");
            last = r.Number;
        }
    }

    [Fact]
    public void Schema_ElidesOptionalDefaults_ButNeverRequired()
    {
        var s = new Sample { Id = Guid.Empty, When = DateTime.MinValue, Name = "" };   // Count = 5, Flag = false, Mode = Alpha, Note = null
        var numbers = new List<int>();
        var r = TapeRecordReader.Parse(Bytes(w => w.Write(s)), out _).Fields;
        while (r.MoveNext())
            numbers.Add(r.Number);

        Assert.Equal([1, 2, 32], numbers);   // required Id, When, Name only - even though empty
    }

    [Fact]
    public void Schema_RequiredEmptyString_RoundTrips()
    {
        var back = ReadOne(Bytes(w => w.Write(new Sample { Id = Guid.NewGuid(), When = DateTime.UtcNow, Name = "" })));
        Assert.Equal("", back.Name);
    }

    [Fact]
    public void Schema_ReadsFieldsInAnyOrder()
    {
        var id = Guid.NewGuid();
        // The writer enforces ascending numbers, so concatenate separately written single-field bodies in descending order
        static byte[] BodyOf(Action<TapeFieldWriter> fill) => TapeRecordReader.Parse(RecordWith(fill), out _).Body.ToArray();
        byte[] bytes = Raw((ushort)TapeRecordKind.TocSet, 2,
        [
            .. BodyOf(f => f.WriteString(32, "late")),
            .. BodyOf(f => f.WriteTimestamp(2, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc))),
            .. BodyOf(f => f.WriteGuid(1, id)),
        ]);
        var back = ReadOne(bytes);
        Assert.Equal(id, back.Id);
        Assert.Equal("late", back.Name);
    }

    // Required fields with extra fields slotted in ascending order between fields 2 and 32
    private static void WriteRequiredWith(TapeFieldWriter f, Action<TapeFieldWriter> mid)
    {
        f.WriteGuid(1, Guid.NewGuid());
        f.WriteTimestamp(2, DateTime.UtcNow);
        mid(f);
        f.WriteString(32, "n");
    }

    private static void WriteRequired(TapeFieldWriter f)
    {
        f.WriteGuid(1, Guid.NewGuid());
        f.WriteTimestamp(2, DateTime.UtcNow);
        f.WriteString(32, "n");
    }

    [Fact]
    public void Schema_UnknownNonCriticalField_Skipped()
    {
        var back = ReadOne(RecordWith(f => { WriteRequired(f); f.WriteUInt(40, 99); }));
        Assert.Equal("n", back.Name);
    }

    [Fact]
    public void Schema_UnknownCriticalField_Refused()
        => AssertRefused(FormatErrorKind.UnknownCritical, () => ReadOne(RecordWith(f => { WriteRequired(f); f.WriteUInt(40, 99, critical: true); })));

    [Fact]
    public void Schema_KnownField_MatchesIgnoringCriticalBit()
    {
        // Field 3 is non-critical in the schema; a writer that sets the bit must still be understood (R3).
        var back = ReadOne(RecordWith(f =>
        {
            f.WriteGuid(1, Guid.NewGuid());
            f.WriteTimestamp(2, DateTime.UtcNow);
            f.WriteUInt(3, 9, critical: true);
            f.WriteString(32, "n");
        }));
        Assert.Equal(9U, back.Count);
    }

    [Fact]
    public void Schema_DuplicateField_Refused()
        => AssertRefused(FormatErrorKind.Duplicate, () => ReadOne(RecordWith(f => WriteRequiredWith(f, g => { g.WriteUInt(3, 1); g.WriteUInt(3, 2); }))));

    [Fact]
    public void Schema_RepeatedField_AllowsManyOccurrences()
    {
        var back = ReadOne(RecordWith(f =>
                {
                    WriteRequired(f);
                    foreach (long v in new long[] { 1, 2 })
                    {
                        var g = f.BeginGroup(48);
                        g.WriteInt(1, v);
                        f.EndGroup(g);
                    }
                }));
        Assert.Equal([1L, 2L], back.Items);
    }

    [Fact]
    public void Schema_MissingRequired_Refused()
        => AssertRefused(FormatErrorKind.MissingRequired, () => ReadOne(RecordWith(f =>
        {
            f.WriteGuid(1, Guid.NewGuid());
            f.WriteString(32, "n");      // When (2) is missing
        })));

    [Fact]
    public void Schema_UndefinedEnumValue_Refused()
        => AssertRefused(FormatErrorKind.BadValue, () => ReadOne(RecordWith(f => WriteRequiredWith(f, g => g.WriteUInt(6, 77)))));

    [Fact]
    public void Schema_EnumAboveItsRange_Refused()
        => AssertRefused(FormatErrorKind.BadValue, () => ReadOne(RecordWith(f => WriteRequiredWith(f, g => g.WriteUInt(6, 256)))));

    [Fact]
    public void Schema_CriticalEnum_IsWrittenWithTheCriticalBit()
    {
        var r = TapeRecordReader.Parse(Bytes(w => w.Write(MakeSample())), out _).Fields;
        while (r.MoveNext())
            if (r.Number == 6)
            {
                Assert.True(r.IsCritical);
                return;
            }
        Assert.Fail("Mode field not written");
    }

    [Fact]
    public void Schema_DuplicateDeclaration_Throws()
    {
        var schema = new TapeSchema<Sample>(TapeRecordKind.TocSet);
        schema.Add(1, s => s.Flag, (s, v) => s.Flag = v);
        schema.Add(1, s => s.Flag, (s, v) => s.Flag = v);
        // Duplicates are detected when the schema freezes on first use (Appendix B §B.3.5)
        Assert.Throws<InvalidOperationException>(() => Bytes(w => w.Write(TapeRecordKind.TocSet, f => schema.Write(f, new Sample()))));
    }

    [Fact]
    public void Record_OfAnotherKind_Refused()
        => AssertRefused(FormatErrorKind.UnexpectedKind, () => TapeRecordReader.Parse(RecordWith(WriteRequired, TapeRecordKind.TocHeader), out _).Read<Sample>());

    #endregion

    #region *** Limits (§4.6) ***

    [Fact]
    public void Limits_StringAboveMax_RefusedOnWrite()
        => AssertRefused(FormatErrorKind.LimitExceeded, () => RecordWith(f => f.WriteString(32, new string('a', TapeFormat.MaxStringBytes + 1))));

    [Fact]
    public void Limits_StringAtMax_RoundTrips()
    {
        var back = ReadOne(RecordWith(f =>
        {
            WriteRequired(f);
            f.WriteString(33, new string('a', TapeFormat.MaxStringBytes));
        }));
        Assert.Equal(TapeFormat.MaxStringBytes, back.Note!.Length);
    }

    [Fact]
    public void Limits_StringAboveMax_RefusedOnRead()
    {
        // Hand-built oversized value: the writer would refuse, so bypass it with a bytes field of the same wire shape.
        byte[] bytes = RecordWith(f => f.WriteBytes(33, new byte[TapeFormat.MaxStringBytes + 1]));
        AssertRefused(FormatErrorKind.LimitExceeded, () =>
        {
            var r = TapeRecordReader.Parse(bytes, out _).Fields;
            r.MoveNext();
            r.ReadString();
        });
    }

    [Fact]
    public void Limits_RecordBodyAboveMax_RefusedOnWrite()
        => AssertRefused(FormatErrorKind.LimitExceeded, () => RecordWith(f =>
        {
            for (int i = 0; i < 3; i++)
                f.WriteBytes(48, new byte[TapeFormat.MaxBytesField]);   // 3 x 16 MiB > 16 MiB record body
        }));

    [Fact]
    public void Limits_GroupNesting_FourLevelsOk_FifthRefused()
    {
        static void Nest(TapeFieldWriter f, int levels)
        {
            if (levels == 0)
                f.WriteUInt(1, 1);
            else
                { var g = f.BeginGroup(48); Nest(g, levels - 1); f.EndGroup(g); }
        }

        byte[] ok = RecordWith(f => Nest(f, TapeFormat.MaxGroupDepth));
        var r = TapeRecordReader.Parse(ok, out _).Fields;
        for (int depth = 0; depth < TapeFormat.MaxGroupDepth; depth++)
        {
            Assert.True(r.MoveNext());
            r = r.ReadGroup();
        }
        Assert.True(r.MoveNext());
        Assert.Equal(1UL, r.ReadUInt());

        Assert.Throws<InvalidOperationException>(() => RecordWith(f => Nest(f, TapeFormat.MaxGroupDepth + 1)));
    }

    [Fact]
    public void Limits_GroupNesting_DeepInputRefusedOnRead()
    {
        // Craft 5 nested groups by hand (the writer refuses to).
        byte[] inner = [(1 << 1), 1, 1];
        for (int i = 0; i < TapeFormat.MaxGroupDepth + 1; i++)
            inner = [(48 << 1), (byte)inner.Length, .. inner];

        AssertRefused(FormatErrorKind.LimitExceeded, () =>
        {
            var r = TapeRecordReader.Parse(Raw((ushort)TapeRecordKind.TocSet, 2, inner), out _).Fields;
            while (r.MoveNext())
                r = r.ReadGroup();
        });
    }

    #endregion

    #region *** Front coder ***

    [Fact]
    public void FrontCoder_SharesPrefix_AndRoundTrips()
    {
        string[] names = [@"C:\data\a\one.txt", @"C:\data\a\two.txt", @"C:\data\b\three.txt", "x"];
        var enc = new TapeNameFrontCoder();
        var dec = new TapeNameFrontCoder();

        var shared = new List<int>();
        foreach (string name in names)
        {
            string suffix = enc.Encode(name, out int n).ToString();
            shared.Add(n);
            Assert.Equal(name, dec.Decode(n, suffix));
        }
        Assert.Equal(0, shared[0]);
        Assert.Equal(@"C:\data\a\".Length, shared[1]);
        Assert.Equal(@"C:\data\".Length, shared[2]);
    }

    [Fact]
    public void FrontCoder_NeverSplitsSurrogatePair()
    {
        // "\U0001F600" = D83D DE00; "\U0001F601" = D83D DE01 - they share the high surrogate only.
        var enc = new TapeNameFrontCoder();
        enc.Encode("a\U0001F600", out _);
        string suffix = enc.Encode("a\U0001F601", out int shared).ToString();

        Assert.Equal(1, shared);
        Assert.Equal("\U0001F601", suffix);
    }

    [Fact]
    public void FrontCoder_Reset_StartsAFreshBatch()
    {
        var enc = new TapeNameFrontCoder();
        enc.Encode("abc", out _);
        enc.Reset();
        enc.Encode("abd", out int shared);
        Assert.Equal(0, shared);
    }

    [Fact]
    public void FrontCoder_Decode_RefusesBadShared()
    {
        var dec = new TapeNameFrontCoder();
        dec.Decode(0, "ab");
        AssertRefused(FormatErrorKind.BadValue, () => dec.Decode(3, "x"));
        AssertRefused(FormatErrorKind.BadValue, () => new TapeNameFrontCoder().Decode(-1, "x"));
    }

    [Fact]
    public void FrontCoder_IdenticalNames_ShareEverything()
    {
        var enc = new TapeNameFrontCoder();
        enc.Encode("same", out _);
        string suffix = enc.Encode("same", out int shared).ToString();
        Assert.Equal(4, shared);
        Assert.Equal("", suffix);
    }

    #endregion

    #region *** Sample coder ***

    [Fact]
    public void SampleCoder_RoundTrips_AndIsSmallerThanFixed16Bytes()
    {
        var samples = Enumerable.Range(0, 200)
            .Select(i => ((long)i * 1_000_000, 18_000_000_000_000L - i * 1_000_000L)).ToList();

        byte[] coded = TapeSampleCoder.Encode(samples);
        Assert.Equal(samples, TapeSampleCoder.Decode(coded));
        Assert.True(coded.Length * 2 <= samples.Count * 16, $"{coded.Length} bytes for {samples.Count} samples");
    }

    [Fact]
    public void SampleCoder_Empty_RoundTrips()
        => Assert.Empty(TapeSampleCoder.Decode(TapeSampleCoder.Encode([])));

    [Fact]
    public void SampleCoder_NonMonotone_Rejected()
        => Assert.Throws<ArgumentException>(() => TapeSampleCoder.Encode([(10, 0), (5, 0)]));

    [Fact]
    public void SampleCoder_ReportedMayRiseAndFall()
    {
        List<(long, long)> samples = [(1, 100), (2, 50), (3, 200), (3, -5)];
        Assert.Equal(samples, TapeSampleCoder.Decode(TapeSampleCoder.Encode(samples)));
    }

    [Fact]
    public void SampleCoder_LyingCount_Refused()
        => AssertRefused(FormatErrorKind.Truncated, () => TapeSampleCoder.Decode([0xFF, 0xFF, 0xFF, 0x7F, 0, 0]));

    [Fact]
    public void SampleCoder_TrailingBytes_Refused()
    {
        byte[] coded = [.. TapeSampleCoder.Encode([(1, 1)]), 0];
        AssertRefused(FormatErrorKind.Underrun, () => TapeSampleCoder.Decode(coded));
    }

    #endregion

    #region *** CRC envelope ***

    private static byte[] Envelope(params Sample[] samples)
    {
        using var ms = new MemoryStream();
        TapeCrc64Envelope.Write(ms, s =>
        {
            using var w = new TapeRecordWriter(s);
            foreach (var sample in samples)
                w.Write(sample);
        });
        return ms.ToArray();
    }

    private static List<Sample> ReadEnvelope(Stream stream, int count)
        => TapeCrc64Envelope.Read(stream, s =>
        {
            var reader = new TapeRecordReader(s);
            var list = new List<Sample>();
            for (int i = 0; i < count; i++)
                list.Add(reader.Read<Sample>());
            return list;
        });

    [Fact]
    public void Envelope_RoundTrips_AndTrailerIsCrc64OfTheBody()
    {
        byte[] bytes = Envelope(MakeSample(), MakeSample());
        var crc = new Crc64();
        crc.Append(bytes.AsSpan(0, bytes.Length - 8));
        Assert.Equal(crc.GetCurrentHash(), bytes[^8..]);

        using var ms = new MemoryStream(bytes);
        Assert.Equal(2, ReadEnvelope(ms, 2).Count);
        Assert.Equal(bytes.Length, ms.Position);
    }

    [Fact]
    public void Envelope_FlippedBodyByte_CrcMismatch()
    {
        byte[] bytes = Envelope(MakeSample());
        bytes[bytes.Length / 2] ^= 0x01;
        using var ms = new MemoryStream(bytes);
        var ex = Assert.Throws<TapeFormatException>(() => ReadEnvelope(ms, 1));
        Assert.True(ex.Kind is FormatErrorKind.CrcMismatch or FormatErrorKind.Truncated or FormatErrorKind.BadValue
            or FormatErrorKind.Overrun or FormatErrorKind.Underrun or FormatErrorKind.MissingRequired
            or FormatErrorKind.UnknownCritical or FormatErrorKind.Duplicate, ex.Kind.ToString());
    }

    [Fact]
    public void Envelope_FlippedTrailerByte_CrcMismatch()
    {
        byte[] bytes = Envelope(MakeSample());
        bytes[^1] ^= 0x01;
        using var ms = new MemoryStream(bytes);
        AssertRefused(FormatErrorKind.CrcMismatch, () => ReadEnvelope(ms, 1));
    }

    [Fact]
    public void Envelope_Truncated_Refused()
    {
        byte[] bytes = Envelope(MakeSample());
        using var noTrailer = new MemoryStream(bytes, 0, bytes.Length - 8);
        AssertRefused(FormatErrorKind.Truncated, () => ReadEnvelope(noTrailer, 1));

        using var partial = new MemoryStream(bytes, 0, bytes.Length - 3);
        AssertRefused(FormatErrorKind.Truncated, () => ReadEnvelope(partial, 1));
    }

    [Fact]
    public void Envelope_NoReadAhead_BytesAfterTrailerUntouched()
    {
        byte[] bytes = [.. Envelope(MakeSample()), 0xAA, 0xBB];
        using var ms = new MemoryStream(bytes);
        ReadEnvelope(ms, 1);
        Assert.Equal(bytes.Length - 2, ms.Position);
        Assert.Equal(0xAA, ms.ReadByte());
    }

    #endregion

    #region *** Frames ***

    [Fact]
    public void Frame_Block_RoundTrips()
    {
        byte[] block = TapeFrame.PackBlock(MakeSample(), 4096);
        Assert.Equal(4096, block.Length);
        Assert.Equal(TapeFrameStatus.Ok, TapeFrame.TryUnpack(block, out Sample? back));
        Assert.Equal(MakeSample().Name, back!.Name);
    }

    [Fact]
    public void Frame_Block_RandomPadding_IsIgnored()
    {
        byte[] block = TapeFrame.PackBlock(MakeSample(), 4096, new Random(1));
        Assert.Equal(TapeFrameStatus.Ok, TapeFrame.TryUnpack(block, out Sample? _));
    }

    [Fact]
    public void Frame_Status_NotFramed()
    {
        Assert.Equal(TapeFrameStatus.NotFramed, TapeFrame.TryUnpack(new byte[512], out Sample? _));
        Assert.Equal(TapeFrameStatus.NotFramed, TapeFrame.TryUnpack(new byte[2], out Sample? _));

        byte[] frame = TapeFrame.Pack(MakeSample());
        Assert.Equal(TapeFrameStatus.NotFramed, TapeFrame.TryUnpack(frame.AsSpan(0, frame.Length - 9), out Sample? _));   // length runs past the bytes
    }

    [Fact]
    public void Frame_Status_CrcMismatch()
    {
        byte[] block = TapeFrame.PackBlock(MakeSample(), 4096);
        block[20] ^= 0x01;
        Assert.Equal(TapeFrameStatus.CrcMismatch, TapeFrame.TryUnpack(block, out Sample? _));
    }

    [Fact]
    public void Frame_Status_Unparseable_ForEveryIntactButUnreadableCase()
    {
        static byte[] Seal(byte[] record) => [.. record, .. TapeFrame.ComputeCrc(record)];

        // newer major
        Assert.Equal(TapeFrameStatus.Unparseable,
            TapeFrame.TryUnpack(Seal(Raw((ushort)TapeRecordKind.TocSet, 3, [])), out Sample? _));
        // unknown kind
        Assert.Equal(TapeFrameStatus.Unparseable,
            TapeFrame.TryUnpack(Seal(Raw(0x0999, 2, [])), out Sample? _));
        // unknown critical tag
        Assert.Equal(TapeFrameStatus.Unparseable,
            TapeFrame.TryUnpack(Seal(RecordWith(f => { WriteRequired(f); f.WriteUInt(40, 1, critical: true); })), out Sample? _));
        // missing required field
        Assert.Equal(TapeFrameStatus.Unparseable,
            TapeFrame.TryUnpack(Seal(RecordWith(f => f.WriteString(32, "n"))), out Sample? _));
        // bad enum value
        Assert.Equal(TapeFrameStatus.Unparseable,
            TapeFrame.TryUnpack(Seal(RecordWith(f => WriteRequiredWith(f, g => g.WriteUInt(6, 77)))), out Sample? _));
        // a different (known) kind than asked for
        Assert.Equal(TapeFrameStatus.Unparseable,
            TapeFrame.TryUnpack(Seal(RecordWith(WriteRequired, TapeRecordKind.TocHeader)), out Sample? _));
    }

    [Fact]
    public void Frame_Inline_RoundTrips_AndLeavesTheBodyUntouched()
    {
        using var ms = new MemoryStream();
        int frameLength = TapeFrame.WriteInline(ms, MakeSample());
        byte[] body = [1, 2, 3, 4, 5];
        ms.Write(body);

        ms.Position = 0;
        var back = TapeFrame.ReadInline<Sample>(ms);
        Assert.Equal(MakeSample().Id, back.Id);
        Assert.Equal(frameLength, ms.Position);

        var rest = new byte[body.Length];
        ms.ReadExactly(rest);
        Assert.Equal(body, rest);
    }

    [Fact]
    public void Frame_Inline_CorruptCrc_Refused()
    {
        using var ms = new MemoryStream();
        TapeFrame.WriteInline(ms, MakeSample());
        byte[] bytes = ms.ToArray();
        bytes[^1] ^= 1;
        using var bad = new MemoryStream(bytes);
        AssertRefused(FormatErrorKind.CrcMismatch, () => TapeFrame.ReadInline<Sample>(bad));
    }

    #endregion

    #region *** Peek stream ***

    [Fact]
    public void PeekStream_ReplaysPeekedBytes()
    {
        byte[] data = RecordWith(f => f.WriteString(32, "x"));
        using var inner = new MemoryStream(data);
        using var peek = TapePeekStream.Wrap(inner, out ReadOnlySpan<byte> magic);
        Assert.True(TapeFormat.IsV2(magic));

        using var copy = new MemoryStream();
        peek.CopyTo(copy);
        Assert.Equal(data, copy.ToArray());
    }

    [Fact]
    public void PeekStream_ShortStream_PeeksWhatThereIs()
    {
        using var inner = new MemoryStream([1, 2]);
        using var peek = TapePeekStream.Wrap(inner, 4);
        Assert.Equal([1, 2], peek.Peeked.ToArray());
        Assert.False(TapeFormat.IsV2(peek.Peeked));

        using var copy = new MemoryStream();
        peek.CopyTo(copy);
        Assert.Equal([1, 2], copy.ToArray());
    }

    [Fact]
    public void PeekStream_LegacyBytes_AreNotV2()
        => Assert.False(TapeFormat.IsV2("TF\u0001\u0001"u8));

    #endregion
}
