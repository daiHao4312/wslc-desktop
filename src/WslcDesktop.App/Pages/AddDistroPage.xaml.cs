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
/// 添加发行版（M17）。本地安装包导入 + 在线清单安装两条路径。
///
/// 设计要点：
/// <list type="bullet">
/// <item>装前检查与命令预览都走 <see cref="DistroInstallPlanner"/>（纯函数），
/// 与真正执行共用同一份参数拼装——预览里看到的参数就是会执行的参数。</item>
/// <item>重名检查需要现有发行版列表，进页面时取一次并缓存，避免每敲一个字就问一次引擎。</item>
/// <item>安装可能持续几分钟，所以输出是<b>流式</b>显示在本页底部，不弹窗、可取消。</item>
/// </list>
/// </summary>
public sealed partial class AddDistroPage : Page
{
    private const int MaxLogChars = 20000;

    private readonly IDistroService _distroService;
    private readonly CancellationTokenSource _pageCts = new();

    private IReadOnlyList<DistroModel> _existing = Array.Empty<DistroModel>();
    private IReadOnlyList<OnlineDistroModel> _online = Array.Empty<OnlineDistroModel>();

    private CancellationTokenSource? _installCts;
    private bool _isOnlineMode;
    private bool _isInstalling;
    private bool _isReady;

    public AddDistroPage()
    {
        InitializeComponent();
        _distroService = App.Services.GetRequiredService<IDistroService>();
        SourceSelector.SelectedItem = LocalFileItem;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _isReady = true;
        ApplyMode();
        await ReloadExistingAsync();
        UpdatePreview();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        // 离开页面即取消在途操作：安装进程不能因为"用户点了返回"就留在后台
        _pageCts.Cancel();
        _installCts?.Cancel();
    }

    // ---------------- 表单 ----------------

    private void SourceSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _isOnlineMode = ReferenceEquals(sender.SelectedItem, OnlineItem);
        ApplyMode();
        UpdatePreview();
    }

    private void ApplyMode()
    {
        LocalFileCard.Visibility = _isOnlineMode ? Visibility.Collapsed : Visibility.Visible;
        OnlineCard.Visibility = _isOnlineMode ? Visibility.Visible : Visibility.Collapsed;

        SourceHintText.Text = _isOnlineMode
            ? "从微软官方的发行版清单里选一个，联网下载后安装。安装位置可留空（用引擎默认位置）。"
            : "支持 tar / tar.gz / tar.xz 归档、.vhdx 虚拟磁盘、.wsl 打包文件；装法按后缀自动识别。";
    }

    private void Field_Changed(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void Field_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void Field_Checked(object sender, RoutedEventArgs e) => UpdatePreview();

    private DistroInstallRequest BuildRequest()
    {
        if (_isOnlineMode)
        {
            return new DistroInstallRequest
            {
                Source = DistroInstallSource.Online,
                OnlineName = (OnlineCombo.SelectedItem as OnlineDistroModel)?.Name ?? string.Empty,
                InstallDirectory = OnlineDirectoryBox.Text
            };
        }

        var path = FilePathBox.Text.Trim();

        return new DistroInstallRequest
        {
            Source = DistroInstallPlanner.DetectSource(path),
            FilePath = path,
            Name = NameBox.Text,
            InstallDirectory = DirectoryBox.Text,
            Version = Version1Radio.IsChecked == true ? 1 : 2
        };
    }

    /// <summary>装前检查 + 预览刷新。<c>_isReady</c> 之前一律不动控件（XAML 解析期 Checked 会先触发一次）。</summary>
    private void UpdatePreview()
    {
        if (!_isReady) return;

        var request = BuildRequest();
        var plan = DistroInstallPlanner.Plan(request, _existing);

        ModeText.Text = $"装法：{plan.Mode}";
        CommandPreviewText.Text = plan.IsValid ? plan.CommandLine : "（填完左侧表单后显示）";
        StepsText.Text = string.Join(
            Environment.NewLine,
            plan.Steps.Select((step, index) => $"{index + 1}. {step}"));

        UpdateLocalFileHints(request);

        if (plan.Problems.Count > 0)
        {
            MessageInfoBar.Severity = InfoBarSeverity.Warning;
            MessageInfoBar.Title = "还差这些信息";
            MessageInfoBar.Message = string.Join(Environment.NewLine, plan.Problems.Select(p => "· " + p));
            MessageInfoBar.IsOpen = true;
        }
        else
        {
            MessageInfoBar.IsOpen = false;
        }

        InstallButton.IsEnabled = plan.IsValid && !_isInstalling;
    }

    private void UpdateLocalFileHints(DistroInstallRequest request)
    {
        if (_isOnlineMode)
        {
            DetectedModeText.Text = string.Empty;
            return;
        }

        var path = request.FilePath.Trim();
        if (path.Length == 0)
        {
            DetectedModeText.Text = "按后缀自动选择装法：tar 归档走 wsl --import，.vhdx 走 wsl --import --vhd，.wsl 走 wsl --install --from-file。";
        }
        else if (!DistroInstallPlanner.IsRecognizedLocalFile(path))
        {
            DetectedModeText.Text = "不认识这个后缀，支持 .tar / .tar.gz / .tgz / .tar.xz / .vhdx / .wsl。";
        }
        else if (request.Source == DistroInstallSource.WslBundle)
        {
            DetectedModeText.Text = "识别为 .wsl 打包文件（需 WSL 2.4.4+）：名字与位置都可留空，留空则用包内清单的默认值；填写则覆盖。";
        }
        else
        {
            DetectedModeText.Text = request.Source == DistroInstallSource.LocalVhd
                ? "识别为 .vhdx 虚拟磁盘：以 --vhd 注册，只能是 WSL 2（不会转换格式）。"
                : "识别为 tar 归档：导入后会解包成 ext4.vhdx 放进安装目录。";
        }

        // 名字与位置对每种来源都有效（.wsl 包只是"可留空"），所以不禁用；
        // 版本选择只在 tar 导入时才有意义，其余来源隐藏而不是留个改了没用的控件
        VersionPanel.Visibility = DistroInstallPlanner.UsesVersionSelector(request.Source)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async Task ReloadExistingAsync()
    {
        try
        {
            _existing = await _distroService.GetDistrosAsync(_pageCts.Token);
        }
        catch (OperationCanceledException)
        {
            // 页面已离开
        }
        catch
        {
            // 列表拿不到就不做重名检查，不因此拦下安装（引擎自己会拒绝）
            _existing = Array.Empty<DistroModel>();
        }
    }

    // ---------------- 选取文件与目录 ----------------

    private async void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List
            };

            // FileTypeFilter 必须非空，否则 PickSingleFileAsync 直接抛参数异常
            foreach (var filter in new[] { ".tar", ".gz", ".tgz", ".xz", ".txz", ".vhdx", ".wsl", "*" })
            {
                picker.FileTypeFilter.Add(filter);
            }

            if (!TryBindPickerToWindow(picker)) return;

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;   // 用户取消

            FilePathBox.Text = file.Path;
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Warning, "打开文件选择器失败", $"请手动输入安装包路径：{ex.Message}");
        }
    }

    private async void BrowseDirectory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
                CommitButtonText = "选择此文件夹"
            };

            picker.FileTypeFilter.Add("*");
            if (!TryBindPickerToWindow(picker)) return;

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            DirectoryBox.Text = folder.Path;
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Warning, "打开文件夹选择器失败", $"请手动输入安装目录：{ex.Message}");
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
            ShowMessage(InfoBarSeverity.Warning, "无法打开选择器", "找不到主窗口，请手动输入路径。");
            return false;
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        return true;
    }

    // ---------------- 在线清单 ----------------

    private async void LoadOnline_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            OnlineHintText.Text = "正在读取发行版清单…";

            var list = await _distroService.GetOnlineDistrosAsync(_pageCts.Token);
            _online = list;
            OnlineCombo.ItemsSource = list;

            if (list.Count == 0)
            {
                OnlineHintText.Text = "清单为空。可能原因：未联网、wsl.exe 版本过旧（需要 ≥ 2.0）、或发行版安装被组策略禁用。";
                return;
            }

            OnlineHintText.Text = $"共 {list.Count} 个可选发行版。若下载缓慢，可在「设置」里配置镜像源，或改用本地安装包。";
            UpdatePreview();
        }
        catch (OperationCanceledException)
        {
            // 页面已离开
        }
        catch (Exception ex)
        {
            OnlineHintText.Text = $"读取清单失败：{ex.Message}";
            ShowMessage(InfoBarSeverity.Error, "读取发行版清单失败", ex.Message);
        }
    }

    // ---------------- 安装 ----------------

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_isInstalling) return;

        var request = BuildRequest();
        var plan = DistroInstallPlanner.Plan(request, _existing);
        if (!plan.IsValid)
        {
            UpdatePreview();
            return;
        }

        var confirmed = await DialogHelper.ConfirmAsync(
            XamlRoot,
            "开始安装？",
            $"将执行：{Environment.NewLine}{plan.CommandLine}{Environment.NewLine}{Environment.NewLine}" +
            $"安装可能要几分钟，过程中可随时取消。",
            "开始安装");

        if (!confirmed) return;

        _isInstalling = true;
        _installCts = new CancellationTokenSource();
        LogBox.Text = string.Empty;
        ProgressCard.Visibility = Visibility.Visible;
        InstallProgressBar.IsIndeterminate = true;
        ProgressTitleText.Text = $"正在安装（{plan.Mode}）…";
        CancelInstallButton.IsEnabled = true;
        MessageInfoBar.IsOpen = false;
        InstallButton.IsEnabled = false;

        AppendLog("$ " + plan.CommandLine);

        try
        {
            var result = await _distroService.InstallAsync(
                request,
                line => DispatcherQueue.TryEnqueue(() => AppendLog(line)),
                _installCts.Token);

            if (XamlRoot is null) return;

            if (result.Success)
            {
                var installedName = request.Name.Trim().Length > 0
                    ? request.Name.Trim()
                    : request.OnlineName.Trim();

                ProgressTitleText.Text = "安装完成";
                ShowMessage(
                    InfoBarSeverity.Success,
                    "安装完成",
                    $"{installedName} 已注册。返回列表即可看到它；首次启动时可能需要创建用户名与密码。");
            }
            else
            {
                ProgressTitleText.Text = "安装失败";
                ShowMessage(InfoBarSeverity.Error, "安装失败", result.Message);
            }
        }
        catch (Exception ex)
        {
            if (XamlRoot is not null)
            {
                ProgressTitleText.Text = "安装失败";
                ShowMessage(InfoBarSeverity.Error, "安装失败", ex.Message);
            }
        }
        finally
        {
            _isInstalling = false;
            _installCts?.Dispose();
            _installCts = null;

            InstallProgressBar.IsIndeterminate = false;
            CancelInstallButton.IsEnabled = false;

            // 安装会改变发行版列表（也可能因失败没变），两种情况都要重新取一次
            await ReloadExistingAsync();
            UpdatePreview();
        }
    }

    private void CancelInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_installCts is null) return;

        CancelInstallButton.IsEnabled = false;
        ProgressTitleText.Text = "正在取消…";
        _installCts.Cancel();
    }

    private void AppendLog(string line)
    {
        if (XamlRoot is null) return;
        if (string.IsNullOrEmpty(line)) return;

        var text = LogBox.Text + line + Environment.NewLine;

        // 日志只保留尾部：长安装会刷出几千行，无上限会拖慢文本框
        if (text.Length > MaxLogChars) text = text[^MaxLogChars..];

        LogBox.Text = text;
        LogBox.SelectionStart = LogBox.Text.Length;
        LogBox.SelectionLength = 0;
    }

    // ---------------- 其它 ----------------

    private void CopyCommand_Click(object sender, RoutedEventArgs e)
        => TerminalLauncher.CopyToClipboard(CommandPreviewText.Text);

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(DistrosPage));
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string message)
    {
        MessageInfoBar.Severity = severity;
        MessageInfoBar.Title = title;
        MessageInfoBar.Message = message;
        MessageInfoBar.IsOpen = true;
    }
}
