namespace TapeLibNET.Format;

/// <summary>
/// A record as read from a stream or span: its prologue and the raw body (Design-Format-v2 §4.2).
/// The body is not interpreted until <see cref="Fields"/> / <see cref="Read{T}"/>.
/// </summary>
public sealed class TapeRecord(ushort rawKind, byte major, byte minor, ReadOnlyMemory<byte> body)
{
    /// <summary>The kind as written, including kinds this build does not know.</summary>
    public ushort RawKind => rawKind;

    /// <summary>The kind; may be a value outside the registry for a record from a newer build.</summary>
    public TapeRecordKind Kind => (TapeRecordKind)rawKind;

    /// <summary>Format major version of the writer.</summary>
    public byte Major => major;

    /// <summary>Format minor version of the writer (informational).</summary>
    public byte Minor => minor;

    /// <summary>The raw record body.</summary>
    public ReadOnlyMemory<byte> Body => body;

    /// <summary>A fresh field reader over the body.</summary>
    public TapeFieldReader Fields => new(body, new TapeRecordInfo(Kind, major, minor));

    /// <summary>
    /// Decides whether this build handles the record: <see langword="true"/> = deliver, <see langword="false"/> =
    ///  unknown but skippable. Throws for a newer major or an unknown non-skippable kind.
    /// </summary>
    internal bool Admit()
    {
        RequireSupportedMajor();

        if (TapeFormat.IsKnownKind(Kind))
            return true;
        if ((rawKind & TapeFormat.SkippableKindBit) != 0)
            return false;

        throw new TapeFormatException(FormatErrorKind.UnknownKind,
            $"record kind 0x{rawKind:X4} is unknown to this build (format {major}.{minor})");
    }

    internal void RequireSupportedMajor()
    {
        if (major > TapeFormat.Major)
            throw new TapeFormatException(FormatErrorKind.NewerMajor,
                $"written by a newer TapeNET (format {major}.{minor}); this build reads format {TapeFormat.VersionText}");
        if (major < TapeFormat.Major)
            throw TapeFormatException.Bad($"unsupported format major {major}");
    }

    /// <summary>Interprets the record as <typeparamref name="T"/>; refuses a record of another kind.</summary>
    public T Read<T>() where T : ITapeRecord<T>
    {
        RequireSupportedMajor();
        if (!T.Accepts(Kind))
            throw new TapeFormatException(FormatErrorKind.UnexpectedKind,
                $"record kind 0x{rawKind:X4} is not accepted by {typeof(T).Name}");
        try
        {
            return T.ReadBody(Fields);
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            // Domain conversion refused the data (ToRecord): report it as a format error (G8).
            throw new TapeFormatException(FormatErrorKind.BadValue, $"{typeof(T).Name}: {ex.Message}", ex) { Record = Kind };
        }
    }
}
