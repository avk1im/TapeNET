using Xunit;

namespace AiNET.Tests;

/// <summary>
/// Tests for <see cref="LanHostsRegistry"/> — JSON persistence round-trip
/// and concurrent add/remove behaviour.
/// </summary>
public class LanHostsRegistryTests : IDisposable
{
    // Redirect storage to a temp file so tests do not touch the real
    // %LocalAppData%\AiNET\lan-hosts.json on the developer's machine.
    private readonly string _tempDir;

    public LanHostsRegistryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"AiNET_Tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        LanHostsRegistry.OverrideStoragePathForTests(
            Path.Combine(_tempDir, "lan-hosts.json"));
    }

    public void Dispose()
    {
        LanHostsRegistry.OverrideStoragePathForTests(null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this); // recommendation CA1816: Dispose methods should call SuppressFinalize(this)
    }

    // ── Basic CRUD ───────────────────────────────────────────────────────────

    [Fact]
    public void GetAll_EmptyRegistry_ReturnsEmptyList()
    {
        var registry = new LanHostsRegistry();
        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public void Add_NewHost_AppearsInGetAll()
    {
        var registry = new LanHostsRegistry();
        registry.Add(new Uri("http://192.168.1.10:11434"));

        var hosts = registry.GetAll();
        Assert.Single(hosts);
        Assert.Equal(new Uri("http://192.168.1.10:11434"), hosts[0]);
    }

    [Fact]
    public void Add_DuplicateHost_StoredOnce()
    {
        var registry = new LanHostsRegistry();
        var host = new Uri("http://192.168.1.10:11434");
        registry.Add(host);
        registry.Add(host);

        Assert.Single(registry.GetAll());
    }

    [Fact]
    public void Remove_ExistingHost_DisappearsFromGetAll()
    {
        var registry = new LanHostsRegistry();
        var host = new Uri("http://192.168.1.10:11434");
        registry.Add(host);
        registry.Remove(host);

        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public void Remove_NonExistentHost_DoesNotThrow()
    {
        var registry = new LanHostsRegistry();
        var ex = Record.Exception(() => registry.Remove(new Uri("http://10.0.0.1:1234")));
        Assert.Null(ex);
    }

    [Fact]
    public void Clear_RemovesAllHosts()
    {
        var registry = new LanHostsRegistry();
        registry.Add(new Uri("http://10.0.0.1:11434"));
        registry.Add(new Uri("http://10.0.0.2:11434"));
        registry.Clear();

        Assert.Empty(registry.GetAll());
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    [Fact]
    public void Add_PersistsAcrossInstances()
    {
        var host = new Uri("http://192.168.1.20:1234");
        var r1 = new LanHostsRegistry();
        r1.Add(host);

        // New instance reads from the same file
        var r2 = new LanHostsRegistry();
        Assert.Contains(host, r2.GetAll());
    }

    [Fact]
    public void Clear_PersistsAcrossInstances()
    {
        var r1 = new LanHostsRegistry();
        r1.Add(new Uri("http://10.0.0.3:11434"));
        r1.Clear();

        var r2 = new LanHostsRegistry();
        Assert.Empty(r2.GetAll());
    }

    // ── Thread safety ────────────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentAdds_AllHostsStoredWithoutException()
    {
        var registry = new LanHostsRegistry();
        var hosts = Enumerable.Range(1, 20)
            .Select(i => new Uri($"http://10.0.0.{i}:11434"))
            .ToList();

        var tasks = hosts.Select(h => Task.Run(() => registry.Add(h))).ToArray();
        await Task.WhenAll(tasks);

        var stored = registry.GetAll();
        Assert.Equal(20, stored.Count);
        foreach (var h in hosts)
            Assert.Contains(h, stored);
    }
}
