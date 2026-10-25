using TapeLibNET.Agents;
using TapeLibNET.Services;
using TapeLibNET.Tests.Helpers;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests.Services;

/// <summary>
/// Design-TocLocation Phase 5, service level: the one TOC question — search the whole medium? — asked through the media
///  prompt (<see cref="MediaPromptContext.FullWalkForTOC"/>), once, and only where it applies.
/// </summary>
/// <remarks>
/// Every TOC copy is made unreadable inside the agent (<c>SimulateTOCFailureMask = -1</c>, armed through
///  <see cref="TestTapeService.OnAgentReady"/>, which the TOC handler factory raises): the ladder then exhausts S0–S2 and
///  arrives at the question. Whatever the answer, nothing reads — the tests are about the question.
/// </remarks>
public class ServiceTocFullWalkTests : ServiceTestBase
{
#if TEST_HOOKS
    #region *** Helpers ***

    private static async Task SeedAsync(ServiceTocFullWalkTests self, TempVirtualMedia media, bool withHeader = true)
    {
        using var src = new TempFileTree();
        src.AddFiles("walk", count: 3, minSize: 512, maxSize: 4_096);
        var (svc, host) = await self.OpenAndFormatAsync(media);
        using (svc)
            Assert.True((await svc.ExecuteBackupAsync(MakeBackupRequest(svc, src.RootPath, "Walk"))).Success,
                host.DumpReports());
    }

    /// <summary>Opens and loads the media WITHOUT reading the TOC — the test reads it, with its answers queued.</summary>
    private async Task<(TestTapeService, TestTapeServiceHost)> OpenUnreadAsync(TempVirtualMedia media)
    {
        var (service, host) = CreateService();
        var caps = media.HasInitiator
            ? VirtualTapeDriveCapabilities.WithPartitions
            : VirtualTapeDriveCapabilities.WithSetmarks;
        var vmd = new VirtualMediaDescriptor(media.ContentPath, media.ContentCapacity,
            media.InitiatorPath, media.HasInitiator ? media.InitiatorCapacity : 0);
        Assert.True(await service.OpenVirtualDriveAsync(caps, vmd, FileMode.Open), service.LastError);
        Assert.True(await service.LoadMediaAsync(), service.LastError);
        service.OnAgentReady = a => a.SimulateTOCFailureMask = -1;
        return (service, host);
    }

    #endregion

    #region *** RestoreTOCAsync ***

    /// <summary>Declined: asked once, nothing searched, and the failure says why.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreToc_Unreadable_AsksOnce_Declined(bool withInitiator)
    {
        using var media = new TempVirtualMedia(withInitiator, ContentCapacity, InitiatorCapacity);
        await SeedAsync(this, media);

        var (svc, host) = await OpenUnreadAsync(media);
        using (svc)
        {
            host.MediaMismatchAnswers.Enqueue(MediaMismatchChoice.Abort);
            Assert.False(await svc.RestoreTOCAsync());

            AssertMediaPrompts(host, (TapeMediaVerdict.Unidentified, MediaPromptContext.FullWalkForTOC));
            Assert.Contains("declined", svc.LastError);
            Assert.True(host.ContainsMessage("Search of the whole medium declined"), host.DumpReports());
        }
    }

    /// <summary>Accepted: the whole medium is searched — and here nothing reads, which the failure says too.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreToc_Unreadable_AsksOnce_Searches(bool withInitiator)
    {
        using var media = new TempVirtualMedia(withInitiator, ContentCapacity, InitiatorCapacity);
        await SeedAsync(this, media);

        var (svc, host) = await OpenUnreadAsync(media);
        using (svc)
        {
            host.MediaMismatchAnswers.Enqueue(MediaMismatchChoice.Proceed);
            Assert.False(await svc.RestoreTOCAsync());

            AssertMediaPrompts(host, (TapeMediaVerdict.Unidentified, MediaPromptContext.FullWalkForTOC));
            Assert.True(host.ContainsMessage("Searching the whole medium"), host.DumpReports());
            Assert.Contains("whole medium", svc.LastError);
        }
    }

    #endregion

    #region *** IdentifyMediaAsync ***

    /// <summary>A headed cartridge: no "search for a TOC?" question first — so the full-walk question is the only one.</summary>
    [Fact]
    public async Task Identify_Headed_Unreadable_AsksTheFullWalkQuestion()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        await SeedAsync(this, media);

        var (svc, host) = await OpenUnreadAsync(media);
        using (svc)
        {
            host.MediaMismatchAnswers.Enqueue(MediaMismatchChoice.Abort);
            Assert.Equal(IdentifyMediaOutcome.Failed, await svc.IdentifyMediaAsync());
            AssertMediaPrompts(host, (TapeMediaVerdict.Unidentified, MediaPromptContext.FullWalkForTOC));
        }
    }

    #endregion
#endif // TEST_HOOKS
}
