using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WSLC.Panel.Core.Abstractions;
using WSLC.Panel.Core.Models;
using WSLC_Panel_App.Helpers;

namespace WSLC_Panel_App.Pages;

public sealed partial class ImagesPage : Page, IRefreshable
{
    private readonly IWslcClient _client;
    private readonly CancellationTokenSource _pageCts = new();

    private CancellationTokenSource? _pullCts;
    private readonly HashSet<string> _completedLayers = new(StringComparer.OrdinalIgnoreCase);
    private bool _isPulling;

    public ImagesPage()
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
        // 离开页面：取消在途刷新与拉取。拉取可持续数十秒，不取消的话续延会一直
        // 操作旧页面实例（PullPanel 已随新实例重建），旧实例也无法被回收
        _pageCts.Cancel();
        _pullCts?.Cancel();
    }

    public async Task RefreshAsync(bool userInitiated)
    {
        if (_isPulling)
        {
            // 忙时不是静默吞掉：说清楚点了没反应的原因（本页无 InfoBar，走对话框）
            if (userInitiated)
            {
                _ = DialogHelper.ShowErrorAsync(XamlRoot, "操作进行中", "正在拉取镜像，完成后会自动刷新列表。");
            }
            return;
        }

        if (userInitiated) LoadingBar.Visibility = Visibility.Visible;

        // 换 ItemsSource 会清空选中项，先记下来，后台刷新不能打断用户的勾选
        var selection = CaptureSelection();

        try
        {
            var images = await _client.GetImagesAsync(_pageCts.Token);
            ImagesListView.ItemsSource = images;

            foreach (var image in images)
            {
                if (selection.Contains(image.Id))
                {
                    ImagesListView.SelectedItems.Add(image);
                }
            }

            var isEmpty = images.Count == 0;
            EmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            ImagesListView.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            // 页面已离开：静默收尾
        }
        catch (Exception ex)
        {
            _ = DialogHelper.ShowErrorAsync(XamlRoot, "读取镜像列表失败", ex.Message);
        }
        finally
        {
            if (userInitiated) LoadingBar.Visibility = Visibility.Collapsed;
            UpdateBatchBar();
        }
    }

    // ---------- 批量选择 ----------

    private HashSet<string> CaptureSelection()
        => ImagesListView.SelectedItems.OfType<ImageModel>()
            .Select(i => i.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void ImagesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 程序化改动选择（取消选择、刷新后回填）不会让已实现的行重新触发 Loaded，这里统一补一次
        RowSelectionHelper.Sync(ImagesListView, e);
        UpdateBatchBar();
    }

    private void RowCheckBox_Loaded(object sender, RoutedEventArgs e)
    {
        // 行被实例化或回收复用时，按真实选择态摆正勾选框
        if (sender is CheckBox box && box.DataContext is not null)
        {
            box.IsChecked = ImagesListView.SelectedItems.Contains(box.DataContext);
        }
    }

    private void RowCheckBox_Click(object sender, RoutedEventArgs e)
        => RowSelectionHelper.Toggle(ImagesListView, (sender as FrameworkElement)?.DataContext);

    private void UpdateBatchBar()
    {
        var count = ImagesListView.SelectedItems.Count;

        BatchSelectionText.Text = $"已选 {count} 项";

        if (count > 0) BatchBarAnimator.Show(BatchBar);
        else BatchBarAnimator.Hide(BatchBar);
    }

    private void BatchClearSelection_Click(object sender, RoutedEventArgs e)
    {
        ImagesListView.SelectedItems.Clear();
        UpdateBatchBar();
    }

    private async void BatchDelete_Click(object sender, RoutedEventArgs e)
    {
        var items = ImagesListView.SelectedItems.OfType<ImageModel>().ToList();
        if (items.Count == 0) return;

        var names = string.Join("、", items.Select(i => i.FullName));
        var confirmed = await DialogHelper.ConfirmDestructiveAsync(
            XamlRoot,
            $"删除 {items.Count} 个镜像？",
            $"将删除：{names}\n\n若镜像的层仍被容器引用，该项会失败（可先删除相关容器）。删除后需重新拉取。\n此操作不可撤销。",
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
                    if (!await _client.DeleteImageAsync(item.Id, force: true)) failed.Add(item.FullName);
                }
                catch (Exception ex)
                {
                    failed.Add($"{item.FullName}（{ex.Message}）");
                }
            }
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
        }

        ImagesListView.SelectedItems.Clear();
        UpdateBatchBar();

        if (failed.Count > 0)
        {
            _ = DialogHelper.ShowErrorAsync(
                XamlRoot,
                "批量删除部分失败",
                $"{items.Count - failed.Count}/{items.Count} 成功。\n失败：{string.Join("、", failed)}\n\n通常是被容器引用导致，可先删除相关容器再重试。");
        }

        await RefreshAsync(userInitiated: false);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await RefreshAsync(userInitiated: true);

    private async void PullButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isPulling) return;

        var reference = await DialogHelper.PromptTextAsync(
            XamlRoot,
            "拉取镜像",
            "例如 alpine:latest 或 docker.1panel.live/library/nginx:latest",
            "开始拉取",
            "直连 Docker Hub 在国内网络下常超时，可改用加速源前缀（需自行测速）。");

        if (string.IsNullOrWhiteSpace(reference)) return;

        await PullAsync(reference);
    }

    private async Task PullAsync(string reference)
    {
        _isPulling = true;
        _completedLayers.Clear();
        _pullCts = new CancellationTokenSource();

        PullPanel.Visibility = Visibility.Visible;
        CancelPullButton.IsEnabled = true;
        PullTitleText.Text = $"正在拉取 {reference}";
        PullStatusText.Text = "正在连接镜像源…";
        PullBar.IsIndeterminate = true;
        PullBar.Value = 0;

        try
        {
            var result = await _client.PullImageAsync(
                reference,
                progress => DispatcherQueue.TryEnqueue(() => ApplyProgress(progress)),
                _pullCts.Token);

            if (result.Success)
            {
                PullBar.IsIndeterminate = false;
                PullBar.Value = 100;
                PullStatusText.Text = $"{result.Message}（耗时 {result.Duration.TotalSeconds:F1}s）";
                await Task.Delay(900);
            }
            else
            {
                PullBar.IsIndeterminate = false;
                PullBar.Value = 0;
                PullStatusText.Text = result.Message;
                _ = DialogHelper.ShowErrorAsync(XamlRoot, "拉取失败", result.Message);
            }
        }
        catch (OperationCanceledException)
        {
            PullStatusText.Text = "已取消拉取。";
        }
        catch (Exception ex)
        {
            PullStatusText.Text = ex.Message;
            _ = DialogHelper.ShowErrorAsync(XamlRoot, "拉取失败", ex.Message);
        }
        finally
        {
            _isPulling = false;
            _pullCts?.Dispose();
            _pullCts = null;
            CancelPullButton.IsEnabled = false;

            await RefreshAsync(userInitiated: false);
            await Task.Delay(1200);
            PullPanel.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 应用到进度条。实测 wslc 不给字节数，只能按状态文字推进：
    /// 拿得到字节/百分比就用确定进度，拿不到就走不确定进度 + 实时状态文本（诚实优先）。
    /// </summary>
    private void ApplyProgress(PullProgress progress)
    {
        // 拉取期间切页：旧页面实例的续延直接丢弃，不再碰控件
        if (XamlRoot is null) return;

        if (progress.Percent.HasValue)
        {
            PullBar.IsIndeterminate = false;
            PullBar.Value = progress.Percent.Value;
            PullStatusText.Text = $"{progress.Status} {progress.Percent.Value:F0}%";
        }
        else
        {
            PullBar.IsIndeterminate = true;
        }

        if (string.Equals(progress.Status, "Pull complete", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(progress.LayerId))
        {
            _completedLayers.Add(progress.LayerId);
        }

        var parts = new List<string> { progress.Status };
        if (!string.IsNullOrEmpty(progress.LayerId)) parts.Add($"层 {progress.LayerId}");
        if (_completedLayers.Count > 0) parts.Add($"已完成 {_completedLayers.Count} 个层");

        PullTitleText.Text = progress.ImageReference;
        if (!progress.Percent.HasValue) PullStatusText.Text = string.Join(" · ", parts);
    }

    private void CancelPull_Click(object sender, RoutedEventArgs e)
    {
        _pullCts?.Cancel();
        CancelPullButton.IsEnabled = false;
        PullStatusText.Text = "正在取消…";
    }

    private void CreateFromImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string image }) return;
        Frame.Navigate(typeof(CreateContainerPage), image);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;

        var confirmed = await DialogHelper.ConfirmDestructiveAsync(
            XamlRoot,
            "删除镜像？",
            $"将删除镜像 {id}。若已有容器使用该镜像的层，删除可能失败；删除后需重新拉取。\n此操作不可撤销。",
            "删除镜像");

        if (!confirmed) return;

        LoadingBar.Visibility = Visibility.Visible;
        try
        {
            var ok = await _client.DeleteImageAsync(id, force: true);
            if (!ok)
            {
                _ = DialogHelper.ShowErrorAsync(XamlRoot, "删除镜像失败", "引擎拒绝删除该镜像，可能仍被容器引用（可先删除相关容器）。");
            }
        }
        catch (Exception ex)
        {
            _ = DialogHelper.ShowErrorAsync(XamlRoot, "删除镜像失败", ex.Message);
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            await RefreshAsync(userInitiated: false);
        }
    }
}
