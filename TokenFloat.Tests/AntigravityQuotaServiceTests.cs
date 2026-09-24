using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using TokenFloat.Services;

namespace TokenFloat.Tests;

public sealed class AntigravityQuotaServiceTests
{
    [Fact]
    public void ParseUserStatus_ReadsNestedCreditsAndModelQuotas()
    {
        using var document = JsonDocument.Parse("""
            {
              "userStatus": {
                "planStatus": {
                  "planInfo": {
                    "planName": "Pro",
                    "monthlyPromptCredits": "50000"
                  },
                  "availablePromptCredits": 1250
                },
                "cascadeModelConfigData": {
                  "clientModelConfigs": [
                    {
                      "label": "Preferred",
                      "modelId": "model-preferred",
                      "modelOrAlias": { "model": "alias-preferred" },
                      "quotaInfo": {
                        "remainingFraction": 0.75,
                        "resetTime": "2026-01-02T03:04:05Z"
                      }
                    },
                    {
                      "label": "Alias only",
                      "modelOrAlias": { "model": "model-alias" },
                      "quotaInfo": {
                        "remainingFraction": "0.25",
                        "resetTime": "2026-01-03T03:04:05Z"
                      }
                    }
                  ]
                }
              }
            }
            """);

        var snapshot = AntigravityQuotaService.ParseUserStatus(document.RootElement);

        Assert.NotNull(snapshot);
        Assert.Equal("Pro", snapshot.PlanName);
        Assert.Equal(50_000m, snapshot.MonthlyPromptCredits);
        Assert.Equal(1_250m, snapshot.AvailablePromptCredits);
        Assert.Equal(2, snapshot.Models.Count);
        Assert.Equal("model-preferred", snapshot.Models[0].Model);
        Assert.Equal("Preferred", snapshot.Models[0].Label);
        Assert.Equal(0.75m, snapshot.Models[0].RemainingFraction);
        Assert.Equal(
            DateTimeOffset.Parse("2026-01-02T03:04:05Z").ToLocalTime(),
            snapshot.Models[0].ResetAt);
        Assert.Equal("model-alias", snapshot.Models[1].Model);
        Assert.Equal(0.25m, snapshot.Models[1].RemainingFraction);
    }

    [Fact]
    public void ParseProcesses_FiltersByAntigravityDataDirectoryAndCsrfToken()
    {
        var processes = AntigravityQuotaService.ParseProcesses([
            (1001, "language_server_windows_x64.exe --app_data_dir antigravity-ide --csrf_token token-a --extension_server_port 53500"),
            (1002, "language_server_windows_x64.exe --app_data_dir antigravity --csrf_token=token-b --extension_server_port=53501"),
            (1003, "language_server_windows_x64.exe --app_data_dir antigravity-ide --extension_server_port 53502"),
            (1004, "language_server_windows_x64.exe --app_data_dir vscode --csrf_token token-c --extension_server_port 53503")
        ]);

        Assert.Equal(2, processes.Count);
        Assert.Collection(
            processes,
            item =>
            {
                Assert.Equal(1001, item.ProcessId);
                Assert.Equal(53500, item.ExtensionServerPort);
                Assert.Equal("token-a", item.CsrfToken);
            },
            item =>
            {
                Assert.Equal(1002, item.ProcessId);
                Assert.Equal(53501, item.ExtensionServerPort);
                Assert.Equal("token-b", item.CsrfToken);
            });
    }

    [Theory]
    [InlineData("""{"planInfo":{"planName":"Pro","monthlyPromptCredits":50000}}""")]
    [InlineData("""{"userStatus":{"planInfo":{"planName":"Pro","monthlyPromptCredits":"50000"}}}""")]
    public void ParseUserStatus_AcceptsPlanInfoAtSupportedProtocolLevels(string json)
    {
        using var document = JsonDocument.Parse(json);

        var snapshot = AntigravityQuotaService.ParseUserStatus(document.RootElement);

        Assert.NotNull(snapshot);
        Assert.Equal("Pro", snapshot.PlanName);
        Assert.Equal(50_000m, snapshot.MonthlyPromptCredits);
    }

    [Fact]
    public void ParseUserStatus_PreservesUnknownValuesAndReadsAliasAndTimestamp()
    {
        using var document = JsonDocument.Parse("""
            {
              "cascadeModelConfigData": {
                "clientModelConfigs": [
                  null,
                  {
                    "modelId": "",
                    "modelOrAlias": { "alias": "MODEL_CLAUDE" },
                    "quotaInfo": {
                      "remainingFraction": 0,
                      "resetTime": { "seconds": "1767323045", "nanos": 123456700 }
                    }
                  },
                  {
                    "modelOrAlias": { "model": 42 },
                    "quotaInfo": {
                      "remainingFraction": 1.1,
                      "resetTime": { "seconds": "9223372036854775807" }
                    }
                  },
                  {
                    "modelId": "unknown",
                    "quotaInfo": {
                      "remainingFraction": "NaN",
                      "resetTime": "not-a-time"
                    }
                  }
                ]
              }
            }
            """);

        var snapshot = AntigravityQuotaService.ParseUserStatus(document.RootElement);

        Assert.NotNull(snapshot);
        Assert.Equal(3, snapshot.Models.Count);
        Assert.Equal("MODEL_CLAUDE", snapshot.Models[0].Model);
        Assert.Equal(0m, snapshot.Models[0].RemainingFraction);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1767323045).AddTicks(1234567).ToLocalTime(), snapshot.Models[0].ResetAt);
        Assert.Equal("42", snapshot.Models[1].Model);
        Assert.Null(snapshot.Models[1].RemainingFraction);
        Assert.Null(snapshot.Models[1].ResetAt);
        Assert.Null(snapshot.Models[2].RemainingFraction);
        Assert.Null(snapshot.Models[2].ResetAt);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"userStatus":{"planStatus":{"planInfo":{}}}}""")]
    public void ParseUserStatus_DoesNotInventQuotaForEmptyResponses(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Null(AntigravityQuotaService.ParseUserStatus(document.RootElement));
    }

    [Theory]
    [InlineData("""{"resetTime":"2026-01-02T03:04:05Z"}""", true)]
    [InlineData("""{"remainingFraction":null,"resetTime":"2026-01-02T03:04:05Z"}""", false)]
    public void ParseUserStatus_DistinguishesProto3DefaultZeroFromInvalidQuota(string quota, bool usesDefault)
    {
        using var document = JsonDocument.Parse($$$"""
            {"cascadeModelConfigData":{"clientModelConfigs":[{"modelId":"model-a","quotaInfo": {{{quota}}} }]}}
            """);

        var snapshot = AntigravityQuotaService.ParseUserStatus(document.RootElement);

        Assert.NotNull(snapshot);
        var model = Assert.Single(snapshot.Models);
        Assert.Equal(usesDefault ? 0m : (decimal?)null, model.RemainingFraction);
        Assert.NotNull(model.ResetAt);
    }

    [Fact]
    public void ParseProcesses_ReadsQuotedArgumentsAndRejectsInvalidPortRanges()
    {
        var processes = AntigravityQuotaService.ParseProcesses([
            (1001, """
                "C:\Program Files\Antigravity\language_server.exe" "--app_data_dir=C:\User Data\Antigravity" --csrf_token="token-a" --extension_server_port=65535
                """),
            (1002, """language_server.exe --app_data_dir "C:\User Data\Antigravity" --csrf_token token-b --extension_server_port 65536"""),
            (1003, "language_server.exe --app_data_dir antigravity --csrf_token token-c --extension_server_port -1"),
            (1004, "language_server.exe --app_data_dir antigravity --csrf_token --extension_server_port 50000"),
            (0, "language_server.exe --app_data_dir antigravity --csrf_token token-d")
        ]);

        Assert.Equal(3, processes.Count);
        Assert.Equal(65535, processes[0].ExtensionServerPort);
        Assert.Equal("token-a", processes[0].CsrfToken);
        Assert.Null(processes[1].ExtensionServerPort);
        Assert.Null(processes[2].ExtensionServerPort);
    }
}

public sealed class AntigravityLocalClientTests
{
    private const string QuotaJson = """{"userStatus":{"planInfo":{"planName":"Pro"}}}""";
    private const string DefaultListeners = "TCP 127.0.0.1:53500 0.0.0.0:0 LISTENING 1001";
    private const string TwoProcessListeners = """
        TCP 127.0.0.1:53500 0.0.0.0:0 LISTENING 1001
        TCP 127.0.0.1:53600 0.0.0.0:0 LISTENING 2002
        """;

    [Fact]
    public async Task ReadAsync_ContinuesAfterAnUnresponsivePort()
    {
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.Port == 53500)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return JsonResponse(QuotaJson);
        });
        using var http = new HttpClient(handler);
        var local = CreateClient(http, """
            TCP 127.0.0.1:53500 0.0.0.0:0 LISTENING 1001
            TCP 127.0.0.1:53501 0.0.0.0:0 LISTENING 1001
            """, requestTimeout: TimeSpan.FromMilliseconds(50));

        var result = await new AntigravityQuotaService(local).ReadAsync();

        Assert.NotNull(result.Snapshot);
        Assert.Equal("Pro", result.Snapshot.PlanName);
        Assert.Equal([53500, 53500, 53501], handler.Requests.Select(item => item.Uri.Port));
    }

    [Theory]
    [InlineData("<html>wrong protocol</html>")]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task ReadAsync_ContinuesAfterAnInvalidJsonResponse(string invalidJson)
    {
        var handler = new RecordingHandler((request, _) =>
            Task.FromResult(JsonResponse(request.RequestUri!.Scheme == "https" ? invalidJson : QuotaJson)));
        using var http = new HttpClient(handler);

        var result = await new AntigravityQuotaService(CreateClient(http)).ReadAsync();

        Assert.NotNull(result.Snapshot);
        Assert.Equal("http", new Uri(result.Endpoint!).Scheme);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ReadAsync_PreservesAuthenticationFailureWhenAnotherCandidateCannotConnect()
    {
        var handler = new RecordingHandler((request, _) => request.RequestUri!.Scheme == "https"
            ? Task.FromResult(JsonResponse("""{"code":"unauthenticated"}""", HttpStatusCode.Unauthorized))
            : throw new HttpRequestException("connection refused"));
        using var http = new HttpClient(handler);

        var result = await new AntigravityQuotaService(CreateClient(http)).ReadAsync();

        Assert.Null(result.Snapshot);
        Assert.Contains("401", result.Message);
        Assert.Contains("认证失败", result.Message);
        Assert.DoesNotContain("未找到正在运行", result.Message);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ReadAsync_SendsRpcOnlyToListenersOwnedByTheLanguageServer()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(JsonResponse(QuotaJson)));
        using var http = new HttpClient(handler);
        var local = CreateClient(http, """
            TCP 127.0.0.1:53499 0.0.0.0:0 LISTENING 2002
            TCP 10.0.0.1:53500 0.0.0.0:0 LISTENING 1001
            TCP 127.0.0.1:53501 127.0.0.1:1000 ESTABLISHED 1001
            TCP 127.0.0.1:65536 0.0.0.0:0 LISTENING 1001
            TCP 127.0.0.1:0 0.0.0.0:0 LISTENING 1001
            UDP 127.0.0.1:53502 0.0.0.0:0 LISTENING 1001
            TCP [::1]:53503 [::]:0 LISTENING 1001
            """);

        var result = await new AntigravityQuotaService(local).ReadAsync();

        Assert.NotNull(result.Snapshot);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.True(request.Uri.IsLoopback);
        Assert.Equal(53503, request.Uri.Port);
        Assert.Equal("/exa.language_server_pb.LanguageServerService/GetUserStatus", request.Uri.AbsolutePath);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal("1", request.ConnectVersion);
        Assert.Equal("test-token", request.CsrfToken);
        using var body = JsonDocument.Parse(request.Body);
        var metadata = body.RootElement.GetProperty("metadata");
        Assert.Equal("antigravity", metadata.GetProperty("ideName").GetString());
        Assert.Equal("antigravity", metadata.GetProperty("extensionName").GetString());
        Assert.Equal("en", metadata.GetProperty("locale").GetString());
    }

    [Fact]
    public async Task ReadAsync_DoesNotUseAnExtensionPortWithoutAConfirmedListener()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(JsonResponse(QuotaJson)));
        using var http = new HttpClient(handler);
        var local = CreateClient(http, "TCP 127.0.0.1:53499 0.0.0.0:0 LISTENING 2002");

        var result = await new AntigravityQuotaService(local).ReadAsync();

        Assert.Null(result.Snapshot);
        Assert.Contains("尚未发现本地监听端口", result.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CallAsync_ReusesTheEndpointAcrossMethodsAndRediscoversRotatedCredentials()
    {
        var discoveryCount = 0;
        var listenerReadCount = 0;
        var rotated = false;
        var handler = new RecordingHandler((request, _) =>
        {
            var token = request.Headers.GetValues("X-Codeium-Csrf-Token").Single();
            return Task.FromResult(rotated && token == "old-token"
                ? JsonResponse("""{"code":"unauthenticated"}""", HttpStatusCode.Unauthorized)
                : JsonResponse("""{"ok":true}"""));
        });
        using var http = new HttpClient(handler);
        var local = new AntigravityLocalClient(
            http,
            _ =>
            {
                discoveryCount++;
                return ProcessRows(rotated ? "new-token" : "old-token");
            },
            _ =>
            {
                listenerReadCount++;
                return Task.FromResult(DefaultListeners);
            });

        await local.CallAsync("GetUserStatus", new { });
        var second = await local.CallAsync("GetAllCascadeTrajectories", new { });
        Assert.True(second.Data!.Value.GetProperty("ok").GetBoolean());
        Assert.Equal(1, discoveryCount);
        rotated = true;
        var third = await local.CallAsync("GetCascadeTrajectoryGeneratorMetadata", new { cascadeId = "conversation-1" });

        Assert.NotNull(third.Data);
        Assert.Equal(2, discoveryCount);
        Assert.Equal(2, listenerReadCount);
        Assert.Equal(["old-token", "old-token", "old-token", "new-token"], handler.Requests.Select(item => item.CsrfToken));
        Assert.EndsWith("/GetAllCascadeTrajectories", handler.Requests[1].Uri.AbsolutePath);
        using var body = JsonDocument.Parse(handler.Requests[^1].Body);
        Assert.Equal("conversation-1", body.RootElement.GetProperty("cascadeId").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "invalid_argument")]
    [InlineData(HttpStatusCode.NotImplemented, "unimplemented")]
    [InlineData(HttpStatusCode.NotFound, "not_found")]
    public async Task CallAsync_DoesNotRediscoverForApplicationErrorsAtAKnownEndpoint(HttpStatusCode status, string code)
    {
        var discoveryCount = 0;
        var handler = new RecordingHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/GetUserStatus", StringComparison.Ordinal)
                ? JsonResponse(QuotaJson)
                : JsonResponse(JsonSerializer.Serialize(new { code }), status)));
        using var http = new HttpClient(handler);
        var local = new AntigravityLocalClient(http, _ =>
        {
            discoveryCount++;
            return ProcessRows();
        }, _ => Task.FromResult(DefaultListeners));
        await local.CallAsync("GetUserStatus", new { });

        var result = await local.CallAsync("GetCascadeTrajectoryGeneratorMetadata", new { cascadeId = "missing" });

        Assert.Null(result.Data);
        Assert.Contains(((int)status).ToString(), result.Message);
        Assert.Equal(1, discoveryCount);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CallAsync_RediscoversWhenTheCachedPortNoLongerServesRpc()
    {
        var moved = false;
        var discoveryCount = 0;
        var handler = new RecordingHandler((request, _) => Task.FromResult(moved && request.RequestUri!.Port == 53500
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : JsonResponse("""{"ok":true}""")));
        using var http = new HttpClient(handler);
        var local = new AntigravityLocalClient(http, _ =>
        {
            discoveryCount++;
            return ProcessRows();
        }, _ => Task.FromResult(moved
            ? "TCP 127.0.0.1:53501 0.0.0.0:0 LISTENING 1001"
            : DefaultListeners));
        await local.CallAsync("GetUserStatus", new { });
        moved = true;

        var result = await local.CallAsync("GetUserStatus", new { });

        Assert.NotNull(result.Data);
        Assert.Equal(2, discoveryCount);
        Assert.Equal([53500, 53500, 53501], handler.Requests.Select(item => item.Uri.Port));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallAsync_PropagatesCallerCancellationDuringDiscoveryOrHttp(bool cancelDuringDiscovery)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse(QuotaJson);
        });
        using var http = new HttpClient(handler);
        var local = new AntigravityLocalClient(http, async cancellationToken =>
        {
            if (cancelDuringDiscovery)
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return await ProcessRows();
        }, _ => Task.FromResult(DefaultListeners));
        using var cancellation = new CancellationTokenSource();

        var reading = local.CallAsync("GetUserStatus", new { }, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.Equal(cancelDuringDiscovery ? 0 : 1, handler.Requests.Count);
    }

    [Fact]
    public async Task CallAsync_HasAnOverallBudgetAcrossRequests()
    {
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse(QuotaJson);
        });
        using var http = new HttpClient(handler);
        var local = CreateClient(http, requestTimeout: TimeSpan.FromSeconds(10), totalTimeout: TimeSpan.FromMilliseconds(100));

        var result = await local.CallAsync("GetUserStatus", new { }).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(result.Data);
        Assert.Contains("超时", result.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CallAsync_BoundsDiscoveryEvenWhenItDoesNotHonorCancellation()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<(int ProcessId, string? CommandLine)>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler((_, _) => Task.FromResult(JsonResponse(QuotaJson)));
        using var http = new HttpClient(handler);
        var local = new AntigravityLocalClient(http, _ => pending.Task, _ => Task.FromResult(DefaultListeners),
            totalTimeout: TimeSpan.FromMilliseconds(50));
        try
        {
            var result = await local.CallAsync("GetUserStatus", new { }).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Null(result.Data);
            Assert.Contains("超时", result.Message);
            Assert.Empty(handler.Requests);
        }
        finally
        {
            pending.TrySetResult([]);
        }
    }

    [Fact]
    public async Task CallAllAsync_ReadsEveryProcessOnceEvenWhenTheFirstServiceHasNoHistory()
    {
        var handler = new RecordingHandler((request, _) => Task.FromResult(JsonResponse(
            request.RequestUri!.Port == 53500 ? "{}" : """{"trajectorySummaries":{"conversation-1":{}}}""")));
        using var http = new HttpClient(handler);
        var local = new AntigravityLocalClient(http, _ => TwoProcessRows(), _ => Task.FromResult("""
            TCP 127.0.0.1:53500 0.0.0.0:0 LISTENING 1001
            TCP 127.0.0.1:53501 0.0.0.0:0 LISTENING 1001
            TCP 127.0.0.1:53600 0.0.0.0:0 LISTENING 2002
            TCP 127.0.0.1:53601 0.0.0.0:0 LISTENING 2002
            """));

        var results = await local.CallAllAsync("GetAllCascadeTrajectories", new { excludeSubtrajectories = false });

        Assert.Equal(2, results.Count);
        Assert.Empty(results[0].Data!.Value.EnumerateObject());
        Assert.True(results[1].Data!.Value.GetProperty("trajectorySummaries").TryGetProperty("conversation-1", out _));
        Assert.Equal([53500, 53600], handler.Requests.Select(item => item.Uri.Port));
        Assert.Equal(["test-token", "second-token"], handler.Requests.Select(item => item.CsrfToken));
    }

    [Fact]
    public async Task CallAllAsync_ReportsFailedProcessesAlongsideSuccessfulOnes()
    {
        var handler = new RecordingHandler((request, _) => Task.FromResult(request.RequestUri!.Port == 53500
            ? JsonResponse("{}")
            : JsonResponse("""{"code":"unauthenticated"}""", HttpStatusCode.Unauthorized)));
        using var http = new HttpClient(handler);
        var local = new AntigravityLocalClient(http, _ => TwoProcessRows(), _ => Task.FromResult(TwoProcessListeners));

        var results = await local.CallAllAsync("GetAllCascadeTrajectories", new { });

        Assert.Equal(2, results.Count);
        Assert.NotNull(results[0].Data);
        Assert.Null(results[1].Data);
        Assert.Contains("401", results[1].Message);
        Assert.Equal([53500, 53600, 53600], handler.Requests.Select(item => item.Uri.Port));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallAllAsync_DoesNotReportEmptySuccessWhenDiscoveryCannotFindAService(bool fails)
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(JsonResponse("{}")));
        using var http = new HttpClient(handler);
        var local = new AntigravityLocalClient(http,
            _ => fails ? throw new IOException("discovery failed") : Task.FromResult<IReadOnlyList<(int, string?)>>([]),
            _ => throw new InvalidOperationException("There are no processes to inspect."));

        var results = await local.CallAllAsync("GetAllCascadeTrajectories", new { });

        var result = Assert.Single(results);
        Assert.Null(result.Data);
        Assert.Contains(fails ? "发现 Antigravity 本地服务失败" : "未找到正在运行", result.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CallAllAsync_RediscoversNewProcessesWhileReusingVerifiedEndpoints()
    {
        var secondStarted = false;
        var discoveries = 0;
        var handler = new RecordingHandler((_, _) => Task.FromResult(JsonResponse("{}")));
        using var http = new HttpClient(handler);
        var local = new AntigravityLocalClient(http, _ =>
        {
            discoveries++;
            return secondStarted ? TwoProcessRows() : ProcessRows();
        }, _ => Task.FromResult(TwoProcessListeners));
        Assert.Single(await local.CallAllAsync("GetAllCascadeTrajectories", new { }));
        secondStarted = true;

        var results = await local.CallAllAsync("GetAllCascadeTrajectories", new { });

        Assert.Equal(2, results.Count);
        Assert.Equal(2, discoveries);
        Assert.Equal([53500, 53500, 53600], handler.Requests.Select(item => item.Uri.Port));
    }

    [Fact]
    public async Task CallOnEndpointAsync_KeepsMetadataOnItsSourceAndPreservesTheQuotaConnection()
    {
        var discoveries = 0;
        var handler = new RecordingHandler((request, _) => Task.FromResult(JsonResponse(
            JsonSerializer.Serialize(new { server = request.RequestUri!.Port }))));
        using var http = new HttpClient(handler);
        var local = new AntigravityLocalClient(http, _ =>
        {
            discoveries++;
            return TwoProcessRows();
        }, _ => Task.FromResult(TwoProcessListeners));
        await local.CallAsync("GetUserStatus", new { });
        var lists = await local.CallAllAsync("GetAllCascadeTrajectories", new { });

        var metadata = await local.CallOnEndpointAsync(lists[1].Endpoint!, "GetCascadeTrajectoryGeneratorMetadata", new { cascadeId = "conversation-1" });
        var quota = await local.CallAsync("GetUserStatus", new { });

        Assert.Equal(53600, metadata.Data!.Value.GetProperty("server").GetInt32());
        Assert.Equal(53500, quota.Data!.Value.GetProperty("server").GetInt32());
        Assert.Equal(2, discoveries);
        Assert.Equal([53500, 53500, 53600, 53600, 53500], handler.Requests.Select(item => item.Uri.Port));
        Assert.Equal("second-token", handler.Requests[3].CsrfToken);
        Assert.EndsWith("/GetCascadeTrajectoryGeneratorMetadata", handler.Requests[3].Uri.AbsolutePath);
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("https://127.0.0.1:53501/")]
    [InlineData("https://user:password@127.0.0.1:53500/")]
    public async Task CallOnEndpointAsync_RejectsUnverifiedEndpointsWithoutSendingCredentials(string endpoint)
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(JsonResponse("{}")));
        using var http = new HttpClient(handler);
        var local = CreateClient(http);
        await local.CallAllAsync("GetAllCascadeTrajectories", new { });

        var result = await local.CallOnEndpointAsync(endpoint, "GetCascadeTrajectoryGeneratorMetadata", new { });

        Assert.Null(result.Data);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CallOnEndpointAsync_DoesNotSwitchServicesAfterConnectionFailure()
    {
        var failSecond = false;
        var discoveries = 0;
        var handler = new RecordingHandler((request, _) => failSecond && request.RequestUri!.Port == 53600
            ? throw new HttpRequestException("connection refused")
            : Task.FromResult(JsonResponse("{}")));
        using var http = new HttpClient(handler);
        var local = new AntigravityLocalClient(http, _ =>
        {
            discoveries++;
            return TwoProcessRows();
        }, _ => Task.FromResult(TwoProcessListeners));
        var lists = await local.CallAllAsync("GetAllCascadeTrajectories", new { });
        failSecond = true;

        var result = await local.CallOnEndpointAsync(lists[1].Endpoint!, "GetCascadeTrajectoryGeneratorMetadata", new { });

        Assert.Null(result.Data);
        Assert.Contains("无法连接", result.Message);
        Assert.Equal(1, discoveries);
        Assert.Equal([53500, 53600, 53600], handler.Requests.Select(item => item.Uri.Port));
    }

    [Fact]
    public async Task CallAllAsync_PreservesEarlierSuccessWhenItsOverallBudgetExpires()
    {
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.Port == 53600)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return JsonResponse("{}");
        });
        using var http = new HttpClient(handler);
        var local = new AntigravityLocalClient(http, _ => TwoProcessRows(), _ => Task.FromResult(TwoProcessListeners),
            requestTimeout: TimeSpan.FromSeconds(10), totalTimeout: TimeSpan.FromMilliseconds(100));

        var results = await local.CallAllAsync("GetAllCascadeTrajectories", new { }).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, results.Count);
        Assert.NotNull(results[0].Data);
        Assert.Null(results[1].Data);
        Assert.Contains("超时", results[1].Message);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultiServiceCalls_PropagateCallerCancellation(bool targetEndpoint)
    {
        var block = false;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            if (block)
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return JsonResponse("{}");
        });
        using var http = new HttpClient(handler);
        var local = CreateClient(http);
        var lists = await local.CallAllAsync("GetAllCascadeTrajectories", new { });
        block = true;
        using var cancellation = new CancellationTokenSource();
        Task reading = targetEndpoint
            ? local.CallOnEndpointAsync(lists[0].Endpoint!, "GetCascadeTrajectoryGeneratorMetadata", new { }, cancellation.Token)
            : local.CallAllAsync("GetAllCascadeTrajectories", new { }, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void CreateHttpClientHandler_KeepsCredentialsLocalAndLimitsCertificateExceptionsToLoopback()
    {
        using var handler = AntigravityLocalClient.CreateHttpClientHandler();
        using var local = new HttpRequestMessage(HttpMethod.Post, "https://127.0.0.1:53500/");
        using var remote = new HttpRequestMessage(HttpMethod.Post, "https://example.com/");

        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.True(handler.ServerCertificateCustomValidationCallback!(local, null, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(handler.ServerCertificateCustomValidationCallback!(remote, null, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(handler.ServerCertificateCustomValidationCallback!(remote, null, null, SslPolicyErrors.None));
    }

    private static AntigravityLocalClient CreateClient(HttpClient http, string listeners = DefaultListeners, TimeSpan? requestTimeout = null, TimeSpan? totalTimeout = null) =>
        new(http, _ => ProcessRows(), _ => Task.FromResult(listeners), requestTimeout, totalTimeout);

    private static Task<IReadOnlyList<(int ProcessId, string? CommandLine)>> ProcessRows(string csrfToken = "test-token") =>
        Task.FromResult<IReadOnlyList<(int ProcessId, string? CommandLine)>>([
            (1001, $"language_server_windows_x64.exe --app_data_dir antigravity --csrf_token {csrfToken} --extension_server_port 53499")
        ]);

    private static Task<IReadOnlyList<(int ProcessId, string? CommandLine)>> TwoProcessRows() =>
        Task.FromResult<IReadOnlyList<(int ProcessId, string? CommandLine)>>([
            (1001, "language_server_windows_x64.exe --app_data_dir antigravity --csrf_token test-token"),
            (2002, "language_server_windows_x64.exe --app_data_dir antigravity --csrf_token second-token")
        ]);

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? ContentType, string? ConnectVersion, string? CsrfToken, string Body);

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Content?.Headers.ContentType?.MediaType,
                request.Headers.GetValues("Connect-Protocol-Version").Single(),
                request.Headers.GetValues("X-Codeium-Csrf-Token").Single(),
                body));
            return await send(request, cancellationToken);
        }
    }
}
