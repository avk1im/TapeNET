namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// Helpers for working with NTFS Alternate Data Streams (ADS) in tests.
/// <para>
/// ADS are accessed via the <c>file:streamname</c> colon syntax supported by
/// <see cref="File"/> and <see cref="FileStream"/> on NTFS volumes.
/// These helpers are intentionally thin wrappers so test code reads clearly.
/// </para>
/// </summary>
public static class AdsHelper
{
    // -----------------------------------------------------------------------
    // Guards
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns <c>true</c> when <paramref name="path"/> resides on an NTFS volume.
    /// Use this before writing or asserting ADS so tests self-skip on FAT/exFAT/network shares.
    /// </summary>
    public static bool IsNtfs(string path)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root))
            return false;
        try
        {
            return new DriveInfo(root).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // -----------------------------------------------------------------------
    // ADS I/O
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns the colon-separated path used to access the named ADS of a file.
    /// Example: <c>C:\Temp\foo.txt:metadata</c>
    /// </summary>
    public static string AdsPath(string filePath, string streamName)
        => $"{filePath}:{streamName}";

    /// <summary>
    /// Writes <paramref name="content"/> bytes to a named ADS.
    /// </summary>
    public static void WriteAds(string filePath, string streamName, byte[] content)
    {
        File.WriteAllBytes(AdsPath(filePath, streamName), content);
    }

    /// <summary>
    /// Writes a UTF-8 string to a named ADS.
    /// </summary>
    public static void WriteAds(string filePath, string streamName, string content)
        => WriteAds(filePath, streamName, System.Text.Encoding.UTF8.GetBytes(content));

    /// <summary>
    /// Reads all bytes from a named ADS.
    /// Returns <c>null</c> if the stream does not exist.
    /// </summary>
    public static byte[]? ReadAds(string filePath, string streamName)
    {
        string adsPath = AdsPath(filePath, streamName);
        return File.Exists(adsPath) ? File.ReadAllBytes(adsPath) : null;
    }

    /// <summary>
    /// Returns <c>true</c> when the named ADS exists on the given file.
    /// </summary>
    /// <remarks>
    /// Existence is checked by opening the colon-path; <see cref="File.Exists"/> alone is
    /// unreliable for ADS on some .NET versions.
    /// </remarks>
    public static bool AdsExists(string filePath, string streamName)
    {
        string adsPath = AdsPath(filePath, streamName);
        try
        {
            using var fs = new FileStream(adsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    // -----------------------------------------------------------------------
    // Assertions
    // -----------------------------------------------------------------------

    /// <summary>
    /// Asserts that the named ADS exists and its content exactly equals
    /// <paramref name="expected"/>.
    /// </summary>
    public static void AssertAdsContent(string filePath, string streamName, byte[] expected)
    {
        Assert.True(AdsExists(filePath, streamName),
            $"ADS not found: {AdsPath(filePath, streamName)}");

        byte[] actual = ReadAds(filePath, streamName)!;
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Asserts that the named ADS exists and its UTF-8 content equals
    /// <paramref name="expected"/>.
    /// </summary>
    public static void AssertAdsContent(string filePath, string streamName, string expected)
        => AssertAdsContent(filePath, streamName,
            System.Text.Encoding.UTF8.GetBytes(expected));
}
