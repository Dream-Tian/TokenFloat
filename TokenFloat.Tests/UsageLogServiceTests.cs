using System.Net;
using System.Net.Http;
using System.Text;
using TokenFloat.Models;
using TokenFloat.Services;

namespace TokenFloat.Tests;

public sealed class UsageLogServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"TokenFloat.UsageLogTests.{Guid.NewGuid():N}");

    public UsageLogServiceTests() => Directory.CreateDirectory(_folder);

    [Fact]
    public async Task TestConnection_SendsNewApiAuthAndAcceptsSuccessfulStatus()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);
        var settings = new AppSettings(
            NewApiBaseUrl: "https://newapi.example.com",
            NewApiAccessToken: "test-token",
            NewApiUserId: 123);
        var service = new UsageLogService(
            new AppSettingsService(Path.Combine(_folder, "settings")),
            client,
            _folder);

        var result = await service.TestConnectionAsync(settings);

        Assert.True(result.IsSuccess);
        Assert.Equal("https://newapi.example.com/api/status", handler.Requests.Single().RequestUri!.ToString());
        Assert.Equal("Bearer test-token", handler.Requests[0].Headers.Authorization?.ToString());
        Assert.Equal("123", handler.Requests[0].Headers.GetValues("New-Api-User").Single());
        Assert.True(result.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task TestConnection_ReturnsFailureForUnauthorizedStatus()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client = new HttpClient(handler);
        var settings = new AppSettings(
            NewApiBaseUrl: "https://newapi.example.com",
            NewApiAccessToken: "bad-token");
        var service = new UsageLogService(
            new AppSettingsService(Path.Combine(_folder, "settings")),
            client,
            _folder);

        var result = await service.TestConnectionAsync(settings);

        Assert.False(result.IsSuccess);
        Assert.Contains("401", result.Message);
    }

    [Fact]
    public async Task LoadSnapshot_UsesIncrementalRangeAndMergesCachedEvents()
    {
        var now = DateTimeOffset.Now;
        var firstRow = $"{{\"model_name\":\"gpt-test\",\"created_at\":{now.AddMinutes(-20).ToUnixTimeSeconds()},\"token_used\":100,\"count\":1,\"quota\":50}}";
        var secondRow = $"{{\"model_name\":\"gpt-test\",\"created_at\":{now.AddMinutes(-1).ToUnixTimeSeconds()},\"token_used\":200,\"count\":1,\"quota\":100}}";
        var loadNumber = 0;
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/status", StringComparison.Ordinal))
            {
                loadNumber++;
                return JsonResponse("{\"quota_per_unit\":500000}");
            }

            return JsonResponse($"{{\"success\":true,\"data\":[{(loadNumber == 1 ? firstRow : secondRow)}]}}");
        });
        var settingsService = new AppSettingsService(Path.Combine(_folder, "settings"));
        settingsService.SetNewApi("https://newapi.example.com", "test-token", 0);
        using var client = new HttpClient(handler);
        var service = new UsageLogService(settingsService, client, Path.Combine(_folder, "cache"));

        var first = await service.LoadSnapshotAsync();
        var second = await service.LoadSnapshotAsync();

        var dataUris = handler.Requests
            .Where(request => request.RequestUri!.AbsolutePath.EndsWith("/api/data/self", StringComparison.Ordinal))
            .Select(request => request.RequestUri!)
            .ToArray();
        Assert.True(dataUris.Length >= 2);
        var firstStart = long.Parse(GetQueryValue(dataUris[0], "start_timestamp"));
        var secondStart = long.Parse(GetQueryValue(dataUris[^1], "start_timestamp"));
        Assert.True(secondStart > firstStart);
        Assert.Equal(300, second.TotalFor(UsagePeriod.Today).TotalTokens);
        Assert.Equal(2, second.Events.Count);
        Assert.Equal(100, first.TotalFor(UsagePeriod.Today).TotalTokens);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static string GetQueryValue(Uri uri, string name)
    {
        var pair = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Single(item => item.StartsWith(name + "=", StringComparison.Ordinal));
        return Uri.UnescapeDataString(pair[(name.Length + 1)..]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responseFactory(request));
        }
    }
}
