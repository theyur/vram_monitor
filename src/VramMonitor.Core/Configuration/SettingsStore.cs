using System.Text.Json;

namespace VramMonitor.Core.Configuration;

/// <summary>Result of loading settings, including why defaults were used if they were.</summary>
public sealed record SettingsLoadResult(MonitorSettings Settings, bool UsedDefaults, string? Problem);

/// <summary>
/// Reads and writes the settings file. RAM-only monitoring history is never touched by this -- only
/// configuration is persisted (spec section 3.1).
/// </summary>
public sealed class SettingsStore(string filePath)
{
    public string FilePath { get; } = filePath;

    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VramMonitor",
        "settings.json");

    public static SettingsStore Default() => new(DefaultFilePath);

    /// <summary>
    /// Loads settings, falling back to defaults if the file is absent or unreadable.
    /// </summary>
    /// <remarks>
    /// A corrupt file must not stop monitoring or raise a dialog: the problem is reported through monitor
    /// health instead (spec section 16). The bad file is left untouched so the user can inspect it.
    /// </remarks>
    public SettingsLoadResult Load()
    {
        if (!File.Exists(FilePath)) return new SettingsLoadResult(new MonitorSettings(), true, null);

        try
        {
            string json = File.ReadAllText(FilePath);
            SettingsDocument? document = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.SettingsDocument);
            return document is null
                ? new SettingsLoadResult(new MonitorSettings(), true, "Settings file was empty.")
                : new SettingsLoadResult(document.ToSettings(), false, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new SettingsLoadResult(new MonitorSettings(), true, $"Settings could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes settings atomically, so an interrupted write can never leave a half-written file behind.
    /// </summary>
    public void Save(MonitorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string? directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string json = JsonSerializer.Serialize(
            SettingsDocument.From(settings), SettingsJsonContext.Default.SettingsDocument);

        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, FilePath, overwrite: true);
    }
}
