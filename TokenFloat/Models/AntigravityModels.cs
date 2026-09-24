namespace TokenFloat.Models;

public sealed record AntigravityQuotaSnapshot(
    DateTimeOffset RetrievedAt,
    string? PlanName,
    decimal? MonthlyPromptCredits,
    decimal? AvailablePromptCredits,
    IReadOnlyList<AntigravityModelQuota> Models);

public sealed record AntigravityModelQuota(
    string Model,
    string? Label,
    decimal? RemainingFraction,
    DateTimeOffset? ResetAt);
