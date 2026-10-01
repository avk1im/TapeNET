using System.Runtime.CompilerServices;

namespace TapeLibNET.Format;

/// <summary>Per-field behaviour in a <see cref="TapeSchema{T}"/> (Design-Format-v2 §4.4, §5.7).</summary>
[Flags]
public enum FieldFlags
{
    /// <summary>Optional, non-critical: elided on write when equal to the default.</summary>
    None = 0,

    /// <summary>Always written; absent on read refuses the record.</summary>
    Required = 1,

    /// <summary>The critical bit of the tag: a reader that does not know the field must refuse the record.</summary>
    Critical = 2,

    /// <summary>
    /// Required only when <see cref="TapeSchema{T}.IsV2"/> says the instance is a V2 one (e.g. <c>SetId</c>, absent
    ///  for legacy sets). Always written when present in memory; checked after the read.
    /// </summary>
    RequiredWhenV2 = 4,
}

/// <summary>
/// Declarative field table of a record: one line per field, both directions, no reflection (Design-Format-v2 §5.7).
/// </summary>
/// <remarks>
/// <para>
/// Collection-initializer form: <c>new(kind) { { 2, s => s.X, (s, v) => s.X = v, FieldFlags.Required }, ... }</c>.
/// Typed <c>Add</c> overloads exist per primitive, plus a generic one for enums, plus
///  <see cref="AddCustom"/> for groups, repeated fields and other non-1:1 shapes.
/// </para>
/// <para>
/// Writing emits ascending field numbers and elides OPTIONAL fields at their default; required fields are always
///  written. Reading accepts any order, skips unknown non-critical fields, refuses unknown critical ones, refuses
///  duplicates of non-repeated fields and refuses a missing required field. Interpretation-driving enums
///  (<c>DataFormat</c>, <c>HashAlgorithm</c>, ...) refuse undefined values (R3).
/// </para>
/// </remarks>
/// <typeparam name="T">The record type.</typeparam>
public sealed class TapeSchema<T>(TapeRecordKind kind) : System.Collections.IEnumerable where T : class
{
    private abstract class FieldDef(int number, FieldFlags flags, bool repeated)
    {
        public int Number { get; } = number;
        public FieldFlags Flags { get; } = flags;
        public bool Repeated { get; } = repeated;
        public bool Critical => (Flags & FieldFlags.Critical) != 0;

        public abstract void Write(TapeFieldWriter w, T obj);
        public abstract void Read(TapeFieldReader f, T obj);
    }

    private sealed class ValueField<V>(int number, FieldFlags flags, Func<T, V> get, Action<T, V> set,
        V defaultValue, Action<TapeFieldWriter, int, V, bool> write, Func<TapeFieldReader, V> read,
        Func<V, V, bool> isDefault)
        : FieldDef(number, flags, repeated: false)
    {
        public override void Write(TapeFieldWriter w, T obj)
        {
            V value = get(obj);
            bool required = (Flags & (FieldFlags.Required | FieldFlags.RequiredWhenV2)) != 0;
            if (!required && isDefault(value, defaultValue))
                return;
            write(w, Number, value, Critical);
        }

        public override void Read(TapeFieldReader f, T obj) => set(obj, read(f));
    }

    private sealed class CustomField(int number, FieldFlags flags, bool repeated,
        Action<TapeFieldWriter, T> write, Action<TapeFieldReader, T> read)
        : FieldDef(number, flags, repeated)
    {
        public override void Write(TapeFieldWriter w, T obj) => write(w, obj);
        public override void Read(TapeFieldReader f, T obj) => read(f, obj);
    }

    private readonly List<FieldDef> m_fields = [];
    private readonly Dictionary<int, FieldDef> m_byNumber = [];
    private bool m_sorted = true;

    /// <summary>The record kind this schema writes and reads.</summary>
    public TapeRecordKind Kind { get; } = kind;

    /// <summary>Tells whether an instance is a V2 one, for <see cref="FieldFlags.RequiredWhenV2"/>. Null = always.</summary>
    public Func<T, bool>? IsV2 { get; init; }

    // Collection-initializer support
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => m_fields.GetEnumerator();

    private void Register(FieldDef def)
    {
        if (def.Number < 1)
            throw new ArgumentOutOfRangeException(nameof(def), "field numbers start at 1");
        if (!m_byNumber.TryAdd(def.Number, def))
            throw new InvalidOperationException($"field number {def.Number} is declared twice in schema {Kind}");

        // Numbers are never reused; a schema table normally lists them ascending anyway.
        if (m_fields.Count > 0 && m_fields[^1].Number > def.Number)
            m_sorted = false;
        m_fields.Add(def);
    }

    private List<FieldDef> Sorted()
    {
        if (!m_sorted)
        {
            m_fields.Sort(static (a, b) => a.Number.CompareTo(b.Number));
            m_sorted = true;
        }
        return m_fields;
    }

    #region *** Typed Add overloads ***

    private void AddValue<V>(int number, Func<T, V> get, Action<T, V> set, V defaultValue, FieldFlags flags,
        Action<TapeFieldWriter, int, V, bool> write, Func<TapeFieldReader, V> read, Func<V, V, bool>? isDefault = null)
        => Register(new ValueField<V>(number, flags, get, set, defaultValue, write, read,
            isDefault ?? EqualityComparer<V>.Default.Equals));

    /// <summary>Adds a <c>varuint</c> field mapped to <see cref="ulong"/>.</summary>
    public void Add(int number, Func<T, ulong> get, Action<T, ulong> set, ulong defaultValue, FieldFlags flags = FieldFlags.None)
        => AddValue(number, get, set, defaultValue, flags, static (w, n, v, c) => w.WriteUInt(n, v, c), static f => f.ReadUInt64());

    /// <inheritdoc cref="Add(int, Func{T, ulong}, Action{T, ulong}, ulong, FieldFlags)"/>
    public void Add(int number, Func<T, ulong> get, Action<T, ulong> set, FieldFlags flags = FieldFlags.None)
        => Add(number, get, set, 0UL, flags);

    /// <summary>Adds a <c>varuint</c> field mapped to <see cref="uint"/> (refuses values above 32 bits).</summary>
    public void Add(int number, Func<T, uint> get, Action<T, uint> set, uint defaultValue, FieldFlags flags = FieldFlags.None)
        => AddValue(number, get, set, defaultValue, flags, static (w, n, v, c) => w.WriteUInt(n, v, c), static f => f.ReadUInt32());

    /// <inheritdoc cref="Add(int, Func{T, uint}, Action{T, uint}, uint, FieldFlags)"/>
    public void Add(int number, Func<T, uint> get, Action<T, uint> set, FieldFlags flags = FieldFlags.None)
        => Add(number, get, set, 0U, flags);

    /// <summary>Adds a <c>varuint</c> field mapped to a non-negative <see cref="int"/>.</summary>
    public void Add(int number, Func<T, int> get, Action<T, int> set, int defaultValue, FieldFlags flags = FieldFlags.None)
        => AddValue(number, get, set, defaultValue, flags, static (w, n, v, c) => w.WriteUInt(n, checked((ulong)v), c), static f => f.ReadInt32());

    /// <inheritdoc cref="Add(int, Func{T, int}, Action{T, int}, int, FieldFlags)"/>
    public void Add(int number, Func<T, int> get, Action<T, int> set, FieldFlags flags = FieldFlags.None)
        => Add(number, get, set, 0, flags);

    /// <summary>Adds a <c>varuint</c> field mapped to a non-negative <see cref="long"/>.</summary>
    public void Add(int number, Func<T, long> get, Action<T, long> set, long defaultValue, FieldFlags flags = FieldFlags.None)
        => AddValue(number, get, set, defaultValue, flags, static (w, n, v, c) => w.WriteUInt(n, checked((ulong)v), c), static f => f.ReadNonNegativeInt64());

    /// <inheritdoc cref="Add(int, Func{T, long}, Action{T, long}, long, FieldFlags)"/>
    public void Add(int number, Func<T, long> get, Action<T, long> set, FieldFlags flags = FieldFlags.None)
        => Add(number, get, set, 0L, flags);

    /// <summary>Adds a <c>bool</c> field.</summary>
    public void Add(int number, Func<T, bool> get, Action<T, bool> set, bool defaultValue, FieldFlags flags = FieldFlags.None)
        => AddValue(number, get, set, defaultValue, flags, static (w, n, v, c) => w.WriteBool(n, v, c), static f => f.ReadBool());

    /// <inheritdoc cref="Add(int, Func{T, bool}, Action{T, bool}, bool, FieldFlags)"/>
    public void Add(int number, Func<T, bool> get, Action<T, bool> set, FieldFlags flags = FieldFlags.None)
        => Add(number, get, set, false, flags);

    /// <summary>Adds an <c>f64</c> field.</summary>
    public void Add(int number, Func<T, double> get, Action<T, double> set, double defaultValue, FieldFlags flags = FieldFlags.None)
        => AddValue(number, get, set, defaultValue, flags, static (w, n, v, c) => w.WriteF64(n, v, c), static f => f.ReadF64());

    /// <inheritdoc cref="Add(int, Func{T, double}, Action{T, double}, double, FieldFlags)"/>
    public void Add(int number, Func<T, double> get, Action<T, double> set, FieldFlags flags = FieldFlags.None)
        => Add(number, get, set, 0.0, flags);

    /// <summary>Adds a <c>guid</c> field (default <see cref="Guid.Empty"/> unless given).</summary>
    public void Add(int number, Func<T, Guid> get, Action<T, Guid> set, Guid defaultValue, FieldFlags flags = FieldFlags.None)
        => AddValue(number, get, set, defaultValue, flags, static (w, n, v, c) => w.WriteGuid(n, v, c), static f => f.ReadGuid());

    /// <inheritdoc cref="Add(int, Func{T, Guid}, Action{T, Guid}, Guid, FieldFlags)"/>
    public void Add(int number, Func<T, Guid> get, Action<T, Guid> set, FieldFlags flags = FieldFlags.None)
        => Add(number, get, set, Guid.Empty, flags);

    /// <summary>Adds a <c>timestamp</c> field (UTC; default <see cref="DateTime.MinValue"/> unless given).</summary>
    public void Add(int number, Func<T, DateTime> get, Action<T, DateTime> set, DateTime defaultValue, FieldFlags flags = FieldFlags.None)
        => AddValue(number, get, set, defaultValue, flags, static (w, n, v, c) => w.WriteTimestamp(n, v, c), static f => f.ReadTimestamp(),
            static (a, b) => a.ToUniversalTime().Ticks == b.ToUniversalTime().Ticks);

    /// <inheritdoc cref="Add(int, Func{T, DateTime}, Action{T, DateTime}, DateTime, FieldFlags)"/>
    public void Add(int number, Func<T, DateTime> get, Action<T, DateTime> set, FieldFlags flags = FieldFlags.None)
        => Add(number, get, set, DateTime.MinValue, flags);

    /// <summary>
    /// Adds a <c>string</c> field. A <see langword="null"/> value is never written (absent); optional fields are
    ///  also elided at <paramref name="defaultValue"/>. A required field writes null as the empty string.
    /// </summary>
    public void Add(int number, Func<T, string?> get, Action<T, string> set, string? defaultValue, FieldFlags flags = FieldFlags.None)
    {
        bool required = (flags & (FieldFlags.Required | FieldFlags.RequiredWhenV2)) != 0;
        Register(new StringField(number, flags, get, set, defaultValue, required, this));
    }

    /// <inheritdoc cref="Add(int, Func{T, string}, Action{T, string}, string, FieldFlags)"/>
    public void Add(int number, Func<T, string?> get, Action<T, string> set, FieldFlags flags = FieldFlags.None)
        => Add(number, get, set, "", flags);

    private sealed class StringField(int number, FieldFlags flags, Func<T, string?> get, Action<T, string> set,
        string? defaultValue, bool required, TapeSchema<T> owner) : FieldDef(number, flags, repeated: false)
    {
        public override void Write(TapeFieldWriter w, T obj)
        {
            string? value = get(obj);
            if (value == null)
            {
                if (required)
                    w.WriteString(Number, "", Critical);
                return;
            }
            if (!required && value == defaultValue)
                return;
            w.WriteString(Number, value, Critical);
        }

        public override void Read(TapeFieldReader f, T obj) => set(obj, f.ReadString());
    }

    /// <summary>Adds a <c>bytes</c> field; absent when null or empty (unless required).</summary>
    public void Add(int number, Func<T, byte[]?> get, Action<T, byte[]> set, FieldFlags flags = FieldFlags.None)
    {
        bool required = (flags & (FieldFlags.Required | FieldFlags.RequiredWhenV2)) != 0;
        Register(new CustomField(number, flags, repeated: false,
            (w, o) =>
            {
                byte[]? v = get(o);
                if (v != null && (v.Length > 0 || required))
                    w.WriteBytes(number, v, (flags & FieldFlags.Critical) != 0);
                else if (required)
                    w.WriteBytes(number, [], (flags & FieldFlags.Critical) != 0);
            },
            (f, o) => set(o, f.ReadBytes())));
    }

    /// <summary>
    /// Adds an enum field stored as <c>varuint</c>. An undefined value on read refuses the record (R3).
    /// </summary>
    public void Add<TEnum>(int number, Func<T, TEnum> get, Action<T, TEnum> set, TEnum defaultValue, FieldFlags flags = FieldFlags.None)
        where TEnum : unmanaged, Enum
        => AddValue(number, get, set, defaultValue, flags,
            static (w, n, v, c) => w.WriteUInt(n, EnumToUInt64(v), c),
            static f => EnumFromUInt64<TEnum>(f.ReadUInt64()));

    /// <summary>
    /// Adds a field that is not 1:1 with a property: a nested group, a repeated field, a coded blob.
    /// </summary>
    /// <param name="write">Writes zero or more fields numbered <paramref name="number"/>.</param>
    /// <param name="read">Called with the reader positioned on each occurrence.</param>
    /// <param name="repeated">Whether several occurrences are legal (otherwise a second one is refused).</param>
    public void AddCustom(int number, Action<TapeFieldWriter, T> write, Action<TapeFieldReader, T> read,
        FieldFlags flags = FieldFlags.None, bool repeated = false)
        => Register(new CustomField(number, flags, repeated, write, read));

    private static ulong EnumToUInt64<TEnum>(TEnum value) where TEnum : unmanaged, Enum
        => Unsafe.SizeOf<TEnum>() switch
        {
            1 => Unsafe.As<TEnum, byte>(ref value),
            2 => Unsafe.As<TEnum, ushort>(ref value),
            4 => Unsafe.As<TEnum, uint>(ref value),
            _ => Unsafe.As<TEnum, ulong>(ref value),
        };

    private static TEnum EnumFromUInt64<TEnum>(ulong raw) where TEnum : unmanaged, Enum
    {
        int size = Unsafe.SizeOf<TEnum>();
        if (size < sizeof(ulong) && raw >> (size * 8) != 0)
            throw TapeFormatException.Bad($"value {raw} is out of range for {typeof(TEnum).Name}");

        TEnum value = size switch
        {
            1 => Cast<TEnum, byte>((byte)raw),
            2 => Cast<TEnum, ushort>((ushort)raw),
            4 => Cast<TEnum, uint>((uint)raw),
            _ => Cast<TEnum, ulong>(raw),
        };

        if (!Enum.IsDefined(value))
            throw TapeFormatException.Bad($"value {raw} is not a defined {typeof(TEnum).Name}");
        return value;
    }

    private static TTo Cast<TTo, TFrom>(TFrom from) where TTo : unmanaged where TFrom : unmanaged
        => Unsafe.As<TFrom, TTo>(ref from);

    #endregion

    #region *** Write / Read ***

    /// <summary>Writes the fields of <paramref name="obj"/> (ascending numbers) - for groups or an open record.</summary>
    public void WriteFields(TapeFieldWriter w, T obj)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(obj);
        foreach (FieldDef def in Sorted())
            def.Write(w, obj);
    }

    /// <summary>Writes <paramref name="obj"/> as one complete record of <see cref="Kind"/>.</summary>
    public void Write(TapeRecordWriter writer, T obj)
    {
        ArgumentNullException.ThrowIfNull(writer);
        TapeFieldWriter w = writer.BeginRecord(Kind);
        try
        {
            WriteFields(w, obj);
        }
        catch
        {
            writer.AbandonRecord();
            throw;
        }
        writer.EndRecord();
    }

    /// <summary>
    /// Reads fields into <paramref name="target"/> until the body ends; skips unknown non-critical fields; refuses
    ///  unknown critical ones, duplicates and missing required fields.
    /// </summary>
    /// <returns><paramref name="target"/>, for chaining.</returns>
    public T Read(TapeFieldReader fields, T target)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(target);

        HashSet<int>? seen = null;

        while (fields.MoveNext())
        {
            if (!m_byNumber.TryGetValue(fields.Number, out FieldDef? def))
            {
                if (fields.IsCritical)
                    throw new TapeFormatException(FormatErrorKind.UnknownCritical,
                        $"record {Kind} uses feature {fields.Number} unknown to this build (format {TapeFormat.VersionText})");
                continue;   // unknown non-critical: skipped
            }

            // The critical bit belongs to the tag; known fields match by number and ignore it (R3).
            seen ??= [];
            if (!seen.Add(fields.Number) && !def.Repeated)
                throw new TapeFormatException(FormatErrorKind.Duplicate, $"field {fields.Number} of {Kind} occurs more than once");

            def.Read(fields, target);
        }

        foreach (FieldDef def in m_fields)
        {
            bool mustHave = (def.Flags & FieldFlags.Required) != 0
                || ((def.Flags & FieldFlags.RequiredWhenV2) != 0 && (IsV2?.Invoke(target) ?? true));
            if (mustHave && (seen == null || !seen.Contains(def.Number)))
                throw new TapeFormatException(FormatErrorKind.MissingRequired, $"record {Kind} lacks required field {def.Number}");
        }
        return target;
    }

    #endregion
}
