using Windows.Win32.Foundation;

namespace TapeLibNET.Format;


/// <summary>What a field reader knows about its record - for error context and polymorphic dispatch.</summary>
public readonly record struct TapeRecordInfo(TapeRecordKind Kind, byte Major, byte Minor);

/// <summary>Why a record, frame or stream was refused (Design-Format-v2 §8.1).</summary>
public enum FormatErrorKind
{
    /// <summary>Written by a newer TapeNET: major version above <see cref="TapeFormat.Major"/>.</summary>
    NewerMajor,

    /// <summary>The input does not start with the 2.1 record magic.</summary>
    BadMagic,

    /// <summary>Record kind not in the registry and not marked skippable.</summary>
    UnknownKind,

    /// <summary>A field carrying the critical bit is unknown to this build.</summary>
    UnknownCritical,

    /// <summary>A required field is absent.</summary>
    MissingRequired,

    /// <summary>A non-repeated field occurs more than once.</summary>
    Duplicate,

    /// <summary>A length runs past the end of its enclosing body.</summary>
    Overrun,

    /// <summary>A value does not consume exactly the bytes it declares.</summary>
    Underrun,

    /// <summary>A value is malformed or out of range (overlong varint, bad bool, bad UTF-8, bad enum, limits ...).</summary>
    BadValue,

    /// <summary>A CRC-64 trailer disagrees with the bytes it guards.</summary>
    CrcMismatch,

    /// <summary>The input ended inside a record or before a required trailer.</summary>
    Truncated,

    /// <summary>The record kind is known but not the one the caller asked for.</summary>
    UnexpectedKind,

    /// <summary>A size or depth limit was exceeded (string / bytes / record body / group depth).</summary>
    LimitExceeded,

    /// <summary>A cross-field consistency check (schema validate hook) failed.</summary>
    CrossCheck,
}

/// <summary>
/// Thrown by the format core for any malformed, unsupported or damaged input. User-input problems surface
///  as this exception from the low-level readers; callers map it to a diagnostic or a frame status.
/// </summary>
public sealed class TapeFormatException : FormatException
{
    /// <summary>The reason the input was refused.</summary>
    public FormatErrorKind Kind { get; }

    /// <summary>Record the error arose in, if known.</summary>
    public TapeRecordKind? Record { get; init; }

    /// <summary>Field number the error arose in, if known.</summary>
    public int? Field { get; init; }

    /// <summary>Byte offset within the record body, if known.</summary>
    public int? Offset { get; init; }

    public TapeFormatException(FormatErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        HResult = (int)ToErrorWin32(kind);
    }

    /// <summary>The message, with record / field / offset context appended when set.</summary>
    public override string Message
    {
        get
        {
            List<string> ctx = [];
            if (Record is { } r) ctx.Add($"record {r}");
            if (Field is { } f) ctx.Add($"field {f}");
            if (Offset is { } o) ctx.Add($"body offset {o}");
            return ctx.Count == 0 ? base.Message : $"{base.Message} [{string.Join(", ", ctx)}]";
        }
    }

    /// <summary>Shorthand for a <see cref="FormatErrorKind.BadValue"/> refusal.</summary>
    internal static TapeFormatException Bad(string message) => new(FormatErrorKind.BadValue, message);

    internal static WIN32_ERROR ToErrorWin32(FormatErrorKind kind) =>
    kind switch
    {
        FormatErrorKind.NewerMajor => WIN32_ERROR.ERROR_REVISION_MISMATCH,

        FormatErrorKind.BadMagic => WIN32_ERROR.ERROR_BAD_FORMAT,
        FormatErrorKind.UnknownKind => WIN32_ERROR.ERROR_BAD_FORMAT,
        FormatErrorKind.UnexpectedKind => WIN32_ERROR.ERROR_BAD_FORMAT,

        FormatErrorKind.UnknownCritical => WIN32_ERROR.ERROR_NOT_SUPPORTED,

        FormatErrorKind.MissingRequired => WIN32_ERROR.ERROR_INVALID_DATA,
        FormatErrorKind.Overrun => WIN32_ERROR.ERROR_INVALID_DATA,
        FormatErrorKind.Underrun => WIN32_ERROR.ERROR_INVALID_DATA,
        FormatErrorKind.BadValue => WIN32_ERROR.ERROR_INVALID_DATA,
        FormatErrorKind.CrossCheck => WIN32_ERROR.ERROR_INVALID_DATA,

        FormatErrorKind.Duplicate => WIN32_ERROR.ERROR_ALREADY_EXISTS,

        FormatErrorKind.CrcMismatch => WIN32_ERROR.ERROR_CRC,

        FormatErrorKind.Truncated => WIN32_ERROR.ERROR_HANDLE_EOF,

        FormatErrorKind.LimitExceeded => WIN32_ERROR.ERROR_BUFFER_OVERFLOW,

        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

}
