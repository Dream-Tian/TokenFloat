using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TokenFloat.Services;

namespace TokenFloat.Tests;

public sealed class UpdateServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"TokenFloat.Tests.{Guid.NewGuid():N}");

    public UpdateServiceTests() => Directory.CreateDirectory(_folder);

    [Fact]
    public async Task CheckManifest_ParsesRelativeInstallerPath()
    {
        var manifestPath = Path.Combine(_folder, "update-manifest.json");
        await WriteManifestAsync(manifestPath, "99.0.0", "setup.exe", new string('A', 64));

        var result = await new UpdateService(_folder).CheckManifestAsync(manifestPath);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.NotNull(result.Update);
        Assert.Equal(Path.Combine(_folder, "setup.exe"), result.Update.InstallerSource.LocalPath);
        Assert.Null(result.Update.InstallerSource.Uri);
    }

    [Fact]
    public async Task DownloadInstaller_AcceptsMatchingSha256AndReportsCompletion()
    {
        var sourcePath = Path.Combine(_folder, "source.exe");
        var content = Encoding.UTF8.GetBytes("verified installer content");
        await File.WriteAllBytesAsync(sourcePath, content);
        var sha256 = Convert.ToHexString(SHA256.HashData(content));
        var update = new UpdateInfo(
            new Version(99, 0, 0),
            new UpdateSource(null, sourcePath),
            sha256,
            string.Empty);
        UpdateDownloadProgress? lastProgress = null;
        var progress = new InlineProgress<UpdateDownloadProgress>(value => lastProgress = value);

        var resultPath = await new UpdateService(_folder).DownloadInstallerAsync(update, progress);

        Assert.Equal(content, await File.ReadAllBytesAsync(resultPath));
        Assert.NotNull(lastProgress);
        Assert.Equal(100, lastProgress.Percentage);
    }

    [Fact]
    public async Task DownloadInstaller_RejectsBadSha256AndDeletesTemporaryFile()
    {
        var sourcePath = Path.Combine(_folder, "source.exe");
        await File.WriteAllTextAsync(sourcePath, "tampered installer");
        var update = new UpdateInfo(
            new Version(99, 0, 1),
            new UpdateSource(null, sourcePath),
            new string('0', 64),
            string.Empty);
        var service = new UpdateService(_folder);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadInstallerAsync(update));

        var updatesFolder = Path.Combine(_folder, "updates");
        Assert.Empty(Directory.EnumerateFiles(updatesFolder, "*.download"));
        Assert.Empty(Directory.EnumerateFiles(updatesFolder, "*.exe"));
    }

    [Fact]
    public async Task DownloadInstaller_CancellationDeletesTemporaryFile()
    {
        var sourcePath = Path.Combine(_folder, "large-source.exe");
        var content = new byte[1024 * 1024];
        await File.WriteAllBytesAsync(sourcePath, content);
        var update = new UpdateInfo(
            new Version(99, 0, 2),
            new UpdateSource(null, sourcePath),
            Convert.ToHexString(SHA256.HashData(content)),
            string.Empty);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new UpdateService(_folder).DownloadInstallerAsync(update, cancellationToken: cancellation.Token));

        var updatesFolder = Path.Combine(_folder, "updates");
        Assert.Empty(Directory.EnumerateFiles(updatesFolder, "*.download"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    private static async Task WriteManifestAsync(
        string path,
        string version,
        string installerUrl,
        string sha256)
    {
        var manifest = new
        {
            version,
            installerUrl,
            sha256,
            releaseNotes = "test"
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest));
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
