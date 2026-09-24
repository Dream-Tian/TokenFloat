using System.Text.Json;
using System.Windows;
using TokenFloat.Models;
using TokenFloat.Services;

namespace TokenFloat;

public partial class App : System.Windows.Application
{
    private readonly ErrorLogService _errorLogService = new();
    private readonly AppSettingsService _appSettingsService;
    private readonly LocalDataService _localDataService = new();
    private readonly UsageLogService _usageLogService;
    private TrayIconService? _trayIcon;
    private SingleInstanceService? _singleInstance;
    private SettingsWindow? _settingsWindow;
    private UpdateService? _updateService;

    public bool IsExiting { get; private set; }

    public App()
    {
        _appSettingsService = new AppSettingsService();
        _usageLogService = new UsageLogService(_appSettingsService);
        DispatcherUnhandledException += (_, args) =>
            _errorLogService.Write("DispatcherUnhandledException", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                _errorLogService.Write("AppDomain.UnhandledException", exception);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _errorLogService.Write("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>
    /// 验证模式只输出统计；正常模式创建悬浮窗和系统托盘入口。
    /// </summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var isVerificationMode = e.Args.Contains("--verify-usage", StringComparer.OrdinalIgnoreCase) ||
                                 e.Args.Contains("--verify-antigravity", StringComparer.OrdinalIgnoreCase) ||
                                 e.Args.Contains("--verify-update-manifest", StringComparer.OrdinalIgnoreCase);
        if (!isVerificationMode)
        {
            _singleInstance = new SingleInstanceService();
            if (!_singleInstance.IsPrimary)
            {
                _singleInstance.SignalPrimary();
                Shutdown();
                return;
            }
        }

        var updateManifestIndex = Array.FindIndex(
            e.Args,
            item => string.Equals(item, "--verify-update-manifest", StringComparison.OrdinalIgnoreCase));
        if (updateManifestIndex >= 0 && updateManifestIndex + 1 < e.Args.Length)
        {
            var updateResult = await new UpdateService().CheckManifestAsync(e.Args[updateManifestIndex + 1]);
            Console.WriteLine(JsonSerializer.Serialize(updateResult));
            Shutdown();
            return;
        }

        if (e.Args.Contains("--verify-antigravity", StringComparer.OrdinalIgnoreCase))
        {
            var quotaService = new AntigravityQuotaService();
            var quotaResult = await quotaService.ReadAsync();
            var usageResult = await new AntigravityUsageService(quotaService.Client).ReadAsync();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                QuotaMessage = quotaResult.Message,
                Quota = quotaResult.Snapshot,
                UsageMessage = usageResult.Message,
                usageResult.IsComplete,
                usageResult.IsUnavailable,
                Records = usageResult.Events.Count,
                InputTokens = usageResult.Events.Sum(item => item.InputTokens),
                ReportedInputTokens = usageResult.Events.Sum(item => item.ReportedInputTokens ?? item.InputTokens),
                OutputTokens = usageResult.Events.Sum(item => item.OutputTokens),
                CacheReadTokens = usageResult.Events.Sum(item => item.CachedInputTokens),
                CacheWriteTokens = usageResult.Events.Sum(item => item.CacheWriteInputTokens)
            }));
            Shutdown();
            return;
        }

        if (e.Args.Contains("--verify-usage", StringComparer.OrdinalIgnoreCase))
        {
            var appSettingsService = new AppSettingsService();
            var snapshot = await new UsageLogService(appSettingsService).LoadSnapshotAsync();
            var pricingService = new PricingService();
            var result = new
            {
                NewApiConfigured = appSettingsService.Settings.IsNewApiConfigured,
                NewApiBaseUrl = appSettingsService.Settings.NewApiBaseUrl,
                appSettingsService.Settings.AntigravityUsageEnabled,
                snapshot.AntigravityUsageMessage,
                snapshot.AntigravityUsageIsComplete,
                snapshot.AntigravityStatusMessage,
                snapshot.AntigravityQuota,
                snapshot.SourceMessage,
                TodayRates = snapshot.RatesFor(UsagePeriod.Today),
                Totals = Enum.GetValues<UsagePeriod>().ToDictionary(
                    period => period.ToString(),
                    period => snapshot.TotalFor(period)),
                TrendTotals = new
                {
                    Today = snapshot.TrendFor(UsagePeriod.Today).Points.Sum(item => item.Tokens),
                    Week = snapshot.TrendFor(UsagePeriod.Week).Points.Sum(item => item.Tokens),
                    Month = snapshot.TrendFor(UsagePeriod.Month).Points.Sum(item => item.Tokens)
                },
                Pricing = Enum.GetValues<UsagePeriod>().ToDictionary(
                    period => period.ToString(),
                    period => pricingService.Estimate(snapshot, period)),
                PricingTrendTotals = Enum.GetValues<UsagePeriod>().ToDictionary(
                    period => period.ToString(),
                    period => new
                    {
                        Tokens = pricingService.Trend(snapshot, period).Points.Sum(item => item.Tokens),
                        Requests = pricingService.Trend(snapshot, period).Points.Sum(item => item.RequestCount),
                        EstimatedUsd = pricingService.Trend(snapshot, period).Points.Sum(item => item.Pricing.EstimatedUsd)
                    }),
                TodayModels = snapshot.ModelsFor(UsagePeriod.Today)
                    .Select(model => new
                    {
                        model.Model,
                        Tokens = model.Totals.TotalTokens,
                        Requests = model.Totals.RequestCount,
                        model.Totals.Quota
                    }),
                MonthModelPricing = pricingService.EstimateModels(snapshot, UsagePeriod.Month)
            };
            Console.WriteLine(JsonSerializer.Serialize(result));
            Shutdown();
            return;
        }

        var window = new MainWindow(_usageLogService, _appSettingsService);
        MainWindow = window;
        _updateService = new UpdateService();
        _trayIcon = new TrayIconService(
            () => Dispatcher.Invoke(ShowMainWindow),
            () => Dispatcher.Invoke(ShowSettingsWindow),
            () => Dispatcher.Invoke(ExitApplication),
            new StartupService(),
            _updateService,
            _errorLogService);
        window.TraySummaryChanged += summary => _trayIcon?.UpdateSummary(summary);
        window.SettingsRequested += ShowSettingsWindow;
        window.RefreshCompleted += duration => _settingsWindow?.SetLastRefreshDuration(duration);
        _singleInstance?.StartListening(() => Dispatcher.Invoke(ShowMainWindow));
        window.Show();
        _ = _trayIcon.CheckForUpdatesOnStartupAsync();
    }

    private async Task<string?> DownloadUpdateAsync(UpdateInfo update, UpdateService updateService)
    {
        var progressWindow = new UpdateProgressWindow(update);
        if (_settingsWindow is { IsVisible: true } settingsOwner)
        {
            progressWindow.Owner = settingsOwner;
        }
        else if (MainWindow is { IsVisible: true } owner)
        {
            progressWindow.Owner = owner;
        }

        progressWindow.Show();
        try
        {
            return await progressWindow.DownloadAsync(updateService);
        }
        finally
        {
            progressWindow.CloseAfterDownload();
        }
    }

    private void ShowMainWindow()
    {
        if (MainWindow is not TokenFloat.MainWindow window)
        {
            return;
        }

        window.ShowFromTray();
    }

    /// <summary>
    /// 复用同一个设置窗口，并把更新下载交给现有的校验与进度流程。
    /// </summary>
    private void ShowSettingsWindow()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Show();
            _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }

        if (_updateService is null)
        {
            return;
        }

        _settingsWindow = new SettingsWindow(
            _updateService,
            _appSettingsService,
            _usageLogService,
            _localDataService,
            update => DownloadUpdateAsync(update, _updateService),
            () => (MainWindow as TokenFloat.MainWindow)?.ClearUsageCacheAsync() ?? Task.FromResult(false),
            () => (MainWindow as TokenFloat.MainWindow)?.RefreshUsageAsync() ?? Task.FromResult(false),
            ExitApplication,
            _errorLogService,
            () => (MainWindow as TokenFloat.MainWindow)?.LastRefreshDuration);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void ExitApplication()
    {
        IsExiting = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
        _settingsWindow?.Close();
        _settingsWindow = null;
        MainWindow?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _singleInstance?.Dispose();
        _singleInstance = null;
        base.OnExit(e);
    }
}
