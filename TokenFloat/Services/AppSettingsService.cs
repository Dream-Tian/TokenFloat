using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenFloat.Services;

public sealed class AppSettingsService
{
    private const string ProtectedTokenPrefix = "dpapi:v1:";
    private static readonly byte[] TokenEntropy = Encoding.UTF8.GetBytes("TokenFloat.NewApiAccessToken.v1");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly string _settingsPath;
    private AppSettings _settings;

    public AppSettingsService(string? dataFolder = null)
    {
        var folder = dataFolder ?? Path.Combine(
            Environment.GetEnvironmentVariable("LOCALAPPDATA")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TokenFloat");
        _settingsPath = Path.Combine(folder, "app-settings.json");
        var loaded = Load();
        _settings = loaded.Settings;
        if (loaded.MigrateLegacyToken)
        {
            Save();
        }
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

    public void SetNewApi(string baseUrl, string accessToken, int userId)
    {
        _settings = _settings with
        {
            NewApiBaseUrl = baseUrl.Trim(),
            NewApiAccessToken = accessToken.Trim(),
            NewApiUserId = Math.Max(0, userId)
        };
        Save();
        SettingsChanged?.Invoke(_settings);
    }

    /// <summary>
    /// 读取设置并解密 Token；发现旧版明文字段时标记为需要立即迁移。
    /// </summary>
    private (AppSettings Settings, bool MigrateLegacyToken) Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var stored = JsonSerializer.Deserialize<StoredAppSettings>(File.ReadAllText(_settingsPath));
                if (stored is not null)
                {
                    var protectedToken = UnprotectToken(stored.NewApiAccessTokenProtected);
                    var legacyToken = stored.NewApiAccessToken?.Trim() ?? string.Empty;
                    var migrateLegacyToken = protectedToken is null && !string.IsNullOrWhiteSpace(legacyToken);
                    var settings = new AppSettings(
                        NormalizeInterval(stored.RefreshIntervalSeconds),
                        stored.RefreshOnlyWhenVisible,
                        stored.NewApiBaseUrl,
                        protectedToken ?? legacyToken,
                        Math.Max(0, stored.NewApiUserId));
                    return (settings, migrateLegacyToken);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return (new AppSettings(), false);
    }

    /// <summary>
    /// 使用当前 Windows 用户范围的 DPAPI 加密 Token 后再写入配置文件。
    /// </summary>
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var stored = new StoredAppSettings
            {
                RefreshIntervalSeconds = _settings.RefreshIntervalSeconds,
                RefreshOnlyWhenVisible = _settings.RefreshOnlyWhenVisible,
                NewApiBaseUrl = _settings.NewApiBaseUrl,
                NewApiAccessTokenProtected = ProtectToken(_settings.NewApiAccessToken),
                NewApiUserId = _settings.NewApiUserId
            };
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(stored, JsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
        }
    }

    private static string ProtectToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        var plaintext = Encoding.UTF8.GetBytes(token);
        var protectedData = ProtectedData.Protect(plaintext, TokenEntropy, DataProtectionScope.CurrentUser);
        return ProtectedTokenPrefix + Convert.ToBase64String(protectedData);
    }

    private static string? UnprotectToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith(ProtectedTokenPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var protectedData = Convert.FromBase64String(value[ProtectedTokenPrefix.Length..]);
            var plaintext = ProtectedData.Unprotect(protectedData, TokenEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return null;
        }
    }

    private static int NormalizeInterval(int intervalSeconds) =>
        intervalSeconds is 10 or 30 or 60 or 300 ? intervalSeconds : 30;

    private sealed class StoredAppSettings
    {
        public int RefreshIntervalSeconds { get; set; } = 30;

        public bool RefreshOnlyWhenVisible { get; set; }

        public string NewApiBaseUrl { get; set; } = string.Empty;

        public string NewApiAccessTokenProtected { get; set; } = string.Empty;

        public int NewApiUserId { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? NewApiAccessToken { get; set; }
    }
}

public sealed record AppSettings(
    int RefreshIntervalSeconds = 30,
    bool RefreshOnlyWhenVisible = false,
    string NewApiBaseUrl = "",
    [property: JsonIgnore] string NewApiAccessToken = "",
    int NewApiUserId = 0)
{
    public bool IsNewApiConfigured =>
        !string.IsNullOrWhiteSpace(NewApiBaseUrl) &&
        !string.IsNullOrWhiteSpace(NewApiAccessToken);
}
