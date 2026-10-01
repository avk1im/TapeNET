namespace TapeLibNET.Format;

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
}

/// <summary>
/// Thrown by the format core for any malformed, unsupported or damaged input. User-input problems surface
///  as this exception from the low-level readers; callers map it to a diagnostic or a frame status.
/// </summary>
public sealed class TapeFormatException : Exception
{
    /// <summary>The reason the input was refused.</summary>
    public FormatErrorKind Kind { get; }

    public TapeFormatException(FormatErrorKind kind, string message) : base(message)
    {
        Kind = kind;
    }

    public TapeFormatException(FormatErrorKind kind, string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>Shorthand for a <see cref="FormatErrorKind.BadValue"/> refusal.</summary>
    internal static TapeFormatException Bad(string message) => new(FormatErrorKind.BadValue, message);
}
