using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WSLC.Panel.Core.Abstractions;
using WSLC.Panel.Core.Models;
using WSLC_Panel_App.Helpers;

namespace WSLC_Panel_App.Pages;

public sealed partial class NetworksPage : Page, IRefreshable
{
    private readonly IWslcClient _client;
    private readonly CancellationTokenSource _pageCts = new();

    public NetworksPage()
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

        // 换 ItemsSource 会清空选中项，先记下来，后台刷新不能打断用户的勾选
        var selection = CaptureSelection();

        try
        {
            var networks = await _client.GetNetworksAsync(_pageCts.Token);
            NetworksListView.ItemsSource = networks;

            foreach (var network in networks)
            {
                if (selection.Contains(network.Name))
                {
                    NetworksListView.SelectedItems.Add(network);
                }
            }

            var isEmpty = networks.Count == 0;
            EmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            NetworksListView.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;

            ErrorInfoBar.IsOpen = false;
        }
        catch (OperationCanceledException)
        {
            // 页面已离开：静默收尾
        }
        catch (Exception ex)
        {
            ErrorInfoBar.Title = "读取网络失败";
            ErrorInfoBar.Message = ex.Message;
            ErrorInfoBar.IsOpen = true;
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            UpdateBatchBar();
        }
    }

    // ---------- 批量选择 ----------

    private HashSet<string> CaptureSelection()
        => NetworksListView.SelectedItems.OfType<NetworkModel>()
            .Select(n => n.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void NetworksListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 程序化改动选择（取消选择、刷新后回填）不会让已实现的行重新触发 Loaded，这里统一补一次
        RowSelectionHelper.Sync(NetworksListView, e);
        UpdateBatchBar();
    }

    private void RowCheckBox_Loaded(object sender, RoutedEventArgs e)
    {
        // 行被实例化或回收复用时，按真实选择态摆正勾选框
        if (sender is CheckBox box && box.DataContext is not null)
        {
            box.IsChecked = NetworksListView.SelectedItems.Contains(box.DataContext);
        }
    }

    private void RowCheckBox_Click(object sender, RoutedEventArgs e)
        => RowSelectionHelper.Toggle(NetworksListView, (sender as FrameworkElement)?.DataContext);

    private void UpdateBatchBar()
    {
        var count = NetworksListView.SelectedItems.Count;

        BatchSelectionText.Text = $"已选 {count} 项";

        if (count > 0) BatchBarAnimator.Show(BatchBar);
        else BatchBarAnimator.Hide(BatchBar);
    }

    private void BatchClearSelection_Click(object sender, RoutedEventArgs e)
    {
        NetworksListView.SelectedItems.Clear();
        UpdateBatchBar();
    }

    private async void BatchDelete_Click(object sender, RoutedEventArgs e)
    {
        var selected = NetworksListView.SelectedItems.OfType<NetworkModel>().ToList();
        if (selected.Count == 0) return;

        var removable = selected.Where(n => n.IsRemovable).ToList();
        var builtIn = selected.Where(n => !n.IsRemovable).ToList();

        if (removable.Count == 0)
        {
            // 全是被引擎保护的内置网络：说清楚为什么不动，而不是静默失败
            ErrorInfoBar.Title = "未执行删除";
            ErrorInfoBar.Message = $"选中的网络都是内置网络（{string.Join("、", builtIn.Select(n => n.Name))}），引擎不允许删除。";
            ErrorInfoBar.IsOpen = true;
            return;
        }

        var detail = $"将删除：{string.Join("、", removable.Select(n => n.Name))}\n\n若有容器仍连接，该项会失败（需先断开容器）。\n此操作不可撤销。";
        if (builtIn.Count > 0)
        {
            detail += $"\n\n以下内置网络会自动跳过：{string.Join("、", builtIn.Select(n => n.Name))}";
        }

        var confirmed = await DialogHelper.ConfirmDestructiveAsync(
            XamlRoot,
            $"删除 {removable.Count} 个网络？",
            detail,
            $"删除 {removable.Count} 个");

        if (!confirmed) return;

        LoadingBar.Visibility = Visibility.Visible;
        var failed = new List<string>();

        try
        {
            foreach (var item in removable)
            {
                try
                {
                    if (!await _client.RemoveNetworkAsync(item.Name)) failed.Add(item.Name);
                }
                catch (Exception ex)
                {
                    failed.Add($"{item.Name}（{ex.Message}）");
                }
            }
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
        }

        NetworksListView.SelectedItems.Clear();
        UpdateBatchBar();

        if (failed.Count > 0)
        {
            ErrorInfoBar.Title = "批量删除部分失败";
            ErrorInfoBar.Message = $"{removable.Count - failed.Count}/{removable.Count} 成功；失败：{string.Join("、", failed)}（通常是有容器仍连接该网络）";
            ErrorInfoBar.IsOpen = true;
        }

        await RefreshAsync(userInitiated: false);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await RefreshAsync(userInitiated: true);

    private async void CreateNetwork_Click(object sender, RoutedEventArgs e)
    {
        var name = await DialogHelper.PromptTextAsync(
            XamlRoot,
            "新建网络",
            "网络名称，例如 app-net",
            "创建",
            "驱动固定使用默认的 bridge。");

        if (string.IsNullOrWhiteSpace(name)) return;

        LoadingBar.Visibility = Visibility.Visible;
        try
        {
            var ok = await _client.CreateNetworkAsync(name, "bridge");
            if (!ok)
            {
                _ = DialogHelper.ShowErrorAsync(XamlRoot, "创建网络失败", "引擎拒绝创建该网络，可能同名网络已存在。");
            }
        }
        catch (Exception ex)
        {
            _ = DialogHelper.ShowErrorAsync(XamlRoot, "创建网络失败", ex.Message);
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            await RefreshAsync(userInitiated: false);
        }
    }

    private async void RemoveNetwork_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name }) return;

        var confirmed = await DialogHelper.ConfirmDestructiveAsync(
            XamlRoot,
            "删除网络？",
            $"将删除网络 {name}。若有容器仍连接该网络，删除会失败（请先断开容器）。\n此操作不可撤销。",
            "删除网络");

        if (!confirmed) return;

        LoadingBar.Visibility = Visibility.Visible;
        try
        {
            var ok = await _client.RemoveNetworkAsync(name);
            if (!ok)
            {
                _ = DialogHelper.ShowErrorAsync(XamlRoot, "删除网络失败", "仍有容器连接该网络，或该网络受引擎保护。");
            }
        }
        catch (Exception ex)
        {
            _ = DialogHelper.ShowErrorAsync(XamlRoot, "删除网络失败", ex.Message);
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            await RefreshAsync(userInitiated: false);
        }
    }
}
