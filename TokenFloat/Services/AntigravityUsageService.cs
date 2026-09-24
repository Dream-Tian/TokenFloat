using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TokenFloat.Models;

namespace TokenFloat.Services;

public sealed class AntigravityUsageService
{
    private const int MaxTrajectoriesPerRead = 1_024;
    private readonly Func<string, object, CancellationToken, Task<IReadOnlyList<AntigravityRpcResult>>> _callAll;
    private readonly Func<string, string, object, CancellationToken, Task<AntigravityRpcResult>> _callOnEndpoint;
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly Dictionary<string, CachedTrajectory> _cache = new(StringComparer.Ordinal);
    private readonly int _maxPagesPerRead;
    private readonly TimeSpan _readTimeout;
    private int _cacheGeneration;
    private int _appliedCacheGeneration;

    public AntigravityUsageService(HttpClient? httpClient = null)
        : this(new AntigravityLocalClient(httpClient))
    {
    }

    internal AntigravityUsageService(AntigravityLocalClient client)
        : this(client.CallAllAsync, client.CallOnEndpointAsync)
    {
    }

    internal AntigravityUsageService(
        Func<string, object, CancellationToken, Task<AntigravityRpcResult>> call,
        int maxPagesPerRead = 128,
        TimeSpan? readTimeout = null)
        : this(
            async (method, request, token) => [await call(method, request, token)],
            (_, method, request, token) => call(method, request, token),
            maxPagesPerRead,
            readTimeout)
    {
    }

    internal AntigravityUsageService(
        Func<string, object, CancellationToken, Task<IReadOnlyList<AntigravityRpcResult>>> callAll,
        Func<string, string, object, CancellationToken, Task<AntigravityRpcResult>> callOnEndpoint,
        int maxPagesPerRead = 128,
        TimeSpan? readTimeout = null)
    {
        _callAll = callAll;
        _callOnEndpoint = callOnEndpoint;
        _maxPagesPerRead = Math.Max(1, maxPagesPerRead);
        _readTimeout = readTimeout ?? TimeSpan.FromSeconds(20);
    }

    /// <summary>
    /// 分页读取本机可见会话的模型调用元数据，只累计 usage；未变且空闲的会话复用扫描缓存。
    /// </summary>
    public async Task<AntigravityUsageReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _readLock.WaitAsync(cancellationToken);
        try
        {
            return await ReadCoreAsync(cancellationToken);
        }
        finally
        {
            _readLock.Release();
        }
    }

    /// <summary>
    /// 使下一轮扫描重新读取全部调用元数据，避免与正在进行的读取同时修改缓存。
    /// </summary>
    internal void ClearCache() => Interlocked.Increment(ref _cacheGeneration);

    // 先枚举当前可见会话，再按调用元数据分页；失败时只返回本轮已验证的记录。
    private async Task<AntigravityUsageReadResult> ReadCoreAsync(CancellationToken cancellationToken)
    {
        var generation = Volatile.Read(ref _cacheGeneration);
        if (_appliedCacheGeneration != generation)
        {
            _cache.Clear();
            _appliedCacheGeneration = generation;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_readTimeout);
        var state = new ReadState();
        var hasList = false;
        try
        {
            var listResults = await _callAll(
                "GetAllCascadeTrajectories",
                new { excludeSubtrajectories = false },
                deadline.Token);
            cancellationToken.ThrowIfCancellationRequested();
            var trajectories = new List<ServerTrajectory>();
            foreach (var listResult in listResults)
            {
                if (listResult.Data is not { } list)
                {
                    state.FailedServers++;
                    continue;
                }

                hasList = true;
                if (!TryReadTrajectories(list, out var serverTrajectories))
                {
                    state.MalformedLists++;
                    continue;
                }

                var endpoint = EndpointIdentity(listResult.Endpoint);
                if (endpoint is null)
                {
                    state.FailedServers++;
                    continue;
                }

                trajectories.AddRange(serverTrajectories.Select(item =>
                    new ServerTrajectory(listResult.Endpoint!, $"{endpoint}|{item.Id}", item)));
            }

            if (!hasList)
            {
                return new AntigravityUsageReadResult([], "未能读取 Antigravity 本地服务的会话列表", false, IsUnavailable: true);
            }

            var visibleIds = trajectories.Select(item => item.CacheKey).ToHashSet(StringComparer.Ordinal);
            foreach (var id in _cache.Keys.Where(id => !visibleIds.Contains(id)).ToArray())
            {
                _cache.Remove(id);
            }

            var pagesRead = 0;
            foreach (var serverTrajectory in trajectories.OrderByDescending(item => item.Trajectory.LastModified).Take(MaxTrajectoriesPerRead))
            {
                deadline.Token.ThrowIfCancellationRequested();
                var trajectory = serverTrajectory.Trajectory;
                var cacheKey = serverTrajectory.CacheKey;
                if (trajectory.Fingerprint is not null &&
                    _cache.TryGetValue(cacheKey, out var cached) &&
                    cached.Fingerprint == trajectory.Fingerprint)
                {
                    state.Add(cached.Events);
                    continue;
                }

                if (pagesRead >= _maxPagesPerRead)
                {
                    state.LimitReached = true;
                    break;
                }

                var events = new List<TokenUsageEvent>();
                var offset = 0;
                var finished = false;
                var invalidBefore = state.InvalidRecords;
                string? previousPage = null;
                while (pagesRead < _maxPagesPerRead)
                {
                    pagesRead++;
                    var pageResult = await _callOnEndpoint(
                        serverTrajectory.Endpoint,
                        "GetCascadeTrajectoryGeneratorMetadata",
                        new { cascadeId = trajectory.Id, generatorMetadataOffset = offset, includeMessages = false },
                        deadline.Token);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pageResult.Data is not { } page)
                    {
                        state.FailedTrajectories++;
                        break;
                    }

                    if (!string.Equals(EndpointIdentity(pageResult.Endpoint), EndpointIdentity(serverTrajectory.Endpoint), StringComparison.Ordinal))
                    {
                        state.ServiceChanged = true;
                        break;
                    }

                    if (!TryGetRepeatedField(page, "generatorMetadata", out var metadata))
                    {
                        state.FailedTrajectories++;
                        break;
                    }

                    if (metadata is null || metadata.Value.GetArrayLength() == 0)
                    {
                        finished = true;
                        break;
                    }

                    var pageIdentity = Hash(metadata.Value.GetRawText());
                    if (pageIdentity == previousPage)
                    {
                        state.FailedTrajectories++;
                        break;
                    }

                    previousPage = pageIdentity;
                    foreach (var record in metadata.Value.EnumerateArray())
                    {
                        var result = ParseRecord(cacheKey, offset++, record);
                        if (result.Event is not null)
                        {
                            events.Add(result.Event);
                            state.Add([result.Event]);
                        }
                        else if (result.Problem is not null)
                        {
                            state.RecordProblem(result.Problem.Value);
                        }
                    }
                }

                if (!finished && pagesRead >= _maxPagesPerRead)
                {
                    state.LimitReached = true;
                }

                if (finished && offset == 0 && trajectory.StepCount > 0)
                {
                    state.MissingUsage++;
                }

                if (finished && state.InvalidRecords == invalidBefore && trajectory.Fingerprint is not null)
                {
                    _cache[cacheKey] = new CachedTrajectory(trajectory.Fingerprint, events.ToArray());
                }
                else
                {
                    _cache.Remove(cacheKey);
                }
            }

            if (trajectories.Count > MaxTrajectoriesPerRead)
            {
                state.LimitReached = true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return state.Result();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            state.LimitReached = true;
            return state.Result(isUnavailable: !hasList);
        }
    }

    /// <summary>
    /// 单条 generator 对应一次模型调用，将缓存读写并入总输入并保留原始输入；按调用时间归属。
    /// </summary>
    internal static AntigravityUsageRecordParseResult ParseRecord(string cascadeId, int offset, JsonElement record)
    {
        if (!TryGetObject(record, "chatModel", out var chatModel))
        {
            return TryGetObject(record, "injected", out _) ? new(null, null) : new(null, RecordProblem.MissingUsage);
        }

        if (!TryGetObject(chatModel, "usage", out var usage))
        {
            return new(null, RecordProblem.MissingUsage);
        }

        if (!TryGetProperty(usage, "inputTokens", out _) && !TryGetProperty(usage, "outputTokens", out _) &&
            !TryGetProperty(usage, "cacheReadTokens", out _) && !TryGetProperty(usage, "cacheWriteTokens", out _))
        {
            return new(null, RecordProblem.MissingUsage);
        }

        if (!TryReadCounter(usage, "inputTokens", out var input) ||
            !TryReadCounter(usage, "outputTokens", out var output) ||
            !TryReadCounter(usage, "cacheReadTokens", out var cacheRead) ||
            !TryReadCounter(usage, "cacheWriteTokens", out var cacheWrite))
        {
            return new(null, RecordProblem.InvalidCounters);
        }

        long totalInput;
        try
        {
            totalInput = checked(input + cacheRead + cacheWrite);
            _ = checked(totalInput + output);
        }
        catch (OverflowException)
        {
            return new(null, RecordProblem.InvalidCounters);
        }

        if (!TryGetObject(chatModel, "chatStartMetadata", out var started) ||
            !TryGetProperty(started, "createdAt", out var createdAt) ||
            !TryReadTimestamp(createdAt, out var timestamp))
        {
            return new(null, RecordProblem.MissingTimestamp);
        }

        var responseId = GetString(usage, "responseId");
        var messageId = GetString(usage, "messageId");
        var providerMessageId = GetString(usage, "providerAssignedMessageId");
        var provider = GetString(usage, "apiProvider") ?? "";
        var identity = responseId is not null ? $"response:{provider}:{responseId}"
            : messageId is not null ? $"message:{provider}:{messageId}"
            : providerMessageId is not null ? $"provider-message:{provider}:{providerMessageId}"
            : $"generator:{cascadeId}:{offset}";
        var model = GetString(chatModel, "responseModel")
            ?? GetString(chatModel, "responseModelFull")
            ?? GetString(chatModel, "modelDisplayName")
            ?? GetString(usage, "model")
            ?? GetString(chatModel, "model");
        var item = new TokenUsageEvent(
            $"antigravity:{Hash(identity)}",
            "Antigravity",
            timestamp,
            totalInput,
            output,
            cacheRead,
            model,
            CacheWriteInputTokens: cacheWrite,
            RequestCount: 1,
            ReportedInputTokens: input);
        return new(item, null);
    }

    // 会话摘要仅用于发现和缓存失效，不把 stepCount 当成请求数或用更新时间代替调用时间。
    private static bool TryReadTrajectories(JsonElement root, out IReadOnlyList<Trajectory> trajectories)
    {
        trajectories = [];
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!TryGetProperty(root, "trajectorySummaries", out var summaries))
        {
            return !root.EnumerateObject().Any();
        }

        if (summaries.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var result = new List<Trajectory>();
        foreach (var summary in summaries.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(summary.Name) || summary.Value.ValueKind != JsonValueKind.Object ||
                !TryReadCounter(summary.Value, "stepCount", out var stepCount))
            {
                return false;
            }

            var modified = TryGetProperty(summary.Value, "lastModifiedTime", out var rawModified) &&
                TryReadTimestamp(rawModified, out var timestamp) ? timestamp : (DateTimeOffset?)null;
            var status = GetString(summary.Value, "status");
            var isIdle = status is "CASCADE_RUN_STATUS_IDLE" or "1";
            var trajectoryId = GetString(summary.Value, "trajectoryId") ?? summary.Name;
            var created = TryGetProperty(summary.Value, "createdTime", out var rawCreated)
                ? rawCreated.GetRawText()
                : "";
            var fingerprint = isIdle && modified is not null
                ? Hash($"{trajectoryId}|{modified:O}|{stepCount}|{created}")
                : null;
            result.Add(new Trajectory(summary.Name, stepCount, modified, fingerprint));
        }

        trajectories = result.OrderByDescending(item => item.LastModified).ToArray();
        return true;
    }

    /// <summary>
    /// usage 对象存在时遵循 protobuf JSON 的标量零值省略规则，拒绝负数、非整数和溢出计数。
    /// </summary>
    private static bool TryReadCounter(JsonElement parent, string name, out long value)
    {
        value = 0;
        if (!TryGetProperty(parent, name, out var property))
        {
            return true;
        }

        var text = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null
        };
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
            number < 0 || number > long.MaxValue || decimal.Truncate(number) != number)
        {
            return false;
        }

        value = (long)number;
        return true;
    }

    private static bool TryReadTimestamp(JsonElement value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (value.ValueKind == JsonValueKind.String)
        {
            return DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp);
        }

        if (value.ValueKind != JsonValueKind.Object || !TryGetProperty(value, "seconds", out var secondsValue) ||
            !long.TryParse(secondsValue.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) ||
            !TryReadCounter(value, "nanos", out var nanos) || nanos >= 1_000_000_000)
        {
            return false;
        }

        try
        {
            timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(nanos / 100);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryGetRepeatedField(JsonElement parent, string name, out JsonElement? array)
    {
        array = null;
        if (parent.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!TryGetProperty(parent, name, out var value))
        {
            return !parent.EnumerateObject().Any();
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        array = value;
        return true;
    }

    private static bool TryGetObject(JsonElement parent, string name, out JsonElement value) =>
        TryGetProperty(parent, name, out value) && value.ValueKind == JsonValueKind.Object;

    private static bool TryGetProperty(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        if (parent.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (parent.TryGetProperty(name, out value))
        {
            return true;
        }

        var snakeCase = JsonNamingPolicy.SnakeCaseLower.ConvertName(name);
        return parent.TryGetProperty(snakeCase, out value);
    }

    private static string? GetString(JsonElement parent, string name)
    {
        if (!TryGetProperty(parent, name, out var value) || value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
        {
            return null;
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string? EndpointIdentity(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : endpoint;

    private sealed record Trajectory(string Id, long StepCount, DateTimeOffset? LastModified, string? Fingerprint);
    private sealed record ServerTrajectory(string Endpoint, string CacheKey, Trajectory Trajectory);
    private sealed record CachedTrajectory(string Fingerprint, IReadOnlyList<TokenUsageEvent> Events);

    internal enum RecordProblem
    {
        MissingUsage,
        MissingTimestamp,
        InvalidCounters
    }

    internal sealed record AntigravityUsageRecordParseResult(TokenUsageEvent? Event, RecordProblem? Problem);

    private sealed class ReadState
    {
        private readonly Dictionary<string, TokenUsageEvent> _events = new(StringComparer.Ordinal);
        public int MissingUsage { get; set; }
        public int MissingTimestamp { get; private set; }
        public int InvalidCounters { get; private set; }
        public int FailedTrajectories { get; set; }
        public int FailedServers { get; set; }
        public int MalformedLists { get; set; }
        public bool ServiceChanged { get; set; }
        public bool LimitReached { get; set; }
        public int InvalidRecords => MissingUsage + MissingTimestamp + InvalidCounters;

        public void Add(IEnumerable<TokenUsageEvent> events)
        {
            foreach (var item in events)
            {
                if (!_events.TryGetValue(item.Id, out var previous) ||
                    item.InputTokens + item.OutputTokens > previous.InputTokens + previous.OutputTokens)
                {
                    _events[item.Id] = item;
                }
            }
        }

        public void RecordProblem(RecordProblem problem)
        {
            switch (problem)
            {
                case AntigravityUsageService.RecordProblem.MissingUsage:
                    MissingUsage++;
                    break;
                case AntigravityUsageService.RecordProblem.MissingTimestamp:
                    MissingTimestamp++;
                    break;
                case AntigravityUsageService.RecordProblem.InvalidCounters:
                    InvalidCounters++;
                    break;
            }
        }

        public AntigravityUsageReadResult Result(bool isUnavailable = false)
        {
            var problems = new List<string>();
            if (MissingUsage > 0) problems.Add($"{MissingUsage} 条记录未提供 usage");
            if (MissingTimestamp > 0) problems.Add($"{MissingTimestamp} 条调用缺少真实时间");
            if (InvalidCounters > 0) problems.Add($"{InvalidCounters} 条调用的 Token 计数无效");
            if (FailedTrajectories > 0) problems.Add($"{FailedTrajectories} 个会话未完成分页读取");
            if (FailedServers > 0) problems.Add($"{FailedServers} 个本地服务读取失败");
            if (MalformedLists > 0) problems.Add($"{MalformedLists} 个本地服务的会话列表格式无法识别");
            if (ServiceChanged) problems.Add("读取期间 Antigravity 服务发生切换");
            if (LimitReached) problems.Add("本轮扫描达到时间或分页上限");
            var complete = problems.Count == 0;
            var message = _events.Count > 0
                ? $"本机可见历史 · {_events.Count} 条模型调用记录"
                : complete ? "本机当前没有可统计的模型调用记录" : "当前未取得可统计的实际 Token 记录";
            if (!complete)
            {
                message += $"；统计不完整：{string.Join("，", problems)}";
            }

            return new AntigravityUsageReadResult(
                _events.Values.OrderBy(item => item.Timestamp).ToArray(), message, complete, isUnavailable);
        }
    }
}

public sealed record AntigravityUsageReadResult(
    IReadOnlyList<TokenUsageEvent> Events,
    string Message,
    bool IsComplete,
    bool IsUnavailable = false);
