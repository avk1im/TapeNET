using TapeLibNET.Services;

using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Services;


public class ServicePropertiesTests : ServiceTestBase
{

    /// <summary>Every property the media sheet holds reaches the CLI listing, one line each.</summary>
    [Fact]
    public async Task List_Media_LogsEveryDescribedProperty()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        var (svc, host) = await OpenAndFormatAsync(media);
        using (svc)
        {
            var list = await svc.ListContentsAsync(new ListRequest(Depth: ListDepth.Media));
            Assert.True(list.Success, list.Message);

            foreach (var p in svc.DescribeMedia())
                Assert.True(host.ContainsMessage($"{p.Label}: {p.Value}"), $"missing '{p.Label}'\n{host.DumpReports()}");
        }
    }

}
