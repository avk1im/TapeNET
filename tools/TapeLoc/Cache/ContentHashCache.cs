using System.Security.Cryptography;
using System.Text;

namespace TapeLoc.Cache;

// Content-hash cache (docs/Design-TapeLoc.md §10). A file is skipped when its
//  canonical content, target culture, and rulesVersion all match a prior run.
//  The cache stores one marker file per (relative path) keyed entry under
//  loc/.cache/<lang>/.

internal sealed class ContentHashCache(string cacheRoot, string culture, string rulesVersion)
{
    private readonly string _dir = Path.Combine(cacheRoot, culture);
    private readonly string _culture = culture;
    private readonly string _rulesVersion = rulesVersion;

    public bool TryGetUnchanged(string relativePath, string sourceContent)
    {
        var markerPath = MarkerPath(relativePath);
        if (!File.Exists(markerPath))
            return false;

        var expected = ComputeHash(sourceContent);
        var actual = File.ReadAllText(markerPath).Trim();
        return string.Equals(expected, actual, StringComparison.Ordinal);
    }

    public void Update(string relativePath, string sourceContent)
    {
        var markerPath = MarkerPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        File.WriteAllText(markerPath, ComputeHash(sourceContent));
    }

    private string MarkerPath(string relativePath) =>
        Path.Combine(_dir, relativePath.Replace('/', Path.DirectorySeparatorChar) + ".hash");

    private string ComputeHash(string sourceContent)
    {
        // Salt the hash with culture + rulesVersion so changing either invalidates.
        var payload = $"{_culture}\u0000{_rulesVersion}\u0000{sourceContent}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes);
    }
}
