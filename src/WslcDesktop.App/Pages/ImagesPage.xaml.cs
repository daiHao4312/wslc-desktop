using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;
using WslcDesktop.Core.Services;
using WslcDesktop.Dialogs;
using WslcDesktop.Helpers;

namespace WslcDesktop.Pages;

public sealed partial class ImagesPage : Page, IRefreshable
{
    private readonly IWslcClient _client;
    private readonly IRegistryClient _registryClient;

    /// <summary>私有仓库的令牌由它按镜像源自动取（凭据存在设置页）。</summary>
    private readonly RegistryAuthService _registryAuth;

    /// <summary>镜像源列表与默认源：下拉候选与初值都从它拿，不在页面里拼。</summary>
    private readonly RegistrySourceService _registrySources;
    private readonly CancellationTokenSource _pageCts = new();

    private CancellationTokenSource? _pullCts;
    private readonly HashSet<string> _completedLayers = new(StringComparer.OrdinalIgnoreCase);
    private bool _isPulling;

    /// <summary>导出/导入进行中的取消源与忙标记（与拉取互不干扰：两者都在 Grid.Row=1 的面板里）。</summary>
    private CancellationTokenSource? _archiveCts;
    private bool _isArchiving;

    public ImagesPage()
    {
        InitializeComponent();
        _client = App.Services.GetRequiredService<IWslcClient>();
        _registryClient = App.Services.GetRequiredService<IRegistryClient>();
        // 通道不可用时它会明确报"通道不可用"（而不是拿匿名去撞 401），所以这里不必判断
        _registryAuth = App.Services.GetRequiredService<RegistryAuthService>();
        _registrySources = App.Services.GetRequiredService<RegistrySourceService>();
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
        _archiveCts?.Cancel();
    }

    public async Task RefreshAsync(bool userInitiated)
    {
        if (_isPulling || _isArchiving)
        {
            // 忙时不是静默吞掉：说清楚点了没反应的原因（本页无 InfoBar，走对话框）
            if (userInitiated)
            {
                var doing = _isPulling ? "正在拉取镜像" : "正在导出或导入镜像";
                _ = DialogHelper.ShowErrorAsync(XamlRoot, "操作进行中", $"{doing}，完成后会自动刷新列表。");
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

    // ---------------- 拉取 ----------------

    private async void PullButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isPulling) return;

        // 表单放在对话框里：三段输入直接铺在页面上，窄窗口下会挤成一团
        var dialog = new PullImageDialog(
            _registryClient,
            _registryAuth,
            _registrySources.EnabledHosts(),
            _registrySources.EffectiveDefaultHost)
        {
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (dialog.ResultReference is not { Length: > 0 } reference) return;

        await PullAsync(reference, dialog.ResultRegistryAuth);
    }

    // ---------------- 改标签 / 重命名 ----------------

    /// <summary>
    /// 把镜像改成另一个引用。
    ///
    /// <c>wslc tag</c> 只是**新增一个引用**（同一个镜像 ID 会同时挂两个名字），
    /// 所以这里补一步 <c>wslc rmi</c> 删掉旧名字 —— 两步分开报，
    /// 避免"看着成功了其实旧名字还留着"。
    /// </summary>
    private async void RenameImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string source } || _isPulling) return;

        var target = await DialogHelper.PromptTextAsync(
            XamlRoot,
            "重命名镜像",
            "my-alpine:3.20",
            "下一步",
            $"当前引用：{source}\n新引用可以不带 registry 前缀，例如 my-alpine:3.20。");

        if (string.IsNullOrWhiteSpace(target)) return;

        target = target.Trim();

        if (!RegistryInput.TryValidateTagReference(target, out var error))
        {
            _ = DialogHelper.ShowErrorAsync(XamlRoot, "引用不合法", error);
            return;
        }

        if (string.Equals(target, source, StringComparison.Ordinal))
        {
            _ = DialogHelper.ShowErrorAsync(XamlRoot, "没有变化", "新引用与当前引用相同。");
            return;
        }

        var confirmed = await DialogHelper.ConfirmDestructiveAsync(
            XamlRoot,
            "重命名镜像？",
            $"将执行：{Environment.NewLine}" +
            $"  1. wslc tag {source} {target}{Environment.NewLine}" +
            $"  2. wslc rmi {source}{Environment.NewLine}{Environment.NewLine}" +
            "删掉的只是旧**名字**，镜像本体不会消失。\n" +
            "若这个镜像正被容器占用，第 2 步会被引擎拒绝 —— 那时新名字已经生效，旧的会一起留着。",
            "重命名");

        if (!confirmed) return;

        LoadingBar.Visibility = Visibility.Visible;
        try
        {
            var tagged = await _client.TagImageAsync(source, target, _pageCts.Token);
            if (!tagged)
            {
                _ = DialogHelper.ShowErrorAsync(
                    XamlRoot,
                    "重命名失败",
                    $"wslc tag 被引擎拒绝。旧引用未受影响：{source}");
                return;
            }

            var removed = await _client.DeleteImageAsync(source, force: false, _pageCts.Token);
            if (!removed && XamlRoot is not null)
            {
                _ = DialogHelper.ShowErrorAsync(
                    XamlRoot,
                    "新名字已生效，旧引用没删掉",
                    $"wslc tag 成功，但 wslc rmi {source} 被拒绝（多半是镜像正被容器占用）。\n" +
                    $"新引用 {target} 已可用，这个镜像现在同时挂两个名字。");
            }
        }
        catch (OperationCanceledException)
        {
            // 页面已离开
        }
        catch (Exception ex)
        {
            _ = DialogHelper.ShowErrorAsync(XamlRoot, "重命名失败", ex.Message);
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            await RefreshAsync(userInitiated: false);
        }
    }


    // ---------------- 导出 / 导入（tar 归档）----------------

    private async void ExportImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string reference }) return;
        await ExportAsync(new[] { reference });
    }

    private async void BatchExport_Click(object sender, RoutedEventArgs e)
    {
        var references = ImagesListView.SelectedItems.OfType<ImageModel>()
            .Select(i => i.FullName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        if (references.Count == 0)
        {
            _ = DialogHelper.ShowErrorAsync(XamlRoot, "没有选中镜像", "先勾选要导出的镜像。");
            return;
        }

        await ExportAsync(references);
    }

    /// <summary>
    /// 导出为 tar：一个归档可以装多个镜像（<c>wslc save</c> 支持一次给多个引用）。
    /// 先选文件再动手 —— 文件选择器本身就是取消点，不该先排上导出任务再去问路径。
    /// </summary>
    private async Task ExportAsync(IReadOnlyList<string> references)
    {
        if (_isPulling || _isArchiving) return;

        var path = await PickExportArchiveAsync(references[0]);
        if (path is null) return;

        var what = references.Count == 1 ? references[0] : $"{references.Count} 个镜像";
        var confirmed = await DialogHelper.ConfirmAsync(
            XamlRoot,
            "导出为 tar 归档？",
            $"将执行：wslc save -o \"{path}\" {what}\n\n" +
            "归档里是完整的镜像层，体积接近镜像本身；导出期间可以取消 —— 取消会删掉写了一半的文件。",
            "开始导出");

        if (!confirmed) return;

        await RunArchiveAsync(
            isExport: true,
            $"正在导出 {what}",
            (progress, ct) => _client.ExportImagesAsync(references, path, progress, ct));
    }

    private async void ImportImage_Click(object sender, RoutedEventArgs e)
    {
        if (_isPulling || _isArchiving) return;

        var path = await PickArchiveToImportAsync();
        if (path is null) return;

        var confirmed = await DialogHelper.ConfirmAsync(
            XamlRoot,
            "从 tar 导入镜像？",
            $"将执行：wslc load -i \"{path}\"\n\n" +
            "归档里的引用按原名恢复；本地已有同名引用时会被归档里的内容覆盖（层已在本地则不会重复写入）。",
            "开始导入");

        if (!confirmed) return;

        await RunArchiveAsync(
            isExport: false,
            $"正在导入 {Path.GetFileName(path)}",
            (progress, ct) => _client.ImportImageArchiveAsync(path, progress, ct));
    }

    /// <summary>
    /// 导出/导入的统一执行壳：面板 + 进度 + 取消 + 结束语。
    ///
    /// 与拉取共用 Grid.Row=1 的位置（两者互斥）：用户看到的始终是"页头下面那一条"，
    /// 不需要学两个不同的进度位置。
    /// </summary>
    private async Task RunArchiveAsync(
        bool isExport,
        string title,
        Func<Action<ImageArchiveProgress>, CancellationToken, Task<ImageArchiveResult>> run)
    {
        _isArchiving = true;
        _archiveCts = new CancellationTokenSource();

        ArchivePanel.Visibility = Visibility.Visible;
        ArchiveTitleText.Text = title;
        ArchiveBar.IsIndeterminate = true;
        ArchiveBar.Value = 0;
        ArchiveStatusText.Text = isExport ? "正在写入文件…" : "正在读取归档…";
        CancelArchiveButton.IsEnabled = true;

        try
        {
            var result = await run(
                progress => DispatcherQueue.TryEnqueue(() => ApplyArchiveProgress(progress)),
                _archiveCts.Token);

            if (XamlRoot is null) return;

            ArchiveBar.IsIndeterminate = false;
            ArchiveBar.Value = result.Success ? 100 : 0;

            if (result.Cancelled)
            {
                ArchiveTitleText.Text = "已取消";
                ArchiveStatusText.Text = result.Message;
            }
            else if (result.Success)
            {
                ArchiveTitleText.Text = isExport ? "导出完成" : "导入完成";

                if (isExport)
                {
                    ArchiveStatusText.Text =
                        $"{result.Message} → {result.Path}（耗时 {result.Duration.TotalSeconds:F1}s）";
                }
                else
                {
                    // 导入最要紧的是"到底进来了哪些名字"，直接摆出来
                    ArchiveStatusText.Text = result.References.Count > 0
                        ? $"{result.Message}：{string.Join("、", result.References)}"
                        : $"{result.Message}（耗时 {result.Duration.TotalSeconds:F1}s）";
                }
            }
            else
            {
                ArchiveTitleText.Text = "操作失败";
                ArchiveStatusText.Text = result.Message;
                _ = DialogHelper.ShowErrorAsync(XamlRoot, isExport ? "导出失败" : "导入失败", result.Message);
            }
        }
        catch (OperationCanceledException)
        {
            // 页面已离开：面板随之消失，不用再更新文字
        }
        catch (Exception ex)
        {
            if (XamlRoot is not null)
            {
                ArchiveTitleText.Text = "操作失败";
                ArchiveStatusText.Text = ex.Message;
                _ = DialogHelper.ShowErrorAsync(XamlRoot, isExport ? "导出失败" : "导入失败", ex.Message);
            }
        }
        finally
        {
            // 先清忙标记再刷新：RefreshAsync 会把"忙"当成"点了没反应"挡掉
            _isArchiving = false;
            _archiveCts?.Dispose();
            _archiveCts = null;
            CancelArchiveButton.IsEnabled = false;

            await RefreshAsync(userInitiated: false);

            await Task.Delay(1500);
            if (XamlRoot is not null) ArchivePanel.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>进度：导出报已写字节数（文件大小是唯一连续量），导入报已加载的镜像名。</summary>
    private void ApplyArchiveProgress(ImageArchiveProgress progress)
    {
        if (XamlRoot is null) return;

        ArchiveStatusText.Text = progress.BytesWritten > 0
            ? $"{progress.Status} · 已写入 {progress.Display}"
            : progress.Status;
    }

    private void CancelArchive_Click(object sender, RoutedEventArgs e)
    {
        if (_archiveCts is null) return;

        CancelArchiveButton.IsEnabled = false;
        ArchiveTitleText.Text = "正在取消…";
        _archiveCts.Cancel();
    }

    private async Task<string?> PickExportArchiveAsync(string firstReference)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                SuggestedFileName = ImageArchiveParser.SuggestFileName(firstReference, DateTimeOffset.Now)
            };

            picker.FileTypeChoices.Add("tar 归档", new List<string> { ImageArchiveParser.ArchiveExtension });
            if (!TryBindPickerToWindow(picker)) return null;

            return (await picker.PickSaveFileAsync())?.Path;
        }
        catch (Exception ex)
        {
            _ = DialogHelper.ShowErrorAsync(XamlRoot, "打开保存对话框失败", ex.Message);
            return null;
        }
    }

    private async Task<string?> PickArchiveToImportAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List
            };

            // FileTypeFilter 必须非空，否则 PickSingleFileAsync 直接抛参数异常
            picker.FileTypeFilter.Add(ImageArchiveParser.ArchiveExtension);
            picker.FileTypeFilter.Add("*");
            if (!TryBindPickerToWindow(picker)) return null;

            return (await picker.PickSingleFileAsync())?.Path;
        }
        catch (Exception ex)
        {
            _ = DialogHelper.ShowErrorAsync(XamlRoot, "打开文件选择器失败", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 非打包（WindowsPackageType=None）应用**必须**把 picker 绑到窗口 HWND，
    /// 否则会以 COM 失败告终、连对话框都不弹（与发行版导出同一个坑）。
    /// </summary>
    private static bool TryBindPickerToWindow(object picker)
    {
        var window = App.MainWindow;
        if (window is null) return false;

        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        return true;
    }


    private async Task PullAsync(string reference, string? registryAuth = null)
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
                registryAuth,
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
                // 失败就表单放回来，用户改个 tag 就能重试，不用再点一次页头按钮
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
