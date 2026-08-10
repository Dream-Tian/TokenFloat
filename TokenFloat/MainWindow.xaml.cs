using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace TokenFloat;

public partial class MainWindow : Window
{
    private const double NormalWidth = 500;
    private const double NormalHeight = 305;
    private const double MiniWidth = 286;
    private const double MiniHeight = 78;
    private const double TrendPlotLeft = 54;
    private const double TrendPlotTop = 14;
    private const double TrendPlotRight = 16;
    private const double TrendPlotBottom = 42;

    private readonly UsageLogService _usageLogService;
    private readonly AppSettingsService _appSettingsService;
    private readonly PricingService _pricingService = new();
    private readonly WindowPositionStore _positionStore = new();
    private readonly DispatcherTimer _refreshTimer;
    private UsageSnapshot? _snapshot;
    private UsagePeriod _period = UsagePeriod.Today;
    private UsageDateRange? _customRange;
    private bool _isRefreshing;
    private IReadOnlyList<PricingTrendPoint> _trendPoints = [];
    private TrendMetric _trendMetric = TrendMetric.Cost;
    private TrendChartStyle _trendChartStyle = TrendChartStyle.Bars;
    private NormalTab _normalTab = NormalTab.Overview;
    private double _trendMaximum;
    private bool _isMiniMode;
    private long? _lastTodayTokens;

    public event Action<string>? TraySummaryChanged;
    public event Action? SettingsRequested;
    public event Action<TimeSpan>? RefreshCompleted;

    public TimeSpan? LastRefreshDuration { get; private set; }

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
        UpdateTrendChartStyleButtons();
        UpdateNormalTabButtons();

        if (_usageLogService.GetCachedSnapshot() is { } cachedSnapshot)
        {
            _snapshot = cachedSnapshot;
            RenderSnapshot();
        }

        _ = RefreshAsync();
        _refreshTimer.Start();
    }

    /// <summary>
    /// 后台从 NewAPI 拉取消费日志，并复用最近一次压缩缓存做启动展示。
    /// </summary>
    private async Task<bool> RefreshAsync()
    {
        if (_isRefreshing)
        {
            return false;
        }

        _isRefreshing = true;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            _snapshot = _customRange is null
                ? await _usageLogService.LoadSnapshotAsync()
                : await _usageLogService.LoadSnapshotAsync(_customRange.Start);
            RenderSnapshot();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            stopwatch.Stop();
            _isRefreshing = false;
            LastRefreshDuration = stopwatch.Elapsed;
            RefreshCompleted?.Invoke(stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// 删除本地统计缓存后立即从 NewAPI 重拉；刷新进行中时拒绝重复清理。
    /// </summary>
    public async Task<bool> ClearUsageCacheAsync()
    {
        if (_isRefreshing)
        {
            return false;
        }

        _isRefreshing = true;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            _usageLogService.ClearCache();
            _snapshot = _customRange is null
                ? await _usageLogService.LoadSnapshotAsync()
                : await _usageLogService.LoadSnapshotAsync(_customRange.Start);
            RenderSnapshot();
            return true;
        }
        finally
        {
            stopwatch.Stop();
            _isRefreshing = false;
            LastRefreshDuration = stopwatch.Elapsed;
            RefreshCompleted?.Invoke(stopwatch.Elapsed);
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

        var total = ActiveTotal();
        var pricing = ActivePricing();
        TotalTokensText.Text = FormatTokens(total.TotalTokens);
        PrimaryUnitText.Text = " Token";
        var rates = ActiveRates();
        RequestCountText.Text = FormatCount(rates.RequestCount);
        AverageRpmText.Text = FormatRate(rates.AverageRpm);
        AverageTpmText.Text = FormatRate(rates.AverageTpm);
        EstimatedCostText.Text = FormatCost(pricing);
        EstimatedCostText.ToolTip = BuildCostTooltip(pricing);
        var today = _snapshot.TotalFor(UsagePeriod.Today);
        var todayPricing = _pricingService.Estimate(_snapshot, UsagePeriod.Today);
        MiniTokensText.Text = FormatTokens(today.TotalTokens);
        MiniCostText.Text = FormatCost(todayPricing);
        MiniCostText.ToolTip = BuildCostTooltip(todayPricing);
        UpdateMiniTokenIncrease(today.TotalTokens);
        DrawUsageTrend();
        RenderModelPage();
        TraySummaryChanged?.Invoke(BuildTraySummary());
    }

    private void NormalTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse(tag, out NormalTab tab))
        {
            return;
        }

        _normalTab = tab;
        UpdateNormalTabButtons();
        if (tab == NormalTab.Trend)
        {
            DrawUsageTrend();
        }
        else if (tab == NormalTab.Model)
        {
            RenderModelPage();
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void UpdateNormalTabButtons()
    {
        SetSegmentButtonState(OverviewTabButton, _normalTab == NormalTab.Overview);
        SetSegmentButtonState(TrendTabButton, _normalTab == NormalTab.Trend);
        SetSegmentButtonState(ModelTabButton, _normalTab == NormalTab.Model);
        OverviewPage.Visibility = _normalTab == NormalTab.Overview ? Visibility.Visible : Visibility.Collapsed;
        TrendPage.Visibility = _normalTab == NormalTab.Trend ? Visibility.Visible : Visibility.Collapsed;
        ModelPage.Visibility = _normalTab == NormalTab.Model ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderModelPage()
    {
        if (_snapshot is null)
        {
            return;
        }

        var periodPricing = ActivePricing();
        var ranking = ActiveModelsFor()
            .Select(model => new ModelRankingEntry(model, ActiveModelPricing(model.Model)))
            .OrderByDescending(item => item.Model.Totals.TotalTokens)
            .ToArray();
        ModelPageTitle.Text = $"模型用量 · {PeriodShortText()}";
        ModelPageSummary.Text = $"{ranking.Length} 个模型 · {FormatCost(periodPricing)}";
        ModelPagePanel.Children.Clear();
        for (var index = 0; index < ranking.Length; index++)
        {
            ModelPagePanel.Children.Add(CreateModelRankingRow(index + 1, ranking[index], periodPricing));
        }

        if (ranking.Length == 0)
        {
            ModelPagePanel.Children.Add(new TextBlock
            {
                Text = "当前范围暂无模型记录",
                FontFamily = (FontFamily)FindResource("DashboardFont"),
                FontSize = 11,
                Foreground = (Brush)FindResource("DashboardMuted"),
                Margin = new Thickness(0, 12, 0, 5)
            });
        }
    }

    private TokenTotals ActiveTotal() =>
        _customRange is null ? _snapshot!.TotalFor(_period) : _snapshot!.TotalFor(_customRange);

    private UsageRates ActiveRates() =>
        _customRange is null ? _snapshot!.RatesFor(_period) : _snapshot!.RatesFor(_customRange);

    private PricingEstimate ActivePricing() =>
        _customRange is null
            ? _pricingService.Estimate(_snapshot!, _period)
            : _pricingService.Estimate(_snapshot!, _customRange);

    private string BuildTraySummary()
    {
        var total = _snapshot!.TotalFor(UsagePeriod.Today);
        var rates = _snapshot.RatesFor(UsagePeriod.Today);
        var pricing = _pricingService.Estimate(_snapshot, UsagePeriod.Today);
        if (total.TotalTokens == 0 && total.Quota > 0)
        {
            return $"今日消耗 {FormatCost(pricing)}";
        }

        return $"今日 {FormatTokens(total.TotalTokens)} · {total.RequestCount}次 · 消耗 {FormatCost(pricing)}";
    }

    /// <summary>
    /// 按当前周期绘制带坐标轴的柱状图或面积图，并复用同一组悬浮数据点。
    /// </summary>
    private void DrawUsageTrend()
    {
        if (_snapshot is null || UsageTrendCanvas.ActualWidth <= 1 || UsageTrendCanvas.ActualHeight <= 1)
        {
            return;
        }

        var trend = _customRange is null
            ? _pricingService.Trend(_snapshot, _period)
            : _pricingService.Trend(_snapshot, _customRange);
        var maximum = trend.Points.Count == 0 ? 0 : trend.Points.Max(TrendValue);
        _trendPoints = trend.Points;
        _trendMaximum = maximum;
        TrendTitleText.Text = trend.Title;
        TrendTotalText.Text = _trendMetric == TrendMetric.Tokens
            ? $"总计 {FormatTokens(trend.Points.Sum(point => point.Tokens))}"
            : $"总计 {FormatCost(ActivePricing())}";
        RestoreTrendPeakText();
        TrendHoverCanvas.Visibility = Visibility.Collapsed;
        UsageTrendCanvas.Children.Clear();

        var width = UsageTrendCanvas.ActualWidth;
        var height = UsageTrendCanvas.ActualHeight;
        var plotWidth = Math.Max(1, width - TrendPlotLeft - TrendPlotRight);
        var plotHeight = Math.Max(1, height - TrendPlotTop - TrendPlotBottom);
        var plotBottom = TrendPlotTop + plotHeight;

        for (var tick = 0; tick <= 4; tick++)
        {
            var ratio = tick / 4d;
            var y = TrendPlotTop + ratio * plotHeight;
            UsageTrendCanvas.Children.Add(new Line
            {
                X1 = TrendPlotLeft,
                Y1 = y,
                X2 = TrendPlotLeft + plotWidth,
                Y2 = y,
                Stroke = new SolidColorBrush(Color.FromRgb(235, 238, 233)),
                StrokeThickness = 1
            });

            var axisLabel = new TextBlock
            {
                Text = FormatTrendAxisValue(maximum * (1 - ratio)),
                Width = 44,
                TextAlignment = TextAlignment.Right,
                FontFamily = (FontFamily)FindResource("DashboardFont"),
                FontSize = 10,
                Foreground = (Brush)FindResource("DashboardMuted")
            };
            Canvas.SetLeft(axisLabel, 0);
            Canvas.SetTop(axisLabel, y - 7);
            UsageTrendCanvas.Children.Add(axisLabel);
        }

        if (trend.Points.Count == 0)
        {
            return;
        }

        var labelStride = Math.Max(1, (int)Math.Ceiling(trend.Points.Count / 8d));
        for (var index = 0; index < trend.Points.Count; index++)
        {
            if (index % labelStride != 0 && index != trend.Points.Count - 1)
            {
                continue;
            }

            var x = TrendPointX(index, trend.Points.Count, plotWidth);
            var label = new TextBlock
            {
                Text = trend.Points[index].Label,
                Width = 58,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontFamily = (FontFamily)FindResource("DashboardFont"),
                FontSize = 10,
                Foreground = (Brush)FindResource("DashboardMuted")
            };
            Canvas.SetLeft(label, Math.Clamp(x - 29, TrendPlotLeft - 6, width - 58));
            Canvas.SetTop(label, plotBottom + 10);
            UsageTrendCanvas.Children.Add(label);
        }

        if (maximum == 0)
        {
            return;
        }

        var accent = TrendAccentColor();
        if (_trendChartStyle == TrendChartStyle.Bars)
        {
            var slotWidth = plotWidth / trend.Points.Count;
            var barWidth = Math.Clamp(slotWidth * 0.64, 3, 54);
            for (var index = 0; index < trend.Points.Count; index++)
            {
                var barHeight = TrendValue(trend.Points[index]) / maximum * plotHeight;
                var bar = new Rectangle
                {
                    Width = barWidth,
                    Height = Math.Max(1, barHeight),
                    RadiusX = 2,
                    RadiusY = 2,
                    Fill = new SolidColorBrush(accent)
                };
                Canvas.SetLeft(bar, TrendPointX(index, trend.Points.Count, plotWidth) - barWidth / 2);
                Canvas.SetTop(bar, plotBottom - bar.Height);
                UsageTrendCanvas.Children.Add(bar);
            }

            return;
        }

        var linePoints = new PointCollection();
        for (var index = 0; index < trend.Points.Count; index++)
        {
            var x = TrendPointX(index, trend.Points.Count, plotWidth);
            var y = plotBottom - TrendValue(trend.Points[index]) / maximum * plotHeight;
            linePoints.Add(new Point(x, y));
        }

        var areaPoints = new PointCollection { new(TrendPlotLeft, plotBottom) };
        foreach (var point in linePoints)
        {
            areaPoints.Add(point);
        }
        areaPoints.Add(new Point(TrendPlotLeft + plotWidth, plotBottom));

        UsageTrendCanvas.Children.Add(new Polygon
        {
            Points = areaPoints,
            Fill = new SolidColorBrush(Color.FromArgb(36, accent.R, accent.G, accent.B))
        });
        UsageTrendCanvas.Children.Add(new Polyline
        {
            Points = linePoints,
            Stroke = new SolidColorBrush(accent),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round
        });
    }

    private void UsageTrendCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_trendPoints.Count == 0 || UsageTrendCanvas.ActualWidth <= 1)
        {
            return;
        }

        var position = e.GetPosition(UsageTrendCanvas);
        var plotWidth = Math.Max(1, UsageTrendCanvas.ActualWidth - TrendPlotLeft - TrendPlotRight);
        var relativeX = Math.Clamp((position.X - TrendPlotLeft) / plotWidth, 0, 0.999999);
        var index = _trendChartStyle == TrendChartStyle.Bars
            ? Math.Min((int)(relativeX * _trendPoints.Count), _trendPoints.Count - 1)
            : Math.Clamp((int)Math.Round(relativeX * (_trendPoints.Count - 1)), 0, _trendPoints.Count - 1);
        var point = _trendPoints[index];
        var height = UsageTrendCanvas.ActualHeight;
        var plotHeight = Math.Max(1, height - TrendPlotTop - TrendPlotBottom);
        var plotBottom = TrendPlotTop + plotHeight;
        var x = TrendPointX(index, _trendPoints.Count, plotWidth);
        var y = _trendMaximum == 0
            ? plotBottom
            : plotBottom - TrendValue(point) / _trendMaximum * plotHeight;

        var hoverText = $"{point.Label} · {FormatTokens(point.Tokens)} · {point.RequestCount}次 · {FormatCost(point.Pricing)}";
        TrendPeakText.Text = hoverText;
        UsageTrendCanvas.ToolTip = hoverText;
        TrendHoverLine.X1 = x;
        TrendHoverLine.X2 = x;
        TrendHoverLine.Y1 = TrendPlotTop;
        TrendHoverLine.Y2 = plotBottom;
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
                ? "暂无消耗"
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

    private double TrendPointX(int index, int pointCount, double plotWidth) =>
        _trendChartStyle == TrendChartStyle.Bars
            ? TrendPlotLeft + (index + 0.5) * plotWidth / pointCount
            : TrendPlotLeft + index * plotWidth / Math.Max(1, pointCount - 1);

    private Color TrendAccentColor() =>
        _trendMetric == TrendMetric.Tokens
            ? Color.FromRgb(76, 134, 198)
            : Color.FromRgb(245, 154, 50);

    private string FormatTrendAxisValue(double value) =>
        _trendMetric == TrendMetric.Tokens
            ? FormatTokens((long)Math.Round(value))
            : value >= 100 ? $"${value:0}" : $"${value:0.##}";

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
        SetSegmentButtonState(TrendTokensButton, _trendMetric == TrendMetric.Tokens);
        SetSegmentButtonState(TrendCostButton, _trendMetric == TrendMetric.Cost);
    }

    private void TrendChartStyleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse(tag, out TrendChartStyle chartStyle))
        {
            return;
        }

        _trendChartStyle = chartStyle;
        UpdateTrendChartStyleButtons();
        DrawUsageTrend();
    }

    private void UpdateTrendChartStyleButtons()
    {
        SetSegmentButtonState(TrendBarsButton, _trendChartStyle == TrendChartStyle.Bars);
        SetSegmentButtonState(TrendAreaButton, _trendChartStyle == TrendChartStyle.Area);
    }

    private void SetSegmentButtonState(Button button, bool active)
    {
        button.Background = active ? (Brush)FindResource("DashboardPanel") : Brushes.Transparent;
        button.Foreground = active ? (Brush)FindResource("DashboardText") : (Brush)FindResource("DashboardMuted");
    }

    private void ModelRankingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null)
        {
            return;
        }

        ModelRankingTitle.Text = $"模型榜 · {PeriodShortText()}";
        ModelRankingPanel.Children.Clear();
        var periodPricing = ActivePricing();
        var ranking = ActiveModelsFor()
            .Select(model => new ModelRankingEntry(
                model,
                ActiveModelPricing(model.Model)))
            .OrderByDescending(item => item.Model.Totals.TotalTokens)
            .ToArray();

        for (var index = 0; index < ranking.Length; index++)
        {
            ModelRankingPanel.Children.Add(CreateModelRankingRow(
                index + 1,
                ranking[index],
                periodPricing));
        }

        if (ranking.Length == 0)
        {
            ModelRankingPanel.Children.Add(new TextBlock
            {
                Text = "当前范围暂无模型记录",
                FontFamily = (FontFamily)FindResource("DashboardFont"),
                FontSize = 12,
                Foreground = (Brush)FindResource("DashboardMuted"),
                Margin = new Thickness(0, 8, 0, 5)
            });
        }

        ModelRankingPopup.IsOpen = true;
    }

    private IReadOnlyList<ModelUsage> ActiveModelsFor() =>
        _customRange is null
            ? _snapshot!.ModelsFor(_period)
            : _snapshot!.ModelsFor(_customRange);

    private PricingEstimate ActiveModelPricing(string model) =>
        _customRange is null
            ? _pricingService.EstimateModel(_snapshot!, model, _period)
            : _pricingService.EstimateModel(_snapshot!, model, _customRange);

    private FrameworkElement CreateModelRankingRow(
        int rank,
        ModelRankingEntry item,
        PricingEstimate periodPricing)
    {
        var totals = item.Model.Totals;
        var totalTokens = Math.Max(1, totals.TotalTokens);
        var inputPercent = totals.InputTokens * 100d / totalTokens;
        var outputPercent = totals.OutputTokens * 100d / totalTokens;
        var costShare = item.Pricing.HasPricedUsage && periodPricing.EstimatedUsd > 0
            ? $"{item.Pricing.EstimatedUsd * 100m / periodPricing.EstimatedUsd:0.#}%"
            : "未计价";

        var grid = new Grid { Margin = new Thickness(0, 10, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = rank.ToString(CultureInfo.InvariantCulture),
            FontFamily = (FontFamily)FindResource("DashboardMonoFont"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = rank <= 3
                ? (Brush)FindResource("DashboardOrange")
                : (Brush)FindResource("DashboardSubtle"),
            VerticalAlignment = VerticalAlignment.Top
        });

        var modelText = new StackPanel
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(4, 0, 8, 0)
        };
        modelText.Children.Add(new TextBlock
        {
            Text = item.Model.Model,
            FontFamily = (FontFamily)FindResource("DashboardFont"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("DashboardText"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 190
        });
        modelText.Children.Add(new TextBlock
        {
            Text = $"输入 {inputPercent:0.#}% · 输出 {outputPercent:0.#}%",
            Margin = new Thickness(0, 2, 0, 0),
            FontFamily = (FontFamily)FindResource("DashboardFont"),
            FontSize = 10,
            Foreground = (Brush)FindResource("DashboardMuted"),
            ToolTip = $"输入 Token：{FormatTokens(totals.InputTokens)} · 输出 Token：{FormatTokens(totals.OutputTokens)}"
        });
        Grid.SetColumn(modelText, 1);
        grid.Children.Add(modelText);

        var values = new TextBlock
        {
            Text = $"{FormatTokens(totals.TotalTokens)}\n{totals.RequestCount}次 · 消耗 {costShare}",
            FontFamily = (FontFamily)FindResource("DashboardFont"),
            FontSize = 11,
            Foreground = (Brush)FindResource("DashboardGreen"),
            TextAlignment = TextAlignment.Right,
            ToolTip = JoinTooltip(FormatCost(item.Pricing), BuildCostTooltip(item.Pricing))
        };
        Grid.SetColumn(values, 2);
        grid.Children.Add(values);

        return new Border
        {
            BorderBrush = (Brush)FindResource("DashboardBorder"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid
        };
    }

    private string PeriodShortText() => _customRange?.Title ?? (_period switch
    {
        UsagePeriod.Today => "今日",
        UsagePeriod.Week => "本周",
        _ => "本月"
    });

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
        _customRange = null;
        UpdatePeriodButtons();
        RenderSnapshot();
    }

    private void CustomRangeButton_Click(object sender, RoutedEventArgs e)
    {
        var end = _customRange?.EndExclusive.AddDays(-1) ?? DateTime.Today;
        var start = _customRange?.Start ?? end.AddDays(-6);
        CustomStartDateTextBox.Text = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        CustomEndDateTextBox.Text = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        CustomRangeStatusText.Text = "格式：2026-07-30";
        CustomRangeStatusText.Foreground = (Brush)FindResource("DashboardMuted");
        CustomRangePopup.IsOpen = true;
    }

    private void CustomRangePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out var days))
        {
            return;
        }

        var end = DateTime.Today;
        CustomStartDateTextBox.Text = end.AddDays(1 - days)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        CustomEndDateTextBox.Text = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 校验包含首尾两天的日期范围，并按需读取更早日志后切换统计。
    /// </summary>
    private async void ApplyCustomRangeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadCustomRange(out var range, out var error))
        {
            ShowCustomRangeStatus(error, false);
            return;
        }

        if (_isRefreshing)
        {
            ShowCustomRangeStatus("统计正在刷新，请稍后再试", false);
            return;
        }

        var previousRange = _customRange;
        _customRange = range;
        ApplyCustomRangeButton.IsEnabled = false;
        ShowCustomRangeStatus("正在读取所选日期的日志…", true);
        try
        {
            if (!await RefreshAsync())
            {
                _customRange = previousRange;
                RenderSnapshot();
                ShowCustomRangeStatus("读取日志失败，请检查 NewAPI 设置", false);
                return;
            }

            UpdatePeriodButtons();
            CustomRangePopup.IsOpen = false;
        }
        finally
        {
            ApplyCustomRangeButton.IsEnabled = true;
        }
    }

    private bool TryReadCustomRange(out UsageDateRange range, out string error)
    {
        const string format = "yyyy-MM-dd";
        if (!DateTime.TryParseExact(
                CustomStartDateTextBox.Text.Trim(),
                format,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var start) ||
            !DateTime.TryParseExact(
                CustomEndDateTextBox.Text.Trim(),
                format,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var end))
        {
            range = null!;
            error = "请输入 yyyy-MM-dd 格式的日期";
            return false;
        }

        if (end < start)
        {
            range = null!;
            error = "结束日期不能早于开始日期";
            return false;
        }

        if (end > DateTime.Today)
        {
            range = null!;
            error = "结束日期不能晚于今天";
            return false;
        }

        range = UsageDateRange.FromInclusiveDates(start, end);
        error = string.Empty;
        return true;
    }

    private void ShowCustomRangeStatus(string message, bool neutral)
    {
        CustomRangeStatusText.Text = message;
        CustomRangeStatusText.Foreground = neutral
            ? (Brush)FindResource("DashboardMuted")
            : (Brush)FindResource("DashboardOrange");
    }

    private void UpdatePeriodButtons()
    {
        var activeBackground = new SolidColorBrush(Color.FromRgb(231, 238, 226));
        var inactiveBackground = Brushes.Transparent;
        var activeForeground = (Brush)FindResource("DashboardGreen");
        var inactiveForeground = (Brush)FindResource("DashboardMuted");

        SetPeriodButtonState(TodayButton, _customRange is null && _period == UsagePeriod.Today, activeBackground, inactiveBackground, activeForeground, inactiveForeground);
        SetPeriodButtonState(WeekButton, _customRange is null && _period == UsagePeriod.Week, activeBackground, inactiveBackground, activeForeground, inactiveForeground);
        SetPeriodButtonState(MonthButton, _customRange is null && _period == UsagePeriod.Month, activeBackground, inactiveBackground, activeForeground, inactiveForeground);
        SetPeriodButtonState(CustomRangeButton, _customRange is not null, activeBackground, inactiveBackground, activeForeground, inactiveForeground);
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
            FontFamily = (FontFamily)FindResource("DashboardMonoFont"),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("DashboardGreen"),
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
        if (estimate.IsQuotaBased)
        {
            return FormatUsd(estimate.EstimatedUsd);
        }

        if (!estimate.HasPricedUsage && estimate.UnpricedRequestCount > 0)
        {
            return "未计价";
        }

        var value = FormatUsd(estimate.EstimatedUsd);
        return estimate.IsComplete ? value : $"≈{value}";
    }

    private static string FormatUsd(decimal value)
    {
        return value switch
        {
            >= 1_000m => $"${value:N0}",
            >= 1m => $"${value:0.00}",
            >= 0.01m => $"${value:0.000}",
            > 0m => $"${value:0.0000}",
            _ => "$0.00"
        };
    }

    private static string BuildCostTooltip(PricingEstimate estimate)
    {
        if (estimate.IsQuotaBased || (estimate.PricedRequestCount == 0 && estimate.UnpricedRequestCount == 0))
        {
            return string.Empty;
        }

        if (estimate.IsComplete)
        {
            return PricingService.PricingNotice;
        }

        return $"{PricingService.PricingNotice}\n未计价模型：{string.Join("、", estimate.UnpricedModels)}";
    }

    private static string JoinTooltip(string firstLine, string extra) =>
        string.IsNullOrWhiteSpace(extra) ? firstLine : $"{firstLine}\n{extra}";

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
        NormalModeBorder.Visibility = _isMiniMode ? Visibility.Collapsed : Visibility.Visible;
        NormalContent.Visibility = _isMiniMode ? Visibility.Collapsed : Visibility.Visible;
        MiniContent.Visibility = _isMiniMode ? Visibility.Visible : Visibility.Collapsed;
        MinWidth = _isMiniMode ? MiniWidth : NormalWidth;
        MinHeight = _isMiniMode ? MiniHeight : NormalHeight;
        Width = _isMiniMode ? MiniWidth : NormalWidth;
        Height = _isMiniMode ? MiniHeight : NormalHeight;
        ShellBorder.CornerRadius = new CornerRadius(14);
        ShellBorder.Background = (Brush)FindResource("DashboardBackground");
        ShellBorder.BorderBrush = (Brush)FindResource("DashboardBorder");
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

    private enum TrendChartStyle
    {
        Bars,
        Area
    }

    private enum NormalTab
    {
        Overview,
        Trend,
        Model
    }

    private sealed record ModelRankingEntry(
        ModelUsage Model,
        PricingEstimate Pricing);
}
