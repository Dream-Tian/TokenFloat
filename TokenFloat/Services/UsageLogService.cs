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
    private const int AntigravityCacheVersion = 1;
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
    private readonly AntigravityQuotaService _antigravityQuotaService;
    private readonly AntigravityUsageService _antigravityUsageService;
    private readonly string _cachePath;
    private readonly string _antigravityCachePath;
    private PersistentCache? _cache;
    private AntigravityPersistentCache? _antigravityCache;

    public UsageLogService(
        AppSettingsService? settingsService = null,
        HttpClient? httpClient = null,
        string? cacheFolder = null,
        AntigravityQuotaService? antigravityQuotaService = null,
        AntigravityUsageService? antigravityUsageService = null)
    {
        _settingsService = settingsService ?? new AppSettingsService();
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _antigravityQuotaService = antigravityQuotaService ?? new AntigravityQuotaService();
        _antigravityUsageService = antigravityUsageService ?? new AntigravityUsageService(_antigravityQuotaService.Client);
        _cachePath = Path.Combine(
            cacheFolder ?? Path.Combine(
                ResolveFolder("LOCALAPPDATA", Environment.SpecialFolder.LocalApplicationData),
                "TokenFloat"),
            $"usage-index-v{CacheVersion}.json.gz");
        _antigravityCachePath = Path.Combine(Path.GetDirectoryName(_cachePath)!, $"antigravity-usage-v{AntigravityCacheVersion}.json.gz");
        LoadPersistentCache();
        LoadAntigravityCache();
    }

    /// <summary>
    /// 按启用来源读取各自缓存并重新聚合日期边界，Antigravity 旧记录明确标为本机缓存。
    /// </summary>
    public UsageSnapshot? GetCachedSnapshot()
    {
        var settings = _settingsService.Settings;
        var newApiCache = IsCurrentCache(settings) ? _cache : null;
        var antigravityCache = settings.AntigravityUsageEnabled ? _antigravityCache : null;
        if (newApiCache is null && antigravityCache is null)
        {
            return null;
        }

        var antigravityMessage = antigravityCache is null ? null : FormatAntigravityCacheMessage(antigravityCache);
        var messages = new[] { newApiCache?.SourceMessage, antigravityMessage }
            .Where(message => !string.IsNullOrWhiteSpace(message));
        return BuildSnapshot(
            (newApiCache?.Events ?? []).Concat(antigravityCache?.Events ?? []),
            string.Join(" | ", messages),
            newApiCache?.QuotaPerUnit ?? DefaultQuotaPerUnit,
            antigravityUsageMessage: antigravityMessage,
            antigravityUsageIsComplete: antigravityCache is null ? null : false);
    }

    /// <summary>
    /// 从已启用的 NewAPI 和 Antigravity 来源读取记录，并生成当前用量快照。
    /// </summary>
    public Task<UsageSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken = default) =>
        LoadSnapshotCoreAsync(null, cancellationToken);

    /// <summary>
    /// 自定义范围需要更早数据时，向 NewAPI 请求从指定日期开始的小时级汇总。
    /// </summary>
    public Task<UsageSnapshot> LoadSnapshotAsync(
        DateTime requestedHistoryStart,
        CancellationToken cancellationToken = default) =>
        LoadSnapshotCoreAsync(requestedHistoryStart.Date, cancellationToken);

    /// <summary>
    /// 只请求 NewAPI 状态接口验证地址和认证信息，并返回结果与请求耗时。
    /// </summary>
    public Task<NewApiConnectionResult> TestConnectionAsync(CancellationToken cancellationToken = default) =>
        TestConnectionAsync(_settingsService.Settings, cancellationToken);

    /// <summary>
    /// 直接测试 Antigravity 本地配额接口，供设置页诊断连接而不改变来源开关。
    /// </summary>
    public Task<AntigravityQuotaReadResult> TestAntigravityConnectionAsync(CancellationToken cancellationToken = default) =>
        _antigravityQuotaService.ReadAsync(cancellationToken);

    /// <summary>
    /// 使用指定配置测试 NewAPI，便于设置页在保存前验证输入内容。
    /// </summary>
    public async Task<NewApiConnectionResult> TestConnectionAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
            if (!response.IsSuccessStatusCode)
            {
                return new NewApiConnectionResult(
                    false,
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim(),
                    stopwatch.Elapsed);
            }

            return new NewApiConnectionResult(true, "连接成功", stopwatch.Elapsed);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
            exception is HttpRequestException or TaskCanceledException or UriFormatException or FormatException)
        {
            var message = exception is TaskCanceledException
                ? "请求超时"
                : $"{exception.Message}";
            return new NewApiConnectionResult(false, message, stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// 清空本地压缩缓存；下一次刷新会重新读取已启用的数据来源。
    /// </summary>
    public void ClearCache()
    {
        _cache = null;
        _antigravityCache = null;
        _antigravityUsageService.ClearCache();
        var folder = Path.GetDirectoryName(_cachePath)!;
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(folder, "usage-index-v*.json.gz"))
        {
            File.Delete(path);
        }

        if (File.Exists(_antigravityCachePath))
        {
            File.Delete(_antigravityCachePath);
        }
    }

    /// <summary>
    /// 分别读取消费和配额，只在 NewAPI 完整成功后推进历史缓存，失败时保留记录与费率。
    /// </summary>
    private async Task<UsageSnapshot> LoadSnapshotCoreAsync(
        DateTime? requestedHistoryStart,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = _settingsService.Settings;
        if (!settings.HasUsageSource)
        {
            return BuildSnapshot([], "请在设置中配置至少一个用量来源", DefaultQuotaPerUnit);
        }

        var now = DateTime.Now;
        var monthStart = new DateTime(now.Year, now.Month, 1);
        var defaultHistoryStart = monthStart.AddMonths(-1);
        var useIncremental = settings.IsNewApiConfigured && requestedHistoryStart is null && CanUseIncrementalCache(settings);
        var historyStart = useIncremental
            ? GetIncrementalHistoryStart(now, defaultHistoryStart)
            : requestedHistoryStart is not null && requestedHistoryStart.Value < defaultHistoryStart
                ? requestedHistoryStart.Value
                : defaultHistoryStart;
        var currentCache = IsCurrentCache(settings) ? _cache : null;
        var cachedEvents = currentCache?.Events ?? [];
        var events = new List<TokenUsageEvent>();
        var messages = new List<string>();
        var quotaPerUnit = currentCache?.QuotaPerUnit ?? DefaultQuotaPerUnit;
        AntigravityQuotaSnapshot? antigravityQuota = null;
        string? antigravityStatusMessage = null;
        string? antigravityUsageMessage = null;
        bool? antigravityUsageIsComplete = null;
        Exception? newApiError = null;

        if (settings.IsNewApiConfigured)
        {
            try
            {
                var fetchedQuotaPerUnit = await FetchQuotaPerUnitAsync(settings, quotaPerUnit, cancellationToken);
                var rows = await FetchQuotaDataAsync(settings, historyStart, now, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var fetchedEvents = rows.Select(ToEvent).ToArray();
                events.AddRange(useIncremental
                    ? MergeEvents(cachedEvents.Where(item => item.Provider == ProviderName), fetchedEvents)
                    : fetchedEvents);
                quotaPerUnit = fetchedQuotaPerUnit;
                var sourceMessage = $"NewAPI · {NormalizeBaseUrl(settings.NewApiBaseUrl)} · {now:HH:mm:ss}";
                messages.Add(sourceMessage);
                SavePersistentCache(settings, events, sourceMessage, quotaPerUnit, DateTimeOffset.UtcNow);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or UriFormatException or FormatException)
            {
                newApiError = exception;
                events.AddRange(cachedEvents.Where(item => item.Provider == ProviderName));
            }
        }

        if (settings.AntigravityUsageEnabled)
        {
            var usageResult = await _antigravityUsageService.ReadAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (usageResult.IsComplete)
            {
                SaveAntigravityCache(usageResult.Events, DateTimeOffset.UtcNow);
            }
            else if (usageResult.IsUnavailable && _antigravityCache is not null)
            {
                usageResult = usageResult with
                {
                    Events = _antigravityCache.Events,
                    Message = $"{usageResult.Message}；{FormatAntigravityCacheMessage(_antigravityCache)}"
                };
            }

            events.AddRange(usageResult.Events);
            antigravityUsageMessage = usageResult.Message;
            antigravityUsageIsComplete = usageResult.IsComplete;
            messages.Add($"Antigravity Token：{antigravityUsageMessage}");

            var quotaResult = await _antigravityQuotaService.ReadAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            antigravityQuota = quotaResult.Snapshot;
            antigravityStatusMessage = quotaResult.Message;
            if (antigravityQuota is not null)
            {
                messages.Add($"当前配额 {FormatAntigravityQuota(antigravityQuota)}");
            }
            else
            {
                messages.Add($"配额：{quotaResult.Message}");
            }
        }

        if (newApiError is not null)
        {
            messages.Add($"读取 NewAPI 失败：{newApiError.Message}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return BuildSnapshot(
            events,
            string.Join(" | ", messages),
            quotaPerUnit,
            antigravityQuota,
            antigravityStatusMessage,
            antigravityUsageMessage,
            antigravityUsageIsComplete);
    }

    private bool IsCurrentCache(AppSettings settings) =>
        settings.IsNewApiConfigured && _cache is not null && _cache.SourceKey == BuildSourceKey(settings);

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
        decimal quotaPerUnit = DefaultQuotaPerUnit,
        AntigravityQuotaSnapshot? antigravityQuota = null,
        string? antigravityStatusMessage = null,
        string? antigravityUsageMessage = null,
        bool? antigravityUsageIsComplete = null)
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

        return new UsageSnapshot(
            DateTime.Now,
            providers,
            uniqueEvents,
            sourceMessage,
            quotaPerUnit,
            antigravityQuota,
            antigravityStatusMessage,
            antigravityUsageMessage,
            antigravityUsageIsComplete);
    }

    private static string FormatAntigravityQuota(AntigravityQuotaSnapshot snapshot)
    {
        var models = snapshot.Models
            .Select(item => (item, Remaining: item.RemainingFraction))
            .Where(item => item.Remaining is not null)
            .Take(3)
            .Select(item => $"{item.item.Label ?? item.item.Model} {item.Remaining!.Value:P0}");
        return string.Join("、", models);
    }

    /// <summary>
    /// 从状态接口读取消费换算单位，接口暂不可用时沿用同一账户最近成功的费率。
    /// </summary>
    private async Task<decimal> FetchQuotaPerUnitAsync(
        AppSettings settings,
        decimal fallbackQuotaPerUnit,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(settings, "api/status"));
            ApplyAuth(request, settings);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return fallbackQuotaPerUnit;
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
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
            exception is HttpRequestException or TaskCanceledException or JsonException)
        {
        }

        return fallbackQuotaPerUnit;
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

    /// <summary>
    /// 仅用 NewAPI 地址和认证信息标识消费缓存，Antigravity 开关不影响历史记录。
    /// </summary>
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

    /// <summary>
    /// 只恢复本程序保存的完整 Antigravity 会话快照，供 IDE 离线时明确展示旧记录。
    /// </summary>
    private void LoadAntigravityCache()
    {
        try
        {
            if (!File.Exists(_antigravityCachePath))
            {
                return;
            }

            using var file = File.OpenRead(_antigravityCachePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var stored = JsonSerializer.Deserialize<AntigravityPersistentCache>(gzip, CacheJsonOptions);
            if (stored?.Version == AntigravityCacheVersion)
            {
                _antigravityCache = stored;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            _antigravityCache = null;
        }
    }

    /// <summary>
    /// 完整扫描后替换 Antigravity 独立缓存，不把来源身份未知的两次会话集合混合。
    /// </summary>
    private void SaveAntigravityCache(IReadOnlyList<TokenUsageEvent> events, DateTimeOffset fetchedUtc)
    {
        _antigravityCache = new AntigravityPersistentCache
        {
            Version = AntigravityCacheVersion,
            LastFetchedUtc = fetchedUtc,
            Events = MergeEvents([], events).ToList()
        };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_antigravityCachePath)!);
            var temporaryPath = _antigravityCachePath + ".tmp";
            using (var file = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            {
                JsonSerializer.Serialize(gzip, _antigravityCache, CacheJsonOptions);
            }

            File.Move(temporaryPath, _antigravityCachePath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string FormatAntigravityCacheMessage(AntigravityPersistentCache cache) =>
        $"显示本机历史缓存（最近完整读取：{cache.LastFetchedUtc.LocalDateTime:yyyy-MM-dd HH:mm}）";

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

    private sealed class AntigravityPersistentCache
    {
        public int Version { get; set; }

        public DateTimeOffset LastFetchedUtc { get; set; }

        public List<TokenUsageEvent> Events { get; set; } = [];
    }
}

public sealed record NewApiConnectionResult(bool IsSuccess, string Message, TimeSpan Duration);
