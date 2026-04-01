namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// Generates a deterministic, reproducible directory tree of temporary files
/// for use in backup/restore round-trip tests.
/// <para>
/// Uses a fixed <see cref="Random"/> seed so that identical trees are generated
/// across test runs. The seed is recorded in <see cref="Seed"/> for diagnostics.
/// </para>
/// </summary>
public sealed class TempFileTree : IDisposable
{
    #region *** Constants ***

    /// <summary>Default seed for reproducible file content.</summary>
    public const int DefaultSeed = 42;

    /// <summary>Default repeating byte pattern for file content.</summary>
    private static readonly byte[] s_pattern = "TapeNET-TestData-"u8.ToArray();

    #endregion

    #region *** Properties ***

    /// <summary>Root directory containing all generated files.</summary>
    public string RootPath { get; }

    /// <summary>Random seed used for content generation.</summary>
    public int Seed { get; }

    /// <summary>Full paths of all generated files, in creation order.</summary>
    public List<string> Files { get; } = [];

    /// <summary>Total size of all generated files in bytes.</summary>
    public long TotalSize { get; private set; }

    #endregion

    #region *** Construction ***

    /// <summary>
    /// Creates a new temp file tree under a unique temporary directory.
    /// </summary>
    /// <param name="seed">Random seed for content generation.</param>
    /// <param name="basePath">Optional base directory (defaults to system temp).</param>
    public TempFileTree(int seed = DefaultSeed, string? basePath = null)
    {
        Seed = seed;
        RootPath = Path.Combine(
            basePath ?? Path.GetTempPath(),
            $"TapeNET_Test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(RootPath);
    }

    #endregion

    #region *** Single File Creation ***

    /// <summary>
    /// Creates a file at the specified relative path with a repeating byte pattern.
    /// </summary>
    /// <param name="relativePath">Path relative to <see cref="RootPath"/>.</param>
    /// <param name="size">File size in bytes.</param>
    /// <returns>Full path of the created file.</returns>
    public string AddFile(string relativePath, long size)
    {
        string fullPath = Path.Combine(RootPath, relativePath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        WritePatternFile(fullPath, size);

        Files.Add(fullPath);
        TotalSize += size;
        return fullPath;
    }

    /// <summary>
    /// Creates a file with explicit byte content.
    /// </summary>
    public string AddFile(string relativePath, byte[] content)
    {
        string fullPath = Path.Combine(RootPath, relativePath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllBytes(fullPath, content);

        Files.Add(fullPath);
        TotalSize += content.Length;
        return fullPath;
    }

    /// <summary>
    /// Creates a file and sets the specified <see cref="FileAttributes"/>.
    /// </summary>
    public string AddFile(string relativePath, long size, FileAttributes attributes)
    {
        string fullPath = AddFile(relativePath, size);
        File.SetAttributes(fullPath, attributes);
        return fullPath;
    }

    #endregion

    #region *** Batch Generation ***

    /// <summary>
    /// Generates multiple files with pseudo-random sizes in a subdirectory.
    /// </summary>
    /// <param name="subDir">Subdirectory name relative to root.</param>
    /// <param name="count">Number of files to generate.</param>
    /// <param name="minSize">Minimum file size in bytes.</param>
    /// <param name="maxSize">Maximum file size in bytes (exclusive).</param>
    /// <param name="extensions">Pool of file extensions to cycle through.</param>
    /// <returns>Full paths of the created files.</returns>
    public List<string> AddFiles(
        string subDir,
        int count,
        long minSize = 0,
        long maxSize = 64 * 1024,
        string[]? extensions = null)
    {
        extensions ??= [".txt", ".dat", ".bin", ".log", ".xml"];
        var rng = new Random(Seed + Files.Count); // vary per call to avoid identical batches
        var created = new List<string>(count);

        for (int i = 0; i < count; i++)
        {
            string ext = extensions[i % extensions.Length];
            string name = $"file_{i:D4}{ext}";
            long size = rng.NextInt64(minSize, maxSize);
            created.Add(AddFile(Path.Combine(subDir, name), size));
        }

        return created;
    }

    /// <summary>
    /// Adds a pre-built set of edge-case files that stress known boundaries:
    /// zero-byte, exact block size, block+1, large, special characters,
    /// and various file attributes.
    /// </summary>
    /// <param name="blockSize">Drive block size for boundary calculations.</param>
    /// <returns>Full paths of all edge-case files.</returns>
    public List<string> AddEdgeCases(uint blockSize = 16 * 1024)
    {
        var created = new List<string>();

        // Zero-byte file
        created.Add(AddFile("edges/empty.dat", 0));

        // Single byte
        created.Add(AddFile("edges/one_byte.dat", 1));

        // Exactly one block
        created.Add(AddFile("edges/exact_block.dat", blockSize));

        // Block + 1 (forces a second block with minimal content)
        created.Add(AddFile("edges/block_plus_one.dat", blockSize + 1));

        // Two full blocks
        created.Add(AddFile("edges/two_blocks.dat", blockSize * 2));

        // Just under one block
        created.Add(AddFile("edges/block_minus_one.dat", blockSize - 1));

        // Larger file (~256 KB)
        created.Add(AddFile("edges/large_256k.dat", 256 * 1024));

        // Larger file (~1 MB)
        created.Add(AddFile("edges/large_1mb.dat", 1024 * 1024));

        // File name with spaces
        created.Add(AddFile("edges/file with spaces.txt", 100));

        // File name with special characters
        created.Add(AddFile("edges/special (copy) [1] {test}.txt", 100));

        // File name with dots
        created.Add(AddFile("edges/archive.2024.01.15.tar.gz", 200));

        // Deeply nested directory
        created.Add(AddFile("edges/deep/nested/path/to/file.dat", 500));

        // Read-only file
        created.Add(AddFile("edges/readonly.dat", 100, FileAttributes.ReadOnly));

        // Hidden file
        created.Add(AddFile("edges/hidden.dat", 100, FileAttributes.Hidden));

        // Archive attribute
        created.Add(AddFile("edges/archive.dat", 100, FileAttributes.Archive));

        return created;
    }

    #endregion

    #region *** Content Generation ***

    /// <summary>
    /// Writes a file filled with a repeating byte pattern for deterministic content.
    /// </summary>
    private static void WritePatternFile(string path, long size)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

        if (size == 0)
            return;

        // Write the repeating pattern in chunks
        const int bufferSize = 64 * 1024;
        byte[] buffer = new byte[(int)Math.Min(bufferSize, size)];
        FillWithPattern(buffer);

        long remaining = size;
        while (remaining > 0)
        {
            int toWrite = (int)Math.Min(buffer.Length, remaining);
            fs.Write(buffer, 0, toWrite);
            remaining -= toWrite;
        }
    }

    /// <summary>
    /// Fills a buffer with the repeating test pattern.
    /// </summary>
    private static void FillWithPattern(byte[] buffer)
    {
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = s_pattern[i % s_pattern.Length];
    }

    #endregion

    #region *** Dispose ***

    public void Dispose()
    {
        // Reset read-only attributes before deletion
        try
        {
            foreach (var file in Directory.EnumerateFiles(RootPath, "*", SearchOption.AllDirectories))
            {
                var attrs = File.GetAttributes(file);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            }

            Directory.Delete(RootPath, recursive: true);
        }
        catch
        {
            // Best effort cleanup — temp directories may be locked
        }
    }

    #endregion
}
