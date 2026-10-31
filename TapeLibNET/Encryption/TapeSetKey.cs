using System.Buffers.Binary;
using System.Security.Cryptography;

namespace TapeLibNET.Encryption;

/// <summary>
/// The data key (DEK) of one encrypted chain, unlocked — and the only place the three working keys are derived from it
///  (Design-Encryption §4.2, §7.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Agents and ciphers never touch the raw DEK.</b> They ask for the key they need — per file, per set's metadata, per
///  set's drive key — and receive it in a caller-owned span they zero after use.
/// </para>
/// <para>
/// <b>Hygiene, not protection (§2.2).</b> The DEK lives in a pinned array (so the GC never leaves copies behind while
///  compacting) and is zeroed on <see cref="Dispose"/>. Nothing more: the running process is outside the threat model.
/// </para>
/// <para>
/// <b>Ownership.</b> Keys are owned by the <see cref="TapeKeyRing"/>; sets and TOC copies hold references only. A disposed
///  key refuses every derivation (<see cref="ObjectDisposedException"/>), so a stale reference fails loudly.
/// </para>
/// <para>
/// <b>The derivations are part of the format.</b> Salt and info layouts below are fixed by this class and pinned by
///  <c>KeyDerivationTests</c>; changing a byte makes every existing encrypted set unreadable.
/// </para>
/// </remarks>
public sealed class TapeSetKey : IDisposable
{
    #region *** Constants ***

    /// <summary>Size of the DEK and of every derived key, in bytes (AES-256).</summary>
    public const int Size = 32;

    /// <summary>Size of the HKDF salt — the SetId's 16 bytes.</summary>
    private const int SaltSize = 16;

    private static ReadOnlySpan<byte> FileLabel => "TapeNET file v1"u8;
    private static ReadOnlySpan<byte> MetaLabel => "TapeNET set meta v1"u8;
    private static ReadOnlySpan<byte> DriveLabel => "TapeNET drive v1"u8;

    #endregion

    #region *** Fields ***

    // Pinned: the GC must not move — and thereby copy — key material during compaction.
    private readonly byte[] m_dek = GC.AllocateArray<byte>(Size, pinned: true);
    private bool m_disposed;

    #endregion

    #region *** Construction ***

    /// <summary>Wraps an existing DEK (e.g. just unwrapped from an envelope). The bytes are copied.</summary>
    internal TapeSetKey(ReadOnlySpan<byte> dek)
    {
        if (dek.Length != Size)
            throw new ArgumentException($"A data key is exactly {Size} bytes", nameof(dek));
        dek.CopyTo(m_dek);
    }

    /// <summary>Mints a fresh random DEK — a new chain (§4.2).</summary>
    internal static TapeSetKey Generate()
    {
        Span<byte> dek = stackalloc byte[Size];
        try
        {
            TapeCrypto.Fill(dek);
            return new TapeSetKey(dek);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    #endregion

    #region *** Properties ***

    /// <summary>Whether the key has been zeroed. A disposed key refuses every use.</summary>
    public bool IsDisposed => m_disposed;

    /// <summary>The raw DEK — for wrapping and comparison only. Never logged, never persisted.</summary>
    internal ReadOnlySpan<byte> Dek
    {
        get
        {
            ObjectDisposedException.ThrowIf(m_disposed, this);
            return m_dek;
        }
    }

    #endregion

    #region *** Derivations ***

    /// <summary>
    /// <c>K_file = HKDF-SHA256(DEK, salt = SetId, info = "TapeNET file v1" ‖ FileId as u64 big-endian)</c> — one key per
    ///  file record (Software scheme). Unique per <c>SetId ‖ FileId</c>, which E3 rests on.
    /// </summary>
    internal void DeriveFileKey(Guid setId, ulong fileId, Span<byte> key32)
    {
        Span<byte> info = stackalloc byte[FileLabel.Length + sizeof(ulong)];
        FileLabel.CopyTo(info);
        BinaryPrimitives.WriteUInt64BigEndian(info[FileLabel.Length..], fileId);
        Derive(setId, info, key32);
    }

    /// <summary>
    /// <c>K_meta = HKDF-SHA256(DEK, salt = SetId, info = "TapeNET set meta v1")</c> — seals one set's TOC entry, in both
    ///  schemes.
    /// </summary>
    internal void DeriveMetaKey(Guid setId, Span<byte> key32) => Derive(setId, MetaLabel, key32);

    /// <summary>
    /// <c>K_drive = HKDF-SHA256(DEK, salt = SetId, info = "TapeNET drive v1")</c> — the key loaded into the drive for one
    ///  set (Hardware scheme, v2). Reserved now so v2 adds no new key material.
    /// </summary>
    internal void DeriveDriveKey(Guid setId, Span<byte> key32) => Derive(setId, DriveLabel, key32);

    /// <summary>
    /// The shared HKDF step. The salt is the SetId in <see cref="Guid.TryWriteBytes(Span{byte})"/> layout — the .NET
    ///  in-memory order, fixed for all time by this method.
    /// </summary>
    /// <exception cref="ArgumentException">An empty SetId (a legacy or unstamped set) — never encryptable.</exception>
    private void Derive(Guid setId, ReadOnlySpan<byte> info, Span<byte> key32)
    {
        if (setId == Guid.Empty)
            throw new ArgumentException("An encrypted set must carry a SetId", nameof(setId));
        if (key32.Length != Size)
            throw new ArgumentException($"A derived key is exactly {Size} bytes", nameof(key32));

        Span<byte> salt = stackalloc byte[SaltSize];
        setId.TryWriteBytes(salt);
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Dek, key32, salt, info);
    }

    #endregion

    #region *** Comparison ***

    /// <summary>
    /// Whether <paramref name="other"/> holds the same DEK — in constant time. Lets the key ring keep ONE instance per
    ///  chain however often its password is entered.
    /// </summary>
    internal bool SameKeyAs(TapeSetKey other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return CryptographicOperations.FixedTimeEquals(Dek, other.Dek);
    }

    #endregion

    #region *** Disposal ***

    /// <summary>Zeroes the DEK. Idempotent.</summary>
    public void Dispose()
    {
        if (m_disposed)
            return;
        CryptographicOperations.ZeroMemory(m_dek);
        m_disposed = true;
    }

    /// <summary>Never reveals key material.</summary>
    public override string ToString() => m_disposed ? "TapeSetKey(disposed)" : "TapeSetKey(****)";

    #endregion
}