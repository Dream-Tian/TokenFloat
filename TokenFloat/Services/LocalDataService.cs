using System.Diagnostics;
using System.IO;

namespace TokenFloat.Services;

public sealed class LocalDataService
{
    private readonly string _folder;
    private readonly string _logFolder;

    public LocalDataService(string? dataFolder = null)
    {
        _folder = dataFolder ?? Path.Combine(
            Environment.GetEnvironmentVariable("LOCALAPPDATA")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TokenFloat");
        _logFolder = Path.Combine(_folder, "logs");
    }

    public LocalDataUsage GetUsage() => new(
        SumFiles(_folder, "usage-index-v*.json.gz"),
        SumFiles(_logFolder, "*.log"));

    public void OpenFolder()
    {
        Directory.CreateDirectory(_folder);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{_folder}\"",
            UseShellExecute = true
        });
    }

    public void ClearErrorLogs()
    {
        if (!Directory.Exists(_logFolder))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(_logFolder, "*.log"))
        {
            File.Delete(path);
        }
    }

    private static long SumFiles(string folder, string pattern)
    {
        try
        {
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly)
                    .Sum(path => new FileInfo(path).Length)
                : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

public sealed record LocalDataUsage(long UsageCacheBytes, long ErrorLogBytes);
