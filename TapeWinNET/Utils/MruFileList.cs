using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TapeWinNET.Utils;

/// <summary>
/// Manages a most-recently-used (MRU) list of file paths with persistence
/// to a JSON file in the app's local data folder.
/// Thread-safe for concurrent reads and writes.
/// </summary>
public class MruFileList
{
    private readonly string _pathOrKey;
    private readonly AppSettings? _settings;
    private readonly int _maxCount;
    private readonly object _lock = new();
    private readonly List<string> _items = [];

    /// <summary>
    /// Creates an MRU list backed by a JSON file in <c>%LocalAppData%\TapeWinNET\</c>.
    /// </summary>
    /// <param name="fileName">Name of the persistence file (e.g., "VirtualDriveMru.json").</param>
    /// <param name="maxCount">Maximum number of entries to keep.</param>
    public MruFileList(string fileName, int maxCount = 4)
    {
        _maxCount = maxCount;
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TapeWinNET");
        Directory.CreateDirectory(folder);
        _pathOrKey = Path.Combine(folder, fileName);
        // check if there's .json extension, if not add it
        if (!Path.HasExtension(_pathOrKey))
            _pathOrKey += ".json";
        _settings = null;

        Load();
    }

    /// <summary>
    /// Creates an MRU list backed by AppSettings
    /// </summary>
    /// <param name="setting">The AppSettings instance.</param>
    /// <param name="appSettingsKey">The key in AppSettings to store the MRU list.</param>
    /// <param name="maxCount">Maximum number of entries to keep.</param>
    public MruFileList(AppSettings settings, string settingsKey, int maxCount = 4)
    {
        _maxCount = maxCount;
        _settings = settings;
        _pathOrKey = settingsKey;

        Load();
    }

    /// <summary>The current MRU entries, most recent first.</summary>
    public IReadOnlyList<string> Items
    {
        get { lock (_lock) return [.. _items]; }
    }

    /// <summary>
    /// Adds or promotes a path to the top of the MRU list and persists.
    /// </summary>
    public void Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var fullPath = Path.GetFullPath(path);

        lock (_lock)
        {
            _items.RemoveAll(p => string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase));
            _items.Insert(0, fullPath);

            while (_items.Count > _maxCount)
                _items.RemoveAt(_items.Count - 1);
        }

        Save();
    }

    /// <summary>
    /// Removes a path from the MRU list and persists.
    /// </summary>
    public void Remove(string path)
    {
        var fullPath = Path.GetFullPath(path);

        lock (_lock)
            _items.RemoveAll(p => string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase));

        Save();
    }

    /// <summary>
    /// Abbreviates a file path for menu display, keeping the drive/root and file name
    /// visible while compacting middle segments with "...".
    /// Example: <c>D:\Documents.DEV\Projects\TapeNET\Data\media.tape</c>
    /// → <c>D:\Docume...\Data\media.tape</c>
    /// </summary>
    /// <param name="path">Full file path.</param>
    /// <param name="maxLength">Maximum display length (default 64).</param>
    public static string AbbreviatePath(string path, int maxLength = 64)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= maxLength)
            return path;

        const string ellipsis = @"\...";

        var root = Path.GetPathRoot(path) ?? string.Empty;     // e.g. "D:\"
        var fileName = Path.GetFileName(path);                  // e.g. "media.tape"

        // Minimum: root + ellipsis + "\" + fileName
        int minLength = root.Length + ellipsis.Length + 1 + fileName.Length;
        if (minLength >= maxLength)
            return root + ellipsis + @"\" + fileName;

        // Budget for the trailing part (after ellipsis): fill from the right
        var relativePath = path[root.Length..];                  // everything after root
        int budget = maxLength - root.Length - ellipsis.Length;

        // Walk backward through path segments to fit as many trailing segments as possible
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        int startSegment = segments.Length;
        int trailingLength = 0;

        for (int i = segments.Length - 1; i >= 1; i--) // skip segment 0 (we'll always truncate something)
        {
            int segLen = segments[i].Length + 1; // +1 for separator
            if (trailingLength + segLen > budget)
                break;
            trailingLength += segLen;
            startSegment = i;
        }

        var trailing = string.Join('\\', segments[startSegment..]);
        return root + ellipsis + @"\" + trailing;
    }

    private void Load()
    {
        if (_settings != null)
            LoadFromSettings();
        else
            LoadFromFile();
    }

    private void LoadFromFile()
    {
        lock (_lock)
        {
            _items.Clear();

            try
            {
                if (!File.Exists(_pathOrKey))
                    return;

                var json = File.ReadAllText(_pathOrKey);
                var list = JsonSerializer.Deserialize<List<string>>(json);
                if (list != null)
                {
                    foreach (var item in list)
                    {
                        if (!string.IsNullOrWhiteSpace(item) && _items.Count < _maxCount)
                            _items.Add(item);
                    }
                }
            }
            catch
            {
                // Best effort — corrupted file is silently ignored
            }
        }
    }

    private void LoadFromSettings()
    {
        lock (_lock)
        {
            _items.Clear();
            var items = _settings?.GetStringList(_pathOrKey);
            if (items != null)
                _items.AddRange(items);
        }
    }

    private void Save()
    {
        if (_settings != null)
            SaveToSettings();
        else
            SaveToFile();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    
    private void SaveToFile()
    {
        lock (_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(_items, JsonOptions);
                File.WriteAllText(_pathOrKey, json);
            }
            catch
            {
                // Best effort — e.g. folder permissions
            }
        }
    }

    private void SaveToSettings()
    {
        lock (_lock)
        {
            _settings?.SetStringList(_pathOrKey, _items);
            // _settings?.SaveToFile(); -- the App will do this on closing
        }
    }
}
