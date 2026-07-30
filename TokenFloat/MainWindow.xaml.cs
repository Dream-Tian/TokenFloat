using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using TokenFloat.Models;
using TokenFloat.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using MessageBox = System.Windows.MessageBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace TokenFloat;

public partial class MainWindow : Window
{
    private const double NormalWidth = 370;
    private const double NormalHeight = 520;
    private const double MiniWidth = 286;
    private const double MiniHeight = 78;

    private readonly UsageLogService _usageLogService;
    private readonly AppSettingsService _appSettingsService;
    private readonly PricingService _pricingService = new();
    private readonly WindowPositionStore _positionStore = new();
    private readonly CodexAppLauncherService _codexAppLauncher = new();
    private readonly DispatcherTimer _refreshTimer;
    private UsageSnapshot? _snapshot;
    private UsagePeriod _period = UsagePeriod.Today;
    private bool _isRefreshing;
    private IReadOnlyList<PricingTrendPoint> _trendPoints = [];
    private TrendMetric _trendMetric = TrendMetric.Tokens;
    private double _trendMaximum;
    private bool _isMiniMode;
    private long? _lastTodayTokens;

    public event Action<string>? TraySummaryChanged;

    public MainWindow(UsageLogService usageLogService, AppSettingsService appSettingsService)
    {
        InitializeComponent();
        _usageLogService = usageLogService;
        _appSettingsService = appSettingsService;
        Topmost = false;
        _isMiniMode = _positionStore.LoadMiniMode();
        ApplyWindowMode();
        _refreshTimer = new DispatcherTimer();
        ApplyRefreshSettings(_appSettingsService.Settings);
        _appSettingsService.SettingsChanged += ApplyRefreshSettings;
        _refreshTimer.Tick += async (_, _) =>
        {
            if (!_appSettingsService.Settings.RefreshOnlyWhenVisible || IsVisible)
            {
                await RefreshAsync();
            }
        };
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _appSettingsService.SettingsChanged -= ApplyRefreshSettings;
        };
        SizeChanged += (_, _) => DrawUsageTrend();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                HideToTray();
            }
        };
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _positionStore.Restore(this, _isMiniMode);
        UpdatePeriodButtons();
        UpdateTrendMetricButtons();

        if (_usageLogService.GetCachedSnapshot() is { } cachedSnapshot)
        {
            _snapshot = cachedSnapshot;
            RenderSnapshot();
        }

        _ = RefreshAsync();
        _refreshTimer.Start();
    }

    /// <summary>
    /// 后台检查变化的日志文件，未变化部分直接复用磁盘索引。
    /// </summary>
    private async Task RefreshAsync()
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            _snapshot = await _usageLogService.LoadSnapshotAsync();
            RenderSnapshot();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    /// <summary>
    /// 删除统计索引后立即从原始日志重建；刷新进行中时拒绝重复清理。
    /// </summary>
    public async Task<bool> ClearUsageCacheAsync()
    {
        if (_isRefreshing)
        {
            return false;
        }

        _isRefreshing = true;
        try
        {
            _usageLogService.ClearCache();
            _snapshot = await _usageLogService.LoadSnapshotAsync();
            RenderSnapshot();
            return true;
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void ApplyRefreshSettings(AppSettings settings) =>
        _refreshTimer.Interval = TimeSpan.FromSeconds(settings.RefreshIntervalSeconds);

    private void RenderSnapshot()
    {
        if (_snapshot is null)
        {
            return;
        }

        var total = _snapshot.TotalFor(_period);
        TotalTokensText.Text = FormatTokens(total.TotalTokens);
        InputTokensText.Text = FormatTokens(total.InputTokens);
        OutputTokensText.Text = FormatTokens(total.OutputTokens);
        var rates = _snapshot.RatesFor(_period);
        RequestCountText.Text = FormatCount(rates.RequestCount);
        AverageRpmText.Text = FormatRate(rates.AverageRpm);
        AverageTpmText.Text = FormatRate(rates.AverageTpm);
        var pricing = _pricingService.Estimate(_snapshot, _period);
        EstimatedCostText.Text = FormatCost(pricing);
        EstimatedCostText.ToolTip = BuildCostTooltip(pricing);
        PeriodTitleText.Text = _period switch
        {
            UsagePeriod.Today => "今日所耗",
            UsagePeriod.Week => "本周所耗",
            _ => "本月所耗"
        };
        RenderComparison(_snapshot.ComparisonFor(_period));

        CodexTokensText.Text = ProviderText("Codex");
        ClaudeTokensText.Text = ProviderText("Claude");
        GeminiTokensText.Text = ProviderText("Gemini");
        var today = _snapshot.TotalFor(UsagePeriod.Today);
        var todayPricing = _pricingService.Estimate(_snapshot, UsagePeriod.Today);
        MiniTokensText.Text = FormatTokens(today.TotalTokens);
        MiniCostText.Text = FormatCost(todayPricing);
        MiniCostText.ToolTip = BuildCostTooltip(todayPricing);
        UpdateMiniTokenIncrease(today.TotalTokens);
        DrawUsageTrend();
        TraySummaryChanged?.Invoke(BuildTraySummary());
    }

    private void RenderComparison(UsageComparison comparison)
    {
        if (comparison.PreviousTokens == 0 || comparison.ChangePercent is null)
        {
            ComparisonText.Text = $"{comparison.Label} · 暂无记录";
            ComparisonText.Foreground = (Brush)FindResource("InkClear");
        }
        else
        {
            var direction = comparison.ChangePercent > 0 ? "↑" : comparison.ChangePercent < 0 ? "↓" : "—";
            ComparisonText.Text = $"{comparison.Label}  {direction} {Math.Abs(comparison.ChangePercent.Value):0.#}%";
            ComparisonText.Foreground = comparison.ChangePercent switch
            {
                > 0 => (Brush)FindResource("SealRed"),
                < 0 => (Brush)FindResource("LandscapeGreen"),
                _ => (Brush)FindResource("InkClear")
            };
        }

        ComparisonText.ToolTip = $"当前 {FormatTokens(comparison.CurrentTokens)} · 同期 {FormatTokens(comparison.PreviousTokens)}";
    }

    private string ProviderText(string providerName)
    {
        var provider = _snapshot?.Providers.FirstOrDefault(item => item.Provider == providerName);
        if (provider is null || !provider.HasData)
        {
            return "暂无记录";
        }

        return FormatTokens(provider.For(_period).TotalTokens);
    }

    private string BuildTraySummary()
    {
        var total = _snapshot!.TotalFor(UsagePeriod.Today);
        var rates = _snapshot.RatesFor(UsagePeriod.Today);
        return $"今日 {FormatTokens(total.TotalTokens)} · {total.RequestCount}次 · RPM {FormatRate(rates.AverageRpm)} · TPM {FormatRate(rates.AverageTpm)}";
    }

    /// <summary>
    /// 按当前周期绘制 Token 或费用水墨趋势线。
    /// </summary>
    private void DrawUsageTrend()
    {
        if (_snapshot is null || UsageTrendCanvas.ActualWidth <= 1 || UsageTrendCanvas.ActualHeight <= 1)
        {
            return;
        }

        var trend = _pricingService.Trend(_snapshot, _period);
        var maximum = trend.Points.Max(TrendValue);
        _trendPoints = trend.Points;
        _trendMaximum = maximum;
        TrendTitleText.Text = trend.Title;
        RestoreTrendPeakText();
        TrendHoverCanvas.Visibility = Visibility.Collapsed;
        UsageTrendCanvas.Children.Clear();

        var width = UsageTrendCanvas.ActualWidth;
        var height = UsageTrendCanvas.ActualHeight;
        UsageTrendCanvas.Children.Add(new Line
        {
            X1 = 0,
            Y1 = height - 1,
            X2 = width,
            Y2 = height - 1,
            Stroke = new SolidColorBrush(Color.FromArgb(30, 26, 26, 26)),
            StrokeThickness = 1
        });

        if (maximum == 0)
        {
            return;
        }

        var linePoints = new PointCollection();
        for (var index = 0; index < trend.Points.Count; index++)
        {
            var x = index * width / (trend.Points.Count - 1);
            var y = height - 2 - TrendValue(trend.Points[index]) / maximum * (height - 6);
            linePoints.Add(new Point(x, y));
        }

        var areaPoints = new PointCollection { new(0, height) };
        foreach (var point in linePoints)
        {
            areaPoints.Add(point);
        }
        areaPoints.Add(new Point(width, height));

        UsageTrendCanvas.Children.Add(new Polygon
        {
            Points = areaPoints,
            Fill = new SolidColorBrush(Color.FromArgb(22, 46, 139, 87))
        });
        UsageTrendCanvas.Children.Add(new Polyline
        {
            Points = linePoints,
            Stroke = new SolidColorBrush(Color.FromRgb(46, 139, 87)),
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round
        });
    }

    private void UsageTrendCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_trendPoints.Count < 2 || UsageTrendCanvas.ActualWidth <= 1)
        {
            return;
        }

        var position = e.GetPosition(UsageTrendCanvas);
        var index = Math.Clamp(
            (int)Math.Round(position.X / UsageTrendCanvas.ActualWidth * (_trendPoints.Count - 1)),
            0,
            _trendPoints.Count - 1);
        var point = _trendPoints[index];
        var x = index * UsageTrendCanvas.ActualWidth / (_trendPoints.Count - 1);
        var height = UsageTrendCanvas.ActualHeight;
        var y = _trendMaximum == 0
            ? height - 2
            : height - 2 - TrendValue(point) / _trendMaximum * (height - 6);

        var hoverText = $"{point.Label} · {FormatTokens(point.Tokens)} · {point.RequestCount}次 · {FormatCost(point.Pricing)}";
        TrendPeakText.Text = hoverText;
        UsageTrendCanvas.ToolTip = $"{hoverText}\n{BuildCostTooltip(point.Pricing)}";
        TrendHoverLine.X1 = x;
        TrendHoverLine.X2 = x;
        TrendHoverLine.Y1 = 0;
        TrendHoverLine.Y2 = height;
        Canvas.SetLeft(TrendHoverDot, x - TrendHoverDot.Width / 2);
        Canvas.SetTop(TrendHoverDot, y - TrendHoverDot.Height / 2);
        TrendHoverCanvas.Visibility = Visibility.Visible;
    }

    private void UsageTrendCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        TrendHoverCanvas.Visibility = Visibility.Collapsed;
        RestoreTrendPeakText();
    }

    private void RestoreTrendPeakText()
    {
        if (_trendMaximum <= 0)
        {
            TrendPeakText.Text = _trendMetric == TrendMetric.Cost && _trendPoints.Any(item => item.RequestCount > 0)
                ? "暂无计价"
                : "暂无记录";
            return;
        }

        var peak = _trendPoints.MaxBy(TrendValue)!;
        TrendPeakText.Text = _trendMetric == TrendMetric.Tokens
            ? $"峰 {FormatTokens(peak.Tokens)}"
            : $"峰 {FormatCost(peak.Pricing)}";
    }

    private double TrendValue(PricingTrendPoint point) =>
        _trendMetric == TrendMetric.Tokens ? point.Tokens : (double)point.Pricing.EstimatedUsd;

    private void TrendMetricButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse(tag, out TrendMetric metric))
        {
            return;
        }

        _trendMetric = metric;
        UpdateTrendMetricButtons();
        DrawUsageTrend();
    }

    private void UpdateTrendMetricButtons()
    {
        var active = new SolidColorBrush(Color.FromRgb(196, 30, 58));
        var inactive = new SolidColorBrush(Color.FromRgb(153, 153, 153));
        TrendTokensButton.Foreground = _trendMetric == TrendMetric.Tokens ? active : inactive;
        TrendCostButton.Foreground = _trendMetric == TrendMetric.Cost ? active : inactive;
    }

    private void ProviderRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_snapshot is null || sender is not FrameworkElement { Tag: string provider })
        {
            return;
        }

        var models = _snapshot.ModelsFor(provider, _period);
        ModelDetailsPopup.PlacementTarget = (UIElement)sender;
        ModelDetailsTitle.Text = $"{provider} · {PeriodShortText()}";
        ModelDetailsPanel.Children.Clear();

        foreach (var model in models)
        {
            var pricing = _pricingService.EstimateModel(_snapshot, provider, model.Model, _period);
            var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = model.Model,
                FontFamily = (FontFamily)FindResource("SerifFont"),
                FontSize = 13,
                Foreground = (Brush)FindResource("InkStrong"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 12, 0)
            });
            var value = new TextBlock
            {
                Text = $"{FormatTokens(model.Totals.TotalTokens)} · {model.Totals.RequestCount}次\n{FormatCost(pricing)}",
                FontFamily = (FontFamily)FindResource("BodyFont"),
                FontSize = 13,
                Foreground = (Brush)FindResource("SealRed"),
                TextAlignment = TextAlignment.Right,
                ToolTip = BuildCostTooltip(pricing)
            };
            Grid.SetColumn(value, 1);
            row.Children.Add(value);
            ModelDetailsPanel.Children.Add(row);
        }

        if (models.Count == 0)
        {
            ModelDetailsPanel.Children.Add(new TextBlock
            {
                Text = "暂无模型记录",
                FontFamily = (FontFamily)FindResource("BodyFont"),
                Foreground = (Brush)FindResource("InkClear"),
                Margin = new Thickness(0, 8, 0, 4)
            });
        }

        ModelDetailsPopup.IsOpen = true;
        e.Handled = true;
    }

    private void CodexRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2)
        {
            return;
        }

        ModelDetailsPopup.IsOpen = false;
        if (!_codexAppLauncher.TryLaunch(out var error))
        {
            var choice = MessageBox.Show(
                $"{error}\n\n是否手动选择 Codex 桌面应用？",
                "TokenFloat",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (choice == MessageBoxResult.Yes)
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "选择 Codex 桌面应用",
                    Filter = "应用程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                    CheckFileExists = true
                };
                if (dialog.ShowDialog(this) == true &&
                    !_codexAppLauncher.SaveAndLaunch(dialog.FileName, out error))
                {
                    MessageBox.Show(error, "TokenFloat", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        e.Handled = true;
    }

    private string PeriodShortText() => _period switch
    {
        UsagePeriod.Today => "今日",
        UsagePeriod.Week => "本周",
        _ => "本月"
    };

    public void ShowFromTray()
    {
        Topmost = false;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _positionStore.Save(this, _isMiniMode);
        if (System.Windows.Application.Current is App { IsExiting: false })
        {
            e.Cancel = true;
            HideToTray();
        }
    }

    private void HideToTray()
    {
        _positionStore.Save(this, _isMiniMode);
        WindowState = WindowState.Normal;
        Hide();
    }

    private void PeriodButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse(tag, out UsagePeriod period))
        {
            return;
        }

        _period = period;
        UpdatePeriodButtons();
        RenderSnapshot();
    }

    private void UpdatePeriodButtons()
    {
        var activeBackground = Brushes.Transparent;
        var inactiveBackground = Brushes.Transparent;
        var activeForeground = new SolidColorBrush(Color.FromRgb(196, 30, 58));
        var inactiveForeground = new SolidColorBrush(Color.FromRgb(102, 102, 102));

        SetPeriodButtonState(TodayButton, _period == UsagePeriod.Today, activeBackground, inactiveBackground, activeForeground, inactiveForeground);
        SetPeriodButtonState(WeekButton, _period == UsagePeriod.Week, activeBackground, inactiveBackground, activeForeground, inactiveForeground);
        SetPeriodButtonState(MonthButton, _period == UsagePeriod.Month, activeBackground, inactiveBackground, activeForeground, inactiveForeground);
    }

    private static void SetPeriodButtonState(
        Button button,
        bool active,
        Brush activeBackground,
        Brush inactiveBackground,
        Brush activeForeground,
        Brush inactiveForeground)
    {
        button.Background = active ? activeBackground : inactiveBackground;
        button.Foreground = active ? activeForeground : inactiveForeground;
    }

    private static string FormatTokens(long value)
    {
        return value switch
        {
            >= 100_000_000 => $"{value / 100_000_000d:0.##}亿",
            >= 10_000 => $"{value / 10_000d:0.##}万",
            _ => value.ToString("N0")
        };
    }

    /// <summary>
    /// 迷你窗口可见时把本次 Token 增量向上漂浮并渐隐，其他状态只更新比较基准。
    /// </summary>
    private void UpdateMiniTokenIncrease(long currentTokens)
    {
        var previousTokens = _lastTodayTokens;
        _lastTodayTokens = currentTokens;
        if (!_isMiniMode || !IsVisible || previousTokens is null || currentTokens <= previousTokens.Value)
        {
            return;
        }

        var text = new TextBlock
        {
            Text = $"+{FormatTokens(currentTokens - previousTokens.Value)}",
            FontFamily = (FontFamily)FindResource("BodyFont"),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("LandscapeGreen"),
            Opacity = 0,
            RenderTransform = new TranslateTransform()
        };
        Canvas.SetLeft(text, 58);
        Canvas.SetTop(text, 31);
        MiniIncreaseCanvas.Children.Add(text);

        var duration = TimeSpan.FromSeconds(1.8);
        var storyboard = new Storyboard();
        var rise = new DoubleAnimation(7, -27, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(rise, text);
        Storyboard.SetTargetProperty(rise, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));
        storyboard.Children.Add(rise);

        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0.14)));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0.82, KeyTime.FromPercent(0.55)));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        fade.Duration = duration;
        Storyboard.SetTarget(fade, text);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        storyboard.Children.Add(fade);

        storyboard.Completed += (_, _) => MiniIncreaseCanvas.Children.Remove(text);
        storyboard.Begin();
    }

    private static string FormatCount(long value) => value.ToString("N0");

    private static string FormatCost(PricingEstimate estimate)
    {
        if (!estimate.HasPricedUsage && estimate.UnpricedRequestCount > 0)
        {
            return "未计价";
        }

        var value = estimate.EstimatedUsd switch
        {
            >= 1_000m => $"${estimate.EstimatedUsd:N0}",
            >= 1m => $"${estimate.EstimatedUsd:0.00}",
            >= 0.01m => $"${estimate.EstimatedUsd:0.000}",
            > 0m => $"${estimate.EstimatedUsd:0.0000}",
            _ => "$0.00"
        };
        return estimate.IsComplete ? value : $"≥{value}";
    }

    private static string BuildCostTooltip(PricingEstimate estimate)
    {
        if (estimate.IsComplete)
        {
            return PricingService.PricingNotice;
        }

        return $"{PricingService.PricingNotice}\n未计价模型：{string.Join("、", estimate.UnpricedModels)}";
    }

    private static string FormatRate(double value) => value switch
    {
        >= 100_000_000 => $"{value / 100_000_000d:0.##}亿",
        >= 10_000 => $"{value / 10_000d:0.##}万",
        >= 100 => $"{value:0}",
        _ => $"{value:0.##}"
    };

    private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMiniMode();
            e.Handled = true;
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void ToggleMiniMode()
    {
        var currentLeft = Left;
        var currentTop = Top;
        _positionStore.Save(this, _isMiniMode);
        _isMiniMode = !_isMiniMode;
        _positionStore.SaveMiniMode(_isMiniMode);
        ApplyWindowMode();
        _positionStore.PlaceAt(this, currentLeft, currentTop);
        _positionStore.Save(this, _isMiniMode);
    }

    private void ApplyWindowMode()
    {
        NormalContent.Visibility = _isMiniMode ? Visibility.Collapsed : Visibility.Visible;
        MiniContent.Visibility = _isMiniMode ? Visibility.Visible : Visibility.Collapsed;
        MinWidth = _isMiniMode ? MiniWidth : NormalWidth;
        MinHeight = _isMiniMode ? MiniHeight : NormalHeight;
        Width = _isMiniMode ? MiniWidth : NormalWidth;
        Height = _isMiniMode ? MiniHeight : NormalHeight;
        if (!_isMiniMode)
        {
            MiniIncreaseCanvas.Children.Clear();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => HideToTray();

    private enum TrendMetric
    {
        Tokens,
        Cost
    }
}
