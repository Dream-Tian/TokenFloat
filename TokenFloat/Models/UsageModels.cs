namespace TokenFloat.Models;

public enum UsagePeriod
{
    Today,
    Week,
    Month
}

public sealed record UsageDateRange(DateTime Start, DateTime EndExclusive, string Title)
{
    public int DayCount => Math.Max(1, (EndExclusive.Date - Start.Date).Days);

    public static UsageDateRange FromInclusiveDates(DateTime start, DateTime end)
    {
        var normalizedStart = start.Date;
        var normalizedEnd = end.Date;
        if (normalizedEnd < normalizedStart)
        {
            throw new ArgumentOutOfRangeException(nameof(end));
        }

        return new UsageDateRange(
            normalizedStart,
            normalizedEnd.AddDays(1),
            normalizedStart.Year == normalizedEnd.Year
                ? $"{normalizedStart:MM.dd}—{normalizedEnd:MM.dd}"
                : $"{normalizedStart:yyyy.MM.dd}—{normalizedEnd:yyyy.MM.dd}");
    }
}

public sealed record TokenUsageEvent(
    string Id,
    string Provider,
    DateTimeOffset Timestamp,
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    string? Model,
    long CacheWriteInputTokens = 0,
    long CacheWriteOneHourInputTokens = 0);

public sealed record TokenTotals(
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    long RequestCount = 0)
{
    public long EffectiveInputTokens => Math.Max(0, InputTokens - CachedInputTokens);

    public long TotalTokens => InputTokens + OutputTokens;

    public long EffectiveTokens => EffectiveInputTokens + OutputTokens;

    public static TokenTotals operator +(TokenTotals left, TokenTotals right) =>
        new(
            left.InputTokens + right.InputTokens,
            left.OutputTokens + right.OutputTokens,
            left.CachedInputTokens + right.CachedInputTokens,
            left.RequestCount + right.RequestCount);
}

public sealed record ProviderUsage(
    string Provider,
    TokenTotals Today,
    TokenTotals Week,
    TokenTotals Month,
    bool HasData)
{
    public TokenTotals For(UsagePeriod period) => period switch
    {
        UsagePeriod.Today => Today,
        UsagePeriod.Week => Week,
        _ => Month
    };
}

public sealed record UsageSnapshot(
    DateTime UpdatedAt,
    IReadOnlyList<ProviderUsage> Providers,
    IReadOnlyList<TokenUsageEvent> Events)
{
    public TokenTotals TotalFor(UsagePeriod period) => Providers
        .Select(provider => provider.For(period))
        .Aggregate(new TokenTotals(0, 0, 0), (sum, item) => sum + item);

    public TokenTotals TotalFor(UsageDateRange range) =>
        SumEvents(EventsFor(range));

    public TokenTotals TotalFor(string provider, UsageDateRange range) =>
        SumEvents(EventsFor(range).Where(item =>
            string.Equals(item.Provider, provider, StringComparison.Ordinal)));

    /// <summary>
    /// 按所选周期截至当前经过的分钟数，计算平均请求和 Token 速率。
    /// </summary>
    public UsageRates RatesFor(UsagePeriod period, DateTime? now = null)
    {
        var current = now ?? DateTime.Now;
        var start = period switch
        {
            UsagePeriod.Today => current.Date,
            UsagePeriod.Week => current.Date.AddDays(-(((int)current.DayOfWeek + 6) % 7)),
            _ => new DateTime(current.Year, current.Month, 1)
        };
        var elapsedMinutes = Math.Max(1, (current - start).TotalMinutes);
        var total = TotalFor(period);

        return new UsageRates(
            total.RequestCount,
            total.RequestCount / elapsedMinutes,
            total.TotalTokens / elapsedMinutes);
    }

    public UsageRates RatesFor(UsageDateRange range, DateTime? now = null)
    {
        var total = TotalFor(range);
        var effectiveEnd = range.EndExclusive <= (now ?? DateTime.Now)
            ? range.EndExclusive
            : now ?? DateTime.Now;
        var elapsedMinutes = Math.Max(1, (effectiveEnd - range.Start).TotalMinutes);
        return new UsageRates(
            total.RequestCount,
            total.RequestCount / elapsedMinutes,
            total.TotalTokens / elapsedMinutes);
    }

    public IReadOnlyList<ModelUsage> ModelsFor(string provider, UsagePeriod period, DateTime? now = null)
    {
        var start = PeriodStart(period, now ?? DateTime.Now);
        return BuildModels(Events.Where(item =>
            item.Provider == provider &&
            item.Timestamp.LocalDateTime >= start));
    }

    public IReadOnlyList<ModelUsage> ModelsFor(string provider, UsageDateRange range) =>
        BuildModels(EventsFor(range).Where(item => item.Provider == provider));

    private static IReadOnlyList<ModelUsage> BuildModels(IEnumerable<TokenUsageEvent> source) =>
        source
            .GroupBy(item => string.IsNullOrWhiteSpace(item.Model) ? "未标注模型" : item.Model!)
            .Select(group => new ModelUsage(
                group.Key,
                group.Aggregate(
                    new TokenTotals(0, 0, 0),
                    (sum, item) => sum + new TokenTotals(
                        item.InputTokens,
                        item.OutputTokens,
                        item.CachedInputTokens,
                        1))))
            .OrderByDescending(item => item.Totals.TotalTokens)
            .ToArray();

    /// <summary>
    /// 将当前周期截至此刻的用量，与上一周期相同进度的用量进行比较。
    /// </summary>
    public UsageComparison ComparisonFor(UsagePeriod period, DateTime? now = null)
    {
        var current = now ?? DateTime.Now;
        var currentStart = PeriodStart(period, current);
        var previousStart = period switch
        {
            UsagePeriod.Today => currentStart.AddDays(-1),
            UsagePeriod.Week => currentStart.AddDays(-7),
            _ => currentStart.AddMonths(-1)
        };
        var previousPeriodEnd = period switch
        {
            UsagePeriod.Today => previousStart.AddDays(1),
            UsagePeriod.Week => previousStart.AddDays(7),
            _ => previousStart.AddMonths(1)
        };
        var previousEnd = previousStart + (current - currentStart);
        if (previousEnd > previousPeriodEnd)
        {
            previousEnd = previousPeriodEnd;
        }

        var currentTokens = SumTokens(currentStart, current);
        var previousTokens = SumTokens(previousStart, previousEnd);
        double? changePercent = previousTokens > 0
            ? (currentTokens - previousTokens) * 100d / previousTokens
            : null;
        var label = period switch
        {
            UsagePeriod.Today => "较昨日同期",
            UsagePeriod.Week => "较上周同期",
            _ => "较上月同期"
        };

        return new UsageComparison(label, currentTokens, previousTokens, changePercent);
    }

    /// <summary>
    /// 今日按小时、本周按星期、本月按日期生成趋势点。
    /// </summary>
    public UsageTrend TrendFor(UsagePeriod period, DateTime? now = null)
    {
        var current = now ?? DateTime.Now;
        var start = PeriodStart(period, current);
        var end = period switch
        {
            UsagePeriod.Today => start.AddDays(1),
            UsagePeriod.Week => start.AddDays(7),
            _ => start.AddMonths(1)
        };
        var pointCount = period switch
        {
            UsagePeriod.Today => 24,
            UsagePeriod.Week => 7,
            _ => DateTime.DaysInMonth(current.Year, current.Month)
        };
        var buckets = new long[pointCount];

        foreach (var item in Events)
        {
            var timestamp = item.Timestamp.LocalDateTime;
            if (timestamp < start || timestamp >= end)
            {
                continue;
            }

            var index = period switch
            {
                UsagePeriod.Today => timestamp.Hour,
                UsagePeriod.Week => (timestamp.Date - start).Days,
                _ => timestamp.Day - 1
            };
            buckets[index] += item.InputTokens + item.OutputTokens;
        }

        var weekLabels = new[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
        var points = buckets
            .Select((tokens, index) =>
            {
                var pointStart = period == UsagePeriod.Today
                    ? start.AddHours(index)
                    : start.AddDays(index);
                var pointEnd = period == UsagePeriod.Today
                    ? pointStart.AddHours(1)
                    : pointStart.AddDays(1);
                return new UsageTrendPoint(
                    period switch
                    {
                        UsagePeriod.Today => $"{index:00} 时",
                        UsagePeriod.Week => weekLabels[index],
                        _ => $"{index + 1} 日"
                    },
                    tokens,
                    pointStart,
                    pointEnd);
            })
            .ToArray();
        var title = period switch
        {
            UsagePeriod.Today => "今日 · 每时",
            UsagePeriod.Week => "本周 · 每日",
            _ => "本月 · 每日"
        };

        return new UsageTrend(title, points);
    }

    /// <summary>
    /// 自定义范围按跨度自动选择小时、日、周或 30 日分桶，避免折线点过密。
    /// </summary>
    public UsageTrend TrendFor(UsageDateRange range)
    {
        var bucketDuration = range.DayCount switch
        {
            <= 1 => TimeSpan.FromHours(1),
            <= 45 => TimeSpan.FromDays(1),
            <= 180 => TimeSpan.FromDays(7),
            _ => TimeSpan.FromDays(30)
        };
        var points = new List<UsageTrendPoint>();
        for (var pointStart = range.Start; pointStart < range.EndExclusive; pointStart += bucketDuration)
        {
            var pointEnd = pointStart + bucketDuration;
            if (pointEnd > range.EndExclusive)
            {
                pointEnd = range.EndExclusive;
            }

            var label = range.DayCount switch
            {
                <= 1 => $"{pointStart:HH} 时",
                <= 45 => $"{pointStart:MM.dd}",
                <= 180 => $"{pointStart:MM.dd} 周",
                _ => $"{pointStart:yyyy.MM}"
            };
            points.Add(new UsageTrendPoint(
                label,
                SumTokens(pointStart, pointEnd),
                pointStart,
                pointEnd));
        }

        var title = range.DayCount switch
        {
            <= 1 => "自定 · 每时",
            <= 45 => "自定 · 每日",
            <= 180 => "自定 · 每周",
            _ => "自定 · 每月"
        };
        return new UsageTrend(title, points);
    }

    private static DateTime PeriodStart(UsagePeriod period, DateTime current) => period switch
    {
        UsagePeriod.Today => current.Date,
        UsagePeriod.Week => current.Date.AddDays(-(((int)current.DayOfWeek + 6) % 7)),
        _ => new DateTime(current.Year, current.Month, 1)
    };

    private long SumTokens(DateTime start, DateTime end) => Events
        .Where(item => item.Timestamp.LocalDateTime >= start && item.Timestamp.LocalDateTime < end)
        .Sum(item => item.InputTokens + item.OutputTokens);

    private IEnumerable<TokenUsageEvent> EventsFor(UsageDateRange range) => Events.Where(item =>
        item.Timestamp.LocalDateTime >= range.Start &&
        item.Timestamp.LocalDateTime < range.EndExclusive);

    private static TokenTotals SumEvents(IEnumerable<TokenUsageEvent> source) =>
        source.Aggregate(
            new TokenTotals(0, 0, 0),
            (sum, item) => sum + new TokenTotals(
                item.InputTokens,
                item.OutputTokens,
                item.CachedInputTokens,
                1));
}

public sealed record UsageRates(long RequestCount, double AverageRpm, double AverageTpm);

public sealed record ModelUsage(string Model, TokenTotals Totals);

public sealed record UsageTrend(string Title, IReadOnlyList<UsageTrendPoint> Points);

public sealed record UsageTrendPoint(
    string Label,
    long Tokens,
    DateTime Start,
    DateTime EndExclusive);

public sealed record UsageComparison(
    string Label,
    long CurrentTokens,
    long PreviousTokens,
    double? ChangePercent);
