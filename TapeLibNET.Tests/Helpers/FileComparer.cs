namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// Byte-for-byte comparison of original vs restored files.
/// Reports the first mismatch position for diagnostic clarity.
/// </summary>
public static class FileComparer
{
    /// <summary>Default buffer size for streaming comparison.</summary>
    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// Asserts that every file in <paramref name="originalPaths"/> has an identical
    /// counterpart under <paramref name="restoreRoot"/>, matching by relative path
    /// from <paramref name="originalRoot"/>.
    /// Compares: existence, size, and byte content.
    /// </summary>
    /// <param name="originalRoot">Root of the original file tree.</param>
    /// <param name="originalPaths">Full paths of the original files.</param>
    /// <param name="restoreRoot">Root of the restored file tree.</param>
    /// <param name="compareAttributes">Whether to also compare file attributes.</param>
    public static void AssertFilesMatch(
        string originalRoot,
        IReadOnlyList<string> originalPaths,
        string restoreRoot,
        bool compareAttributes = false)
    {
        foreach (string originalPath in originalPaths)
        {
            // Compute the relative path and find the restored counterpart
            string relativePath = Path.GetRelativePath(originalRoot, originalPath);
            string restoredPath = Path.Combine(restoreRoot, relativePath);

            Assert.True(File.Exists(restoredPath),
                $"Restored file not found: {restoredPath} (original: {originalPath})");

            var originalInfo = new FileInfo(originalPath);
            var restoredInfo = new FileInfo(restoredPath);

            // Size check
            Assert.Equal(originalInfo.Length, restoredInfo.Length);

            // Byte-for-byte comparison
            AssertContentEqual(originalPath, restoredPath);

            // Optional attribute comparison
            if (compareAttributes)
            {
                // Compare the meaningful subset (ignore transient attributes)
                const FileAttributes mask =
                    FileAttributes.ReadOnly | FileAttributes.Hidden |
                    FileAttributes.System | FileAttributes.Archive;

                Assert.Equal(
                    originalInfo.Attributes & mask,
                    restoredInfo.Attributes & mask);
            }
        }
    }

    /// <summary>
    /// Asserts that two files have identical byte content.
    /// Reports the first mismatch position on failure.
    /// </summary>
    public static void AssertContentEqual(string pathA, string pathB)
    {
        using var streamA = new FileStream(pathA, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var streamB = new FileStream(pathB, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.Equal(streamA.Length, streamB.Length);

        byte[] bufA = new byte[BufferSize];
        byte[] bufB = new byte[BufferSize];
        long position = 0;

        while (true)
        {
            int readA = ReadFully(streamA, bufA);
            int readB = ReadFully(streamB, bufB);

            Assert.Equal(readA, readB);

            if (readA == 0)
                break; // both streams exhausted

            for (int i = 0; i < readA; i++)
            {
                if (bufA[i] != bufB[i])
                {
                    Assert.Fail(
                        $"Content mismatch at byte {position + i}: " +
                        $"expected 0x{bufA[i]:X2}, actual 0x{bufB[i]:X2}. " +
                        $"Files: {pathA} vs {pathB}");
                }
            }

            position += readA;
        }
    }

    /// <summary>
    /// Reads exactly <paramref name="buffer"/>.Length bytes, or fewer only at end-of-stream.
    /// </summary>
    private static int ReadFully(Stream stream, byte[] buffer)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (read == 0)
                break;
            totalRead += read;
        }
        return totalRead;
    }
}
