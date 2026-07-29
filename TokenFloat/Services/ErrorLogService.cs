using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace TokenFloat.Services;

public sealed partial class ErrorLogService
{
    private const int RetentionDays = 14;
    private readonly object _syncRoot = new();
    private readonly string _logFolder;

    public ErrorLogService(string? dataFolder = null)
    {
        var root = dataFolder ?? Path.Combine(
            Environment.GetEnvironmentVariable("LOCALAPPDATA")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TokenFloat");
        _logFolder = Path.Combine(root, "logs");
        DeleteExpiredLogs();
    }

    /// <summary>
    /// 记录异常上下文和堆栈；写入失败会被忽略，避免错误日志引发二次崩溃。
    /// </summary>
    public void Write(string context, Exception exception)
    {
        try
        {
            var now = DateTimeOffset.Now;
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
            var content = new StringBuilder()
                .AppendLine($"[{now:O}] {Sanitize(context)}")
                .AppendLine($"Version: {version}")
                .AppendLine($"Exception: {exception.GetType().FullName}")
                .AppendLine($"Message: {Sanitize(exception.Message)}")
                .AppendLine(Sanitize(exception.StackTrace ?? "(no stack trace)"))
                .AppendLine()
                .ToString();

            lock (_syncRoot)
            {
                Directory.CreateDirectory(_logFolder);
                File.AppendAllText(
                    Path.Combine(_logFolder, $"tokenfloat-{now:yyyy-MM-dd}.log"),
                    content,
                    Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    private void DeleteExpiredLogs()
    {
        try
        {
            if (!Directory.Exists(_logFolder))
            {
                return;
            }

            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var path in Directory.EnumerateFiles(_logFolder, "tokenfloat-*.log"))
            {
                if (File.GetLastWriteTime(path) < cutoff)
                {
                    File.Delete(path);
                }
            }
        }
        catch
        {
        }
    }

    private static string Sanitize(string value) =>
        SecretPattern().Replace(value, "$1=[redacted]");

    [GeneratedRegex("(?i)(token|key|secret|password|authorization)\\s*[=:]\\s*[^\\s&]+")]
    private static partial Regex SecretPattern();
}
