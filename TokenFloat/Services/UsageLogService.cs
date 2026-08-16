using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TokenFloat.Models;

namespace TokenFloat.Services;

public sealed class UsageLogService
{
    private const int CacheVersion = 7;
    private const int ConsumeLogType = 2;
    private const string ProviderName = "NewAPI";
    private const decimal DefaultQuotaPerUnit = 500_000m;
    private static readonly TimeSpan IncrementalOverlap = TimeSpan.FromHours(2);
    private static readonly TimeSpan MaxIncrementalCacheAge = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly AppSettingsService _settingsService;
    private readonly HttpClient _httpClient;
    private readonly string _cachePath;
    private PersistentCache? _cache;

    public UsageLogService(
        AppSettingsService? settingsService = null,
        HttpClient? httpClient = null,
        string? cacheFolder = null)
    {
        _settingsService = settingsService ?? new AppSettingsService();
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _cachePath = Path.Combine(
            cacheFolder ?? Path.Combine(
                ResolveFolder("LOCALAPPDATA", Environment.SpecialFolder.LocalApplicationData),
                "TokenFloat"),
            $"usage-index-v{CacheVersion}.json.gz");
        LoadPersistentCache();
    }

    /// <summary>
    /// 返回上次从 NewAPI 拉取并压缩保存的事件，按当前日期边界重新聚合，避免跨天后沿用旧口径。
    /// </summary>
    public UsageSnapshot? GetCachedSnapshot()
    {
        var settings = _settingsService.Settings;
        if (_cache is null || _cache.SourceKey != BuildSourceKey(settings))
        {
            return null;
        }

        return BuildSnapshot(_cache.Events, _cache.SourceMessage, _cache.QuotaPerUnit);
    }

    /// <summary>
    /// 从 NewAPI 读取个人消费汇总，并生成当前用量快照。
    /// </summary>
    public Task<UsageSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken = default) =>
        LoadSnapshotAsync(null, cancellationToken);

    /// <summary>
    /// 自定义范围需要更早数据时，向 NewAPI 请求从指定日期开始的小时级汇总。
    /// </summary>
    public Task<UsageSnapshot> LoadSnapshotAsync(
        DateTime requestedHistoryStart,
        CancellationToken cancellationToken = default) =>
        LoadSnapshotAsync(requestedHistoryStart.Date, cancellationToken);

    /// <summary>
    /// 只请求 NewAPI 状态接口验证地址和认证信息，并返回结果与请求耗时。
    /// </summary>
    public Task<NewApiConnectionResult> TestConnectionAsync(CancellationToken cancellationToken = default) =>
        TestConnectionAsync(_settingsService.Settings, cancellationToken);

    /// <summary>
    /// 使用指定配置测试 NewAPI，便于设置页在保存前验证输入内容。
    /// </summary>
    public async Task<NewApiConnectionResult> TestConnectionAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!settings.IsNewApiConfigured)
        {
            return new NewApiConnectionResult(false, "请先填写 NewAPI 地址和系统 Token", stopwatch.Elapsed);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(settings, "api/status"));
            ApplyAuth(request, settings);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new NewApiConnectionResult(
                    false,
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim(),
                    stopwatch.Elapsed);
            }

            return new NewApiConnectionResult(true, "连接成功", stopwatch.Elapsed);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException or FormatException)
        {
            var message = exception is TaskCanceledException
                ? "请求超时"
                : $"{exception.Message}";
            return new NewApiConnectionResult(false, message, stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// 清空本地压缩缓存；下一次刷新会重新从 NewAPI 拉取汇总。
    /// </summary>
    public void ClearCache()
    {
        _cache = null;
        var folder = Path.GetDirectoryName(_cachePath)!;
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(folder, "usage-index-v*.json.gz"))
        {
            File.Delete(path);
        }
    }

    private async Task<UsageSnapshot> LoadSnapshotAsync(
        DateTime? requestedHistoryStart,
        CancellationToken cancellationToken)
    {
        var settings = _settingsService.Settings;
        if (!settings.IsNewApiConfigured)
        {
            return BuildSnapshot([], "请在设置中填写 NewAPI 地址和系统 Token", DefaultQuotaPerUnit);
        }

        try
        {
            var now = DateTime.Now;
            var monthStart = new DateTime(now.Year, now.Month, 1);
            var defaultHistoryStart = monthStart.AddMonths(-1);
            var useIncremental = requestedHistoryStart is null && CanUseIncrementalCache(settings);
            var historyStart = useIncremental
                ? GetIncrementalHistoryStart(now, defaultHistoryStart)
                : requestedHistoryStart is not null && requestedHistoryStart.Value < defaultHistoryStart
                    ? requestedHistoryStart.Value
                    : defaultHistoryStart;
            var quotaPerUnit = await FetchQuotaPerUnitAsync(settings, cancellationToken);
            var rows = await FetchQuotaDataAsync(settings, historyStart, now, cancellationToken);
            var fetchedEvents = rows.Select(ToEvent).ToArray();
            var events = useIncremental && _cache is not null
                ? MergeEvents(_cache.Events, fetchedEvents)
                : fetchedEvents;
            var message = $"NewAPI · {NormalizeBaseUrl(settings.NewApiBaseUrl)} · {now:HH:mm:ss}";
            var snapshot = BuildSnapshot(events, message, quotaPerUnit);
            SavePersistentCache(settings, snapshot.Events, message, quotaPerUnit, DateTimeOffset.UtcNow);
            return snapshot;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or UriFormatException or FormatException)
        {
            var message = $"读取 NewAPI 失败：{exception.Message}";
            if (GetCachedSnapshot() is { } cached)
            {
                return cached with { SourceMessage = message };
            }

            return BuildSnapshot([], message, DefaultQuotaPerUnit);
        }
    }

    private bool CanUseIncrementalCache(AppSettings settings)
    {
        if (_cache is null || _cache.SourceKey != BuildSourceKey(settings) || _cache.LastFetchedUtc is null)
        {
            return false;
        }

        var age = DateTimeOffset.UtcNow - _cache.LastFetchedUtc.Value;
        return age >= TimeSpan.Zero && age <= MaxIncrementalCacheAge;
    }

    private DateTime GetIncrementalHistoryStart(DateTime now, DateTime defaultHistoryStart)
    {
        var lastFetched = _cache!.LastFetchedUtc!.Value.ToLocalTime().DateTime;
        var overlapStart = lastFetched - IncrementalOverlap;
        return overlapStart < defaultHistoryStart ? defaultHistoryStart : overlapStart > now ? now : overlapStart;
    }

    private static IReadOnlyList<TokenUsageEvent> MergeEvents(
        IEnumerable<TokenUsageEvent> existing,
        IEnumerable<TokenUsageEvent> incoming) =>
        existing
            .Concat(incoming)
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.MaxBy(item => item.InputTokens + item.OutputTokens + item.Quota + item.RequestCount)!)
            .OrderBy(item => item.Timestamp)
            .ToArray();

    internal static UsageSnapshot BuildSnapshot(
        IEnumerable<TokenUsageEvent> source,
        string? sourceMessage = null,
        decimal quotaPerUnit = DefaultQuotaPerUnit)
    {
        var uniqueEvents = source
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.MaxBy(item => item.InputTokens + item.OutputTokens + item.Quota + item.RequestCount)!)
            .ToArray();

        var now = DateTime.Now;
        var todayStart = now.Date;
        var weekStart = todayStart.AddDays(-(((int)todayStart.DayOfWeek + 6) % 7));
        var monthStart = new DateTime(now.Year, now.Month, 1);
        var providers = uniqueEvents
            .Select(item => string.IsNullOrWhiteSpace(item.Provider) ? ProviderName : item.Provider)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(provider => BuildProviderUsage(
                provider,
                uniqueEvents.Where(item => item.Provider == provider),
                todayStart,
                weekStart,
                monthStart))
            .ToArray();

        return new UsageSnapshot(DateTime.Now, providers, uniqueEvents, sourceMessage, quotaPerUnit);
    }

    private async Task<decimal> FetchQuotaPerUnitAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(settings, "api/status"));
            ApplyAuth(request, settings);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return DefaultQuotaPerUnit;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("quota_per_unit", out var quotaElement) &&
                quotaElement.ValueKind == JsonValueKind.Number &&
                quotaElement.TryGetDecimal(out var quotaPerUnit) &&
                quotaPerUnit > 0)
            {
                return quotaPerUnit;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
        }

        return DefaultQuotaPerUnit;
    }

    private async Task<IReadOnlyList<NewApiQuotaData>> FetchQuotaDataAsync(
        AppSettings settings,
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken)
    {
        var result = new List<NewApiQuotaData>();
        for (var cursor = start; cursor < end;)
        {
            var chunkEnd = cursor.AddDays(30);
            if (chunkEnd > end)
            {
                chunkEnd = end;
            }

            result.AddRange(await FetchQuotaDataChunkAsync(settings, cursor, chunkEnd, cancellationToken));
            cursor = chunkEnd;
        }

        return CollapseDuplicateRows(result);
    }

    /// <summary>
    /// NewAPI 返回按小时聚合的行；相邻分片边界会重复带回同一行，按创建秒和模型去重并保留数值最大的一条。
    /// </summary>
    private static IReadOnlyList<NewApiQuotaData> CollapseDuplicateRows(List<NewApiQuotaData> rows) =>
        rows
            .GroupBy(row => (row.CreatedAt, row.ModelName))
            .Select(group => group.MaxBy(row => row.TokenUsed + row.Count + row.Quota)!)
            .ToList();

    private async Task<IReadOnlyList<NewApiQuotaData>> FetchQuotaDataChunkAsync(
        AppSettings settings,
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken)
    {
        var startTimestamp = new DateTimeOffset(start).ToUnixTimeSeconds();
        var endTimestamp = new DateTimeOffset(end).ToUnixTimeSeconds();
        var uri = BuildUri(
            settings,
            "api/data/self",
            ("start_timestamp", startTimestamp.ToString(CultureInfo.InvariantCulture)),
            ("end_timestamp", endTimestamp.ToString(CultureInfo.InvariantCulture)));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplyAuth(request, settings);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var envelope = await JsonSerializer.DeserializeAsync<ApiEnvelope<List<NewApiQuotaData>>>(
            stream,
            JsonOptions,
            cancellationToken);
        if (envelope is null)
        {
            throw new InvalidOperationException("NewAPI 返回为空");
        }

        if (!envelope.IsSuccess)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(envelope.Message)
                ? "NewAPI 返回失败"
                : envelope.Message);
        }

        return envelope.Data ?? [];
    }

    private static TokenUsageEvent ToEvent(NewApiQuotaData row)
    {
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, row.CreatedAt)).ToLocalTime();
        var model = string.IsNullOrWhiteSpace(row.ModelName) ? "未标注模型" : row.ModelName;
        return new TokenUsageEvent(
            $"newapi-data:{row.CreatedAt}:{model}",
            ProviderName,
            timestamp,
            Math.Max(0, row.TokenUsed),
            0,
            0,
            model,
            Quota: Math.Max(0, row.Quota),
            RequestCount: Math.Max(0, row.Count));
    }

    private static ProviderUsage BuildProviderUsage(
        string provider,
        IEnumerable<TokenUsageEvent> source,
        DateTime todayStart,
        DateTime weekStart,
        DateTime monthStart)
    {
        var events = source.ToArray();
        return new ProviderUsage(
            provider,
            SumSince(events, todayStart),
            SumSince(events, weekStart),
            SumSince(events, monthStart),
            events.Any(item => item.Timestamp.LocalDateTime >= monthStart));
    }

    private static TokenTotals SumSince(IEnumerable<TokenUsageEvent> source, DateTime start)
    {
        var result = new TokenTotals(0, 0, 0);
        foreach (var item in source.Where(item => item.Timestamp.LocalDateTime >= start))
        {
            result += new TokenTotals(
                item.InputTokens,
                item.OutputTokens,
                item.CachedInputTokens,
                Math.Max(0, item.RequestCount),
                item.Quota);
        }

        return result;
    }

    private static void ApplyAuth(HttpRequestMessage request, AppSettings settings)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.NewApiAccessToken.Trim());
        if (settings.NewApiUserId > 0)
        {
            request.Headers.TryAddWithoutValidation(
                "New-Api-User",
                settings.NewApiUserId.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static Uri BuildUri(AppSettings settings, string path, params (string Name, string Value)[] query)
    {
        var relative = path.TrimStart('/');
        if (query.Length > 0)
        {
            relative += "?" + string.Join(
                "&",
                query.Select(item =>
                    $"{Uri.EscapeDataString(item.Name)}={Uri.EscapeDataString(item.Value)}"));
        }

        return new Uri(new Uri(NormalizeBaseUrl(settings.NewApiBaseUrl)), relative);
    }

    private static string NormalizeBaseUrl(string value) => value.Trim().TrimEnd('/') + "/";

    private static string BuildSourceKey(AppSettings settings)
    {
        if (!settings.IsNewApiConfigured)
        {
            return string.Empty;
        }

        var raw = $"{NormalizeBaseUrl(settings.NewApiBaseUrl)}|{settings.NewApiUserId}|{settings.NewApiAccessToken.Trim()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private void LoadPersistentCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return;
            }

            using var file = File.OpenRead(_cachePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var stored = JsonSerializer.Deserialize<PersistentCache>(gzip, CacheJsonOptions);
            if (stored?.Version == CacheVersion)
            {
                _cache = stored;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            _cache = null;
        }
    }

    private void SavePersistentCache(
        AppSettings settings,
        IReadOnlyList<TokenUsageEvent> events,
        string sourceMessage,
        decimal quotaPerUnit,
        DateTimeOffset fetchedUtc)
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _cachePath + ".tmp";
            _cache = new PersistentCache
            {
                Version = CacheVersion,
                SourceKey = BuildSourceKey(settings),
                SourceMessage = sourceMessage,
                QuotaPerUnit = quotaPerUnit,
                LastFetchedUtc = fetchedUtc,
                Events = events.ToList()
            };

            using (var file = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            {
                JsonSerializer.Serialize(gzip, _cache, CacheJsonOptions);
            }

            File.Move(temporaryPath, _cachePath, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string ResolveFolder(string environmentName, Environment.SpecialFolder fallback)
    {
        var environmentPath = Environment.GetEnvironmentVariable(environmentName);
        return !string.IsNullOrWhiteSpace(environmentPath) && Directory.Exists(environmentPath)
            ? environmentPath
            : Environment.GetFolderPath(fallback);
    }

    private sealed class ApiEnvelope<T>
    {
        public bool Success { get; set; }

        public bool Code { get; set; }

        public string Message { get; set; } = string.Empty;

        public T? Data { get; set; }

        public bool IsSuccess => Success || Code;
    }

    private sealed class NewApiQuotaData
    {
        [JsonPropertyName("model_name")]
        public string ModelName { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public long CreatedAt { get; set; }

        [JsonPropertyName("token_used")]
        public long TokenUsed { get; set; }

        [JsonPropertyName("count")]
        public long Count { get; set; }

        [JsonPropertyName("quota")]
        public long Quota { get; set; }
    }

    private sealed class PersistentCache
    {
        public int Version { get; set; }

        public string SourceKey { get; set; } = string.Empty;

        public string? SourceMessage { get; set; }

        public decimal QuotaPerUnit { get; set; } = DefaultQuotaPerUnit;

        public DateTimeOffset? LastFetchedUtc { get; set; }

        public List<TokenUsageEvent> Events { get; set; } = [];
    }
}

public sealed record NewApiConnectionResult(bool IsSuccess, string Message, TimeSpan Duration);
