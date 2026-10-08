using TapeLibNET.Virtual;

namespace TapeLibNET.Tests;


/// <summary>
/// Format-v2 Appendix A, Phase 0: unreadable virtual media metadata must never wipe the medium.
/// </summary>
public class VirtualTapeDriveBackendLoadTests
{
    private const int ContentBytes = 4096;

    private static VirtualTapeDriveBackend CreateBackend(MemoryStream content, MemoryStream metadata, FileMode mode)
    {
        var backend = new VirtualTapeDriveBackend(
            Helpers.TestLoggerFactory.Default,
            VirtualTapeDriveCapabilities.WithFilemarksOnlyLargeBlocks,
            contentCapacity: 10L * 1024 * 1024,
            content,
            ownsStreams: false,
            metadata)
        {
            MediaMode = mode,
        };
        Assert.True(backend.Open(0));
        return backend;
    }

    private static (MemoryStream content, MemoryStream metadata) CreateUnreadableState()
    {
        var content = new MemoryStream(new byte[ContentBytes]);
        var metadata = new MemoryStream([0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04]);
        return (content, metadata);
    }

    [Theory]
    [InlineData(FileMode.OpenOrCreate)]
    [InlineData(FileMode.Open)]
    public void LoadMedia_UnreadableMetadata_FailsWithoutTruncating(FileMode mode)
    {
        var (content, metadata) = CreateUnreadableState();
        long metadataLength = metadata.Length;

        using var backend = CreateBackend(content, metadata, mode);

        Assert.False(backend.LoadMedia());
        Assert.Equal(ContentBytes, content.Length);
        Assert.Equal(metadataLength, metadata.Length);
    }

    [Fact]
    public void LoadMedia_UnreadableMetadata_CreateMode_StartsOver()
    {
        var (content, metadata) = CreateUnreadableState();

        using var backend = CreateBackend(content, metadata, FileMode.Create);

        // Create is the explicit "start over": the stale content is discarded
        Assert.True(backend.LoadMedia());
        Assert.True(content.Length < ContentBytes);
    }

    [Fact]
    public void LoadMedia_AbsentMetadata_OpenOrCreate_CreatesNewMedia()
    {
        using var backend = CreateBackend(new MemoryStream(), new MemoryStream(), FileMode.OpenOrCreate);

        Assert.True(backend.LoadMedia());
    }
}
