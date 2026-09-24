using System.Text.Json;
using TokenFloat.Services;

namespace TokenFloat.Tests;

public sealed class AntigravityUsageServiceTests
{
    private const string Endpoint = "https://127.0.0.1:43210/exa.language_server_pb.LanguageServerService/GetAllCascadeTrajectories";
    private const string Record = """
        {
          "stepIndices": [1, 2, 3],
          "executionId": "execution-a",
          "chatModel": {
            "responseModel": "gemini-test",
            "chatStartMetadata": { "createdAt": "2026-09-14T01:00:00Z" },
            "usage": {
              "inputTokens": "1200",
              "outputTokens": "300",
              "cacheReadTokens": "200",
              "cacheWriteTokens": "100",
              "thinkingOutputTokens": "100",
              "responseOutputTokens": "200",
              "responseId": "response-a"
            }
          }
        }
        """;

    [Fact]
    public void ParseRecord_NormalizesInputOnceAndPreservesRawCounters()
    {
        var result = Parse(Record);

        Assert.Null(result.Problem);
        var item = Assert.IsType<TokenFloat.Models.TokenUsageEvent>(result.Event);
        Assert.Equal("Antigravity", item.Provider);
        Assert.Equal("gemini-test", item.Model);
        Assert.Equal(1500, item.InputTokens);
        Assert.Equal(1200, item.ReportedInputTokens);
        Assert.Equal(300, item.OutputTokens);
        Assert.Equal(200, item.CachedInputTokens);
        Assert.Equal(100, item.CacheWriteInputTokens);
        Assert.Equal(0, item.CacheWriteOneHourInputTokens);
        Assert.Equal(0, item.Quota);
        Assert.Equal(1, item.RequestCount);
        Assert.Equal(DateTimeOffset.Parse("2026-09-14T01:00:00Z"), item.Timestamp);
        Assert.DoesNotContain("response-a", item.Id);
        Assert.DoesNotContain("cascade-a", item.Id);
    }

    [Fact]
    public void ParseRecord_AppliesProtobufZeroDefaultsOnlyInsideProvidedUsage()
    {
        var result = Parse("""
            { "chatModel": {
                "chatStartMetadata": { "createdAt": "2026-09-14T01:00:00Z" },
                "usage": { "inputTokens": "100" }
            } }
            """);

        Assert.Null(result.Problem);
        Assert.NotNull(result.Event);
        Assert.Equal(100, result.Event.InputTokens);
        Assert.Equal(0, result.Event.OutputTokens);
        Assert.Equal(0, result.Event.CachedInputTokens);
        Assert.Equal(0, result.Event.CacheWriteInputTokens);
    }

    [Fact]
    public void ParseRecord_MissingUsageIsUnavailableEvenWhenStepUsageIsPresent()
    {
        var result = Parse("""
            {
              "chatModel": { "chatStartMetadata": { "createdAt": "2026-09-14T01:00:00Z" } },
              "metadata": { "modelUsage": { "inputTokens": "100", "outputTokens": "20" } }
            }
            """);

        Assert.Null(result.Event);
        Assert.Equal(AntigravityUsageService.RecordProblem.MissingUsage, result.Problem);
    }

    [Fact]
    public void ParseRecord_EmptyUsageDoesNotInventAZeroTokenRequest()
    {
        var result = Parse("""
            { "chatModel": {
                "chatStartMetadata": { "createdAt": "2026-09-14T01:00:00Z" },
                "usage": {}
            } }
            """);

        Assert.Null(result.Event);
        Assert.Equal(AntigravityUsageService.RecordProblem.MissingUsage, result.Problem);
    }

    [Fact]
    public void ParseRecord_MissingCallTimeDoesNotUseConversationOrRefreshTime()
    {
        var result = Parse("""
            {
              "createdAt": "2026-09-14T01:00:00Z",
              "lastModifiedTime": "2026-09-14T02:00:00Z",
              "chatModel": { "usage": { "inputTokens": "100", "outputTokens": "20" } }
            }
            """);

        Assert.Null(result.Event);
        Assert.Equal(AntigravityUsageService.RecordProblem.MissingTimestamp, result.Problem);
    }

    [Fact]
    public void ParseRecord_SupportsSnakeCaseAndTimestampSecondsAndNanos()
    {
        var result = Parse("""
            { "chat_model": {
                "response_model": "claude-test",
                "chat_start_metadata": { "created_at": { "seconds": "1767225600", "nanos": 123400000 } },
                "usage": { "input_tokens": "1e3", "output_tokens": 20, "cache_read_tokens": 30 }
            } }
            """);

        Assert.Null(result.Problem);
        Assert.NotNull(result.Event);
        Assert.Equal("claude-test", result.Event.Model);
        Assert.Equal(1030, result.Event.InputTokens);
        Assert.Equal(1000, result.Event.ReportedInputTokens);
        Assert.Equal(20, result.Event.OutputTokens);
        Assert.Equal(30, result.Event.CachedInputTokens);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1767225600).AddTicks(1234000), result.Event.Timestamp);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("\"NaN\"")]
    [InlineData("\"18446744073709551615\"")]
    [InlineData("\"9223372036854775807\"")]
    public void ParseRecord_RejectsInvalidOrOverflowingTokenCounters(string value)
    {
        var result = Parse(Record.Replace("\"1200\"", value, StringComparison.Ordinal));

        Assert.Null(result.Event);
        Assert.Equal(AntigravityUsageService.RecordProblem.InvalidCounters, result.Problem);
    }

    [Fact]
    public void ParseRecord_DistinguishesGeneratorsWithoutResponseIds()
    {
        var record = Record.Replace("\"responseId\": \"response-a\"", "\"model\": \"MODEL_TEST\"", StringComparison.Ordinal);

        var first = Parse(record, "cascade-a", 0);
        var second = Parse(record, "cascade-a", 1);
        var reloaded = Parse(record, "cascade-a", 0);

        Assert.NotEqual(first.Event!.Id, second.Event!.Id);
        Assert.Equal(first.Event.Id, reloaded.Event!.Id);
    }

    [Fact]
    public void ParseRecord_RejectsOverflowAfterAddingCachedInput()
    {
        var result = Parse(Record.Replace("\"200\"", "\"9223372036854775000\"", StringComparison.Ordinal));

        Assert.Null(result.Event);
        Assert.Equal(AntigravityUsageService.RecordProblem.InvalidCounters, result.Problem);
    }

    [Fact]
    public async Task Read_PaginatesGeneratorMetadataAndCountsEachModelCallOnce()
    {
        var offsets = new List<int>();
        var service = new AntigravityUsageService((method, request, _) =>
        {
            var body = JsonSerializer.SerializeToElement(request);
            if (method == "GetAllCascadeTrajectories")
            {
                Assert.False(body.GetProperty("excludeSubtrajectories").GetBoolean());
                return Task.FromResult(Data(List("cascade-a")));
            }

            Assert.Equal("GetCascadeTrajectoryGeneratorMetadata", method);
            Assert.False(body.GetProperty("includeMessages").GetBoolean());
            Assert.Equal("cascade-a", body.GetProperty("cascadeId").GetString());
            var offset = body.GetProperty("generatorMetadataOffset").GetInt32();
            offsets.Add(offset);
            return Task.FromResult(Data(offset switch
            {
                0 => Page(Record, "{\"injected\":{}}"),
                2 => Page(Record.Replace("response-a", "response-b", StringComparison.Ordinal)),
                _ => "{}"
            }));
        });

        var result = await service.ReadAsync();

        Assert.True(result.IsComplete, result.Message);
        Assert.False(result.IsUnavailable);
        Assert.Equal([0, 2, 3], offsets);
        Assert.Equal(2, result.Events.Count);
        Assert.Equal(3000, result.Events.Sum(item => item.InputTokens));
        Assert.Equal(600, result.Events.Sum(item => item.OutputTokens));
        Assert.Equal(2, result.Events.Sum(item => item.RequestCount));
    }

    [Fact]
    public async Task Read_DeduplicatesTheSameResponseAcrossParentAndSubtrajectory()
    {
        var service = new AntigravityUsageService((method, request, _) =>
        {
            if (method == "GetAllCascadeTrajectories") return Task.FromResult(Data(List("parent", "child")));
            var offset = JsonSerializer.SerializeToElement(request).GetProperty("generatorMetadataOffset").GetInt32();
            return Task.FromResult(Data(offset == 0 ? Page(Record) : "{}"));
        });

        var result = await service.ReadAsync();

        Assert.True(result.IsComplete, result.Message);
        Assert.Single(result.Events);
        Assert.Equal(1, result.Events[0].RequestCount);
    }

    [Fact]
    public async Task Read_DeduplicatesByResponseAndKeepsTheLargerCompletedUsage()
    {
        var service = ServiceFor(Page(Record.Replace("\"300\"", "\"100\"", StringComparison.Ordinal), Record));

        var result = await service.ReadAsync();

        Assert.True(result.IsComplete, result.Message);
        Assert.Single(result.Events);
        Assert.Equal(300, result.Events[0].OutputTokens);
    }

    [Fact]
    public async Task Read_KeepsValidCallsAndMarksMissingUsageOrTimeAsIncomplete()
    {
        var service = ServiceFor(Page(Record,
            "{\"chatModel\":{}}",
            "{\"chatModel\":{\"usage\":{\"inputTokens\":42}}}"));

        var result = await service.ReadAsync();

        Assert.False(result.IsComplete);
        Assert.False(result.IsUnavailable);
        Assert.Single(result.Events);
        Assert.Contains("未提供 usage", result.Message);
        Assert.Contains("缺少真实时间", result.Message);
    }

    [Fact]
    public async Task Read_MissingUsageProducesNoFabricatedZeroEvents()
    {
        var service = ServiceFor(Page("{\"chatModel\":{}}"));

        var result = await service.ReadAsync();

        Assert.False(result.IsComplete);
        Assert.False(result.IsUnavailable);
        Assert.Empty(result.Events);
        Assert.Contains("未提供 usage", result.Message);
    }

    [Fact]
    public async Task Read_EmptyMetadataForANonemptyConversationIsIncomplete()
    {
        var result = await ServiceFor("{}").ReadAsync();

        Assert.False(result.IsComplete);
        Assert.False(result.IsUnavailable);
        Assert.Empty(result.Events);
    }

    [Fact]
    public async Task Read_EmptyProtobufSummaryMapIsACompleteEmptySnapshot()
    {
        var service = new AntigravityUsageService((_, _, _) => Task.FromResult(Data("{}")));

        var result = await service.ReadAsync();

        Assert.True(result.IsComplete);
        Assert.False(result.IsUnavailable);
        Assert.Empty(result.Events);
    }

    [Fact]
    public async Task Read_ListFailureIsUnavailableRatherThanAZeroUsageSnapshot()
    {
        var service = new AntigravityUsageService((_, _, _) =>
            Task.FromResult(new AntigravityRpcResult(null, "未找到服务", null)));

        var result = await service.ReadAsync();

        Assert.False(result.IsComplete);
        Assert.True(result.IsUnavailable);
        Assert.Empty(result.Events);
    }

    [Fact]
    public async Task Read_MalformedSummaryIsIncompleteButTheServiceIsAvailable()
    {
        var service = new AntigravityUsageService((_, _, _) =>
            Task.FromResult(Data("{\"trajectorySummaries\":[]}")));

        var result = await service.ReadAsync();

        Assert.False(result.IsComplete);
        Assert.False(result.IsUnavailable);
        Assert.Empty(result.Events);
    }

    [Fact]
    public async Task Read_PageFailureRetainsOnlyCallsActuallyReadAndDoesNotCacheAnIncompleteConversation()
    {
        var calls = 0;
        var service = new AntigravityUsageService((method, request, _) =>
        {
            if (method == "GetAllCascadeTrajectories") return Task.FromResult(Data(List("cascade-a")));
            calls++;
            var offset = JsonSerializer.SerializeToElement(request).GetProperty("generatorMetadataOffset").GetInt32();
            return Task.FromResult(offset == 0 ? Data(Page(Record)) : new AntigravityRpcResult(null, "HTTP 503", Endpoint));
        });

        var first = await service.ReadAsync();
        var second = await service.ReadAsync();

        Assert.False(first.IsComplete);
        Assert.False(first.IsUnavailable);
        Assert.Single(first.Events);
        Assert.False(second.IsComplete);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task Read_StopsWhenServerRepeatsTheSamePage()
    {
        var calls = 0;
        var service = new AntigravityUsageService((method, _, _) =>
        {
            if (method == "GetAllCascadeTrajectories") return Task.FromResult(Data(List("cascade-a")));
            calls++;
            return Task.FromResult(Data(Page(Record)));
        });

        var result = await service.ReadAsync();

        Assert.False(result.IsComplete);
        Assert.Equal(2, calls);
        Assert.Single(result.Events);
    }

    [Fact]
    public async Task Read_BoundsPaginationAndReportsPartialCoverage()
    {
        var calls = 0;
        var service = new AntigravityUsageService((method, request, _) =>
        {
            if (method == "GetAllCascadeTrajectories") return Task.FromResult(Data(List("cascade-a")));
            var offset = JsonSerializer.SerializeToElement(request).GetProperty("generatorMetadataOffset").GetInt32();
            calls++;
            return Task.FromResult(Data(Page(Record.Replace("response-a", $"response-{offset}", StringComparison.Ordinal))));
        }, maxPagesPerRead: 2);

        var result = await service.ReadAsync();

        Assert.False(result.IsComplete);
        Assert.Equal(2, calls);
        Assert.Equal(2, result.Events.Count);
        Assert.Contains("上限", result.Message);
    }

    [Fact]
    public async Task Read_ReusesOnlyIdleUnchangedConversationsAndPrunesInvisibleHistory()
    {
        var pass = 0;
        var calls = 0;
        var service = new AntigravityUsageService((method, request, _) =>
        {
            if (method == "GetAllCascadeTrajectories")
            {
                pass++;
                return Task.FromResult(Data(pass <= 2 ? List("cascade-a") : "{}"));
            }

            calls++;
            var offset = JsonSerializer.SerializeToElement(request).GetProperty("generatorMetadataOffset").GetInt32();
            return Task.FromResult(Data(offset == 0 ? Page(Record) : "{}"));
        });

        var first = await service.ReadAsync();
        var cached = await service.ReadAsync();
        var empty = await service.ReadAsync();

        Assert.True(first.IsComplete, first.Message);
        Assert.True(cached.IsComplete, cached.Message);
        Assert.Single(cached.Events);
        Assert.Equal(2, calls);
        Assert.True(empty.IsComplete);
        Assert.Empty(empty.Events);
    }

    [Fact]
    public async Task Read_ChangedOrExplicitlyClearedConversationsAreReadAgain()
    {
        var pass = 0;
        var calls = 0;
        var service = new AntigravityUsageService((method, request, _) =>
        {
            if (method == "GetAllCascadeTrajectories")
            {
                pass++;
                return Task.FromResult(Data(List("cascade-a").Replace("01:01:00", pass == 1 ? "01:01:00" : "01:02:00", StringComparison.Ordinal)));
            }

            calls++;
            var offset = JsonSerializer.SerializeToElement(request).GetProperty("generatorMetadataOffset").GetInt32();
            return Task.FromResult(Data(offset == 0 ? Page(Record) : "{}"));
        });

        await service.ReadAsync();
        await service.ReadAsync();
        service.ClearCache();
        await service.ReadAsync();

        Assert.Equal(6, calls);
    }

    [Fact]
    public async Task Read_RunningConversationIsRefreshedEvenWhenSummaryTimeIsUnchanged()
    {
        var calls = 0;
        var service = new AntigravityUsageService((method, request, _) =>
        {
            if (method == "GetAllCascadeTrajectories")
                return Task.FromResult(Data(List("cascade-a").Replace("CASCADE_RUN_STATUS_IDLE", "CASCADE_RUN_STATUS_RUNNING", StringComparison.Ordinal)));
            calls++;
            var offset = JsonSerializer.SerializeToElement(request).GetProperty("generatorMetadataOffset").GetInt32();
            return Task.FromResult(Data(offset == 0 ? Page(Record) : "{}"));
        });

        await service.ReadAsync();
        await service.ReadAsync();

        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task Read_EndpointSwitchInvalidatesTheCache()
    {
        var pass = 0;
        var calls = 0;
        var service = new AntigravityUsageService((method, request, _) =>
        {
            if (method == "GetAllCascadeTrajectories") pass++;
            var endpoint = Endpoint.Replace("43210", pass == 1 ? "43210" : "43211", StringComparison.Ordinal);
            if (method == "GetAllCascadeTrajectories") return Task.FromResult(Data(List("cascade-a"), endpoint));
            calls++;
            var offset = JsonSerializer.SerializeToElement(request).GetProperty("generatorMetadataOffset").GetInt32();
            return Task.FromResult(Data(offset == 0 ? Page(Record) : "{}", endpoint));
        });

        await service.ReadAsync();
        await service.ReadAsync();

        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task Read_DiscardsAResultIfTheServerChangesDuringItsScan()
    {
        var service = new AntigravityUsageService((method, _, _) =>
            Task.FromResult(method == "GetAllCascadeTrajectories"
                ? Data(List("cascade-a"))
                : Data(Page(Record), Endpoint.Replace("43210", "43211", StringComparison.Ordinal))));

        var result = await service.ReadAsync();

        Assert.False(result.IsComplete);
        Assert.Empty(result.Events);
        Assert.Contains("服务发生切换", result.Message);
    }

    [Fact]
    public async Task Read_PropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new AntigravityUsageService((_, _, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Data("{}"));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadAsync(cancellation.Token));
    }

    [Fact]
    public async Task Read_ScanDeadlineReportsUnavailableWhenNoListWasRead()
    {
        var service = new AntigravityUsageService(async (_, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Data("{}");
        }, readTimeout: TimeSpan.FromMilliseconds(20));

        var result = await service.ReadAsync();

        Assert.False(result.IsComplete);
        Assert.True(result.IsUnavailable);
        Assert.Empty(result.Events);
    }

    [Fact]
    public async Task Read_AnEmptyServerDoesNotHideCallsFromAnotherServer()
    {
        var populatedEndpoint = Endpoint.Replace("43210", "43211", StringComparison.Ordinal);
        var service = new AntigravityUsageService(
            (_, _, _) => Task.FromResult<IReadOnlyList<AntigravityRpcResult>>(
                [Data("{}"), Data(List("cascade-a"), populatedEndpoint)]),
            (endpoint, _, request, _) =>
            {
                Assert.Equal(populatedEndpoint, endpoint);
                var offset = JsonSerializer.SerializeToElement(request).GetProperty("generatorMetadataOffset").GetInt32();
                return Task.FromResult(Data(offset == 0 ? Page(Record) : "{}", endpoint));
            });

        var result = await service.ReadAsync();

        Assert.True(result.IsComplete, result.Message);
        Assert.Single(result.Events);
        Assert.Equal(1500, result.Events[0].InputTokens);
    }

    [Fact]
    public async Task Read_DeduplicatesResponsesSharedByDifferentServers()
    {
        var secondEndpoint = Endpoint.Replace("43210", "43211", StringComparison.Ordinal);
        var service = new AntigravityUsageService(
            (_, _, _) => Task.FromResult<IReadOnlyList<AntigravityRpcResult>>(
                [Data(List("cascade-a")), Data(List("cascade-b"), secondEndpoint)]),
            (endpoint, _, request, _) =>
            {
                var offset = JsonSerializer.SerializeToElement(request).GetProperty("generatorMetadataOffset").GetInt32();
                return Task.FromResult(Data(offset == 0 ? Page(Record) : "{}", endpoint));
            });

        var result = await service.ReadAsync();

        Assert.True(result.IsComplete, result.Message);
        Assert.Single(result.Events);
        Assert.Equal(1, result.Events[0].RequestCount);
    }

    [Fact]
    public async Task Read_ServerFailurePreservesAnotherServersCallsAndMarksCoveragePartial()
    {
        var service = new AntigravityUsageService(
            (_, _, _) => Task.FromResult<IReadOnlyList<AntigravityRpcResult>>(
                [new AntigravityRpcResult(null, "HTTP 503", null), Data(List("cascade-a"))]),
            (endpoint, _, request, _) =>
            {
                var offset = JsonSerializer.SerializeToElement(request).GetProperty("generatorMetadataOffset").GetInt32();
                return Task.FromResult(Data(offset == 0 ? Page(Record) : "{}", endpoint));
            });

        var result = await service.ReadAsync();

        Assert.False(result.IsComplete);
        Assert.False(result.IsUnavailable);
        Assert.Single(result.Events);
        Assert.Contains("1 个本地服务读取失败", result.Message);
    }

    [Fact]
    public async Task Read_SeparatesScanCachesForSameCascadeIdOnDifferentServers()
    {
        var secondEndpoint = Endpoint.Replace("43210", "43211", StringComparison.Ordinal);
        var calls = 0;
        var service = new AntigravityUsageService(
            (_, _, _) => Task.FromResult<IReadOnlyList<AntigravityRpcResult>>(
                [Data(List("cascade-a")), Data(List("cascade-a"), secondEndpoint)]),
            (endpoint, _, request, _) =>
            {
                calls++;
                var record = endpoint == secondEndpoint ? Record.Replace("response-a", "response-b", StringComparison.Ordinal) : Record;
                var offset = JsonSerializer.SerializeToElement(request).GetProperty("generatorMetadataOffset").GetInt32();
                return Task.FromResult(Data(offset == 0 ? Page(record) : "{}", endpoint));
            });

        var first = await service.ReadAsync();
        var cached = await service.ReadAsync();

        Assert.True(first.IsComplete, first.Message);
        Assert.True(cached.IsComplete, cached.Message);
        Assert.Equal(2, first.Events.Count);
        Assert.Equal(2, cached.Events.Count);
        Assert.Equal(4, calls);
    }

    private static AntigravityUsageService ServiceFor(string firstPage) =>
        new((method, request, _) =>
        {
            if (method == "GetAllCascadeTrajectories") return Task.FromResult(Data(List("cascade-a")));
            var offset = JsonSerializer.SerializeToElement(request).GetProperty("generatorMetadataOffset").GetInt32();
            return Task.FromResult(Data(offset == 0 ? firstPage : "{}"));
        });

    private static AntigravityUsageService.AntigravityUsageRecordParseResult Parse(string json, string cascadeId = "cascade-a", int offset = 0)
    {
        using var document = JsonDocument.Parse(json);
        return AntigravityUsageService.ParseRecord(cascadeId, offset, document.RootElement);
    }

    private static AntigravityRpcResult Data(string json, string endpoint = Endpoint)
    {
        using var document = JsonDocument.Parse(json);
        return new AntigravityRpcResult(document.RootElement.Clone(), "读取成功", endpoint);
    }

    private static string Page(params string[] records) => "{\"generatorMetadata\":[" + string.Join(",", records) + "]}";

    private static string List(params string[] ids) =>
        JsonSerializer.Serialize(new
        {
            trajectorySummaries = ids.ToDictionary(id => id, id => new
            {
                trajectoryId = "trajectory-" + id,
                stepCount = 3,
                lastModifiedTime = "2026-09-14T01:01:00Z",
                status = "CASCADE_RUN_STATUS_IDLE"
            })
        });
}
