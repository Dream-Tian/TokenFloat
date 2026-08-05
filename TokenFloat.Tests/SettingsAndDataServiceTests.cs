using TokenFloat.Services;

namespace TokenFloat.Tests;

public sealed class SettingsAndDataServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"TokenFloat.SettingsTests.{Guid.NewGuid():N}");

    public SettingsAndDataServiceTests() => Directory.CreateDirectory(_folder);

    [Fact]
    public void RefreshSettings_PersistAndNotify()
    {
        var service = new AppSettingsService(_folder);
        AppSettings? notified = null;
        service.SettingsChanged += settings => notified = settings;

        service.SetRefresh(60, true);
        var reloaded = new AppSettingsService(_folder);

        Assert.NotNull(notified);
        Assert.Equal(60, notified.RefreshIntervalSeconds);
        Assert.True(notified.RefreshOnlyWhenVisible);
        Assert.Equal(60, reloaded.Settings.RefreshIntervalSeconds);
        Assert.True(reloaded.Settings.RefreshOnlyWhenVisible);
    }

    [Fact]
    public void RefreshSettings_RejectUnsupportedInterval()
    {
        var service = new AppSettingsService(_folder);

        service.SetRefresh(1, false);

        Assert.Equal(30, service.Settings.RefreshIntervalSeconds);
    }

    [Fact]
    public void NewApiSettings_PersistAndNotify()
    {
        var service = new AppSettingsService(_folder);
        AppSettings? notified = null;
        service.SettingsChanged += settings => notified = settings;

        service.SetNewApi(" https://newapi.example.com/ ", " test-token ", 123);
        var reloaded = new AppSettingsService(_folder);

        Assert.NotNull(notified);
        Assert.Equal("https://newapi.example.com/", notified.NewApiBaseUrl);
        Assert.Equal("test-token", notified.NewApiAccessToken);
        Assert.Equal(123, notified.NewApiUserId);
        Assert.True(reloaded.Settings.IsNewApiConfigured);
        Assert.Equal("https://newapi.example.com/", reloaded.Settings.NewApiBaseUrl);
        Assert.Equal("test-token", reloaded.Settings.NewApiAccessToken);
        Assert.Equal(123, reloaded.Settings.NewApiUserId);
    }

    [Fact]
    public void LocalDataUsage_CountsAndClearsManagedFiles()
    {
        var logFolder = Path.Combine(_folder, "logs");
        Directory.CreateDirectory(logFolder);
        File.WriteAllBytes(Path.Combine(_folder, "usage-index-v6.json.gz"), new byte[128]);
        File.WriteAllBytes(Path.Combine(_folder, "other.json"), new byte[256]);
        File.WriteAllBytes(Path.Combine(logFolder, "tokenfloat.log"), new byte[64]);
        var service = new LocalDataService(_folder);

        var usage = service.GetUsage();
        service.ClearErrorLogs();
        var cleared = service.GetUsage();

        Assert.Equal(128, usage.UsageCacheBytes);
        Assert.Equal(64, usage.ErrorLogBytes);
        Assert.Equal(128, cleared.UsageCacheBytes);
        Assert.Equal(0, cleared.ErrorLogBytes);
        Assert.True(File.Exists(Path.Combine(_folder, "other.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }
}
