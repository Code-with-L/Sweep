using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sweep;

public enum ThemeMode
{
    Dark,
    Light,
}

public sealed class AppSettings
{
    public bool SilentUninstall { get; set; } = true;
    public bool CreateRestorePoint { get; set; } = true;
    public bool UseQuarantine { get; set; } = true;
    public bool ScanRegistry { get; set; } = true;
    public bool UsePublisherTokens { get; set; }
    public int FileScanDepth { get; set; } = 3;
    public int FileScanSeconds { get; set; } = 120;
    public int MaxFileMatches { get; set; } = 2000;
    public int MaxRegistryMatches { get; set; } = 300;
    public int RegistryScanDepth { get; set; } = 3;
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    public bool MinimizeToTray { get; set; }
}

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly object Gate = new();

    public static string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Sweep",
        "settings.json");

    private static AppSettings _current = new();

    public static AppSettings Current => _current;

    public static AppSettings Load()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string json = File.ReadAllText(SettingsPath);
                    _current = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
                    Validate(_current);
                }
            }
            catch (Exception ex)
            {
                LogStore.Warn($"Could not read settings, using defaults ({ex.Message})");
                _current = new AppSettings();
            }

            return _current;
        }
    }

    public static void Save(AppSettings settings)
    {
        lock (Gate)
        {
            Validate(settings);
            _current = settings;
            try
            {
                string? dir = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Options));
            }
            catch (Exception ex)
            {
                LogStore.Warn($"Could not save settings: {ex.Message}");
            }
        }
    }

    public static void ExportTo(string path, AppSettings settings)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
    }

    /// <summary>
    /// Reads settings from a file. Values that fail to deserialise fall back to the
    /// defaults for that field rather than discarding the whole file.
    /// </summary>
    public static AppSettings ImportFrom(string path)
    {
        string json = File.ReadAllText(path);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        Validate(loaded);
        return loaded;
    }

    private static void Validate(AppSettings s)
    {
        s.FileScanDepth = Math.Clamp(s.FileScanDepth, 1, 6);
        s.RegistryScanDepth = Math.Clamp(s.RegistryScanDepth, 1, 5);
        s.FileScanSeconds = Math.Clamp(s.FileScanSeconds, 10, 600);
        s.MaxFileMatches = Math.Clamp(s.MaxFileMatches, 50, 20000);
        s.MaxRegistryMatches = Math.Clamp(s.MaxRegistryMatches, 20, 5000);
    }
}