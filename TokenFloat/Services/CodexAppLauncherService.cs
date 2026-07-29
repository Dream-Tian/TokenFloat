using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace TokenFloat.Services;

public sealed class CodexAppLauncherService
{
    private readonly string _savedPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TokenFloat",
        "codex-app-path.txt");

    /// <summary>
    /// 优先使用官方 codex 协议，再检查已保存路径、常见安装位置和开始菜单快捷方式。
    /// </summary>
    public bool TryLaunch(out string error)
    {
        if (IsCodexProtocolRegistered() && TryStart("codex://", out _))
        {
            error = string.Empty;
            return true;
        }

        foreach (var candidate in CandidatePaths())
        {
            if (File.Exists(candidate) && TryStart(candidate, out _))
            {
                error = string.Empty;
                return true;
            }
        }

        error = "未检测到已安装的 Codex 桌面应用。";
        return false;
    }

    public bool SaveAndLaunch(string executablePath, out string error)
    {
        if (!File.Exists(executablePath))
        {
            error = "所选程序文件不存在。";
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_savedPath)!);
        File.WriteAllText(_savedPath, executablePath);
        return TryStart(executablePath, out error);
    }

    private IEnumerable<string> CandidatePaths()
    {
        if (File.Exists(_savedPath))
        {
            string saved;
            try
            {
                saved = File.ReadAllText(_savedPath).Trim();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                saved = string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(saved))
            {
                yield return saved;
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var path in new[]
                 {
                     Path.Combine(localAppData, "Programs", "Codex", "Codex.exe"),
                     Path.Combine(localAppData, "Programs", "OpenAI Codex", "Codex.exe"),
                     Path.Combine(localAppData, "OpenAI", "Codex", "Codex.exe"),
                     Path.Combine(programFiles, "Codex", "Codex.exe"),
                     Path.Combine(programFiles, "OpenAI", "Codex", "Codex.exe")
                 })
        {
            yield return path;
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
                 })
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> shortcuts;
            try
            {
                shortcuts = Directory.EnumerateFiles(root, "*Codex*.lnk", SearchOption.AllDirectories).ToArray();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var shortcut in shortcuts)
            {
                yield return shortcut;
            }
        }
    }

    private static bool TryStart(string target, out string error)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool IsCodexProtocolRegistered()
    {
        using var protocolKey = Registry.ClassesRoot.OpenSubKey("codex");
        return protocolKey is not null;
    }
}
