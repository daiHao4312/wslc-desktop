using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WSLC.Panel.Core.Abstractions;
using WSLC.Panel.Core.Models;
using WSLC_Panel_App.Helpers;

namespace WSLC_Panel_App.Pages;

public sealed partial class VolumesPage : Page, IRefreshable
{
    private readonly IWslcClient _client;
    private readonly CancellationTokenSource _pageCts = new();

    public VolumesPage()
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
            var volumes = await _client.GetVolumesAsync(_pageCts.Token);
            VolumesListView.ItemsSource = volumes;

            foreach (var volume in volumes)
            {
                if (selection.Contains(volume.Name))
                {
                    VolumesListView.SelectedItems.Add(volume);
                }
            }

            var isEmpty = volumes.Count == 0;
            EmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            VolumesListView.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;

            ErrorInfoBar.IsOpen = false;
        }
        catch (OperationCanceledException)
        {
            // 页面已离开：静默收尾
        }
        catch (Exception ex)
        {
            ErrorInfoBar.Title = "读取数据卷失败";
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
        => VolumesListView.SelectedItems.OfType<VolumeModel>()
            .Select(v => v.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void VolumesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 程序化改动选择（取消选择、刷新后回填）不会让已实现的行重新触发 Loaded，这里统一补一次
        RowSelectionHelper.Sync(VolumesListView, e);
        UpdateBatchBar();
    }

    private void RowCheckBox_Loaded(object sender, RoutedEventArgs e)
    {
        // 行被实例化或回收复用时，按真实选择态摆正勾选框
        if (sender is CheckBox box && box.DataContext is not null)
        {
            box.IsChecked = VolumesListView.SelectedItems.Contains(box.DataContext);
        }
    }

    private void RowCheckBox_Click(object sender, RoutedEventArgs e)
        => RowSelectionHelper.Toggle(VolumesListView, (sender as FrameworkElement)?.DataContext);

    private void UpdateBatchBar()
    {
        var count = VolumesListView.SelectedItems.Count;

        BatchSelectionText.Text = $"已选 {count} 项";

        if (count > 0) BatchBarAnimator.Show(BatchBar);
        else BatchBarAnimator.Hide(BatchBar);
    }

    private void BatchClearSelection_Click(object sender, RoutedEventArgs e)
    {
        VolumesListView.SelectedItems.Clear();
        UpdateBatchBar();
    }

    private async void BatchDelete_Click(object sender, RoutedEventArgs e)
    {
        var items = VolumesListView.SelectedItems.OfType<VolumeModel>().ToList();
        if (items.Count == 0) return;

        var names = string.Join("、", items.Select(v => v.Name));
        var confirmed = await DialogHelper.ConfirmDestructiveAsync(
            XamlRoot,
            $"删除 {items.Count} 个数据卷？",
            $"将删除：{names}\n\n卷中的数据一并消失；若仍有容器在使用，该项会失败。\n此操作不可撤销。",
            $"删除 {items.Count} 个");

        if (!confirmed) return;

        LoadingBar.Visibility = Visibility.Visible;
        var failed = new List<string>();

        try
        {
            foreach (var item in items)
            {
                try
                {
                    if (!await _client.RemoveVolumeAsync(item.Name, force: true)) failed.Add(item.Name);
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

        VolumesListView.SelectedItems.Clear();
        UpdateBatchBar();

        if (failed.Count > 0)
        {
            ErrorInfoBar.Title = "批量删除部分失败";
            ErrorInfoBar.Message = $"{items.Count - failed.Count}/{items.Count} 成功；失败：{string.Join("、", failed)}（通常是有容器仍在使用）";
            ErrorInfoBar.IsOpen = true;
        }

        await RefreshAsync(userInitiated: false);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await RefreshAsync(userInitiated: true);

    private async void CreateVolume_Click(object sender, RoutedEventArgs e)
    {
        var name = await DialogHelper.PromptTextAsync(
            XamlRoot,
            "新建命名卷",
            "卷名，例如 mysql-data",
            "创建",
            "命名卷可以在多个容器之间复用。");

        if (string.IsNullOrWhiteSpace(name)) return;

        LoadingBar.Visibility = Visibility.Visible;
        try
        {
            var ok = await _client.CreateVolumeAsync(name);
            if (!ok)
            {
                _ = DialogHelper.ShowErrorAsync(XamlRoot, "创建卷失败", "引擎拒绝创建该卷，可能同名卷已存在。");
            }
        }
        catch (Exception ex)
        {
            _ = DialogHelper.ShowErrorAsync(XamlRoot, "创建卷失败", ex.Message);
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            await RefreshAsync(userInitiated: false);
        }
    }

    private async void RemoveVolume_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name }) return;

        var confirmed = await DialogHelper.ConfirmDestructiveAsync(
            XamlRoot,
            "删除数据卷？",
            $"将删除卷 {name} 及其中的数据。若仍有容器在使用该卷，删除会失败。\n此操作不可撤销。",
            "删除卷");

        if (!confirmed) return;

        LoadingBar.Visibility = Visibility.Visible;
        try
        {
            var ok = await _client.RemoveVolumeAsync(name, force: true);
            if (!ok)
            {
                _ = DialogHelper.ShowErrorAsync(XamlRoot, "删除卷失败", "仍有容器在使用该卷（可先删除相关容器）。");
            }
        }
        catch (Exception ex)
        {
            _ = DialogHelper.ShowErrorAsync(XamlRoot, "删除卷失败", ex.Message);
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            await RefreshAsync(userInitiated: false);
        }
    }

    private async void PruneVolumes_Click(object sender, RoutedEventArgs e)
    {
        var volumes = VolumesListView.ItemsSource as IReadOnlyList<VolumeModel>;
        var anonymous = volumes?.Count(v => v.IsAnonymous) ?? 0;

        var confirmed = await DialogHelper.ConfirmDestructiveAsync(
            XamlRoot,
            "清理未使用的卷？",
            $"将删除所有未被任何容器使用的卷（当前列表中有 {anonymous} 个匿名卷）。匿名卷一旦清理，其数据无法恢复；正在运行的容器数据不受影响。\n此操作不可撤销。",
            "清理");

        if (!confirmed) return;

        LoadingBar.Visibility = Visibility.Visible;
        try
        {
            var ok = await _client.PruneVolumesAsync();
            if (!ok) _ = DialogHelper.ShowErrorAsync(XamlRoot, "清理失败", "引擎未完成清理，请稍后重试。");
        }
        catch (Exception ex)
        {
            _ = DialogHelper.ShowErrorAsync(XamlRoot, "清理失败", ex.Message);
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            await RefreshAsync(userInitiated: false);
        }
    }
}
