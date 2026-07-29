using TokenFloat.Services;

namespace TokenFloat.Tests;

public sealed class ErrorLogServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"TokenFloat.ErrorLogs.{Guid.NewGuid():N}");

    [Fact]
    public void Write_RedactsCommonSecretFields()
    {
        var service = new ErrorLogService(_folder);

        service.Write("test", new InvalidOperationException("token=abc123 password:xyz789"));

        var logPath = Assert.Single(Directory.EnumerateFiles(Path.Combine(_folder, "logs"), "*.log"));
        var content = File.ReadAllText(logPath);
        Assert.DoesNotContain("abc123", content);
        Assert.DoesNotContain("xyz789", content);
        Assert.Contains("token=[redacted]", content);
        Assert.Contains("password=[redacted]", content);
    }

    [Fact]
    public void Constructor_DeletesLogsOlderThanRetentionPeriod()
    {
        var logFolder = Path.Combine(_folder, "logs");
        Directory.CreateDirectory(logFolder);
        var oldLog = Path.Combine(logFolder, "tokenfloat-2000-01-01.log");
        File.WriteAllText(oldLog, "old");
        File.SetLastWriteTime(oldLog, DateTime.Now.AddDays(-20));

        _ = new ErrorLogService(_folder);

        Assert.False(File.Exists(oldLog));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }
}
