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
    private const double NormalWidth = 516;
    private const double NormalHeight = 328;
    private const double MiniWidth = 200;
    private const double MiniHeight = 80;
    private const double TrendPlotLeft = 54;
    private const double TrendPlotTop = 14;
    private const double TrendPlotRight = 16;
    private const double TrendPlotBottom = 42;

    private readonly UsageLogService _usageLogService;
    private readonly AppSettingsService _appSettingsService;
    private readonly PricingService _pricingService = new();
    private readonly WindowPositionStore _positionStore = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _trendResizeTimer;
    private UsageSnapshot? _snapshot;
    private UsagePeriod _period = UsagePeriod.Today;
    private UsageDateRange? _customRange;
    private bool _isRefreshing;
    private bool _refreshQueued;
    private bool _clearCacheQueued;
    private IReadOnlyList<PricingTrendPoint> _trendPoints = [];
    private TrendMetric _trendMetric = TrendMetric.Cost;
    private TrendChartStyle _trendChartStyle = TrendChartStyle.Bars;
    private NormalTab _normalTab = NormalTab.Overview;
    private string? _trendModelFilter;
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
        LoadUiState();
        ApplyWindowMode();
        _refreshTimer = new DispatcherTimer();
        ApplySettings(_appSettingsService.Settings);
        _appSettingsService.SettingsChanged += ApplySettings;
        _refreshTimer.Tick += async (_, _) =>
        {
            if (!_appSettingsService.Settings.RefreshOnlyWhenVisible || IsVisible)
            {
                await RefreshAsync();
            }
        };
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        _trendResizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _trendResizeTimer.Tick += (_, _) =>
        {
            _trendResizeTimer.Stop();
            DrawUsageTrend();
        };
        SizeChanged += (_, _) =>
        {
            _trendResizeTimer.Stop();
            _trendResizeTimer.Start();
        };
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _trendResizeTimer.Stop();
            _appSettingsService.SettingsChanged -= ApplySettings;
        };
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
        UpdateTrendFilterChip();

        if (_usageLogService.GetCachedSnapshot() is { } cachedSnapshot)
        {
            _snapshot = cachedSnapshot;
            RenderSnapshot();
        }

        _ = RefreshAsync();
        _refreshTimer.Start();
    }

    /// <summary>
    /// 刷新已启用的来源；来源切换时排队重读，避免旧请求覆盖新设置。
    /// </summary>
    private async Task<bool> RefreshAsync(bool clearCache = false)
    {
        if (_isRefreshing)
        {
            return false;
        }

        _isRefreshing = true;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (clearCache)
            {
                _usageLogService.ClearCache();
            }

            var snapshot = _customRange is null
                ? await _usageLogService.LoadSnapshotAsync()
                : await _usageLogService.LoadSnapshotAsync(_customRange.Start);
            if (!_refreshQueued)
            {
                _snapshot = snapshot;
                RenderSnapshot();
            }
            return true;
        }
        catch (Exception exception) when (!clearCache && (exception is IOException or UnauthorizedAccessException))
        {
            return false;
        }
        finally
        {
            stopwatch.Stop();
            _isRefreshing = false;
            LastRefreshDuration = stopwatch.Elapsed;
            RefreshCompleted?.Invoke(stopwatch.Elapsed);
            if (_refreshQueued)
            {
                var clearQueued = _clearCacheQueued;
                _refreshQueued = false;
                _clearCacheQueued = false;
                _ = RefreshAsync(clearQueued);
            }
        }
    }

    /// <summary>
    /// 保存来源后请求刷新；已有任务时只排队一次，保留其他来源的历史缓存。
    /// </summary>
    public Task<bool> RefreshUsageAsync()
    {
        if (_isRefreshing)
        {
            _refreshQueued = true;
            return Task.FromResult(false);
        }

        return RefreshAsync();
    }

    /// <summary>
    /// 清除两种来源的统计缓存并重建；正在刷新时延后清理，避免并发写入。
    /// </summary>
    public Task<bool> ClearUsageCacheAsync()
    {
        if (_isRefreshing)
        {
            _refreshQueued = true;
            _clearCacheQueued = true;
            return Task.FromResult(false);
        }

        return RefreshAsync(clearCache: true);
    }

    /// <summary>
    /// 恢复上次会话的标签页、趋势指标、图表样式和模型筛选。
    /// </summary>
    private void LoadUiState()
    {
        var state = _positionStore.LoadUiState();
        if (state?.Tab is { } tab && Enum.TryParse(tab, out NormalTab savedTab))
        {
            _normalTab = savedTab;
        }

        if (state?.TrendMetric is { } metric && Enum.TryParse(metric, out TrendMetric savedMetric))
        {
            _trendMetric = savedMetric;
        }

        if (state?.TrendChartStyle is { } style && Enum.TryParse(style, out TrendChartStyle savedStyle))
        {
            _trendChartStyle = savedStyle;
        }

        _trendModelFilter = string.IsNullOrWhiteSpace(state?.TrendModelFilter) ? null : state!.TrendModelFilter;
        Topmost = _appSettingsService.Settings.KeepWindowOnTop;
    }

    private void SaveUiState() =>
        _positionStore.SaveUiState(new DashboardUiState(
            _normalTab.ToString(),
            _trendMetric.ToString(),
            _trendChartStyle.ToString(),
            _trendModelFilter));

    private void ApplySettings(AppSettings settings)
    {
        _refreshTimer.Interval = TimeSpan.FromSeconds(settings.RefreshIntervalSeconds);
        Topmost = settings.KeepWindowOnTop;
        if (_snapshot is not null)
        {
            DrawUsageTrend();
        }
    }

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
        RpmCard.ToolTip = $"所选时段共 {rates.RequestCount:N0} 次请求，平均每分钟 {FormatRate(rates.AverageRpm)} 次";
        TpmCard.ToolTip = $"所选时段共 {total.TotalTokens:N0} Token，平均每分钟 {FormatRate(rates.AverageTpm)}";
        EstimatedCostText.Text = FormatCost(pricing);
        EstimatedCostText.ToolTip = BuildCostTooltip(pricing);
        SourceStatusText.Text = BuildSourceStatus();
        SourceStatusText.ToolTip = BuildSourceTooltip(_snapshot);
        var usageLimited = _appSettingsService.Settings.AntigravityUsageEnabled &&
            _snapshot.AntigravityUsageIsComplete != true;
        var usageUnavailable = IsUsageUnavailable(total);
        TokenCoverageText.Text = usageLimited ? "已读取 · 见提示" : "统计 Token 数";
        TokenCoverageText.ToolTip = _snapshot.AntigravityUsageMessage;
        TotalTokensText.ToolTip = JoinTooltip(
            $"输入 {total.InputTokens:N0}（含缓存读取 {total.CachedInputTokens:N0}） · 输出 {total.OutputTokens:N0}",
            _snapshot.AntigravityUsageMessage ?? string.Empty);
        if (usageUnavailable)
        {
            TotalTokensText.Text = "--";
            RequestCountText.Text = "--";
            AverageRpmText.Text = "--";
            AverageTpmText.Text = "--";
            EstimatedCostText.Text = "未计价";
        }

        var today = _snapshot.TotalFor(UsagePeriod.Today);
        var todayPricing = _pricingService.Estimate(_snapshot, UsagePeriod.Today);
        var todayUnavailable = IsUsageUnavailable(today);
        MiniTokensText.Text = todayUnavailable ? "--" : FormatTokens(today.TotalTokens);
        MiniTokensText.ToolTip = JoinTooltip(
            $"今日输入 {today.InputTokens:N0}（含缓存读取 {today.CachedInputTokens:N0}） · 输出 {today.OutputTokens:N0}",
            _snapshot.AntigravityUsageMessage ?? string.Empty);
        MiniCostText.Text = todayUnavailable ? "未计价" : FormatCost(todayPricing);
        MiniCostText.ToolTip = BuildCostTooltip(todayPricing);
        if (!todayUnavailable)
        {
            UpdateMiniTokenIncrease(today.TotalTokens);
        }
        DrawUsageTrend();
        RenderModelPage();
        RenderQuotaPage();
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
        SaveUiState();
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
        SetSegmentButtonState(QuotaTabButton, _normalTab == NormalTab.Quota);
        OverviewPage.Visibility = _normalTab == NormalTab.Overview ? Visibility.Visible : Visibility.Collapsed;
        TrendPage.Visibility = _normalTab == NormalTab.Trend ? Visibility.Visible : Visibility.Collapsed;
        ModelPage.Visibility = _normalTab == NormalTab.Model ? Visibility.Visible : Visibility.Collapsed;
        QuotaPage.Visibility = _normalTab == NormalTab.Quota ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderModelPage()
    {
        if (_snapshot is null)
        {
            return;
        }

        var periodPricing = ActivePricing();
        var modelPricing = ActiveModelPricing();
        var ranking = ActiveModelsFor()
            .Select(model => new ModelRankingEntry(model, modelPricing.GetValueOrDefault(model.Model) ?? ZeroPricing))
            .OrderByDescending(item => item.Model.Totals.TotalTokens)
            .ToArray();
        ModelPageTitle.Text = $"模型用量 · {PeriodShortText()}";
        var unavailable = IsUsageUnavailable(ActiveTotal());
        ModelPageSummary.Text = unavailable ? "用量未提供" : $"{ranking.Length} 个模型 · {FormatCost(periodPricing)}";
        ModelPageSummary.ToolTip = _snapshot.AntigravityUsageMessage;
        ModelPagePanel.Children.Clear();
        for (var index = 0; index < ranking.Length; index++)
        {
            ModelPagePanel.Children.Add(CreateModelRankingRow(index + 1, ranking[index], periodPricing));
        }

        if (ranking.Length == 0)
        {
            ModelPagePanel.Children.Add(new TextBlock
            {
                Text = unavailable ? "当前范围的 Token 数据尚不可用，详见顶部来源提示。" : "当前范围暂无模型记录",
                TextWrapping = TextWrapping.Wrap,
                FontFamily = (FontFamily)FindResource("DashboardFont"),
                FontSize = 11,
                Foreground = (Brush)FindResource("DashboardMuted"),
                Margin = new Thickness(0, 12, 0, 5)
            });
        }
    }

    /// <summary>
    /// 独立展示当前模型配额及重置时间，保留未知值，不从比例推算 Token。
    /// </summary>
    private void RenderQuotaPage()
    {
        QuotaModelsPanel.Children.Clear();
        QuotaUpdatedText.Text = string.Empty;
        if (!_appSettingsService.Settings.AntigravityUsageEnabled)
        {
            QuotaStatusText.Text = "在设置中启用本地 Antigravity，即可读取模型用量和当前配额。";
            return;
        }

        if (_snapshot?.AntigravityQuota is not { } quota)
        {
            QuotaStatusText.Text = _snapshot?.AntigravityStatusMessage ?? "等待读取 Antigravity，请保持 IDE 运行并登录。";
            return;
        }

        QuotaUpdatedText.Text = $"{quota.RetrievedAt.LocalDateTime:HH:mm:ss} 更新";
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(quota.PlanName))
        {
            details.Add(quota.PlanName);
        }

        if (quota.AvailablePromptCredits is { } credits)
        {
            details.Add(quota.MonthlyPromptCredits is { } monthly
                ? $"积分剩余 {credits:N0} / {monthly:N0}"
                : $"积分剩余 {credits:N0}");
        }

        details.Add(quota.Models.Count == 0 ? "未返回模型配额" : "模型剩余比例 · 本地重置时间");
        QuotaStatusText.Text = string.Join(" · ", details);
        foreach (var model in quota.Models)
        {
            QuotaModelsPanel.Children.Add(CreateQuotaRow(model));
        }
    }

    /// <summary>
    /// 用剩余比例绘制配额条，同时显示完整模型名和本地重置时间。
    /// </summary>
    private FrameworkElement CreateQuotaRow(AntigravityModelQuota model)
    {
        var row = new Grid { Margin = new Thickness(0, 1, 0, 4) };
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = string.IsNullOrWhiteSpace(model.Label) ? model.Model : model.Label;
        var remaining = model.RemainingFraction is { } fraction ? Math.Clamp(fraction, 0m, 1m) : (decimal?)null;
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = (Brush)FindResource("DashboardText"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = $"{label}\n{model.Model}",
            Margin = new Thickness(0, 0, 10, 0)
        });
        var percentage = new TextBlock
        {
            Text = remaining is { } value ? $"{value:P0}" : "未知",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource(remaining is <= 0.2m ? "DashboardOrange" : "DashboardGreen")
        };
        Grid.SetColumn(percentage, 1);
        row.Children.Add(percentage);
        var reset = new TextBlock
        {
            Text = FormatQuotaReset(model.ResetAt),
            FontSize = 9,
            Foreground = (Brush)FindResource("DashboardMuted"),
            Margin = new Thickness(0, 2, 0, 4)
        };
        Grid.SetRow(reset, 1);
        Grid.SetColumnSpan(reset, 2);
        row.Children.Add(reset);
        var track = new Grid { Height = 6 };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength((double)(remaining ?? 0m), GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength((double)(1m - (remaining ?? 0m)), GridUnitType.Star) });
        track.Children.Add(new Border
        {
            Background = percentage.Foreground,
            CornerRadius = new CornerRadius(3)
        });
        var trackShell = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = (Brush)FindResource("DashboardTrack"),
            ClipToBounds = true,
            Child = track
        };
        Grid.SetRow(trackShell, 2);
        Grid.SetColumnSpan(trackShell, 2);
        row.Children.Add(trackShell);
        return row;
    }

    private static string FormatQuotaReset(DateTimeOffset? resetAt)
    {
        if (resetAt is null)
        {
            return "重置时间未提供";
        }

        var remaining = resetAt.Value - DateTimeOffset.Now;
        var countdown = remaining <= TimeSpan.Zero ? "等待刷新" : remaining.TotalHours >= 24
            ? $"约 {remaining.TotalDays:0.#} 天后"
            : remaining.TotalHours >= 1 ? $"约 {remaining.TotalHours:0.#} 小时后" : $"约 {Math.Ceiling(remaining.TotalMinutes):0} 分钟后";
        return $"{resetAt.Value.LocalDateTime:MM-dd HH:mm} 重置 · {countdown}";
    }

    private TokenTotals ActiveTotal() =>
        _customRange is null ? _snapshot!.TotalFor(_period) : _snapshot!.TotalFor(_customRange);

    private bool IsUsageUnavailable(TokenTotals totals) =>
        _appSettingsService.Settings.AntigravityUsageEnabled &&
        !_appSettingsService.Settings.IsNewApiConfigured &&
        _snapshot?.AntigravityUsageIsComplete != true &&
        totals.RequestCount == 0 && totals.TotalTokens == 0;

    private UsageRates ActiveRates() =>
        _customRange is null ? _snapshot!.RatesFor(_period) : _snapshot!.RatesFor(_customRange);

    private PricingEstimate ActivePricing() =>
        _customRange is null
            ? _pricingService.Estimate(_snapshot!, _period)
            : _pricingService.Estimate(_snapshot!, _customRange);

    private string BuildTraySummary()
    {
        var total = _snapshot!.TotalFor(UsagePeriod.Today);
        if (IsUsageUnavailable(total))
        {
            return "Antigravity Token 暂不可用，打开面板查看状态";
        }

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
            ? _pricingService.Trend(_snapshot, _period, _trendModelFilter)
            : _pricingService.Trend(_snapshot, _customRange, _trendModelFilter);
        var maximum = trend.Points.Count == 0 ? 0 : trend.Points.Max(TrendValue);
        _trendPoints = trend.Points;
        _trendMaximum = maximum;
        TrendTitleText.Text = trend.Title;
        var trendPricing = new PricingEstimate(
            trend.Points.Sum(point => point.Pricing.EstimatedUsd),
            trend.Points.Sum(point => point.Pricing.PricedRequestCount),
            trend.Points.Sum(point => point.Pricing.UnpricedRequestCount),
            trend.Points.SelectMany(point => point.Pricing.UnpricedModels).Distinct(StringComparer.Ordinal).ToArray());
        var usageUnavailable = IsUsageUnavailable(ActiveTotal());
        var costUnavailable = _trendMetric == TrendMetric.Cost && !trendPricing.HasPricedUsage && !trendPricing.IsComplete;
        TrendTotalText.Text = usageUnavailable ? "用量未提供" : _trendMetric switch
        {
            TrendMetric.Tokens => $"总计 {FormatTokens(trend.Points.Sum(point => point.Tokens))}",
            TrendMetric.Requests => $"总计 {FormatCount(trend.Points.Sum(point => point.RequestCount))}",
            _ => $"总计 {FormatCost(trendPricing)}"
        };
        TrendTotalText.ToolTip = _trendMetric == TrendMetric.Cost ? BuildCostTooltip(trendPricing) : _snapshot.AntigravityUsageMessage;
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
                Stroke = (Brush)FindResource("DashboardBorder"),
                StrokeThickness = 1
            });

            var axisLabel = new TextBlock
            {
                Text = usageUnavailable || costUnavailable ? "—" : FormatTrendAxisValue(maximum * (1 - ratio)),
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
        if (_snapshot is not null && IsUsageUnavailable(ActiveTotal()))
        {
            TrendPeakText.Text = string.Empty;
            return;
        }

        if (_trendMaximum <= 0)
        {
            TrendPeakText.Text = _trendMetric == TrendMetric.Cost && _trendPoints.Any(item => item.Pricing.UnpricedRequestCount > 0)
                ? "费用未提供"
                : _trendMetric == TrendMetric.Cost && _trendPoints.Any(item => item.RequestCount > 0)
                ? "暂无消耗"
                : "暂无记录";
            return;
        }

        var peak = _trendPoints.MaxBy(TrendValue)!;
        TrendPeakText.Text = _trendMetric switch
        {
            TrendMetric.Tokens => $"峰 {FormatTokens(peak.Tokens)}",
            TrendMetric.Requests => $"峰 {FormatCount(peak.RequestCount)}",
            _ => $"{(_trendPoints.Any(item => !item.Pricing.IsComplete) ? "已知峰" : "峰")} {FormatCost(peak.Pricing)}"
        };
    }

    private double TrendValue(PricingTrendPoint point) =>
        _trendMetric switch
        {
            TrendMetric.Tokens => point.Tokens,
            TrendMetric.Requests => point.RequestCount,
            _ => (double)point.Pricing.EstimatedUsd
        };

    private double TrendPointX(int index, int pointCount, double plotWidth) =>
        _trendChartStyle == TrendChartStyle.Bars
            ? TrendPlotLeft + (index + 0.5) * plotWidth / pointCount
            : TrendPlotLeft + index * plotWidth / Math.Max(1, pointCount - 1);

    private Color TrendAccentColor() =>
        _trendMetric switch
        {
            TrendMetric.Tokens => ((SolidColorBrush)FindResource("DashboardBlue")).Color,
            TrendMetric.Requests => ((SolidColorBrush)FindResource("DashboardViolet")).Color,
            _ => ((SolidColorBrush)FindResource("DashboardOrange")).Color
        };

    private string FormatTrendAxisValue(double value) =>
        _trendMetric switch
        {
            TrendMetric.Tokens => FormatTokens((long)Math.Round(value)),
            TrendMetric.Requests => FormatCount((long)Math.Round(value)),
            _ => value >= 100 ? $"${value:0}" : $"${value:0.##}"
        };

    private void TrendMetricButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse(tag, out TrendMetric metric))
        {
            return;
        }

        _trendMetric = metric;
        UpdateTrendMetricButtons();
        SaveUiState();
        DrawUsageTrend();
    }

    private void UpdateTrendMetricButtons()
    {
        SetSegmentButtonState(TrendTokensButton, _trendMetric == TrendMetric.Tokens);
        SetSegmentButtonState(TrendRequestsButton, _trendMetric == TrendMetric.Requests);
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
        SaveUiState();
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
        var modelPricing = ActiveModelPricing();
        var ranking = ActiveModelsFor()
            .Select(model => new ModelRankingEntry(
                model,
                modelPricing.GetValueOrDefault(model.Model) ?? ZeroPricing))
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

    private IReadOnlyDictionary<string, PricingEstimate> ActiveModelPricing() =>
        _customRange is null
            ? _pricingService.EstimateModels(_snapshot!, _period)
            : _pricingService.EstimateModels(_snapshot!, _customRange);

    /// <summary>
    /// 点击模型行后仅按该模型过滤趋势图，并切换到趋势标签页。
    /// </summary>
    private void ApplyTrendModelFilter(string? model)
    {
        _trendModelFilter = string.IsNullOrWhiteSpace(model) ? null : model;
        UpdateTrendFilterChip();
        SaveUiState();
        _normalTab = NormalTab.Trend;
        UpdateNormalTabButtons();
        DrawUsageTrend();
        ModelRankingPopup.IsOpen = false;
    }

    private void TrendModelFilterButton_Click(object sender, RoutedEventArgs e) =>
        ApplyTrendModelFilter(null);

    private void UpdateTrendFilterChip()
    {
        TrendModelFilterButton.Visibility = _trendModelFilter is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (_trendModelFilter is not null)
        {
            TrendModelFilterButton.Content = $"✕ {_trendModelFilter}";
        }
    }

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

        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
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

        var row = new Border
        {
            Padding = new Thickness(4, 1, 4, 1),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "点击查看该模型趋势",
            Child = grid
        };
        row.MouseEnter += (_, _) => row.Background = (Brush)FindResource("DashboardGreenSoft");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonDown += (_, _) => ApplyTrendModelFilter(item.Model.Model);
        return row;
    }

    private string PeriodShortText() => _customRange?.Title ?? (_period switch
    {
        UsagePeriod.Today => "今日",
        UsagePeriod.Week => "本周",
        _ => "本月"
    });

    public void ShowFromTray()
    {
        Topmost = _appSettingsService.Settings.KeepWindowOnTop;
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
        var activeBackground = (Brush)FindResource("DashboardPanel");
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
        Canvas.SetLeft(text, 2);
        Canvas.SetTop(text, 22);
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

        var value = FormatUsd(estimate.EstimatedUsd);
        return estimate.IsComplete ? value : $"≥{value}";
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
        if (estimate.IsQuotaBased && estimate.IsComplete)
        {
            return PricingService.QuotaNotice;
        }

        if (estimate.PricedRequestCount == 0 && estimate.UnpricedRequestCount == 0)
        {
            return string.Empty;
        }

        if (estimate.IsComplete)
        {
            return PricingService.PricingNotice;
        }

        return $"仅显示已有计费信息的消耗。{PricingService.PricingNotice}\n未计价模型：{string.Join("、", estimate.UnpricedModels)}";
    }

    private string BuildSourceStatus()
    {
        var sources = new List<string>();
        if (_appSettingsService.Settings.IsNewApiConfigured)
        {
            sources.Add("NewAPI");
        }

        if (_appSettingsService.Settings.AntigravityUsageEnabled)
        {
            sources.Add("Antigravity");
        }

        return sources.Count == 0 ? "未配置来源" : string.Join(" + ", sources);
    }

    private static string BuildSourceTooltip(UsageSnapshot snapshot)
    {
        var lines = new List<string>();
        if (snapshot.Providers.Count > 0)
        {
            lines.Add($"Token 统计：{string.Join("、", snapshot.Providers.Select(item => item.Provider))}");
        }

        if (snapshot.AntigravityQuota is { } quota)
        {
            lines.Add($"Antigravity 配额：{FormatAntigravityQuota(quota)}");
        }

        if (!string.IsNullOrWhiteSpace(snapshot.AntigravityUsageMessage))
        {
            lines.Add(snapshot.AntigravityUsageMessage);
        }

        if (!string.IsNullOrWhiteSpace(snapshot.SourceMessage))
        {
            lines.Add(snapshot.SourceMessage);
        }

        return lines.Count == 0 ? "尚未读取到数据来源" : string.Join("\n", lines);
    }

    private static string FormatAntigravityQuota(AntigravityQuotaSnapshot snapshot)
    {
        var models = snapshot.Models
            .Select(item => (item, Remaining: item.RemainingFraction))
            .Where(item => item.Remaining is not null)
            .Take(5)
            .Select(item => $"{item.item.Label ?? item.item.Model} {item.Remaining!.Value:P0}");
        return string.Join("、", models);
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
        Requests,
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
        Model,
        Quota
    }

    private sealed record ModelRankingEntry(
        ModelUsage Model,
        PricingEstimate Pricing);

    private static readonly PricingEstimate ZeroPricing = new(0m, 0, 0, []);
}
