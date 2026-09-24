using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    [Fact]
    public async Task LoadSnapshot_CollapsesDuplicateRowsForSameHourAndModel()
    {
        var hourStart = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.Now.ToUnixTimeSeconds() / 3600 * 3600);
        var rows = string.Join(",",
            $"{{\"model_name\":\"gpt-test\",\"created_at\":{hourStart.ToUnixTimeSeconds()},\"token_used\":100,\"count\":1,\"quota\":50}}",
            $"{{\"model_name\":\"gpt-test\",\"created_at\":{hourStart.ToUnixTimeSeconds()},\"token_used\":200,\"count\":2,\"quota\":120}}");
        var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/api/status", StringComparison.Ordinal)
                ? JsonResponse("{\"quota_per_unit\":500000}")
                : JsonResponse($"{{\"success\":true,\"data\":[{rows}]}}"));
        var settingsService = new AppSettingsService(Path.Combine(_folder, "settings"));
        settingsService.SetNewApi("https://newapi.example.com", "test-token", 0);
        using var client = new HttpClient(handler);
        var service = new UsageLogService(settingsService, client, Path.Combine(_folder, "cache"));

        var snapshot = await service.LoadSnapshotAsync();

        var usage = Assert.Single(snapshot.Events);
        Assert.Equal(120, usage.Quota);
        Assert.Equal(200, usage.InputTokens);
        Assert.Equal(2, usage.RequestCount);
        Assert.Equal(120, snapshot.Events.Sum(item => item.Quota));
    }

    [Fact]
    public async Task LoadSnapshot_CustomHistoryStartsAtRequestedDate()
    {
        var now = DateTime.Now;
        var requestedStart = new DateTime(now.Year, now.Month, 1).AddMonths(-3).AddDays(5).AddHours(17);
        var handler = new RecordingHandler(request => IsStatusRequest(request)
            ? JsonResponse("{\"quota_per_unit\":500000}")
            : JsonResponse("{\"success\":true,\"data\":[]}"));
        var settingsService = CreateSettingsService();
        using var client = new HttpClient(handler);
        var service = new UsageLogService(settingsService, client, Path.Combine(_folder, "cache"));

        await service.LoadSnapshotAsync(requestedStart);

        var dataRequests = handler.Requests.Where(request => !IsStatusRequest(request)).ToArray();
        var firstStart = long.Parse(GetQueryValue(dataRequests[0].RequestUri!, "start_timestamp"));
        Assert.Equal(new DateTimeOffset(requestedStart.Date).ToUnixTimeSeconds(), firstStart);
        Assert.True(dataRequests.Length >= 3);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(240)]
    public async Task LoadSnapshot_FailureKeepsCachedRateAndSuccessfulFetchTimeForRecovery(int offlineHours)
    {
        var now = DateTimeOffset.Now;
        var row = $"{{\"model_name\":\"gpt-test\",\"created_at\":{now.AddMinutes(-1).ToUnixTimeSeconds()},\"token_used\":100,\"count\":1,\"quota\":50}}";
        var failData = false;
        var statusQuota = 1000m;
        var handler = new RecordingHandler(request => IsStatusRequest(request)
            ? JsonResponse($"{{\"quota_per_unit\":{statusQuota}}}")
            : failData
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : JsonResponse($"{{\"success\":true,\"data\":[{row}]}}"));
        var settingsService = CreateSettingsService();
        var cacheFolder = Path.Combine(_folder, "cache");
        using var client = new HttpClient(handler);
        var service = new UsageLogService(settingsService, client, cacheFolder);
        var initial = await service.LoadSnapshotAsync();
        var cachePath = Directory.GetFiles(cacheFolder, "usage-index-v*.json.gz").Single();
        var cache = ReadCache(cachePath);
        var lastFetched = now.ToUniversalTime().AddHours(-offlineHours);
        cache["lastFetchedUtc"] = JsonValue.Create(lastFetched);
        WriteCache(cachePath, cache);
        var originalCache = File.ReadAllBytes(cachePath);
        service = new UsageLogService(settingsService, client, cacheFolder);
        failData = true;
        statusQuota = 2000m;

        var failed = await service.LoadSnapshotAsync();

        Assert.Contains("读取 NewAPI 失败", failed.SourceMessage);
        Assert.Equal(initial.Events.ToArray(), failed.Events.ToArray());
        Assert.Equal(1000m, failed.QuotaPerUnit);
        Assert.Equal(originalCache, File.ReadAllBytes(cachePath));
        service = new UsageLogService(settingsService, client, cacheFolder);
        Assert.Equal(1000m, service.GetCachedSnapshot()!.QuotaPerUnit);
        failData = false;
        handler.Requests.Clear();

        var recovered = await service.LoadSnapshotAsync();

        var dataRequest = handler.Requests.First(request => !IsStatusRequest(request));
        var recoveryStart = long.Parse(GetQueryValue(dataRequest.RequestUri!, "start_timestamp"));
        var defaultStart = new DateTime(now.Year, now.Month, 1).AddMonths(-1);
        var expectedStart = offlineHours <= 7 * 24
            ? lastFetched.LocalDateTime.AddHours(-2)
            : defaultStart;
        if (expectedStart < defaultStart)
        {
            expectedStart = defaultStart;
        }

        Assert.Equal(new DateTimeOffset(expectedStart).ToUnixTimeSeconds(), recoveryStart);
        Assert.Equal(2000m, recovered.QuotaPerUnit);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("json")]
    public async Task LoadSnapshot_StatusFailureKeepsCachedQuotaPerUnit(string failure)
    {
        var failStatus = false;
        var handler = new RecordingHandler(request =>
        {
            if (!IsStatusRequest(request))
            {
                return JsonResponse("{\"success\":true,\"data\":[]}");
            }

            if (!failStatus)
            {
                return JsonResponse("{\"quota_per_unit\":1000}");
            }

            return failure switch
            {
                "http" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                "timeout" => throw new TaskCanceledException("HTTP request timed out"),
                _ => JsonResponse("not json")
            };
        });
        var settingsService = CreateSettingsService();
        using var client = new HttpClient(handler);
        var service = new UsageLogService(settingsService, client, Path.Combine(_folder, "cache"));
        await service.LoadSnapshotAsync();
        failStatus = true;

        var snapshot = await service.LoadSnapshotAsync();

        Assert.Equal(1000m, snapshot.QuotaPerUnit);
        Assert.DoesNotContain("读取 NewAPI 失败", snapshot.SourceMessage);
        Assert.Equal(1000m, service.GetCachedSnapshot()!.QuotaPerUnit);
    }

    [Fact]
    public async Task LoadSnapshot_InitialFailureDoesNotCreateSuccessfulCache()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var settingsService = CreateSettingsService();
        using var client = new HttpClient(handler);
        var cacheFolder = Path.Combine(_folder, "cache");
        var service = new UsageLogService(settingsService, client, cacheFolder);

        var snapshot = await service.LoadSnapshotAsync();

        Assert.Contains("读取 NewAPI 失败", snapshot.SourceMessage);
        Assert.Empty(snapshot.Events);
        Assert.Null(service.GetCachedSnapshot());
        Assert.False(Directory.Exists(cacheFolder));
    }

    [Fact]
    public async Task GetCachedSnapshot_AntigravityToggleKeepsNewApiHistoryAcrossRestarts()
    {
        var row = $"{{\"model_name\":\"gpt-test\",\"created_at\":{DateTimeOffset.Now.ToUnixTimeSeconds()},\"token_used\":100,\"count\":1,\"quota\":50}}";
        var handler = new RecordingHandler(request => IsStatusRequest(request)
            ? JsonResponse("{\"quota_per_unit\":1000}")
            : JsonResponse($"{{\"success\":true,\"data\":[{row}]}}"));
        var settingsService = CreateSettingsService();
        using var client = new HttpClient(handler);
        var cacheFolder = Path.Combine(_folder, "cache");
        var service = new UsageLogService(settingsService, client, cacheFolder);
        var initial = await service.LoadSnapshotAsync();

        foreach (var enabled in new[] { true, false })
        {
            settingsService.SetAntigravity(enabled);
            service = new UsageLogService(settingsService, client, cacheFolder);

            var cached = service.GetCachedSnapshot();

            Assert.NotNull(cached);
            Assert.Equal(initial.Events.ToArray(), cached.Events.ToArray());
            Assert.Equal(initial.QuotaPerUnit, cached.QuotaPerUnit);
        }

        settingsService.SetNewApi("https://newapi.example.com", "another-token", 0);
        Assert.Null(service.GetCachedSnapshot());
    }

    [Theory]
    [InlineData("/api/status")]
    [InlineData("/api/data/self")]
    public async Task LoadSnapshot_CallerCancellationPropagatesWithoutChangingCache(string cancelPath)
    {
        using var cancellation = new CancellationTokenSource();
        var cancelRequests = false;
        var handler = new RecordingHandler(request =>
        {
            if (cancelRequests && request.RequestUri!.AbsolutePath == cancelPath)
            {
                cancellation.Cancel();
                throw new TaskCanceledException("Caller canceled", null, cancellation.Token);
            }

            return IsStatusRequest(request)
                ? JsonResponse("{\"quota_per_unit\":1000}")
                : JsonResponse("{\"success\":true,\"data\":[]}");
        });
        var settingsService = CreateSettingsService();
        using var client = new HttpClient(handler);
        var cacheFolder = Path.Combine(_folder, "cache");
        var service = new UsageLogService(settingsService, client, cacheFolder);
        await service.LoadSnapshotAsync();
        var cachePath = Directory.GetFiles(cacheFolder, "usage-index-v*.json.gz").Single();
        var originalCache = File.ReadAllBytes(cachePath);
        cancelRequests = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LoadSnapshotAsync(cancellation.Token));

        Assert.Equal(originalCache, File.ReadAllBytes(cachePath));
    }

    [Fact]
    public async Task LoadSnapshot_PreCanceledTokenPropagatesWithoutConfiguredSource()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new UsageLogService(
            new AppSettingsService(Path.Combine(_folder, "settings")),
            cacheFolder: Path.Combine(_folder, "cache"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LoadSnapshotAsync(cancellation.Token));
    }

    [Fact]
    public async Task TestConnection_CallerCancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHandler(_ =>
        {
            cancellation.Cancel();
            throw new TaskCanceledException("Caller canceled", null, cancellation.Token);
        });
        var settingsService = CreateSettingsService();
        using var client = new HttpClient(handler);
        var service = new UsageLogService(settingsService, client, Path.Combine(_folder, "cache"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TestConnectionAsync(cancellation.Token));
    }

    [Fact]
    public async Task TestConnection_HttpTimeoutReturnsFailure()
    {
        var handler = new RecordingHandler(_ => throw new TaskCanceledException("HTTP request timed out"));
        var settingsService = CreateSettingsService();
        using var client = new HttpClient(handler);
        var service = new UsageLogService(settingsService, client, Path.Combine(_folder, "cache"));

        var result = await service.TestConnectionAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal("请求超时", result.Message);
    }

    [Fact]
    public void BuildSnapshot_AntigravityQuotaDoesNotCountAsTokensOrConsumption()
    {
        var quota = new AntigravityQuotaSnapshot(
            DateTimeOffset.Now,
            "Pro",
            1000m,
            250m,
            [new AntigravityModelQuota("gemini-test", "Gemini", 0.25m, DateTimeOffset.Now.AddHours(2))]);
        var snapshot = UsageLogService.BuildSnapshot([], antigravityQuota: quota);

        var estimate = new PricingService().Estimate(snapshot, UsagePeriod.Today);

        Assert.Same(quota, snapshot.AntigravityQuota);
        Assert.Empty(snapshot.Events);
        Assert.Empty(snapshot.Providers);
        Assert.Equal(0, snapshot.TotalFor(UsagePeriod.Today).TotalTokens);
        Assert.Equal(0, snapshot.TotalFor(UsagePeriod.Today).Quota);
        Assert.Equal(0m, estimate.EstimatedUsd);
        Assert.Equal(0, estimate.PricedRequestCount);
    }

    [Fact]
    public async Task GetCachedSnapshot_ComposesEnabledSourcesAndClearCacheRemovesBoth()
    {
        var now = DateTimeOffset.Now;
        var row = $"{{\"model_name\":\"gpt-test\",\"created_at\":{now.ToUnixTimeSeconds()},\"token_used\":100,\"count\":1,\"quota\":50}}";
        var handler = new RecordingHandler(request => IsStatusRequest(request)
            ? JsonResponse("{\"quota_per_unit\":1000}")
            : JsonResponse($"{{\"success\":true,\"data\":[{row}]}}"));
        var settingsService = CreateSettingsService();
        using var client = new HttpClient(handler);
        var cacheFolder = Path.Combine(_folder, "cache");
        var service = new UsageLogService(settingsService, client, cacheFolder);
        await service.LoadSnapshotAsync();
        var antigravityPath = Path.Combine(cacheFolder, "antigravity-usage-v1.json.gz");
        WriteCache(antigravityPath, JsonSerializer.SerializeToNode(new
        {
            Version = 1,
            LastFetchedUtc = now.ToUniversalTime(),
            Events = new[] { new TokenUsageEvent("antigravity:test", "Antigravity", now, 200, 50, 0, "gemini-test") }
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!.AsObject());
        settingsService.SetAntigravity(true);
        service = new UsageLogService(settingsService, client, cacheFolder);

        var combined = service.GetCachedSnapshot();

        Assert.NotNull(combined);
        Assert.Equal(350, combined.Events.Sum(item => item.InputTokens + item.OutputTokens));
        Assert.Equal(2, combined.Providers.Count);
        Assert.Equal(1000m, combined.QuotaPerUnit);
        Assert.Contains("本机历史缓存", combined.AntigravityUsageMessage);
        Assert.False(combined.AntigravityUsageIsComplete);
        settingsService.SetAntigravity(false);
        Assert.Equal("NewAPI", Assert.Single(service.GetCachedSnapshot()!.Events).Provider);
        settingsService.SetAntigravity(true);
        settingsService.SetNewApi(string.Empty, string.Empty, 0);
        Assert.Equal("Antigravity", Assert.Single(service.GetCachedSnapshot()!.Events).Provider);

        service.ClearCache();

        Assert.Null(service.GetCachedSnapshot());
        Assert.Empty(Directory.GetFiles(cacheFolder, "*.json.gz"));
    }

    [Fact]
    public async Task LoadSnapshot_AntigravityReplacesVisibleHistoryWithoutChangingFailedNewApiCache()
    {
        var revision = 0;
        var usageService = new AntigravityUsageService((method, request, _) =>
        {
            var cascade = $"cascade-{revision}";
            var result = method == "GetAllCascadeTrajectories"
                ? AntigravityTrajectories(cascade)
                : JsonSerializer.SerializeToElement(request).GetProperty("generatorMetadataOffset").GetInt32() == 0
                    ? AntigravityResponse(new { generatorMetadata = new[] { AntigravityRecord(100 * (revision + 1), 20, cascade) } })
                    : AntigravityResponse(new { });
            return Task.FromResult(result);
        });
        var row = $"{{\"model_name\":\"gpt-test\",\"created_at\":{DateTimeOffset.Now.ToUnixTimeSeconds()},\"token_used\":50,\"count\":1,\"quota\":50}}";
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/GetUserStatus", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            if (revision > 0)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return IsStatusRequest(request)
                ? JsonResponse("{\"quota_per_unit\":1000}")
                : JsonResponse($"{{\"success\":true,\"data\":[{row}]}}");
        });
        var settingsService = CreateSettingsService();
        settingsService.SetAntigravity(true);
        using var client = new HttpClient(handler);
        var cacheFolder = Path.Combine(_folder, "cache");
        var quotaService = CreateAntigravityQuotaService(client);
        var service = new UsageLogService(settingsService, client, cacheFolder, quotaService, usageService);
        var first = await service.LoadSnapshotAsync();
        var newApiCachePath = Directory.GetFiles(cacheFolder, "usage-index-v*.json.gz").Single();
        var newApiCache = File.ReadAllBytes(newApiCachePath);
        revision = 1;

        var second = await service.LoadSnapshotAsync();

        Assert.Equal(170, first.Events.Sum(item => item.InputTokens + item.OutputTokens));
        Assert.Equal(270, second.Events.Sum(item => item.InputTokens + item.OutputTokens));
        Assert.Equal(2, second.Events.Count);
        Assert.True(second.AntigravityUsageIsComplete);
        Assert.Null(second.AntigravityQuota);
        Assert.Contains("认证失败", second.AntigravityStatusMessage);
        Assert.Contains("读取 NewAPI 失败", second.SourceMessage);
        Assert.Equal(1000m, second.QuotaPerUnit);
        Assert.Equal(newApiCache, File.ReadAllBytes(newApiCachePath));
        var restored = new UsageLogService(settingsService, client, cacheFolder, quotaService, usageService).GetCachedSnapshot();
        Assert.NotNull(restored);
        Assert.Equal(second.Events.OrderBy(item => item.Id), restored.Events.OrderBy(item => item.Id));
    }

    [Fact]
    public async Task LoadSnapshot_PartialAntigravityReadKeepsLastCompleteCacheAndOnlyOfflineUsesIt()
    {
        var phase = 0;
        var usageService = new AntigravityUsageService((method, request, _) =>
        {
            if (phase == 3)
            {
                return Task.FromResult(new AntigravityRpcResult(null, "未找到正在运行的 Antigravity IDE 服务", null));
            }

            if (method == "GetAllCascadeTrajectories")
            {
                return Task.FromResult(phase == 4 ? AntigravityResponse(new { }) : AntigravityTrajectories($"cascade-{phase}"));
            }

            if (JsonSerializer.SerializeToElement(request).GetProperty("generatorMetadataOffset").GetInt32() != 0)
            {
                return Task.FromResult(AntigravityResponse(new { }));
            }

            var missingUsage = new { chatModel = new { responseModel = "gemini-test" } };
            object[] records = phase switch
            {
                0 => [AntigravityRecord(100, 20, "first")],
                1 => [AntigravityRecord(200, 40, "partial"), missingUsage],
                _ => [missingUsage]
            };
            return Task.FromResult(AntigravityResponse(new { generatorMetadata = records }));
        });
        var settingsService = new AppSettingsService(Path.Combine(_folder, "settings"));
        settingsService.SetAntigravity(true);
        using var client = new HttpClient(new RecordingHandler(_ => JsonResponse("{}")));
        var quotaService = CreateAntigravityQuotaService(client);
        var cacheFolder = Path.Combine(_folder, "cache");
        var service = new UsageLogService(settingsService, client, cacheFolder, quotaService, usageService);
        var first = await service.LoadSnapshotAsync();
        var cachePath = Path.Combine(cacheFolder, "antigravity-usage-v1.json.gz");
        var originalCache = File.ReadAllBytes(cachePath);
        phase = 1;

        var partial = await service.LoadSnapshotAsync();

        Assert.False(partial.AntigravityUsageIsComplete);
        Assert.Equal(240, partial.Events.Sum(item => item.InputTokens + item.OutputTokens));
        Assert.Contains("统计不完整", partial.AntigravityUsageMessage);
        Assert.DoesNotContain("本机历史缓存", partial.AntigravityUsageMessage);
        Assert.Equal(originalCache, File.ReadAllBytes(cachePath));
        phase = 2;

        var missing = await service.LoadSnapshotAsync();

        Assert.False(missing.AntigravityUsageIsComplete);
        Assert.Empty(missing.Events);
        Assert.Contains("未提供 usage", missing.AntigravityUsageMessage);
        Assert.DoesNotContain("本机历史缓存", missing.AntigravityUsageMessage);
        Assert.Equal(originalCache, File.ReadAllBytes(cachePath));
        phase = 3;
        service = new UsageLogService(settingsService, client, cacheFolder, quotaService, usageService);

        var offline = await service.LoadSnapshotAsync();

        Assert.False(offline.AntigravityUsageIsComplete);
        Assert.Equal(first.Events.ToArray(), offline.Events.ToArray());
        Assert.Contains("本机历史缓存", offline.AntigravityUsageMessage);
        Assert.Equal(originalCache, File.ReadAllBytes(cachePath));
        Assert.Empty(Directory.GetFiles(cacheFolder, "usage-index-v*.json.gz"));
        phase = 4;

        var empty = await service.LoadSnapshotAsync();

        Assert.True(empty.AntigravityUsageIsComplete);
        Assert.Empty(empty.Events);
        Assert.Empty(new UsageLogService(settingsService, client, cacheFolder, quotaService, usageService).GetCachedSnapshot()!.Events);
    }

    [Fact]
    public async Task LoadSnapshot_AntigravityCancellationDoesNotReturnOrOverwriteCachedSnapshot()
    {
        using var cancellation = new CancellationTokenSource();
        var cancelRead = false;
        var usageService = new AntigravityUsageService((method, request, token) =>
        {
            if (cancelRead)
            {
                cancellation.Cancel();
                return Task.FromCanceled<AntigravityRpcResult>(token);
            }

            var response = method == "GetAllCascadeTrajectories"
                ? AntigravityTrajectories("cascade-first")
                : JsonSerializer.SerializeToElement(request).GetProperty("generatorMetadataOffset").GetInt32() == 0
                    ? AntigravityResponse(new { generatorMetadata = new[] { AntigravityRecord(100, 20, "first") } })
                    : AntigravityResponse(new { });
            return Task.FromResult(response);
        });
        var settingsService = new AppSettingsService(Path.Combine(_folder, "settings"));
        settingsService.SetAntigravity(true);
        using var client = new HttpClient(new RecordingHandler(_ => JsonResponse("{}")));
        var cacheFolder = Path.Combine(_folder, "cache");
        var service = new UsageLogService(settingsService, client, cacheFolder, CreateAntigravityQuotaService(client), usageService);
        await service.LoadSnapshotAsync();
        var cachePath = Path.Combine(cacheFolder, "antigravity-usage-v1.json.gz");
        var originalCache = File.ReadAllBytes(cachePath);
        cancelRead = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LoadSnapshotAsync(cancellation.Token));

        Assert.Equal(originalCache, File.ReadAllBytes(cachePath));
    }

    private static AntigravityQuotaService CreateAntigravityQuotaService(HttpClient client) =>
        new(new AntigravityLocalClient(
            client,
            _ => Task.FromResult<IReadOnlyList<(int ProcessId, string? CommandLine)>>(
                [(1001, "language_server_windows_x64.exe --app_data_dir antigravity --csrf_token test-token")]),
            _ => Task.FromResult("TCP 127.0.0.1:53500 0.0.0.0:0 LISTENING 1001")));

    private static AntigravityRpcResult AntigravityResponse(object value) =>
        new(JsonSerializer.SerializeToElement(value), "读取成功", "http://127.0.0.1:53500");

    private static AntigravityRpcResult AntigravityTrajectories(string cascade) => AntigravityResponse(new
    {
        trajectorySummaries = new Dictionary<string, object>
        {
            [cascade] = new
            {
                trajectoryId = $"{cascade}-trajectory",
                stepCount = 1,
                lastModifiedTime = "2026-09-14T01:01:00Z",
                status = "CASCADE_RUN_STATUS_IDLE"
            }
        }
    });

    private static object AntigravityRecord(long input, long output, string responseId) => new
    {
        chatModel = new
        {
            responseModel = "gemini-test",
            chatStartMetadata = new { createdAt = "2026-09-14T01:00:00Z" },
            usage = new { inputTokens = input, outputTokens = output, responseId }
        }
    };

    private AppSettingsService CreateSettingsService()
    {
        var service = new AppSettingsService(Path.Combine(_folder, "settings"));
        service.SetNewApi("https://newapi.example.com", "test-token", 0);
        return service;
    }

    private static bool IsStatusRequest(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.EndsWith("/api/status", StringComparison.Ordinal);

    /// <summary>
    /// 读取真实持久化缓存，便于在回归测试中模拟长时间离线后的恢复。
    /// </summary>
    private static JsonObject ReadCache(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonNode.Parse(gzip)!.AsObject();
    }

    private static void WriteCache(string path, JsonObject cache)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        JsonSerializer.Serialize(gzip, cache);
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
