using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TapeWinNET.Utils;

/// <summary>
/// Persisted application settings (window layout, last drive, etc.).
/// Serialized as JSON to a <see cref="Stream"/>; the default file lives in
/// <c>%LocalAppData%\TapeWinNET\Settings.json</c>.
/// </summary>
public class AppSettings
{
    private static readonly string DefaultFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TapeWinNET", "Settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    #region Window Layout

    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool IsMaximized { get; set; }

    public double? TreePaneWidth { get; set; }
    public double? LogPaneHeight { get; set; }
    public double? PropertiesPaneHeight { get; set; }

    #endregion

    #region Last Drive

    public int? LastDriveNumber { get; set; }
    public bool LastDriveWasVirtual { get; set; }

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
