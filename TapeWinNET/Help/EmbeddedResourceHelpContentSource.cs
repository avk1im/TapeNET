using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

using HelpNET.Content;

namespace TapeWinNET.Help;

/// <summary>
/// <see cref="IHelpContentSource"/> that reads help documents from embedded
/// resources in the TapeWinNET assembly under the logical path prefix
/// <c>*.Resources.Help.</c> (auto-detected from the manifest at construction time).
/// </summary>
/// <remarks>
/// Topics must be declared as <c>&lt;EmbeddedResource&gt;</c> in the .csproj.
/// The optional precomputed embedding bundle is loaded from
/// <c>TapeWinNET.Resources.Help._index.embeddings.bin</c> +
/// <c>…embeddings.meta.json</c> + <c>…embeddings.index.json</c>.
/// </remarks>
public sealed class EmbeddedResourceHelpContentSource : IHelpContentSource
{
    private const string HelpFolder = ".Resources.Help.";

    private readonly Assembly _assembly;
    private readonly string   _resourcePrefix;
    private readonly string   _bundleBin;
    private readonly string   _bundleMeta;
    private readonly string   _bundleIndex;

    public EmbeddedResourceHelpContentSource()
    {
        _assembly = Assembly.GetExecutingAssembly();

        // Auto-detect the prefix by finding the first manifest resource that
        //  contains ".Resources.Help.", so renaming the assembly or moving the
        //  class to another namespace never breaks discovery.
        var marker = _assembly.GetManifestResourceNames()
            .Select(n => n.IndexOf(HelpFolder, StringComparison.Ordinal))
            .FirstOrDefault(i => i >= 0, -1);

        _resourcePrefix = marker >= 0
            ? _assembly.GetManifestResourceNames()
                .First(n => n.IndexOf(HelpFolder, StringComparison.Ordinal) == marker)
                [..(marker + HelpFolder.Length)]
            : throw new InvalidOperationException(
                $"No embedded resources matching '*{HelpFolder}' were found in "
                + $"{_assembly.GetName().Name}. "
                + "Ensure help Markdown files are declared as EmbeddedResource.");

        _bundleBin   = _resourcePrefix + "_index.embeddings.bin";
        _bundleMeta  = _resourcePrefix + "_index.embeddings.meta.json";
        _bundleIndex = _resourcePrefix + "_index.embeddings.index.json";
    }

    /// <inheritdoc/>
    public string SourceId => "TapeWinNET.EmbeddedResources";

    /// <inheritdoc/>
    public async IAsyncEnumerable<HelpRawDocument> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var name in _assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(_resourcePrefix, StringComparison.Ordinal))
                continue;
            if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                continue;
            // Skip any bundle-index embedded files
            if (name.Contains("._index.", StringComparison.Ordinal))
                continue;

            ct.ThrowIfCancellationRequested();

            // Convert resource name back to a logical path, e.g.:
            //  TapeWinNET.Resources.Help.concepts.backup_sets.md  →  concepts/backup-sets.md
            var logicalPath = ToLogicalPath(name[_resourcePrefix.Length..]);

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
        var binStream   = _assembly.GetManifestResourceStream(_bundleBin);
        var metaStream  = _assembly.GetManifestResourceStream(_bundleMeta);
        var indexStream = _assembly.GetManifestResourceStream(_bundleIndex);

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
