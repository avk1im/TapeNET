using System.Buffers.Binary;
using System.Security.Cryptography;
using TapeLibNET.Encryption;

namespace TapeLibNET.Tests.Encryption;

/// <summary>
/// Design-Encryption §4.2, §15.1: the three derivations of <see cref="TapeSetKey"/>. Their byte layout is part of the
///  format — the <c>*_MatchesTheDefinition</c> tests pin it, so a change to salt or info breaks here, not on a tape.
/// </summary>
public class KeyDerivationTests
{
    #region *** Helpers ***

    private static readonly Guid s_setA = new("6a2f1c00-0021-4e0b-9d5c-0000000000a1");
    private static readonly Guid s_setB = new("6a2f1c00-0021-4e0b-9d5c-0000000000b2");

    private static TapeSetKey KeyOf(byte seed)
    {
        var dek = new byte[TapeSetKey.Size];
        new Random(seed).NextBytes(dek);
        return new TapeSetKey(dek);
    }

    private static byte[] File(TapeSetKey key, Guid set, ulong fileId)
    {
        var k = new byte[TapeSetKey.Size];
        key.DeriveFileKey(set, fileId, k);
        return k;
    }

    private static byte[] Meta(TapeSetKey key, Guid set)
    {
        var k = new byte[TapeSetKey.Size];
        key.DeriveMetaKey(set, k);
        return k;
    }

    private static byte[] Drive(TapeSetKey key, Guid set)
    {
        var k = new byte[TapeSetKey.Size];
        key.DeriveDriveKey(set, k);
        return k;
    }

    private static byte[] Reference(TapeSetKey key, Guid set, byte[] info)
    {
        var salt = new byte[16];
        set.TryWriteBytes(salt);
        var k = new byte[TapeSetKey.Size];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key.Dek, k, salt, info);
        return k;
    }

    #endregion

    #region *** The definitions ***

    [Fact]
    public void FileKey_MatchesTheDefinition()
    {
        using var key = KeyOf(1);
        byte[] info = [.. "TapeNET file v1"u8, .. new byte[8]];
        BinaryPrimitives.WriteUInt64BigEndian(info.AsSpan(15), 0x0102030405060708UL);
        Assert.Equal(Reference(key, s_setA, info), File(key, s_setA, 0x0102030405060708UL));
    }

    [Fact]
    public void MetaKey_MatchesTheDefinition()
    {
        using var key = KeyOf(1);
        Assert.Equal(Reference(key, s_setA, [.. "TapeNET set meta v1"u8]), Meta(key, s_setA));
    }

    [Fact]
    public void DriveKey_MatchesTheDefinition()
    {
        using var key = KeyOf(1);
        Assert.Equal(Reference(key, s_setA, [.. "TapeNET drive v1"u8]), Drive(key, s_setA));
    }

    #endregion

    #region *** Separation ***

    [Fact]
    public void EveryFileOfEverySet_GetsItsOwnKey()
    {
        using var key = KeyOf(1);
        var keys = new HashSet<string>
        {
            Convert.ToHexString(File(key, s_setA, 1)),
            Convert.ToHexString(File(key, s_setA, 2)),
            Convert.ToHexString(File(key, s_setB, 1)),
            Convert.ToHexString(File(key, s_setB, 2)),
        };
        Assert.Equal(4, keys.Count);
    }

    [Fact]
    public void Labels_Separate_FileMetaAndDriveKeys()
    {
        using var key = KeyOf(1);
        var keys = new HashSet<string>
        {
            Convert.ToHexString(File(key, s_setA, 0)),
            Convert.ToHexString(Meta(key, s_setA)),
            Convert.ToHexString(Drive(key, s_setA)),
        };
        Assert.Equal(3, keys.Count);
    }

    [Fact]
    public void DifferentDeks_DifferentKeys_SameDek_SameKeys()
    {
        using var a = KeyOf(1);
        using var b = KeyOf(2);
        using var aAgain = KeyOf(1);

        Assert.NotEqual(Meta(a, s_setA), Meta(b, s_setA));
        Assert.Equal(Meta(a, s_setA), Meta(aAgain, s_setA));
        Assert.True(a.SameKeyAs(aAgain));
        Assert.False(a.SameKeyAs(b));
    }

    #endregion

    #region *** Guards ***

    [Fact]
    public void EmptySetId_Refused()
    {
        using var key = KeyOf(1);
        Assert.Throws<ArgumentException>(() => Meta(key, Guid.Empty));
    }

    [Fact]
    public void WrongOutputSize_Refused()
    {
        using var key = KeyOf(1);
        Assert.Throws<ArgumentException>(() => key.DeriveMetaKey(s_setA, new byte[16]));
    }

    [Fact]
    public void WrongDekSize_Refused()
        => Assert.Throws<ArgumentException>(() => new TapeSetKey(new byte[16]));

    [Fact]
    public void DisposedKey_RefusesEveryUse_AndNeverPrintsItsBytes()
    {
        var key = KeyOf(1);
        Assert.Equal("TapeSetKey(****)", key.ToString());
        key.Dispose();
        key.Dispose();      // idempotent

        Assert.True(key.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => Meta(key, s_setA));
        Assert.Throws<ObjectDisposedException>(() => key.Dek.Length);
        Assert.Equal("TapeSetKey(disposed)", key.ToString());
    }

    [Fact]
    public void Generate_ProducesDistinctKeys_UnlessDeterministic()
    {
        using (var a = TapeSetKey.Generate())
        using (var b = TapeSetKey.Generate())
            Assert.False(a.SameKeyAs(b));

        static TapeSetKey Seeded()
        {
            using var _ = TapeCrypto.UseDeterministicRandom(5);
            return TapeSetKey.Generate();
        }
        using var c = Seeded();
        using var d = Seeded();
        Assert.True(c.SameKeyAs(d));
    }

    #endregion
}