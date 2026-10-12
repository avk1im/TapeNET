# Design-Format-v2 — Appendix B (Revised): Format Core Types

**Status:** binding for implementation. **Replaces:** `Design-Format-v2-AppendixB-CoreTypes.md` — delete; this is the only Appendix B.
**Applies to:** the Phase 1 code already in `TapeLibNET/Format/`, which this document amends (§B.4), and Phase 3
(TOC), which it prepares (§B.8). **Last updated:** 2026-10-02

**How to use this document (Copilot):**

1. Read §B.1–§B.3 once — the rules and the target API.
2. Execute §B.4 **step by step, in order** (R1 → R6). Each step: build, run the Format tests, commit.
3. Apply §B.7 to the master design document in the same PR.
4. Use §B.8 when Phase 3 starts.

Where this document and the master document disagree, **this document wins**.

---

## B.1 Layering

```
 Domain types   TapeTOC · TapeSetTOC · TapeHeader… · TapeCalibrationCheckpoint · VirtualMediaStateRecord
      │  implement ITapeRecord<TSelf> (or ITapeFramedRecord<TSelf>) — body only, via a schema
      ▼
 Wire DTOs      mutable mirrors of immutable domain types (FileEntryWire, SetHeaderWire, …)
      ▼
 Schema         TapeSchema<T> — one line per field, both directions, no reflection
      ▼
 Fields         TapeFieldWriter / TapeFieldReader — Tag · Length · Value, nested groups
      ▼
 Records        TapeRecordWriter / TapeRecordReader / TapeRecord — prologue "TpN#" kind major minor length
      ▼
 Carriers       TapeFrame (Record ‖ CRC-64, block or inline) · TapeCrc64Envelope (TOC stream)
```

Each layer knows only the one below. Domain types never touch prologues, lengths or CRCs.

---

## B.2 Ground Rules (must-follow)

| # | Rule |
|---|---|
| G1 | **No reflection in product code.** The property's own CLR type picks the codec through overload resolution (§B.3.4). |
| G2 | **C# 12.** `TapeFieldWriter` / `TapeFieldReader` stay `sealed class`es (no `ref struct` — they are captured by lambdas and stored). |
| G3 | **Schemas are `static readonly`**, frozen and validated on first use (§B.3.5). A broken schema throws at type initialization; one test touches every product schema. |
| G4 | **Schema targets are mutable with a parameterless constructor.** Immutable domain types get a nested `Wire` class: `Wire.From(domain)` on write, `wire.ToRecord()` on read. Shape mismatches (split address, front-coded name, delta-coded samples) live in `From` / `ToRecord`, never in the schema. |
| G5 | **Integers are non-negative `varuint`.** Every integer field of the catalog is a count, size, index, address, id or volume. Signed values exist only where the catalog says `varint`: timestamps, and inside coded blobs (calibration sample deltas). A negative `int` / `long` on write is a programming error → `ArgumentOutOfRangeException` naming record and field. |
| G6 | **Timestamps are UTC.** Writers normalize: `Utc` as is; `Local` → `ToUniversalTime()`; `Unspecified` → taken as UTC (`SpecifyKind`), **never** shifted. Readers return `DateTimeKind.Utc`. No `Debug.Assert` on `Unspecified` — it would fail-fast the test host. |
| G7 | **Inside a field, values carry no own length prefix** — the field's `Length` delimits them. Every typed read consumes exactly `Value.Length` bytes or throws. |
to a `WIN32_ERROR`. `TapeRecord.Read<T>` maps `ArgumentException` / `OverflowException` from `ReadBody` to `BadValue` (F7). |
| G9 | **Writers emit ascending field numbers** — enforced by `InvalidOperationException` in the field writer; a repeated field repeats in place. Readers accept any order. |
| G10 | **Readers never read past the record they were asked for** — a CRC trailer or a file body follows directly. |
| G11 | **A record the own reader would refuse never reaches tape.** The schema runs its `validate` hook on write too. |

---

## B.3 Target API

### B.3.1 Small types

```csharp
namespace TapeLibNET.Format;

// TapeRecordKind — UNCHANGED (keep the code's names, incl. CalibrationRunHeader).

/// Per-field behaviour in a schema.
[Flags]
public enum FieldFlags
{
    None     = 0,
    Required = 1 << 0,   // always written; absence refuses the record
    Critical = 1 << 1,   // the tag carries the critical bit: a reader that does not know the number refuses
    Repeated = 1 << 2,   // groups only: any number of occurrences
    Bitmask  = 1 << 3,   // [Flags] enums only: no defined-value check on read
}

/// Wire shape of a field; must match its number range (§4.4 of the master document).
public enum FieldShape : byte
{
    Scalar,   // numbers 1–31
    Blob,     // numbers 32–63 (strings and byte arrays 32–47, bulk bytes 48–63)
    Group,    // numbers 48–63
}

/// What a field reader knows about its record — for error context and polymorphic dispatch.
public readonly record struct TapeRecordInfo(TapeRecordKind Kind, byte Major, byte Minor);

public enum FormatErrorKind
{
    NewerMajor, BadMagic, UnknownKind, UnexpectedKind, UnknownCritical, MissingRequired, Duplicate,
    Overrun, Underrun, BadValue, LimitExceeded, CrcMismatch, Truncated, CrossCheck,
}

public sealed class TapeFormatException : FormatException
{
    public FormatErrorKind Kind { get; }
    public TapeRecordKind? Record { get; init; }   // record the error arose in, if known
    public int? Field { get; init; }               // field number, if known
    public int? Offset { get; init; }              // byte offset within the record body, if known
    public TapeFormatException(FormatErrorKind kind, string message, Exception? inner = null);
    internal static TapeFormatException Bad(string message);   // keep
}
```

### B.3.2 Field writer and reader

```csharp
public sealed class TapeFieldWriter
{
    // scalars
    public void WriteUInt     (int number, ulong value,    bool critical = false);
    public void WriteInt      (int number, long value,     bool critical = false);   // ZigZag varint
    public void WriteBool     (int number, bool value,     bool critical = false);
    public void WriteF64      (int number, double value,   bool critical = false);
    public void WriteGuid     (int number, Guid value,     bool critical = false);
    public void WriteTimestamp(int number, DateTime value, bool critical = false);   // G6
    // blobs
    public void WriteString   (int number, string value,   bool critical = false);
    public void WriteBytes    (int number, ReadOnlySpan<byte> value, bool critical = false);
    // groups — one reusable child per depth; no closure, no rented buffer per group
    public TapeFieldWriter BeginGroup(int number, bool critical = false);
    public void EndGroup(TapeFieldWriter child);
    /// Body bytes written so far (batch size control, §B.8.4).
    public int BytesWritten { get; }
}

public sealed class TapeFieldReader
{
    public TapeFieldReader(ReadOnlyMemory<byte> body);                          // keep (tests)
    internal TapeFieldReader(ReadOnlyMemory<byte> body, TapeRecordInfo record);
    public TapeRecordInfo Record { get; }
    public bool MoveNext();
    public int  Number { get; }
    public bool IsCritical { get; }
    public ReadOnlySpan<byte> Value { get; }

    public ulong    ReadUInt();                  // renamed from ReadUInt64
    public long     ReadInt();                   // renamed from ReadInt64 (ZigZag)
    public uint     ReadUInt32();                // keep: range-checked varuint
    public int      ReadInt32();                 // keep: range-checked non-negative varuint
    public long     ReadNonNegativeInt64();      // keep
    public bool     ReadBool();
    public double   ReadF64();
    public Guid     ReadGuid();
    public DateTime ReadTimestamp();
    public string   ReadString();
    public byte[]   ReadBytes();
    public TapeFieldReader ReadGroup();

    /// Unknown field number: no-op, or UnknownCritical when the tag's critical bit is set.
    public void SkipUnknown();
    /// Builds an exception carrying Record, current Field and body Offset.
    public TapeFormatException Error(FormatErrorKind kind, string detail, int? field = null);
}
```

### B.3.3 Schema surface

```csharp
/// One field of a schema. Contravariant: a base wire type's fields serve every derived wire type (§B.3.6).
public interface ITapeField<in T>
{
    int Number { get; }
    FieldFlags Flags { get; }
    FieldShape Shape { get; }
    void Write(TapeFieldWriter w, T source);   // optional field at its default: writes nothing
    void Read(TapeFieldReader r, T target);    // reader positioned on this field
    void ApplyDefault(T target);               // field absent and optional
}

public sealed class TapeSchema<T> : IEnumerable<ITapeField<T>> where T : class
{
    /// <param name="kind">Record kind; null for a group schema (nested only).</param>
    /// <param name="inherits">Fields of a base wire type, e.g. the shared header tags 1–3.</param>
    /// <param name="validate">Cross-field check; returns an error text or null. Run on read AND write (G11).</param>
    public TapeSchema(TapeRecordKind? kind = null,
                      IEnumerable<ITapeField<T>>? inherits = null,
                      Func<T, string?>? validate = null);

    public bool HasKind { get; }
    public TapeRecordKind Kind { get; }                   // InvalidOperationException for group schemas
    public IReadOnlyList<ITapeField<T>> Fields { get; }   // ascending; freezes the schema

    public void Write(TapeFieldWriter w, T source);       // renamed from WriteFields; validate first
    public T Read(TapeFieldReader r, T target);

    // Value fields — DEFAULT BEFORE FLAGS; each type twice: with and without a default.
    public void Add(int n, Func<T, bool>     get, Action<T, bool>     set, bool     @default, FieldFlags flags = 0);
    public void Add(int n, Func<T, bool>     get, Action<T, bool>     set,                    FieldFlags flags = 0);
    // … same pair for byte, ushort, uint, ulong, int, long, double, Guid, DateTime …
    public void Add(int n, Func<T, string?>  get, Action<T, string>   set, string?  @default, FieldFlags flags = 0);
    public void Add(int n, Func<T, string?>  get, Action<T, string>   set,                    FieldFlags flags = 0);
    public void Add(int n, Func<T, byte[]?>  get, Action<T, byte[]>   set,                    FieldFlags flags = 0);
    public void Add<TEnum>(int n, Func<T, TEnum> get, Action<T, TEnum> set, TEnum @default, FieldFlags flags = 0)
        where TEnum : unmanaged, Enum;
    public void Add<TEnum>(int n, Func<T, TEnum> get, Action<T, TEnum> set,                 FieldFlags flags = 0)
        where TEnum : unmanaged, Enum;

    // Groups: an absent optional group is set to null; repeated groups append on read - read them into fresh targets
    public void Add<TChild>(int n, Func<T, TChild?> get, Action<T, TChild?> set,
                            TapeSchema<TChild> child, FieldFlags flags = 0) where TChild : class, new();
    public void Add<TChild>(int n, Func<T, IEnumerable<TChild>> getAll, Action<T, TChild> add,
                            TapeSchema<TChild> child, FieldFlags flags = FieldFlags.Repeated) where TChild : class, new();

    // Escape hatch — must declare its shape so validation covers it
    internal void AddCustom(int n, FieldShape shape, Action<TapeFieldWriter, T> write, Action<TapeFieldReader, T> read,
                            Action<T>? applyDefault = null, FieldFlags flags = 0);
}
```

Defaults when no default is given: numbers `0`, `bool` `false`, `Guid.Empty`, `DateTime` = `TapePrimitives.MinUtc`
`byte[]` empty array,

Optional blobs: `null` **and** empty are both elided and read back as the default. A required string writes `null`
as `""`.

### B.3.4 Why the overloads bind — and the traps

A lambda converts to a delegate only if its body compiles. For a `uint` property, the `ulong` candidate's setter
`(s, v) => s.BlockSize = v` fails (`ulong → uint`), so only the `uint` overload remains; enums only match the
generic overload. The property's type picks the codec — no casts, no reflection.

Write in schema tables:

- `{ n, get, set }` — optional, type default.
- `{ n, get, set, FieldFlags.Required }` — required.
- `{ n, get, set, TapeDataFormat.V2, FieldFlags.Critical }` — optional with an explicit default.
- **Enum defaults as named members**, never `0`.
- **Never a bare `0` as the fourth argument for an integer field.** It binds to `@default` (identity beats the enum
  conversion) — legal but misleading. Write `FieldFlags.None` or omit it.

### B.3.5 Schema algorithms and validation

```csharp
public void Write(TapeFieldWriter w, T source)
{
    Freeze();
    if (m_validate?.Invoke(source) is { } problem)
        throw new InvalidOperationException($"{Describe()}: {problem}");     // G11 — programming error
    foreach (ITapeField<T> field in m_sorted)
        field.Write(w, source);
}

public T Read(TapeFieldReader r, T target)
{
    Freeze();
    ulong seen = 0;                                        // numbers are 1..63
    while (r.MoveNext())
    {
        int n = r.Number;
        ITapeField<T>? field = (uint)n < 64 ? m_byNumber[n] : null;
        if (field is null)
        {
            r.SkipUnknown();                               // UnknownCritical if the tag says so
            continue;
        }
        ulong bit = 1UL << n;                              // known number: critical bit irrelevant (R3)
        if ((seen & bit) != 0 && (field.Flags & FieldFlags.Repeated) == 0)
            throw r.Error(FormatErrorKind.Duplicate, $"field {n} occurs more than once");
        seen |= bit;
        field.Read(r, target);
    }
    foreach (ITapeField<T> field in m_sorted)
    {
        if ((seen & (1UL << field.Number)) != 0)
            continue;
        if ((field.Flags & FieldFlags.Required) != 0)
            throw r.Error(FormatErrorKind.MissingRequired, $"required field {field.Number} is absent");
        field.ApplyDefault(target);                        // absent ⇒ schema default, never the ctor's value
    }
    if (m_validate?.Invoke(target) is { } problem)
        throw r.Error(FormatErrorKind.CrossCheck, problem);
    return target;
}
```

`Freeze()` (first use, thread-safe via `Lazy` or a lock) merges `inherits`, sorts, builds `m_byNumber[64]`, and
throws `InvalidOperationException` when:

| Check | Reason |
|---|---|
| number outside 1–63, or used twice (incl. inherited) | §4.4; bitmask in `Read` |
| `Scalar` outside 1–31; `Blob` outside 32–63; `Group` outside 48–63 | keeps "scalars first" forever |
| `Repeated` on a non-group | only groups repeat |
| `Required` with a default other than the type's zero value | contradictory |
| `Bitmask` on a non-enum | meaningless |
| a group's child schema has a kind | nested groups carry no prologue |
| `Add` called after freeze | schemas are immutable once used |

### B.3.6 Inheritance — shared header tags

```csharp
internal abstract class HeaderWire
{
    public Guid Id;
    public DateTime CreatedUtc;
    public uint BlockSize;

    public static readonly TapeSchema<HeaderWire> Shared = new()          // group-style: no kind, never used alone
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
        { 4,  m => m.Volume,        (m, v) => m.Volume = v,        1 },
        { 5,  m => m.Partition,     (m, v) => m.Partition = v,     MediaPartition.Content },
        { 6,  m => m.TocPlacement,  (m, v) => m.TocPlacement = v,  TocPlacement.InSet },
        { 7,  m => m.HasSetHeaders, (m, v) => m.HasSetHeaders = v },
        { 32, m => m.OriginalName,  (m, v) => m.OriginalName = v },
    };
}
```

Works because `ITapeField<in T>` is contravariant and `IEnumerable<out T>` covariant: an
`ITapeField<HeaderWire>` *is* an `ITapeField<MediaHeaderWire>`.

### B.3.7 Record interfaces

```csharp
/// A top-level record: writes and reads its BODY. TapeRecordWriter / TapeRecord own the prologue.
public interface ITapeRecord<TSelf> where TSelf : ITapeRecord<TSelf>
{
    TapeRecordKind RecordKind { get; }                  // instance: polymorphic bases answer per subtype
    void WriteBody(TapeFieldWriter fields);
    static abstract bool Accepts(TapeRecordKind kind);  // single-kind types: kind == TheirKind
    static abstract TSelf ReadBody(TapeFieldReader fields);   // fields.Record.Kind selects the subtype
}

/// A record carried in a BLOCK frame whose legacy ancestor may still be on tape.
public interface ITapeFramedRecord<TSelf> : ITapeRecord<TSelf> where TSelf : class, ITapeFramedRecord<TSelf>
{
    /// Parses a legacy frame ([int32 len][payload][crc32]) at the start of the block. Never throws.
    /// One-line forward into TapeLibNET.Legacy.
    static abstract TapeFramer.FrameStatus TryReadLegacy(ReadOnlySpan<byte> block, out TSelf? record);
}
```

| Type | Interface | Note |
|---|---|---|
| `TocHeaderWire`, `TapeSetTOC.Wire`, `TocEndWire` | `ITapeRecord` | TOC stream; legacy TOCs are read whole by `LegacyTocReader` |
| `TapeFileHeader` | `ITapeRecord` | inline frame; legacy 12-byte headers are checked per `DataFormat` (§5.8), not through the frame |
| `VirtualMediaStateRecord` | `ITapeRecord` | legacy `.vrt` is dispatched by the loader (Appendix A) |
| `TapeHeader` (abstract) | `ITapeFramedRecord<TapeHeader>` | `Accepts` = the three header kinds; `ReadBody` switches on `fields.Record.Kind` |
| `TapeMediaHeader`, `TapeSetHeader`, `TapeCalibrationHeader` | `ITapeFramedRecord<Self>` | forward to `TapeHeader.ReadBody` + type check |
| `TapeCalibrationCheckpoint` | `ITapeFramedRecord<Self>` | |

### B.3.8 Records and carriers

```csharp
public sealed class TapeRecordWriter(Stream stream) : IDisposable
{
    public void Write<T>(T record) where T : ITapeRecord<T>;    // Begin → WriteBody → End; Abandon on throw
    public void Write(TapeRecordKind kind, Action<TapeFieldWriter> fill);   // keep (hand-written records, tests)
    public TapeFieldWriter BeginRecord(TapeRecordKind kind);    // keep
    public void EndRecord();                                    // keep — explicit, never implied by Dispose
    public void AbandonRecord();                                // keep
    public long BytesWritten { get; }                           // keep
}

public sealed class TapeRecordReader(Stream stream)            // keep the code's model
{
    public TapeRecord? ReadRecord();                            // null only at a clean end of stream
    public TapeRecord  ReadRecord(TapeRecordKind expected);
    public T Read<T>() where T : ITapeRecord<T>;
    public static TapeRecord Parse(ReadOnlySpan<byte> data, out int consumed);
}

public sealed class TapeRecord                                  // keep
{
    public TapeRecordKind Kind { get; }
    public TapeFieldReader Fields { get; }                      // now carries TapeRecordInfo
    public T Read<T>() where T : ITapeRecord<T>;                // checks T.Accepts(Kind)
}

public static class TapeFrame
{
    public static byte[] Pack<T>(T record) where T : ITapeRecord<T>;                              // keep
    public static byte[] PackBlock<T>(T record, int blockSize, Random? padding = null) where T : ITapeRecord<T>;
    public static int WriteInline<T>(Stream stream, T record) where T : ITapeRecord<T>;          // keep
    public static T ReadInline<T>(Stream stream) where T : ITapeRecord<T>;                       // keep

    /// 2.1 only; no magic → NotFramed. Never throws.
    public static TapeFramer.FrameStatus TryUnpack<T>(ReadOnlySpan<byte> data, out T? record,
        out int frameLength, out TapeFormatException? error) where T : class, ITapeRecord<T>;

    /// 2.1, or the legacy form via T.TryReadLegacy when the magic is absent. Never throws.
    public static TapeFramer.FrameStatus TryUnpackWithLegacy<T>(ReadOnlySpan<byte> data, out T? record,
        out int frameLength, out TapeFormatException? error) where T : class, ITapeFramedRecord<T>;
}
```

Not added (decided): `PeekInfo`, `ReadRaw`, `Skip` on the reader — `ReadRecord()` already returns one bounded record
whose `Kind` the caller switches on, and skips unknown skippable kinds itself. No span-based `Pack`.

---

## B.4 Amending the Existing Code — Step by Step

Order matters: each layer sits on the one below. Every step ends with `dotnet build`, the Format tests green, a commit.

### R1 — Errors and primitives

**`TapeFormatException.cs`**

- Derive from `FormatException` (keeps every `catch (FormatException)` in callers working).
- Add `Record`, `Field`, `Offset` as `init` properties; append them to `Message` when set:
  `"… [record TocSet, field 7, body offset 123]"`.
- Constructor: `(FormatErrorKind kind, string message, Exception? inner = null)`.
- `FormatErrorKind`: add `UnexpectedKind`, `LimitExceeded`, `CrossCheck`. Keep `BadMagic`.

**`TapePrimitives.cs`**

- Add `public static readonly DateTime MinUtc = new(0, DateTimeKind.Utc);`
- Add `public static long ToUtcTicks(DateTime value)` implementing G6:
  ```csharp
  value.Kind switch
  {
      DateTimeKind.Utc   => value.Ticks,
      DateTimeKind.Local => value.ToUniversalTime().Ticks,
      _                  => value.Ticks,          // Unspecified: taken as UTC, never shifted
  };
  ```
- `DecodeString` / `DecodeBytes`: size above limit → `LimitExceeded` (was `BadValue`).

### R2 — Field writer and reader

**`TapeFieldWriter.cs`**

- `WriteTimestamp`: `WriteInt(number, TapePrimitives.ToUtcTicks(value), critical)` — replaces `ToUniversalTime()`.
- Remove `MaxFieldNumber = int.MaxValue`; keep accepting any number ≥ 1 (the schema enforces 1–63).
- Track `m_lastNumber`; `Debug.Assert(number >= m_lastNumber)` per write; reset on `BeginGroup` (in the child) and
  when the root is reset for a new record.
- Add `public int BytesWritten => m_buffer.Count;`
- **Replace `WriteGroup(Action)` by `BeginGroup` / `EndGroup`:**
  - field `m_child` (`TapeFieldWriter?`) with its own `TapeBuffer`, created on first use, **reused** afterwards;
  - `BeginGroup(number, critical)`: depth check (`MaxGroupDepth` → `InvalidOperationException`); throws if a group
    is already open on this writer; clears the child buffer; remembers `number` / `critical`; returns the child;
  - `EndGroup(child)`: throws unless `child` is the open child; writes header + `child.Written` into the own buffer;
  - internal `Release()` returns the child buffers to the pool (called by `TapeRecordWriter.Dispose`).
- Migrate every `WriteGroup` caller (tests, `VirtualMediaStateRecord` if any) to the Begin/End pair.

**`TapeFieldReader.cs`**

- Store `TapeRecordInfo`; public ctor uses `default`. `ReadGroup()` passes it on to the child.
- Rename `ReadUInt64` → `ReadUInt`, `ReadInt64` → `ReadInt`. Keep `ReadUInt32`, `ReadInt32`,
  `ReadNonNegativeInt64`, `ReadF64`.
- Add `SkipUnknown()`: `if (IsCritical) throw Error(UnknownCritical, $"feature {Number} unknown to this build (format {TapeFormat.VersionText})")`.
- Add `Error(kind, detail)`: `new TapeFormatException(kind, detail) { Record = Record.Kind, Field = m_hasCurrent ? Number : null, Offset = m_valueStart }`
  (`Record` is null when the info is `default`).
- Wrap every typed read so a `TapeFormatException` from `TapePrimitives` gains `Record` / `Field` / `Offset`
  (one private helper: `T Decode<T>(Func<ReadOnlySpan<byte>, T>)` — or catch-and-rethrow in each accessor).
- Group depth exceeded → `LimitExceeded`.

**`TapeRecord.cs`**

- `Fields` → `new TapeFieldReader(body, new TapeRecordInfo(Kind, Major, Minor))`.

### R3 — Schema

**`TapeSchema.cs`** — rework in place:

1. Replace the private `FieldDef` hierarchy by internal sealed classes implementing **`ITapeField<T>`**:
   `ValueField<T, V>` (scalars, strings, bytes, enums), `GroupField<T, TChild>`, `RepeatedGroupField<T, TChild>`,
   `CustomField<T>`. Each implements `ApplyDefault` (repeated group and custom without `applyDefault`: no-op).
2. Implement `IEnumerable<ITapeField<T>>` (was non-generic `IEnumerable`).
3. Constructor `(TapeRecordKind? kind = null, IEnumerable<ITapeField<T>>? inherits = null, Func<T, string?>? validate = null)`.
   Remove `IsV2` and `FieldFlags.RequiredWhenV2`; add `Repeated`, `Bitmask`.
4. `Freeze()` and its checks (§B.3.5). Replace `HashSet<int>` and `Dictionary` with the `ulong` bitmask and a
   64-slot array.
5. Rename `WriteFields` → `Write(TapeFieldWriter, T)`. **Remove `Write(TapeRecordWriter, T)`** — records go
   through `TapeRecordWriter.Write(record)` → `WriteBody`.
6. Keep the overload order **default before flags** and the no-default twins. Add: `byte`, `ushort` pairs; the
   no-default enum overload; the two group overloads. `AddCustom` gains `FieldShape` and `applyDefault` and becomes
   `internal`.
7. Value encodings (keep the static lambdas — they are allocation-free):
   - `int`, `long`: unsigned `varuint`; on write
     `v < 0 ? throw new ArgumentOutOfRangeException(nameof(v), v, $"field {n} must not be negative")`
     (replaces `checked((ulong)v)` and its `OverflowException`). Read via `ReadInt32` / `ReadNonNegativeInt64`.
   - `byte`, `ushort`: `WriteUInt`; read range-checked → `BadValue`.
   - `DateTime`: `isDefault` compares `ToUtcTicks` of both sides.
   - Enums: as now (`Unsafe` zero-extension, range check); `Enum.IsDefined` **only without `Bitmask`**.
     Without this, `FileAttributes.Archive | ReadOnly` is "undefined" and every TOC becomes unreadable.
8. `Read` per §B.3.5: unknown → `r.SkipUnknown()`; errors via `r.Error(...)`; `ApplyDefault` for absent optional
   fields; `validate` → `CrossCheck`.
9. `Write`: `validate` first (G11).

### R4 — Record interfaces, writer, reader, frame

**`ITapeRecord.cs`** — replace with §B.3.7. `FrameStatus` stays `TapeFramer.FrameStatus` for now; Phase 5 may hoist
it to `Format/` when `TapeFramer` itself is reworked.

**`TapeRecordWriter.cs`**

- `Write<T>(T record)`: `var f = BeginRecord(record.RecordKind); try { record.WriteBody(f); } catch { AbandonRecord(); throw; } EndRecord();`
- Cache the root `TapeFieldWriter` (reset per `BeginRecord`) so group children are reused across records.
- `Dispose`: release the field writers' child buffers, then the own buffer.

**`TapeRecord.cs`** — `Read<T>()`: `RequireSupportedMajor(); if (!T.Accepts(Kind)) throw new TapeFormatException(UnexpectedKind, …); return T.ReadBody(Fields);`

**`TapeRecordReader.cs`**

- `Read<T>()`: `(ReadRecord() ?? throw Truncated).Read<T>()` — no longer relies on a static `T.Kind`.
- `ReadRecord(expected)`: wrong kind → `UnexpectedKind` (was `BadValue`).
- Body above `MaxRecordBody` → `LimitExceeded`.

**`TapeFrame.cs`**

- `Pack`: `using var writer = new TapeRecordWriter(ms); writer.Write(record);` (no `record.WriteTo`).
- `TryUnpack<T>` gains `out int frameLength, out TapeFormatException? error`; `error` is set whenever the status is
  `Unparseable` (unknown kind, newer major, body refused).
- Add `TryUnpackWithLegacy<T>` (§B.3.8): `!TapeFormat.IsV2(data)` → `T.TryReadLegacy(data, out record)`,
  `frameLength = 0`, `error = null`; else as `TryUnpack`.
- Status order: magic → prologue / bounds (`NotFramed`) → CRC-64 (`CrcMismatch`) → major, `T.Accepts`, `ReadBody`
  (`Unparseable` + `error`) → `Ok`.

### R5 — Migrate users

- **`VirtualMediaStateRecord`:** implement `RecordKind`, `WriteBody`, `Accepts`, `ReadBody`; use
  `TapeFrame.Pack` / `TryUnpack` (2.1 only — the legacy `.vrt` branch stays in the loader per Appendix A).
- **Format tests:** renames (`ReadUInt64`, `ReadInt64`, `WriteFields`, `WriteGroup`), new `ITapeRecord` members,
  test wire types per G4.

### R6 — New and updated tests (`TapeLibNET.Tests/Format/`)

*Implemented in `FormatCoreTests.cs` + `FormatCoreCoverageTests.cs`.*

| Test | Covers |
|---|---|
| `Schema_AllProductSchemas_Valid` | touches every `static readonly` schema (test-side reflection allowed) |
| `Schema_Validation_Rejects_*` | number out of range, duplicate (incl. inherited), shape vs range, `Repeated` scalar, `Required` + non-zero default, `Bitmask` non-enum, child schema with kind, `Add` after freeze |
| `Schema_OverloadBinding_AllPrimitives` | one property per supported type + enum + `[Flags]` enum; compiles and round-trips |
| `Schema_OptionalDefault_Elided_AndApplied` | not on the wire; absent → schema default even when the ctor sets another value |
| `Schema_RequiredEmptyString_Written` / `Schema_OptionalEmptyBlob_ReadsDefault` | |
| `Schema_MissingRequired_Refused` / `Duplicate_Refused` | error carries `Record` and `Field` |
| `Schema_UnknownField_Skipped` / `UnknownCritical_Refused` / `KnownField_CriticalBitIgnored` | R3 |
| `Schema_Enum_Undefined_Refused` / `Bitmask_CombinedValue_Accepted` | `FileAttributes.Archive \| ReadOnly` |
| `Schema_Int_Negative_ThrowsOnWrite` / `UInt_OutOfRange_RefusedOnRead` | G5 |
| `Schema_Validate_RefusesOnRead_ThrowsOnWrite` | G11 |
| `Schema_Inherits_SharedFields` | merged ascending; inherited `ApplyDefault` |
| `Timestamp_Unspecified_NotShifted` | `DateTime.MinValue` and `new DateTime(2024, 1, 1)` round-trip with identical ticks under a non-UTC `TimeZoneInfo` (run the conversion in-process; do not change the machine zone) |
| `Group_BeginEnd_ReusesChild` / `Group_MismatchedEnd_Throws` / `Group_DepthLimit` | |
| `FieldWriter_DescendingNumber_Asserts` | DEBUG only |
| `Record_PolymorphicDispatch` | a test base type with two kinds: `Read<Base>` builds the right subtype; `Read<SubA>` refuses kind B with `UnexpectedKind` |
| `Frame_AllStatuses` | `Ok`, `NotFramed`, `CrcMismatch`, `Unparseable` with `error`; `TryUnpackWithLegacy` forwards non-magic data |
| `RecordReader_NoReadAhead` | bytes after a record remain unread on a non-seekable stream |
| `Crc64Envelope_NonSeekable_TrailingBytes` | envelope over a forward-only stream; bytes after the CRC untouched; inner stream **not** disposed by `HashingStream` |
| `Batch_WriteAllocations_Bounded` | 100 k entries via one reused wire object — allocations per entry bounded (`GC.GetAllocatedBytesForCurrentThread`) |

---

## B.5 Kept As Is

`TapeBuffer`, `TapeCrc64Envelope` (pending the `HashingStream` check in R6), `TapePeekStream`, `TapeNameFrontCoder`,
`TapeSampleCoder`, `TapeFormat`, `TapeRecordKind`, the span `TapeRecordReader.Parse` / `ParsePrologue`.

---

## B.6 Known Follow-ups (later phases)

| Phase | Item |
|---|---|
| 4 | `TapeFrame.WriteInline` allocates a `MemoryStream`, a writer and a rented buffer per call — once per file. Add a reusable inline-frame writer owned by the backup session. |
| 5 | Hoist `TapeFramer.FrameStatus` into `Format/` when `TapeFramer` moves to `Legacy/`. |

---

## B.7 Edits to the Master Document (`Design-Format-v2.md`)

| Where | Edit |
|---|---|
| Header, "Augmented by" | Replace the `AppendixB-CoreTypes` line with: *`Design-Format-v2-AppendixB-Revised.md` — format core types; binding for `TapeLibNET/Format/`.* |
| §4.1, `timestamp` row | *varint UTC ticks; writers normalize (`Utc` as is, `Local` converted, `Unspecified` taken as UTC — never shifted); readers return `DateTimeKind.Utc`.* |
| §4.1, after the table | Add: *Integer fields are non-negative `varuint`; `varint` appears only where a table says so. Inside a field, `string` and `bytes` carry no own length — the field's `Length` delimits them; the `bytes` / `string` rows describe the field value.* |
| §4.4, range table | `32–47` strings and byte arrays; `48–63` nested groups **and bulk byte arrays** (calibration `Samples`, virtual-media `Blocks`). |
| §5.1, `TocSet` row 1 | `SetId` — *optional (default empty); the schema's `validate` hook refuses a `V2` set without it.* |
| §5.7, schema sample | Replace by the sample in §B.8.2 of this appendix (wire DTO, default-before-flags, `validate`, `WriteBody` / `ReadBody`). |
| §5.7, "The TOC owns its CRC envelope" | Replace by §B.8.3. |
| §8.1, rows | `TapeFormatException + FormatErrorKind` → *see Appendix B §B.3.1.* `TapeRecordReader / TapeFieldReader` → *`ReadRecord()` returns one bounded `TapeRecord`; dispatch on `Kind`.* `TapeSchema<T>` → *Appendix B §B.3.3.* `ITapeRecord<TSelf>` → *`RecordKind`, `WriteBody`, static `Accepts`, static `ReadBody`.* `ITapeFramedRecord<TSelf>` → *static `TryReadLegacy(block, out record)` returning `FrameStatus`.* Add rows `ITapeField<in T>`, `FieldShape`, `TapeRecordInfo`, `TapeRecord`. |
| §11.2 | Append: *Full list: Appendix B §B.4 R6.* |
| §12, Phase 1 | Append after step 11: *11R. Revise the Format core per Appendix B §B.4 (R1–R6), before Phase 3.* |
| §12, Phase 3, step 17 | Append: *Follow Appendix B §B.8.* |
| §14, Decisions Log | Add rows: *Integers — non-negative varuint (signed only where stated)*; *Timestamps — Unspecified taken as UTC, never shifted*; *Schema API — default before flags; `validate` hook run on read and write; groups via Begin/End.* |

---

## B.8 Preparing Phase 3 — the 2.1 TOC

### B.8.1 Shape

```
TocHeader                         ← TocHeaderWire           ITapeRecord
(TocSet  TocFileBatch*)*          ← TapeSetTOC.Wire          ITapeRecord
                                  ← batch: hand-written record, FileEntryWire per entry (group, repeated field 48)
TocEnd                            ← TocEndWire              ITapeRecord
CRC-64                            ← TapeCrc64Envelope
```

A set with zero files writes no batch. Tag numbers: master document §5.1.

### B.8.2 Wire DTOs and schemas

```csharp
public sealed partial class TapeSetTOC
{
    internal sealed class Wire
    {
        public Guid SetId;
        public DateTime CreationTime, LastSaveTime;
        public uint BlockSize;
        public int Volume;
        public TapeDataFormat DataFormat = TapeDataFormat.V2;
        public ulong NextFileId;
        public TapeHashAlgorithm HashAlgorithm;
        public TapeCompression Compression;
        public ZstdLevel CompressionLevel;
        public bool Incremental, ContinuedFromPrevVolume;
        public ulong FileCount;
        public string Description = "";

        public static readonly TapeSchema<Wire> Schema = new(TapeRecordKind.TocSet,
            validate: w => w.DataFormat == TapeDataFormat.V2 && w.SetId == Guid.Empty ? "V2 set without SetId" : null)
        {
            // ── scalars 1–31 ──
            { 1,  w => w.SetId,                   (w, v) => w.SetId = v },
            { 2,  w => w.CreationTime,            (w, v) => w.CreationTime = v,            FieldFlags.Required },
            { 3,  w => w.LastSaveTime,            (w, v) => w.LastSaveTime = v,            FieldFlags.Required },
            { 4,  w => w.BlockSize,               (w, v) => w.BlockSize = v,               FieldFlags.Required },
            { 5,  w => w.Volume,                  (w, v) => w.Volume = v,                  FieldFlags.Required },
            { 6,  w => w.DataFormat,              (w, v) => w.DataFormat = v,              TapeDataFormat.V2, FieldFlags.Critical },
            { 7,  w => w.NextFileId,              (w, v) => w.NextFileId = v,              FieldFlags.Required },
            { 8,  w => w.HashAlgorithm,           (w, v) => w.HashAlgorithm = v,           TapeHashAlgorithm.Crc32 },
            { 9,  w => w.Compression,             (w, v) => w.Compression = v,             TapeCompression.None },
            { 10, w => w.CompressionLevel,        (w, v) => w.CompressionLevel = v,        ZstdLevel.Default },
            { 11, w => w.Incremental,             (w, v) => w.Incremental = v },
            { 12, w => w.ContinuedFromPrevVolume, (w, v) => w.ContinuedFromPrevVolume = v },
            { 13, w => w.FileCount,               (w, v) => w.FileCount = v,               FieldFlags.Required },
            // ── strings 32–47 ──
            { 32, w => w.Description,             (w, v) => w.Description = v },
        };

        public static Wire From(TapeSetTOC set) => new() { /* copy; FileCount = set.Count; times already UTC */ };
        public void ApplyTo(TapeSetTOC set) { /* copy back; file list filled by the batches */ }
    }
}

internal sealed class FileEntryWire
{
    public ulong FileId, AddressBlock, AddressOffset, Length, SizeOnTape, NameShared;
    public FileAttributes Attributes;
    public TapeFileCodec Codec;
    public DateTime CreationTime, LastWriteTime, LastAccessTime;
    public string NameSuffix = "";
    public byte[]? Hash;

    public static readonly TapeSchema<FileEntryWire> Schema = new()            // group schema: no kind
    {
        { 1,  e => e.FileId,         (e, v) => e.FileId = v,         FieldFlags.Required },
        { 2,  e => e.AddressBlock,   (e, v) => e.AddressBlock = v,   FieldFlags.Required },
        { 3,  e => e.AddressOffset,  (e, v) => e.AddressOffset = v },
        { 4,  e => e.Length,         (e, v) => e.Length = v },
        { 5,  e => e.SizeOnTape,     (e, v) => e.SizeOnTape = v },
        { 6,  e => e.Attributes,     (e, v) => e.Attributes = v,     FieldFlags.Bitmask },
        { 7,  e => e.Codec,          (e, v) => e.Codec = v,          TapeFileCodec.Stored, FieldFlags.Critical },
        { 8,  e => e.CreationTime,   (e, v) => e.CreationTime = v,   FieldFlags.Required },
        { 9,  e => e.LastWriteTime,  (e, v) => e.LastWriteTime = v,  FieldFlags.Required },
        { 10, e => e.LastAccessTime, (e, v) => e.LastAccessTime = v, FieldFlags.Required },
        { 11, e => e.NameShared,     (e, v) => e.NameShared = v },
        { 32, e => e.NameSuffix,     (e, v) => e.NameSuffix = v,     FieldFlags.Required },   // "" is written
        { 33, e => e.Hash,           (e, v) => e.Hash = v },
    };

    public void From(TapeFileInfo f, TapeNameFrontCoder coder)
    {
        NameSuffix = coder.Encode(f.FullName, out int shared).ToString();
        NameShared = (ulong)shared;
        // … the rest 1:1; Address split into AddressBlock / AddressOffset; times via the …Utc values
    }

    public TapeFileInfo ToRecord(TapeNameFrontCoder coder)
    {
        string name = coder.Decode(checked((int)NameShared), NameSuffix);   // NameShared > int.MaxValue → Decode refuses
        // … build TapeFileInfo (constructor-only type)
    }
}
```

`Codec` is **critical**: a future codec value must refuse the set, not restore garbage (R3 already refuses an
undefined value of a known field; the critical bit additionally protects readers that drop the field).

### B.8.3 Save and load

```csharp
// TapeTOC
public void SaveTo(Stream stream)
{
    LastSaveTime = DateTime.UtcNow;
    TapeCrc64Envelope.Write(stream, s =>
    {
        using var w = new TapeRecordWriter(s);
        w.Write(TocHeaderWire.From(this));
        foreach (TapeSetTOC set in m_setTOCs)
        {
            w.Write(TapeSetTOC.WireRecord.From(set));     // ITapeRecord adapter over Wire
            set.WriteBatches(w);                           // §B.8.4
        }
        w.Write(TocEndWire.From(this));
    });
    LoadedFromLegacy = false;
}

public static TapeTOC LoadFrom(Stream stream)
{
    Stream s = TapePeekStream.Wrap(stream, out ReadOnlySpan<byte> magic);
    return TapeFormat.IsV2(magic)
        ? TapeCrc64Envelope.Read(s, ReadRecords)
        : LegacyTocReader.Read(s);                         // Phase 2; sets LoadedFromLegacy
}

private static TapeTOC ReadRecords(Stream s)
{
    var r = new TapeRecordReader(s);
    var toc = TocHeaderWire.ToToc(r.Read<TocHeaderWire>());
    TapeSetTOC? set = null;
    var coder = new TapeNameFrontCoder();
    while (true)
    {
        TapeRecord rec = r.ReadRecord()
            ?? throw new TapeFormatException(FormatErrorKind.Truncated, "TOC ends without a TocEnd record");
        switch (rec.Kind)
        {
            case TapeRecordKind.TocSet:
                FinishSet(set);                            // FileCount cross-check of the previous set
                set = rec.Read<TapeSetTOC.WireRecord>().ToSet();
                toc.AddLoadedSet(set);
                break;
            case TapeRecordKind.TocFileBatch:
                if (set is null)
                    throw rec.Fields.Error(FormatErrorKind.UnexpectedKind, "file batch before any set");
                set.ReadBatch(rec.Fields, coder);          // coder.Reset() inside
                break;
            case TapeRecordKind.TocEnd:
                FinishSet(set);
                rec.Read<TocEndWire>().CrossCheck(toc);    // SetCount, TotalFileCount → CrossCheck
                return toc;
            default:
                throw rec.Fields.Error(FormatErrorKind.UnexpectedKind, $"{rec.Kind} inside a TOC stream");
        }
    }
}
```

- `ReadRecord()` already skips unknown **skippable** kinds; any other unknown kind throws `UnknownKind` inside it.
- A `null` from `ReadRecord()` is never "end of TOC" — only `TocEnd` is.
- Cross-checks: `TocHeader.SetCount == TocEnd.SetCount == sets read`; per set `FileCount == files read`;
  `TocEnd.TotalFileCount == Σ`. Any mismatch → `CrossCheck`; the agent then tries the second TOC copy as today.

### B.8.4 Batches

```csharp
private const int c_maxFilesPerBatch  = 4096;
private const int c_maxBatchBodyBytes = 1024 * 1024;

internal void WriteBatches(TapeRecordWriter w)
{
    var wire = new FileEntryWire();                        // ONE per set — no per-entry allocation besides the suffix
    var coder = new TapeNameFrontCoder();
    int i = 0;
    while (i < m_files.Count)
    {
        coder.Reset();                                     // batches decode on their own
        TapeFieldWriter body = w.BeginRecord(TapeRecordKind.TocFileBatch);
        int inBatch = 0;
        do
        {
            wire.From(m_files[i++], coder);
            TapeFieldWriter g = body.BeginGroup(BatchTags.File);
            FileEntryWire.Schema.Write(g, wire);
            body.EndGroup(g);
        }
        while (i < m_files.Count && ++inBatch < c_maxFilesPerBatch && body.BytesWritten < c_maxBatchBodyBytes);
        w.EndRecord();
    }
}

internal void ReadBatch(TapeFieldReader body, TapeNameFrontCoder coder)
{
    coder.Reset();
    var wire = new FileEntryWire();
    while (body.MoveNext())
    {
        if (body.Number != BatchTags.File) { body.SkipUnknown(); continue; }
        FileEntryWire.Schema.Read(body.ReadGroup(), wire);    // ApplyDefault resets every absent field
        AppendLoaded(wire.ToRecord(coder));
    }
}
```

Reusing one `FileEntryWire` on read is safe **only because absent optional fields are reset by `ApplyDefault`**
(R3, item 8) — a `Hash` from the previous entry can never leak into the next.

### B.8.5 Domain rules to wire in (master document §5.2, §6.2, §8.5)

- `SetId`: minted for a new set, a reused trailing empty set and `ReplaceCurrentSetTOC`; **carried** to a
  continuation set via `TapeSetTOCParams`. `NextFileId` starts at 1 and is carried likewise.
- `GenerateFileId()` lives on `TapeSetTOC`; `FileId == 0` is invalid.
- Legacy sets loaded by `LegacyTocReader`: `DataFormat = Legacy`, `SetId = Guid.Empty`, `FileId = UID`,
  `NextFileId = max + 1`. They round-trip through a 2.1 TOC unchanged (`DataFormat` written, critical).
- All TOC, set and file times are UTC in memory (`DateTime.UtcNow`, `…Utc` file APIs); displays convert.
- `TocHeader.Description` clamped to 8 KiB, `WrittenBy` to 256 B (`TapePrimitives.ClampToUtf8Bytes`), so the
  header fits the first 16 KiB TOC block (identification anchor).
- `TapeTOC.TryPeek`: `TapeFormat.IsV2(block)` → `TapeRecordReader.Parse(block, out _)` and require
  `Kind == TocHeader`; else the legacy probe.

### B.8.6 Phase 3 TOC tests

| Test | Covers |
|---|---|
| `Toc21_RoundTrip_Full` | multi-set, incremental, continuation, empty set, Unicode / long names, hashes, all codecs |
| `Toc21_BatchBoundaries` | 0, 1, 4 096, 4 097 files; a batch closed by the 1 MiB limit (long names) |
| `Toc21_FrontCoding_ResetsPerBatch` | first entry of every batch has `NameShared = 0` (inspect raw records) |
| `Toc21_EmptySet_NoBatch` | |
| `Toc21_CrossCheck_*` | tampered `SetCount`, `FileCount`, `TotalFileCount` → `CrossCheck` |
| `Toc21_MissingTocEnd_Truncated` / `Toc21_CrcMismatch` | |
| `Toc21_UnknownSkippableRecord_Skipped` / `UnknownKind_Refused` | hand-crafted kind `0x81FF` / `0x01FF` |
| `Toc21_UnknownField_Skipped` / `UnknownCriticalField_Refused` | in set and file entry |
| `Toc21_V2SetWithoutSetId_ThrowsOnWrite_RefusedOnRead` | `validate` both ways |
| `Toc21_LegacySet_RoundTrips` | `DataFormat = Legacy`, empty `SetId`, `FileId = UID` preserved |
| `Toc21_TrailingBytesAfterCrc_Untouched` | non-seekable stream |
| `Toc21_FileAttributes_Combined` | `Archive \| ReadOnly \| Hidden` round-trips (`Bitmask`) |
| `Toc21_Timestamps_TimeZoneIndependent` | `Format_TimeZoneShift` of the master document |
