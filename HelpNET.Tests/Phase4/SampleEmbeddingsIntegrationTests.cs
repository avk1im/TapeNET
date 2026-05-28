using System.Text;
using System.Text.Json;
using HelpNET.Content;
using HelpNET.Embeddings;
using HelpNET.Indexing;
using HelpNET.Retrieval;
using Xunit;
using Xunit.Abstractions;

namespace HelpNET.Tests.Phase4;

/// <summary>
/// End-to-end integration tests that exercise the full sample-corpus embedding
/// pipeline: content loading -> embedding search with a real precomputed bundle.
///
/// These tests are skipped automatically when the precomputed embeddings have
/// not yet been generated (the <c>Embeddings/</c> output directory is absent or
/// incomplete).  Run <c>HelpNET\Tools\Build-SampleEmbeddings.ps1</c> first to
/// generate the bundle, then re-run these tests.
/// </summary>
public class SampleEmbeddingsIntegrationTests(ITestOutputHelper output)
{
    // -- Paths ----------------------------------------------------------------

    /// <summary>
    /// Walks up from the test assembly output directory until a directory
    /// containing a <c>.sln</c> file is found.  This is robust against varying
    /// build configurations (Debug/Release) and trailing-separator differences.
    /// </summary>
    private static string SolutionRoot()
    {
        string? dir = AppContext.BaseDirectory
                               .TrimEnd(Path.DirectorySeparatorChar,
                                        Path.AltDirectorySeparatorChar);
        while (dir is not null)
        {
            if (Directory.EnumerateFiles(dir, "*.sln", SearchOption.TopDirectoryOnly).Any())
                return dir;
            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            "Could not locate the solution root (.sln) from the test assembly path: " +
            AppContext.BaseDirectory);
    }

    private static string SampleContentDir =>
        Path.Combine(SolutionRoot(), "HelpNET", "Tools", "SampleContent");

    private static string EmbeddingsDir =>
        Path.Combine(SampleContentDir, "Embeddings");

    private static string BinPath   => Path.Combine(EmbeddingsDir, "embeddings.bin");
    private static string MetaPath  => Path.Combine(EmbeddingsDir, "embeddings.meta.json");
    private static string IndexPath => Path.Combine(EmbeddingsDir, "embeddings.index.json");

    // Known parameters for all-MiniLM-L6-v2
    private const string ModelId   = "all-MiniLM-L6-v2";
    private const int    Dimension = 384;

    // -- Skip guard -----------------------------------------------------------

    /// <summary>
    /// Returns a skip reason string when the embeddings have not been generated,
    /// or <c>null</c> when the tests should run.
    /// </summary>
    private static string? SkipReason()
    {
        if (!Directory.Exists(EmbeddingsDir))
            return $"Embeddings directory not found: {EmbeddingsDir} -- run Build-SampleEmbeddings.ps1 first.";
        if (!File.Exists(BinPath))
            return $"embeddings.bin not found in {EmbeddingsDir}.";
        if (!File.Exists(MetaPath))
            return $"embeddings.meta.json not found in {EmbeddingsDir}.";
        if (!File.Exists(IndexPath))
            return $"embeddings.index.json not found in {EmbeddingsDir}.";
        return null;
    }

    // -- Fixture --------------------------------------------------------------

    /// <summary>
    /// Loads the sample content store and builds a <see cref="HelpEmbeddingIndex"/>
    /// from the precomputed bundle on disk and a <see cref="FakeEmbeddingGenerator"/>
    /// that produces consistent unit vectors for search queries.
    /// </summary>
    /// <remarks>
    /// The index uses <see cref="FakeEmbeddingGenerator"/> (not the real ONNX model)
    /// so that query embedding is deterministic and the tests do not require the ONNX
    /// model at runtime.  The bundle itself was produced by the real ONNX model via
    /// <c>Build-SampleEmbeddings.ps1</c>, so bundle-loading and cosine-search
    ///  correctness are exercised against real data.
    /// </remarks>
    private static async Task<(HelpEmbeddingIndex Index, HelpContentStore Store)> BuildFixtureAsync()
    {
        // 1. Load the content store from the sample directory.
        var source = new FilesystemContentSource(SampleContentDir);
        var store  = await HelpContentStore.LoadAsync(source).ConfigureAwait(false);

        // 2. Load the precomputed bundle from the three output files.
        var bundle = await LoadBundleAsync().ConfigureAwait(false);

        // 3. Build the index using the fake generator for query embedding
        //    (avoids requiring the ONNX model path at test runtime).
        var gen   = new FakeEmbeddingGenerator(Dimension);
        var index = HelpEmbeddingIndex.Build(bundle, gen, store);

        return (index, store);
    }

    /// <summary>
    /// Reads <c>embeddings.bin</c>, <c>embeddings.meta.json</c>, and
    /// <c>embeddings.index.json</c> from the output directory and assembles a
    /// <see cref="HelpEmbeddingBundle"/>.
    /// </summary>
    private static async Task<HelpEmbeddingBundle> LoadBundleAsync()
    {
        byte[] blob     = await File.ReadAllBytesAsync(BinPath).ConfigureAwait(false);
        string metaJson = await File.ReadAllTextAsync(MetaPath, Encoding.UTF8).ConfigureAwait(false);
        string idxJson  = await File.ReadAllTextAsync(IndexPath, Encoding.UTF8).ConfigureAwait(false);

        using var metaDoc = JsonDocument.Parse(metaJson);
        var root = metaDoc.RootElement;

        string modelId   = root.GetProperty("modelId").GetString()
                           ?? throw new InvalidDataException("modelId missing from meta.json");
        int    dimension = root.GetProperty("dimension").GetInt32();
        string modelHash = root.GetProperty("modelHash").GetString()
                           ?? throw new InvalidDataException("modelHash missing from meta.json");

        return new HelpEmbeddingBundle(
            modelId,
            dimension,
            modelHash,
            new ReadOnlyMemory<byte>(blob),
            idxJson);
    }

    // -- Tests ----------------------------------------------------------------

    [Fact]
    public async Task ContentStore_LoadsSampleTopics()
    {
        var skipReason = SkipReason();
        if (skipReason is not null) { output.WriteLine($"[SKIP] {skipReason}"); return; }

        var source = new FilesystemContentSource(SampleContentDir);
        var store  = await HelpContentStore.LoadAsync(source);

        output.WriteLine($"Loaded {store.All.Count} topics from sample corpus.");

        // Expect at least the 11 topics created in SampleContent/.
        Assert.True(store.All.Count >= 11,
            $"Expected >=11 topics, got {store.All.Count}.");

        // A few spot-checks on well-known ids.
        Assert.NotNull(store.GetById("home"));
        Assert.NotNull(store.GetById("quickstart.backup"));
        Assert.NotNull(store.GetById("concepts.fcl-filters"));
        Assert.NotNull(store.GetById("cli.overview"));
    }

    [Fact]
    public async Task Bundle_LoadsWithExpectedMetadata()
    {
        var skipReason = SkipReason();
        if (skipReason is not null) { output.WriteLine($"[SKIP] {skipReason}"); return; }

        var bundle = await LoadBundleAsync();

        output.WriteLine($"Bundle: modelId={bundle.ModelId}, dim={bundle.Dimension}, " +
                         $"blob={bundle.EmbeddingBlob.Length} bytes");

        Assert.Equal(ModelId,   bundle.ModelId);
        Assert.Equal(Dimension, bundle.Dimension);

        // Blob must be a multiple of dim x 4 bytes.
        Assert.Equal(0, bundle.EmbeddingBlob.Length % (Dimension * sizeof(float)));

        // ChunkIndexJson must be a non-empty JSON array.
        Assert.False(string.IsNullOrWhiteSpace(bundle.ChunkIndexJson));
        using var doc = JsonDocument.Parse(bundle.ChunkIndexJson);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);

        int chunkCount = bundle.EmbeddingBlob.Length / (Dimension * sizeof(float));
        Assert.Equal(chunkCount, doc.RootElement.GetArrayLength());
        output.WriteLine($"Chunk count: {chunkCount}");
    }

    [Fact]
    public async Task EmbeddingIndex_ReturnsResults_ForAnyQuery()
    {
        var skipReason = SkipReason();
        if (skipReason is not null) { output.WriteLine($"[SKIP] {skipReason}"); return; }

        var (index, _) = await BuildFixtureAsync();

        // With a fake generator the cosine scores will not be semantically
        // meaningful, but we verify that the pipeline wires up end-to-end:
        // parse -> chunk -> load bundle -> embed query -> cosine search -> results.
        var results = await index.SearchAsync("backup tape", topK: 5);

        output.WriteLine($"Got {results.Count} result(s):");
        foreach (var r in results)
            output.WriteLine($"  [{r.Score:F4}] {r.Topic.Id} -- {r.Heading}");

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.NotNull(r.Topic));
        Assert.All(results, r => Assert.InRange(r.Score, -1f, 1f));
    }

    [Fact]
    public async Task EmbeddingIndex_TopK_IsRespected()
    {
        var skipReason = SkipReason();
        if (skipReason is not null) { output.WriteLine($"[SKIP] {skipReason}"); return; }

        var (index, _) = await BuildFixtureAsync();

        var top1 = await index.SearchAsync("restore files from tape", topK: 1);
        var top3 = await index.SearchAsync("restore files from tape", topK: 3);

        Assert.True(top1.Count <= 1);
        Assert.True(top3.Count <= 3);
        Assert.True(top3.Count >= top1.Count);
    }

    [Fact]
    public async Task EmbeddingIndex_EmptyQuery_ReturnsEmpty()
    {
        var skipReason = SkipReason();
        if (skipReason is not null) { output.WriteLine($"[SKIP] {skipReason}"); return; }

        var (index, _) = await BuildFixtureAsync();

        var results = await index.SearchAsync("", topK: 5);
        Assert.Empty(results);
    }

    [Fact]
    public async Task EmbeddingIndex_AllResultTopics_ExistInContentStore()
    {
        var skipReason = SkipReason();
        if (skipReason is not null) { output.WriteLine($"[SKIP] {skipReason}"); return; }

        var (index, store) = await BuildFixtureAsync();

        var results = await index.SearchAsync("incremental backup archive bit", topK: 10);

        Assert.All(results, r =>
        {
            var topic = store.GetById(r.Topic.Id);
            Assert.NotNull(topic);
        });
    }
}

// -- Helper: file-system content source --------------------------------------

/// <summary>
/// Minimal <see cref="IHelpContentSource"/> that reads <c>.md</c> files from a
/// local directory tree.  Used only by the integration tests (not shipped with
///  the library).  Mirrors the logic in <c>HelpIndexBuilder.DirectoryHelpContentSource</c>
/// but lives here so the tests do not need a project reference to the tool.
/// </summary>
file sealed class FilesystemContentSource(string rootDir) : IHelpContentSource
{
    public string SourceId => rootDir;

    public async IAsyncEnumerable<HelpRawDocument> EnumerateAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var file in Directory.EnumerateFiles(
            rootDir, "*.md", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            string logicalPath = Path.GetRelativePath(rootDir, file).Replace('\\', '/');
            string markdown    = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            var    lastMod     = File.GetLastWriteTimeUtc(file);

            yield return new HelpRawDocument(logicalPath, markdown, lastMod);
        }
    }

    public Task<HelpEmbeddingBundle?> TryLoadEmbeddingBundleAsync(CancellationToken ct)
        => Task.FromResult<HelpEmbeddingBundle?>(null);
}