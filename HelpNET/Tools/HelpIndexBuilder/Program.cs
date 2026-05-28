using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HelpNET.Content;
using HelpNET.Embeddings;
using HelpNET.Indexing;
using Microsoft.Extensions.AI;

namespace HelpIndexBuilder;

/// <summary>
/// Build-time console tool that produces a precomputed embedding bundle
/// (<c>embeddings.bin</c> + <c>embeddings.meta.json</c>) from a directory of
/// Markdown help topics.
/// </summary>
/// <remarks>
/// Usage:
/// <code>
///   HelpIndexBuilder
///     --content  &lt;path/to/help/markdown/dir&gt;
///     --model    &lt;path/to/model.onnx&gt;
///     --vocab    &lt;path/to/vocab.txt&gt;
///     --model-id &lt;e.g. all-MiniLM-L6-v2&gt;
///     --dim      &lt;embedding dimension, e.g. 384&gt;
///     --output   &lt;path/to/output/dir&gt;
///     [--max-tokens &lt;int, default 512&gt;]
///     [--dry-run]
/// </code>
/// Outputs in the specified directory:
/// <list type="bullet">
///  <item><c>embeddings.bin</c>  — packed little-endian float32 array, chunk-major.</item>
///  <item><c>embeddings.meta.json</c> — model id, hash, dimension, chunk catalog.</item>
/// </list>
/// </remarks>
internal static class Program
{
    // ── Entry point ───────────────────────────────────────────────────────────

    internal static async Task<int> Main(string[] args)
    {
        // ── Parse args ────────────────────────────────────────────────────────
        var parsed = ParseArgs(args);
        if (parsed is null)
        {
            PrintUsage();
            return 1;
        }

        Console.WriteLine($"[HelpIndexBuilder] Content : {parsed.ContentDir}");
        Console.WriteLine($"[HelpIndexBuilder] Model   : {parsed.ModelPath}");
        Console.WriteLine($"[HelpIndexBuilder] Vocab   : {parsed.VocabPath}");
        Console.WriteLine($"[HelpIndexBuilder] Model-id: {parsed.ModelId}");
        Console.WriteLine($"[HelpIndexBuilder] Dim     : {parsed.Dimension}");
        Console.WriteLine($"[HelpIndexBuilder] Output  : {parsed.OutputDir}");
        if (parsed.DryRun) Console.WriteLine("[HelpIndexBuilder] *** DRY RUN — no files written ***");

        try
        {
            await RunAsync(parsed).ConfigureAwait(false);
            Console.WriteLine("[HelpIndexBuilder] Done.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HelpIndexBuilder] FATAL: {ex.Message}");
            return 2;
        }
    }

    // ── Core pipeline ─────────────────────────────────────────────────────────

    private static async Task RunAsync(BuilderOptions opts)
    {
        // 1. Load content from the directory.
        var source = new DirectoryHelpContentSource(opts.ContentDir);
        var store  = await HelpContentStore.LoadAsync(source).ConfigureAwait(false);

        Console.WriteLine($"[HelpIndexBuilder] Loaded {store.All.Count} topics.");

        // 2. Chunk all topics (use the same maxTokens limit that the ONNX model imposes).
        var chunker = new Chunker(maxTokens: opts.MaxTokens);
        var chunks  = new List<HelpChunk>();
        foreach (var topic in store.All)
        {
            if (!topic.IncludeInAiCorpus) continue;
            chunks.AddRange(chunker.Chunk(topic));
        }

        Console.WriteLine($"[HelpIndexBuilder] Generated {chunks.Count} chunks.");

        if (chunks.Count == 0)
        {
            Console.WriteLine("[HelpIndexBuilder] No chunks to embed — nothing written.");
            return;
        }

        // 3. Load the ONNX model.
        Console.WriteLine("[HelpIndexBuilder] Loading ONNX model…");
        await using var modelStream = File.OpenRead(opts.ModelPath);
        await using var vocabStream = File.OpenRead(opts.VocabPath);

        var onnxOptions = new OnnxEmbeddingOptions(
            modelStream,
            vocabStream,
            opts.ModelId,
            opts.Dimension,
            opts.MaxTokens);

        using IEmbeddingGenerator<string, Embedding<float>> generator =
            new HelpOnnxEmbeddingGenerator(onnxOptions);

        // 4. Embed all chunks (in small batches to show progress).
        Console.WriteLine("[HelpIndexBuilder] Embedding chunks…");
        const int BatchSize = 32;
        var allVectors      = new List<float[]>(chunks.Count);

        for (int i = 0; i < chunks.Count; i += BatchSize)
        {
            var batch  = chunks.Skip(i).Take(BatchSize).Select(c => c.Text).ToList();
            var result = await generator.GenerateAsync(batch).ConfigureAwait(false);
            allVectors.AddRange(result.Select(e => e.Vector.ToArray()));

            int done = Math.Min(i + BatchSize, chunks.Count);
            Console.Write($"\r  {done}/{chunks.Count}");
        }

        Console.WriteLine();

        // 5. Compute model hash (SHA-256 of the model file for bundle validation).
        string modelHash = ComputeFileHash(opts.ModelPath);

        // 6. Serialise the blob (little-endian float32, chunk-major).
        var blob = new byte[chunks.Count * opts.Dimension * sizeof(float)];
        for (int i = 0; i < allVectors.Count; i++)
        {
            float[] vec = allVectors[i];
            if (vec.Length != opts.Dimension)
                throw new InvalidOperationException(
                    $"Chunk {i}: expected dimension {opts.Dimension}, got {vec.Length}.");

            int offset = i * opts.Dimension * sizeof(float);
            for (int d = 0; d < opts.Dimension; d++)
                BitConverter.TryWriteBytes(blob.AsSpan(offset + d * sizeof(float)), vec[d]);
        }

        // 7. Build the chunk-index JSON.
        var chunkIndex = chunks.Select((c, idx) => new
        {
            topicId    = c.TopicId,
            heading    = c.Heading,
            chunkIndex = c.Index
        }).ToList();

        string chunkIndexJson = JsonSerializer.Serialize(chunkIndex, new JsonSerializerOptions
        {
            WriteIndented = false
        });

        // 8. Build the metadata JSON.
        var meta = new
        {
            modelId   = opts.ModelId,
            dimension = opts.Dimension,
            modelHash,
            chunkCount = chunks.Count,
            builtAt   = DateTimeOffset.UtcNow.ToString("O")
        };

        string metaJson = JsonSerializer.Serialize(meta, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        // 9. Write outputs (unless dry-run).
        if (!opts.DryRun)
        {
            Directory.CreateDirectory(opts.OutputDir);

            string binPath  = Path.Combine(opts.OutputDir, "embeddings.bin");
            string metaPath = Path.Combine(opts.OutputDir, "embeddings.meta.json");
            string idxPath  = Path.Combine(opts.OutputDir, "embeddings.index.json");

            await File.WriteAllBytesAsync(binPath, blob).ConfigureAwait(false);
            await File.WriteAllTextAsync(metaPath, metaJson, Encoding.UTF8).ConfigureAwait(false);
            await File.WriteAllTextAsync(idxPath, chunkIndexJson, Encoding.UTF8).ConfigureAwait(false);

            Console.WriteLine($"[HelpIndexBuilder] Written: {binPath}");
            Console.WriteLine($"[HelpIndexBuilder] Written: {metaPath}");
            Console.WriteLine($"[HelpIndexBuilder] Written: {idxPath}");
        }
        else
        {
            Console.WriteLine($"[HelpIndexBuilder] Dry-run: would write {blob.Length} bytes to embeddings.bin");
            Console.WriteLine($"[HelpIndexBuilder] Dry-run: meta = {metaJson}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ComputeFileHash(string path)
    {
        using var sha = SHA256.Create();
        using var fs  = File.OpenRead(path);
        byte[] hash   = sha.ComputeHash(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static BuilderOptions? ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length - 1; i += 2)
        {
            if (args[i].StartsWith("--"))
                map[args[i][2..]] = args[i + 1];
        }

        bool dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);

        if (!map.TryGetValue("content",  out var contentDir) ||
            !map.TryGetValue("model",    out var modelPath)   ||
            !map.TryGetValue("vocab",    out var vocabPath)   ||
            !map.TryGetValue("model-id", out var modelId)     ||
            !map.TryGetValue("dim",      out var dimStr)      ||
            !map.TryGetValue("output",   out var outputDir))
            return null;

        if (!int.TryParse(dimStr, out int dim) || dim <= 0)
        {
            Console.Error.WriteLine("--dim must be a positive integer.");
            return null;
        }

        int maxTokens = 512;
        if (map.TryGetValue("max-tokens", out var mtStr))
            int.TryParse(mtStr, out maxTokens);

        return new BuilderOptions(
            contentDir, modelPath, vocabPath, modelId, dim, outputDir, maxTokens, dryRun);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            HelpIndexBuilder — generates precomputed embeddings for HelpNET.

            Usage:
              HelpIndexBuilder
                --content   <dir>      Directory of Markdown help topics
                --model     <file>     Path to .onnx embedding model
                --vocab     <file>     Path to vocab.txt (WordPiece tokenizer)
                --model-id  <string>   Stable model identifier (e.g. all-MiniLM-L6-v2)
                --dim       <int>      Embedding vector dimension (e.g. 384)
                --output    <dir>      Output directory for .bin and .meta.json files
                [--max-tokens <int>]   Max token sequence length (default 512)
                [--dry-run]            Parse and embed but do not write output files

            Outputs:
              embeddings.bin           Packed float32 blob (chunk-major, little-endian)
              embeddings.meta.json     Model id, hash, dimension, build timestamp
              embeddings.index.json    Chunk catalog (topicId, heading, chunkIndex)
            """);
    }

    // ── Option bag ────────────────────────────────────────────────────────────

    private sealed record BuilderOptions(
        string ContentDir,
        string ModelPath,
        string VocabPath,
        string ModelId,
        int    Dimension,
        string OutputDir,
        int    MaxTokens,
        bool   DryRun);
}
