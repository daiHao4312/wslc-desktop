using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WSLC.Panel.Core.Abstractions;
using WSLC.Panel.Core.Parsing;
using WSLC_Panel_App.Helpers;

namespace WSLC_Panel_App.Pages;

public sealed partial class OverviewPage : Page, IRefreshable
{
    private readonly IWslcClient _client;
    private readonly CancellationTokenSource _pageCts = new();

    public OverviewPage()
    {
        InitializeComponent();
        _client = App.Services.GetRequiredService<IWslcClient>();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await RefreshAsync(userInitiated: false);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // 离开页面即取消在途刷新：一次概览刷新会拉起 8+ 个 CLI 进程，
        // 不取消的话切页后这些进程和续延都在旧页面实例上跑
        _pageCts.Cancel();
    }

    public async Task RefreshAsync(bool userInitiated)
    {
        try
        {
            var infoTask = _client.GetSystemInfoAsync(_pageCts.Token);
            var containersTask = _client.GetContainersAsync(all: true, _pageCts.Token);
            var imagesTask = _client.GetImagesAsync(_pageCts.Token);
            var networksTask = _client.GetNetworksAsync(_pageCts.Token);
            var volumesTask = _client.GetVolumesAsync(_pageCts.Token);
            var sessionsTask = _client.GetSessionsAsync(_pageCts.Token);

            await Task.WhenAll(infoTask, containersTask, imagesTask, networksTask, volumesTask, sessionsTask);

            var info = await infoTask;
            var containers = await containersTask;
            var images = await imagesTask;
            var networks = await networksTask;
            var volumes = await volumesTask;
            var sessions = await sessionsTask;

            RunningContainersText.Text = containers.Count(c => c.IsRunning).ToString();
            TotalContainersText.Text = containers.Count.ToString();
            TotalImagesText.Text = images.Count.ToString();
            TotalNetworksText.Text = networks.Count.ToString();
            TotalVolumesText.Text = volumes.Count.ToString();
            ActiveSessionsText.Text = sessions.Count.ToString();

            WslVersionText.Text = Fallback(info.WslVersion, "未检测到");
            KernelVersionText.Text = Fallback(info.KernelVersion, "未知");
            SessionManagerVersionText.Text = Fallback(info.SessionManagerVersion, "未知");
            WindowsVersionText.Text = Fallback(info.WindowsVersion, Environment.OSVersion.VersionString);
            SettingsFileText.Text = Fallback(info.SettingsFilePath, "未检测到");

            SessionsListView.ItemsSource = sessions;
            SessionsEmptyText.Visibility = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            TotalStorageText.Text = sessions.Count == 0
                ? "—"
                : SessionStorageLocator.FormatBytes(SessionStorageLocator.GetTotalUsageBytes(sessions));

            SessionsRootText.Text = SessionStorageLocator.GetSessionsRoot();

            ErrorInfoBar.IsOpen = false;
        }
        catch (OperationCanceledException)
        {
            // 页面已离开：静默收尾
        }
        catch (Exception ex)
        {
            // 页面级错误直接可见（§7.3-5），不吞掉
            ShowError($"读取概览数据失败：{ex.Message}");
        }
    }

    private void ShowError(string message)
    {
        ErrorInfoBar.Title = "数据读取失败";
        ErrorInfoBar.Message = message;
        ErrorInfoBar.IsOpen = true;
    }

    private static string Fallback(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;
}
