using Microsoft.Win32;

namespace TokenFloat.Services;

public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TokenFloat";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        var command = key?.GetValue(ValueName) as string;
        var executablePath = Environment.ProcessPath;
        return !string.IsNullOrWhiteSpace(command) &&
               !string.IsNullOrWhiteSpace(executablePath) &&
               string.Equals(
                   command.Trim().Trim('"'),
                   executablePath,
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 在当前用户的 Run 注册表项中启用或移除 TokenFloat 启动命令。
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
        if (!enabled)
        {
            key.DeleteValue(ValueName, false);
            return;
        }

        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定 TokenFloat 程序路径。");
        key.SetValue(ValueName, $"\"{executablePath}\"", RegistryValueKind.String);
    }
}
