using System.Buffers.Binary;
using System.Collections;
using System.Reflection;

using TapeLibNET.Format;

namespace TapeLibNET.Tests;


/// <summary>
/// Appendix B (Revised) §B.4 R6: the format-core coverage <see cref="FormatCoreTests"/> does not reach - schema
///  validation, overload binding, defaults, inheritance, polymorphic records, frame details, forward-only streams,
///  the batch allocation budget, and regressions for review fixes F1-F7 (Format-Core-Review-R1-R5.md).
/// </summary>
/// <remarks>
/// Requires F1-F7. Without F3, <see cref="FieldWriter_DescendingNumber_Throws"/> trips a <c>Debug.Assert</c>,
///  which fail-fasts the test host.
/// </remarks>
public class FormatCoreCoverageTests
{
    #region *** Test types ***

    private enum Mode : byte { Alpha = 0, Beta = 1 }

    // Group schema: a nested child for single and repeated groups
    private sealed class Child
    {
        public uint V;
        public string S = "";

        public static readonly TapeSchema<Child> Schema = new()
        {
            { 1,  c => c.V, (c, x) => c.V = x, FieldFlags.Required },
            { 32, c => c.S, (c, x) => c.S = x },
        };
    }

    // One property per supported CLR type: the property's type alone must pick the codec (§B.3.4)
    private sealed class Prim
    {
        public bool Bo;
        public byte By;
        public ushort Us;
        public uint Ui;
        public ulong Ul;
        public int I;
        public long L;
        public double D;
        public Guid G;
        public DateTime T;
        public Mode M;
        public FileAttributes A;
        public string S = "";
        public byte[]? B;
        public Child? One;
        public List<Child> Many = [];

        public static readonly TapeSchema<Prim> Schema = new()
        {
            // ── scalars 1–31 ──
            { 1,  p => p.Bo, (p, x) => p.Bo = x },
            { 2,  p => p.By, (p, x) => p.By = x },
            { 3,  p => p.Us, (p, x) => p.Us = x },
            { 4,  p => p.Ui, (p, x) => p.Ui = x },
            { 5,  p => p.Ul, (p, x) => p.Ul = x },
            { 6,  p => p.I,  (p, x) => p.I = x },
            { 7,  p => p.L,  (p, x) => p.L = x },
            { 8,  p => p.D,  (p, x) => p.D = x },
            { 9,  p => p.G,  (p, x) => p.G = x },
            { 10, p => p.T,  (p, x) => p.T = x },
            { 11, p => p.M,  (p, x) => p.M = x, Mode.Alpha, FieldFlags.Critical },
            { 12, p => p.A,  (p, x) => p.A = x, FieldFlags.Bitmask },
            // ── blobs 32–47 ──
            { 32, p => p.S,  (p, x) => p.S = x },
            { 33, p => p.B,  (p, x) => p.B = x },
            // ── groups 48–63 ──
            { 48, p => p.One,  (p, x) => p.One = x,     Child.Schema },
            { 49, p => p.Many, (p, x) => p.Many.Add(x), Child.Schema },
        };
    }

    // Constructor values deliberately differ from the schema defaults
    private sealed class Defaults
    {
        public uint X = 99;
        public DateTime T = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public Mode M = Mode.Beta;
        public uint WithDefault = 1;
        public string S = "ctor";
        public byte[]? B = [1, 2];

        public static readonly TapeSchema<Defaults> Schema = new()
        {
            { 1,  d => d.X,           (d, x) => d.X = x },
            { 2,  d => d.T,           (d, x) => d.T = x },
            { 3,  d => d.M,           (d, x) => d.M = x },
            { 4,  d => d.WithDefault, (d, x) => d.WithDefault = x, 7U },
            { 32, d => d.S,           (d, x) => d.S = x },
            { 33, d => d.B,           (d, x) => d.B = x },
        };
    }

    // Target for schema-validation tests
    private sealed class Vw
    {
        public uint U;
        public string S = "";
        public Child? C;
    }

    // Shared header tags, inherited by a derived wire type (§B.3.6)
    private class BaseWire
    {
        public Guid Id;
        public uint Size = 3;

        public static readonly TapeSchema<BaseWire> Shared = new()
        {
            { 1, b => b.Id,   (b, x) => b.Id = x,   FieldFlags.Required },
            { 2, b => b.Size, (b, x) => b.Size = x, 3U },
        };
    }

    private sealed class DerivedWire : BaseWire
    {
        public bool Flag;
        public string Name = "";

        public static readonly TapeSchema<DerivedWire> Schema = new(inherits: Shared)
        {
            { 4,  d => d.Flag, (d, x) => d.Flag = x },
            { 32, d => d.Name, (d, x) => d.Name = x, FieldFlags.Required },
        };
    }

    // A record with a cross-field rule (G11)
    private sealed class Ranged : ITapeRecord<Ranged>
    {
        public uint Lo;
        public uint Hi;

        private static readonly TapeSchema<Ranged> s_schema = new(TapeRecordKind.TocEnd,
            validate: r => r.Lo > r.Hi ? "Lo above Hi" : null)
        {
            { 1, r => r.Lo, (r, x) => r.Lo = x },
            { 2, r => r.Hi, (r, x) => r.Hi = x },
        };

        public TapeRecordKind RecordKind => TapeRecordKind.TocEnd;
        public void WriteBody(TapeFieldWriter fields) => s_schema.Write(fields, this);
        public static bool Accepts(TapeRecordKind kind) => kind == TapeRecordKind.TocEnd;
        public static Ranged ReadBody(TapeFieldReader fields) => s_schema.Read(fields, new Ranged());
    }

    // Polymorphic base over two kinds; subtypes re-expose the typed static members (§B.3.7)
    private abstract class Shape : ITapeRecord<Shape>
    {
        public string Label = "";

        public abstract TapeRecordKind RecordKind { get; }

        public void WriteBody(TapeFieldWriter fields) => fields.WriteString(32, Label);

        public static bool Accepts(TapeRecordKind kind) => kind is TapeRecordKind.TocHeader or TapeRecordKind.TocEnd;

        public static Shape ReadBody(TapeFieldReader fields)
        {
            Shape shape = fields.Record.Kind switch
            {
                TapeRecordKind.TocHeader => new Circle(),
                TapeRecordKind.TocEnd => new Square(),
                _ => throw fields.Error(FormatErrorKind.UnexpectedKind, "not a shape"),
            };
            while (fields.MoveNext())
            {
                if (fields.Number == 32)
                    shape.Label = fields.ReadString();
                else
                    fields.SkipUnknown();
            }
            return shape;
        }
    }

    private sealed class Circle : Shape, ITapeRecord<Circle>
    {
        public override TapeRecordKind RecordKind => TapeRecordKind.TocHeader;
        public static new bool Accepts(TapeRecordKind kind) => kind == TapeRecordKind.TocHeader;
        public static new Circle ReadBody(TapeFieldReader fields) => (Circle)Shape.ReadBody(fields);
    }

    private sealed class Square : Shape, ITapeRecord<Square>
    {
        public override TapeRecordKind RecordKind => TapeRecordKind.TocEnd;
        public static new bool Accepts(TapeRecordKind kind) => kind == TapeRecordKind.TocEnd;
        public static new Square ReadBody(TapeFieldReader fields) => (Square)Shape.ReadBody(fields);
    }

    // ReadBody fails the way domain conversion code does (F7)
    private sealed class Boom : ITapeRecord<Boom>
    {
        public TapeRecordKind RecordKind => TapeRecordKind.TocEnd;
        public void WriteBody(TapeFieldWriter fields) => fields.WriteUInt(1, 1);
        public static bool Accepts(TapeRecordKind kind) => kind == TapeRecordKind.TocEnd;
        public static Boom ReadBody(TapeFieldReader fields) => throw new ArgumentException("domain refuses the value");
    }

    // A framed record with a stub legacy reader: 0x42 at byte 0 means "legacy record"
    private sealed class Legacyish : ITapeFramedRecord<Legacyish>
    {
        public static int LegacyCalls;
        public string Origin = "";

        public TapeRecordKind RecordKind => TapeRecordKind.SetHeader;
        public void WriteBody(TapeFieldWriter fields) => fields.WriteString(32, Origin);
        public static bool Accepts(TapeRecordKind kind) => kind == TapeRecordKind.SetHeader;

        public static Legacyish ReadBody(TapeFieldReader fields)
        {
            var record = new Legacyish();
            while (fields.MoveNext())
            {
                if (fields.Number == 32)
                    record.Origin = fields.ReadString();
                else
                    fields.SkipUnknown();
            }
            return record;
        }

        public static TapeFrameStatus TryReadLegacy(ReadOnlySpan<byte> block, out Legacyish? record)
        {
            LegacyCalls++;
            record = block.Length > 0 && block[0] == 0x42 ? new Legacyish { Origin = "legacy" } : null;
            return record is null ? TapeFrameStatus.NotFramed : TapeFrameStatus.Ok;
        }
    }

    // A TOC file entry stand-in for the allocation budget (§B.8.4)
    private sealed class Entry
    {
        public ulong Id;
        public ulong Block;
        public ulong Offset;
        public ulong Length;
        public FileAttributes Attr;
        public DateTime Created;
        public DateTime Written;
        public string Suffix = "";
        public byte[]? Hash;

        public static readonly TapeSchema<Entry> Schema = new()
        {
            { 1,  e => e.Id,      (e, x) => e.Id = x,      FieldFlags.Required },
            { 2,  e => e.Block,   (e, x) => e.Block = x,   FieldFlags.Required },
            { 3,  e => e.Offset,  (e, x) => e.Offset = x },
            { 4,  e => e.Length,  (e, x) => e.Length = x },
            { 6,  e => e.Attr,    (e, x) => e.Attr = x,    FieldFlags.Bitmask },
            { 8,  e => e.Created, (e, x) => e.Created = x, FieldFlags.Required },
            { 9,  e => e.Written, (e, x) => e.Written = x, FieldFlags.Required },
            { 32, e => e.Suffix,  (e, x) => e.Suffix = x,  FieldFlags.Required },
            { 33, e => e.Hash,    (e, x) => e.Hash = x },
        };
    }

    #endregion

    #region *** Streams ***

    // Non-seekable source; optionally returns at most maxChunk bytes per Read (short reads)
    private sealed class ForwardOnlyStream(byte[] data, int maxChunk = int.MaxValue) : Stream
    {
        private int m_pos;

        public bool Disposed { get; private set; }

        /// <summary>Bytes not consumed yet.</summary>
        public byte[] Rest() => data[m_pos..];

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = Math.Min(Math.Min(buffer.Length, maxChunk), data.Length - m_pos);
            data.AsSpan(m_pos, n).CopyTo(buffer);
            m_pos += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    // Non-seekable sink
    private sealed class ForwardOnlySink : Stream
    {
        private readonly MemoryStream m_inner = new();

        public bool Disposed { get; private set; }

        public byte[] ToArray() => m_inner.ToArray();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => m_inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => m_inner.Write(buffer);
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    #endregion

    #region *** Helpers ***

    private static byte[] RecordWith(Action<TapeFieldWriter> fill, TapeRecordKind kind = TapeRecordKind.TocSet)
    {
        using var ms = new MemoryStream();
        using (var w = new TapeRecordWriter(ms))
            w.Write(kind, fill);
        return ms.ToArray();
    }

    // Raw record bytes with any kind / major (the writer refuses unregistered kinds)
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

    private static byte[] Seal(byte[] record) => [.. record, .. TapeFrame.ComputeCrc(record)];

    private static TapeFieldReader FieldsOf(byte[] record) => TapeRecordReader.Parse(record, out _).Fields;

    private static List<int> NumbersOf(byte[] record)
    {
        TapeFieldReader f = FieldsOf(record);
        var numbers = new List<int>();
        while (f.MoveNext())
            numbers.Add(f.Number);
        return numbers;
    }

    private static TapeFormatException AssertRefused(FormatErrorKind kind, Action act)
    {
        var ex = Assert.Throws<TapeFormatException>(act);
        Assert.Equal(kind, ex.Kind);
        return ex;
    }

    // Runs body inside an open record of a throwaway writer
    private static void InRecord(Action<TapeFieldWriter> body)
    {
        using var w = new TapeRecordWriter(Stream.Null);
        body(w.BeginRecord(TapeRecordKind.TocSet));
    }

    #endregion

    #region *** Schema: product schemas and validation (§B.3.5) ***

    [Fact]
    public void Schema_AllProductSchemas_Valid()
    {
        Assembly product = typeof(TapeSchema<>).Assembly;
        Type[] types;
        try
        {
            types = product.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = [.. ex.Types.OfType<Type>()];
        }

        int found = 0;
        foreach (Type type in types)
        {
            if (type.ContainsGenericParameters)
                continue;

            const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (FieldInfo field in type.GetFields(statics))
            {
                Type ft = field.FieldType;
                if (!ft.IsGenericType || ft.GetGenericTypeDefinition() != typeof(TapeSchema<>))
                    continue;

                // GetValue runs the static initializer; Fields freezes and validates the table
                object? schema = field.GetValue(null);
                Assert.NotNull(schema);
                var fields = (IEnumerable)ft.GetProperty(nameof(TapeSchema<object>.Fields))!.GetValue(schema)!;
                Assert.NotEmpty(fields);
                found++;
            }
        }

        Assert.True(found > 0, "no product schema found - expected at least the virtual media state record");
    }

    [Fact]
    public void Schema_Validation_NumberOutsideRange()
    {
        Assert.Throws<InvalidOperationException>(() => new TapeSchema<Vw> { { 0, v => v.U, (v, x) => v.U = x } });
        Assert.Throws<InvalidOperationException>(() => new TapeSchema<Vw> { { 64, v => v.U, (v, x) => v.U = x } });
    }

    [Fact]
    public void Schema_Validation_ShapeOutsideItsRange()
    {
        Assert.Throws<InvalidOperationException>(() => new TapeSchema<Vw> { { 32, v => v.U, (v, x) => v.U = x } });             // scalar in blob range
        Assert.Throws<InvalidOperationException>(() => new TapeSchema<Vw> { { 5, v => v.S, (v, x) => v.S = x } });              // blob in scalar range
        Assert.Throws<InvalidOperationException>(() => new TapeSchema<Vw> { { 40, v => v.C, (v, x) => v.C = x, Child.Schema } }); // group below 48
    }

    [Fact]
    public void Schema_Validation_BulkBytesAllowedInGroupRange()
        => _ = new TapeSchema<Prim> { { 50, p => p.B, (p, x) => p.B = x } }.Fields;    // calibration Samples, virtual-media Blocks

    [Fact]
    public void Schema_Validation_RepeatedScalar()
        => Assert.Throws<InvalidOperationException>(() => new TapeSchema<Vw> { { 1, v => v.U, (v, x) => v.U = x, FieldFlags.Repeated } });

    [Fact]
    public void Schema_Validation_RequiredWithNonZeroDefault()
    {
        Assert.Throws<InvalidOperationException>(() => new TapeSchema<Vw> { { 1, v => v.U, (v, x) => v.U = x, 5U, FieldFlags.Required } });
        Assert.Throws<InvalidOperationException>(() => new TapeSchema<Vw> { { 32, v => v.S, (v, x) => v.S = x, "x", FieldFlags.Required } });
    }

    [Fact]
    public void Schema_Validation_BitmaskOnNonEnum()
        => Assert.Throws<InvalidOperationException>(() => new TapeSchema<Vw> { { 1, v => v.U, (v, x) => v.U = x, FieldFlags.Bitmask } });

    [Fact]
    public void Schema_Validation_ChildSchemaWithKind()
        => Assert.Throws<InvalidOperationException>(() => new TapeSchema<Vw>
        {
            { 48, v => v.C, (v, x) => v.C = x, new TapeSchema<Child>(TapeRecordKind.TocSet) },
        });

    [Fact]
    public void Schema_Validation_AddAfterFreeze()
    {
        var schema = new TapeSchema<Vw> { { 1, v => v.U, (v, x) => v.U = x } };
        _ = schema.Fields;
        Assert.Throws<InvalidOperationException>(() => schema.Add(2, v => v.U, (v, x) => v.U = x));
    }

    [Fact]
    public void Schema_Validation_DuplicateWithInheritedField()
    {
        var schema = new TapeSchema<DerivedWire>(inherits: BaseWire.Shared) { { 2, d => d.Flag, (d, x) => d.Flag = x } };
        Assert.Throws<InvalidOperationException>(() => _ = schema.Fields);
    }

    [Fact]
    public void Schema_GroupSchema_HasNoKind()
    {
        Assert.False(Child.Schema.HasKind);
        Assert.Throws<InvalidOperationException>(() => _ = Child.Schema.Kind);
    }

    #endregion

    #region *** Schema: overload binding, ranges, enums ***

    [Fact]
    public void Schema_OverloadBinding_AllPrimitives_RoundTrip()
    {
        var p = new Prim
        {
            Bo = true,
            By = byte.MaxValue,
            Us = ushort.MaxValue,
            Ui = uint.MaxValue,
            Ul = ulong.MaxValue,
            I = int.MaxValue,
            L = long.MaxValue,
            D = -0.125,
            G = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
            T = new DateTime(2025, 5, 6, 7, 8, 9, DateTimeKind.Utc),
            M = Mode.Beta,
            A = FileAttributes.Archive | FileAttributes.ReadOnly | FileAttributes.Hidden,   // combined [Flags] value
            S = "s\u00FC",
            B = [1, 2, 3],
            One = new Child { V = 1, S = "one" },
            Many = [new Child { V = 2 }, new Child { V = 3, S = "three" }],
        };

        byte[] bytes = RecordWith(f => Prim.Schema.Write(f, p));
#pragma warning disable CA1861 // Avoid constant arrays as arguments
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 32, 33, 48, 49, 49 }, NumbersOf(bytes));
#pragma warning restore CA1861 // Avoid constant arrays as arguments

        Prim back = Prim.Schema.Read(FieldsOf(bytes), new Prim());
        Assert.True(back.Bo);
        Assert.Equal(p.By, back.By);
        Assert.Equal(p.Us, back.Us);
        Assert.Equal(p.Ui, back.Ui);
        Assert.Equal(p.Ul, back.Ul);
        Assert.Equal(p.I, back.I);
        Assert.Equal(p.L, back.L);
        Assert.Equal(p.D, back.D);
        Assert.Equal(p.G, back.G);
        Assert.Equal(p.T, back.T);
        Assert.Equal(DateTimeKind.Utc, back.T.Kind);
        Assert.Equal(Mode.Beta, back.M);
        Assert.Equal(p.A, back.A);
        Assert.Equal(p.S, back.S);
        Assert.Equal(p.B, back.B);
        Assert.Equal(1U, back.One!.V);
        Assert.Equal("one", back.One.S);
#pragma warning disable IDE0305 // Simplify collection initialization -- to prevent xUnit call ambiguity byte[] / Span<byte>
        Assert.Equal(new uint[] { 2, 3 }, back.Many.Select(c => c.V).ToArray());
#pragma warning restore IDE0305 // Simplify collection initialization
        Assert.Equal("three", back.Many[1].S);
    }

    [Theory]
    [InlineData(2, 256UL)]                          // byte
    [InlineData(3, 65_536UL)]                       // ushort
    [InlineData(4, 4_294_967_296UL)]                // uint
    [InlineData(6, 2_147_483_648UL)]                // int (non-negative)
    [InlineData(7, 9_223_372_036_854_775_808UL)]    // long (non-negative)
    public void Schema_ValueAboveTargetRange_RefusedOnRead(int field, ulong value)
    {
        byte[] bytes = RecordWith(f => f.WriteUInt(field, value));
        var ex = AssertRefused(FormatErrorKind.BadValue, () => Prim.Schema.Read(FieldsOf(bytes), new Prim()));
        Assert.Equal(field, ex.Field);
    }

    [Fact]
    public void Schema_NegativeInt_ThrowsOnWrite()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RecordWith(f => Prim.Schema.Write(f, new Prim { I = -1 })));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecordWith(f => Prim.Schema.Write(f, new Prim { L = -1 })));
    }

    [Fact]
    public void Schema_Bitmask_AboveUnderlyingRange_Refused()
        => AssertRefused(FormatErrorKind.BadValue, () => Prim.Schema.Read(FieldsOf(RecordWith(f => f.WriteUInt(12, 1UL << 40))), new Prim()));

    #endregion

    #region *** Schema: defaults (§B.3.3, §B.8.4) ***

    [Fact]
    public void Schema_OptionalDefaults_Elided()
    {
        // Every field at its schema default - including DateTime.MinValue (Unspecified) and an empty array
        var d = new Defaults { X = 0, T = DateTime.MinValue, M = Mode.Alpha, WithDefault = 7, S = "", B = [] };
        Assert.Empty(NumbersOf(RecordWith(f => Defaults.Schema.Write(f, d))));
    }

    [Fact]
    public void Schema_OptionalDefaults_AppliedWhenAbsent()
    {
        Defaults d = Defaults.Schema.Read(FieldsOf(RecordWith(_ => { })), new Defaults());
        Assert.Equal(0U, d.X);
        Assert.Equal(TapePrimitives.MinUtc, d.T);
        Assert.Equal(Mode.Alpha, d.M);
        Assert.Equal(7U, d.WithDefault);
        Assert.Equal("", d.S);
        Assert.True(d.B is null || d.B.Length == 0);
    }

    [Fact]
    public void Schema_OptionalDefaults_ResetOnReusedTarget()
    {
        // The §B.8.4 pattern: one wire object read again and again
        var target = new Defaults();
        Defaults.Schema.Read(FieldsOf(RecordWith(f => Defaults.Schema.Write(f, new Defaults { X = 5, S = "first", B = [9] }))), target);
        Assert.Equal(5U, target.X);
        Assert.Equal("first", target.S);

        Defaults.Schema.Read(FieldsOf(RecordWith(_ => { })), target);
        Assert.Equal(0U, target.X);
        Assert.Equal("", target.S);
        Assert.True(target.B is null || target.B.Length == 0);
        Assert.Equal(Mode.Alpha, target.M);
    }

    [Fact]
    public void Schema_AbsentOptionalGroup_ResetToNull()    // F4
    {
        var target = new Prim { One = new Child { V = 7 } };
        Prim.Schema.Read(FieldsOf(RecordWith(_ => { })), target);
        Assert.Null(target.One);
    }

    [Fact]
    public void Timestamp_Unspecified_TakenAsUtc_NeverShifted()
    {
        var unspecified = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Unspecified);
        Assert.Equal(unspecified.Ticks, TapePrimitives.ToUtcTicks(unspecified));

        TapeFieldReader r = FieldsOf(RecordWith(f => f.WriteTimestamp(2, unspecified)));
        Assert.True(r.MoveNext());
        DateTime back = r.ReadTimestamp();
        Assert.Equal(unspecified.Ticks, back.Ticks);
        Assert.Equal(DateTimeKind.Utc, back.Kind);
    }

    #endregion

    #region *** Schema: validate hook, inheritance ***

    [Fact]
    public void Schema_Validate_ThrowsOnWrite_RefusesOnRead()
    {
        using var ms = new MemoryStream();
        using (var w = new TapeRecordWriter(ms))
            Assert.Throws<InvalidOperationException>(() => w.Write(new Ranged { Lo = 5, Hi = 1 }));
        Assert.Equal(0, ms.Length);     // nothing reached the stream

        byte[] crafted = RecordWith(f => { f.WriteUInt(1, 5); f.WriteUInt(2, 1); }, TapeRecordKind.TocEnd);
        AssertRefused(FormatErrorKind.CrossCheck, () => TapeRecordReader.Parse(crafted, out _).Read<Ranged>());
    }

    [Fact]
    public void Schema_Inherits_SharedFields()
    {
        var id = Guid.NewGuid();
        byte[] bytes = RecordWith(f => DerivedWire.Schema.Write(f, new DerivedWire { Id = id, Size = 3, Flag = true, Name = "n" }));

        // inherited and own fields merged ascending; inherited Size at its default is elided
#pragma warning disable CA1861 // Avoid constant arrays as arguments
        Assert.Equal(new[] { 1, 4, 32 }, NumbersOf(bytes));
#pragma warning restore CA1861 // Avoid constant arrays as arguments

        DerivedWire back = DerivedWire.Schema.Read(FieldsOf(bytes), new DerivedWire { Size = 99 });
        Assert.Equal(id, back.Id);
        Assert.Equal(3U, back.Size);    // inherited ApplyDefault
        Assert.True(back.Flag);
        Assert.Equal("n", back.Name);
    }

    #endregion

    #region *** Errors carry context (F5) ***

    [Fact]
    public void Errors_Duplicate_CarriesContext()
    {
        byte[] bytes = RecordWith(f => { f.WriteUInt(1, 1); f.WriteUInt(1, 2); }, TapeRecordKind.TocEnd);
        var ex = AssertRefused(FormatErrorKind.Duplicate, () => TapeRecordReader.Parse(bytes, out _).Read<Ranged>());
        Assert.Equal(TapeRecordKind.TocEnd, ex.Record);
        Assert.Equal(1, ex.Field);
        Assert.NotNull(ex.Offset);
        Assert.Contains("record TocEnd", ex.Message);
        Assert.Contains("field 1", ex.Message);
    }

    [Fact]
    public void Errors_MissingRequired_NamesTheField()
    {
        byte[] bytes = RecordWith(f => f.WriteString(32, "n"));    // Id (1) missing
        var ex = AssertRefused(FormatErrorKind.MissingRequired, () => DerivedWire.Schema.Read(FieldsOf(bytes), new DerivedWire()));
        Assert.Equal(TapeRecordKind.TocSet, ex.Record);
        Assert.Equal(1, ex.Field);
    }

    [Fact]
    public void Errors_PrimitiveDecode_GainsContext()
    {
        byte[] bytes = RecordWith(f => f.WriteBytes(9, [1, 2, 3]));     // a 3-byte guid
        var ex = AssertRefused(FormatErrorKind.Truncated, () => Prim.Schema.Read(FieldsOf(bytes), new Prim()));
        Assert.Equal(TapeRecordKind.TocSet, ex.Record);
        Assert.Equal(9, ex.Field);
    }

    [Fact]
    public void Errors_Overrun_CarriesRecord()
    {
        byte[] bytes = Raw((ushort)TapeRecordKind.TocSet, 2, [(1 << 1), 5, 0xFF]);
        var ex = AssertRefused(FormatErrorKind.Overrun, () => FieldsOf(bytes).MoveNext());
        Assert.Equal(TapeRecordKind.TocSet, ex.Record);
    }

    #endregion

    #region *** Field writer and reader ***

    [Fact]
    public void FieldWriter_DescendingNumber_Throws()     // F3
        => Assert.Throws<InvalidOperationException>(() => RecordWith(f => { f.WriteUInt(5, 1); f.WriteUInt(3, 1); }));

    [Fact]
    public void FieldWriter_RepeatedNumberAndFreshChildOrder_Allowed()
        => RecordWith(f =>
        {
            f.WriteUInt(5, 1);
            f.WriteUInt(5, 2);          // repeats in place
            TapeFieldWriter g = f.BeginGroup(48);
            g.WriteUInt(1, 1);          // the child starts its own ascending sequence
            f.EndGroup(g);
            f.WriteUInt(49, 1);
        });

    [Fact]
    public void Group_BeginEnd_ReusesTheChildWriter()
        => InRecord(f =>
        {
            TapeFieldWriter a = f.BeginGroup(48);
            f.EndGroup(a);
            TapeFieldWriter b = f.BeginGroup(48);
            f.EndGroup(b);
            Assert.Same(a, b);
        });

    [Fact]
    public void Group_BeginTwice_Throws()
        => InRecord(f =>
        {
            f.BeginGroup(48);
            Assert.Throws<InvalidOperationException>(() => f.BeginGroup(49));
        });

    [Fact]
    public void Group_EndWithoutBegin_Throws()
        => InRecord(f =>
        {
            TapeFieldWriter g = f.BeginGroup(48);
            f.EndGroup(g);
            Assert.Throws<InvalidOperationException>(() => f.EndGroup(g));
        });

    [Fact]
    public void Group_EndWithForeignWriter_Throws()
        => InRecord(f =>
        {
            TapeFieldWriter g = f.BeginGroup(48);
            TapeFieldWriter gg = g.BeginGroup(48);
            Assert.Throws<InvalidOperationException>(() => f.EndGroup(gg));
        });

    [Fact]
    public void Group_EndWhileNestedOpen_Throws()
        => InRecord(f =>
        {
            TapeFieldWriter g = f.BeginGroup(48);
            g.BeginGroup(48);
            Assert.Throws<InvalidOperationException>(() => f.EndGroup(g));
        });

    [Theory]
    [InlineData(0)]     // number 0
    [InlineData(1)]     // number 0, critical
    public void FieldReader_FieldNumberZero_Refused(int tag)
    {
        byte[] bytes = Raw((ushort)TapeRecordKind.TocSet, 2, [(byte)tag, 0]);
        AssertRefused(FormatErrorKind.BadValue, () => FieldsOf(bytes).MoveNext());
    }

    [Fact]
    public void FieldReader_SkipUnknown_NoOpOrRefusal()
    {
        TapeFieldReader f = FieldsOf(RecordWith(g => { g.WriteUInt(40, 1); g.WriteUInt(41, 1, critical: true); }));
        Assert.True(f.MoveNext());
        f.SkipUnknown();
        Assert.True(f.MoveNext());
        var ex = AssertRefused(FormatErrorKind.UnknownCritical, f.SkipUnknown);
        Assert.Equal(41, ex.Field);
    }

    [Fact]
    public void FieldReader_ValueBeforeMoveNext_Throws()
    {
        TapeFieldReader f = FieldsOf(RecordWith(g => g.WriteUInt(1, 1)));
        Assert.Throws<InvalidOperationException>(() => f.ReadUInt());
    }

    #endregion

    #region *** Records: writer, reader, polymorphism ***

    [Fact]
    public void Writer_FillThrows_RecordAbandoned_WriterReusable()     // F1
    {
        using var ms = new MemoryStream();
        using var w = new TapeRecordWriter(ms);

        Assert.Throws<InvalidOperationException>(() => w.Write(TapeRecordKind.TocSet, f =>
        {
            f.WriteUInt(1, 1);
            throw new InvalidOperationException("boom");
        }));
        Assert.Throws<InvalidOperationException>(() => w.Write(new Ranged { Lo = 5, Hi = 1 }));   // validate refuses
        w.Write(new Ranged { Lo = 1, Hi = 2 });

        Assert.Equal(ms.Length, w.BytesWritten);
        ms.Position = 0;
        var reader = new TapeRecordReader(ms);
        Assert.Equal(2U, reader.Read<Ranged>().Hi);
        Assert.Null(reader.ReadRecord());     // nothing of the abandoned records reached the stream
    }

    [Fact]
    public void Writer_UnregisteredKind_Refused()
    {
        using var w = new TapeRecordWriter(Stream.Null);
        Assert.Throws<ArgumentOutOfRangeException>(() => w.BeginRecord((TapeRecordKind)0x0999));
    }

    [Fact]
    public void Record_PolymorphicDispatch()
    {
        using var ms = new MemoryStream();
        using (var w = new TapeRecordWriter(ms))
        {
            w.Write(new Circle { Label = "c" });
            w.Write(new Square { Label = "s" });
        }
        byte[] bytes = ms.ToArray();

        ms.Position = 0;
        var reader = new TapeRecordReader(ms);
        Shape first = reader.Read<Shape>();
        Shape second = reader.Read<Shape>();
        Assert.IsType<Circle>(first);
        Assert.Equal("c", first.Label);
        Assert.IsType<Square>(second);
        Assert.Equal("s", second.Label);

        Assert.Equal("c", TapeRecordReader.Parse(bytes, out _).Read<Circle>().Label);
        AssertRefused(FormatErrorKind.UnexpectedKind, () => TapeRecordReader.Parse(bytes, out _).Read<Square>());
    }

    [Fact]
    public void Record_DomainExceptionInReadBody_BecomesFormatError()     // F7
    {
        byte[] record = RecordWith(f => f.WriteUInt(1, 1), TapeRecordKind.TocEnd);

        var ex = AssertRefused(FormatErrorKind.BadValue, () => TapeRecordReader.Parse(record, out _).Read<Boom>());
        Assert.IsType<ArgumentException>(ex.InnerException);
        Assert.Equal(TapeRecordKind.TocEnd, ex.Record);

        Assert.Equal(TapeFrameStatus.Unparseable, TapeFrame.TryUnpack(Seal(record), out Boom? _, out _, out var error));
        Assert.Equal(FormatErrorKind.BadValue, error!.Kind);
    }

    [Fact]
    public void RecordReader_ReadAtCleanEnd_Truncated()
    {
        using var ms = new MemoryStream();
        AssertRefused(FormatErrorKind.Truncated, () => new TapeRecordReader(ms).Read<Ranged>());
    }

    [Fact]
    public void RecordReader_ReadRecordExpected_WrongKind()
    {
        using var ms = new MemoryStream(RecordWith(f => f.WriteUInt(1, 1), TapeRecordKind.TocEnd));
        AssertRefused(FormatErrorKind.UnexpectedKind, () => new TapeRecordReader(ms).ReadRecord(TapeRecordKind.TocHeader));
    }

    [Fact]
    public void Prologue_OlderMajor_Refused()
        => AssertRefused(FormatErrorKind.BadValue, () => TapeRecordReader.Parse(Raw((ushort)TapeRecordKind.TocEnd, 1, []), out _));

    [Fact]
    public void Prologue_StreamEndsInsideFixedPart_Truncated()
    {
        byte[] record = RecordWith(f => f.WriteUInt(1, 1));
        using var ms = new MemoryStream(record, 0, 5);
        AssertRefused(FormatErrorKind.Truncated, () => new TapeRecordReader(ms).ReadRecord());
    }

    [Fact]
    public void Prologue_SpanBodyAboveLimit_LimitExceeded()     // F6
    {
        var prologue = new byte[16];
        "TpN#"u8.CopyTo(prologue);
        BinaryPrimitives.WriteUInt16LittleEndian(prologue.AsSpan(4), (ushort)TapeRecordKind.TocSet);
        prologue[6] = TapeFormat.Major;
        prologue[7] = TapeFormat.Minor;
        int n = 8 + TapePrimitives.WriteVarUInt(prologue.AsSpan(8), (ulong)TapeFormat.MaxRecordBody + 1);
        AssertRefused(FormatErrorKind.LimitExceeded, () => TapeRecordReader.Parse(prologue.AsSpan(0, n), out _));
    }

    [Fact]
    public void RecordReader_ForwardOnlyTrickle_NoReadAhead()
    {
        byte[] record = RecordWith(f => f.WriteString(32, new string('x', 1000)));
        var source = new ForwardOnlyStream([.. record, 1, 2, 3], maxChunk: 1);

        TapeRecord? read = new TapeRecordReader(source).ReadRecord();
        Assert.NotNull(read);
        Assert.Equal(new byte[] { 1, 2, 3 }, source.Rest());
    }

    #endregion

    #region *** Frames ***

    [Fact]
    public void Frame_Unparseable_ReportsErrorAndLength()
    {
        (byte[] Record, FormatErrorKind Kind)[] cases =
        [
            (Raw((ushort)TapeRecordKind.TocEnd, 3, []), FormatErrorKind.NewerMajor),
            (Raw((ushort)TapeRecordKind.TocEnd, 1, []), FormatErrorKind.BadValue),
            (Raw(0x0999, 2, []), FormatErrorKind.UnknownKind),
            (Raw(0x8999, 2, []), FormatErrorKind.UnknownKind),     // a frame carries one record: nothing to skip to
            (RecordWith(f => f.WriteUInt(1, 1), TapeRecordKind.TocSet), FormatErrorKind.UnexpectedKind),
            (RecordWith(f => f.WriteUInt(40, 1, critical: true), TapeRecordKind.TocEnd), FormatErrorKind.UnknownCritical),
            (RecordWith(f => { f.WriteUInt(1, 5); f.WriteUInt(2, 1); }, TapeRecordKind.TocEnd), FormatErrorKind.CrossCheck),
        ];

        foreach ((byte[] record, FormatErrorKind kind) in cases)
        {
            byte[] frame = Seal(record);
            var status = TapeFrame.TryUnpack(frame, out Ranged? value, out int frameLength, out TapeFormatException? error);
            Assert.Equal(TapeFrameStatus.Unparseable, status);
            Assert.Null(value);
            Assert.NotNull(error);
            Assert.Equal(kind, error.Kind);
            Assert.Equal(frame.Length, frameLength);
        }
    }

    [Fact]
    public void Frame_OkAndCrcMismatch_NoError()
    {
        byte[] frame = TapeFrame.Pack(new Ranged { Lo = 1, Hi = 2 });
        byte[] block = [.. frame, .. new byte[100]];

        Assert.Equal(TapeFrameStatus.Ok, TapeFrame.TryUnpack(block, out Ranged? value, out int frameLength, out var error));
        Assert.Equal(2U, value!.Hi);
        Assert.Equal(frame.Length, frameLength);
        Assert.Null(error);

        block[frame.Length - 1] ^= 1;
        Assert.Equal(TapeFrameStatus.CrcMismatch, TapeFrame.TryUnpack(block, out value, out frameLength, out error));
        Assert.Null(value);
        Assert.Null(error);
        Assert.Equal(frame.Length, frameLength);
    }

    [Fact]
    public void Frame_TryUnpackWithLegacy_V2NeverReachesTheLegacyReader()
    {
        int before = Legacyish.LegacyCalls;
        byte[] block = TapeFrame.PackBlock(new Legacyish { Origin = "v2" }, 1024);

        Assert.Equal(TapeFrameStatus.Ok, TapeFrame.TryUnpackWithLegacy(block, out Legacyish? value, out int length, out var error));
        Assert.Equal("v2", value!.Origin);
        Assert.True(length > 0);
        Assert.Null(error);

        block[12] ^= 1;     // damaged behind the magic: still ours, never handed to the legacy reader
        Assert.Equal(TapeFrameStatus.CrcMismatch, TapeFrame.TryUnpackWithLegacy(block, out Legacyish? _, out _, out _));
        Assert.Equal(before, Legacyish.LegacyCalls);
    }

    [Fact]
    public void Frame_TryUnpackWithLegacy_ForwardsNonMagicData()
    {
        int before = Legacyish.LegacyCalls;

        Assert.Equal(TapeFrameStatus.Ok, TapeFrame.TryUnpackWithLegacy([0x42, 0, 0, 0], out Legacyish? value, out int length, out var error));
        Assert.Equal("legacy", value!.Origin);
        Assert.Equal(0, length);
        Assert.Null(error);

        Assert.Equal(TapeFrameStatus.NotFramed, TapeFrame.TryUnpackWithLegacy(new byte[16], out value, out _, out _));
        Assert.Null(value);
        Assert.Equal(before + 2, Legacyish.LegacyCalls);
    }

    [Fact]
    public void Frame_Inline_Truncated_IsFormatError()     // F2
    {
        using var ms = new MemoryStream();
        TapeFrame.WriteInline(ms, new Ranged { Hi = 1 });
        byte[] bytes = ms.ToArray();

        using var cutCrc = new MemoryStream(bytes, 0, bytes.Length - 3);
        AssertRefused(FormatErrorKind.Truncated, () => TapeFrame.ReadInline<Ranged>(cutCrc));

        using var cutBody = new MemoryStream(bytes, 0, bytes.Length - TapeFrame.CrcLength - 1);
        AssertRefused(FormatErrorKind.Truncated, () => TapeFrame.ReadInline<Ranged>(cutBody));
    }

    [Fact]
    public void Frame_Inline_ForwardOnlyTrickle_LeavesBodyUntouched()
    {
        using var ms = new MemoryStream();
        TapeFrame.WriteInline(ms, new Ranged { Lo = 1, Hi = 2 });
        byte[] body = [7, 8, 9];
        var source = new ForwardOnlyStream([.. ms.ToArray(), .. body], maxChunk: 1);

        Assert.Equal(2U, TapeFrame.ReadInline<Ranged>(source).Hi);
        Assert.Equal(body, source.Rest());
        Assert.False(source.Disposed);
    }

    #endregion

    #region *** CRC envelope ***

    [Fact]
    public void Envelope_ForwardOnly_BothWays_NoReadAhead_InnerNotDisposed()
    {
        var sink = new ForwardOnlySink();
        TapeCrc64Envelope.Write(sink, s =>
        {
            using var w = new TapeRecordWriter(s);
            w.Write(new Ranged { Lo = 1, Hi = 2 });
            w.Write(new Ranged { Lo = 3, Hi = 4 });
        });
        Assert.False(sink.Disposed);

        byte[] tail = [0xAA, 0xBB, 0xCC];
        var source = new ForwardOnlyStream([.. sink.ToArray(), .. tail], maxChunk: 3);
        Ranged[] records = TapeCrc64Envelope.Read(source, s =>
        {
            var reader = new TapeRecordReader(s);
            return new[] { reader.Read<Ranged>(), reader.Read<Ranged>() };
        });

        Assert.Equal(4U, records[1].Hi);
        Assert.False(source.Disposed);
        Assert.Equal(tail, source.Rest());
    }

    #endregion

    #region *** Allocation budget (§B.8.4) ***

    [Fact]
    public void Batch_WriteWithReusedWire_AllocatesNothingPerEntry()
    {
        const int entries = 100_000;
        var wire = new Entry
        {
            Attr = FileAttributes.Archive | FileAttributes.ReadOnly,
            Created = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Written = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Suffix = "file.txt",
            Hash = [1, 2, 3, 4],
        };
        using var w = new TapeRecordWriter(Stream.Null);

        void WriteBatch()
        {
            TapeFieldWriter body = w.BeginRecord(TapeRecordKind.TocFileBatch);
            for (int i = 0; i < entries; i++)
            {
                wire.Id = (ulong)i + 1;
                wire.Block = (ulong)i * 3;
                wire.Offset = (ulong)(i % 4096);
                wire.Length = (ulong)i * 17;
                TapeFieldWriter group = body.BeginGroup(48);
                Entry.Schema.Write(group, wire);
                body.EndGroup(group);
            }
            w.EndRecord();
        }

        WriteBatch();       // warm-up: grows the pooled buffers, JITs the path

        long before = GC.GetAllocatedBytesForCurrentThread();
        WriteBatch();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < entries, $"{allocated} bytes allocated for {entries} entries - expected well under 1 byte per entry");
    }

    #endregion
}
