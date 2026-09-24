using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Security;
using System.Text;
using System.Text.Json;

namespace TokenFloat.Services;

internal sealed class AntigravityLocalClient
{
    private const string EndpointPrefix = "/exa.language_server_pb.LanguageServerService/";
    private static readonly HttpClient DefaultHttpClient = new(CreateHttpClientHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _httpClient;
    private readonly Func<CancellationToken, Task<IReadOnlyList<(int ProcessId, string? CommandLine)>>> _readProcesses;
    private readonly Func<CancellationToken, Task<string>> _readListeners;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _totalTimeout;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly Dictionary<string, LocalServer> _knownServers = new(StringComparer.Ordinal);
    private LocalServer? _cachedServer;

    internal AntigravityLocalClient(HttpClient? httpClient = null)
        : this(httpClient ?? DefaultHttpClient, ReadProcessesAsync, ReadListenersAsync)
    {
    }

    internal AntigravityLocalClient(
        HttpClient httpClient,
        Func<CancellationToken, Task<IReadOnlyList<(int ProcessId, string? CommandLine)>>> readProcesses,
        Func<CancellationToken, Task<string>> readListeners,
        TimeSpan? requestTimeout = null,
        TimeSpan? totalTimeout = null)
    {
        _httpClient = httpClient;
        _readProcesses = readProcesses;
        _readListeners = readListeners;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(2);
        _totalTimeout = totalTimeout ?? TimeSpan.FromSeconds(12);
        if (_requestTimeout <= TimeSpan.Zero || _totalTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "超时必须大于零。");
        }
    }

    // 复用已验证的本地端点；连接失效时按进程监听表重新发现，并限制整轮耗时。
    internal async Task<AntigravityRpcResult> CallAsync(string method, object request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(method, request);
        cancellationToken.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_totalTimeout);
        var token = budget.Token;
        RpcAttempt? failure = null;
        var entered = false;
        try
        {
            await _requestGate.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            var previousServer = _cachedServer;
            if (previousServer is not null)
            {
                var attempt = await SendAsync(previousServer, method, request, endpointConfirmed: true, token).ConfigureAwait(false);
                if (attempt.Result.Data is not null || !attempt.RetryDiscovery)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return attempt.Result;
                }

                failure = attempt;
                ForgetServer(previousServer);
            }

            var rows = await _readProcesses(token).WaitAsync(token).ConfigureAwait(false);
            var processes = ParseProcesses(rows);
            if (processes.Count == 0)
            {
                _knownServers.Clear();
                cancellationToken.ThrowIfCancellationRequested();
                return failure?.Result ?? new AntigravityRpcResult(null, "未找到正在运行的 Antigravity IDE 服务", null);
            }

            var listeners = ParseListeners(await _readListeners(token).WaitAsync(token).ConfigureAwait(false));
            ForgetUnavailableServers(processes, listeners);
            foreach (var process in processes)
            {
                foreach (var listener in listeners.Where(item => item.ProcessId == process.ProcessId))
                {
                    foreach (var scheme in new[] { "https", "http" })
                    {
                        token.ThrowIfCancellationRequested();
                        var server = new LocalServer(process.ProcessId, new UriBuilder(scheme, listener.Address.ToString(), listener.Port).Uri, process.CsrfToken);
                        if (server == previousServer)
                        {
                            continue;
                        }

                        var attempt = await SendAsync(server, method, request, endpointConfirmed: false, token).ConfigureAwait(false);
                        if (attempt.Result.Data is not null)
                        {
                            _cachedServer = server;
                            RememberServer(server);
                            cancellationToken.ThrowIfCancellationRequested();
                            return attempt.Result;
                        }

                        if (!attempt.RetryDiscovery)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            return attempt.Result;
                        }

                        if (failure is null || attempt.Priority > failure.Priority)
                        {
                            failure = attempt;
                        }
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return failure?.Result ?? new AntigravityRpcResult(null, "已找到 Antigravity IDE 服务，尚未发现本地监听端口", null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return failure is null
                ? new AntigravityRpcResult(null, "读取 Antigravity 本地服务超时", null)
                : failure.Result with { Message = $"{failure.Result.Message}；本轮探测已超时" };
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or InvalidOperationException or IOException or Win32Exception or TimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return failure?.Result ?? new AntigravityRpcResult(null, $"发现 Antigravity 本地服务失败：{exception.Message}", null);
        }
        finally
        {
            if (entered)
            {
                _requestGate.Release();
            }
        }
    }

    // 每轮重新发现所有 Language Server，每个 PID 只选一个有效端点，保留失败以标记部分读取。
    internal async Task<IReadOnlyList<AntigravityRpcResult>> CallAllAsync(string method, object request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(method, request);
        cancellationToken.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_totalTimeout);
        var token = budget.Token;
        var results = new List<AntigravityRpcResult>();
        var expectedResults = 1;
        var entered = false;
        try
        {
            await _requestGate.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            var rows = await _readProcesses(token).WaitAsync(token).ConfigureAwait(false);
            var processes = ParseProcesses(rows).DistinctBy(item => item.ProcessId).ToArray();
            if (processes.Length == 0)
            {
                _knownServers.Clear();
                _cachedServer = null;
                cancellationToken.ThrowIfCancellationRequested();
                return [new AntigravityRpcResult(null, "未找到正在运行的 Antigravity IDE 服务", null)];
            }

            var listeners = ParseListeners(await _readListeners(token).WaitAsync(token).ConfigureAwait(false));
            ForgetUnavailableServers(processes, listeners);
            expectedResults = processes.Length;
            foreach (var process in processes)
            {
                token.ThrowIfCancellationRequested();
                var result = await ReadFromProcessAsync(process.ProcessId, process.CsrfToken, listeners, method, request, token).ConfigureAwait(false);
                results.Add(result);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return results;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AddUnreadFailures(results, expectedResults, "读取 Antigravity 本地服务超时，部分服务尚未读取");
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or InvalidOperationException or IOException or Win32Exception or TimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return AddUnreadFailures(results, expectedResults, $"发现 Antigravity 本地服务失败：{exception.Message}");
        }
        finally
        {
            if (entered)
            {
                _requestGate.Release();
            }
        }
    }

    // 会话后续请求固定到已验证的来源端点，失败时不切换到其他进程或账户。
    internal async Task<AntigravityRpcResult> CallOnEndpointAsync(string endpoint, string method, object request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(method, request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
        {
            return new AntigravityRpcResult(null, "拒绝读取未经验证的 Antigravity 本地端点", null);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_totalTimeout);
        var entered = false;
        try
        {
            await _requestGate.WaitAsync(budget.Token).ConfigureAwait(false);
            entered = true;
            var baseUri = uri.GetLeftPart(UriPartial.Authority) + "/";
            if (!_knownServers.TryGetValue(baseUri, out var server))
            {
                return new AntigravityRpcResult(null, "Antigravity 本地端点尚未验证或已失效，请重新读取服务列表", null);
            }

            var result = await SendAsync(server, method, request, endpointConfirmed: true, budget.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result.Result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AntigravityRpcResult(null, "读取指定的 Antigravity 本地服务超时", new Uri(uri, EndpointPrefix + method).ToString());
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or HttpRequestException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new AntigravityRpcResult(null, $"读取指定的 Antigravity 本地服务失败：{exception.Message}", new Uri(uri, EndpointPrefix + method).ToString());
        }
        finally
        {
            if (entered)
            {
                _requestGate.Release();
            }
        }
    }

    private async Task<AntigravityRpcResult> ReadFromProcessAsync(
        int processId, string csrfToken, IReadOnlyList<LocalListener> listeners, string method, object request, CancellationToken cancellationToken)
    {
        RpcAttempt? failure = null;
        var candidates = GetCandidates(processId, csrfToken, listeners).OrderByDescending(IsKnownServer);
        foreach (var server in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = await SendAsync(server, method, request, IsKnownServer(server), cancellationToken).ConfigureAwait(false);
            if (attempt.Result.Data is not null)
            {
                RememberServer(server);
                return attempt.Result;
            }

            if (!attempt.RetryDiscovery)
            {
                return attempt.Result;
            }

            ForgetServer(server);
            if (failure is null || attempt.Priority > failure.Priority)
            {
                failure = attempt;
            }
        }

        return failure?.Result ?? new AntigravityRpcResult(null, $"Antigravity 进程 {processId} 尚未发现本地监听端口", null);
    }

    private bool IsKnownServer(LocalServer server) =>
        _knownServers.TryGetValue(server.BaseUri.AbsoluteUri, out var known) && known == server;

    private void RememberServer(LocalServer server)
    {
        _knownServers[server.BaseUri.AbsoluteUri] = server;
        if (_cachedServer?.ProcessId == server.ProcessId && _cachedServer.BaseUri == server.BaseUri)
        {
            _cachedServer = server;
        }
    }

    private void ForgetServer(LocalServer server)
    {
        if (IsKnownServer(server))
        {
            _knownServers.Remove(server.BaseUri.AbsoluteUri);
        }
        if (_cachedServer == server)
        {
            _cachedServer = null;
        }
    }

    // 进程、CSRF 或监听地址变化后撤销旧端点，避免把旧会话请求发给复用该端口的进程。
    private void ForgetUnavailableServers(
        IReadOnlyList<(int ProcessId, int? ExtensionServerPort, string CsrfToken)> processes,
        IReadOnlyList<LocalListener> listeners)
    {
        var available = processes.SelectMany(process => GetCandidates(process.ProcessId, process.CsrfToken, listeners)).ToHashSet();
        foreach (var entry in _knownServers.ToArray())
        {
            if (!available.Contains(entry.Value))
            {
                _knownServers.Remove(entry.Key);
            }
        }

        if (_cachedServer is not null && !available.Contains(_cachedServer))
        {
            _cachedServer = null;
        }
    }

    private static IEnumerable<LocalServer> GetCandidates(int processId, string csrfToken, IReadOnlyList<LocalListener> listeners) =>
        listeners.Where(listener => listener.ProcessId == processId)
            .SelectMany(listener => new[] { "https", "http" }.Select(scheme =>
                new LocalServer(processId, new UriBuilder(scheme, listener.Address.ToString(), listener.Port).Uri, csrfToken)));

    private static IReadOnlyList<AntigravityRpcResult> AddUnreadFailures(List<AntigravityRpcResult> results, int expectedResults, string message)
    {
        var remaining = Math.Max(1, expectedResults - results.Count);
        for (var index = 0; index < remaining; index++)
        {
            results.Add(new AntigravityRpcResult(null, message, null));
        }
        return results;
    }

    private static void ValidateRequest(string method, object request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(request);
        if (!method.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("RPC 方法名只能包含字母和数字。", nameof(method));
        }
    }

    // 每个协议/端口单独限时；保留认证、HTTP 和响应格式错误，供后续候选恢复。
    private async Task<RpcAttempt> SendAsync(LocalServer server, string method, object body, bool endpointConfirmed, CancellationToken cancellationToken)
    {
        var endpoint = new Uri(server.BaseUri, EndpointPrefix + method);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        RpcAttempt? responseFailure = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(body) };
            request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
            request.Headers.TryAddWithoutValidation("X-Codeium-Csrf-Token", server.CsrfToken);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var authenticationFailed = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
                var endpointMayHaveChanged = response.StatusCode == HttpStatusCode.NotFound || (int)response.StatusCode is >= 300 and < 400;
                var message = authenticationFailed ? $"Antigravity 本地服务认证失败（HTTP {(int)response.StatusCode}）" : $"Antigravity 本地服务返回 HTTP {(int)response.StatusCode}";
                responseFailure = new RpcAttempt(new AntigravityRpcResult(null, message, endpoint.ToString()),
                    authenticationFailed || endpointMayHaveChanged || !endpointConfirmed, authenticationFailed ? 4 : 3);
                var code = await ReadRpcErrorCodeAsync(response, timeout.Token).ConfigureAwait(false);
                if (code is not null)
                {
                    message += $"（{code}）";
                }

                return new RpcAttempt(new AntigravityRpcResult(null, message, endpoint.ToString()),
                    authenticationFailed || (code is null && (endpointMayHaveChanged || !endpointConfirmed)), authenticationFailed ? 4 : 3);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new RpcAttempt(new AntigravityRpcResult(null, "Antigravity 本地服务返回了无效的 JSON 对象", endpoint.ToString()), true, 2);
            }

            return new RpcAttempt(new AntigravityRpcResult(document.RootElement.Clone(), "读取成功", endpoint.ToString()), false, 0);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return responseFailure ?? new RpcAttempt(new AntigravityRpcResult(null, "Antigravity 本地服务请求超时", endpoint.ToString()), true, 1);
        }
        catch (JsonException)
        {
            return new RpcAttempt(new AntigravityRpcResult(null, "Antigravity 本地服务返回了无效的 JSON", endpoint.ToString()), true, 2);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return responseFailure ?? new RpcAttempt(new AntigravityRpcResult(null, $"无法连接 Antigravity 本地服务：{exception.Message}", endpoint.ToString()), true, 1);
        }
    }

    private static async Task<string?> ReadRpcErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() switch
                {
                    "canceled" or "unknown" or "invalid_argument" or "deadline_exceeded" or "not_found" or
                    "already_exists" or "permission_denied" or "resource_exhausted" or "failed_precondition" or
                    "aborted" or "out_of_range" or "unimplemented" or "internal" or "unavailable" or "data_loss" or
                    "unauthenticated" => value.GetString(),
                    _ => null
                };
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    // 只接受 Antigravity 数据目录及有效 CSRF；扩展端口仅解析，不作为请求目标。
    internal static IReadOnlyList<(int ProcessId, int? ExtensionServerPort, string CsrfToken)> ParseProcesses(
        IEnumerable<(int ProcessId, string? CommandLine)> processes)
    {
        var result = new List<(int ProcessId, int? ExtensionServerPort, string CsrfToken)>();
        foreach (var process in processes)
        {
            var arguments = ParseArguments(process.CommandLine ?? string.Empty);
            var csrf = arguments.GetValueOrDefault("csrf_token");
            if (process.ProcessId <= 0 || string.IsNullOrWhiteSpace(csrf) || csrf.Any(char.IsControl) ||
                arguments.GetValueOrDefault("app_data_dir")?.Contains("antigravity", StringComparison.OrdinalIgnoreCase) != true)
            {
                continue;
            }

            int? extensionPort = int.TryParse(arguments.GetValueOrDefault("extension_server_port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535 ? port : null;
            result.Add((process.ProcessId, extensionPort, csrf));
        }

        return result;
    }

    private static IReadOnlyList<LocalListener> ParseListeners(string output)
    {
        var listeners = new HashSet<LocalListener>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 5 || !fields[0].Equals("TCP", StringComparison.OrdinalIgnoreCase) ||
                !fields[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out var processId) || processId <= 0 ||
                !IPEndPoint.TryParse(fields[1], out var endpoint) || endpoint.Port is <= 0 or > 65535)
            {
                continue;
            }

            var address = endpoint.Address.Equals(IPAddress.Any) ? IPAddress.Loopback :
                endpoint.Address.Equals(IPAddress.IPv6Any) ? IPAddress.IPv6Loopback : endpoint.Address;
            if (IPAddress.IsLoopback(address))
            {
                listeners.Add(new LocalListener(processId, address, endpoint.Port));
            }
        }

        return listeners.ToArray();
    }

    // WMI 在工作线程运行，并给枚举设置期限，避免阻塞 WPF 界面。
    private static Task<IReadOnlyList<(int ProcessId, string? CommandLine)>> ReadProcessesAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<(int, string?)>>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name LIKE 'language_server%'")
        {
            Options = new System.Management.EnumerationOptions { Rewindable = false, Timeout = TimeSpan.FromSeconds(3) }
        };
        using var collection = searcher.Get();
        var result = new List<(int, string?)>();
        foreach (ManagementObject process in collection)
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add((Convert.ToInt32(process["ProcessId"], CultureInfo.InvariantCulture), process["CommandLine"] as string));
            }
        }

        return result;
    }, cancellationToken);

    // 单次读取全部 TCP 监听端口；取消和超时都会结束启动的 netstat 进程。
    private static async Task<string> ReadListenersAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "netstat.exe"),
                Arguments = "-ano",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        timeout.Token.ThrowIfCancellationRequested();
        if (!process.Start())
        {
            throw new IOException("无法启动 netstat。");
        }

        using var registration = timeout.Token.Register(() => StopProcess(process));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token)).WaitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new IOException($"netstat 退出码为 {process.ExitCode}。");
            }

            return await output.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("获取本地监听端口超时。");
        }
        finally
        {
            StopProcess(process);
        }
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }
    }

    private static Dictionary<string, string> ParseArguments(string commandLine)
    {
        var tokens = SplitArguments(commandLine);
        var arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var separator = token.IndexOf('=', 2);
            if (separator >= 0)
            {
                arguments[token[2..separator]] = token[(separator + 1)..];
            }
            else if (index + 1 < tokens.Count && !tokens[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                arguments[token[2..]] = tokens[++index];
            }
        }

        return arguments;
    }

    // 按 Windows 的双引号和反斜杠规则拆分参数，保留目录名里的空格。
    private static IReadOnlyList<string> SplitArguments(string commandLine)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var started = false;
        for (var index = 0; index < commandLine.Length; index++)
        {
            var character = commandLine[index];
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (started)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    started = false;
                }
                continue;
            }

            started = true;
            if (character == '\\')
            {
                var first = index;
                while (index + 1 < commandLine.Length && commandLine[index + 1] == '\\')
                {
                    index++;
                }

                var count = index - first + 1;
                if (index + 1 < commandLine.Length && commandLine[index + 1] == '"')
                {
                    current.Append('\\', count / 2);
                    if (count % 2 == 0)
                    {
                        quoted = !quoted;
                    }
                    else
                    {
                        current.Append('"');
                    }
                    index++;
                }
                else
                {
                    current.Append('\\', count);
                }
            }
            else if (character == '"')
            {
                quoted = !quoted;
            }
            else
            {
                current.Append(character);
            }
        }

        if (started)
        {
            tokens.Add(current.ToString());
        }
        return tokens;
    }

    internal static HttpClientHandler CreateHttpClientHandler() => new()
    {
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
            errors == SslPolicyErrors.None || request.RequestUri is { IsLoopback: true }
    };

    private sealed record LocalListener(int ProcessId, IPAddress Address, int Port);
    private sealed record LocalServer(int ProcessId, Uri BaseUri, string CsrfToken);
    private sealed record RpcAttempt(AntigravityRpcResult Result, bool RetryDiscovery, int Priority);
}

internal sealed record AntigravityRpcResult(JsonElement? Data, string Message, string? Endpoint);
