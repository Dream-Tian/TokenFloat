using System.IO;
using System.IO.Compression;
using System.Text.Json;
using TokenFloat.Models;

namespace TokenFloat.Services;

public sealed class UsageLogService
{
    private const int CacheVersion = 5;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Dictionary<string, CachedLogFile> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _userProfile = ResolveFolder(
        "USERPROFILE",
        Environment.SpecialFolder.UserProfile);
    private readonly string _cachePath = Path.Combine(
        ResolveFolder("LOCALAPPDATA", Environment.SpecialFolder.LocalApplicationData),
        "TokenFloat",
        $"usage-index-v{CacheVersion}.json.gz");
    private bool _cacheDirty;

    public UsageLogService()
    {
        LoadPersistentCache();
    }

    /// <summary>
    /// 返回磁盘索引中的上次结果，让窗口启动后可以立即显示已有数据。
    /// </summary>
    public UsageSnapshot? GetCachedSnapshot()
    {
        if (_cache.Count == 0)
        {
            return null;
        }

        return BuildSnapshot(_cache.Values.SelectMany(item => item.Events));
    }

    /// <summary>
    /// 只检查本月日志，复用未变化文件的持久索引，并在后台更新汇总。
    /// </summary>
    public Task<UsageSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => LoadSnapshot(cancellationToken), cancellationToken);

    private UsageSnapshot LoadSnapshot(CancellationToken cancellationToken)
    {
        var monthStart = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
        var currentFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var events = new List<TokenUsageEvent>();

        events.AddRange(ReadFiles(
            Path.Combine(_userProfile, ".codex", "sessions"),
            "*.jsonl",
            ParseCodexFile,
            monthStart,
            currentFiles,
            cancellationToken));
        events.AddRange(ReadFiles(
            Path.Combine(_userProfile, ".claude", "projects"),
            "*.jsonl",
            ParseClaudeFile,
            monthStart,
            currentFiles,
            cancellationToken));
        events.AddRange(ReadGeminiFiles(monthStart, currentFiles, cancellationToken));

        foreach (var stalePath in _cache.Keys.Where(path => !currentFiles.Contains(path)).ToArray())
        {
            _cache.Remove(stalePath);
            _cacheDirty = true;
        }

        SavePersistentCache();
        return BuildSnapshot(events);
    }

    private static UsageSnapshot BuildSnapshot(IEnumerable<TokenUsageEvent> source)
    {
        var uniqueEvents = source
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.MaxBy(item => item.InputTokens + item.OutputTokens)!)
            .ToArray();

        var now = DateTime.Now;
        var todayStart = now.Date;
        var weekStart = todayStart.AddDays(-(((int)todayStart.DayOfWeek + 6) % 7));
        var monthStart = new DateTime(now.Year, now.Month, 1);
        var providers = new[] { "Codex", "Claude", "Gemini" }
            .Select(provider => BuildProviderUsage(
                provider,
                uniqueEvents.Where(item => item.Provider == provider),
                todayStart,
                weekStart,
                monthStart))
            .ToArray();

        return new UsageSnapshot(DateTime.Now, providers, uniqueEvents);
    }

    private IEnumerable<TokenUsageEvent> ReadFiles(
        string root,
        string pattern,
        Func<string, IReadOnlyList<TokenUsageEvent>> parser,
        DateTime monthStart,
        ISet<string> currentFiles,
        CancellationToken cancellationToken)
    {
        foreach (var file in EnumerateFiles(root, pattern, monthStart))
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentFiles.Add(file);
            foreach (var item in ReadCachedFile(file, parser))
            {
                yield return item;
            }
        }
    }

    /// <summary>
    /// Codex 会重复写入相同累计值；只在累计值变化时记录本次用量。
    /// </summary>
    private IReadOnlyList<TokenUsageEvent> ParseCodexFile(string path)
    {
        var result = new List<TokenUsageEvent>();
        using var stream = OpenSharedRead(path);
        using var reader = new StreamReader(stream);
        long? previousCumulativeTokens = null;
        string? currentModel = null;
        var lineNumber = 0;

        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (!TryParseJsonLine(line, out var document))
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (HasString(root, "type", "turn_context") &&
                    TryGet(root, "payload", out var turnContext))
                {
                    currentModel = GetString(turnContext, "model") ?? currentModel;
                }

                if (!TryGetCodexUsage(root, out var usage, out var totalUsage, out var timestamp))
                {
                    continue;
                }

                var cumulativeTokens = GetInt64(totalUsage, "total_tokens");
                if (previousCumulativeTokens.HasValue && cumulativeTokens == previousCumulativeTokens.Value)
                {
                    continue;
                }

                previousCumulativeTokens = cumulativeTokens;
                var input = GetInt64(usage, "input_tokens");
                var output = GetInt64(usage, "output_tokens");
                var total = GetInt64(usage, "total_tokens");
                if (input == 0 && output == 0 && total > 0)
                {
                    input = total;
                }

                result.Add(new TokenUsageEvent(
                    $"codex:{Path.GetFileName(path)}:{lineNumber}",
                    "Codex",
                    timestamp,
                    input,
                    output,
                    GetInt64(usage, "cached_input_tokens"),
                    currentModel));
            }
        }

        return result;
    }

    private static bool TryGetCodexUsage(
        JsonElement root,
        out JsonElement usage,
        out JsonElement totalUsage,
        out DateTimeOffset timestamp)
    {
        if (HasString(root, "type", "event_msg") &&
            TryGet(root, "payload", out var payload) &&
            HasString(payload, "type", "token_count") &&
            TryGet(payload, "info", out var info) &&
            TryGet(info, "last_token_usage", out usage) &&
            TryGet(info, "total_token_usage", out totalUsage) &&
            TryReadTimestamp(root, out timestamp))
        {
            return true;
        }

        usage = default;
        totalUsage = default;
        timestamp = default;
        return false;
    }

    private IReadOnlyList<TokenUsageEvent> ParseClaudeFile(string path)
    {
        var candidates = ParseJsonLines(path, ParseClaudeLine);
        return candidates
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.MaxBy(item => item.InputTokens + item.OutputTokens)!)
            .ToArray();
    }

    private static TokenUsageEvent? ParseClaudeLine(JsonElement root, string path, int lineNumber)
    {
        if (!HasString(root, "type", "assistant") ||
            !TryGet(root, "message", out var message) ||
            !TryGet(message, "usage", out var usage) ||
            !TryReadTimestamp(root, out var timestamp))
        {
            return null;
        }

        var model = GetString(message, "model");
        if (string.Equals(model, "<synthetic>", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var messageId = GetString(message, "id");
        var id = string.IsNullOrWhiteSpace(messageId)
            ? $"claude:{Path.GetFileName(path)}:{lineNumber}"
            : $"claude:{messageId}";

        var cacheWrite = GetInt64(usage, "cache_creation_input_tokens");
        var cacheWriteFiveMinutes = cacheWrite;
        var cacheWriteOneHour = 0L;
        if (TryGet(usage, "cache_creation", out var cacheCreation))
        {
            var detailedFiveMinutes = GetInt64(cacheCreation, "ephemeral_5m_input_tokens");
            var detailedOneHour = GetInt64(cacheCreation, "ephemeral_1h_input_tokens");
            if (detailedFiveMinutes > 0 || detailedOneHour > 0)
            {
                cacheWriteFiveMinutes = detailedFiveMinutes;
                cacheWriteOneHour = detailedOneHour;
            }
        }

        return new TokenUsageEvent(
            id,
            "Claude",
            timestamp,
            GetInt64(usage, "input_tokens"),
            GetInt64(usage, "output_tokens"),
            GetInt64(usage, "cache_read_input_tokens"),
            model,
            cacheWriteFiveMinutes,
            cacheWriteOneHour);
    }

    private IReadOnlyList<TokenUsageEvent> ParseJsonLines(
        string path,
        Func<JsonElement, string, int, TokenUsageEvent?> parser)
    {
        var result = new List<TokenUsageEvent>();
        using var stream = OpenSharedRead(path);
        using var reader = new StreamReader(stream);
        var lineNumber = 0;

        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (!TryParseJsonLine(line, out var document))
            {
                continue;
            }

            using (document)
            {
                var item = parser(document.RootElement, path, lineNumber);
                if (item is not null)
                {
                    result.Add(item);
                }
            }
        }

        return result;
    }

    private static bool TryParseJsonLine(string line, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(line, JsonOptions);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    private IEnumerable<TokenUsageEvent> ReadGeminiFiles(
        DateTime monthStart,
        ISet<string> currentFiles,
        CancellationToken cancellationToken)
    {
        var roots = new[]
        {
            Path.Combine(_userProfile, ".gemini", "tmp"),
            Path.Combine(_userProfile, ".gemini", "history")
        };

        foreach (var root in roots)
        {
            foreach (var pattern in new[] { "*.json", "*.jsonl" })
            {
                foreach (var item in ReadFiles(
                             root,
                             pattern,
                             ParseGeminiFile,
                             monthStart,
                             currentFiles,
                             cancellationToken))
                {
                    yield return item;
                }
            }
        }
    }

    private IReadOnlyList<TokenUsageEvent> ParseGeminiFile(string path)
    {
        var result = new List<TokenUsageEvent>();
        var fallbackTimestamp = new DateTimeOffset(File.GetLastWriteTime(path));
        using var stream = OpenSharedRead(path);

        try
        {
            using var document = JsonDocument.Parse(stream, JsonOptions);
            CollectGeminiUsage(document.RootElement, path, fallbackTimestamp, result);
        }
        catch (JsonException)
        {
            foreach (var item in ParseJsonLines(path, (root, file, line) =>
                         ParseGeminiRoot(root, file, line, fallbackTimestamp)))
            {
                result.Add(item);
            }
        }

        return result;
    }

    private TokenUsageEvent? ParseGeminiRoot(
        JsonElement root,
        string path,
        int lineNumber,
        DateTimeOffset fallbackTimestamp)
    {
        var items = new List<TokenUsageEvent>();
        CollectGeminiUsage(root, $"{path}:{lineNumber}", fallbackTimestamp, items);
        return items.FirstOrDefault();
    }

    private void CollectGeminiUsage(
        JsonElement element,
        string sourceId,
        DateTimeOffset inheritedTimestamp,
        ICollection<TokenUsageEvent> result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var timestamp = TryReadTimestamp(element, out var ownTimestamp)
                ? ownTimestamp
                : inheritedTimestamp;

            if (TryGet(element, "usageMetadata", out var usage))
            {
                var input = FirstInt64(usage, "promptTokenCount", "inputTokenCount", "prompt_tokens");
                var output = FirstInt64(usage, "candidatesTokenCount", "outputTokenCount", "completion_tokens");
                var total = GetInt64(usage, "totalTokenCount");
                if (input == 0 && output == 0 && total > 0)
                {
                    input = total;
                }

                if (input > 0 || output > 0)
                {
                    result.Add(new TokenUsageEvent(
                        $"gemini:{sourceId}:{result.Count}",
                        "Gemini",
                        timestamp,
                        input,
                        output,
                        FirstInt64(usage, "cachedContentTokenCount", "cached_input_tokens"),
                        GetString(element, "model")));
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                CollectGeminiUsage(property.Value, sourceId, timestamp, result);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectGeminiUsage(item, sourceId, inheritedTimestamp, result);
            }
        }
    }

    private IReadOnlyList<TokenUsageEvent> ReadCachedFile(
        string path,
        Func<string, IReadOnlyList<TokenUsageEvent>> parser)
    {
        try
        {
            var info = new FileInfo(path);
            if (_cache.TryGetValue(path, out var cached) &&
                cached.Length == info.Length &&
                cached.LastWriteTimeUtc == info.LastWriteTimeUtc)
            {
                return cached.Events;
            }

            var events = parser(path);
            _cache[path] = new CachedLogFile(info.Length, info.LastWriteTimeUtc, events);
            _cacheDirty = true;
            return events;
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
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
            if (stored?.Version != CacheVersion)
            {
                return;
            }

            foreach (var entry in stored.Entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.Path))
                {
                    _cache[entry.Path] = new CachedLogFile(
                        entry.Length,
                        entry.LastWriteTimeUtc,
                        entry.Events);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            _cache.Clear();
        }
    }

    private void SavePersistentCache()
    {
        if (!_cacheDirty)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_cachePath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _cachePath + ".tmp";
            var stored = new PersistentCache
            {
                Version = CacheVersion,
                Entries = _cache.Select(pair => new PersistentCacheEntry
                {
                    Path = pair.Key,
                    Length = pair.Value.Length,
                    LastWriteTimeUtc = pair.Value.LastWriteTimeUtc,
                    Events = pair.Value.Events.ToList()
                }).ToList()
            };

            using (var file = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            {
                JsonSerializer.Serialize(gzip, stored, CacheJsonOptions);
            }

            File.Move(temporaryPath, _cachePath, true);
            _cacheDirty = false;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
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
            events.Length > 0);
    }

    private static TokenTotals SumSince(IEnumerable<TokenUsageEvent> source, DateTime start)
    {
        var result = new TokenTotals(0, 0, 0);
        foreach (var item in source.Where(item => item.Timestamp.LocalDateTime >= start))
        {
            result += new TokenTotals(item.InputTokens, item.OutputTokens, item.CachedInputTokens, 1);
        }

        return result;
    }

    private static IEnumerable<string> EnumerateFiles(string root, string pattern, DateTime monthStart)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            return Directory
                .EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                .Where(path => File.GetLastWriteTime(path) >= monthStart)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static FileStream OpenSharedRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static string ResolveFolder(string environmentName, Environment.SpecialFolder fallback)
    {
        var environmentPath = Environment.GetEnvironmentVariable(environmentName);
        return !string.IsNullOrWhiteSpace(environmentPath) && Directory.Exists(environmentPath)
            ? environmentPath
            : Environment.GetFolderPath(fallback);
    }

    private static bool TryReadTimestamp(JsonElement element, out DateTimeOffset timestamp)
    {
        foreach (var name in new[] { "timestamp", "createdAt", "startTime" })
        {
            var value = GetString(element, name);
            if (DateTimeOffset.TryParse(value, out timestamp))
            {
                return true;
            }
        }

        timestamp = default;
        return false;
    }

    private static bool HasString(JsonElement element, string name, string expected) =>
        string.Equals(GetString(element, name), expected, StringComparison.Ordinal);

    private static string? GetString(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long FirstInt64(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = GetInt64(element, name);
            if (value > 0)
            {
                return value;
            }
        }

        return 0;
    }

    private static long GetInt64(JsonElement element, string name)
    {
        if (!TryGet(element, name, out var value))
        {
            return 0;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? Math.Max(0, number)
            : 0;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private sealed record CachedLogFile(
        long Length,
        DateTime LastWriteTimeUtc,
        IReadOnlyList<TokenUsageEvent> Events);

    private sealed class PersistentCache
    {
        public int Version { get; set; }

        public List<PersistentCacheEntry> Entries { get; set; } = [];
    }

    private sealed class PersistentCacheEntry
    {
        public string Path { get; set; } = string.Empty;

        public long Length { get; set; }

        public DateTime LastWriteTimeUtc { get; set; }

        public List<TokenUsageEvent> Events { get; set; } = [];
    }
}
