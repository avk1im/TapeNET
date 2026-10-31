using TapeLibNET.Encryption;

namespace TapeLibNET.Tests.Encryption;

/// <summary>Design-Encryption §4.1, §10.6, §11.2: <see cref="TapeCrypto"/> scopes and <see cref="EncryptionPreset"/>.</summary>
public class EncryptionBasicsTests
{
    #region *** TapeCrypto ***

    [Fact]
    public void DeterministicRandom_RepeatsPerSeed_AndTheScopeRestores()
    {
        static byte[] Draw()
        {
            var bytes = new byte[32];
            TapeCrypto.Fill(bytes);
            return bytes;
        }

        byte[] a, b, c;
        using (TapeCrypto.UseDeterministicRandom(11))
            a = Draw();
        using (TapeCrypto.UseDeterministicRandom(11))
            b = Draw();
        using (TapeCrypto.UseDeterministicRandom(12))
            c = Draw();

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(Draw(), Draw());      // outside a scope: the real generator again
    }

    [Fact]
    public void Iterations_Scopes_Nest_AndRestore()
    {
        Assert.Equal(TapeCrypto.DefaultIterations, TapeCrypto.CreationIterations);
        using (TapeCrypto.UseIterations(1_000))
        {
            Assert.Equal(1_000, TapeCrypto.CreationIterations);
            using (TapeCrypto.UseIterations(2))
                Assert.Equal(2, TapeCrypto.CreationIterations);
            Assert.Equal(1_000, TapeCrypto.CreationIterations);
        }
        Assert.Equal(TapeCrypto.DefaultIterations, TapeCrypto.CreationIterations);
        Assert.Throws<ArgumentOutOfRangeException>(() => TapeCrypto.UseIterations(0));
    }

    /// <summary>A scope set by one test never leaks into a parallel one (AsyncLocal).</summary>
    [Fact]
    public async Task Iterations_Scope_DoesNotLeakAcrossFlows()
    {
        using var scope = TapeCrypto.UseIterations(7);
        Task<int> other;
        using (ExecutionContext.SuppressFlow())
            other = Task.Run(() => TapeCrypto.CreationIterations);   // starts with an empty context
        int inOtherFlow = await other;

        Assert.Equal(7, TapeCrypto.CreationIterations);
        Assert.Equal(TapeCrypto.DefaultIterations, inOtherFlow);
    }

    #endregion

    #region *** EncryptionPreset ***

    [Theory]
    [InlineData(null, TapeEncryption.None)]
    [InlineData("", TapeEncryption.None)]
    [InlineData("off", TapeEncryption.None)]
    [InlineData("NONE", TapeEncryption.None)]
    [InlineData("software", TapeEncryption.Software)]
    [InlineData(" sw ", TapeEncryption.Software)]
    [InlineData("drive", TapeEncryption.Hardware)]
    [InlineData("Hardware", TapeEncryption.Hardware)]
    [InlineData("hw", TapeEncryption.Hardware)]
    public void TryParse_Accepts(string? specifier, TapeEncryption expected)
    {
        Assert.True(EncryptionPreset.TryParse(specifier, out TapeEncryption encryption, out string? error));
        Assert.Equal(expected, encryption);
        Assert.Null(error);
    }

    [Fact]
    public void TryParse_Rejects_WithAMessage()
    {
        Assert.False(EncryptionPreset.TryParse("aes", out TapeEncryption encryption, out string? error));
        Assert.Equal(TapeEncryption.None, encryption);
        Assert.Contains("aes", error);
    }

    [Theory]
    [InlineData(TapeEncryption.None)]
    [InlineData(TapeEncryption.Software)]
    [InlineData(TapeEncryption.Hardware)]
    public void ToSpecifier_RoundTrips(TapeEncryption encryption)
    {
        Assert.True(EncryptionPreset.TryParse(EncryptionPreset.ToSpecifier(encryption), out TapeEncryption back, out _));
        Assert.Equal(encryption, back);
    }

    /// <summary>E15: a value this build does not know is named, never thrown on.</summary>
    [Fact]
    public void DisplayName_UnknownValue_IsUnsupported()
        => Assert.Equal("Unsupported method (99)", EncryptionPreset.DisplayName((TapeEncryption)99));

    #endregion

    #region *** TapeSetLockedException ***

    [Fact]
    public void LockedException_NamesTheSetAndTheMember()
    {
        var id = Guid.NewGuid();
        var ex = new TapeSetLockedException(id, "Count");
        //Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.IsType<InvalidOperationException>(ex, exactMatch: false);
        Assert.Equal(id, ex.SetId);
        Assert.Equal("Count", ex.Member);
        Assert.Contains(id.ToString("N"), ex.Message);
    }

    #endregion
}