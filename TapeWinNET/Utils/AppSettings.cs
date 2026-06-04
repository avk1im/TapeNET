using AiNET;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.TextFormatting;

namespace TapeWinNET.Utils;

/// <summary>
/// Persisted application settings (window layout, last drive, etc.).
/// Serialized as JSON to a <see cref="Stream"/>; the default file lives in
/// <c>%LocalAppData%\TapeWinNET\Settings.json</c>.
/// </summary>
public class AppSettings
{
    #region Window Placements

    public WindowPlacement? MainWindowPlacement { get; set; }
    public Dictionary<string, WindowPlacement> DialogPlacements { get; set; } = [];

    public void ResetWindowPlacements(bool mainWindowToo)
    {
        var placementManager = new WindowPlacementManager(this);
        placementManager.ResetAll(mainWindowToo);
    }

    #endregion // Window Placements

    #region Main Window Layout

    public double? TreePaneWidth { get; set; }
    public double? LogPaneHeight { get; set; }
    public double? PropertiesPaneHeight { get; set; }

    public void ResetMainWindowLayout()
    {
        TreePaneWidth = null;
        LogPaneHeight = null;
        PropertiesPaneHeight = null;

        LogFilterPaneWidth = null;
    }

    #endregion // Main Window Layout

    #region Last Drive

    public int? LastDriveNumber { get; set; }
    public bool LastDriveWasVirtual { get; set; }

    #endregion

    #region Generic String Lists (e.g. MRU lists)

    private Dictionary<string, List<string>> _stringLists = [];

    [JsonInclude]
    public Dictionary<string, List<string>> StringLists
    {
        get => _stringLists;
        private set => _stringLists = value; // for deserialization only
    }

    public IReadOnlyList<string> GetStringList(string key)
    {
        if (!_stringLists.TryGetValue(key, out var list))
        {
            list = [];
            _stringLists[key] = list;
        }
        return list;
    }

    public void SetStringList(string key, IEnumerable<string> items)
    {
        var list = items.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _stringLists[key] = list;
    }

    public void AddString(string key, string item)
    {
        if (string.IsNullOrWhiteSpace(item))
            return;
        if (!_stringLists.TryGetValue(key, out var list))
        {
            list = [];
            _stringLists[key] = list;
        }
        list.RemoveAll(p => string.Equals(p, item, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, item);
    }

    #endregion

    #region Log Pane

    public bool ShowTimestamps { get; set; } = true;

    // Severity filter checkboxes (all visible by default)
    public bool ShowLogInfo { get; set; } = true;
    public bool ShowLogCompleted { get; set; } = true;
    public bool ShowLogWarning { get; set; } = true;
    public bool ShowLogError { get; set; } = true;
    public bool ShowLogDetails { get; set; } = true;

    public double? LogFilterPaneWidth { get; set; }

    #endregion

    #region View Options

    /// <summary>Whether the media usage bar is shown below the Media Properties list.</summary>
    public bool ShowUsageBar { get; set; } = true;

    #endregion

    #region Remote Host

    /// <summary>Hostname or IP address from the last successful remote connection.</summary>
    public string? LastRemoteHost { get; set; }

    /// <summary>Port from the last successful remote connection.</summary>
    public int? LastRemotePort { get; set; }

    /// <summary>Whether TLS was used in the last successful remote connection.</summary>
    public bool LastRemoteUseTls { get; set; }

    /// <summary>Whether "Use local host" was checked in the last connect dialog.</summary>
    public bool LastRemoteUseLocalHost { get; set; }

    #endregion

    #region Help Pane

    /// <summary>
    /// Width (pixels) of the HelpPane column, keyed by host window name (e.g. "MainWindow").
    /// Enables each host to remember its own preferred pane width independently.
    /// </summary>
    [JsonPropertyName("helpPaneWidthPerHost")]
    public Dictionary<string, double>? HelpPaneWidthPerHost { get; set; }

    /// <summary>
    /// Height (pixels) of the chat sub-pane inside the HelpPane.
    /// Shared across all host windows (one splitter position for the whole app).
    /// </summary>
    [JsonPropertyName("helpPaneChatHeight")]
    public double? HelpPaneChatHeight { get; set; }

    /// <summary>
    /// Last-open topic id per host window name, keyed by host name (e.g. "MainWindow").
    /// Enables restoring the last-viewed topic when the pane is reopened.
    /// </summary>
    [JsonPropertyName("helpPaneLastTopicPerHost")]
    public Dictionary<string, string>? HelpPaneLastTopicPerHost { get; set; }

    public void ResetHelpPaneLayout()
    {
        HelpPaneWidthPerHost = null;
        HelpPaneChatHeight = null;
    }

    #endregion

    #region AI

    public AiProviderPreferences? AIProviderPrefs { get; set; }

    #endregion

    #region Stream-level API

    public void Save(Stream stream)
    {
        JsonSerializer.Serialize(stream, this, JsonOptions);
        stream.Flush();
    }

    public static AppSettings Load(Stream stream)
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions) ?? new();
        }
        catch
        {
            return new();
        }
    }

    #endregion

    #region File-level convenience API

    private static readonly string DefaultFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TapeWinNET", "Settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new WindowPlacementJsonConverter() },
    };
    
    public void SaveToFile(string? path = null)
    {
        path ??= DefaultFilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            Save(stream);
        }
        catch
        {
            // Best effort
        }
    }

    public static AppSettings LoadFromFile(string? path = null)
    {
        path ??= DefaultFilePath;
        try
        {
            if (!File.Exists(path))
                return new();

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Load(stream);
        }
        catch
        {
            return new();
        }
    }

    #endregion
}
