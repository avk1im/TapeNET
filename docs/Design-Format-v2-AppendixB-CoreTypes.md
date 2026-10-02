# Design-Format-v2 — Appendix B: Core Types (`TapeSchema<T>`, `ITapeRecord<T>`, `ITapeFramedRecord<T>`)

**Status:** for implementation — binding for Phase 1 (steps 6–11) of Design-Format-v2 v4.
**Supersedes:** the illustrative signatures in §5.7 and §8.1 of the main document where they differ (§B.10).
**Last updated:** 2026-10-01

---

## B.1 Layering

```
 Domain types          TapeTOC · TapeSetTOC · TapeHeader… · TapeCalibrationCheckpoint · VirtualTapeMedia
        │  implement ITapeRecord<TSelf> / ITapeFramedRecord<TSelf>      (body only, via a schema)
        ▼
 Schema               TapeSchema<T>  ── ITapeField<in T>  ── ITapeValueCodec<TValue>
        │  maps T ⇄ fields                                  (one codec per wire primitive)
        ▼
 Fields               TapeFieldWriter / TapeFieldReader      Tag · Length · Value, groups
        ▼
 Records              TapeRecordWriter / TapeRecordReader    prologue "TpN#" kind major minor length
        ▼
 Carriers             TapeFrame (block / inline, + CRC-64) · TapeCrc64Envelope (TOC stream)
```

Each layer knows only the one below. Domain types never touch the prologue, lengths or CRCs.

---

## B.2 Ground Rules (for Copilot-driven code generation)

1. **No reflection in product code.** Codecs bind at compile time through overload resolution (§B.5).
2. **C# 12 compatible.** No `ref struct` in lambdas or generic arguments: `TapeFieldWriter` / `TapeFieldReader`
   are `sealed class`es, pooled and reused per record.
3. **Schemas are `static readonly`**, built once; the constructor **validates** them (§B.5.4). A broken schema
   fails at type initialization, caught by one test that touches every schema.
4. **Schema targets are mutable, with a parameterless constructor.** Immutable domain types (headers,
   `TapeFileInfo`, `VirtualTapeBlock`, checkpoints) use a private nested **`Wire`** class: `Wire.From(this)` on
   write, `wire.ToRecord()` on read. Shape mismatches (split `Address`, front-coded name, delta-coded samples)
   are resolved in `From` / `ToRecord`, never in the schema.
5. **Inside a field, values carry no own length prefix** — `Field.Length` delimits them. A string field's value
   is raw UTF-8; a bytes field's value is the raw bytes; a group's value is a nested `Field*` body.
6. **Every typed read consumes exactly `Value.Length` bytes**, or throws `BadValue`.
7. **Read paths throw only `TapeFormatException`.** Codecs wrap anything else. Carriers turn it into a
   `FrameStatus` or let it propagate to the agent, which maps it to a `WIN32_ERROR`.
8. **Writers emit fields in ascending number order** (`Debug.Assert`); repeated fields repeat in place.
9. **Timestamps:** `Kind == Utc` expected. `Local` is converted; `Unspecified` is treated as UTC and asserts in
   DEBUG.
10. **Readers never read past the record they were asked for** (the TOC's CRC and file bodies follow directly).

---

## B.3 Shared Small Types

```csharp
namespace TapeLibNET.Format;

/// Wire record kinds (§4.3 of the main document). High byte = family. 0x8000 = skippable when unknown.
public enum TapeRecordKind : ushort
{
    TocHeader = 0x0101, TocSet = 0x0102, TocFileBatch = 0x0103, TocEnd = 0x0104,
    FileHeader = 0x0201,
    MediaHeader = 0x0301, SetHeader = 0x0302, CalibrationHeader = 0x0303, CalibrationCheckpoint = 0x0304,
    VirtualMediaState = 0x0F01,
}

[Flags]
public enum FieldFlags : byte
{
    None     = 0,
    Required = 1 << 0,   // always written, even at its default; absence refuses the record
    Critical = 1 << 1,   // the tag carries the critical bit: a reader that does not know the number must refuse
    Repeated = 1 << 2,   // groups only: may occur any number of times
    Bitmask  = 1 << 3,   // [Flags] enums only: skip the defined-value check
}

/// Which wire shape a field has — must match its number range (§4.4).
public enum FieldShape : byte { Scalar, Blob, Group }   // 1–31, 32–47, 48–63

/// What the reader knows about the record it is in — for diagnostics and kind dispatch.
public readonly record struct TapeRecordInfo(TapeRecordKind Kind, byte Major, byte Minor, long Offset);

public enum FormatErrorKind
{
    NotOurs, NewerMajor, UnknownKind, UnknownCritical, MissingRequired, Duplicate,
    Overrun, Underrun, BadValue, LimitExceeded, CrcMismatch, Truncated, CrossCheck,
}

public sealed class TapeFormatException(FormatErrorKind kind, string message,
    TapeRecordKind? record = null, int? field = null, long? offset = null) : FormatException(message)
{
    public FormatErrorKind Kind { get; } = kind;
    public TapeRecordKind? Record { get; } = record;
    public int? Field { get; } = field;
    public long? Offset { get; } = offset;
}
```

---

## B.4 Field Writer and Reader

```csharp
/// Writes Field* into a pooled buffer. One instance per nesting depth, owned by TapeRecordWriter.
public sealed class TapeFieldWriter
{
    // ── scalars (value = varuint / varint / fixed) ──
    public void WriteUInt     (int number, ulong value,    bool critical = false);
    public void WriteInt      (int number, long value,     bool critical = false);  // ZigZag
    public void WriteBool     (int number, bool value,     bool critical = false);
    public void WriteDouble   (int number, double value,   bool critical = false);
    public void WriteGuid     (int number, Guid value,     bool critical = false);
    public void WriteTimestamp(int number, DateTime value, bool critical = false);  // UTC ticks, ZigZag
    // ── blobs ──
    public void WriteString   (int number, string value,   bool critical = false);  // raw UTF-8, ≤ MaxStringBytes
    public void WriteBytes    (int number, ReadOnlySpan<byte> value, bool critical = false);
    // ── groups ──
    /// Returns the child writer for depth+1 (reset). Write the child's fields, then call EndGroup.
    public TapeFieldWriter BeginGroup(int number, bool critical = false);
    /// Appends Tag · Length · child body. The child must be the one returned by the matching BeginGroup.
    public void EndGroup(TapeFieldWriter child);

    internal ReadOnlySpan<byte> Written { get; }
    internal void Reset();
}

/// Iterates Field* over a bounded body (the whole record is already in memory, ≤ MaxRecordBody).
public sealed class TapeFieldReader
{
    public TapeRecordInfo Record { get; }
    public bool MoveNext();                 // next field; validates tag / length against the body bounds
    public int  Number { get; }             // tag >> 1
    public bool IsCritical { get; }         // tag & 1
    public ReadOnlySpan<byte> Value { get; }

    public ulong    ReadUInt();             // each: decodes Value, requires it fully consumed → else BadValue
    public long     ReadInt();
    public bool     ReadBool();
    public double   ReadDouble();
    public Guid     ReadGuid();
    public DateTime ReadTimestamp();        // DateTimeKind.Utc
    public string   ReadString();           // validated UTF-8
    public byte[]   ReadBytes();
    public TapeFieldReader ReadGroup();     // child reader over Value (depth + 1, ≤ MaxGroupDepth)

    /// For numbers the schema does not know: no-op, or UnknownCritical when the tag says so.
    public void SkipUnknown();

    public TapeFormatException Error(FormatErrorKind kind, string? detail = null);   // fills record / field / offset
}
```

---

## B.5 `TapeSchema<T>`

### B.5.1 Field contract

```csharp
/// One field of a schema. Contravariant, so a base wire type's fields serve every derived wire type (§B.5.5).
public interface ITapeField<in T>
{
    int Number { get; }
    FieldFlags Flags { get; }
    FieldShape Shape { get; }
    void Write(TapeFieldWriter w, T source);     // elides itself when optional and at its default
    void Read(TapeFieldReader r, T target);      // r positioned on this field
    void ApplyDefault(T target);                 // field absent and optional
}

/// Encodes one wire primitive. One singleton per supported CLR type; EnumCodec<TEnum> per enum.
internal interface ITapeValueCodec<TValue>
{
    FieldShape Shape { get; }
    void Write(TapeFieldWriter w, int number, TValue value, bool critical);
    TValue Read(TapeFieldReader r);
    bool IsDefault(TValue value, TValue @default);
}
```

Supported CLR types and their wire form:

| CLR type | Codec | Shape |
|---|---|---|
| `bool` | `WriteBool` | Scalar |
| `byte`, `ushort`, `uint`, `ulong` | `WriteUInt` (range-checked on read) | Scalar |
| `int`, `long` | `WriteInt` | Scalar |
| `double` | `WriteDouble` | Scalar |
| `Guid` | `WriteGuid` | Scalar |
| `DateTime` | `WriteTimestamp` | Scalar |
| `TEnum : struct, Enum` | underlying bits as `WriteUInt`; defined-value check unless `Bitmask` | Scalar |
| `string?` | `WriteString`; `null` = absent | Blob |
| `byte[]?` | `WriteBytes`; `null` = absent | Blob |
| nested `TapeSchema<TChild>` | `BeginGroup` / `EndGroup` | Group |

### B.5.2 Public surface

```csharp
/// Declarative map between a mutable wire type and a record body (or a nested group).
/// Usage: a static readonly collection initializer, one line per field (§B.7).
public sealed class TapeSchema<T> : IEnumerable<ITapeField<T>> where T : class
{
    /// <param name="kind">Record kind; null for a group schema (nested only).</param>
    /// <param name="inherits">Fields of a base wire type (e.g. shared header tags 1–3).</param>
    /// <param name="validate">Cross-field checks after a read; throw via the reader's Error(CrossCheck).</param>
    public TapeSchema(TapeRecordKind? kind = null,
                      IEnumerable<ITapeField<T>>? inherits = null,
                      Action<T, TapeFieldReader>? validate = null);

    public TapeRecordKind Kind { get; }         // throws InvalidOperationException for group schemas
    public IReadOnlyList<ITapeField<T>> Fields { get; }   // sorted ascending; frozen after first use

    public void Write(TapeFieldWriter w, T source);
    public T Read(TapeFieldReader r, T target);

    // ── value fields: ONE overload per wire primitive; flags BEFORE default ──
    public void Add(int number, Func<T, bool>     get, Action<T, bool>     set, FieldFlags flags = 0, bool     @default = false);
    public void Add(int number, Func<T, byte>     get, Action<T, byte>     set, FieldFlags flags = 0, byte     @default = 0);
    public void Add(int number, Func<T, ushort>   get, Action<T, ushort>   set, FieldFlags flags = 0, ushort   @default = 0);
    public void Add(int number, Func<T, uint>     get, Action<T, uint>     set, FieldFlags flags = 0, uint     @default = 0);
    public void Add(int number, Func<T, ulong>    get, Action<T, ulong>    set, FieldFlags flags = 0, ulong    @default = 0);
    public void Add(int number, Func<T, int>      get, Action<T, int>      set, FieldFlags flags = 0, int      @default = 0);
    public void Add(int number, Func<T, long>     get, Action<T, long>     set, FieldFlags flags = 0, long     @default = 0);
    public void Add(int number, Func<T, double>   get, Action<T, double>   set, FieldFlags flags = 0, double   @default = 0);
    public void Add(int number, Func<T, Guid>     get, Action<T, Guid>     set, FieldFlags flags = 0, Guid     @default = default);
    public void Add(int number, Func<T, DateTime> get, Action<T, DateTime> set, FieldFlags flags = 0, DateTime @default = default);
    public void Add(int number, Func<T, string?>  get, Action<T, string?>  set, FieldFlags flags = 0, string?  @default = null);
    public void Add(int number, Func<T, byte[]?>  get, Action<T, byte[]?>  set, FieldFlags flags = 0, byte[]?  @default = null);
    public void Add<TEnum>(int number, Func<T, TEnum> get, Action<T, TEnum> set, FieldFlags flags = 0, TEnum @default = default)
        where TEnum : struct, Enum;

    // ── groups ──
    public void Add<TChild>(int number, Func<T, TChild?> get, Action<T, TChild> set,
                            TapeSchema<TChild> child, FieldFlags flags = 0) where TChild : class, new();
    public void Add<TChild>(int number, Func<T, IEnumerable<TChild>> getAll, Action<T, TChild> add,
                            TapeSchema<TChild> child, FieldFlags flags = FieldFlags.Repeated) where TChild : class, new();

    IEnumerator<ITapeField<T>> IEnumerable<ITapeField<T>>.GetEnumerator();   // enables the initializer + inherits
    IEnumerator IEnumerable.GetEnumerator();
}
```

**Why the overloads bind exactly.** A lambda converts to a delegate only if its body compiles. For a `uint`
property, the `ulong` candidate's setter `(s, v) => s.BlockSize = v` does not compile (`ulong → uint`), so only
the `uint` overload remains. The generic enum overload fails its constraint for non-enums; the numeric overloads
fail their setters for enums. Result: **the property's own type picks the codec** — no reflection, no casts.

**Two traps to document in the code:**
- Write enum defaults as **named members** (`TapeDataFormat.V2`), never the literal `0` — `0` converts to any enum,
  `FieldFlags` included.
- Collection initializers do not accept named arguments; hence **flags before default**, so the common
  `{ n, get, set, FieldFlags.Required }` needs no default.

### B.5.3 Core algorithms

```csharp
public void Write(TapeFieldWriter w, T source)
{
    foreach (var field in m_fields)          // ascending, fixed at construction
        field.Write(w, source);
}

public T Read(TapeFieldReader r, T target)
{
    ulong seen = 0;                           // numbers are 1..63 → one bit each
    while (r.MoveNext())
    {
        int n = r.Number;
        ITapeField<T>? field = (uint)n < 64 ? m_byNumber[n] : null;
        if (field is null)
        {
            r.SkipUnknown();                  // throws UnknownCritical when the tag's critical bit is set
            continue;
        }
        // Known number: the tag's critical bit is irrelevant (R3).
        ulong bit = 1UL << n;
        if ((seen & bit) != 0 && (field.Flags & FieldFlags.Repeated) == 0)
            throw r.Error(FormatErrorKind.Duplicate);
        seen |= bit;
        field.Read(r, target);
    }

    foreach (var field in m_fields)
    {
        if ((seen & (1UL << field.Number)) != 0)
            continue;
        if ((field.Flags & FieldFlags.Required) != 0)
            throw r.Error(FormatErrorKind.MissingRequired, $"field {field.Number}");
        field.ApplyDefault(target);           // absent ⇒ schema default, never "whatever the ctor set"
    }

    m_validate?.Invoke(target, r);
    return target;
}
```

```csharp
internal sealed class ValueField<T, TValue>(int number, Func<T, TValue> get, Action<T, TValue> set,
    FieldFlags flags, TValue @default, ITapeValueCodec<TValue> codec) : ITapeField<T>
{
    public int Number => number;
    public FieldFlags Flags => flags;
    public FieldShape Shape => codec.Shape;

    public void Write(TapeFieldWriter w, T source)
    {
        TValue value = get(source);
        bool required = (flags & FieldFlags.Required) != 0;
        if (!required && codec.IsDefault(value, @default))
            return;                                                    // optional at default: elided
        if (required && value is null)
            throw new InvalidOperationException($"Required field {number} is null");   // programming error
        codec.Write(w, number, value, (flags & FieldFlags.Critical) != 0);
    }

    public void Read(TapeFieldReader r, T target) => set(target, codec.Read(r));
    public void ApplyDefault(T target) => set(target, @default);
}
```

`GroupField<T, TChild>` and `RepeatedGroupField<T, TChild>` follow the same pattern via `BeginGroup` / `ReadGroup`
and the child schema; `ApplyDefault` of a repeated group is a no-op.

### B.5.4 Construction-time validation

The constructor (on freeze — first `Write` / `Read` / `Fields`) throws `InvalidOperationException` when:

| Check | Why |
|---|---|
| number outside 1–63, or duplicate | §4.4; bitmask in `Read` |
| `Shape` does not match the range (Scalar 1–31, Blob 32–47, Group 48–63) | keeps "scalars first" true forever |
| `Repeated` on a non-group | only groups repeat |
| `Required` together with a non-default `@default` | contradictory |
| enum with a negative member (`Enum.GetValues<TEnum>()`) | enums travel as zero-extended bits |
| `kind` given for a schema used as a child group, or missing for one used by a record | wiring mistakes |

### B.5.5 Inheritance (shared header tags)

`ITapeField<in T>` is contravariant and `IEnumerable<out T>` covariant, so a base schema plugs into a derived one:

```csharp
internal abstract class HeaderWire
{
    public Guid Id;
    public DateTime CreatedUtc;
    public uint BlockSize;

    public static readonly TapeSchema<HeaderWire> Shared = new()        // group-style: no kind, never used alone
    {
        { 1, h => h.Id,         (h, v) => h.Id = v,         FieldFlags.Required },
        { 2, h => h.CreatedUtc, (h, v) => h.CreatedUtc = v, FieldFlags.Required },
        { 3, h => h.BlockSize,  (h, v) => h.BlockSize = v,  FieldFlags.Required },
    };
}

internal sealed class MediaHeaderWire : HeaderWire
{
    public int Volume = 1;
    public MediaPartition Partition;
    public TocPlacement TocPlacement;
    public bool HasSetHeaders;
    public string? OriginalName;

    public static readonly TapeSchema<MediaHeaderWire> Schema = new(TapeRecordKind.MediaHeader, inherits: Shared)
    {
        { 4,  m => m.Volume,        (m, v) => m.Volume = v,        0, 1 },
        { 5,  m => m.Partition,     (m, v) => m.Partition = v,     0, MediaPartition.Content },
        { 6,  m => m.TocPlacement,  (m, v) => m.TocPlacement = v,  0, TocPlacement.InSet },
        { 7,  m => m.HasSetHeaders, (m, v) => m.HasSetHeaders = v },
        { 32, m => m.OriginalName,  (m, v) => m.OriginalName = v },
    };
}
```

(`0` for `flags` is fine there — the parameter is `FieldFlags`, the default follows by position.)

---

## B.6 Record Interfaces

```csharp
/// A top-level record: the type writes and reads its BODY; TapeRecordWriter / Reader own the prologue.
public interface ITapeRecord<TSelf> where TSelf : ITapeRecord<TSelf>
{
    /// The kind this instance writes (polymorphic bases answer per concrete type).
    TapeRecordKind RecordKind { get; }

    void WriteBody(TapeFieldWriter w);

    /// Kinds ReadBody understands. Single-kind types: kind == TheirKind. TapeHeader: the three header kinds.
    static abstract bool Accepts(TapeRecordKind kind);

    static abstract TSelf ReadBody(TapeFieldReader r);       // r.Record.Kind tells polymorphic bases what to build
}

/// A record that also lives inside a block or inline frame and may be found there in its legacy form.
public interface ITapeFramedRecord<TSelf> : ITapeRecord<TSelf> where TSelf : class, ITapeFramedRecord<TSelf>
{
    /// Parses a LEGACY frame at the start of `block` ([int32 len][payload][crc32]).
    /// One-line forward into TapeLibNET.Legacy; must not throw — report through the status.
    static abstract FrameStatus TryReadLegacy(ReadOnlySpan<byte> block, out TSelf? record);
}
```

**Who implements what:**

| Type | Interface | Notes |
|---|---|---|
| `TocHeaderWire`, `TapeSetTOC` (via `Wire`), `FileBatchWire`, `TocEndWire` | `ITapeRecord` | TOC stream; legacy TOC is read whole by `LegacyTocReader`, not per record |
| `TapeHeader` (abstract) | `ITapeFramedRecord<TapeHeader>` | `Accepts` = 3 header kinds; `ReadBody` switches on `r.Record.Kind` |
| `TapeMediaHeader`, `TapeSetHeader`, `TapeCalibrationHeader` | `ITapeFramedRecord<Self>` | forward to `TapeHeader.ReadBody` + type check, so `ReadRecord<TapeCalibrationHeader>` keeps working |
| `TapeCalibrationCheckpoint` | `ITapeFramedRecord<Self>` | |
| `TapeFileHeader` | `ITapeFramedRecord<Self>` | inline frame; legacy 12-byte form via `TryReadLegacy` |
| `VirtualMediaStateRecord` | `ITapeFramedRecord<Self>` | Appendix A |

**Typical implementation** (immutable domain type + wire DTO):

```csharp
public sealed partial class TapeSetHeader : ITapeFramedRecord<TapeSetHeader>
{
    public TapeRecordKind RecordKind => TapeRecordKind.SetHeader;

    public void WriteBody(TapeFieldWriter w) => SetHeaderWire.Schema.Write(w, SetHeaderWire.From(this));

    public static bool Accepts(TapeRecordKind kind) => kind == TapeRecordKind.SetHeader;

    public static TapeSetHeader ReadBody(TapeFieldReader r)
        => SetHeaderWire.Schema.Read(r, new SetHeaderWire()).ToRecord();

    public static FrameStatus TryReadLegacy(ReadOnlySpan<byte> block, out TapeSetHeader? record)
        => LegacyFramer.TryUnpack(block, LegacyHeaderReader.ReadSetHeader, out record);
}
```

---

## B.7 Records and Carriers

```csharp
/// Writes whole records to a stream: prologue + body in ONE Write. Body built in a pooled buffer.
public sealed class TapeRecordWriter(Stream output)
{
    public void Write<TRecord>(TRecord record) where TRecord : ITapeRecord<TRecord>;

    /// Low-level form for records written by hand (TOC file batches, §B.8): Begin → fields → End.
    public TapeFieldWriter BeginRecord(TapeRecordKind kind);
    public void EndRecord();                     // explicit; never implied by Dispose
}

/// Reads whole records from a stream; never reads beyond the current record.
public sealed class TapeRecordReader(Stream input)
{
    public TapeRecordInfo PeekInfo();            // reads + caches the prologue only
    public TRecord Read<TRecord>() where TRecord : ITapeRecord<TRecord>;   // UnknownKind unless TRecord.Accepts
    public TapeFieldReader ReadRaw(TapeRecordKind expected);               // for hand-read records
    public void Skip();                          // allowed only for skippable kinds (0x8000)
    public long BytesConsumed { get; }
}

/// Record ‖ CRC-64 in a block (headers, checkpoints, virtual media state) or inline (file header).
public static class TapeFrame
{
    /// Writes the frame at block[0..]; returns its length. Throws if it does not fit.
    public static int Pack<T>(T record, Span<byte> destination) where T : ITapeRecord<T>;

    /// Never throws. "TpN#" → 2.1 path; otherwise T.TryReadLegacy.
    public static FrameStatus TryUnpack<T>(ReadOnlySpan<byte> block, out T? record, out int frameLength,
                                           out TapeFormatException? error)
        where T : class, ITapeFramedRecord<T>;
}
```

`TryUnpack` order: magic → prologue bounds (`NotFramed`) → CRC-64 (`CrcMismatch`) → `T.Accepts(kind)`, major,
`ReadBody` (`Unparseable`, with `error` set) → `Ok`.

---

## B.8 Worked Example — TOC File Batches

The batch is the one place where per-entry allocation matters (millions of files), so **write by hand with one
reused wire object; read through the schema**:

```csharp
internal sealed class FileEntryWire
{
    public ulong FileId, AddressBlock, AddressOffset, Length, SizeOnTape, NameShared;
    public FileAttributes Attributes;
    public TapeFileCodec Codec;
    public DateTime CreationTime, LastWriteTime, LastAccessTime;
    public string NameSuffix = "";
    public byte[]? Hash;

    public static readonly TapeSchema<FileEntryWire> Schema = new()      // group schema
    {
        { 1,  e => e.FileId,         (e, v) => e.FileId = v,         FieldFlags.Required },
        { 2,  e => e.AddressBlock,   (e, v) => e.AddressBlock = v,   FieldFlags.Required },
        { 3,  e => e.AddressOffset,  (e, v) => e.AddressOffset = v },
        { 4,  e => e.Length,         (e, v) => e.Length = v },
        { 5,  e => e.SizeOnTape,     (e, v) => e.SizeOnTape = v },
        { 6,  e => e.Attributes,     (e, v) => e.Attributes = v,     FieldFlags.Bitmask },
        { 7,  e => e.Codec,          (e, v) => e.Codec = v,          0, TapeFileCodec.Stored },
        { 8,  e => e.CreationTime,   (e, v) => e.CreationTime = v,   FieldFlags.Required },
        { 9,  e => e.LastWriteTime,  (e, v) => e.LastWriteTime = v,  FieldFlags.Required },
        { 10, e => e.LastAccessTime, (e, v) => e.LastAccessTime = v, FieldFlags.Required },
        { 11, e => e.NameShared,     (e, v) => e.NameShared = v },
        { 32, e => e.NameSuffix,     (e, v) => e.NameSuffix = v ?? "", FieldFlags.Required },  // "" is written
        { 33, e => e.Hash,           (e, v) => e.Hash = v },
    };

    public void From(TapeFileInfo f, TapeNameFrontCoder coder) { /* fill fields; coder.Encode(name) */ }
    public TapeFileInfo ToRecord(TapeNameFrontCoder coder) { /* coder.Decode(...); build TapeFileInfo */ }
}

// TapeSetTOC — writing its batches
private void WriteBatches(TapeRecordWriter w)
{
    var wire = new FileEntryWire();
    var coder = new TapeNameFrontCoder();
    foreach (var chunk in Batches(c_maxFilesPerBatch, c_maxBatchBodyBytes))
    {
        coder.Reset();
        TapeFieldWriter body = w.BeginRecord(TapeRecordKind.TocFileBatch);
        foreach (var file in chunk)
        {
            wire.From(file, coder);
            TapeFieldWriter g = body.BeginGroup(BatchTags.File);
            FileEntryWire.Schema.Write(g, wire);
            body.EndGroup(g);
        }
        w.EndRecord();
    }
}

// … and reading one batch
private void ReadBatch(TapeRecordReader rr)
{
    var coder = new TapeNameFrontCoder();
    TapeFieldReader body = rr.ReadRaw(TapeRecordKind.TocFileBatch);
    while (body.MoveNext())
    {
        if (body.Number != BatchTags.File) { body.SkipUnknown(); continue; }
        var wire = FileEntryWire.Schema.Read(body.ReadGroup(), new FileEntryWire());
        Append(wire.ToRecord(coder));
    }
}
```

`c_maxBatchBodyBytes` is checked against `body.Written.Length` after each entry; the chunking helper closes the
batch when either limit is reached.

---

## B.9 Tests (Phase 1, step 11)

| Test | Covers |
|---|---|
| `Schema_AllProductSchemas_Valid` | touches every `static readonly` schema (test-side reflection is fine) → §B.5.4 passes |
| `Schema_Validation_Rejects_*` | bad number, duplicate, wrong range for shape, `Repeated` scalar, `Required` + default, negative enum member |
| `Schema_OverloadBinding_AllPrimitives` | a test wire type with one property per supported CLR type + an enum + a `[Flags]` enum; compiles and round-trips |
| `Schema_OptionalDefault_Elided_AndRestored` | default not on the wire; absent → schema default even when the ctor sets something else |
| `Schema_RequiredEmptyString_Written` | `""` present on the wire, round-trips |
| `Schema_Missing_Required_Refused` / `Duplicate_Refused` | |
| `Schema_UnknownField_Skipped` / `UnknownCritical_Refused` | hand-crafted bodies |
| `Schema_KnownField_CriticalBitIgnored` | R3 |
| `Schema_Enum_Undefined_Refused` / `Bitmask_Accepted` | |
| `Schema_UInt_OutOfRange_Refused` | `ulong` value into a `uint` property |
| `Schema_Inherits_SharedFields` | contravariant base fields, ascending merge |
| `Schema_Validate_Hook_CrossCheck` | e.g. `SetId` required when `DataFormat = V2` |
| `Record_PolymorphicHeader_Dispatch` | `Read<TapeHeader>` builds the right subtype; `Read<TapeCalibrationHeader>` refuses a media header |
| `Frame_AllStatuses` | `Ok`, `NotFramed`, `CrcMismatch`, `Unparseable` (+ `error` populated), legacy forward |
| `RecordReader_NoReadAhead` | trailing bytes after a record remain unread |
| `Batch_LargeSet_AllocationsBounded` | 100k entries: one `FileEntryWire` on write (allocation counter) |

---

## B.10 Corrections to the Main Document

| Where | Change |
|---|---|
| §5.7 schema sample | parameter order is **flags, then default** (`{ 6, …, FieldFlags.Critical, TapeDataFormat.V2 }`); `WriteTo` / `ReadFrom` → `WriteBody` / `ReadBody` (prologue owned by `TapeRecordWriter`); `FieldFlags.RequiredWhenV2` does not exist → `validate:` hook |
| §5.7 schema sample | schemas target **`Wire` DTOs** for immutable types (rule B.2-4) |
| §8.1 | add `ITapeField<in T>`, `ITapeValueCodec<TValue>`, `FieldShape`, `TapeRecordInfo`; `ITapeFramedRecord.ReadLegacy` → `TryReadLegacy(block, out record)` returning `FrameStatus` |
| §4.1 | note: value encodings inside a field carry no own length prefix (rule B.2-5) |
| §12 Phase 1 | step 9 implements this appendix; step 11 adds §B.9 |
