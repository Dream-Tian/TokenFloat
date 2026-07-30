using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TokenFloat.Services;

namespace TokenFloat;

public partial class SettingsWindow : Window
{
    private readonly UpdateService _updateService;
    private readonly AppSettingsService _appSettingsService;
    private readonly LocalDataService _localDataService;
    private readonly Func<UpdateInfo, Task<string?>> _downloadInstaller;
    private readonly Func<Task<bool>> _clearUsageCache;
    private readonly Action _exitApplication;
    private readonly ErrorLogService _errorLogService;
    private UpdateInfo? _availableUpdate;
    private bool _isLoading = true;

    public SettingsWindow(
        UpdateService updateService,
        AppSettingsService appSettingsService,
        LocalDataService localDataService,
        Func<UpdateInfo, Task<string?>> downloadInstaller,
        Func<Task<bool>> clearUsageCache,
        Action exitApplication,
        ErrorLogService errorLogService)
    {
        InitializeComponent();
        _updateService = updateService;
        _appSettingsService = appSettingsService;
        _localDataService = localDataService;
        _downloadInstaller = downloadInstaller;
        _clearUsageCache = clearUsageCache;
        _exitApplication = exitApplication;
        _errorLogService = errorLogService;

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "未知";
        VersionText.Text = $"TokenFloat · {version}";
        var settings = _updateService.Settings;
        AutoUpdateCheckBox.IsChecked = settings.AutoCheckEnabled;
        ManifestUrlTextBox.Text = settings.ManifestUrl;
        var appSettings = _appSettingsService.Settings;
        RefreshOnlyVisibleCheckBox.IsChecked = appSettings.RefreshOnlyWhenVisible;
        RefreshIntervalComboBox.SelectedItem = RefreshIntervalComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item =>
                int.TryParse(item.Tag?.ToString(), out var seconds) &&
                seconds == appSettings.RefreshIntervalSeconds);
        RefreshDataUsage();
        _isLoading = false;
    }

    private void AutoUpdateCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        _updateService.SetAutoCheck(AutoUpdateCheckBox.IsChecked == true);
        ShowStatus(AutoUpdateCheckBox.IsChecked == true ? "已开启自动检查更新" : "已关闭自动检查更新");
    }

    private void SaveSourceButton_Click(object sender, RoutedEventArgs e) => SaveManifestSource(true);

    private void RefreshIntervalComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SaveRefreshSettings();

    private void RefreshOnlyVisibleCheckBox_Click(object sender, RoutedEventArgs e) =>
        SaveRefreshSettings();

    private void SaveRefreshSettings()
    {
        if (_isLoading ||
            RefreshIntervalComboBox.SelectedItem is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), out var intervalSeconds))
        {
            return;
        }

        _appSettingsService.SetRefresh(
            intervalSeconds,
            RefreshOnlyVisibleCheckBox.IsChecked == true);
        ShowStatus($"刷新设置已保存 · {item.Content}", true);
    }

    private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _localDataService.OpenFolder();
        }
        catch (Exception exception)
        {
            _errorLogService.Write("设置页打开数据目录", exception);
            ShowStatus($"无法打开数据目录：{exception.Message}", false);
        }
    }

    /// <summary>
    /// 清除统计索引并从原始日志重建，完成后刷新占用信息。
    /// </summary>
    private async void ClearUsageCacheButton_Click(object sender, RoutedEventArgs e)
    {
        ClearUsageCacheButton.IsEnabled = false;
        ShowStatus("正在清除并重建统计缓存…");
        try
        {
            if (!await _clearUsageCache())
            {
                ShowStatus("统计正在刷新，请稍后再试", false);
                return;
            }

            RefreshDataUsage();
            ShowStatus("统计缓存已清除并重建", true);
        }
        catch (Exception exception)
        {
            _errorLogService.Write("设置页清除统计缓存", exception);
            ShowStatus($"清除统计缓存失败：{exception.Message}", false);
        }
        finally
        {
            ClearUsageCacheButton.IsEnabled = true;
        }
    }

    private void ClearErrorLogsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _localDataService.ClearErrorLogs();
            RefreshDataUsage();
            ShowStatus("错误日志已清除", true);
        }
        catch (Exception exception)
        {
            _errorLogService.Write("设置页清除错误日志", exception);
            ShowStatus($"清除错误日志失败：{exception.Message}", false);
        }
    }

    private void RefreshDataUsage()
    {
        var usage = _localDataService.GetUsage();
        UsageCacheSizeText.Text = FormatBytes(usage.UsageCacheBytes);
        ErrorLogSizeText.Text = FormatBytes(usage.ErrorLogBytes);
    }

    /// <summary>
    /// 保存当前更新源后检查版本，并在页面内呈现结果和安装入口。
    /// </summary>
    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveManifestSource(false))
        {
            return;
        }

        SetBusy(true);
        ShowStatus("正在检查更新…");
        _availableUpdate = null;
        DownloadUpdateButton.Visibility = Visibility.Collapsed;
        try
        {
            var result = await _updateService.CheckAsync();
            switch (result.Status)
            {
                case UpdateCheckStatus.UpdateAvailable when result.Update is not null:
                    _availableUpdate = result.Update;
                    DownloadUpdateButton.Visibility = Visibility.Visible;
                    var notes = string.IsNullOrWhiteSpace(result.Update.ReleaseNotes)
                        ? string.Empty
                        : $" · {result.Update.ReleaseNotes}";
                    ShowStatus($"发现新版本 {result.Update.Version}{notes}", true);
                    break;
                case UpdateCheckStatus.UpToDate:
                    ShowStatus(result.Message, true);
                    break;
                default:
                    ShowStatus(result.Message, false);
                    if (result.Status == UpdateCheckStatus.Error)
                    {
                        _errorLogService.Write("设置页检查更新", new InvalidOperationException(result.Message));
                    }

                    break;
            }
        }
        catch (Exception exception)
        {
            _errorLogService.Write("设置页检查更新", exception);
            ShowStatus($"检查更新失败：{exception.Message}", false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// 下载通过 SHA-256 校验的安装包，成功后启动安装器并退出当前程序。
    /// </summary>
    private async void DownloadUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var installer = await _downloadInstaller(_availableUpdate);
            if (installer is null)
            {
                ShowStatus("已取消下载");
                return;
            }

            UpdateService.StartInstaller(installer);
            _exitApplication();
        }
        catch (OperationCanceledException)
        {
            ShowStatus("已取消下载");
        }
        catch (Exception exception)
        {
            _errorLogService.Write("设置页下载更新", exception);
            ShowStatus($"更新失败：{exception.Message}", false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool SaveManifestSource(bool announce)
    {
        var value = ManifestUrlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            ShowStatus("更新清单地址不能为空", false);
            return false;
        }

        _updateService.SetManifestUrl(value);
        if (announce)
        {
            ShowStatus("更新源已保存", true);
        }

        return true;
    }

    private void SetBusy(bool busy)
    {
        AutoUpdateCheckBox.IsEnabled = !busy;
        ManifestUrlTextBox.IsEnabled = !busy;
        SaveSourceButton.IsEnabled = !busy;
        CheckUpdatesButton.IsEnabled = !busy;
        DownloadUpdateButton.IsEnabled = !busy;
    }

    private void ShowStatus(string message, bool success = true)
    {
        StatusText.Text = message;
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
            success
                ? System.Windows.Media.Color.FromRgb(46, 139, 87)
                : System.Windows.Media.Color.FromRgb(196, 30, 58));
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)Math.Max(0, bytes);
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{value:0} {units[unitIndex]}" : $"{value:0.##} {units[unitIndex]}";
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
