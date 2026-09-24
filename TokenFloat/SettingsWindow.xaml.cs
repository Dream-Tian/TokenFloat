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
    private readonly UsageLogService _usageLogService;
    private readonly LocalDataService _localDataService;
    private readonly Func<UpdateInfo, Task<string?>> _downloadInstaller;
    private readonly Func<Task<bool>> _clearUsageCache;
    private readonly Func<Task<bool>> _refreshUsage;
    private readonly Action _exitApplication;
    private readonly ErrorLogService _errorLogService;
    private readonly Func<TimeSpan?> _getLastRefreshDuration;
    private UpdateInfo? _availableUpdate;
    private bool _isLoading = true;

    public SettingsWindow(
        UpdateService updateService,
        AppSettingsService appSettingsService,
        UsageLogService usageLogService,
        LocalDataService localDataService,
        Func<UpdateInfo, Task<string?>> downloadInstaller,
        Func<Task<bool>> clearUsageCache,
        Func<Task<bool>> refreshUsage,
        Action exitApplication,
        ErrorLogService errorLogService,
        Func<TimeSpan?>? getLastRefreshDuration = null)
    {
        InitializeComponent();
        _updateService = updateService;
        _appSettingsService = appSettingsService;
        _usageLogService = usageLogService;
        _localDataService = localDataService;
        _downloadInstaller = downloadInstaller;
        _clearUsageCache = clearUsageCache;
        _refreshUsage = refreshUsage;
        _exitApplication = exitApplication;
        _errorLogService = errorLogService;
        _getLastRefreshDuration = getLastRefreshDuration ?? (() => null);

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "未知";
        VersionText.Text = $"TokenFloat · {version}";
        var settings = _updateService.Settings;
        AutoUpdateCheckBox.IsChecked = settings.AutoCheckEnabled;
        ManifestUrlTextBox.Text = settings.ManifestUrl;
        var appSettings = _appSettingsService.Settings;
        RefreshOnlyVisibleCheckBox.IsChecked = appSettings.RefreshOnlyWhenVisible;
        KeepOnTopCheckBox.IsChecked = appSettings.KeepWindowOnTop;
        Topmost = appSettings.KeepWindowOnTop;
        UpdateThemeButtons(appSettings.UseDarkTheme);
        ShowSettingsPage("Source");
        NewApiBaseUrlTextBox.Text = appSettings.NewApiBaseUrl;
        NewApiTokenPasswordBox.Password = appSettings.NewApiAccessToken;
        NewApiUserIdTextBox.Text = appSettings.NewApiUserId > 0
            ? appSettings.NewApiUserId.ToString()
            : string.Empty;
        AntigravityUsageCheckBox.IsChecked = appSettings.AntigravityUsageEnabled;
        RefreshIntervalComboBox.SelectedItem = RefreshIntervalComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item =>
                int.TryParse(item.Tag?.ToString(), out var seconds) &&
                seconds == appSettings.RefreshIntervalSeconds);
        RefreshDataUsage();
        SetLastRefreshDuration(_getLastRefreshDuration());
        _isLoading = false;
    }

    private void SettingsTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string tag })
        {
            ShowSettingsPage(tag);
        }
    }

    /// <summary>
    /// 只显示当前标签页，选中项用浅色底和正文色标出。
    /// </summary>
    private void ShowSettingsPage(string tag)
    {
        SourcePage.Visibility = tag == "Source" ? Visibility.Visible : Visibility.Collapsed;
        WindowPage.Visibility = tag == "Window" ? Visibility.Visible : Visibility.Collapsed;
        DataPage.Visibility = tag == "Data" ? Visibility.Visible : Visibility.Collapsed;
        UpdatePage.Visibility = tag == "Update" ? Visibility.Visible : Visibility.Collapsed;
        SetSettingsTabState(SourceTabButton, tag == "Source");
        SetSettingsTabState(WindowTabButton, tag == "Window");
        SetSettingsTabState(DataTabButton, tag == "Data");
        SetSettingsTabState(UpdateTabButton, tag == "Update");
    }

    private void SetSettingsTabState(System.Windows.Controls.Button button, bool active)
    {
        button.Background = active ? (System.Windows.Media.Brush)FindResource("DashboardPanel") : System.Windows.Media.Brushes.Transparent;
        button.Foreground = active ? (System.Windows.Media.Brush)FindResource("DashboardText") : (System.Windows.Media.Brush)FindResource("DashboardMuted");
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

    private void KeepOnTopCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        var enabled = KeepOnTopCheckBox.IsChecked == true;
        _appSettingsService.SetKeepWindowOnTop(enabled);
        Topmost = enabled;
        ShowStatus(enabled ? "窗口将保持在最上层" : "已取消置于顶层", true);
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading || sender is not System.Windows.Controls.Button { Tag: string tag })
        {
            return;
        }

        var dark = tag == "Dark";
        _appSettingsService.SetUseDarkTheme(dark);
        UpdateThemeButtons(dark);
        ShowStatus(dark ? "已切换为深色" : "已切换为浅色", true);
    }

    private void UpdateThemeButtons(bool dark)
    {
        SetSettingsTabState(LightThemeButton, !dark);
        SetSettingsTabState(DarkThemeButton, dark);
    }

    private async void SaveNewApiButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadNewApiSettings(out var settings))
        {
            return;
        }

        _appSettingsService.SetNewApi(
            settings.NewApiBaseUrl,
            settings.NewApiAccessToken,
            settings.NewApiUserId);
        if (sender is System.Windows.Controls.Button button)
        {
            button.IsEnabled = false;
        }

        ShowStatus("NewAPI 设置已保存，正在刷新远端汇总…", true);
        try
        {
            if (await _refreshUsage())
            {
                RefreshDataUsage();
                ShowStatus("NewAPI 设置已保存，汇总已刷新", true);
            }
            else
            {
                ShowStatus("NewAPI 设置已保存，将在当前刷新完成后重新读取", true);
            }
        }
        catch (Exception exception)
        {
            _errorLogService.Write("设置页刷新 NewAPI 汇总", exception);
            ShowStatus($"NewAPI 设置已保存，但刷新失败：{exception.Message}", false);
        }
        finally
        {
            if (sender is System.Windows.Controls.Button restoreButton)
            {
                restoreButton.IsEnabled = true;
            }
        }
    }

    /// <summary>
    /// 使用当前输入框内容请求 NewAPI 状态接口，不改变已保存配置。
    /// </summary>
    private async void TestNewApiButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadNewApiSettings(out var settings))
        {
            return;
        }

        TestNewApiButton.IsEnabled = false;
        ShowStatus("正在测试 NewAPI 连接…");
        try
        {
            var result = await _usageLogService.TestConnectionAsync(settings);
            var duration = FormatDuration(result.Duration);
            ShowStatus(
                result.IsSuccess
                    ? $"NewAPI 连接成功 · {duration}"
                    : $"NewAPI 连接失败 · {result.Message} · {duration}",
                result.IsSuccess);
        }
        catch (Exception exception)
        {
            _errorLogService.Write("设置页测试 NewAPI 连接", exception);
            ShowStatus($"测试 NewAPI 连接失败：{exception.Message}", false);
        }
        finally
        {
            TestNewApiButton.IsEnabled = true;
        }
    }

    private bool TryReadNewApiSettings(out AppSettings settings)
    {
        var baseUrl = NewApiBaseUrlTextBox.Text.Trim();
        var accessToken = NewApiTokenPasswordBox.Password.Trim();
        var userIdText = NewApiUserIdTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(baseUrl) &&
            string.IsNullOrWhiteSpace(accessToken) &&
            string.IsNullOrWhiteSpace(userIdText))
        {
            settings = new AppSettings();
            return true;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            ShowStatus("NewAPI 服务地址需要以 http:// 或 https:// 开头", false);
            settings = new AppSettings();
            return false;
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            ShowStatus("NewAPI 系统 Token 不能为空", false);
            settings = new AppSettings();
            return false;
        }

        var userId = 0;
        if (!string.IsNullOrWhiteSpace(userIdText) &&
            (!int.TryParse(userIdText, out userId) || userId < 0))
        {
            ShowStatus("用户 ID 需要是正整数；不需要时可留空", false);
            settings = new AppSettings();
            return false;
        }

        settings = new AppSettings(
            NewApiBaseUrl: uri.ToString().TrimEnd('/'),
            NewApiAccessToken: accessToken,
            NewApiUserId: userId);
        return true;
    }

    /// <summary>
    /// 保存 Antigravity 开关并刷新来源，保留 NewAPI 已缓存的历史记录。
    /// </summary>
    private async void SaveAntigravityButton_Click(object sender, RoutedEventArgs e)
    {
        var enabled = AntigravityUsageCheckBox.IsChecked == true;

        _appSettingsService.SetAntigravity(enabled);
        SaveAntigravityButton.IsEnabled = false;
        TestAntigravityButton.IsEnabled = false;
        ShowStatus(enabled ? "Antigravity 本地来源已保存，正在刷新" : "Antigravity 本地来源已关闭，正在刷新", true);
        try
        {
            if (await _refreshUsage())
            {
                RefreshDataUsage();
                ShowStatus("Antigravity 来源已保存，读取结果见主面板及“配额”页", true);
            }
            else
            {
                ShowStatus("来源已保存，将在当前刷新完成后重新读取", true);
            }
        }
        catch (Exception exception)
        {
            _errorLogService.Write("设置页刷新 Antigravity 来源", exception);
            ShowStatus($"来源已保存，但刷新失败：{exception.Message}", false);
        }
        finally
        {
            SaveAntigravityButton.IsEnabled = true;
            TestAntigravityButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 只读测试本地 IDE 配额接口，展示连接结果而不修改已保存的来源开关。
    /// </summary>
    private async void TestAntigravityButton_Click(object sender, RoutedEventArgs e)
    {
        TestAntigravityButton.IsEnabled = false;
        ShowStatus("正在读取 Antigravity 当前配额…");
        try
        {
            var result = await _usageLogService.TestAntigravityConnectionAsync();
            ShowStatus(result.Snapshot is { } quota
                ? $"连接成功 · {quota.Models.Count} 个模型配额；保存后读取 Token 用量"
                : result.Message, result.Snapshot is not null);
        }
        catch (Exception exception)
        {
            _errorLogService.Write("设置页测试 Antigravity 配额", exception);
            ShowStatus("无法读取 Antigravity，请确认 IDE 已运行并登录", false);
        }
        finally
        {
            TestAntigravityButton.IsEnabled = true;
        }
    }

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
    /// 清除两种来源的统计缓存并重读，完成后刷新占用信息。
    /// </summary>
    private async void ClearUsageCacheButton_Click(object sender, RoutedEventArgs e)
    {
        ClearUsageCacheButton.IsEnabled = false;
        ShowStatus("正在清除并重建统计缓存…");
        try
        {
            if (!await _clearUsageCache())
            {
                ShowStatus("已安排在当前刷新完成后清除并重建缓存", true);
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

    public void SetLastRefreshDuration(TimeSpan? duration)
    {
        RefreshDurationText.Text = duration is null ? "尚未刷新" : FormatDuration(duration.Value);
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
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(success ? "DashboardGreen" : "DashboardDanger");
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

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalSeconds >= 1
            ? $"{duration.TotalSeconds:0.##} 秒"
            : $"{Math.Max(0, duration.TotalMilliseconds):0} ms";

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
