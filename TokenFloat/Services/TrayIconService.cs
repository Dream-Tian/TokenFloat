using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TokenFloat.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly Icon _icon;
    private readonly UpdateService _updateService;
    private readonly ErrorLogService _errorLogService;

    public TrayIconService(
        Action showWindow,
        Action showSettings,
        Action exitApplication,
        StartupService startupService,
        UpdateService updateService,
        ErrorLogService errorLogService)
    {
        _updateService = updateService;
        _errorLogService = errorLogService;
        _icon = CreateAppIcon();
        _menu = CreateContextMenu();
        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "TokenFloat 用量",
            Visible = true
        };
        _menu.Items.Add("显示用量", null, (_, _) => showWindow());
        var startupItem = new ToolStripMenuItem("开机自启")
        {
            Checked = startupService.IsEnabled()
        };
        startupItem.Click += (_, _) =>
        {
            try
            {
                startupService.SetEnabled(!startupItem.Checked);
                startupItem.Checked = startupService.IsEnabled();
            }
            catch (Exception exception)
            {
                _errorLogService.Write("设置开机自启", exception);
                _notifyIcon.ShowBalloonTip(
                    3000,
                    "TokenFloat",
                    $"开机自启设置失败：{exception.Message}",
                    ToolTipIcon.Warning);
            }
        };
        _menu.Items.Add(startupItem);
        _menu.Items.Add("设置...", null, (_, _) => showSettings());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => exitApplication());
        foreach (ToolStripItem item in _menu.Items)
        {
            item.Padding = item is ToolStripSeparator
                ? new Padding(0, 2, 0, 2)
                : new Padding(8, 4, 12, 4);
        }

        _menu.Opening += (_, _) =>
        {
            startupItem.Checked = startupService.IsEnabled();
            InkContextMenuRenderer.ApplyRoundedRegion(_menu);
        };

        _notifyIcon.ContextMenuStrip = _menu;
        _notifyIcon.DoubleClick += (_, _) => showWindow();
        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                showWindow();
            }
        };
    }

    public async Task CheckForUpdatesOnStartupAsync()
    {
        if (!_updateService.ShouldAutoCheck())
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            var result = await _updateService.CheckAsync();
            if (result.Status == UpdateCheckStatus.UpdateAvailable && result.Update is not null)
            {
                _notifyIcon.ShowBalloonTip(
                    5000,
                    "TokenFloat 有新版本",
                    $"发现 {result.Update.Version}，请打开托盘菜单中的“设置”查看。",
                    ToolTipIcon.Info);
            }
            else if (result.Status == UpdateCheckStatus.Error)
            {
                _errorLogService.Write("启动时检查更新", new InvalidOperationException(result.Message));
            }
        }
        catch (Exception exception)
        {
            _errorLogService.Write("启动时检查更新", exception);
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }

    public void UpdateSummary(string summary)
    {
        _notifyIcon.Text = string.IsNullOrWhiteSpace(summary)
            ? "TokenFloat 用量"
            : summary[..Math.Min(summary.Length, 63)];
    }

    /// <summary>
    /// 从当前可执行文件读取应用图标，保证托盘与程序文件图标一致。
    /// </summary>
    private static Icon CreateAppIcon()
    {
        var executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
        {
            var extracted = Icon.ExtractAssociatedIcon(executablePath);
            if (extracted is not null)
            {
                return extracted;
            }
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    /// <summary>
    /// 创建与主窗口一致的宣纸菜单，并用自绘渲染器替换系统默认配色和勾选样式。
    /// </summary>
    private static ContextMenuStrip CreateContextMenu() => new()
    {
        Renderer = new InkContextMenuRenderer(),
        Font = new Font("楷体", 11f, FontStyle.Regular, GraphicsUnit.Point),
        BackColor = Color.FromArgb(255, 255, 255),
        ForeColor = Color.FromArgb(32, 34, 31),
        ShowImageMargin = false,
        ShowCheckMargin = true,
        DropShadowEnabled = true,
        Padding = new Padding(5, 6, 5, 6),
        MinimumSize = new Size(190, 0)
    };
}
