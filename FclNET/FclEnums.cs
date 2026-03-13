namespace FclNET;

/// <summary>
/// FCL field identifiers — the left-hand side of a condition.
/// </summary>
public enum FclField
{
    /// <summary>Full path including file name.</summary>
    FullName,
    /// <summary>File name only (with extension).</summary>
    Name,
    /// <summary>File extension (e.g. ".doc").</summary>
    Extension,
    /// <summary>Directory path only.</summary>
    Path,
    /// <summary>File size in bytes.</summary>
    Size,
    /// <summary>File creation date/time.</summary>
    Created,
    /// <summary>Last modification date/time.</summary>
    Modified,
    /// <summary>File attribute flags.</summary>
    Attributes
}

/// <summary>
/// FCL comparison operators — the middle part of a condition.
/// These are the canonical (normalized) operators. Aliases (e.g. <c>==</c>)
/// are resolved by the lexer to these values.
/// </summary>
public enum FclOperator
{
    // --- String operators ---

    /// <summary>Exact match (case-insensitive for strings, exact for sizes).</summary>
    Equals,
    /// <summary>Not an exact match.</summary>
    NotEquals,
    /// <summary>Substring match (case-insensitive).</summary>
    Contains,
    /// <summary>Does not contain substring.</summary>
    NotContains,
    /// <summary>DOS-style wildcard match (<c>*</c>, <c>?</c>).</summary>
    Matches,
    /// <summary>Full .NET regular expression match.</summary>
    Regex,

    // --- Date/time operators ---

    /// <summary>Strictly before (or less than for sizes).</summary>
    Before,
    /// <summary>Before or on the same date (or less than or equal for sizes).</summary>
    BeforeOrOn,
    /// <summary>Strictly after (or greater than for sizes).</summary>
    After,
    /// <summary>After or on the same date (or greater than or equal for sizes).</summary>
    AfterOrOn,

    // --- Size operators (aliases for date operators where semantics differ) ---

    /// <summary>Strictly greater than (size).</summary>
    GreaterThan,
    /// <summary>Greater than or equal (size).</summary>
    GreaterOrEqual,
    /// <summary>Strictly less than (size).</summary>
    LessThan,
    /// <summary>Less than or equal (size).</summary>
    LessOrEqual,

    // --- Attribute operators ---

    /// <summary>File has the specified attribute flag.</summary>
    Has,
    /// <summary>File does not have the specified attribute flag.</summary>
    NotHas
}

/// <summary>
/// Recognized file attribute values in FCL conditions.
/// </summary>
public enum FclAttribute
{
    Hidden,
    ReadOnly,
    System,
    Archive,
    Temporary
}

/// <summary>
/// Anchor for relative date expressions (resolved at evaluation time).
/// </summary>
public enum FclDateAnchor
{
    /// <summary>Start of the current day (00:00).</summary>
    Today,
    /// <summary>Start of the previous day.</summary>
    Yesterday,
    /// <summary>Current date and time.</summary>
    Now
}

/// <summary>
/// Time unit for relative date offsets.
/// </summary>
public enum FclDateUnit
{
    Minutes,
    Hours,
    Days,
    Weeks,
    Months,
    Years
}

/// <summary>
/// Size unit suffixes recognized in FCL size literals.
/// </summary>
public enum FclSizeUnit
{
    /// <summary>Bytes (no suffix or "B").</summary>
    Bytes,
    /// <summary>Kilobytes (1,024 bytes).</summary>
    KB,
    /// <summary>Megabytes (1,048,576 bytes).</summary>
    MB,
    /// <summary>Gigabytes (1,073,741,824 bytes).</summary>
    GB,
    /// <summary>Terabytes (1,099,511,627,776 bytes).</summary>
    TB
}
