using TokenFloat.Models;

namespace TokenFloat.Services;

public sealed class PricingService
{
    public const string PricingNotice =
        "缺少可用计费信息的请求保留为未计价；Antigravity Token 和配额不能折算为订阅账单。";
    public const string QuotaNotice =
        "按 NewAPI 消费日志中的已扣额度折算；实际显示受服务端 quota_per_unit 配置影响。";

    /// <summary>
    /// 按事件来源计算已知消耗；仅汇总数据沿用 NewAPI quota，避免给本地请求误计费用。
    /// </summary>
    public PricingEstimate Estimate(UsageSnapshot snapshot, UsagePeriod period)
    {
        var total = snapshot.TotalFor(period);
        return snapshot.Events.Count == 0 && total.Quota > 0
            ? EstimateQuota(total.Quota, total.RequestCount, snapshot.QuotaPerUnit)
            : EstimateEvents(EventsForPeriod(snapshot, period), snapshot.QuotaPerUnit);
    }

    public PricingEstimate Estimate(UsageSnapshot snapshot, UsageDateRange range) =>
        EstimateEvents(EventsForRange(snapshot, range), snapshot.QuotaPerUnit);

    public IReadOnlyDictionary<string, PricingEstimate> EstimateModels(
        UsageSnapshot snapshot,
        UsagePeriod period) =>
        EstimateModels(EventsForPeriod(snapshot, period), snapshot.QuotaPerUnit);

    public IReadOnlyDictionary<string, PricingEstimate> EstimateModels(
        UsageSnapshot snapshot,
        UsageDateRange range) =>
        EstimateModels(EventsForRange(snapshot, range), snapshot.QuotaPerUnit);

    /// <summary>
    /// 单次遍历按模型分桶后逐组估算，替代每个模型各自全量扫描事件。
    /// </summary>
    private static IReadOnlyDictionary<string, PricingEstimate> EstimateModels(
        IEnumerable<TokenUsageEvent> events,
        decimal quotaPerUnit)
    {
        var byModel = new Dictionary<string, List<TokenUsageEvent>>(StringComparer.Ordinal);
        foreach (var item in events)
        {
            var key = item.NormalizedModel;
            if (!byModel.TryGetValue(key, out var bucket))
            {
                bucket = [];
                byModel[key] = bucket;
            }

            bucket.Add(item);
        }

        return byModel.ToDictionary(
            pair => pair.Key,
            pair => EstimateEvents(pair.Value, quotaPerUnit),
            StringComparer.Ordinal);
    }

    public PricingTrend Trend(UsageSnapshot snapshot, UsagePeriod period, string? model = null)
    {
        var tokenTrend = snapshot.TrendFor(period, model: model);
        return BuildTrend(tokenTrend, FilterByModel(EventsForPeriod(snapshot, period), model), snapshot.QuotaPerUnit);
    }

    public PricingTrend Trend(UsageSnapshot snapshot, UsageDateRange range, string? model = null) =>
        BuildTrend(
            snapshot.TrendFor(range, model),
            FilterByModel(EventsForRange(snapshot, range), model),
            snapshot.QuotaPerUnit);

    private static IEnumerable<TokenUsageEvent> FilterByModel(
        IEnumerable<TokenUsageEvent> events,
        string? model) =>
        model is null
            ? events
            : events.Where(item => item.NormalizedModel == model);

    private static PricingTrend BuildTrend(
        UsageTrend tokenTrend,
        IEnumerable<TokenUsageEvent> events,
        decimal quotaPerUnit)
    {
        var points = tokenTrend.Points;
        var buckets = BucketEvents(points, events);
        return new PricingTrend(
            tokenTrend.Title,
            points.Select((point, index) =>
            {
                var bucket = buckets[index] ?? [];
                return new PricingTrendPoint(
                    point.Label,
                    point.Tokens,
                    bucket.Sum(EventRequestCount),
                    EstimateEvents(bucket, quotaPerUnit));
            }).ToArray());
    }

    /// <summary>
    /// 单次遍历并按趋势点起点二分装入分桶，替代每个趋势点单独扫描全部事件。
    /// </summary>
    private static List<TokenUsageEvent>?[] BucketEvents(
        IReadOnlyList<UsageTrendPoint> points,
        IEnumerable<TokenUsageEvent> events)
    {
        var buckets = new List<TokenUsageEvent>?[points.Count];
        if (points.Count == 0)
        {
            return buckets;
        }

        var starts = new long[points.Count];
        for (var index = 0; index < points.Count; index++)
        {
            starts[index] = points[index].Start.Ticks;
        }

        foreach (var item in events)
        {
            var timestamp = item.Timestamp.LocalDateTime;
            var index = Array.BinarySearch(starts, timestamp.Ticks);
            if (index < 0)
            {
                index = ~index - 1;
            }

            if (index < 0 || timestamp >= points[index].EndExclusive)
            {
                continue;
            }

            (buckets[index] ??= []).Add(item);
        }

        return buckets;
    }

    private static PricingEstimate EstimateEvents(IEnumerable<TokenUsageEvent> events, decimal quotaPerUnit)
    {
        var source = events.ToArray();
        var quotaTotal = source.Sum(item => item.Quota);
        var unit = quotaPerUnit > 0 ? quotaPerUnit : 500_000m;
        decimal total = quotaTotal / unit;
        long pricedRequests = 0;
        long unpricedRequests = 0;
        var hasModelPricing = false;
        var unpricedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in source)
        {
            if (item.Quota > 0)
            {
                pricedRequests += EventRequestCount(item);
                continue;
            }

            var price = ResolvePrice(item);
            if (price is null)
            {
                unpricedRequests += EventRequestCount(item);
                unpricedModels.Add($"{item.Provider} / {item.NormalizedModel}");
                continue;
            }

            var regularInput = price.InputIncludesCached
                ? Math.Max(0, item.InputTokens - item.CachedInputTokens)
                : item.InputTokens;
            total += (regularInput * price.InputPerMillion +
                item.CachedInputTokens * price.CachedInputPerMillion +
                item.CacheWriteInputTokens * price.CacheWriteFiveMinutesPerMillion +
                item.CacheWriteOneHourInputTokens * price.CacheWriteOneHourPerMillion +
                item.OutputTokens * price.OutputPerMillion) / 1_000_000m;
            pricedRequests += EventRequestCount(item);
            hasModelPricing = true;
        }

        return new PricingEstimate(
            total,
            pricedRequests,
            unpricedRequests,
            unpricedModels.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            quotaTotal > 0 && !hasModelPricing,
            quotaTotal);
    }

    private static PricingEstimate EstimateQuota(decimal quota, long requestCount, decimal quotaPerUnit)
    {
        var unit = quotaPerUnit > 0 ? quotaPerUnit : 500_000m;
        return new PricingEstimate(
            quota / unit,
            requestCount,
            0,
            [],
            true,
            quota);
    }

    private static IEnumerable<TokenUsageEvent> EventsForPeriod(
        UsageSnapshot snapshot,
        UsagePeriod period)
    {
        var now = DateTime.Now;
        var start = PeriodStart(period, now);
        var end = period switch
        {
            UsagePeriod.Today => start.AddDays(1),
            UsagePeriod.Week => start.AddDays(7),
            _ => start.AddMonths(1)
        };

        return snapshot.Events.Where(item =>
            item.Timestamp.LocalDateTime >= start && item.Timestamp.LocalDateTime < end);
    }

    private static IEnumerable<TokenUsageEvent> EventsForRange(
        UsageSnapshot snapshot,
        UsageDateRange range) =>
        snapshot.Events.Where(item =>
            item.Timestamp.LocalDateTime >= range.Start &&
            item.Timestamp.LocalDateTime < range.EndExclusive);

    private static DateTime PeriodStart(UsagePeriod period, DateTime now) => period switch
    {
        UsagePeriod.Today => now.Date,
        UsagePeriod.Week => now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7)),
        _ => new DateTime(now.Year, now.Month, 1)
    };

    private static long EventRequestCount(TokenUsageEvent item) =>
        Math.Max(0, item.RequestCount);

    private static ModelPrice? ResolvePrice(TokenUsageEvent item)
    {
        var model = NormalizeModel(item.Model);
        return item.Provider switch
        {
            "Codex" => ResolveOpenAiPrice(model),
            "Claude" => ResolveAnthropicPrice(model, item.Timestamp.LocalDateTime),
            "Gemini" => ResolveGeminiPrice(model, item.InputTokens),
            _ => null
        };
    }

    private static ModelPrice? ResolveOpenAiPrice(string model)
    {
        if (Matches(model, "gpt-5-6-sol")) return StandardPrice(5m, 0.5m, 30m);
        if (Matches(model, "gpt-5-6-terra")) return StandardPrice(2.5m, 0.25m, 15m);
        if (Matches(model, "gpt-5-6-luna")) return StandardPrice(1m, 0.1m, 6m);
        if (Matches(model, "gpt-5-5-pro")) return StandardPrice(30m, 30m, 180m);
        if (Matches(model, "gpt-5-5")) return StandardPrice(5m, 0.5m, 30m);
        if (Matches(model, "gpt-5-4-mini")) return StandardPrice(0.75m, 0.075m, 4.5m);
        if (Matches(model, "gpt-5-4-nano")) return StandardPrice(0.2m, 0.02m, 1.25m);
        if (Matches(model, "gpt-5-4-pro")) return StandardPrice(30m, 30m, 180m);
        if (Matches(model, "gpt-5-4")) return StandardPrice(2.5m, 0.25m, 15m);
        return null;
    }

    private static ModelPrice? ResolveAnthropicPrice(string model, DateTime timestamp)
    {
        if (Matches(model, "claude-fable-5") || Matches(model, "claude-mythos-5"))
            return AnthropicPrice(10m, 1m, 50m, 12.5m, 20m);
        if (Matches(model, "claude-opus-5") || Matches(model, "claude-opus-4-8") ||
            Matches(model, "claude-opus-4-7") || Matches(model, "claude-opus-4-6") ||
            Matches(model, "claude-opus-4-5"))
            return AnthropicPrice(5m, 0.5m, 25m, 6.25m, 10m);
        if (Matches(model, "claude-opus-4-1") || Matches(model, "claude-opus-4"))
            return AnthropicPrice(15m, 1.5m, 75m, 18.75m, 30m);
        if (Matches(model, "claude-sonnet-5"))
            return timestamp < new DateTime(2026, 9, 1)
                ? AnthropicPrice(2m, 0.2m, 10m, 2.5m, 4m)
                : AnthropicPrice(3m, 0.3m, 15m, 3.75m, 6m);
        if (Matches(model, "claude-sonnet-4-6") || Matches(model, "claude-sonnet-4-5") ||
            Matches(model, "claude-sonnet-4"))
            return AnthropicPrice(3m, 0.3m, 15m, 3.75m, 6m);
        if (Matches(model, "claude-haiku-4-5"))
            return AnthropicPrice(1m, 0.1m, 5m, 1.25m, 2m);
        if (Matches(model, "claude-haiku-3-5"))
            return AnthropicPrice(0.8m, 0.08m, 4m, 1m, 1.6m);
        return null;
    }

    private static ModelPrice? ResolveGeminiPrice(string model, long inputTokens)
    {
        if (Matches(model, "gemini-3-6-flash")) return StandardPrice(1.5m, 0.15m, 7.5m);
        if (Matches(model, "gemini-3-5-flash-lite")) return StandardPrice(0.3m, 0.03m, 2.5m);
        if (Matches(model, "gemini-3-5-flash")) return StandardPrice(1.5m, 0.15m, 9m);
        if (Matches(model, "gemini-3-1-pro"))
            return inputTokens > 200_000
                ? StandardPrice(4m, 0.4m, 18m)
                : StandardPrice(2m, 0.2m, 12m);
        if (Matches(model, "gemini-3-flash")) return StandardPrice(0.5m, 0.05m, 3m);
        if (Matches(model, "gemini-2-5-pro"))
            return inputTokens > 200_000
                ? StandardPrice(2.5m, 0.25m, 15m)
                : StandardPrice(1.25m, 0.125m, 10m);
        if (Matches(model, "gemini-2-5-flash-lite")) return StandardPrice(0.1m, 0.01m, 0.4m);
        if (Matches(model, "gemini-2-5-flash")) return StandardPrice(0.3m, 0.03m, 2.5m);
        if (Matches(model, "gemini-2-0-flash-lite")) return StandardPrice(0.075m, 0.075m, 0.3m);
        if (Matches(model, "gemini-2-0-flash")) return StandardPrice(0.1m, 0.025m, 0.4m);
        return null;
    }

    private static ModelPrice StandardPrice(decimal input, decimal cached, decimal output) =>
        new(input, cached, output, 0m, 0m, true);

    private static ModelPrice AnthropicPrice(
        decimal input,
        decimal cached,
        decimal output,
        decimal cacheWriteFiveMinutes,
        decimal cacheWriteOneHour) =>
        new(input, cached, output, cacheWriteFiveMinutes, cacheWriteOneHour, false);

    private static bool Matches(string model, string prefix) =>
        model == prefix || model.StartsWith($"{prefix}-", StringComparison.Ordinal);

    private static string NormalizeModel(string? model) =>
        (model ?? string.Empty)
        .Trim()
        .ToLowerInvariant()
        .Replace("models/", string.Empty, StringComparison.Ordinal)
        .Replace('.', '-')
        .Replace('_', '-');

    private sealed record ModelPrice(
        decimal InputPerMillion,
        decimal CachedInputPerMillion,
        decimal OutputPerMillion,
        decimal CacheWriteFiveMinutesPerMillion,
        decimal CacheWriteOneHourPerMillion,
        bool InputIncludesCached);
}

public sealed record PricingEstimate(
    decimal EstimatedUsd,
    long PricedRequestCount,
    long UnpricedRequestCount,
    IReadOnlyList<string> UnpricedModels,
    bool IsQuotaBased = false,
    decimal Quota = 0m)
{
    public bool HasPricedUsage => PricedRequestCount > 0;

    public bool IsComplete => UnpricedRequestCount == 0;
}

public sealed record PricingTrend(string Title, IReadOnlyList<PricingTrendPoint> Points);

public sealed record PricingTrendPoint(
    string Label,
    long Tokens,
    long RequestCount,
    PricingEstimate Pricing);
