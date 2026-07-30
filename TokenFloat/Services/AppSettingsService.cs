using System.IO;
using System.Text.Json;

namespace TokenFloat.Services;

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;
    private AppSettings _settings;

    public AppSettingsService(string? dataFolder = null)
    {
        var folder = dataFolder ?? Path.Combine(
            Environment.GetEnvironmentVariable("LOCALAPPDATA")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TokenFloat");
        _settingsPath = Path.Combine(folder, "app-settings.json");
        _settings = Load();
    }

    public AppSettings Settings => _settings;

    public event Action<AppSettings>? SettingsChanged;

    public void SetRefresh(int intervalSeconds, bool onlyWhenVisible)
    {
        var interval = NormalizeInterval(intervalSeconds);
        _settings = _settings with
        {
            RefreshIntervalSeconds = interval,
            RefreshOnlyWhenVisible = onlyWhenVisible
        };
        Save();
        SettingsChanged?.Invoke(_settings);
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath));
                return settings is null
                    ? new AppSettings()
                    : settings with { RefreshIntervalSeconds = NormalizeInterval(settings.RefreshIntervalSeconds) };
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return new AppSettings();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings, JsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static int NormalizeInterval(int intervalSeconds) =>
        intervalSeconds is 10 or 30 or 60 or 300 ? intervalSeconds : 30;
}

public sealed record AppSettings(
    int RefreshIntervalSeconds = 30,
    bool RefreshOnlyWhenVisible = false);
