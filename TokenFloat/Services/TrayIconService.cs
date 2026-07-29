using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.VisualBasic;

namespace TokenFloat.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly Icon _icon;
    private readonly Action _exitApplication;
    private readonly UpdateService _updateService;
    private readonly Func<UpdateInfo, Task<string?>> _downloadInstaller;
    private readonly ErrorLogService _errorLogService;

    public TrayIconService(
        Action showWindow,
        Action exitApplication,
        StartupService startupService,
        UpdateService updateService,
        Func<UpdateInfo, Task<string?>> downloadInstaller,
        ErrorLogService errorLogService)
    {
        _exitApplication = exitApplication;
        _updateService = updateService;
        _downloadInstaller = downloadInstaller;
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
        var autoUpdateItem = new ToolStripMenuItem("自动检查更新")
        {
            Checked = updateService.Settings.AutoCheckEnabled
        };
        autoUpdateItem.Click += (_, _) =>
        {
            updateService.SetAutoCheck(!autoUpdateItem.Checked);
            autoUpdateItem.Checked = updateService.Settings.AutoCheckEnabled;
        };
        _menu.Items.Add(autoUpdateItem);
        _menu.Items.Add("检查更新...", null, async (_, _) => await CheckForUpdatesAsync(true));
        _menu.Items.Add("设置更新源...", null, (_, _) => ConfigureUpdateSource());
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
            autoUpdateItem.Checked = updateService.Settings.AutoCheckEnabled;
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
                    $"发现 {result.Update.Version}，请在托盘菜单中选择“检查更新”。",
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

    /// <summary>
    /// 手动检查时确认下载，安装包通过 SHA-256 校验后才启动静默安装。
    /// </summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        var result = await _updateService.CheckAsync();
        if (result.Status == UpdateCheckStatus.NotConfigured)
        {
            if (manual && MessageBox.Show(
                    "尚未设置更新清单地址，是否现在设置？",
                    "TokenFloat 更新",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information) == DialogResult.Yes)
            {
                ConfigureUpdateSource();
            }

            return;
        }

        if (result.Status == UpdateCheckStatus.UpToDate)
        {
            if (manual)
            {
                MessageBox.Show(result.Message, "TokenFloat 更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            return;
        }

        if (result.Status == UpdateCheckStatus.Error || result.Update is null)
        {
            _errorLogService.Write("手动检查更新", new InvalidOperationException(result.Message));
            if (manual)
            {
                MessageBox.Show(result.Message, "TokenFloat 更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            return;
        }

        var notes = string.IsNullOrWhiteSpace(result.Update.ReleaseNotes)
            ? string.Empty
            : $"\n\n{result.Update.ReleaseNotes}";
        if (MessageBox.Show(
                $"发现 TokenFloat {result.Update.Version}。{notes}\n\n是否下载并安装？",
                "TokenFloat 更新",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            var installer = await _downloadInstaller(result.Update);
            if (installer is null)
            {
                return;
            }

            UpdateService.StartInstaller(installer);
            _exitApplication();
        }
        catch (OperationCanceledException)
        {
            _notifyIcon.ShowBalloonTip(2500, "TokenFloat 更新", "已取消下载。", ToolTipIcon.Info);
        }
        catch (Exception exception)
        {
            _errorLogService.Write("下载或启动更新", exception);
            MessageBox.Show(
                $"更新失败：{exception.Message}",
                "TokenFloat 更新",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void ConfigureUpdateSource()
    {
        var current = _updateService.Settings.ManifestUrl;
        var value = Interaction.InputBox(
            "请输入 HTTPS 更新清单地址；测试时也可填写本地 JSON 文件路径。",
            "TokenFloat 更新源",
            current);
        if (!string.IsNullOrWhiteSpace(value))
        {
            _updateService.SetManifestUrl(value);
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
        BackColor = Color.FromArgb(248, 245, 240),
        ForeColor = Color.FromArgb(51, 51, 51),
        ShowImageMargin = false,
        ShowCheckMargin = true,
        DropShadowEnabled = false,
        Padding = new Padding(5, 6, 5, 6),
        MinimumSize = new Size(184, 0)
    };
}
