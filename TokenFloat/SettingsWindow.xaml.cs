using System.Reflection;
using System.Windows;
using System.Windows.Input;
using TokenFloat.Services;

namespace TokenFloat;

public partial class SettingsWindow : Window
{
    private readonly UpdateService _updateService;
    private readonly Func<UpdateInfo, Task<string?>> _downloadInstaller;
    private readonly Action _exitApplication;
    private readonly ErrorLogService _errorLogService;
    private UpdateInfo? _availableUpdate;
    private bool _isLoading = true;

    public SettingsWindow(
        UpdateService updateService,
        Func<UpdateInfo, Task<string?>> downloadInstaller,
        Action exitApplication,
        ErrorLogService errorLogService)
    {
        InitializeComponent();
        _updateService = updateService;
        _downloadInstaller = downloadInstaller;
        _exitApplication = exitApplication;
        _errorLogService = errorLogService;

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "未知";
        VersionText.Text = $"TokenFloat · {version}";
        var settings = _updateService.Settings;
        AutoUpdateCheckBox.IsChecked = settings.AutoCheckEnabled;
        ManifestUrlTextBox.Text = settings.ManifestUrl;
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

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
