using System.IO;
using System.Text.Json;
using System.Windows;

namespace TokenFloat.Services;

public sealed class WindowPositionStore
{
    private readonly string _folder = Path.Combine(
        Environment.GetEnvironmentVariable("LOCALAPPDATA")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TokenFloat");

    public void Restore(Window window, bool miniMode)
    {
        var path = PositionPath(miniMode);
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var position = JsonSerializer.Deserialize<WindowPosition>(File.ReadAllText(path));
            if (position is null || !IsVisible(position.Left, position.Top))
            {
                return;
            }

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = position.Left;
            window.Top = position.Top;
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
        }
    }

    public void Save(Window window, bool miniMode)
    {
        if (window.WindowState != WindowState.Normal)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(
                PositionPath(miniMode),
                JsonSerializer.Serialize(new WindowPosition(window.Left, window.Top)));
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// 在指定坐标切换窗口尺寸，仅在新尺寸超出虚拟桌面时向屏幕内收回。
    /// </summary>
    public void PlaceAt(Window window, double left, double top)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top))
        {
            return;
        }

        var maxLeft = Math.Max(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - window.Width);
        var maxTop = Math.Max(
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - window.Height);

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = Math.Clamp(left, SystemParameters.VirtualScreenLeft, maxLeft);
        window.Top = Math.Clamp(top, SystemParameters.VirtualScreenTop, maxTop);
    }

    public bool LoadMiniMode()
    {
        try
        {
            return File.Exists(ModePath) && bool.TryParse(File.ReadAllText(ModePath), out var value) && value;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void SaveMiniMode(bool miniMode)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(ModePath, miniMode.ToString());
        }
        catch (IOException)
        {
        }
    }

    private string PositionPath(bool miniMode) =>
        Path.Combine(_folder, miniMode ? "window-position-mini.json" : "window-position.json");

    private string ModePath => Path.Combine(_folder, "window-mode.txt");

    private static bool IsVisible(double left, double top) =>
        left >= SystemParameters.VirtualScreenLeft - 100 &&
        left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
        top >= SystemParameters.VirtualScreenTop - 100 &&
        top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100;

    private sealed record WindowPosition(double Left, double Top);
}
