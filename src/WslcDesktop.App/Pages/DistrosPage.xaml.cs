using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;
using WslcDesktop.Helpers;

namespace WslcDesktop.Pages;

/// <summary>
/// WSL 发行版管理（M10 + M17 + M18）。走 wsl.exe + 注册表 Lxss，与容器通道完全独立——
/// 本机没有发行版或 wsl.exe 被策略禁用时，这里只是空态，不影响面板其它功能。
///
/// M18 加的详情操作（压缩 / 稀疏 / 调整大小 / 移动 / 导出 / 编辑 wsl.conf / 默认用户）全部走
/// <c>wsl --manage</c>，参数由 <see cref="DistroManagePlanner"/> 统一拼装并做前置校验；
/// 长操作（移动、导出）就地显示进度与引擎输出，不弹窗。
/// </summary>
public sealed partial class DistrosPage : Page, IRefreshable
{
    private const int MaxLogChars = 20000;

    private readonly IDistroService _distroService;
    private readonly CancellationTokenSource _pageCts = new();

    private CancellationTokenSource? _operationCts;
    private bool _isOperating;

    public DistrosPage()
    {
        InitializeComponent();
        _distroService = App.Services.GetRequiredService<IDistroService>();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await RefreshAsync(userInitiated: false);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // 离开页面即取消在途刷新与在途长操作：续延不再操作已卸载的 UI
        _pageCts.Cancel();
        _operationCts?.Cancel();
    }

    // ---------------- 列表 ----------------

    public async Task RefreshAsync(bool userInitiated)
    {
        if (userInitiated) LoadingBar.Visibility = Visibility.Visible;

        try
        {
            var distros = await _distroService.GetDistrosAsync(_pageCts.Token);
            DistrosListView.ItemsSource = distros;

            var isEmpty = distros.Count == 0;
            EmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            DistrosListView.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;

            if (isEmpty)
            {
                // 空态给可执行原因，而不是留一片空白（§7.2 空状态）
                ShowInfo(
                    InfoBarSeverity.Warning,
                    "未读取到发行版",
                    "wsl.exe 未返回任何发行版。若确认已安装，请检查是否被组策略禁用（.wslconfig / Intune）。");
            }
            else if (!_isOperating)
            {
                ErrorInfoBar.IsOpen = false;
            }
        }
        catch (OperationCanceledException)
        {
            // 页面已离开：静默收尾
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, "读取发行版失败", ex.Message);
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await RefreshAsync(userInitiated: true);

    /// <summary>打开「添加发行版」页（离开页面后回来会重新刷新列表）。</summary>
    private void AddDistro_Click(object sender, RoutedEventArgs e)
        => Frame.Navigate(typeof(AddDistroPage));

    private async void Diagnose_Click(object sender, RoutedEventArgs e)
    {
        LoadingBar.Visibility = Visibility.Visible;
        try
        {
            var status = await _distroService.GetStatusAsync();
            await DialogHelper.ShowContentAsync(
                XamlRoot,
                "wsl --status 输出",
                string.IsNullOrWhiteSpace(status) ? "（未获取到输出，wsl.exe 可能不可用）" : status);
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
        }
    }

    // ---------------- 行内快捷操作 ----------------

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (ModelOf(sender) is not { } distro) return;
        await RunRowActionAsync(distro, d => _distroService.StartAsync(d.Name), "启动发行版失败");
    }

    private async void Terminate_Click(object sender, RoutedEventArgs e)
    {
        if (ModelOf(sender) is not { } distro) return;
        await RunRowActionAsync(distro, d => _distroService.TerminateAsync(d.Name), "终止发行版失败");
    }

    private async void SetDefault_Click(object sender, RoutedEventArgs e)
    {
        if (ModelOf(sender) is not { } distro) return;
        await RunRowActionAsync(distro, d => _distroService.SetDefaultAsync(d.Name), "设为默认发行版失败");
    }

    private void OpenTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (ModelOf(sender) is not { } distro) return;

        if (!TerminalLauncher.OpenDistroShell(distro.Name))
        {
            _ = DialogHelper.ShowErrorAsync(
                XamlRoot,
                "无法打开终端",
                $"未找到可用的终端程序，请在命令行执行：wsl -d {distro.Name}");
        }
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (ModelOf(sender) is not { } distro) return;

        if (string.IsNullOrWhiteSpace(distro.BasePath))
        {
            _ = DialogHelper.ShowErrorAsync(
                XamlRoot,
                "无法打开安装位置",
                "注册表里没有读到这个发行版的安装路径（可能被组策略限制读取 Lxss）。");
            return;
        }

        if (!TerminalLauncher.RevealInExplorer(distro.BasePath))
        {
            _ = DialogHelper.ShowErrorAsync(XamlRoot, "无法打开资源管理器", distro.BasePath);
        }
    }

    private async void Unregister_Click(object sender, RoutedEventArgs e)
    {
        if (ModelOf(sender) is not { } distro) return;

        var confirmed = await DialogHelper.ConfirmDestructiveAsync(
            XamlRoot,
            "注销（删除）发行版？",
            $"将注销发行版 {distro.Name}：其中所有文件、已安装软件与用户数据都会被删除" +
            $"（对应 VHDX 文件也会被移除，当前占用 {distro.VhdSizeDisplay}）。\n" +
            "如需保留数据，请先「导出为 tar 归档」。此操作不可撤销。",
            "注销并删除");

        if (!confirmed) return;

        await RunRowActionAsync(distro, d => _distroService.UnregisterAsync(d.Name), "注销发行版失败");
    }

    // ---------------- 磁盘与位置（M18）----------------

    private async void Compact_Click(object sender, RoutedEventArgs e)
    {
        if (ModelOf(sender) is not { } distro) return;

        var confirmed = await DialogHelper.ConfirmAsync(
            XamlRoot,
            "压缩磁盘？",
            DistroManagePlanner.DescribeOperation(DistroOperationKind.Compact, distro.Name),
            "开始压缩");

        if (!confirmed) return;

        await RunOperationAsync(
            $"正在压缩 {distro.Name} 的磁盘…",
            (_, ct) => _distroService.CompactAsync(distro.Name, ct));
    }

    private async void Resize_Click(object sender, RoutedEventArgs e)
    {
        if (ModelOf(sender) is not { } distro) return;

        var size = await DialogHelper.PromptTextAsync(
            XamlRoot,
            $"调整 {distro.Name} 的磁盘大小",
            "例如 50GB",
            "调整",
            "支持 B / KB / MB / GB / TB（如 50GB、1.5TB）。缩小到比当前已用空间还小会失败。" +
            $"当前占用 {distro.VhdSizeDisplay}。");

        if (size is null) return;

        await RunOperationAsync(
            $"正在调整 {distro.Name} 的磁盘大小…",
            (_, ct) => _distroService.ResizeAsync(distro.Name, size, ct));
    }

    private async void Move_Click(object sender, RoutedEventArgs e)
    {
        if (ModelOf(sender) is not { } distro) return;

        var target = await PickFolderAsync("选择移动目标位置（要移入的目录）");
        if (target is null) return;

        var confirmed = await DialogHelper.ConfirmAsync(
            XamlRoot,
            "移动安装位置？",
            $"{DistroManagePlanner.DescribeOperation(DistroOperationKind.Move, distro.Name)}\n\n" +
            $"目标位置：{target}",
            "开始移动");

        if (!confirmed) return;

        await RunOperationAsync(
            $"正在移动 {distro.Name}…",
            (_, ct) => _distroService.MoveAsync(distro.Name, target, ct));
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (ModelOf(sender) is not { } distro) return;

        var path = await PickExportFileAsync(distro.Name);
        if (path is null) return;

        var confirmed = await DialogHelper.ConfirmAsync(
            XamlRoot,
            "导出为 tar 归档？",
            DistroManagePlanner.DescribeOperation(DistroOperationKind.Export, distro.Name) +
            $"\n\n目标文件：{path}",
            "开始导出");

        if (!confirmed) return;

        await RunOperationAsync(
            $"正在导出 {distro.Name}…",
            (progress, ct) => _distroService.ExportAsync(
                distro.Name,
                path,
                progress,
                line => DispatcherQueue.TryEnqueue(() => AppendOperationLog(line)),
                ct));
    }

    private async void EnableSparse_Click(object sender, RoutedEventArgs e)
    {
        if (ModelOf(sender) is not { } distro) return;

        // WSL 3.0.1 起默认认为这个操作有潜在的数据损坏风险 —— 按破坏性操作的纪律来
        var confirmed = await DialogHelper.ConfirmDestructiveAsync(
            XamlRoot,
            "开启稀疏盘？",
            DistroManagePlanner.DescribeOperation(DistroOperationKind.SetSparse, distro.Name),
            "我已了解风险，开启");

        if (!confirmed) return;

        await RunOperationAsync(
            $"正在开启 {distro.Name} 的稀疏盘…",
            (_, ct) => _distroService.SetSparseAsync(distro.Name, sparse: true, ct));
    }

    private async void DisableSparse_Click(object sender, RoutedEventArgs e)
    {
        if (ModelOf(sender) is not { } distro) return;

        await RunOperationAsync(
            $"正在关闭 {distro.Name} 的稀疏盘…",
            (_, ct) => _distroService.SetSparseAsync(distro.Name, sparse: false, ct));
    }

    // ---------------- 配置（M18）----------------

    private async void EditWslConf_Click(object sender, RoutedEventArgs e)
    {
        if (ModelOf(sender) is not { } distro) return;

        LoadingBar.Visibility = Visibility.Visible;
        WslConfSnapshot snapshot;
        try
        {
            snapshot = await _distroService.ReadWslConfAsync(distro.Name, _pageCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
        }

        if (!snapshot.Success)
        {
            _ = DialogHelper.ShowErrorAsync(
                XamlRoot,
                $"读取 /etc/wsl.conf 失败",
                $"{snapshot.Error}\n\n（发行版必须先能被 wsl.exe 启动才能读它的配置。）");
            return;
        }

        var edited = await DialogHelper.PromptMultilineAsync(
            XamlRoot,
            $"{distro.Name} 的 /etc/wsl.conf",
            snapshot.Content,
            $"路径 {DistroManagePlanner.WslConfPath}；保存前会先备份到 {DistroManagePlanner.WslConfBackupPath}。" +
            "这个文件写错会让发行版下次启动失败，改完可用 wsl --shutdown 让它生效。",
            "保存");

        if (edited is null) return;

        if (string.Equals(edited, snapshot.Content, StringComparison.Ordinal))
        {
            ShowInfo(InfoBarSeverity.Informational, "没有改动", "内容与磁盘上的一致，未写入。");
            return;
        }

        // 写错会让发行版起不来 —— 按破坏性操作的纪律来
        var confirmed = await DialogHelper.ConfirmDestructiveAsync(
            XamlRoot,
            "写入 /etc/wsl.conf？",
            DistroManagePlanner.DescribeOperation(DistroOperationKind.WriteWslConf, distro.Name),
            "备份并写入");

        if (!confirmed) return;

        await RunOperationAsync(
            $"正在写入 {distro.Name} 的 /etc/wsl.conf…",
            (_, ct) => _distroService.WriteWslConfAsync(distro.Name, edited, ct));
    }

    private async void SetDefaultUser_Click(object sender, RoutedEventArgs e)
    {
        if (ModelOf(sender) is not { } distro) return;

        var user = await DialogHelper.PromptTextAsync(
            XamlRoot,
            $"设置 {distro.Name} 的默认用户",
            "例如 alice",
            "设置",
            "用户名必须是发行版里**已经存在**的账号，否则发行版下次启动会失败。");

        if (user is null) return;

        await RunOperationAsync(
            $"正在设置 {distro.Name} 的默认用户…",
            (_, ct) => _distroService.SetDefaultUserAsync(distro.Name, user, ct));
    }

    // ---------------- 长操作面板 ----------------

    private async Task RunOperationAsync(
        string title,
        Func<Action<DistroExportProgress>, CancellationToken, Task<DistroOperationResult>> run)
    {
        if (_isOperating) return;

        _isOperating = true;
        _operationCts = new CancellationTokenSource();

        OperationCard.Visibility = Visibility.Visible;
        OperationTitleText.Text = title;
        OperationProgressBar.IsIndeterminate = true;
        OperationStatusText.Text = string.Empty;
        OperationLogBox.Text = string.Empty;
        CancelOperationButton.IsEnabled = true;
        ErrorInfoBar.IsOpen = false;

        try
        {
            var result = await run(OnOperationProgress, _operationCts.Token);

            if (XamlRoot is null) return;

            AppendOperationLog($"$ {result.CommandLine}");
            AppendOperationLog($"{result.Message}（用时 {result.Duration.TotalSeconds:F1} 秒）");

            if (result.Success)
            {
                OperationTitleText.Text = "操作完成";
                OperationStatusText.Text = result.Message;
                ShowInfo(InfoBarSeverity.Success, "操作完成", result.Message);
            }
            else
            {
                OperationTitleText.Text = "操作失败";
                OperationStatusText.Text = result.Message;
                ShowInfo(InfoBarSeverity.Error, "操作失败", result.Message);
            }
        }
        catch (Exception ex)
        {
            if (XamlRoot is not null)
            {
                OperationTitleText.Text = "操作失败";
                ShowInfo(InfoBarSeverity.Error, "操作失败", ex.Message);
            }
        }
        finally
        {
            _isOperating = false;
            _operationCts?.Dispose();
            _operationCts = null;

            OperationProgressBar.IsIndeterminate = false;
            CancelOperationButton.IsEnabled = false;

            // 位置 / 磁盘占用 / 稀疏状态都可能变了，重新取一次
            await RefreshAsync(userInitiated: false);
        }
    }

    /// <summary>导出进度：只有字节数是连续的，所以用文字报出来，进度条保持不确定态。</summary>
    private void OnOperationProgress(DistroExportProgress progress)
        => DispatcherQueue.TryEnqueue(() =>
        {
            if (XamlRoot is null) return;
            OperationStatusText.Text = $"已写入 {progress.Display}";
        });

    private void CancelOperation_Click(object sender, RoutedEventArgs e)
    {
        if (_operationCts is null) return;

        CancelOperationButton.IsEnabled = false;
        OperationTitleText.Text = "正在取消…";
        _operationCts.Cancel();
    }

    private void AppendOperationLog(string line)
    {
        if (XamlRoot is null || string.IsNullOrEmpty(line)) return;

        var text = OperationLogBox.Text + line + Environment.NewLine;

        // 只保留尾部：长时间导出的输出没有上限，全留着会拖慢文本框
        if (text.Length > MaxLogChars) text = text[^MaxLogChars..];

        OperationLogBox.Text = text;
        OperationLogBox.SelectionStart = OperationLogBox.Text.Length;
        OperationLogBox.SelectionLength = 0;
    }

    // ---------------- 选择器 ----------------

    private async Task<string?> PickFolderAsync(string commitText)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
                CommitButtonText = commitText
            };

            picker.FileTypeFilter.Add("*");
            if (!TryBindPickerToWindow(picker)) return null;

            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Warning, "打开文件夹选择器失败", $"请改用其它方式：{ex.Message}");
            return null;
        }
    }

    private async Task<string?> PickExportFileAsync(string distroName)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"{distroName}.tar"
            };

            picker.FileTypeChoices.Add("tar 归档", new List<string> { ".tar" });
            if (!TryBindPickerToWindow(picker)) return null;

            var file = await picker.PickSaveFileAsync();
            return file?.Path;
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Warning, "打开保存对话框失败", $"请改用其它方式：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 非打包（WindowsPackageType=None）应用**必须**把 picker 绑到窗口 HWND，
    /// 否则会以 COM 失败告终、连对话框都不弹。
    /// </summary>
    private bool TryBindPickerToWindow(object picker)
    {
        var window = App.MainWindow;
        if (window is null)
        {
            ShowInfo(InfoBarSeverity.Warning, "无法打开选择器", "找不到主窗口。");
            return false;
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        return true;
    }

    // ---------------- 公共 ----------------

    private static DistroModel? ModelOf(object sender)
        => sender is FrameworkElement { Tag: DistroModel model } ? model : null;

    /// <summary>行内快捷操作（启停 / 设默认 / 注销）：跑完就刷新，失败给可读原因。</summary>
    private async Task RunRowActionAsync(DistroModel distro, Func<DistroModel, Task<bool>> action, string failureTitle)
    {
        LoadingBar.Visibility = Visibility.Visible;
        try
        {
            var ok = await action(distro);
            if (!ok && XamlRoot is not null)
            {
                _ = DialogHelper.ShowErrorAsync(
                    XamlRoot,
                    failureTitle,
                    $"对 {distro.Name} 的操作被引擎拒绝（可点右上角查看 wsl --status）。");
            }
        }
        catch (Exception ex)
        {
            _ = DialogHelper.ShowErrorAsync(XamlRoot, failureTitle, ex.Message);
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            await RefreshAsync(userInitiated: false);
        }
    }

    private void ShowInfo(InfoBarSeverity severity, string title, string message)
    {
        ErrorInfoBar.Severity = severity;
        ErrorInfoBar.Title = title;
        ErrorInfoBar.Message = message;
        ErrorInfoBar.IsOpen = true;
    }
}
