using TapeLibNET.Services;
using TapeLibNET.Toc;
using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Services;

/// <summary>
/// <see cref="TapeServiceBase.DescribeSet"/> describes any set and leaves the TOC's current set where it is.
/// </summary>
public class ServiceDescribeSetTests : ServiceTestBase
{
    private static readonly DateTime T0 = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>The two-volume incremental series of <c>TapeTOCCursorTests</c>, written to a .tapetoc file.</summary>
    private static string WriteSeriesFile(string folder)
    {
        var toc = new TapeTOC("series") { Volume = 1 };
        void Add(string name, bool incremental)
        {
            toc.AddNewSetTOC(incremental: incremental);
            toc.CurrentSetTOC.Description = name;
            var set = toc.CurrentSetTOC;
            set.Append(new TapeFileInfo(set.GenerateFileId(), TapeAddress.Zero, new TapeFileDescriptor($@"C:\{name}\f.bin")
            {
                Length = 100,
                CreationTime = T0,
                LastWriteTime = T0,
                LastAccessTime = T0,
            }));
        }
        Add("Full", false);
        Add("Inc1", true);
        toc.Volume = 2;
        Add("Inc2", true);

        string path = Path.Combine(folder, "series.tapetoc");
        using (var fs = File.Create(path))
            toc.SaveTo(fs);
        return path;
    }

    private static string Value(IReadOnlyList<ServiceProperty> props, string label)
        => Assert.Single(props, p => p.Label == label).Value;

    [Fact]
    public async Task DescribeSet_AnySet_CursorUntouched()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        var (svc, host) = CreateService();
        using (svc)
        {
            Assert.True(await svc.OpenVirtualDriveAsync(CapsFor(media), VmdFor(media), FileMode.Create), svc.LastError);
            Assert.True(await svc.LoadMediaAsync(), svc.LastError);     // the import's agent needs a navigator: media loaded
            Assert.True(await svc.ImportTOCFromFileAsync(WriteSeriesFile(media.Root)), svc.LastError);

            var toc = svc.TOC!;
            Assert.Equal(2, toc.Volume);
            toc.CurrentSetIndex = 1;        // parked on volume 1

            var inc2 = svc.DescribeSet(3);
            Assert.Equal("Inc2", Value(inc2, "Description"));
            Assert.Equal("Yes, incrementally", Value(inc2, "Continued from Previous Volume"));
            Assert.Equal("No", Value(inc2, "Continued on Next Volume"));

            var inc1 = svc.DescribeSet(2);
            Assert.Equal("No", Value(inc1, "Continued from Previous Volume"));

            Assert.Equal(1, toc.CurrentSetIndex);
            Assert.Empty(svc.DescribeSet(9));           // out of range: nothing, no throw
            Assert.Empty(svc.DescribeSet(0));
        }
    }
}
