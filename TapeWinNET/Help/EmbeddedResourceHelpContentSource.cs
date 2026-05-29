using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

using HelpNET.Content;

namespace TapeWinNET.Help;

/// <summary>
/// <see cref="IHelpContentSource"/> that reads help documents from embedded
/// resources in the TapeWinNET assembly under the logical path prefix
/// <c>TapeWinNET.Resources.Help.</c>.
/// </summary>
/// <remarks>
/// Topics must be declared as <c>&lt;EmbeddedResource&gt;</c> in the .csproj.
/// The optional precomputed embedding bundle is loaded from
/// <c>TapeWinNET.Resources.Help._index.embeddings.bin</c> +
/// <c>…embeddings.meta.json</c> + <c>…embeddings.index.json</c>.
/// </remarks>
public sealed class EmbeddedResourceHelpContentSource : IHelpContentSource
{
    private const string ResourcePrefix  = "TapeWin.Resources.Help.";
    private const string BundleBin       = ResourcePrefix + "_index.embeddings.bin";
    private const string BundleMeta      = ResourcePrefix + "_index.embeddings.meta.json";
    private const string BundleIndex     = ResourcePrefix + "_index.embeddings.index.json";

    private readonly Assembly _assembly;

    public EmbeddedResourceHelpContentSource()
    {
        _assembly = Assembly.GetExecutingAssembly();
    }

    /// <inheritdoc/>
    public string SourceId => "TapeWinNET.EmbeddedResources";

    /// <inheritdoc/>
    public async IAsyncEnumerable<HelpRawDocument> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var name in _assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                continue;
            if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                continue;
            // Skip any bundle-index embedded files
            if (name.Contains("._index.", StringComparison.Ordinal))
                continue;

            ct.ThrowIfCancellationRequested();

            // Convert resource name back to a logical path:
            //  TapeWin.Resources.Help.concepts.backup_sets.md  →  concepts/backup-sets.md
            var logicalPath = name[ResourcePrefix.Length..]
                .Replace('.', '/')
                // Restore the last segment's extension (the final '/' before .md should be '.')
                .Replace("/md", ".md");

            // Fix: the extension dot got converted — last occurrence of '/md' should be '.md'
            //  The simpler approach: just strip the prefix and replace underscores.
            //  Resource names use dots as path separators, so:
            //   TapeWin.Resources.Help.concepts.backup-sets.md is impossible (hyphens aren't valid
            //   in identifiers) — so file names use underscores in the resource but hyphens on disk.
            //  We keep whatever name the resource has; the id comes from front-matter.
            logicalPath = ToLogicalPath(name[ResourcePrefix.Length..]);

            await using var stream = _assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var markdown = await reader.ReadToEndAsync(ct);

            yield return new HelpRawDocument(logicalPath, markdown, LastModified: null);
        }
    }

    /// <inheritdoc/>
    public async Task<HelpEmbeddingBundle?> TryLoadEmbeddingBundleAsync(CancellationToken ct)
    {
        // All three files must be present
        var binStream   = _assembly.GetManifestResourceStream(BundleBin);
        var metaStream  = _assembly.GetManifestResourceStream(BundleMeta);
        var indexStream = _assembly.GetManifestResourceStream(BundleIndex);

        if (binStream is null || metaStream is null || indexStream is null)
            return null;

        await using var _ = binStream;
        await using var __ = metaStream;
        await using var ___ = indexStream;

        // Read binary blob
        using var ms = new MemoryStream();
        await binStream.CopyToAsync(ms, ct);
        var blob = new ReadOnlyMemory<byte>(ms.ToArray());

        // Read JSON metadata
        var meta = await JsonSerializer.DeserializeAsync<EmbeddingMeta>(metaStream,
            cancellationToken: ct) ?? throw new InvalidDataException("embeddings.meta.json is empty.");

        using var indexReader = new StreamReader(indexStream);
        var chunkIndexJson = await indexReader.ReadToEndAsync(ct);

        return new HelpEmbeddingBundle(
            ModelId:        meta.ModelId,
            Dimension:      meta.Dimension,
            ModelHash:      meta.ModelHash ?? string.Empty,
            EmbeddingBlob:  blob,
            ChunkIndexJson: chunkIndexJson);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts a bare resource suffix (after the <see cref="ResourcePrefix"/>) to a
    /// human-readable logical path.  The SDK replaces path separators with dots and
    /// hyphens in filenames with underscores, so we reverse that transformation.
    /// </summary>
    private static string ToLogicalPath(string resourceSuffix)
    {
        // The last dot separates the extension from the file name stem.
        // E.g.:  "concepts.backup_sets.md"  →  "concepts/backup-sets.md"
        int lastDot = resourceSuffix.LastIndexOf('.');
        if (lastDot < 0) return resourceSuffix;

        var directory = resourceSuffix[..lastDot].Replace('.', '/').Replace('_', '-');
        var ext       = resourceSuffix[lastDot..];          // ".md"

        // The "file name" is the last segment after the final '/'
        int lastSlash = directory.LastIndexOf('/');
        if (lastSlash < 0)
            return directory + ext;

        var folder   = directory[..(lastSlash + 1)];
        var fileName = directory[(lastSlash + 1)..];
        return folder + fileName + ext;
    }

    /// <summary>Minimal deserialization model for embeddings.meta.json.</summary>
    private sealed class EmbeddingMeta
    {
        public string ModelId   { get; init; } = string.Empty;
        public int    Dimension { get; init; }
        public string? ModelHash { get; init; }
    }
}
