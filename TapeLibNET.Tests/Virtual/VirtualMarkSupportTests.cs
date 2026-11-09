using Microsoft.Extensions.Logging.Abstractions;
using TapeLibNET.Virtual;

namespace TapeLibNET.Tests.Virtual;

/// <summary>
/// The mark profile persists in the virtual media's state record: new media records it, reopening under another
///  profile yields the recorded one, and media without a record takes the opener's profile and records it on its
///  first write.
/// </summary>
public sealed class VirtualMarkSupportTests : IDisposable
{
    private const long Capacity = 1024 * 1024;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"VMarkSupport_{Guid.NewGuid():N}");
    private string ContentPath => Path.Combine(_root, "media.vt");

    public VirtualMarkSupportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    public static TheoryData<string> Profiles => ["Setmarks", "SeqFilemarks", "FilemarksOnly", "FullFeatured"];

    private static VirtualTapeDriveCapabilities ProfileOf(string name) => name switch
    {
        "Setmarks" => VirtualTapeDriveCapabilities.WithSetmarks,
        "SeqFilemarks" => VirtualTapeDriveCapabilities.WithSeqFilemarks,
        "FilemarksOnly" => VirtualTapeDriveCapabilities.WithFilemarksOnly,
        "FullFeatured" => VirtualTapeDriveCapabilities.FullFeatured,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private VirtualTapeDriveBackend Open(VirtualTapeDriveCapabilities caps, FileMode mode)
    {
        var backend = VirtualTapeDriveBackend.CreateFileBacked(NullLoggerFactory.Instance, ContentPath,
            mode == FileMode.Open ? 0 : Capacity, capabilities: caps, mediaMode: mode);
        Assert.True(backend.Open(0));
        Assert.True(backend.LoadMedia(), backend.LastErrorMessage);
        return backend;
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void NewMedia_RecordsProfile_ReopenUnderOtherProfile_YieldsRecorded(string name)
    {
        var created = ProfileOf(name);
        using (var backend = Open(created, FileMode.Create))
            Assert.True(backend.IsMarkProfileRecorded);

        // Reopen deliberately under a different profile: the media's record wins.
        var other = created.SupportsSetmarks ? VirtualTapeDriveCapabilities.WithFilemarksOnly : VirtualTapeDriveCapabilities.WithSetmarks;
        using var reopened = Open(other, FileMode.Open);
        Assert.True(reopened.IsMarkProfileRecorded);
        Assert.Equal(created.SupportsSetmarks, reopened.SupportsSetmarks);
        Assert.Equal(created.SupportsSeqFilemarks, reopened.SupportsSeqFilemarks);
    }

    [Fact]
    public void UnrecordedMedia_TakesOpenersProfile_AndRecordsItOnFirstWrite()
    {
        // Simulate media written before the profile was persisted.
        using (var backend = Open(VirtualTapeDriveCapabilities.WithSeqFilemarks, FileMode.Create))
        {
            backend.ContentMedia!.MarkSupport = VirtualMarkSupport.Unknown;
            Assert.False(backend.IsMarkProfileRecorded);
        }

        // Open: no record, so the opener's profile applies — and nothing is recorded by merely reading.
        using (var backend = Open(VirtualTapeDriveCapabilities.WithSetmarks, FileMode.Open))
        {
            Assert.False(backend.IsMarkProfileRecorded);
            Assert.True(backend.SupportsSetmarks);
            Assert.False(backend.SupportsSeqFilemarks);

            // The first write fixes the layout to the profile it was written under.
            Assert.True(backend.WriteFilemarks(1, out _));
            Assert.True(backend.IsMarkProfileRecorded);
        }

        using var reopened = Open(VirtualTapeDriveCapabilities.WithSeqFilemarks, FileMode.Open);
        Assert.True(reopened.SupportsSetmarks);
        Assert.False(reopened.SupportsSeqFilemarks);
    }

    [Fact]
    public void Format_RecordsCurrentProfile()
    {
        using (var backend = Open(VirtualTapeDriveCapabilities.WithSetmarks, FileMode.Create))
        {
            backend.ContentMedia!.MarkSupport = VirtualMarkSupport.Unknown;
            Assert.True(backend.FormatMedia());
            Assert.True(backend.IsMarkProfileRecorded);
        }

        using var reopened = Open(VirtualTapeDriveCapabilities.WithFilemarksOnly, FileMode.Open);
        Assert.True(reopened.SupportsSetmarks);
    }

    [Fact]
    public void Extensions_RoundTrip()
    {
        foreach (var name in new[] { "Setmarks", "SeqFilemarks", "FilemarksOnly", "FullFeatured" })
        {
            var caps = ProfileOf(name);
            var support = caps.ToMarkSupport();
            Assert.True(support.IsRecorded());
            var applied = support.ApplyTo(VirtualTapeDriveCapabilities.Basic);
            Assert.Equal(caps.SupportsSetmarks, applied.SupportsSetmarks);
            Assert.Equal(caps.SupportsSeqFilemarks, applied.SupportsSeqFilemarks);
        }

        var basic = VirtualTapeDriveCapabilities.WithSetmarks;
        Assert.Equal(basic, VirtualMarkSupport.Unknown.ApplyTo(basic));
    }
}
