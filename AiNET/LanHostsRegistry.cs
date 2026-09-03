using System.Text.Json;

namespace AiNET;

/// <summary>
/// Persists a list of user-defined LAN host URIs that should be probed
/// during discovery. Stored in
/// <c>%LocalAppData%\AiNET\lan-hosts.json</c>.
/// </summary>
/// <remarks>
/// All mutating methods are thread-safe. The file is written synchronously
/// inside a lock to avoid concurrent write races; reads always return a
/// snapshot copy.
/// </remarks>
public sealed class LanHostsRegistry
{
    public static readonly string DefaultStoragePath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AiNET",
            "lan-hosts.json");

    // Allows unit tests to redirect persistence to a temp directory.
    [ThreadStatic]
    private static string? _testStoragePathOverride;

    /// <summary>
    /// Overrides the storage path for the calling test. Pass <c>null</c> to
    /// restore production behaviour. Internal — for unit tests only.
    /// </summary>
    internal static void OverrideStoragePathForTests(string? path) =>
        _testStoragePathOverride = path;

    private static string StoragePath =>
        _testStoragePathOverride ?? DefaultStoragePath;

    private readonly object _lock = new();
    private readonly List<Uri> _hosts;

    /// <summary>
    /// Initialises the registry, loading any previously persisted hosts from
    /// disk. If the file does not exist, starts with an empty list.
    /// </summary>
    public LanHostsRegistry()
    {
        _hosts = LoadFromDisk();
    }

    /// <summary>Returns a snapshot of all currently registered LAN hosts.</summary>
    public IReadOnlyList<Uri> GetAll()
    {
        lock (_lock)
            return [.. _hosts];
    }

    /// <summary>
    /// Adds a host URI if it is not already present, then persists the list.
    /// </summary>
    /// <remarks>
    /// The URI is normalised first (see <see cref="Normalize"/>), so
    ///  <c>10.0.0.5:11434</c> and <c>http://10.0.0.5:11434/</c> are treated as
    ///  the same host rather than accumulating as duplicates.
    /// </remarks>
    /// <returns><c>true</c> if the host was added; <c>false</c> if already known.</returns>
    public bool Add(Uri host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var normalized = Normalize(host);
        lock (_lock)
        {
            if (_hosts.Any(h => Uri.Equals(h, normalized)))
                return false;

            _hosts.Add(normalized);
            SaveToDisk(_hosts);
            return true;
        }
    }

    /// <summary>
    /// Removes a host URI if present, then persists the list.
    /// </summary>
    /// <returns><c>true</c> if a matching host was removed.</returns>
    public bool Remove(Uri host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var normalized = Normalize(host);
        lock (_lock)
        {
            // Match both the normalised form and any legacy entry stored verbatim.
            var idx = _hosts.FindIndex(h => Uri.Equals(h, normalized) || Uri.Equals(h, host));
            if (idx < 0)
                return false;

            _hosts.RemoveAt(idx);
            SaveToDisk(_hosts);
            return true;
        }
    }

    /// <summary>
    /// Canonicalises a host URI so equivalent spellings compare equal:
    /// keeps only scheme/host/port and drops any path, query or fragment.
    /// </summary>
    private static Uri Normalize(Uri host)
    {
        var builder = new UriBuilder(host)
        {
            Path     = string.Empty,
            Query    = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    /// <summary>Removes all hosts and persists the empty list.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _hosts.Clear();
            SaveToDisk(_hosts);
        }
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    private static List<Uri> LoadFromDisk()
    {
        try
        {
            if (!File.Exists(StoragePath))
                return [];

            var json = File.ReadAllText(StoragePath);
            var strings = JsonSerializer.Deserialize<List<string>>(json);
            if (strings is null)
                return [];

            return [.. strings
                .Where(s => Uri.TryCreate(s, UriKind.Absolute, out _))
                .Select(s => Normalize(new Uri(s)))
                .Distinct()];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Corrupt or unreadable file — start fresh
            return [];
        }
    }

    private static void SaveToDisk(List<Uri> hosts)
    {
        try
        {
            var dir = Path.GetDirectoryName(StoragePath)!;
            Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(hosts.Select(u => u.ToString()).ToList());
            File.WriteAllText(StoragePath, json);
        }
        catch (IOException)
        {
            // Best-effort persistence; ignore write failures
        }
    }
}
