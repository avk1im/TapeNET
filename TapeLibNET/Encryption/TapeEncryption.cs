using System.Diagnostics.CodeAnalysis;

namespace TapeLibNET.Encryption;


// ── Encryption scheme ────────────────────────────────────────────────────────

/// <summary>
/// Per-set encryption scheme, mirroring <see cref="Compression.TapeCompression"/> (Design-Encryption §1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Stored by value, never enum-validated on read (E15).</b> A set carrying a value this build does not implement —
///  <see cref="Hardware"/> in a v1 build, or any future scheme — loads as a permanently locked set: carried, navigated,
///  listed and deletable, but never opened. Code must therefore never assume the value is one of the named members.
/// </para>
/// </remarks>
public enum TapeEncryption
{
    /// <summary>Not encrypted.</summary>
    None = 0,

    /// <summary>Encrypted by the host: chunked AES-256-GCM per file record (v1).</summary>
    Software = 1,

    /// <summary>Encrypted by the drive: LTO-4+ AES-256-GCM per block, key loaded by SECURITY PROTOCOL OUT (v2, reserved).</summary>
    Hardware = 2,
}

/// <summary>Password-based key derivation function recorded in a <see cref="TapeKeyEnvelope"/> (Design-Encryption §5.5).</summary>
public enum TapeKdf
{
    /// <summary>PBKDF2-HMAC-SHA256 — the only KDF this build implements.</summary>
    Pbkdf2Sha256 = 1,

    /// <summary>Reserved: needs a vetted implementation outside the BCL.</summary>
    Argon2id = 2,
}

// ── Locked-set access ────────────────────────────────────────────────────────

/// <summary>
/// Thrown when a member hidden by encryption is read on a LOCKED set (Design-Encryption §6.1, E6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Fail fast, not 0.</b> A locked set answering <c>Count == 0</c> would silently bring back the "locked = empty" trap;
///  throwing makes every forgotten <c>IsLocked</c> check visible in the test suite.
/// </para>
/// <para>
/// An <see cref="InvalidOperationException"/>: asking a locked set for its files is a programming error of the caller,
///  never a property of the medium.
/// </para>
/// </remarks>
/// <param name="setId">The locked set's identity — the one thing about it that is always known.</param>
/// <param name="member">The member that was read.</param>
public sealed class TapeSetLockedException(Guid setId, string member)
    : InvalidOperationException($"Backup set {setId:N} is locked (encrypted, no key applied) — '{member}' is not available")
{
    /// <summary>The locked set's identity.</summary>
    public Guid SetId { get; } = setId;

    /// <summary>The member that was read.</summary>
    public string Member { get; } = member;
}

// ── EncryptionPreset — shared CLI/WPF parse helper ───────────────────────────

/// <summary>
/// Translates between human-readable encryption specifiers used by the CLI and WPF and <see cref="TapeEncryption"/>.
///  The sibling of <see cref="Compression.CompressionPreset"/>.
/// </summary>
/// <remarks>
/// Parsing <c>drive</c> succeeds in every build: whether THIS build and THIS drive can use it is the service's decision
///  (Design-Encryption §10.2), reported with a precise reason rather than as a usage error.
/// </remarks>
public static class EncryptionPreset
{
    // ── Well-known keyword strings ────────────────────────────────────────────

    public const string KeyEmpty = "";
    public const string KeyOff = "off";
    public const string KeyNone = "none";
    public const string KeySoftware = "software";
    public const string KeySw = "sw";        // alias → Software
    public const string KeyDrive = "drive";
    public const string KeyHardware = "hardware";  // alias → Hardware
    public const string KeyHw = "hw";        // alias → Hardware

    // ── Parse ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a CLI/config specifier such as <c>off | none | software | sw | drive | hardware | hw</c>.
    ///  Returns <see langword="false"/> and an error message when the input is not recognised.
    /// </summary>
    public static bool TryParse(
        string? specifier,
        out TapeEncryption encryption,
        [NotNullWhen(false)] out string? errorMessage)
    {
        errorMessage = null;
        switch (specifier?.Trim().ToLowerInvariant())
        {
            case KeyOff:
            case KeyNone:
            case KeyEmpty:
            case null:
                encryption = TapeEncryption.None;
                return true;

            case KeySoftware:
            case KeySw:
                encryption = TapeEncryption.Software;
                return true;

            case KeyDrive:
            case KeyHardware:
            case KeyHw:
                encryption = TapeEncryption.Hardware;
                return true;

            default:
                encryption = TapeEncryption.None;
                errorMessage = $"Unrecognised encryption specifier '{specifier}'. " +
                               $"Valid values: {KeyOff}, {KeyNone}, {KeySoftware}, {KeyDrive}.";
                return false;
        }
    }

    /// <summary>
    /// Display name, e.g. "Software (AES-256-GCM)". Never throws: a value this build does not know (E15) is named as
    ///  unsupported, with its number.
    /// </summary>
    public static string DisplayName(TapeEncryption encryption) =>
        encryption switch
        {
            TapeEncryption.None => "None",
            TapeEncryption.Software => "Software (AES-256-GCM)",
            TapeEncryption.Hardware => "Drive (LTO AES-256-GCM)",
            _ => $"Unsupported method ({(int)encryption})",
        };

    /// <summary>The canonical CLI/config string — the inverse of <see cref="TryParse"/>.</summary>
    public static string ToSpecifier(TapeEncryption encryption) =>
        encryption switch
        {
            TapeEncryption.Software => KeySoftware,
            TapeEncryption.Hardware => KeyDrive,
            _ => KeyNone,
        };
}