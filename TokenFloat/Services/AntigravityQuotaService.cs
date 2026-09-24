using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using TokenFloat.Models;

namespace TokenFloat.Services;

public sealed class AntigravityQuotaService
{
    internal AntigravityLocalClient Client { get; }

    public AntigravityQuotaService(HttpClient? httpClient = null)
        : this(new AntigravityLocalClient(httpClient))
    {
    }

    internal AntigravityQuotaService(AntigravityLocalClient client)
    {
        Client = client;
    }

    // 通过本地 Language Server 读取套餐和模型配额；余额不换算为 token。
    public async Task<AntigravityQuotaReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        var result = await Client.CallAsync("GetUserStatus", new
        {
            metadata = new { ideName = "antigravity", extensionName = "antigravity", locale = "en" }
        }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = result.Data is { } data ? ParseUserStatus(data) : null;
        return new AntigravityQuotaReadResult(
            snapshot,
            result.Data is null ? result.Message : snapshot is null ? "Antigravity 返回未包含配额信息" : "读取成功",
            result.Endpoint);
    }

    // 兼容 protobuf JSON 中不同层级的套餐信息及数值、字符串形式的配额。
    internal static AntigravityQuotaSnapshot? ParseUserStatus(JsonElement root)
    {
        var userStatus = FindObject(root, "userStatus") ?? root;
        var planStatus = FindObject(userStatus, "planStatus");
        var planInfo = FindObject(planStatus, "planInfo") ?? FindObject(userStatus, "planInfo") ?? FindObject(root, "planInfo");
        var cascade = FindObject(userStatus, "cascadeModelConfigData") ?? FindObject(root, "cascadeModelConfigData");
        var configs = FindProperty(cascade, "clientModelConfigs");
        var models = configs is { ValueKind: JsonValueKind.Array } array
            ? array.EnumerateArray().Select(ParseModelQuota).OfType<AntigravityModelQuota>().ToArray()
            : [];
        var planName = GetString(planInfo, "planName") ?? GetString(planStatus, "planName");
        var monthlyCredits = GetDecimal(planInfo, "monthlyPromptCredits");
        var availableCredits = GetDecimal(planStatus, "availablePromptCredits") ?? GetDecimal(planInfo, "availablePromptCredits");

        if (planName is null && monthlyCredits is null && availableCredits is null && models.Length == 0)
        {
            return null;
        }

        return new AntigravityQuotaSnapshot(DateTimeOffset.Now, planName, monthlyCredits, availableCredits, models);
    }

    internal static IReadOnlyList<(int ProcessId, int? ExtensionServerPort, string CsrfToken)> ParseProcesses(
        IEnumerable<(int ProcessId, string? CommandLine)> processes) => AntigravityLocalClient.ParseProcesses(processes);

    private static AntigravityModelQuota? ParseModelQuota(JsonElement config)
    {
        var alias = FindObject(config, "modelOrAlias");
        var model = GetString(config, "modelId") ?? GetString(alias, "model") ?? GetString(alias, "alias");
        var quota = FindObject(config, "quotaInfo");
        if (model is null || quota is null)
        {
            return null;
        }

        // remainingFraction 是无 presence 的 proto3 float，字段省略时表示零。
        var fraction = FindProperty(quota, "remainingFraction") is null ? 0 : GetDecimal(quota, "remainingFraction");
        if (fraction is < 0 or > 1)
        {
            fraction = null;
        }

        return new AntigravityModelQuota(model, GetString(config, "label"), fraction, ParseResetTime(FindProperty(quota, "resetTime")));
    }

    // Timestamp 通常为 RFC3339，也兼容 seconds/nanos 对象；坏值保留为未知。
    private static DateTimeOffset? ParseResetTime(JsonElement? value)
    {
        if (value is { ValueKind: JsonValueKind.String } text &&
            DateTimeOffset.TryParse(text.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var resetAt))
        {
            return resetAt.ToLocalTime();
        }

        if (value is not { ValueKind: JsonValueKind.Object } timestamp ||
            !long.TryParse(GetString(timestamp, "seconds"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        var nanos = FindProperty(timestamp, "nanos") is null ? 0 : GetDecimal(timestamp, "nanos");
        if (nanos is null or < 0 or >= 1_000_000_000 || decimal.Truncate(nanos.Value) != nanos.Value)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks((long)nanos.Value / 100).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static JsonElement? FindProperty(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) ? property : null;

    private static JsonElement? FindObject(JsonElement? parent, string name) =>
        FindProperty(parent, name) is { ValueKind: JsonValueKind.Object } value ? value : null;

    private static string? GetString(JsonElement? parent, string name)
    {
        var property = FindProperty(parent, name);
        var text = property switch
        {
            { ValueKind: JsonValueKind.String } value => value.GetString(),
            { ValueKind: JsonValueKind.Number } value => value.GetRawText(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static decimal? GetDecimal(JsonElement? parent, string name) =>
        decimal.TryParse(GetString(parent, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}

public sealed record AntigravityQuotaReadResult(
    AntigravityQuotaSnapshot? Snapshot,
    string Message,
    string? Endpoint);
