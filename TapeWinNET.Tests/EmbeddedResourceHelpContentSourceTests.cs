using System.Reflection;

using HelpNET.Content;

using TapeWinNET.Help;

using Xunit;

namespace TapeWinNET.Tests;

/// <summary>
/// Unit tests for <see cref="EmbeddedResourceHelpContentSource"/> using the
/// fake help resources embedded in this test assembly
/// (under <c>TapeWinNET.Tests.Resources.Help.*</c>).
/// </summary>
public sealed class EmbeddedResourceHelpContentSourceTests
{
    // The test assembly embeds two .md resources:
    //   TapeWinNET.Tests.Resources.Help.home.md
    //   TapeWinNET.Tests.Resources.Help.concepts.backup_sets.md
    //
    // We use the internal (Assembly, string?) constructor to point at this
    // assembly instead of the TapeWinNET production assembly.

    private static readonly Assembly TestAssembly = typeof(EmbeddedResourceHelpContentSourceTests).Assembly;

    /// <summary>
    /// The resource prefix in the test assembly (all resources begin with this).
    /// Ends with the trailing dot so EmbeddedResourceHelpContentSource strips it
    /// correctly when deriving logical paths.
    /// </summary>
    private const string Prefix = "TapeWinNET.Tests.Resources.Help.";

    // ── SourceId ──────────────────────────────────────────────────────────────

    [Fact]
    public void SourceId_IsStable_AcrossInstances()
    {
        var a = new EmbeddedResourceHelpContentSource(TestAssembly, Prefix);
        var b = new EmbeddedResourceHelpContentSource(TestAssembly, Prefix);

        Assert.Equal(a.SourceId, b.SourceId);
    }

    [Fact]
    public void SourceId_IsNonEmpty()
    {
        var source = new EmbeddedResourceHelpContentSource(TestAssembly, Prefix);
        Assert.False(string.IsNullOrWhiteSpace(source.SourceId));
    }

    // ── EnumerateAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task EnumerateAsync_ReturnsExpectedDocumentCount()
    {
        var source = new EmbeddedResourceHelpContentSource(TestAssembly, Prefix);

        var docs = await CollectAsync(source);

        // Two .md files are embedded: home.md and concepts/backup-sets.md
        Assert.Equal(2, docs.Count);
    }

    [Fact]
    public async Task EnumerateAsync_ReturnsMarkdownWithContent()
    {
        var source = new EmbeddedResourceHelpContentSource(TestAssembly, Prefix);

        var docs = await CollectAsync(source);

        Assert.All(docs, d => Assert.False(string.IsNullOrWhiteSpace(d.Markdown)));
    }

    [Fact]
    public async Task EnumerateAsync_LogicalPaths_EndWithMdExtension()
    {
        var source = new EmbeddedResourceHelpContentSource(TestAssembly, Prefix);

        var docs = await CollectAsync(source);

        Assert.All(docs, d => Assert.EndsWith(".md", d.LogicalPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EnumerateAsync_LogicalPaths_ContainExpectedNames()
    {
        var source = new EmbeddedResourceHelpContentSource(TestAssembly, Prefix);

        var docs = await CollectAsync(source);
        var paths = docs.Select(d => d.LogicalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains(paths, p => p.EndsWith("home.md", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, p => p.Contains("backup", StringComparison.OrdinalIgnoreCase));
    }

    // ── TryLoadEmbeddingBundleAsync ────────────────────────────────────────────

    [Fact]
    public async Task TryLoadEmbeddingBundleAsync_ReturnsNull_WhenBundleAbsent()
    {
        // No _index/ resources are embedded in the test assembly.
        var source = new EmbeddedResourceHelpContentSource(TestAssembly, Prefix);

        var bundle = await source.TryLoadEmbeddingBundleAsync(CancellationToken.None);

        Assert.Null(bundle);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<List<HelpRawDocument>> CollectAsync(
        EmbeddedResourceHelpContentSource source,
        CancellationToken ct = default)
    {
        var list = new List<HelpRawDocument>();
        await foreach (var doc in source.EnumerateAsync(ct))
            list.Add(doc);
        return list;
    }
}
