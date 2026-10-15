using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Dialogs;
using WslcDesktop.Helpers;

namespace WslcDesktop.Pages;

public sealed partial class ContainersPage : Page, IRefreshable
{
    private readonly IWslcClient _client;
    private readonly CancellationTokenSource _pageCts = new();

    /// <summary>
    /// 行的唯一真相来源，整个页面生命周期只创建一次。
    /// 刷新走"就地增删改"而不是换 ItemsSource，见 <see cref="SyncRows"/>。
    /// </summary>
    private readonly ObservableCollection<ContainerModel> _rows = new();

    private bool _isBusy;

    public ContainersPage()
    {
        InitializeComponent();
        _client = App.Services.GetRequiredService<IWslcClient>();
        ContainersListView.ItemsSource = _rows;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await RefreshAsync(userInitiated: false);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // 离开页面即取消在途的后台刷新：续延不再操作已卸载的 UI，旧实例也能被回收。
        // 用户主动发起的批量/单项操作不传此 token，切页不影响它们跑完
        _pageCts.Cancel();
    }

    public async Task RefreshAsync(bool userInitiated)
    {
        // 动作执行中的应用刷新会打架：正在跑启停时跳过轮询
        if (_isBusy)
        {
            // 忙时不是静默吞掉：说清楚点了没反应的原因
            if (userInitiated)
            {
                ErrorInfoBar.Title = "操作进行中";
                ErrorInfoBar.Message = "容器操作正在进行，完成后会自动刷新列表。";
                ErrorInfoBar.Severity = InfoBarSeverity.Informational;
                ErrorInfoBar.IsOpen = true;
            }
            return;
        }

        if (userInitiated) LoadingBar.Visibility = Visibility.Visible;

        // 列表整体换 ItemsSource 会把选中项清空；后台刷新若这么做，用户勾到一半就被打断
        var selection = CaptureSelection();

        try
        {
            var containers = await _client.GetContainersAsync(all: true, _pageCts.Token);
            SyncRows(containers);
            RestoreSelection(selection);

            var isEmpty = containers.Count == 0;
            EmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            ContainersListView.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;

            ErrorInfoBar.IsOpen = false;
        }
        catch (OperationCanceledException)
        {
            // 页面已离开：静默收尾，不弹错误框
        }
        catch (Exception ex)
        {
            ShowError($"读取容器列表失败：{ex.Message}");
        }
        finally
        {
            if (userInitiated) LoadingBar.Visibility = Visibility.Collapsed;
            UpdateBatchBar();
        }
    }

    // ---------- 批量选择 ----------

    private HashSet<string> CaptureSelection()
        => ContainersListView.SelectedItems.OfType<ContainerModel>()
            .Select(c => c.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 把引擎返回的列表**就地**合并进 <see cref="_rows"/>。
    ///
    /// 为什么不用 `ItemsSource = 新列表`：那会让 ListView 走一次整体 Reset，
    /// 每行容器都被重绑一次——肉眼就是"内容区闪一下"。而一次启停会连着触发两次刷新
    /// （操作完成后主动刷一次 + 300ms 后事件防抖再刷一次），于是就"闪两下"。
    /// 就地合并后只有真正变化的行会重绑，第二次刷新（数据没变）连一次 Replace 都不发。
    /// </summary>
    private void SyncRows(IReadOnlyList<ContainerModel> incoming)
    {
        var incomingIds = incoming.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = _rows.Count - 1; i >= 0; i--)
        {
            if (!incomingIds.Contains(_rows[i].Id)) _rows.RemoveAt(i);
        }

        for (var i = 0; i < incoming.Count; i++)
        {
            var next = incoming[i];
            var existing = IndexOfRow(next.Id);

            if (existing < 0)
            {
                _rows.Insert(i, next);
                continue;
            }

            if (existing != i) _rows.Move(existing, i);

            // 内容逐字段比较：record 的 Equals 会把 PortMappings 当成引用比较，
            // 每次都判定"变了"，退化成每帧都重绑；这里用展示字段快照，稳且便宜。
            if (RowSignature(_rows[i]) != RowSignature(next)) _rows[i] = next;
        }
    }

    private int IndexOfRow(string id)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (string.Equals(_rows[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return -1;
    }

    /// <summary>行内实际会渲染的字段快照，用来判断"这行到底变没变"。</summary>
    private static string RowSignature(ContainerModel c)
        => string.Join('\u001f',
            c.Id, c.Name, c.Image, c.State, c.Status, c.RunningFor ?? string.Empty,
            c.PortsRaw, c.CpuDisplay, c.MemoryDisplay);

    private void RestoreSelection(IReadOnlySet<string> keys)
    {
        if (keys.Count == 0) return;

        var desired = _rows.Where(c => keys.Contains(c.Id)).ToList();

        // 行对象可能已被替换：先摘掉不再需要的旧对象，再补齐缺失的，避免同一个 Id 被选两次
        foreach (var selected in ContainersListView.SelectedItems.OfType<ContainerModel>().ToList())
        {
            if (!desired.Any(d => ReferenceEquals(d, selected)))
            {
                ContainersListView.SelectedItems.Remove(selected);
            }
        }

        foreach (var item in desired)
        {
            if (!ContainersListView.SelectedItems.OfType<ContainerModel>().Any(s => ReferenceEquals(s, item)))
            {
                ContainersListView.SelectedItems.Add(item);
            }
        }
    }

    private void ContainersListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 程序化改动选择（取消选择、刷新后回填）不会让已实现的行重新触发 Loaded，这里统一补一次
        RowSelectionHelper.Sync(ContainersListView, e);
        UpdateBatchBar();
    }

    private void RowCheckBox_Loaded(object sender, RoutedEventArgs e)
    {
        // 行被实例化或回收复用时，按真实选择态摆正勾选框
        if (sender is CheckBox box && box.DataContext is not null)
        {
            box.IsChecked = ContainersListView.SelectedItems.Contains(box.DataContext);
        }
    }

    private void RowCheckBox_Click(object sender, RoutedEventArgs e)
    {
        // 只改 SelectedItems；勾选态交给 SelectionChanged → SyncAll 统一摆正，避免两处各写一份
        RowSelectionHelper.Toggle(ContainersListView, (sender as FrameworkElement)?.DataContext);
    }

    private void UpdateBatchBar()
    {
        var count = ContainersListView.SelectedItems.Count;

        BatchSelectionText.Text = $"已选 {count} 项";

        // 走动画而不是直接切 Visibility：浮层带动画出现，列表不会被顶动
        if (count > 0) BatchBarAnimator.Show(BatchBar);
        else BatchBarAnimator.Hide(BatchBar);
    }

    private IReadOnlyList<ContainerModel> SelectedContainers()
        => ContainersListView.SelectedItems.OfType<ContainerModel>().ToList();

    private async void BatchStart_Click(object sender, RoutedEventArgs e)
        => await RunBatchAsync("启动", SelectedContainers(), c => _client.StartContainerAsync(c.Id));

    private async void BatchStop_Click(object sender, RoutedEventArgs e)
        => await RunBatchAsync("停止", SelectedContainers(), c => _client.StopContainerAsync(c.Id));

    private async void BatchRestart_Click(object sender, RoutedEventArgs e)
        => await RunBatchAsync("重启", SelectedContainers(), c => _client.RestartContainerAsync(c.Id));

    private void BatchClearSelection_Click(object sender, RoutedEventArgs e)
    {
        ContainersListView.SelectedItems.Clear();
        UpdateBatchBar();
    }

    private async void BatchDelete_Click(object sender, RoutedEventArgs e)
    {
        var items = SelectedContainers();
        if (items.Count == 0) return;

        // 破坏性批量操作：列清楚要删谁，且默认焦点在取消
        var names = string.Join("、", items.Select(c => c.Name));
        var confirmed = await DialogHelper.ConfirmDestructiveAsync(
            XamlRoot,
            $"删除 {items.Count} 个容器？",
            $"将删除：{names}\n\n各自容器内的改动一并消失，挂载的命名卷不受影响。\n此操作不可撤销。",
            $"删除 {items.Count} 个");

        if (!confirmed) return;

        await RunBatchAsync("删除", items, c => _client.DeleteContainerAsync(c.Name, force: true));
    }

    /// <summary>
    /// 逐个执行并统计失败项。
    /// 不用 Task.WhenAll：批量启停本来就该串行排队，并发去推同一个引擎反而容易触发内部竞争。
    /// </summary>
    private async Task RunBatchAsync(
        string verb,
        IReadOnlyList<ContainerModel> items,
        Func<ContainerModel, Task<bool>> action)
    {
        if (items.Count == 0) return;

        _isBusy = true;
        LoadingBar.Visibility = Visibility.Visible;
        BatchResultInfoBar.IsOpen = false;

        var failed = new List<string>();

        try
        {
            foreach (var item in items)
            {
                try
                {
                    if (!await action(item)) failed.Add(item.Name);
                }
                catch (Exception ex)
                {
                    failed.Add($"{item.Name}（{ex.Message}）");
                }
            }
        }
        finally
        {
            _isBusy = false;
            LoadingBar.Visibility = Visibility.Collapsed;
        }

        ContainersListView.SelectedItems.Clear();

        if (failed.Count == 0)
        {
            ErrorInfoBar.IsOpen = false;
            BatchResultInfoBar.Severity = InfoBarSeverity.Success;
            BatchResultInfoBar.Title = "批量操作完成";
            BatchResultInfoBar.Message = $"已{verb} {items.Count} 个容器。";
            BatchResultInfoBar.IsOpen = true;
        }
        else
        {
            // 部分失败必须点名，不能只说"操作失败"让用户去猜哪个没成
            ErrorInfoBar.Title = $"批量{verb}部分失败";
            ErrorInfoBar.Message = $"{items.Count - failed.Count}/{items.Count} 成功；失败：{string.Join("、", failed)}";
            ErrorInfoBar.IsOpen = true;
        }

        await RefreshAsync(userInitiated: false);
    }

    // ---------- 单项操作 ----------

    private async Task RunActionAsync(Func<Task<bool>> action, string failureMessage)
    {
        _isBusy = true;
        LoadingBar.Visibility = Visibility.Visible;

        try
        {
            var ok = await action();
            if (!ok) ShowError($"{failureMessage}（引擎返回失败，可用容器详情里的 inspect 进一步排查）");
            else ErrorInfoBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            ShowError($"{failureMessage}：{ex.Message}");
        }
        finally
        {
            _isBusy = false;
            LoadingBar.Visibility = Visibility.Collapsed;
            await RefreshAsync(userInitiated: false);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await RefreshAsync(userInitiated: true);

    private void CreateContainer_Click(object sender, RoutedEventArgs e)
        => Frame.Navigate(typeof(CreateContainerPage));

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetTag(sender, out var id)) return;
        await RunActionAsync(() => _client.StartContainerAsync(id), $"启动容器 {id} 失败");
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetTag(sender, out var id)) return;
        await RunActionAsync(() => _client.StopContainerAsync(id), $"停止容器 {id} 失败");
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetTag(sender, out var id)) return;
        await RunActionAsync(() => _client.RestartContainerAsync(id), $"重启容器 {id} 失败");
    }

    private async void Kill_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetTag(sender, out var id)) return;

        var confirmed = await DialogHelper.ConfirmDestructiveAsync(
            XamlRoot,
            "强制终止容器？",
            $"将向容器 {id} 发送强杀信号，未落盘的数据会丢失（容器本身仍保留，可再启动）。",
            "强制终止");

        if (!confirmed) return;
        await RunActionAsync(() => _client.KillContainerAsync(id), $"终止容器 {id} 失败");
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetTag(sender, out var name)) return;

        var confirmed = await DialogHelper.ConfirmDestructiveAsync(
            XamlRoot,
            "删除容器？",
            $"将删除容器 {name} 及其可写层（容器内的改动一并消失，挂载的命名卷不受影响）。\n此操作不可撤销。",
            "删除容器");

        if (!confirmed) return;
        await RunActionAsync(() => _client.DeleteContainerAsync(name, force: true), $"删除容器 {name} 失败");
    }

    private void Terminal_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetTag(sender, out var id)) return;

        if (!TerminalLauncher.OpenContainerShell(id))
        {
            _ = DialogHelper.ShowErrorAsync(XamlRoot, "无法打开终端", "未找到可用的终端程序（Windows Terminal / 控制台）。");
        }
    }

    private void CopyId_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetTag(sender, out var id)) return;
        TerminalLauncher.CopyToClipboard(id);
    }

    private void OpenPort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string url } || string.IsNullOrWhiteSpace(url)) return;

        if (!TerminalLauncher.OpenUrl(url))
        {
            _ = DialogHelper.ShowErrorAsync(XamlRoot, "无法打开浏览器", $"请手动访问：{url}");
        }
    }

    private async void Details_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;

        var container = _rows.FirstOrDefault(c =>
            string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.Name, id, StringComparison.OrdinalIgnoreCase));

        if (container is null) return;

        var dialog = new ContainerDetailDialog(container, _client) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();

        if (dialog.OpenTerminalRequested)
        {
            TerminalLauncher.OpenContainerShell(container.Id);
        }

        if (dialog.FollowLogsRequested)
        {
            TerminalLauncher.FollowContainerLogs(container.Id);
        }
    }

    private static bool TryGetTag(object sender, out string tag)
    {
        tag = sender is FrameworkElement { Tag: string value } ? value : string.Empty;
        return !string.IsNullOrWhiteSpace(tag);
    }

    private void ShowError(string message)
    {
        ErrorInfoBar.Title = "操作失败";
        ErrorInfoBar.Message = message;
        ErrorInfoBar.IsOpen = true;
    }
}
