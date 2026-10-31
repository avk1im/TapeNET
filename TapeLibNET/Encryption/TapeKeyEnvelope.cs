using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Windows.Win32.Foundation;

namespace TapeLibNET.Encryption;

// ── The envelope ─────────────────────────────────────────────────────────────

/// <summary>
/// A chain's DEK, wrapped under a password — exactly as stored in a sealed set's TOC stub (Design-Encryption §5.5).
///  Immutable; carries nothing secret.
/// </summary>
/// <remarks>
/// <para>
/// <b>Value equality over the bytes.</b> A record would compare the arrays by reference; the TOC's change tracking
///  (Phase 2, <c>PersistedState</c>) needs "is this the envelope I loaded", so equality is overridden to compare
///  contents.
/// </para>
/// <para>
/// <b>The parameters are authenticated.</b> The wrap's associated data covers Kdf, Iterations and Salt, so lowering the
///  iteration count or swapping the salt in a stored envelope only makes it refuse every password.
/// </para>
/// </remarks>
/// <param name="Kdf">Password KDF; only <see cref="TapeKdf.Pbkdf2Sha256"/> is implemented.</param>
/// <param name="Iterations">KDF iterations recorded at creation; readers use this, never their own default.</param>
/// <param name="Salt">KDF salt, <see cref="SaltSize"/> random bytes, fresh per envelope.</param>
/// <param name="WrapNonce">AES-GCM nonce of the wrap, <see cref="WrapNonceSize"/> random bytes.</param>
/// <param name="WrappedDek">The encrypted DEK followed by its 16-byte tag.</param>
public sealed record TapeKeyEnvelope(TapeKdf Kdf, int Iterations, byte[] Salt, byte[] WrapNonce, byte[] WrappedDek)
{
    /// <summary>KDF salt length.</summary>
    public const int SaltSize = 16;

    /// <summary>AES-GCM nonce length of the wrap.</summary>
    public const int WrapNonceSize = TapeAead.NonceSize;

    /// <summary>Wrapped DEK length: ciphertext plus tag.</summary>
    public const int WrappedDekSize = TapeSetKey.Size + TapeAead.TagSize;

    /// <summary>
    /// Whether every field has its exact size and the iteration count is within what a reader accepts
    ///  (<see cref="TapeCrypto.MaxIterations"/>). Says nothing about the password.
    /// </summary>
    public bool IsWellFormed =>
        Iterations is > 0 and <= TapeCrypto.MaxIterations
        && Salt is { Length: SaltSize }
        && WrapNonce is { Length: WrapNonceSize }
        && WrappedDek is { Length: WrappedDekSize };

    /// <inheritdoc/>
    public bool Equals(TapeKeyEnvelope? other) =>
        other is not null
        && Kdf == other.Kdf
        && Iterations == other.Iterations
        && Salt.AsSpan().SequenceEqual(other.Salt)
        && WrapNonce.AsSpan().SequenceEqual(other.WrapNonce)
        && WrappedDek.AsSpan().SequenceEqual(other.WrappedDek);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(Kdf, Iterations,
            Salt is { Length: >= sizeof(int) } ? BinaryPrimitives.ReadInt32LittleEndian(Salt) : 0);
}

// ── Chain creation, wrapping and unwrapping ──────────────────────────────────

/// <summary>
/// Password ↔ DEK: creating a chain, (re-)wrapping a DEK under a password, and unwrapping it (Design-Encryption §4, §7.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>A failed unwrap IS the password check.</b> There is no separate verifier — nothing cheaper than the KDF to guess
///  against. A wrong password and a tampered envelope look the same from here; the TOC's CRC-64 has already ruled out
///  plain media corruption by the time an envelope is unwrapped.
/// </para>
/// <para>
/// <b>Password normalization.</b> NFC, then strict UTF-8 — the same password typed on another machine or keyboard layout
///  yields the same key. The normalized password passes through a managed <see cref="string"/>, which cannot be zeroed;
///  its UTF-8 bytes are. Hygiene only (§2.2).
/// </para>
/// <para>
/// <b>Never logs.</b> No method here has a logger; results carry what went wrong, never the inputs.
/// </para>
/// </remarks>
public static class TapeSetEncryption
{
    #region *** Constants ***

    private static ReadOnlySpan<byte> EnvelopeLabel => "TapeNET env v1"u8;

    /// <summary>Associated data of the wrap: label ‖ Kdf (u8) ‖ Iterations (u32 big-endian) ‖ Salt.</summary>
    private const int AadSize = 14 + 1 + sizeof(uint) + TapeKeyEnvelope.SaltSize;

    private static readonly UTF8Encoding s_strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    #endregion

    #region *** Public API ***

    /// <summary>
    /// Mints a fresh DEK and wraps it under <paramref name="password"/> — a new encrypted chain.
    /// </summary>
    /// <returns>The envelope to store, and the DEK — which the caller hands to the key ring.</returns>
    /// <exception cref="ArgumentException">An empty password, or one that is not valid Unicode.</exception>
    public static (TapeKeyEnvelope Envelope, TapeSetKey Key) CreateChain(ReadOnlySpan<char> password)
    {
        TapeSetKey key = TapeSetKey.Generate();
        try
        {
            return (Wrap(key, password), key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Wraps an EXISTING DEK under <paramref name="password"/> with a fresh salt and nonce: the envelope of a new
    ///  incremental set (§8.2), or of a password change (§9.7).
    /// </summary>
    /// <exception cref="ArgumentException">An empty password, or one that is not valid Unicode.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="key"/> was disposed.</exception>
    public static TapeKeyEnvelope Wrap(TapeSetKey key, ReadOnlySpan<char> password)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (password.IsEmpty)
            throw new ArgumentException("A password must not be empty", nameof(password));

        int iterations = TapeCrypto.CreationIterations;
        var salt = new byte[TapeKeyEnvelope.SaltSize];
        var nonce = new byte[TapeKeyEnvelope.WrapNonceSize];
        TapeCrypto.Fill(salt);
        TapeCrypto.Fill(nonce);

        Span<byte> kek = stackalloc byte[TapeSetKey.Size];
        try
        {
            if (!TryDeriveKek(password, salt, iterations, kek))
                throw new ArgumentException("The password is not valid Unicode", nameof(password));

            Span<byte> aad = stackalloc byte[AadSize];
            BuildAad(TapeKdf.Pbkdf2Sha256, iterations, salt, aad);

            var wrapped = new byte[TapeKeyEnvelope.WrappedDekSize];
            using var aes = new AesGcm(kek, TapeAead.TagSize);
            aes.Encrypt(nonce, key.Dek, wrapped.AsSpan(0, TapeSetKey.Size), wrapped.AsSpan(TapeSetKey.Size), aad);

            return new TapeKeyEnvelope(TapeKdf.Pbkdf2Sha256, iterations, salt, nonce, wrapped);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>
    /// Unwraps the DEK from <paramref name="envelope"/> with <paramref name="password"/>.
    /// </summary>
    /// <param name="key">The unwrapped DEK on success; <see langword="null"/> otherwise. The caller owns it.</param>
    /// <returns>
    /// <see cref="TapeResult.OK"/>; <c>ERROR_INVALID_PASSWORD</c> for a wrong password (or a tampered envelope — the two
    ///  are indistinguishable by design); <c>ERROR_NOT_SUPPORTED</c> for a KDF this build does not implement;
    ///  <c>ERROR_INVALID_DATA</c> for a malformed envelope.
    /// </returns>
    public static TapeResult TryUnwrap(TapeKeyEnvelope envelope, ReadOnlySpan<char> password, out TapeSetKey? key)
    {
        key = null;
        if (envelope is null || !envelope.IsWellFormed)
            return TapeResult.Fail((uint)WIN32_ERROR.ERROR_INVALID_DATA, "The key envelope is malformed");
        if (envelope.Kdf != TapeKdf.Pbkdf2Sha256)
            return TapeResult.Fail((uint)WIN32_ERROR.ERROR_NOT_SUPPORTED,
                $"Key derivation '{envelope.Kdf}' is not supported by this build");

        Span<byte> kek = stackalloc byte[TapeSetKey.Size];
        Span<byte> dek = stackalloc byte[TapeSetKey.Size];
        try
        {
            if (password.IsEmpty || !TryDeriveKek(password, envelope.Salt, envelope.Iterations, kek))
                return WrongPassword();

            Span<byte> aad = stackalloc byte[AadSize];
            BuildAad(envelope.Kdf, envelope.Iterations, envelope.Salt, aad);

            using var aes = new AesGcm(kek, TapeAead.TagSize);
            try
            {
                aes.Decrypt(envelope.WrapNonce,
                    envelope.WrappedDek.AsSpan(0, TapeSetKey.Size),
                    envelope.WrappedDek.AsSpan(TapeSetKey.Size),
                    dek, aad);
            }
            catch (AuthenticationTagMismatchException)
            {
                return WrongPassword();
            }

            key = new TapeSetKey(dek);
            return TapeResult.OK;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    #endregion

    #region *** Helpers ***

    private static TapeResult WrongPassword()
        => TapeResult.Fail((uint)WIN32_ERROR.ERROR_INVALID_PASSWORD, "The password is not correct for this backup set");

    /// <summary>
    /// KEK = PBKDF2-HMAC-SHA256(NFC → strict UTF-8 (password), salt, iterations). Returns <see langword="false"/> when the
    ///  password is not valid Unicode (e.g. a lone surrogate) — such a password can never have been set.
    /// </summary>
    private static bool TryDeriveKek(ReadOnlySpan<char> password, ReadOnlySpan<byte> salt, int iterations, Span<byte> kek)
    {
        string normalized;
        try
        {
            normalized = password.ToString().Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return false;   // invalid code point
        }

        var utf8 = new byte[s_strictUtf8.GetMaxByteCount(normalized.Length)];
        try
        {
            int length = s_strictUtf8.GetBytes(normalized, 0, normalized.Length, utf8, 0);
            Rfc2898DeriveBytes.Pbkdf2(utf8.AsSpan(0, length), salt, kek, iterations, HashAlgorithmName.SHA256);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(utf8);
        }
    }

    /// <summary>Associated data of the wrap: <c>"TapeNET env v1" ‖ Kdf (u8) ‖ Iterations (u32 BE) ‖ Salt</c>.</summary>
    private static void BuildAad(TapeKdf kdf, int iterations, ReadOnlySpan<byte> salt, Span<byte> aad)
    {
        EnvelopeLabel.CopyTo(aad);
        int pos = EnvelopeLabel.Length;
        aad[pos++] = (byte)kdf;
        BinaryPrimitives.WriteUInt32BigEndian(aad[pos..], (uint)iterations);
        pos += sizeof(uint);
        salt.CopyTo(aad[pos..]);
    }

    #endregion
}