using TapeLibNET.Encryption;

namespace TapeLibNET.Tests.Encryption;

/// <summary>
/// Design-Encryption §15.1: password ↔ DEK — <see cref="TapeSetEncryption"/> and <see cref="TapeKeyEnvelope"/> (§4.1,
///  §5.5).
/// </summary>
/// <remarks>Every test runs under a low iteration count (TEST_HOOKS) — the work factor is not what is under test.</remarks>
public class KeyEnvelopeTests
{
    #region *** Helpers ***

    // Win32 codes, spelled out so the tests do not depend on the CsWin32 enum's visibility.
    private const uint ErrorInvalidData = 13;
    private const uint ErrorNotSupported = 50;
    private const uint ErrorInvalidPassword = 86;

    private const int TestIterations = 1_000;

    private static (TapeKeyEnvelope Envelope, TapeSetKey Key) NewChain(string password = "correct horse")
    {
        using var _ = TapeCrypto.UseIterations(TestIterations);
        return TapeSetEncryption.CreateChain(password);
    }

    private static TapeSetKey Unwrap(TapeKeyEnvelope envelope, string password)
    {
        TapeResult result = TapeSetEncryption.TryUnwrap(envelope, password, out TapeSetKey? key);
        Assert.True(result.Success, result.ErrorMessage);
        return key!;
    }

    private static void AssertUnwrapFails(TapeKeyEnvelope envelope, string password, uint expected)
    {
        TapeResult result = TapeSetEncryption.TryUnwrap(envelope, password, out TapeSetKey? key);
        Assert.False(result.Success);
        Assert.Equal(expected, result.ErrorCode);
        Assert.Null(key);
    }

    private static byte[] Flip(byte[] bytes, int at)
    {
        byte[] copy = [.. bytes];
        copy[at] ^= 0x01;
        return copy;
    }

    #endregion

    #region *** Round trip ***

    [Fact]
    public void CreateChain_ThenUnwrap_YieldsTheSameDek()
    {
        var (envelope, key) = NewChain();
        using (key)
        using (TapeSetKey back = Unwrap(envelope, "correct horse"))
            Assert.True(back.SameKeyAs(key));
    }

    [Fact]
    public void Envelope_IsWellFormed_AndRecordsItsParameters()
    {
        var (envelope, key) = NewChain();
        key.Dispose();

        Assert.True(envelope.IsWellFormed);
        Assert.Equal(TapeKdf.Pbkdf2Sha256, envelope.Kdf);
        Assert.Equal(TestIterations, envelope.Iterations);
        Assert.Equal(TapeKeyEnvelope.SaltSize, envelope.Salt.Length);
        Assert.Equal(TapeKeyEnvelope.WrapNonceSize, envelope.WrapNonce.Length);
        Assert.Equal(TapeKeyEnvelope.WrappedDekSize, envelope.WrappedDek.Length);
    }

    [Fact]
    public void CreationIterations_DefaultsTo600k_OutsideAScope()
    {
        Assert.Equal(TapeCrypto.DefaultIterations, TapeCrypto.CreationIterations);
        Assert.Equal(600_000, TapeCrypto.DefaultIterations);
    }

    /// <summary>The same password typed as composed or decomposed Unicode opens the same envelope (§4.1).</summary>
    [Fact]
    public void Password_IsNfcNormalized()
    {
        var (envelope, key) = NewChain("Caf\u00E9 \u00C5ngstr\u00F6m");      // precomposed
        using (key)
        using (TapeSetKey back = Unwrap(envelope, "Cafe\u0301 A\u030Angstro\u0308m"))   // decomposed
            Assert.True(back.SameKeyAs(key));
    }

    [Fact]
    public void Wrap_ExistingKey_NewPassword_SameDek_FreshSaltAndNonce()
    {
        var (first, key) = NewChain("old password");
        using (key)
        {
            TapeKeyEnvelope second;
            using (TapeCrypto.UseIterations(TestIterations))
                second = TapeSetEncryption.Wrap(key, "new password");

            Assert.NotEqual(first.Salt, second.Salt);
            Assert.NotEqual(first.WrapNonce, second.WrapNonce);

            using TapeSetKey viaOld = Unwrap(first, "old password");
            using TapeSetKey viaNew = Unwrap(second, "new password");
            Assert.True(viaOld.SameKeyAs(viaNew));
            AssertUnwrapFails(second, "old password", ErrorInvalidPassword);
        }
    }

    #endregion

    #region *** Refusals ***

    [Fact]
    public void WrongPassword_InvalidPassword()
    {
        var (envelope, key) = NewChain();
        key.Dispose();
        AssertUnwrapFails(envelope, "correct horsE", ErrorInvalidPassword);
        AssertUnwrapFails(envelope, "", ErrorInvalidPassword);
    }

    /// <summary>The wrap authenticates its parameters: lowering the work factor or swapping the salt gains nothing.</summary>
    [Fact]
    public void TamperedParameters_RefuseEveryPassword()
    {
        var (envelope, key) = NewChain();
        key.Dispose();

        AssertUnwrapFails(envelope with { Iterations = TestIterations - 1 }, "correct horse", ErrorInvalidPassword);
        AssertUnwrapFails(envelope with { Salt = Flip(envelope.Salt, 0) }, "correct horse", ErrorInvalidPassword);
        AssertUnwrapFails(envelope with { WrapNonce = Flip(envelope.WrapNonce, 3) }, "correct horse", ErrorInvalidPassword);
        AssertUnwrapFails(envelope with { WrappedDek = Flip(envelope.WrappedDek, 5) }, "correct horse", ErrorInvalidPassword);
        AssertUnwrapFails(envelope with { WrappedDek = Flip(envelope.WrappedDek, 40) }, "correct horse", ErrorInvalidPassword);
    }

    [Fact]
    public void UnknownKdf_NotSupported()
    {
        var (envelope, key) = NewChain();
        key.Dispose();
        AssertUnwrapFails(envelope with { Kdf = TapeKdf.Argon2id }, "correct horse", ErrorNotSupported);
        AssertUnwrapFails(envelope with { Kdf = (TapeKdf)99 }, "correct horse", ErrorNotSupported);
    }

    [Fact]
    public void MalformedEnvelope_InvalidData()
    {
        var (envelope, key) = NewChain();
        key.Dispose();
        AssertUnwrapFails(envelope with { Salt = envelope.Salt[..15] }, "correct horse", ErrorInvalidData);
        AssertUnwrapFails(envelope with { WrapNonce = [] }, "correct horse", ErrorInvalidData);
        AssertUnwrapFails(envelope with { WrappedDek = envelope.WrappedDek[..32] }, "correct horse", ErrorInvalidData);
        AssertUnwrapFails(envelope with { Iterations = 0 }, "correct horse", ErrorInvalidData);
        AssertUnwrapFails(envelope with { Iterations = TapeCrypto.MaxIterations + 1 }, "correct horse", ErrorInvalidData);
    }

    [Fact]
    public void EmptyOrInvalidPassword_RefusedOnCreation()
    {
        using var _ = TapeCrypto.UseIterations(TestIterations);
        Assert.Throws<ArgumentException>(() => TapeSetEncryption.CreateChain(""));
        Assert.Throws<ArgumentException>(() => TapeSetEncryption.CreateChain("bad\uD800"));
    }

    [Fact]
    public void Wrap_DisposedKey_Refused()
    {
        var (_, key) = NewChain();
        key.Dispose();
        using var _ = TapeCrypto.UseIterations(TestIterations);
        Assert.Throws<ObjectDisposedException>(() => TapeSetEncryption.Wrap(key, "pw"));
    }

    #endregion

    #region *** Equality and determinism ***

    [Fact]
    public void Equality_ComparesContents()
    {
        var (envelope, key) = NewChain();
        key.Dispose();
        var copy = envelope with
        {
            Salt = [.. envelope.Salt],
            WrapNonce = [.. envelope.WrapNonce],
            WrappedDek = [.. envelope.WrappedDek],
        };

        Assert.Equal(envelope, copy);
        Assert.Equal(envelope.GetHashCode(), copy.GetHashCode());
        Assert.NotEqual(envelope, copy with { WrappedDek = Flip(copy.WrappedDek, 0) });
    }

    /// <summary>Goldens rest on this: same seed, same password, same bytes (§15.7).</summary>
    [Fact]
    public void DeterministicRandom_ReproducesTheEnvelope()
    {
        static TapeKeyEnvelope Make()
        {
            using var random = TapeCrypto.UseDeterministicRandom(2026);
            var (envelope, key) = NewChain("golden");
            key.Dispose();
            return envelope;
        }

        Assert.Equal(Make(), Make());
    }

    #endregion
}