using TapeLibNET.Encryption;

namespace TapeLibNET.Tests.Encryption;

/// <summary>
/// Design-Encryption §7, §15.1: the session key ring — trial before KDF, whole chains at once (E13), one instance per
///  DEK, and locking back (E12). Runs against a fake <see cref="ITapeLockableSet"/>: the TOC model learns to lock in
///  Phase 2.
/// </summary>
public class KeyRingTests
{
    #region *** Fake set ***

    /// <summary>
    /// Stands in for a sealed <c>TapeSetTOC</c>: "its sealed metadata authenticates under a key" is modelled as "the
    ///  key's <c>K_meta</c> for this SetId equals the one recorded at creation".
    /// </summary>
    private sealed class FakeSet : ITapeLockableSet
    {
        private readonly byte[] m_metaKey;

        public FakeSet(TapeKeyEnvelope envelope, TapeSetKey creatorKey, bool unlockable = true)
        {
            SetId = Guid.NewGuid();
            Envelope = envelope;
            IsUnlockable = unlockable;
            m_metaKey = new byte[TapeSetKey.Size];
            creatorKey.DeriveMetaKey(SetId, m_metaKey);
        }

        public Guid SetId { get; }
        public bool IsLocked => Key is null;
        public bool IsUnlockable { get; }
        public TapeKeyEnvelope? Envelope { get; }
        public TapeSetKey? Key { get; private set; }

        public bool Modified { get; set; }
        public bool SealCorrupted { get; set; }
        public int TryUnlockCalls { get; private set; }

        public bool TryUnlock(TapeSetKey key)
        {
            TryUnlockCalls++;
            if (SealCorrupted)
                return false;
            var k = new byte[TapeSetKey.Size];
            key.DeriveMetaKey(SetId, k);
            if (!k.AsSpan().SequenceEqual(m_metaKey))
                return false;
            Key = key;
            return true;
        }

        public void Lock()
        {
            if (Modified)
                throw new InvalidOperationException("unsaved changes");
            Key = null;
        }
    }

    #endregion

    #region *** Helpers ***

    private const uint ErrorInvalidData = 13;
    private const uint ErrorNotSupported = 50;
    private const uint ErrorInvalidPassword = 86;

    /// <summary>
    /// A chain of <paramref name="sets"/> sets: base + incrementals, each with its OWN envelope around the shared DEK.
    ///  All locked; the creator key is disposed, as in a fresh session.
    /// </summary>
    private static List<FakeSet> Chain(string password, int sets = 3)
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        var (baseEnvelope, key) = TapeSetEncryption.CreateChain(password);
        using (key)
        {
            var chain = new List<FakeSet> { new(baseEnvelope, key) };
            for (int i = 1; i < sets; i++)
                chain.Add(new FakeSet(TapeSetEncryption.Wrap(key, password), key));
            return chain;
        }
    }

    #endregion

    #region *** Unlocking ***

    [Fact]
    public void Unlock_OneSet_OpensItsWholeChain_AndNothingElse()
    {
        var alpha = Chain("alpha");
        var beta = Chain("beta");
        List<FakeSet> all = [.. alpha, .. beta];
        using var ring = new TapeKeyRing();

        TapeResult result = ring.Unlock(all, alpha[2], "alpha");     // an incremental's password prompt

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, ring.Count);
        Assert.All(alpha, s => Assert.False(s.IsLocked));
        Assert.All(beta, s => Assert.True(s.IsLocked));
        Assert.All(alpha, s => Assert.Same(alpha[0].Key, s.Key));    // one instance per DEK
    }

    [Fact]
    public void Unlock_SecondChain_AddsASecondKey()
    {
        var alpha = Chain("alpha");
        var beta = Chain("beta");
        List<FakeSet> all = [.. alpha, .. beta];
        using var ring = new TapeKeyRing();

        Assert.True(ring.Unlock(all, alpha[0], "alpha"));
        Assert.True(ring.Unlock(all, beta[1], "beta"));

        Assert.Equal(2, ring.Count);
        Assert.All(all, s => Assert.False(s.IsLocked));
    }

    [Fact]
    public void WrongPassword_InvalidPassword_NothingUnlocked_NothingHeld()
    {
        var alpha = Chain("alpha");
        using var ring = new TapeKeyRing();

        TapeResult result = ring.Unlock(alpha, alpha[0], "Alpha");

        Assert.False(result.Success);
        Assert.Equal(ErrorInvalidPassword, result.ErrorCode);
        Assert.Equal(ErrorInvalidPassword, ring.LastError);
        Assert.Equal(0, ring.Count);
        Assert.All(alpha, s => Assert.True(s.IsLocked));
    }

    /// <summary>A held key opens a newly met set of its chain without a password — and without a KDF.</summary>
    [Fact]
    public void TrialBeforeKdf_AHeldKeyNeedsNoPassword()
    {
        var alpha = Chain("alpha", sets: 2);
        using var ring = new TapeKeyRing();
        Assert.True(ring.Unlock(alpha, alpha[0], "alpha"));

        alpha[1].Lock();
        TapeResult result = ring.Unlock(alpha, alpha[1], "any text — never used");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(alpha[1].IsLocked);
        Assert.Equal(1, ring.Count);
    }

    [Fact]
    public void UnlockAll_ReturnsHowManySetsOpened()
    {
        var alpha = Chain("alpha", sets: 4);
        using var ring = new TapeKeyRing();
        Assert.True(ring.Unlock(alpha, alpha[0], "alpha"));
        foreach (var s in alpha)
            s.Lock();

        Assert.Equal(4, ring.UnlockAll(alpha));
        Assert.Equal(0, ring.UnlockAll(alpha));      // nothing left to open
    }

    [Fact]
    public void UnlockingAnUnlockedSet_IsOk_AndTouchesNothing()
    {
        var alpha = Chain("alpha", sets: 1);
        using var ring = new TapeKeyRing();
        Assert.True(ring.Unlock(alpha, alpha[0], "alpha"));
        int calls = alpha[0].TryUnlockCalls;

        Assert.True(ring.Unlock(alpha, alpha[0], "wrong"));
        Assert.Equal(calls, alpha[0].TryUnlockCalls);
    }

    [Fact]
    public void NotUnlockable_NotSupported_AndNeverOfferedAKey()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        var (envelope, key) = TapeSetEncryption.CreateChain("pw");
        var future = new FakeSet(envelope, key, unlockable: false);   // e.g. a Hardware set in a v1 build (E15)
        using var ring = new TapeKeyRing();
        ring.Add(key);

        TapeResult result = ring.Unlock([future], future, "pw");

        Assert.Equal(ErrorNotSupported, result.ErrorCode);
        Assert.Equal(0, ring.UnlockAll([future]));
        Assert.Equal(0, future.TryUnlockCalls);
    }

    /// <summary>The password unwraps the DEK, yet the set's metadata does not authenticate: tampering (§7.2).</summary>
    [Fact]
    public void CorrectPassword_ButSealDoesNotAuthenticate_InvalidData()
    {
        var alpha = Chain("alpha", sets: 1);
        alpha[0].SealCorrupted = true;
        using var ring = new TapeKeyRing();

        TapeResult result = ring.Unlock(alpha, alpha[0], "alpha");

        Assert.Equal(ErrorInvalidData, result.ErrorCode);
        Assert.True(alpha[0].IsLocked);
    }

    #endregion

    #region *** Keys ***

    [Fact]
    public void Add_EqualDek_KeepsTheFirstInstance_DisposesTheDuplicate()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        var (envelope, key) = TapeSetEncryption.CreateChain("pw");
        Assert.True(TapeSetEncryption.TryUnwrap(envelope, "pw", out TapeSetKey? duplicate));
        using var ring = new TapeKeyRing();

        Assert.Same(key, ring.Add(key));
        Assert.Same(key, ring.Add(duplicate!));
        Assert.Same(key, ring.Add(key));

        Assert.Equal(1, ring.Count);
        Assert.True(duplicate!.IsDisposed);
        Assert.False(key.IsDisposed);
    }

    #endregion

    #region *** Locking back ***

    [Fact]
    public void Withdraw_LocksExactlyTheSetsThatKeyOpened_AndZeroesIt()
    {
        var alpha = Chain("alpha");
        var beta = Chain("beta");
        List<FakeSet> all = [.. alpha, .. beta];
        using var ring = new TapeKeyRing();
        Assert.True(ring.Unlock(all, alpha[0], "alpha"));
        Assert.True(ring.Unlock(all, beta[0], "beta"));
        TapeSetKey alphaKey = alpha[0].Key!;

        ring.Withdraw(alphaKey, all);

        Assert.All(alpha, s => Assert.True(s.IsLocked));
        Assert.All(beta, s => Assert.False(s.IsLocked));
        Assert.True(alphaKey.IsDisposed);
        Assert.Equal(1, ring.Count);

        // The next access asks again — and the password still works (withdrawal is not revocation).
        Assert.True(ring.Unlock(all, alpha[1], "alpha"));
        Assert.All(alpha, s => Assert.False(s.IsLocked));
    }

    [Fact]
    public void Withdraw_WithUnsavedChanges_Throws_AndKeepsTheKey()
    {
        var alpha = Chain("alpha", sets: 2);
        using var ring = new TapeKeyRing();
        Assert.True(ring.Unlock(alpha, alpha[0], "alpha"));
        TapeSetKey key = alpha[0].Key!;
        alpha[1].Modified = true;

        Assert.Throws<InvalidOperationException>(() => ring.Withdraw(key, alpha));

        Assert.False(key.IsDisposed);
        Assert.Equal(1, ring.Count);
        Assert.False(alpha[1].IsLocked);
        Assert.Equal(1, ring.UnlockAll(alpha));      // the set locked before the throw opens again by trial
    }

    [Fact]
    public void Clear_LocksEverything_AndZeroesEveryKey()
    {
        var alpha = Chain("alpha");
        var beta = Chain("beta");
        List<FakeSet> all = [.. alpha, .. beta];
        using var ring = new TapeKeyRing();
        Assert.True(ring.Unlock(all, alpha[0], "alpha"));
        Assert.True(ring.Unlock(all, beta[0], "beta"));
        TapeSetKey alphaKey = alpha[0].Key!;

        ring.Clear(all);

        Assert.All(all, s => Assert.True(s.IsLocked));
        Assert.Equal(0, ring.Count);
        Assert.True(alphaKey.IsDisposed);
    }

    [Fact]
    public void Dispose_ZeroesKeys_AndRefusesFurtherUse()
    {
        var alpha = Chain("alpha", sets: 1);
        var ring = new TapeKeyRing();
        Assert.True(ring.Unlock(alpha, alpha[0], "alpha"));
        TapeSetKey key = alpha[0].Key!;

        ring.Dispose();

        Assert.True(key.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => ring.UnlockAll(alpha));
    }

    #endregion
}