using System.Runtime.InteropServices;
using Windows.Win32;

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
        var created = new List<string>
        {
            // Zero-byte file
            AddFile("edges/empty.dat", 0),

            // Single byte
            AddFile("edges/one_byte.dat", 1),

            // Exactly one block
            AddFile("edges/exact_block.dat", blockSize),

            // Block + 1 (forces a second block with minimal content)
            AddFile("edges/block_plus_one.dat", blockSize + 1),

            // Two full blocks
            AddFile("edges/two_blocks.dat", blockSize * 2),

            // Just under one block
            AddFile("edges/block_minus_one.dat", blockSize - 1),

            // Larger file (~256 KB)
            AddFile("edges/large_256k.dat", 256 * 1024),

            // Larger file (~1 MB)
            AddFile("edges/large_1mb.dat", 1024 * 1024),

            // File name with spaces
            AddFile("edges/file with spaces.txt", 100),

            // File name with special characters
            AddFile("edges/special (copy) [1] {test}.txt", 100),

            // File name with dots
            AddFile("edges/archive.2024.01.15.tar.gz", 200),

            // Deeply nested directory
            AddFile("edges/deep/nested/path/to/file.dat", 500),

            // Read-only file
            AddFile("edges/readonly.dat", 100, FileAttributes.ReadOnly),

            // Hidden file
            AddFile("edges/hidden.dat", 100, FileAttributes.Hidden),

            // Archive attribute
            AddFile("edges/archive.dat", 100, FileAttributes.Archive)
        };

        return created;
    }

    #endregion

    #region *** Sparse File Creation ***

    /// <summary>Size of each pattern region written into sparse files.</summary>
    private const int SparseRegionSize = 64 * 1024;

    /// <summary>The 2 GB boundary — <c>int.MaxValue + 1</c>.</summary>
    private const long Boundary2GB = 2L * 1024 * 1024 * 1024;

    /// <summary>The 4 GB boundary — <c>uint.MaxValue + 1</c>.</summary>
    private const long Boundary4GB = 4L * 1024 * 1024 * 1024;

    /// <summary>
    /// Creates an NTFS sparse file at the specified relative path.
    /// The file has the given logical <paramref name="size"/> but only consumes disk
    /// space for a few small pattern regions at strategic positions:
    /// <list type="bullet">
    ///   <item>Start of file (first 64 KB)</item>
    ///   <item>Near the 2 GB boundary (straddles <c>int.MaxValue</c>)</item>
    ///   <item>Near the 4 GB boundary (straddles <c>uint.MaxValue</c>)</item>
    ///   <item>End of file (last 64 KB)</item>
    /// </list>
    /// Unwritten regions read back as zeros, which is fine for deterministic
    /// backup → restore → compare round-trips.
    /// </summary>
    /// <param name="relativePath">Path relative to <see cref="RootPath"/>.</param>
    /// <param name="size">Logical file size in bytes.</param>
    /// <returns>Full path of the created sparse file.</returns>
    public string AddSparseFile(string relativePath, long size)
    {
        string fullPath = Path.Combine(RootPath, relativePath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        WriteSparsePatternFile(fullPath, size);

        Files.Add(fullPath);
        TotalSize += size;
        return fullPath;
    }

    /// <summary>
    /// Creates a sparse file and writes the repeating test pattern at key positions.
    /// </summary>
    private static void WriteSparsePatternFile(string path, long size)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        // Mark as sparse — requires NTFS; uses CsWin32-generated DeviceIoControl
        if (!FileSystemHelpers.SetSparseFlag(fs.SafeFileHandle))
        {
            throw new InvalidOperationException(
                $"Failed to set sparse flag (Win32 error {Marshal.GetLastWin32Error()}). " +
                "Ensure the file is on an NTFS volume.");
        }

        // Set the logical file size — unallocated regions will read as zeros
        fs.SetLength(size);

        if (size == 0)
            return;

        int regionSize = (int)Math.Min(SparseRegionSize, size);
        byte[] buffer = new byte[regionSize];
        FillWithPattern(buffer);

        // Region 1: start of file
        WriteRegion(fs, 0, buffer, size);

        // Region 2: straddle the 2 GB boundary (int.MaxValue)
        if (size > Boundary2GB)
            WriteRegion(fs, Boundary2GB - regionSize / 2, buffer, size);

        // Region 3: straddle the 4 GB boundary (uint.MaxValue)
        if (size > Boundary4GB)
            WriteRegion(fs, Boundary4GB - regionSize / 2, buffer, size);

        // Region 4: end of file (skip if it overlaps with the start region)
        long endOffset = size - regionSize;
        if (endOffset > regionSize)
            WriteRegion(fs, endOffset, buffer, size);
    }

    /// <summary>Writes a pattern region at the given offset, clamped to file size.</summary>
    private static void WriteRegion(FileStream fs, long offset, byte[] pattern, long fileSize)
    {
        if (offset < 0) offset = 0;
        int toWrite = (int)Math.Min(pattern.Length, fileSize - offset);
        if (toWrite <= 0) return;
        fs.Position = offset;
        fs.Write(pattern, 0, toWrite);
    }

    #endregion

    #region *** File Modification ***

    /// <summary>
    /// Overwrites an existing file with version-tagged content, ensuring a newer
    /// <see cref="FileInfo.LastWriteTime"/> for incremental backup detection.
    /// The content is a repeating pattern that encodes the <paramref name="version"/>
    /// so that restored files can be verified against the expected version.
    /// </summary>
    /// <param name="fullPath">Full path of the file to modify (must already exist).</param>
    /// <param name="version">Logical version number encoded into the content pattern.</param>
    /// <param name="size">New file size in bytes. If <c>null</c>, keeps the original size.</param>
    public void ModifyFile(string fullPath, int version, long? size = null)
    {
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Cannot modify a file that does not exist.", fullPath);

        // Capture original size before overwriting
        long originalSize = new FileInfo(fullPath).Length;
        long targetSize = size ?? originalSize;

        // Update TotalSize tracking
        TotalSize += targetSize - originalSize;

        // Ensure LastWriteTime will differ — wait briefly if needed
        DateTime oldWrite = File.GetLastWriteTimeUtc(fullPath);

        WriteVersionedPatternFile(fullPath, targetSize, version);

        // Guarantee the timestamp advances (filesystem resolution can be coarse)
        DateTime newWrite = File.GetLastWriteTimeUtc(fullPath);
        if (newWrite <= oldWrite)
        {
            File.SetLastWriteTimeUtc(fullPath, oldWrite.AddSeconds(2));
        }
    }

    /// <summary>
    /// Writes a file filled with a version-tagged repeating pattern.
    /// The pattern embeds the version number so byte-for-byte comparison
    /// can distinguish different file versions.
    /// </summary>
    private static void WriteVersionedPatternFile(string path, long size, int version)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

        if (size == 0)
            return;

        byte[] versionPattern = System.Text.Encoding.UTF8.GetBytes($"TapeNET-v{version:D4}-");

        const int bufferSize = 64 * 1024;
        byte[] buffer = new byte[(int)Math.Min(bufferSize, size)];
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = versionPattern[i % versionPattern.Length];

        long remaining = size;
        while (remaining > 0)
        {
            int toWrite = (int)Math.Min(buffer.Length, remaining);
            fs.Write(buffer, 0, toWrite);
            remaining -= toWrite;
        }
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
