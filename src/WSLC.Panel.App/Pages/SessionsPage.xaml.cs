using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WSLC.Panel.Core.Abstractions;
using WSLC_Panel_App.Helpers;

namespace WSLC_Panel_App.Pages;

public sealed partial class SessionsPage : Page, IRefreshable
{
    private readonly IWslcClient _client;
    private readonly CancellationTokenSource _pageCts = new();

    public SessionsPage()
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
        // 离开页面即取消在途刷新：续延不再操作已卸载的 UI
        _pageCts.Cancel();
    }

    public async Task RefreshAsync(bool userInitiated)
    {
        if (userInitiated) LoadingBar.Visibility = Visibility.Visible;

        try
        {
            var sessions = await _client.GetSessionsAsync(_pageCts.Token);
            SessionsListView.ItemsSource = sessions;

            var isEmpty = sessions.Count == 0;
            EmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            SessionsListView.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;

            ErrorInfoBar.IsOpen = false;
        }
        catch (OperationCanceledException)
        {
            // 页面已离开：静默收尾
        }
        catch (Exception ex)
        {
            ErrorInfoBar.Title = "读取会话失败";
            ErrorInfoBar.Message = ex.Message;
            ErrorInfoBar.IsOpen = true;
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await RefreshAsync(userInitiated: true);

    private void RevealStorage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;

        if (!TerminalLauncher.RevealInExplorer(path))
        {
            _ = DialogHelper.ShowErrorAsync(XamlRoot, "无法打开资源管理器", $"请手动查看：{path}");
        }
    }

    private async void TerminateSession_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int sessionId }) return;

        // 后果必须写清楚（§5.3 会话模型认知）：容器被停下，但数据在 VHD 里保留
        var confirmed = await DialogHelper.ConfirmDestructiveAsync(
            XamlRoot,
            "终止会话？",
            $"会立即停止会话 #{sessionId} 中所有正在运行的容器，容器状态变为“已退出”。\n会话的虚拟磁盘和其中的镜像、容器、卷数据都会保留，下次启动容器时自动恢复。",
            "终止会话");

        if (!confirmed) return;

        LoadingBar.Visibility = Visibility.Visible;
        try
        {
            var ok = await _client.TerminateSessionAsync(sessionId);
            if (!ok)
            {
                _ = DialogHelper.ShowErrorAsync(XamlRoot, "终止会话失败", "引擎拒绝终止该会话（可能是当前容器操作的宿主会话）。");
            }
        }
        catch (Exception ex)
        {
            _ = DialogHelper.ShowErrorAsync(XamlRoot, "终止会话失败", ex.Message);
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            await RefreshAsync(userInitiated: false);
        }
    }
}
