using TapeLibNET.Encryption;
using TapeLibNET.Scan;
using TapeLibNET.Services;
using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Services;

/// <summary>
/// Design-Encryption Phase 5, service level (§12, §15.6): Scan Media on a cartridge with an encrypted set — counted and
///  worded without a password, nothing hidden reported; a recovered TOC adopted with its sealed set locked (E17).
/// </summary>
public class ServiceScanEncryptionTests : ServiceTestBase
{
    private const string Secret = "Hidden set name";

    private async Task<(TestTapeService, TestTapeServiceHost, TempFileTree, TempFileTree)> SeedAsync(TempVirtualMedia media)
    {
        var plain = new TempFileTree(seed: 71);
        plain.AddFiles("plain", count: 3, minSize: 512, maxSize: 4_096);
        var hidden = new TempFileTree(seed: 72);
        hidden.AddFiles("hiddenfiles", count: 3, minSize: 512, maxSize: 4_096);
        var (svc, host) = await OpenAndFormatAsync(media);
        Assert.True((await svc.ExecuteBackupAsync(MakeBackupRequest(svc, plain.RootPath, "Plain")
            with { ProceedOnMediaMismatch = true })).Success, host.DumpReports());
        Assert.True((await svc.ExecuteBackupAsync(MakeBackupRequest(svc, hidden.RootPath, Secret, append: true)
            with { Encryption = TapeEncryption.Software, Password = "pw for scan".ToCharArray(),
                   ProceedOnMediaMismatch = true })).Success, host.DumpReports());
        return (svc, host, plain, hidden);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scan_CountsAndNamesTheEncryptedSet_RevealsNothing(bool withInitiator)
    {
        using var scope = TapeCrypto.UseIterations(1_000); // we don't use scope but also need _ below
        using var media = new TempVirtualMedia(withInitiator, ContentCapacity, InitiatorCapacity);
        var (svc, host, plain, hidden) = await SeedAsync(media);

        while (host.Reports.TryDequeue(out _)) { }      // judge the scan's reports only — the seeding backup
                                                        //  logged the name it was given, legitimately (§2.2)
        // We use the queue drain rather than host.Reset(). Reset() would also empty PasswordPrompts and
        //  MediaMismatchPrompts, which the teardown sweep and Assert.Empty(host.PasswordPrompts) still check.

        using (svc) using (plain) using (hidden)
        {
            var result = await svc.ScanMediaAsync(new ScanMediaRequest());
            Assert.True(result.Success, $"{result.Message}\n{host.DumpReports()}");
            Assert.Equal(2, result.SetsFound);
            Assert.Equal(1, result.EncryptedSets);
            Assert.Equal(ServiceReportLevel.Completed, result.Outcome);         // encrypted is healthy
            Assert.True(host.ContainsMessage("encrypted backup set"), host.DumpReports());
            Assert.True(host.ContainsMessage("(encrypted, Software"), host.DumpReports());
            Assert.False(host.ContainsMessage(Secret), host.DumpReports());
            Assert.False(host.ContainsMessage("hiddenfiles"), host.DumpReports());
            Assert.Empty(host.PasswordPrompts);
        }
    }

    /// <summary>Adopting a recovered TOC: its sealed set is locked, and stays locked — the ring was cleared (E17).</summary>
    [Fact]
    public async Task Recover_Adopt_SealedSetStaysLocked()
    {
        using var _ = TapeCrypto.UseIterations(1_000);
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        var (svc, host, plain, hidden) = await SeedAsync(media);
        using (svc) using (plain) using (hidden)
        {
            MediaScanMap map = (await svc.ScanMediaAsync(new ScanMediaRequest())).Map!;
            int ordinal = map.Fragments.First(f => f.Kind == FragmentKind.TOC).Ordinal;
            var result = await svc.RecoverTocAsync(new RecoverTocRequest(map, ordinal) { Adopt = true });
            Assert.True(result.Success, result.Message);
            Assert.True(result.Adopted);
            Assert.Equal(2, svc.TOC!.Count);
            Assert.True(svc.TOC[2].IsLocked);
            Assert.False(svc.TOC[1].IsLocked);
            AssertNoMediaPrompts(host);
        }
    }
}
