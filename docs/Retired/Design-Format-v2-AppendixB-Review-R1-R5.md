# Format Core — Review of R1–R5 and R6 Test Plan

**Scope:** `TapeLibNET/Format/*` after Appendix B (Revised) steps R1–R5, and `FormatCoreTests.cs`.
**Verdict:** R1–R5 are implemented faithfully. The review found **7 small defects (F1–F7)**, mostly on error
paths. Apply them **first**, then add `FormatCoreCoverageTests.cs`. That file assumes F1–F7 are in place;
without F3, the test `FieldWriter_DescendingNumber_Throws` **crashes the test host** (a failed `Debug.Assert`
calls `Environment.FailFast` in .NET).

---

## 1. Fixes

| # | Severity | File | Defect | Consequence |
|---|---|---|---|---|
| F1 | High | `TapeRecordWriter` | `Write(kind, fill)` does not abandon the record when `fill` throws | The writer is stuck: every later `BeginRecord` throws "previous record was not ended" |
| F2 | Medium | `TapeFrame` | `ReadInline` reads the CRC with `ReadExactly` | A truncated frame throws `EndOfStreamException`, breaking G8. Phase 4 restores file headers through this path |
| F3 | High | `TapeFieldWriter` | Ascending field order is enforced by `Debug.Assert` | Untestable, and fail-fast in DEBUG test runs (G6 already bans asserts on this account) |
| F4 | Medium | `TapeSchema` | An absent optional **group** keeps its previous value (`ApplyDefault` is a no-op) | A wire object reused across reads leaks a group from the previous record (§B.8.4 pattern) |
| F5 | Low | `TapeFieldReader`, `TapeSchema` | `MissingRequired` carries no field number; `MoveNext` errors carry no record | Weaker diagnostics |
| F6 | Low | `TapeFieldWriter`, `TapeBuffer`, `TapeRecordReader` | Size limits on write, and on the span prologue path, report `BadValue` | Inconsistent with `LimitExceeded` on the read path |
| F7 | Medium | `TapeRecord` | Non-format exceptions from `ReadBody` escape (`ArgumentException`, `OverflowException`) | `TryUnpack` "never throws" no longer holds. Phase 3 `ToRecord` code (e.g. `checked((int)NameShared)`) would hit this |

### F1 — `TapeRecordWriter.Write(kind, fill)`

```csharp
public void Write(TapeRecordKind kind, Action<TapeFieldWriter> fill)
{
    ArgumentNullException.ThrowIfNull(fill);
    TapeFieldWriter fields = BeginRecord(kind);
    try
    {
        fill(fields);
    }
    catch
    {
        AbandonRecord();
        throw;
    }
    EndRecord();
}
```

### F2 — `TapeFrame.ReadInline`

```csharp
byte[] stored = new byte[CrcLength];
if (stream.ReadAtLeast(stored, CrcLength, throwOnEndOfStream: false) < CrcLength)
    throw new TapeFormatException(FormatErrorKind.Truncated, "stream ended inside the frame CRC-64");
```

### F3 — `TapeFieldWriter.WriteHeader`

Replace the `Debug.Assert` with a check that throws. A single integer compare is cheap enough for release builds.

```csharp
if (number < m_lastNumber)
    throw new InvalidOperationException($"field {number} written after field {m_lastNumber}: numbers must ascend");
```

Also remove `MaxFieldNumber` (R2 asked for it; the schema limits numbers to 1–63).

### F4 — `TapeSchema` single group

- Group setter becomes nullable: `Add<TChild>(int n, Func<T, TChild?> get, Action<T, TChild?> set, TapeSchema<TChild> child, FieldFlags flags = None)`.
- `GroupField.ApplyDefault(T target) => set(target, null);`
- **Document on the repeated overload:** *Read appends each occurrence; read schemas with repeated groups into
  a fresh target.* (`FileEntryWire` has no groups, so the §B.8.4 reuse stays safe.)

### F5 — error context

```csharp
// TapeFieldReader
public TapeFormatException Error(FormatErrorKind kind, string detail, int? field = null) => new(kind, detail)
{
    Record = Record.Kind == 0 ? null : Record.Kind,
    Field = field ?? (m_hasCurrent ? Number : null),
    Offset = field is null && m_hasCurrent ? m_valueStart : null,
};
```

- `TapeSchema.Read`: `throw r.Error(FormatErrorKind.MissingRequired, $"…", field.Number);`
- `MoveNext`: throw the invalid-tag, overrun and header-overrun errors through `Error(...)`. To do that, make
  `ReadVarUIntInBody` an instance method.

### F6 — `LimitExceeded` everywhere

- `TapeFieldWriter.WriteString` / `WriteBytes`, and `TapeBuffer.Ensure`: throw
  `new TapeFormatException(FormatErrorKind.LimitExceeded, …)`.
- `TapeRecordReader.ParsePrologue`: new `PrologueStatus.TooLarge` when `bodyLength > MaxRecordBody`.
  `Parse` maps it to `LimitExceeded`. `TapeFrame.TryUnpackRecord` keeps mapping every non-`Ok` status to `NotFramed`.
- **Update two existing tests** in `FormatCoreTests.cs`: `Limits_StringAboveMax_RefusedOnWrite` and
  `Limits_RecordBodyAboveMax_RefusedOnWrite` now expect `LimitExceeded`.

### F7 — `TapeRecord.Read<T>`

```csharp
try
{
    return T.ReadBody(Fields);
}
catch (Exception ex) when (ex is ArgumentException or OverflowException)
{
    // Domain conversion refused the data (ToRecord): report it as a format error (G8).
    throw new TapeFormatException(FormatErrorKind.BadValue, $"{typeof(T).Name}: {ex.Message}", ex) { Record = Kind };
}
```

`InvalidOperationException` is deliberately **not** mapped: it signals a programming error, such as a broken
schema, and must stay visible as one.

---

## 2. Non-blocking notes

- **Corrupt TOC copies usually fail before the CRC check.** `TapeCrc64Envelope.Read` parses first, so a flipped
  byte typically fails as `MissingRequired`, `Overrun` and so on before the CRC is compared. That is why
  `Envelope_FlippedBodyByte_CrcMismatch` accepts eight error kinds; rename it `…_Refused`. **For Phase 3:** the
  agent must treat **any** `TapeFormatException` on a TOC copy as "this copy is bad" and try the second copy.
- **Optional `bytes` fields read back as an empty array, not `null`** — the setter type is non-null. Appendix B
  §B.3.3 says `null`; align the document with the code. `FileEntryWire.ToRecord` maps an empty `Hash` to "no hash".
- **`FormatCoreTests.Sample` uses the `TocSet` kind.** This is harmless, but it reads oddly once the real TOC set
  record exists. Leave it as is.

---

## 3. R6 coverage — existing suite vs. plan

| R6 item | Before | Now in `FormatCoreCoverageTests` |
|---|---|---|
| All product schemas valid | ✗ | `Schema_AllProductSchemas_Valid` (reflection over TapeLibNET) |
| Validation rejects misdeclarations | duplicate only | 9 tests: range, shape vs range, bulk bytes ≥ 48 allowed, `Repeated` scalar, `Required` + default, `Bitmask` non-enum, child with kind, `Add` after freeze, inherited duplicate, group schema has no kind |
| Overload binding, all primitives | ✗ | `Schema_OverloadBinding_AllPrimitives_RoundTrip` (14 CLR types + group + typed repeated group) |
| Optional defaults elided **and applied** | elided only | `…_Elided`, `…_AppliedWhenAbsent`, `…_ResetOnReusedTarget`, `Schema_AbsentOptionalGroup_ResetToNull` (F4) |
| Required empty string / optional empty blob | string only | covered by the elision test |
| Missing / duplicate **with context** | kind only | `Errors_Duplicate_CarriesContext`, `Errors_MissingRequired_NamesTheField` (F5), `Errors_PrimitiveDecode_GainsContext`, `Errors_Overrun_CarriesRecord` (F5) |
| Unknown / critical / known-critical | ✓ | — |
| `Bitmask` combined accepted | ✗ (**FileAttributes**) | round trip + `Schema_Bitmask_AboveUnderlyingRange_Refused` |
| Negative on write / out of range on read | ✗ | `Schema_NegativeInt_ThrowsOnWrite`, theory over byte / ushort / uint / int / long |
| Validate on read **and** write | ✗ | `Schema_Validate_ThrowsOnWrite_RefusesOnRead` |
| Inheritance | ✗ | `Schema_Inherits_SharedFields` |
| Timestamp `Unspecified` | ✗ | `Timestamp_Unspecified_TakenAsUtc_NeverShifted` + `MinValue` elision |
| Groups Begin / End | depth only | reuse, begin twice, end without begin, foreign child, nested still open |
| Descending field numbers | ✗ (untestable) | `FieldWriter_DescendingNumber_Throws` (F3) |
| Polymorphic dispatch | ✗ | `Record_PolymorphicDispatch` |
| Frame statuses **with `error` / `frameLength`**, legacy forward | statuses only | `Frame_Unparseable_ReportsErrorAndLength` (7 cases), `Frame_OkAndCrcMismatch_NoError`, `Frame_TryUnpackWithLegacy_*` |
| No read-ahead, non-seekable | seekable only | trickle reader (1-byte reads), inline frame, envelope |
| Envelope over non-seekable, inner not disposed | ✗ | `Envelope_ForwardOnly_BothWays_NoReadAhead_InnerNotDisposed` |
| Batch write allocations | ✗ | `Batch_WriteWithReusedWire_AllocatesNothingPerEntry` (100 k entries) |

**Regression tests for the fixes:** F1 `Writer_FillThrows_RecordAbandoned_WriterReusable` · F2
`Frame_Inline_Truncated_IsFormatError` · F6 `Prologue_SpanBodyAboveLimit_LimitExceeded` · F7
`Record_DomainExceptionInReadBody_BecomesFormatError`.
**Further gaps closed:** `Read<T>` at a clean end of stream, `ReadRecord(expected)` with the wrong kind, older
major, stream ending inside the fixed prologue, writer refusing an unregistered kind, field number 0,
`SkipUnknown`, reading a value before `MoveNext`.

---

## 4. Edits to Appendix B (Revised)

| Where | Edit |
|---|---|
| G8 | Append: *`TapeRecord.Read<T>` maps `ArgumentException` / `OverflowException` from `ReadBody` to `BadValue` (F7).* |
| G9 | *Writers emit ascending field numbers — enforced by `InvalidOperationException` in the field writer.* |
| §B.3.2 | `Error(FormatErrorKind kind, string detail, int? field = null)` |
| §B.3.3 | Group setter `Action<T, TChild?>`; absent optional group → `null`. Optional `byte[]` default: empty array. Repeated groups append: read them into fresh targets. |
| §B.4 R6 | *Implemented in `FormatCoreTests.cs` + `FormatCoreCoverageTests.cs`.* |
