using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace TokenFloat.Services;

public sealed class UpdateService
{
    public const string DefaultManifestUrl =
        "https://github.com/Dream-Tian/TokenFloat/releases/latest/download/update-manifest.json";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _folder;
    private UpdateSettings? _settingsCache;
    private long _settingsStamp;

    public UpdateService(string? dataFolder = null)
    {
        _folder = dataFolder ?? Path.Combine(
            Environment.GetEnvironmentVariable("LOCALAPPDATA")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TokenFloat");
    }

    private string SettingsPath => Path.Combine(_folder, "update-settings.json");

    public UpdateSettings Settings => LoadSettings();

    public void SetAutoCheck(bool enabled)
    {
        var settings = LoadSettings() with { AutoCheckEnabled = enabled };
        SaveSettings(settings);
    }

    public void SetManifestUrl(string manifestUrl)
    {
        var settings = LoadSettings() with { ManifestUrl = manifestUrl.Trim() };
        SaveSettings(settings);
    }

    public bool ShouldAutoCheck()
    {
        var settings = LoadSettings();
        return settings.AutoCheckEnabled &&
               !string.IsNullOrWhiteSpace(settings.ManifestUrl) &&
               (settings.LastCheckedUtc is null ||
                DateTimeOffset.UtcNow - settings.LastCheckedUtc.Value >= TimeSpan.FromHours(24));
    }

    /// <summary>
    /// 读取远程清单并比较当前程序集版本，检查结果会记录时间以限制自动检查频率。
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var settings = LoadSettings();
        if (string.IsNullOrWhiteSpace(settings.ManifestUrl))
        {
            return new UpdateCheckResult(UpdateCheckStatus.NotConfigured, null, "尚未设置更新清单地址。");
        }

        var result = await CheckManifestAsync(settings.ManifestUrl, cancellationToken);
        MarkChecked(settings);
        return result;
    }

    public async Task<UpdateCheckResult> CheckManifestAsync(
        string manifestUrl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var source = ResolveSource(manifestUrl, null);
            var json = await ReadTextAsync(source, cancellationToken);
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions);
            if (manifest is null || !Version.TryParse(manifest.Version, out var remoteVersion) ||
                string.IsNullOrWhiteSpace(manifest.InstallerUrl) ||
                string.IsNullOrWhiteSpace(manifest.Sha256))
            {
                return new UpdateCheckResult(UpdateCheckStatus.Error, null, "更新清单格式不完整。");
            }

            var installerSource = ResolveSource(manifest.InstallerUrl, source);
            var sha256 = NormalizeHash(manifest.Sha256);
            if (sha256.Length != 64)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Error, null, "更新清单中的 SHA-256 格式无效。");
            }

            var info = new UpdateInfo(
                remoteVersion,
                installerSource,
                sha256,
                manifest.ReleaseNotes ?? string.Empty);
            return remoteVersion > CurrentVersion
                ? new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, info, $"发现新版本 {remoteVersion}。")
                : new UpdateCheckResult(UpdateCheckStatus.UpToDate, info, $"当前已是最新版本 {CurrentVersion}。");
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or JsonException or UriFormatException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Error, null, $"检查更新失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 下载更新安装包并验证清单中的 SHA-256，校验失败时不会保留可执行文件。
    /// </summary>
    public async Task<string> DownloadInstallerAsync(
        UpdateInfo update,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var updateFolder = Path.Combine(_folder, "updates");
        Directory.CreateDirectory(updateFolder);
        var finalPath = Path.Combine(updateFolder, $"TokenFloat-{update.Version}-Setup.exe");
        var temporaryPath = $"{finalPath}.download";

        try
        {
            await using (var input = await OpenReadAsync(update.InstallerSource, cancellationToken))
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             true))
            {
                var buffer = new byte[81920];
                long copied = 0;
                var stopwatch = Stopwatch.StartNew();
                progress?.Report(new UpdateDownloadProgress(0, input.Length, 0, 0));
                int read;
                while ((read = await input.Stream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    copied += read;
                    var percentage = input.Length is > 0
                        ? (int)Math.Min(100, copied * 100 / input.Length.Value)
                        : 0;
                    var bytesPerSecond = copied / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
                    progress?.Report(new UpdateDownloadProgress(
                        copied,
                        input.Length,
                        bytesPerSecond,
                        percentage));
                }
            }

            string actualHash;
            await using (var verification = File.OpenRead(temporaryPath))
            {
                actualHash = Convert.ToHexString(await SHA256.HashDataAsync(verification, cancellationToken));
            }

            if (!string.Equals(actualHash, update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("安装包 SHA-256 校验失败，文件可能已损坏或被替换。");
            }

            File.Move(temporaryPath, finalPath, true);
            return finalPath;
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    public static void StartInstaller(string installerPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/SP- /SILENT /CLOSEAPPLICATIONS",
            UseShellExecute = true
        });
    }

    private static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    private static async Task<string> ReadTextAsync(UpdateSource source, CancellationToken cancellationToken)
    {
        if (source.LocalPath is not null)
        {
            return await File.ReadAllTextAsync(source.LocalPath, cancellationToken);
        }

        return await HttpClient.GetStringAsync(source.Uri!, cancellationToken);
    }

    private static async Task<UpdateDownloadStream> OpenReadAsync(
        UpdateSource source,
        CancellationToken cancellationToken)
    {
        if (source.LocalPath is not null)
        {
            var stream = new FileStream(source.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            return new UpdateDownloadStream(stream, stream.Length);
        }

        var response = await HttpClient.GetAsync(
            source.Uri!,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return new UpdateDownloadStream(responseStream, response.Content.Headers.ContentLength, response);
    }

    private static UpdateSource ResolveSource(string value, UpdateSource? baseSource)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme == Uri.UriSchemeHttps)
            {
                return new UpdateSource(uri, null);
            }

            if (uri.IsFile)
            {
                return new UpdateSource(null, uri.LocalPath);
            }

            throw new UriFormatException("更新地址只允许 HTTPS 或本地文件。");
        }

        if (baseSource?.Uri is not null)
        {
            return ResolveSource(new Uri(baseSource.Uri, value).AbsoluteUri, null);
        }

        if (baseSource?.LocalPath is not null)
        {
            var folder = Path.GetDirectoryName(baseSource.LocalPath)!;
            return new UpdateSource(null, Path.GetFullPath(Path.Combine(folder, value)));
        }

        return new UpdateSource(null, Path.GetFullPath(value));
    }

    /// <summary>
    /// 读取更新设置；以文件写入时间为戳做内存缓存，避免每次访问都读盘并反序列化。
    /// </summary>
    private UpdateSettings LoadSettings()
    {
        var stamp = File.Exists(SettingsPath) ? File.GetLastWriteTimeUtc(SettingsPath).Ticks : 0;
        if (_settingsCache is not null && stamp == _settingsStamp)
        {
            return _settingsCache;
        }

        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                               ?? new UpdateSettings();
                settings = string.IsNullOrWhiteSpace(settings.ManifestUrl)
                    ? settings with { ManifestUrl = DefaultManifestUrl }
                    : settings;
                _settingsCache = settings;
                _settingsStamp = stamp;
                return settings;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
        }

        return new UpdateSettings();
    }

    private void SaveSettings(UpdateSettings settings)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
            _settingsCache = settings;
            _settingsStamp = File.GetLastWriteTimeUtc(SettingsPath).Ticks;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void MarkChecked(UpdateSettings settings) =>
        SaveSettings(settings with { LastCheckedUtc = DateTimeOffset.UtcNow });

    private static string NormalizeHash(string hash) =>
        new(hash.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());

    private sealed class UpdateDownloadStream : IAsyncDisposable
    {
        private readonly IDisposable? _owner;

        public UpdateDownloadStream(Stream stream, long? length, IDisposable? owner = null)
        {
            Stream = stream;
            Length = length;
            _owner = owner;
        }

        public Stream Stream { get; }

        public long? Length { get; }

        public async ValueTask DisposeAsync()
        {
            await Stream.DisposeAsync();
            _owner?.Dispose();
        }
    }
}

public sealed record UpdateSettings(
    bool AutoCheckEnabled = true,
    string ManifestUrl = UpdateService.DefaultManifestUrl,
    DateTimeOffset? LastCheckedUtc = null);

public sealed record UpdateManifest(
    string Version,
    string InstallerUrl,
    string Sha256,
    string? ReleaseNotes = null);

public sealed record UpdateInfo(
    Version Version,
    UpdateSource InstallerSource,
    string Sha256,
    string ReleaseNotes);

public sealed record UpdateDownloadProgress(
    long BytesDownloaded,
    long? TotalBytes,
    double BytesPerSecond,
    int Percentage);

public sealed record UpdateSource(Uri? Uri, string? LocalPath);

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    UpdateInfo? Update,
    string Message);

public enum UpdateCheckStatus
{
    NotConfigured,
    UpToDate,
    UpdateAvailable,
    Error
}
