using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using TokenFloat.Services;

namespace TokenFloat;

public partial class UpdateProgressWindow : Window
{
    private readonly UpdateInfo _update;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _isDownloading;
    private bool _allowClose;

    public UpdateProgressWindow(UpdateInfo update)
    {
        InitializeComponent();
        _update = update;
        VersionText.Text = $"TokenFloat {update.Version}";
        Closing += UpdateProgressWindow_Closing;
    }

    /// <summary>
    /// 下载并校验安装包，通过进度回调实时更新百分比、大小和速度。
    /// </summary>
    public async Task<string> DownloadAsync(UpdateService updateService)
    {
        _isDownloading = true;
        var progress = new Progress<UpdateDownloadProgress>(UpdateProgress);
        try
        {
            return await updateService.DownloadInstallerAsync(_update, progress, _cancellation.Token);
        }
        finally
        {
            _isDownloading = false;
        }
    }

    public void CloseAfterDownload()
    {
        _allowClose = true;
        _cancellation.Dispose();
        Close();
    }

    private void UpdateProgress(UpdateDownloadProgress progress)
    {
        DownloadProgressBar.Value = progress.Percentage;
        StatusText.Text = progress.Percentage >= 100
            ? "下载完成，正在校验…"
            : $"正在下载… {progress.Percentage}%";
        SizeText.Text = progress.TotalBytes is > 0
            ? $"{FormatBytes(progress.BytesDownloaded)} / {FormatBytes(progress.TotalBytes.Value)}"
            : FormatBytes(progress.BytesDownloaded);
        SpeedText.Text = $"{FormatBytes((long)progress.BytesPerSecond)}/s";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StatusText.Text = "正在取消…";
        _cancellation.Cancel();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void UpdateProgressWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isDownloading && !_allowClose)
        {
            e.Cancel = true;
            CancelButton_Click(this, new RoutedEventArgs());
        }
    }
}
