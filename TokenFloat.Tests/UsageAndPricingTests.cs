using TokenFloat.Models;
using TokenFloat.Services;

namespace TokenFloat.Tests;

public sealed class UsageAndPricingTests
{
    [Fact]
    public void BuildSnapshot_DeduplicatesByIdAndKeepsLargestUsage()
    {
        var now = DateTimeOffset.Now;
        var snapshot = UsageLogService.BuildSnapshot([
            new TokenUsageEvent("same", "Codex", now, 100, 10, 0, "gpt-5.4"),
            new TokenUsageEvent("same", "Codex", now, 200, 50, 20, "gpt-5.4")
        ]);

        var totals = snapshot.TotalFor(UsagePeriod.Today);

        Assert.Single(snapshot.Events);
        Assert.Equal(250, totals.TotalTokens);
        Assert.Equal(1, totals.RequestCount);
        Assert.Equal(20, totals.CachedInputTokens);
    }

    [Fact]
    public void Estimate_AppliesRegularCachedAndOutputPrices()
    {
        var snapshot = UsageLogService.BuildSnapshot([
            new TokenUsageEvent(
                "priced",
                "Codex",
                DateTimeOffset.Now,
                1_000_000,
                1_000_000,
                200_000,
                "gpt-5.4")
        ]);

        var estimate = new PricingService().Estimate(snapshot, UsagePeriod.Today);

        Assert.Equal(17.05m, estimate.EstimatedUsd);
        Assert.Equal(1, estimate.PricedRequestCount);
        Assert.True(estimate.IsComplete);
    }

    [Fact]
    public void Estimate_DoesNotChargeUnknownModels()
    {
        var snapshot = UsageLogService.BuildSnapshot([
            new TokenUsageEvent(
                "unknown",
                "Codex",
                DateTimeOffset.Now,
                1_000_000,
                1_000_000,
                0,
                "third-party-model")
        ]);

        var estimate = new PricingService().Estimate(snapshot, UsagePeriod.Today);

        Assert.Equal(0m, estimate.EstimatedUsd);
        Assert.Equal(0, estimate.PricedRequestCount);
        Assert.Equal(1, estimate.UnpricedRequestCount);
        Assert.False(estimate.IsComplete);
    }

    [Theory]
    [InlineData(UsagePeriod.Today)]
    [InlineData(UsagePeriod.Week)]
    [InlineData(UsagePeriod.Month)]
    public void TrendTotals_MatchPeriodTotalsAndEstimate(UsagePeriod period)
    {
        var now = DateTimeOffset.Now;
        var snapshot = UsageLogService.BuildSnapshot([
            new TokenUsageEvent("one", "Codex", now.AddMinutes(-10), 1200, 300, 100, "gpt-5.4"),
            new TokenUsageEvent("two", "Gemini", now.AddMinutes(-5), 800, 200, 0, "gemini-2.5-flash")
        ]);
        var pricingService = new PricingService();

        var trend = pricingService.Trend(snapshot, period);
        var estimate = pricingService.Estimate(snapshot, period);

        Assert.Equal(snapshot.TotalFor(period).TotalTokens, trend.Points.Sum(point => point.Tokens));
        Assert.Equal(snapshot.TotalFor(period).RequestCount, trend.Points.Sum(point => point.RequestCount));
        Assert.Equal(estimate.EstimatedUsd, trend.Points.Sum(point => point.Pricing.EstimatedUsd));
    }
}
