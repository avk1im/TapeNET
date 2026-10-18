using System.Security.Cryptography;
using TapeLibNET.Drive;
using TapeLibNET.Toc;
using Windows.Win32.Foundation;

namespace TapeLibNET.Encryption;

// ── The seam ─────────────────────────────────────────────────────────────────

/// <summary>
/// The one place where an encryption scheme meets the agents (Design-Encryption §9.1). Every agent call site that
///  touches encryption goes through the cipher chosen for the current set — plain, software, or (v2) drive.
/// </summary>
/// <remarks>
/// <para>
/// <b>Plain sets go through it too</b>, via the identity <see cref="PlainSetCipher"/>: the agent code stays
///  scheme-free, and adding a scheme is a new class — never an agent edit.
/// </para>
/// <para>
/// <b>Lifecycle per set:</b> <see cref="Validate"/> before any tape movement (E9) → <see cref="BeginSet"/> with the head
///  at the set's first CONTENT block → per file <see cref="OpenWrite"/> … <see cref="CompleteWrite"/> /
///  <see cref="AbandonWrite"/>, or <see cref="OpenRead"/> … <see cref="CompleteRead"/> → <see cref="EndSet"/> on every
///  path (E16).
/// </para>
/// <para>
/// <b>Never prompts, never sees a password</b> (E10): the key comes from the unlocked <see cref="TapeSetTOC"/>.
/// </para>
/// </remarks>
internal interface ITapeSetCipher
{
    /// <summary>The scheme this cipher implements.</summary>
    TapeEncryption Scheme { get; }

    /// <summary>
    /// Whether <see cref="EndSet"/> needs the drive quiescent — no read in flight (v2 drive keys, §19.4). The agent
    ///  then ends the read session first.
    /// </summary>
    bool BindsDrive { get; }

    /// <summary>Whether the set may use drive (hardware) compression.</summary>
    bool AllowsHardwareCompression { get; }

    /// <summary>Whether restore may fall back to an ESTIMATED record size when the TOC lacks <c>SizeOnTape</c>.</summary>
    bool AllowsEstimatedSize { get; }

    /// <summary>
    /// Everything that must hold before the tape moves (E9): a key, a drive able to use it, an emitter able to write the
    ///  scheme, compatible compression, and — for writing — a set that still accepts files (E14).
    /// </summary>
    /// <param name="set">The set about to be written or read; UNLOCKED or not — the cipher checks.</param>
    /// <param name="drive">The drive — for capabilities (v2).</param>
    /// <param name="writesCodecPrefix">Whether the agent's emitter writes format 2.1 (codec prefix).</param>
    /// <param name="forWrite">Backup (<see langword="true"/>) or restore / validate / verify.</param>
    TapeResult Validate(TapeSetTOC set, TapeDrive drive, bool writesCodecPrefix, bool forWrite);

    /// <summary>
    /// Called with the head at the set's first CONTENT block — past the set header, before the first file byte.
    /// </summary>
    TapeResult BeginSet(TapeDrive drive, TapeSetTOC set, bool forWrite);

    /// <summary>Called when the set's content session ends — success, failure, EOM or abort alike. Never throws.</summary>
    void EndSet(TapeDrive drive);

    /// <summary>Wraps the packer's per-file write façade. The returned stream receives header frame, codec and body.</summary>
    Stream OpenWrite(Stream inner, TapeSetTOC set, TapeFileInfo file);

    /// <summary>Seals a file written through <paramref name="opened"/>, after the codec stream is disposed.</summary>
    void CompleteWrite(Stream opened);

    /// <summary>Abandons a failed file. Idempotent; never throws.</summary>
    void AbandonWrite(Stream opened);

    /// <summary>Wraps the packer's per-file read façade (bounded to the record).</summary>
    Stream OpenRead(Stream inner, TapeSetTOC set, TapeFileInfo file);

    /// <summary>After the consumer is done with a file — authenticates whatever it left unread (E5).</summary>
    void CompleteRead(Stream opened);

    /// <summary>Host-side bytes the scheme adds to a record of <paramref name="plainRecordLength"/> bytes.</summary>
    long RecordOverhead(long plainRecordLength);
}

// ── None ─────────────────────────────────────────────────────────────────────

/// <summary>The identity cipher of plain sets: one shared, stateless instance.</summary>
internal sealed class PlainSetCipher : ITapeSetCipher
{
    public static readonly PlainSetCipher Instance = new();

    private PlainSetCipher() { }

    public TapeEncryption Scheme => TapeEncryption.None;
    public bool BindsDrive => false;
    public bool AllowsHardwareCompression => true;
    public bool AllowsEstimatedSize => true;

    public TapeResult Validate(TapeSetTOC set, TapeDrive drive, bool writesCodecPrefix, bool forWrite) => TapeResult.OK;
    public TapeResult BeginSet(TapeDrive drive, TapeSetTOC set, bool forWrite) => TapeResult.OK;
    public void EndSet(TapeDrive drive) { }
    public Stream OpenWrite(Stream inner, TapeSetTOC set, TapeFileInfo file) => inner;
    public void CompleteWrite(Stream opened) { }
    public void AbandonWrite(Stream opened) { }
    public Stream OpenRead(Stream inner, TapeSetTOC set, TapeFileInfo file) => inner;
    public void CompleteRead(Stream opened) { }
    public long RecordOverhead(long plainRecordLength) => 0;
}

// ── Software ─────────────────────────────────────────────────────────────────

/// <summary>
/// Host-side AES-256-GCM: every file record is one chunked AEAD stream under its own key
///  <c>K_file = HKDF(DEK, SetId, FileId)</c> (Design-Encryption §4.2, §5.7).
/// </summary>
/// <remarks>
/// One per agent: owns the chunk-buffer session, reused for every file (one live stream at a time).
/// </remarks>
internal sealed class SoftwareSetCipher : ITapeSetCipher, IDisposable
{
    private TapeAeadSession? m_session;

    public TapeEncryption Scheme => TapeEncryption.Software;
    public bool BindsDrive => false;
    public bool AllowsHardwareCompression => false;     // ciphertext does not compress
    public bool AllowsEstimatedSize => false;           // the decryptor needs the exact end of the final chunk

    public TapeResult Validate(TapeSetTOC set, TapeDrive drive, bool writesCodecPrefix, bool forWrite)
    {
        if (set.IsLocked || set.Key is null || set.Key.IsDisposed)
            return TapeResult.Fail((uint)WIN32_ERROR.ERROR_INVALID_PASSWORD,
                "the set is encrypted and locked — its password has not been supplied");
        if (!forWrite)
            return TapeResult.OK;
        if (!writesCodecPrefix)
            return TapeResult.Fail((uint)WIN32_ERROR.ERROR_NOT_SUPPORTED,
                "encryption needs format 2.1; this set is written in the legacy format");
        if (set.Compression == Compression.TapeCompression.Hardware)
            return TapeResult.Fail((uint)WIN32_ERROR.ERROR_INVALID_PARAMETER,
                "software encryption rules out drive compression — encrypted data does not compress");
        if (!set.AcceptsFiles)
            return TapeResult.Fail((uint)WIN32_ERROR.ERROR_INVALID_STATE,
                "an encrypted set loaded from tape accepts no further files (E14)");
        return TapeResult.OK;
    }

    public TapeResult BeginSet(TapeDrive drive, TapeSetTOC set, bool forWrite) => TapeResult.OK;

    public void EndSet(TapeDrive drive) { }

    public Stream OpenWrite(Stream inner, TapeSetTOC set, TapeFileInfo file)
    {
        Span<byte> key = stackalloc byte[TapeSetKey.Size];
        try
        {
            KeyOf(set).DeriveFileKey(set.SetId, file.FileId, key);
            return Session.BeginEncrypt(inner, key, TapeAead.ZeroPrefix);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public void CompleteWrite(Stream opened) => ((EncryptingStream)opened).Complete();

    public void AbandonWrite(Stream opened)
    {
        if (opened is EncryptingStream encrypting)
            encrypting.Abandon();
    }

    public Stream OpenRead(Stream inner, TapeSetTOC set, TapeFileInfo file)
    {
        Span<byte> key = stackalloc byte[TapeSetKey.Size];
        try
        {
            KeyOf(set).DeriveFileKey(set.SetId, file.FileId, key);
            return Session.BeginDecrypt(inner, key, TapeAead.ZeroPrefix);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public void CompleteRead(Stream opened) => ((DecryptingStream)opened).VerifyToEnd();

    public long RecordOverhead(long plainRecordLength) => Overhead(plainRecordLength);

    /// <summary>The scheme's per-record overhead — static, for the TOC's size estimates.</summary>
    internal static long Overhead(long plainRecordLength) => TapeAead.Overhead(plainRecordLength, TapeAead.FileChunkSize);

    public void Dispose()
    {
        m_session?.Dispose();
        m_session = null;
    }

    private TapeAeadSession Session => m_session ??= new TapeAeadSession(TapeAead.FileChunkSize);

    private static TapeSetKey KeyOf(TapeSetTOC set)
        => set.Key ?? throw new TapeSetLockedException(set.SetId, nameof(TapeSetTOC.Key));
}

// ── Not implemented by this build ────────────────────────────────────────────

/// <summary>
/// The cipher of a scheme this build cannot use — <see cref="TapeEncryption.Hardware"/> in v1, or any future value
///  (E15). Fails <see cref="Validate"/>, so the agent refuses before moving the tape; every other member is unreachable.
/// </summary>
internal sealed class UnsupportedSetCipher(TapeEncryption scheme) : ITapeSetCipher
{
    public TapeEncryption Scheme { get; } = scheme;
    public bool BindsDrive => false;
    public bool AllowsHardwareCompression => false;
    public bool AllowsEstimatedSize => false;

    public TapeResult Validate(TapeSetTOC set, TapeDrive drive, bool writesCodecPrefix, bool forWrite)
        => TapeResult.Fail((uint)WIN32_ERROR.ERROR_NOT_SUPPORTED,
            $"the set is encrypted with a method this build does not support: {EncryptionPreset.DisplayName(Scheme)}");

    public TapeResult BeginSet(TapeDrive drive, TapeSetTOC set, bool forWrite) => throw Unreachable();
    public void EndSet(TapeDrive drive) { }
    public Stream OpenWrite(Stream inner, TapeSetTOC set, TapeFileInfo file) => throw Unreachable();
    public void CompleteWrite(Stream opened) => throw Unreachable();
    public void AbandonWrite(Stream opened) { }
    public Stream OpenRead(Stream inner, TapeSetTOC set, TapeFileInfo file) => throw Unreachable();
    public void CompleteRead(Stream opened) => throw Unreachable();
    public long RecordOverhead(long plainRecordLength) => 0;

    private InvalidOperationException Unreachable()
        => new($"{EncryptionPreset.DisplayName(Scheme)} is not supported by this build — Validate refuses it");
}

// ── Selection ────────────────────────────────────────────────────────────────

/// <summary>
/// Chooses the cipher for a set. One per agent: it owns the agent's <see cref="SoftwareSetCipher"/> (and, in v2, its
///  drive cipher).
/// </summary>
internal sealed class TapeSetCipherProvider : IDisposable
{
    private SoftwareSetCipher? m_software;

    /// <summary>The cipher for <paramref name="set"/>'s scheme.</summary>
    public ITapeSetCipher For(TapeSetTOC set)
    {
#if TEST_HOOKS
        if (TapeSetCiphers.Override?.Invoke(set) is { } injected)
            return injected;
#endif
        return set.Encryption switch
        {
            TapeEncryption.None => PlainSetCipher.Instance,
            TapeEncryption.Software => m_software ??= new SoftwareSetCipher(),
            // v2: TapeEncryption.Hardware => m_hardware ??= new HardwareSetCipher(),
            var other => new UnsupportedSetCipher(other),
        };
    }

    public void Dispose()
    {
        m_software?.Dispose();
        m_software = null;
    }
}

/// <summary>Scheme facts the TOC needs without an agent, and the test seam.</summary>
internal static class TapeSetCiphers
{
    /// <summary>Host-side bytes a scheme adds to a record — for <c>TapeSetTOC.EstimateFileOverhead</c>.</summary>
    public static long RecordOverhead(TapeEncryption scheme, long plainRecordLength) => scheme switch
    {
        TapeEncryption.Software => SoftwareSetCipher.Overhead(plainRecordLength),
        _ => 0,
    };

#if TEST_HOOKS
    private static readonly AsyncLocal<Func<TapeSetTOC, ITapeSetCipher?>?> s_override = new();

    /// <summary>The injected factory of the current logical flow, if any.</summary>
    internal static Func<TapeSetTOC, ITapeSetCipher?>? Override => s_override.Value;

    /// <summary>
    /// Injects <paramref name="factory"/> for the current logical flow until the scope is disposed: a non-null answer
    ///  replaces the normal choice. For recording fakes (Design-Encryption §15.4).
    /// </summary>
    public static IDisposable UseCipher(Func<TapeSetTOC, ITapeSetCipher?> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var previous = s_override.Value;
        s_override.Value = factory;
        return new Scope(() => s_override.Value = previous);
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        private Action? m_restore = restore;

        public void Dispose()
        {
            m_restore?.Invoke();
            m_restore = null;
        }
    }
#endif
}
