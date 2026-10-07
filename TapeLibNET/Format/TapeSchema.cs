using System.Collections;
using System.Runtime.CompilerServices;

namespace TapeLibNET.Format;

/// <summary>Per-field behaviour in a <see cref="TapeSchema{T}"/> (Design-Format-v2 §4.4, Appendix B §B.3.1).</summary>
[Flags]
public enum FieldFlags
{
    /// <summary>Optional, non-critical: elided on write when equal to the default.</summary>
    None = 0,

    /// <summary>Always written; absence on read refuses the record.</summary>
    Required = 1 << 0,

    /// <summary>The critical bit of the tag: a reader that does not know the field number must refuse the record.</summary>
    Critical = 1 << 1,

    /// <summary>Groups only: any number of occurrences.</summary>
    Repeated = 1 << 2,

    /// <summary><c>[Flags]</c> enums only: no defined-value check on read.</summary>
    Bitmask = 1 << 3,
}

/// <summary>Wire shape of a field; must match its number range (§4.4).</summary>
public enum FieldShape : byte
{
    /// <summary>Numbers 1–31.</summary>
    Scalar,

    /// <summary>Numbers 32–63 (strings and byte arrays).</summary>
    Blob,

    /// <summary>Numbers 48–63 (nested groups).</summary>
    Group,
}

/// <summary>
/// One field of a <see cref="TapeSchema{T}"/>. Contravariant: the fields of a base wire type serve every derived wire type.
/// </summary>
public interface ITapeField<in T>
{
    /// <summary>Field number, 1–63.</summary>
    int Number { get; }

    /// <summary>Field behaviour.</summary>
    FieldFlags Flags { get; }

    /// <summary>Wire shape.</summary>
    FieldShape Shape { get; }

    /// <summary>Writes the field; an optional field at its default writes nothing.</summary>
    void Write(TapeFieldWriter w, T source);

    /// <summary>Reads the field; the reader is positioned on it.</summary>
    void Read(TapeFieldReader r, T target);

    /// <summary>Applies the schema default: the field was absent and is optional.</summary>
    void ApplyDefault(T target);
}

/// <summary>
/// Declarative field table of a record or group: one line per field, both directions, no reflection
///  (Design-Format-v2 §5.7, Appendix B §B.3.3).
/// </summary>
/// <remarks>
/// <para>
/// Collection-initializer form: <c>new(kind) { { 2, s =&gt; s.X, (s, v) =&gt; s.X = v, FieldFlags.Required }, ... }</c>.
///  The property's CLR type picks the codec through overload resolution. Default comes BEFORE flags.
/// </para>
/// <para>
/// Writing emits ascending field numbers and elides OPTIONAL fields at their default. Reading accepts any order,
///  skips unknown non-critical fields, refuses unknown critical ones, duplicates of non-repeated fields and missing
///  required fields; an absent optional field receives the schema default. Per-field flag consistency is checked as
///  fields are added; numbering, duplicates and inheritance are validated when the schema freezes on first use.
/// </para>
/// </remarks>
/// <typeparam name="T">The (mutable, parameterless-constructible) wire type.</typeparam>
/// <param name="kind">Record kind; null for a group schema (nested only).</param>
/// <param name="inherits">Fields of a base wire type, e.g. the shared header tags 1–3.</param>
/// <param name="validate">Cross-field check; returns an error text or null. Runs on read AND write.</param>
public sealed class TapeSchema<T>(TapeRecordKind? kind = null,
    IEnumerable<ITapeField<T>>? inherits = null, Func<T, string?>? validate = null) : IEnumerable<ITapeField<T>> where T : class
{
    #region *** Field implementations ***

    private abstract class FieldBase(int number, FieldFlags flags, FieldShape shape) : ITapeField<T>
    {
        public int Number { get; } = number;
        public FieldFlags Flags { get; } = flags;
        public FieldShape Shape { get; } = shape;
        protected bool Critical => (Flags & FieldFlags.Critical) != 0;
        protected bool Required => (Flags & FieldFlags.Required) != 0;

        public virtual bool IsEnum => false;
        public virtual bool DefaultIsZero => true;

        public abstract void Write(TapeFieldWriter w, T source);
        public abstract void Read(TapeFieldReader r, T target);
        public virtual void ApplyDefault(T target) { }
    }

    private sealed class ValueField<V>(int number, FieldFlags flags, Func<T, V> get, Action<T, V> set,
        V defaultValue, bool defaultIsZero, bool isEnum, Action<TapeFieldWriter, int, V, bool> write,
        Func<TapeFieldReader, V> read, Func<V, V, bool> isDefault)
        : FieldBase(number, flags, FieldShape.Scalar)
    {
        public override bool IsEnum => isEnum;
        public override bool DefaultIsZero => defaultIsZero;

        public override void Write(TapeFieldWriter w, T source)
        {
            V value = get(source);
            if (!Required && isDefault(value, defaultValue))
                return;
            write(w, Number, value, Critical);
        }

        public override void Read(TapeFieldReader r, T target) => set(target, read(r));
        public override void ApplyDefault(T target) => set(target, defaultValue);
    }

    private sealed class StringField(int number, FieldFlags flags, Func<T, string?> get, Action<T, string> set, string? defaultValue)
        : FieldBase(number, flags, FieldShape.Blob)
    {
        private readonly string m_default = defaultValue ?? "";

        public override bool DefaultIsZero => m_default.Length == 0;

        public override void Write(TapeFieldWriter w, T source)
        {
            string value = get(source) ?? "";      // a required string writes null as ""
            if (!Required && value == m_default)
                return;
            w.WriteString(Number, value, Critical);
        }

        public override void Read(TapeFieldReader r, T target) => set(target, r.ReadString());
        public override void ApplyDefault(T target) => set(target, m_default);
    }

    private sealed class BytesField(int number, FieldFlags flags, Func<T, byte[]?> get, Action<T, byte[]> set)
        : FieldBase(number, flags, FieldShape.Blob)
    {
        public override void Write(TapeFieldWriter w, T source)
        {
            byte[]? value = get(source);
            if (value is { Length: > 0 } || Required)
                w.WriteBytes(Number, value ?? [], Critical);   // optional null and empty are both elided
        }

        public override void Read(TapeFieldReader r, T target) => set(target, r.ReadBytes());
        public override void ApplyDefault(T target) => set(target, []);     // absent: empty (a setter takes no null)
    }

    private sealed class GroupField<TChild>(int number, FieldFlags flags, Func<T, TChild?> get, Action<T, TChild?> set,
        TapeSchema<TChild> child) : FieldBase(number, flags, FieldShape.Group) where TChild : class, new()
    {
        public override void Write(TapeFieldWriter w, T source)
        {
            TChild? value = get(source);
            if (value is null && !Required)
                return;
            TapeFieldWriter cw = w.BeginGroup(Number, Critical);
            if (value is not null)
                child.Write(cw, value);
            w.EndGroup(cw);
        }

        public override void ApplyDefault(T target) => set(target, null);   // absent optional group: null, not the previous value

        public override void Read(TapeFieldReader r, T target) => set(target, child.Read(r.ReadGroup(), new TChild()));
    }

    private sealed class RepeatedGroupField<TChild>(int number, FieldFlags flags, Func<T, IEnumerable<TChild>> getAll,
        Action<T, TChild> add, TapeSchema<TChild> child) : FieldBase(number, flags | FieldFlags.Repeated, FieldShape.Group)
        where TChild : class, new()
    {
        public override void Write(TapeFieldWriter w, T source)
        {
            foreach (TChild item in getAll(source))
            {
                TapeFieldWriter cw = w.BeginGroup(Number, Critical);
                child.Write(cw, item);
                w.EndGroup(cw);
            }
        }

        public override void Read(TapeFieldReader r, T target) => add(target, child.Read(r.ReadGroup(), new TChild()));
    }

    private sealed class CustomField(int number, FieldShape shape, FieldFlags flags,
        Action<TapeFieldWriter, T> write, Action<TapeFieldReader, T> read, Action<T>? applyDefault)
        : FieldBase(number, flags, shape)
    {
        public override void Write(TapeFieldWriter w, T source) => write(w, source);
        public override void Read(TapeFieldReader r, T target) => read(r, target);
        public override void ApplyDefault(T target) => applyDefault?.Invoke(target);
    }

    #endregion

    private readonly TapeRecordKind? m_kind = kind;
    private readonly Func<T, string?>? m_validate = validate;
    private readonly List<ITapeField<T>> m_inherited = inherits is null ? [] : [.. inherits];
    private readonly List<ITapeField<T>> m_declared = [];
    private readonly object m_lock = new();
    private volatile bool m_frozen;
    private ITapeField<T>[] m_sorted = [];
    private ITapeField<T>?[] m_byNumber = [];

    /// <summary>Whether this is a record schema (has a kind); group schemas have none.</summary>
    public bool HasKind => m_kind.HasValue;

    /// <summary>The record kind.</summary>
    /// <exception cref="InvalidOperationException">For a group schema.</exception>
    public TapeRecordKind Kind => m_kind ?? throw new InvalidOperationException($"{Describe()} has no record kind");

    /// <summary>All fields, ascending by number; freezes the schema.</summary>
    public IReadOnlyList<ITapeField<T>> Fields
    {
        get
        {
            Freeze();
            return m_sorted;
        }
    }

    // Declared fields (own plus inherited, unsorted) - serves collection initializers and inheritance
    public IEnumerator<ITapeField<T>> GetEnumerator() => m_inherited.Concat(m_declared).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private string Describe() => m_kind is { } k ? $"schema {k}" : $"group schema of {typeof(T).Name}";

    #region *** Freeze / validation ***

    private void Freeze()
    {
        if (m_frozen)
            return;
        lock (m_lock)
        {
            if (m_frozen)
                return;

            List<ITapeField<T>> all = [.. m_inherited, .. m_declared];
            all.Sort(static (a, b) => a.Number.CompareTo(b.Number));

            var byNumber = new ITapeField<T>?[64];
            foreach (ITapeField<T> f in all)
            {
                CheckNumber(f.Number, f.Shape);
                if (byNumber[f.Number] is not null)
                    throw new InvalidOperationException($"{Describe()}: field number {f.Number} is declared twice");
                byNumber[f.Number] = f;
            }

            m_sorted = [.. all];
            m_byNumber = byNumber;
            m_frozen = true;
        }
    }

    private void CheckNumber(int number, FieldShape shape)
    {
        if (number is < 1 or > 63)
            throw new InvalidOperationException($"{Describe()}: field number {number} is outside 1-63");

        bool ok = shape switch
        {
            FieldShape.Scalar => number <= 31,
            FieldShape.Blob => number >= 32,
            _ => number >= 48,
        };
        if (!ok)
            throw new InvalidOperationException($"{Describe()}: field {number} of shape {shape} is outside its number range");
    }

    private void Register(FieldBase field)
    {
        if (m_frozen)
            throw new InvalidOperationException($"{Describe()} is frozen: fields cannot be added after first use");

        CheckNumber(field.Number, field.Shape);
        if ((field.Flags & FieldFlags.Repeated) != 0 && field.Shape != FieldShape.Group)
            throw new InvalidOperationException($"{Describe()}: field {field.Number} is Repeated but not a group");
        if ((field.Flags & FieldFlags.Required) != 0 && !field.DefaultIsZero)
            throw new InvalidOperationException($"{Describe()}: field {field.Number} is Required with a non-zero default");
        if ((field.Flags & FieldFlags.Bitmask) != 0 && !field.IsEnum)
            throw new InvalidOperationException($"{Describe()}: field {field.Number} is Bitmask but not an enum");

        lock (m_lock)
            m_declared.Add(field);
    }

    #endregion

    #region *** Typed Add overloads ***

    private void AddValue<V>(int number, Func<T, V> get, Action<T, V> set, V defaultValue, FieldFlags flags,
        Action<TapeFieldWriter, int, V, bool> write, Func<TapeFieldReader, V> read,
        Func<V, V, bool>? isDefault = null, bool isEnum = false)
        => Register(new ValueField<V>(number, flags, get, set, defaultValue,
            EqualityComparer<V>.Default.Equals(defaultValue, default!), isEnum, write, read,
            isDefault ?? EqualityComparer<V>.Default.Equals));

    private static ulong NonNegative(int n, long v)
        => v >= 0 ? (ulong)v : throw new ArgumentOutOfRangeException(nameof(v), v, $"field {n} must not be negative");

    private static byte ReadByte(TapeFieldReader r)
    {
        ulong v = r.ReadUInt();
        return v <= byte.MaxValue ? (byte)v : throw r.Error(FormatErrorKind.BadValue, $"value {v} exceeds 8 bits");
    }

    private static ushort ReadUShort(TapeFieldReader r)
    {
        ulong v = r.ReadUInt();
        return v <= ushort.MaxValue ? (ushort)v : throw r.Error(FormatErrorKind.BadValue, $"value {v} exceeds 16 bits");
    }

    /// <summary>Adds a <see langword="bool"/> field.</summary>
    public void Add(int n, Func<T, bool> get, Action<T, bool> set, bool @default, FieldFlags flags = FieldFlags.None)
        => AddValue(n, get, set, @default, flags, static (w, num, v, c) => w.WriteBool(num, v, c), static r => r.ReadBool());

    /// <inheritdoc cref="Add(int, Func{T, bool}, Action{T, bool}, bool, FieldFlags)"/>
    public void Add(int n, Func<T, bool> get, Action<T, bool> set, FieldFlags flags = FieldFlags.None)
        => Add(n, get, set, false, flags);

    /// <summary>Adds a <c>varuint</c> field mapped to <see langword="byte"/> (range-checked on read).</summary>
    public void Add(int n, Func<T, byte> get, Action<T, byte> set, byte @default, FieldFlags flags = FieldFlags.None)
        => AddValue(n, get, set, @default, flags, static (w, num, v, c) => w.WriteUInt(num, v, c), ReadByte);

    /// <inheritdoc cref="Add(int, Func{T, byte}, Action{T, byte}, byte, FieldFlags)"/>
    public void Add(int n, Func<T, byte> get, Action<T, byte> set, FieldFlags flags = FieldFlags.None)
        => Add(n, get, set, (byte)0, flags);

    /// <summary>Adds a <c>varuint</c> field mapped to <see langword="ushort"/> (range-checked on read).</summary>
    public void Add(int n, Func<T, ushort> get, Action<T, ushort> set, ushort @default, FieldFlags flags = FieldFlags.None)
        => AddValue(n, get, set, @default, flags, static (w, num, v, c) => w.WriteUInt(num, v, c), ReadUShort);

    /// <inheritdoc cref="Add(int, Func{T, ushort}, Action{T, ushort}, ushort, FieldFlags)"/>
    public void Add(int n, Func<T, ushort> get, Action<T, ushort> set, FieldFlags flags = FieldFlags.None)
        => Add(n, get, set, (ushort)0, flags);

    /// <summary>Adds a <c>varuint</c> field mapped to <see langword="uint"/> (range-checked on read).</summary>
    public void Add(int n, Func<T, uint> get, Action<T, uint> set, uint @default, FieldFlags flags = FieldFlags.None)
        => AddValue(n, get, set, @default, flags, static (w, num, v, c) => w.WriteUInt(num, v, c), static r => r.ReadUInt32());

    /// <inheritdoc cref="Add(int, Func{T, uint}, Action{T, uint}, uint, FieldFlags)"/>
    public void Add(int n, Func<T, uint> get, Action<T, uint> set, FieldFlags flags = FieldFlags.None)
        => Add(n, get, set, 0U, flags);

    /// <summary>Adds a <c>varuint</c> field mapped to <see langword="ulong"/>.</summary>
    public void Add(int n, Func<T, ulong> get, Action<T, ulong> set, ulong @default, FieldFlags flags = FieldFlags.None)
        => AddValue(n, get, set, @default, flags, static (w, num, v, c) => w.WriteUInt(num, v, c), static r => r.ReadUInt());

    /// <inheritdoc cref="Add(int, Func{T, ulong}, Action{T, ulong}, ulong, FieldFlags)"/>
    public void Add(int n, Func<T, ulong> get, Action<T, ulong> set, FieldFlags flags = FieldFlags.None)
        => Add(n, get, set, 0UL, flags);

    /// <summary>Adds a <c>varuint</c> field mapped to a non-negative <see langword="int"/> (negative on write throws).</summary>
    public void Add(int n, Func<T, int> get, Action<T, int> set, int @default, FieldFlags flags = FieldFlags.None)
        => AddValue(n, get, set, @default, flags, static (w, num, v, c) => w.WriteUInt(num, NonNegative(num, v), c), static r => r.ReadInt32());

    /// <inheritdoc cref="Add(int, Func{T, int}, Action{T, int}, int, FieldFlags)"/>
    public void Add(int n, Func<T, int> get, Action<T, int> set, FieldFlags flags = FieldFlags.None)
        => Add(n, get, set, 0, flags);

    /// <summary>Adds a ZigZag <c>varint</c> field mapped to <see langword="int"/> (negative values ok).</summary>
    /// <remarks>We use <c>checked((int)r.ReadInt())</c> rather than simply casting. A corrupt or future writer
    ///  could theoretically emit a ZigZag varint outside the <see cref="Int32"/> range. <see langword="checked"/>
    ///  would produce a clean overflow exception instead of silent truncation. The existing unsigned overloads
    ///  already perform explicit range checking, so this keeps the style consistent.</remarks>
    public void AddSigned(int n, Func<T, int> get, Action<T, int> set, int @default, FieldFlags flags = FieldFlags.None)
        => AddValue(n, get, set, @default, flags,
            static (w, num, v, c) => w.WriteInt(num, v, c),
            static r => checked((int)r.ReadInt()));

    /// <inheritdoc cref="AddSigned(int, Func{T, int}, Action{T, int}, int, FieldFlags)"/>
    public void AddSigned(int n, Func<T, int> get, Action<T, int> set, FieldFlags flags = FieldFlags.None)
        => AddSigned(n, get, set, 0, flags);

    /// <summary>Adds a <c>varuint</c> field mapped to a non-negative <see langword="long"/> (negative on write throws).</summary>
    public void Add(int n, Func<T, long> get, Action<T, long> set, long @default, FieldFlags flags = FieldFlags.None)
        => AddValue(n, get, set, @default, flags, static (w, num, v, c) => w.WriteUInt(num, NonNegative(num, v), c), static r => r.ReadNonNegativeInt64());

    /// <inheritdoc cref="Add(int, Func{T, long}, Action{T, long}, long, FieldFlags)"/>
    public void Add(int n, Func<T, long> get, Action<T, long> set, FieldFlags flags = FieldFlags.None)
        => Add(n, get, set, 0L, flags);

    /// <summary>Adds a ZigZag <c>varint</c> field mapped to <see langword="long"/> (negative values ok).</summary>
    public void AddSigned(int n, Func<T, long> get, Action<T, long> set, long @default, FieldFlags flags = FieldFlags.None)
        => AddValue(n, get, set, @default, flags,
            static (w, num, v, c) => w.WriteInt(num, v, c),
            static r => r.ReadInt());

    /// <inheritdoc cref="AddSigned(int, Func{T, long}, Action{T, long}, long, FieldFlags)"/>
    public void AddSigned(int n, Func<T, long> get, Action<T, long> set, FieldFlags flags = FieldFlags.None)
        => AddSigned(n, get, set, 0L, flags);

    /// <summary>Adds an <c>f64</c> field.</summary>
    public void Add(int n, Func<T, double> get, Action<T, double> set, double @default, FieldFlags flags = FieldFlags.None)
        => AddValue(n, get, set, @default, flags, static (w, num, v, c) => w.WriteF64(num, v, c), static r => r.ReadF64());

    /// <inheritdoc cref="Add(int, Func{T, double}, Action{T, double}, double, FieldFlags)"/>
    public void Add(int n, Func<T, double> get, Action<T, double> set, FieldFlags flags = FieldFlags.None)
        => Add(n, get, set, 0.0, flags);

    /// <summary>Adds a <see cref="Guid"/> field.</summary>
    public void Add(int n, Func<T, Guid> get, Action<T, Guid> set, Guid @default, FieldFlags flags = FieldFlags.None)
        => AddValue(n, get, set, @default, flags, static (w, num, v, c) => w.WriteGuid(num, v, c), static r => r.ReadGuid());

    /// <inheritdoc cref="Add(int, Func{T, Guid}, Action{T, Guid}, Guid, FieldFlags)"/>
    public void Add(int n, Func<T, Guid> get, Action<T, Guid> set, FieldFlags flags = FieldFlags.None)
        => Add(n, get, set, Guid.Empty, flags);

    /// <summary>Adds a <see cref="DateTime"/> field (UTC; see <see cref="TapePrimitives.ToUtcTicks"/>).</summary>
    public void Add(int n, Func<T, DateTime> get, Action<T, DateTime> set, DateTime @default, FieldFlags flags = FieldFlags.None)
        => AddValue(n, get, set, @default, flags, static (w, num, v, c) => w.WriteTimestamp(num, v, c), static r => r.ReadTimestamp(),
            static (a, b) => TapePrimitives.ToUtcTicks(a) == TapePrimitives.ToUtcTicks(b));

    /// <inheritdoc cref="Add(int, Func{T, DateTime}, Action{T, DateTime}, DateTime, FieldFlags)"/>
    public void Add(int n, Func<T, DateTime> get, Action<T, DateTime> set, FieldFlags flags = FieldFlags.None)
        => Add(n, get, set, TapePrimitives.MinUtc, flags);

    /// <summary>Adds a UTF-8 <see langword="string"/> field; optional <see langword="null"/> and empty are elided.
    ///  A required <see langword="null"/> writes <c>""</c>.</summary>
    public void Add(int n, Func<T, string?> get, Action<T, string> set, string? @default, FieldFlags flags = FieldFlags.None)
        => Register(new StringField(n, flags, get, set, @default));

    /// <inheritdoc cref="Add(int, Func{T, string}, Action{T, string}, string, FieldFlags)"/>
    public void Add(int n, Func<T, string?> get, Action<T, string> set, FieldFlags flags = FieldFlags.None)
        => Register(new StringField(n, flags, get, set, ""));

    /// <summary>Adds a <c><see langword="byte"/>[]</c> field; optional <see langword="null"/> and empty
    ///  are elided and read back as an empty array.</summary>
    public void Add(int n, Func<T, byte[]?> get, Action<T, byte[]> set, FieldFlags flags = FieldFlags.None)
        => Register(new BytesField(n, flags, get, set));

    /// <summary>Adds an enum field stored as <c>varuint</c>; an undefined value on read refuses the record unless <see cref="FieldFlags.Bitmask"/>.</summary>
    public void Add<TEnum>(int n, Func<T, TEnum> get, Action<T, TEnum> set, TEnum @default, FieldFlags flags = FieldFlags.None)
        where TEnum : unmanaged, Enum
    {
        bool bitmask = (flags & FieldFlags.Bitmask) != 0;
        AddValue(n, get, set, @default, flags,
            static (w, num, v, c) => w.WriteUInt(num, EnumToUInt64(v), c),
            r => EnumFromUInt64<TEnum>(r, bitmask), isEnum: true);
    }

    /// <inheritdoc cref="Add{TEnum}(int, Func{T, TEnum}, Action{T, TEnum}, TEnum, FieldFlags)"/>
    public void Add<TEnum>(int n, Func<T, TEnum> get, Action<T, TEnum> set, FieldFlags flags = FieldFlags.None)
        where TEnum : unmanaged, Enum
        => Add(n, get, set, default, flags);

    /// <summary>Adds a nested group field described by <paramref name="child"/>; optional <see langword="null"/> is elided.</summary>
    public void Add<TChild>(int n, Func<T, TChild?> get, Action<T, TChild?> set, TapeSchema<TChild> child, FieldFlags flags = FieldFlags.None)
        where TChild : class, new()
    {
        RequireGroupSchema(child);
        Register(new GroupField<TChild>(n, flags, get, set, child));
    }

    /// <summary>
    /// Adds a repeated nested group field: one occurrence per item. Read appends each occurrence;
    ///  read schemas with repeated groups into a fresh target.
    /// </summary>
    public void Add<TChild>(int n, Func<T, IEnumerable<TChild>> getAll, Action<T, TChild> add, TapeSchema<TChild> child,
        FieldFlags flags = FieldFlags.Repeated) where TChild : class, new()
    {
        RequireGroupSchema(child);
        Register(new RepeatedGroupField<TChild>(n, flags, getAll, add, child));
    }

    private void RequireGroupSchema<TChild>(TapeSchema<TChild> child) where TChild : class
    {
        ArgumentNullException.ThrowIfNull(child);
        if (child.HasKind)
            throw new InvalidOperationException($"{Describe()}: a group's child schema must not have a record kind");
    }

    /// <summary>
    /// Adds a field that is not 1:1 with a property (a coded blob, a bulk byte array). It declares its
    ///  <paramref name="shape"/> so validation covers it.
    /// </summary>
    /// <param name="write">Writes zero or more fields numbered <paramref name="n"/>.</param>
    /// <param name="read">Called with the reader positioned on each occurrence.</param>
    /// <param name="applyDefault">Applies the default when the optional field is absent; null = nothing.</param>
    internal void AddCustom(int n, FieldShape shape, Action<TapeFieldWriter, T> write, Action<TapeFieldReader, T> read,
        Action<T>? applyDefault = null, FieldFlags flags = FieldFlags.None)
        => Register(new CustomField(n, shape, flags, write, read, applyDefault));

    private static ulong EnumToUInt64<TEnum>(TEnum value) where TEnum : unmanaged, Enum
        => Unsafe.SizeOf<TEnum>() switch
        {
            1 => Unsafe.As<TEnum, byte>(ref value),
            2 => Unsafe.As<TEnum, ushort>(ref value),
            4 => Unsafe.As<TEnum, uint>(ref value),
            _ => Unsafe.As<TEnum, ulong>(ref value),
        };

    private static TEnum EnumFromUInt64<TEnum>(TapeFieldReader r, bool bitmask) where TEnum : unmanaged, Enum
    {
        ulong raw = r.ReadUInt();
        int size = Unsafe.SizeOf<TEnum>();
        if (size < sizeof(ulong) && raw >> (size * 8) != 0)
            throw r.Error(FormatErrorKind.BadValue, $"value {raw} is out of range for {typeof(TEnum).Name}");

        TEnum value = size switch
        {
            1 => Cast<TEnum, byte>((byte)raw),
            2 => Cast<TEnum, ushort>((ushort)raw),
            4 => Cast<TEnum, uint>((uint)raw),
            _ => Cast<TEnum, ulong>(raw),
        };

        if (!bitmask && !Enum.IsDefined(value))
            throw r.Error(FormatErrorKind.BadValue, $"value {raw} is not a defined {typeof(TEnum).Name}");
        return value;
    }

    private static TTo Cast<TTo, TFrom>(TFrom from) where TTo : unmanaged where TFrom : unmanaged
        => Unsafe.As<TFrom, TTo>(ref from);

    #endregion

    #region *** Write / Read ***

    /// <summary>Writes the fields of <paramref name="source"/> in ascending order; the validate hook runs first.</summary>
    /// <exception cref="InvalidOperationException">The validate hook rejects <paramref name="source"/> (a programming error).</exception>
    public void Write(TapeFieldWriter w, T source)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(source);
        Freeze();

        if (m_validate?.Invoke(source) is { } problem)
            throw new InvalidOperationException($"{Describe()}: {problem}");

        foreach (ITapeField<T> field in m_sorted)
            field.Write(w, source);
    }

    /// <summary>
    /// Reads fields into <paramref name="target"/> until the body ends: skips unknown non-critical fields, refuses unknown
    ///  critical ones, duplicates and missing required fields, applies defaults to absent optional ones, then validates.
    /// </summary>
    /// <returns><paramref name="target"/>, for chaining.</returns>
    public T Read(TapeFieldReader r, T target)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(target);
        Freeze();

        ulong seen = 0;     // field numbers are 1..63
        while (r.MoveNext())
        {
            int n = r.Number;
            ITapeField<T>? field = (uint)n < 64 ? m_byNumber[n] : null;
            if (field is null)
            {
                r.SkipUnknown();    // UnknownCritical when the tag says so
                continue;
            }

            // A known number: the critical bit is irrelevant (R3)
            ulong bit = 1UL << n;
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
                throw r.Error(FormatErrorKind.MissingRequired, $"required field {field.Number} is absent", field.Number);
            field.ApplyDefault(target);     // absent: the schema default, never the constructor's value
        }

        if (m_validate?.Invoke(target) is { } problem)
            throw r.Error(FormatErrorKind.CrossCheck, problem);
        return target;
    }

    #endregion
}
