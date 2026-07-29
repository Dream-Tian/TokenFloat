using System.IO;
using System.Text.Json;
using System.Windows;
using TokenFloat.Models;
using TokenFloat.Services;

namespace TokenFloat;

public partial class App : System.Windows.Application
{
    private TrayIconService? _trayIcon;

    public bool IsExiting { get; private set; }

    /// <summary>
    /// 验证模式只输出统计；正常模式创建悬浮窗和系统托盘入口。
    /// </summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        if (e.Args.Contains("--verify-usage", StringComparer.OrdinalIgnoreCase))
        {
            var snapshot = await new UsageLogService().LoadSnapshotAsync();
            var pricingService = new PricingService();
            var providers = snapshot.Providers.Select(provider => new
            {
                provider.Provider,
                Today = provider.For(UsagePeriod.Today).TotalTokens,
                Week = provider.For(UsagePeriod.Week).TotalTokens,
                Month = provider.For(UsagePeriod.Month).TotalTokens,
                TodayRequests = provider.For(UsagePeriod.Today).RequestCount,
                TodayModels = snapshot.ModelsFor(provider.Provider, UsagePeriod.Today)
                    .Select(model => new
                    {
                        model.Model,
                        Tokens = model.Totals.TotalTokens,
                        Requests = model.Totals.RequestCount
                    }),
                MonthModels = snapshot.ModelsFor(provider.Provider, UsagePeriod.Month)
                    .Select(model => new
                    {
                        model.Model,
                        Tokens = model.Totals.TotalTokens,
                        Requests = model.Totals.RequestCount,
                        Pricing = pricingService.EstimateModel(
                            snapshot,
                            provider.Provider,
                            model.Model,
                            UsagePeriod.Month)
                    }),
                EffectiveToday = provider.For(UsagePeriod.Today).EffectiveTokens,
                CachedToday = provider.For(UsagePeriod.Today).CachedInputTokens,
                provider.HasData
            });
            var userProfile = Environment.GetEnvironmentVariable("USERPROFILE")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var result = new
            {
                UserProfile = userProfile,
                CodexRootExists = Directory.Exists(Path.Combine(userProfile, ".codex", "sessions")),
                TodayRates = snapshot.RatesFor(UsagePeriod.Today),
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
                Providers = providers
            };
            Console.WriteLine(JsonSerializer.Serialize(result));
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        var updateService = new UpdateService();
        _trayIcon = new TrayIconService(
            () => Dispatcher.Invoke(ShowMainWindow),
            () => Dispatcher.Invoke(ExitApplication),
            new StartupService(),
            updateService);
        window.TraySummaryChanged += summary => _trayIcon?.UpdateSummary(summary);
        window.Show();
        _ = _trayIcon.CheckForUpdatesOnStartupAsync();
    }

    private void ShowMainWindow()
    {
        if (MainWindow is not TokenFloat.MainWindow window)
        {
            return;
        }

        window.ShowFromTray();
    }

    private void ExitApplication()
    {
        IsExiting = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
        MainWindow?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
