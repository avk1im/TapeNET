using HelpNET.Content;

namespace HelpIndexBuilder;

/// <summary>
/// <see cref="IHelpContentSource"/> that enumerates Markdown files from a
/// local directory tree.  Used exclusively by the <c>HelpIndexBuilder</c> tool;
/// not shipped with HelpNET or TapeWinNET.
/// </summary>
internal sealed class DirectoryHelpContentSource : IHelpContentSource
{
    private readonly string _rootDir;

    internal DirectoryHelpContentSource(string rootDir)
    {
        if (!Directory.Exists(rootDir))
            throw new DirectoryNotFoundException($"Help content directory not found: '{rootDir}'");

        _rootDir = rootDir;
    }

    /// <inheritdoc/>
    public string SourceId => _rootDir;

    /// <inheritdoc/>
    public async IAsyncEnumerable<HelpRawDocument> EnumerateAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var file in Directory.EnumerateFiles(
            _rootDir, "*.md", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            string logicalPath = Path.GetRelativePath(_rootDir, file)
                                     .Replace('\\', '/');
            string markdown    = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            var    lastMod     = File.GetLastWriteTimeUtc(file);

            yield return new HelpRawDocument(logicalPath, markdown, lastMod);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Returns <c>null</c> — the builder tool generates the bundle; it doesn't load one.
    /// </remarks>
    public Task<HelpEmbeddingBundle?> TryLoadEmbeddingBundleAsync(CancellationToken ct)
        => Task.FromResult<HelpEmbeddingBundle?>(null);
}
