using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;
using WslcDesktop.Core.Services;
using WslcDesktop.Helpers;

namespace WslcDesktop.Pages;

/// <summary>
/// 关于页：项目信息 + 版本更新检查 + 环境自检（升级引导的可操作落点，对应 P0-1 与风险表 #1/#7）。
/// </summary>
public sealed partial class AboutPage : Page, IRefreshable
{
    private const string UpgradeCommand = "wsl --update";

    private readonly IWslcClient _client;
    private readonly IPrefsService _prefsService;
    private readonly IUpdateChecker _updateChecker;
    private readonly IUpdateDownloader _updateDownloader;

    /// <summary>最近一次检查结果（"下载并更新"要用它的资产信息）。</summary>
    private UpdateCheckResult? _lastCheck;

    /// <summary>已下载并通过校验、等待安装的更新包路径与版本（选「稍后」时保留）。</summary>
    private string? _stagedPackagePath;
    private string? _stagedVersion;

    /// <summary>下载中的取消源；非 null 即表示正在下载。</summary>
    private CancellationTokenSource? _downloadCts;

    /// <summary>API 通道：诊断包里要写它的真实状态（可用 / 只读附着 / 不可用原因）。</summary>
    private readonly IWslcApiChannel? _apiChannel;

    /// <summary>最近一次导出的诊断包路径（「打开所在文件夹」要用）。</summary>
    private string? _lastDiagnosticsPath;

    /// <summary>引擎组件安装/修复（官方 WslcService 静态能力，不依赖会话）。</summary>
    private readonly IEnvironmentRepairService _repairService;

    public AboutPage()
    {
        InitializeComponent();
        _client = App.Services.GetRequiredService<IWslcClient>();
        _prefsService = App.Services.GetRequiredService<IPrefsService>();
        _updateChecker = App.Services.GetRequiredService<IUpdateChecker>();
        _updateDownloader = App.Services.GetRequiredService<IUpdateDownloader>();
        _apiChannel = App.Services.GetService<IWslcApiChannel>();
        _repairService = App.Services.GetRequiredService<IEnvironmentRepairService>();

        // 版本号只有一个来源（csproj 的 <Version> → 程序集），页面不写死
        VersionText.Text = $"版本 {AppInfo.Version} · 原生 WinUI 3 + .NET 10";
        CurrentVersionText.Text = AppInfo.Version;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        UpdateSourceText.Text = DescribeUpdateSource();
        await RefreshAsync(userInitiated: false);
    }

    public async Task RefreshAsync(bool userInitiated)
    {
        UpdateSourceText.Text = DescribeUpdateSource();

        try
        {
            var info = await _client.GetSystemInfoAsync();

            EngineVersionText.Text = string.IsNullOrWhiteSpace(info.WslVersion) ? "未检测到" : info.WslVersion;
            KernelText.Text = string.IsNullOrWhiteSpace(info.KernelVersion) ? "未知" : info.KernelVersion;
            SessionManagerText.Text = string.IsNullOrWhiteSpace(info.SessionManagerVersion) ? "未知" : info.SessionManagerVersion;
            WindowsText.Text = string.IsNullOrWhiteSpace(info.WindowsVersion) ? Environment.OSVersion.VersionString : info.WindowsVersion;
            SettingsFileText.Text = string.IsNullOrWhiteSpace(info.SettingsFilePath) ? "未检测到" : info.SettingsFilePath;
            MissingComponentsText.Text = info.MissingComponents.Count == 0
                ? "无"
                : string.Join("、", info.MissingComponents);

            // 组件齐全时"一键安装/修复"没有意义 —— 不显示比显示了再报错更诚实
            RepairComponentsButton.Visibility = info.MissingComponents.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;

            if (info.IsWslcReady)
            {
                ReadinessInfoBar.Severity = InfoBarSeverity.Success;
                ReadinessInfoBar.Title = "运行环境就绪";
                ReadinessInfoBar.Message = "WSL 容器引擎可用，面板功能可以正常使用。";
            }
            else
            {
                ReadinessInfoBar.Severity = InfoBarSeverity.Warning;
                ReadinessInfoBar.Title = "运行环境未完全就绪";
                ReadinessInfoBar.Message = $"请执行 “{UpgradeCommand}” 升级 WSL；若为受限环境（企业策略禁用 WSL 容器），请联系管理员。";
            }

            ReadinessInfoBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            ReadinessInfoBar.Severity = InfoBarSeverity.Error;
            ReadinessInfoBar.Title = "环境检测失败";
            ReadinessInfoBar.Message = ex.Message;
            ReadinessInfoBar.IsOpen = true;
        }
    }

    // ---------- 更新检查 ----------

    private string DescribeUpdateSource()

    {
        var repo = _prefsService.GetPreferences().UpdateRepo?.Trim() ?? string.Empty;
        return repo.Length == 0 ? "未配置（见「设置 → 应用偏好」）" : repo;
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        var repo = _prefsService.GetPreferences().UpdateRepo;
        UpdateSourceText.Text = DescribeUpdateSource();

        CheckUpdateButton.IsEnabled = false;
        UpdateInfoBar.IsOpen = false;

        try
        {
            var result = await _updateChecker.CheckAsync(repo, AppInfo.Version);
            ApplyUpdateResult(result);
        }
        catch (Exception ex)
        {
            // 服务层已把常见失败降级成结果对象；这里只兜住意外
            ApplyUpdateResult(UpdateCheckResult.Failure(AppInfo.Version, $"检查更新失败：{ex.Message}"));
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void ApplyUpdateResult(UpdateCheckResult result)
    {
        _lastCheck = result;

        // 手头这只包如果不是这次检查到的新版（用户换了更新源/发布了更新的版本），就当它不存在
        if (_stagedPackagePath is not null
            && (!File.Exists(_stagedPackagePath)
                || !string.Equals(_stagedVersion, result.LatestVersion, StringComparison.OrdinalIgnoreCase)))
        {
            _stagedPackagePath = null;
            _stagedVersion = null;
        }

        if (_stagedPackagePath is not null)
        {
            ShowStagedReadyInfo();
            LastCheckText.Text = $"{result.CheckedAt:yyyy-MM-dd HH:mm:ss}";
            RefreshUpdateActions();
            return;
        }

        UpdateInfoBar.Severity = result.Status switch
        {
            UpdateCheckStatus.UpdateAvailable => InfoBarSeverity.Success,
            UpdateCheckStatus.UpToDate => InfoBarSeverity.Success,
            UpdateCheckStatus.NotConfigured => InfoBarSeverity.Informational,
            _ => InfoBarSeverity.Warning
        };

        UpdateInfoBar.Title = result.Status switch
        {
            UpdateCheckStatus.UpdateAvailable => "有新版本可用",
            UpdateCheckStatus.UpToDate => "已是最新版本",
            UpdateCheckStatus.NotConfigured => "未配置更新源",
            _ => "检查更新未完成"
        };

        var message = result.Message;
        if (!string.IsNullOrWhiteSpace(result.Notes))
        {
            message += $"\n\n更新说明：{result.Notes}";
        }

        if (result.PublishedAt is { } published)
        {
            message += $"\n发布时间：{published:yyyy-MM-dd HH:mm}";
        }

        // 能拿到 exe 资产才谈得上"下载并替换"；只有发布页就是没办法的事，如实说清楚
        if (result.HasUpdate && !result.CanAutoUpdate)
        {
            message += "\n\n这次发布没有提供 exe 资产，只能打开下载页手动更新。";
        }

        UpdateInfoBar.Message = message;
        UpdateInfoBar.ActionButton = null;
        UpdateInfoBar.IsOpen = true;

        LastCheckText.Text = $"{result.CheckedAt:yyyy-MM-dd HH:mm:ss}";
        RefreshUpdateActions();
    }

    /// <summary>
    /// 按当前状态决定三个更新按钮谁可见。
    /// 同一时刻只留一个"下一步动作"，避免用户面对两个都能点的按钮不知道点哪个。
    /// </summary>
    private void RefreshUpdateActions()
    {
        var busy = _downloadCts is not null;
        var hasStaged = !busy && _stagedPackagePath is not null && File.Exists(_stagedPackagePath);
        var canDownload = !busy && !hasStaged && _lastCheck?.CanAutoUpdate == true;
        var canOpenPage = !busy && !hasStaged && _lastCheck?.CanOpenDownload == true;

        DownloadUpdateButton.Visibility = canDownload ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateButton.Visibility = hasStaged ? Visibility.Visible : Visibility.Collapsed;
        OpenDownloadPageButton.Visibility = canOpenPage ? Visibility.Visible : Visibility.Collapsed;

        var size = _lastCheck?.AssetSize;
        DownloadUpdateButtonText.Text = size is > 0
            ? $"下载并更新（{UpdateDownloadProgress.FormatSize(size.Value)}）"
            : "下载并更新";
    }

    private void ShowStagedReadyInfo()
    {
        UpdateInfoBar.Severity = InfoBarSeverity.Success;
        UpdateInfoBar.Title = $"更新 {_stagedVersion} 已下载";
        UpdateInfoBar.Message = "点「重启并安装」完成替换：应用会退出，新版本就位后自动启动。";
        UpdateInfoBar.ActionButton = null;
        UpdateInfoBar.IsOpen = true;
    }

    // ---------- 在线更新 ----------

    private async void DownloadAndUpdate_Click(object sender, RoutedEventArgs e)
    {
        var package = _lastCheck?.ToPackage();
        if (package is null) return;

        // 要动的是正在运行的程序文件，属于不可撤销操作：先确认，且默认焦点落在取消
        if (!await ConfirmUpdateAsync(package)) return;

        await DownloadPackageAsync(package);
    }

    private async Task<bool> ConfirmUpdateAsync(UpdatePackage package)
    {
        var target = AppInfo.ProcessPath;
        var sizeText = package.Size is > 0
            ? UpdateDownloadProgress.FormatSize(package.Size.Value)
            : "更新包";

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"更新到 {package.Version}",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Text =
                    $"将下载 {sizeText} 并替换当前程序文件：\n{target}\n\n" +
                    "替换时应用会自动退出并重启，这一步不可撤销。" +
                    $"旧版本会保留为同目录下的 {Path.GetFileName(target)}.old，出问题时可以手工恢复。"
            },
            PrimaryButtonText = "下载并更新",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task DownloadPackageAsync(UpdatePackage package)
    {
        // 先探一次可写性：装在 Program Files 时，别让用户白等 96 MB 下载才失败
        var directory = Path.GetDirectoryName(Path.GetFullPath(AppInfo.ProcessPath));
        if (!DirectoryWriteProbe.CanWrite(directory))
        {
            UpdateInfoBar.Severity = InfoBarSeverity.Warning;
            UpdateInfoBar.Title = "无法在线更新";
            UpdateInfoBar.Message =
                $"程序所在目录没有写入权限（{directory}），无法自动替换。" +
                "请改用「打开下载页」手动下载，或把程序移到用户可写的目录后再更新。";
            UpdateInfoBar.IsOpen = true;
            RefreshUpdateActions();
            return;
        }

        _downloadCts = new CancellationTokenSource();

        UpdateProgressPanel.Visibility = Visibility.Visible;
        UpdateProgressBar.IsIndeterminate = true;
        UpdateProgressBar.Value = 0;
        UpdateProgressText.Text = "正在连接 GitHub…";
        CheckUpdateButton.IsEnabled = false;
        RefreshUpdateActions();

        try
        {
            var progress = new Progress<UpdateDownloadProgress>(OnDownloadProgress);

            var outcome = await _updateDownloader.DownloadAsync(
                package, UpdateLocations.UpdatesDirectory(), progress, _downloadCts.Token);

            if (outcome.Succeeded && outcome.FilePath is not null)
            {
                _stagedPackagePath = outcome.FilePath;
                _stagedVersion = package.Version;

                UpdateProgressText.Text =
                    $"已下载并校验通过（{UpdateDownloadProgress.FormatSize(outcome.BytesWritten)}）。";

                ShowStagedReadyInfo();
                return;
            }

            UpdateInfoBar.Severity = outcome.Status == UpdateDownloadStatus.Cancelled
                ? InfoBarSeverity.Informational
                : InfoBarSeverity.Warning;
            UpdateInfoBar.Title = outcome.Status == UpdateDownloadStatus.Cancelled ? "已取消下载" : "下载未完成";
            UpdateInfoBar.Message = outcome.Message;
            UpdateInfoBar.ActionButton = null;
            UpdateInfoBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            UpdateInfoBar.Severity = InfoBarSeverity.Warning;
            UpdateInfoBar.Title = "下载未完成";
            UpdateInfoBar.Message = $"下载出错：{ex.Message}";
            UpdateInfoBar.IsOpen = true;
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
            CheckUpdateButton.IsEnabled = true;
            UpdateProgressPanel.Visibility = Visibility.Collapsed;
            RefreshUpdateActions();
        }
    }

    private void OnDownloadProgress(UpdateDownloadProgress progress)
    {
        UpdateProgressBar.IsIndeterminate = progress.IsIndeterminate;

        if (!progress.IsIndeterminate)
        {
            UpdateProgressBar.Value = progress.Percent!.Value;
        }

        UpdateProgressText.Text = progress.Describe();
    }

    private void CancelUpdate_Click(object sender, RoutedEventArgs e) => _downloadCts?.Cancel();

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_stagedPackagePath is null || !File.Exists(_stagedPackagePath))
        {
            _stagedPackagePath = null;
            _stagedVersion = null;

            UpdateInfoBar.Severity = InfoBarSeverity.Warning;
            UpdateInfoBar.Title = "更新包已不在";
            UpdateInfoBar.Message = "下载好的更新包被移走了（可能是安全软件或手工清理），请重新下载。";
            UpdateInfoBar.ActionButton = null;
            UpdateInfoBar.IsOpen = true;

            RefreshUpdateActions();
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "立即重启并安装？",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text =
                    $"应用将退出，由新版本 {_stagedVersion} 完成文件替换后自动启动。\n\n" +
                    "替换期间请不要强制结束进程；如果新版本启动异常，" +
                    $"可以手工恢复同目录下的 {Path.GetFileName(AppInfo.ProcessPath)}.old。"
            },
            PrimaryButtonText = "重启并安装",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (!LaunchUpdateHelper(_stagedPackagePath))
        {
            await DialogHelper.ShowErrorAsync(
                XamlRoot, "无法启动更新进程",
                "更新辅助进程没有起来，本次未做任何改动。请稍后重试，或打开下载页手动替换。");
            return;
        }

        // 退出后由辅助进程接手：等我们真正退出 → 备份 → 替换 → 拉起新版本
        App.MainWindow?.ExitApplication();
    }

    /// <summary>
    /// 启动更新辅助进程。它其实是"新版 exe 自己"，带上旧进程 PID 与目标路径，
    /// 启动后会在 App 构造函数里分流到替换逻辑（见 <see cref="UpdateHelper"/>）。
    /// </summary>
    private static bool LaunchUpdateHelper(string stagingPath)
    {
        try
        {
            var info = new ProcessStartInfo(stagingPath) { UseShellExecute = false };

            // 用 ArgumentList 而不是拼字符串：路径含空格/中文时由系统负责转义
            info.ArgumentList.Add(UpdateHelperArguments.Switch);
            info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add(AppInfo.ProcessPath);

            using var started = Process.Start(info);
            return started is not null;
        }
        catch (Exception ex)
        {
            // 提示由调用方给（这里拿不到 XamlRoot），失败原因落到更新日志里
            UpdateHelper.Log($"启动更新辅助进程失败：{ex}");
            return false;
        }
    }

    private void OpenDownloadPage_Click(object sender, RoutedEventArgs e)
    {
        var url = _lastCheck?.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        if (!TerminalLauncher.OpenUrl(url))
        {
            _ = DialogHelper.ShowErrorAsync(XamlRoot, "无法打开浏览器", $"请手动访问：{url}");
        }
    }

    private void CopyVersionInfo_Click(object sender, RoutedEventArgs e)
    {
        var prefs = _prefsService.GetPreferences();
        var text =
            $"wslc-desktop {AppInfo.Version}（FileVersion {AppInfo.FileVersion}）{Environment.NewLine}" +
            $"路径：{AppInfo.ProcessPath}{Environment.NewLine}" +
            $"系统：{Environment.OSVersion.VersionString}{Environment.NewLine}" +
            $"引擎：{EngineVersionText.Text} / 内核：{KernelText.Text} / 会话管理器：{SessionManagerText.Text}{Environment.NewLine}" +
            $"缺失组件：{MissingComponentsText.Text}{Environment.NewLine}" +
            $"刷新：{(prefs.UseEventStream ? "事件流 + 兜底轮询" : "轮询")} {prefs.RefreshIntervalSeconds}s，更新源：{(string.IsNullOrWhiteSpace(prefs.UpdateRepo) ? "未配置" : prefs.UpdateRepo)}";

        TerminalLauncher.CopyToClipboard(text);
        UpdateInfoBar.Severity = InfoBarSeverity.Informational;
        UpdateInfoBar.Title = "版本信息已复制";
        UpdateInfoBar.Message = "粘贴到 issue 里即可，不含任何容器/镜像数据。";
        UpdateInfoBar.ActionButton = null;
        UpdateInfoBar.IsOpen = true;
    }

    // ---------- 环境自检操作 ----------

    private async void RefreshCheck_Click(object sender, RoutedEventArgs e)
        => await RefreshAsync(userInitiated: true);

    private void CopyUpgradeCommand_Click(object sender, RoutedEventArgs e)
        => TerminalLauncher.CopyToClipboard(UpgradeCommand);

    private void OpenWslcPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (!TerminalLauncher.OpenWslcPrompt())
        {
            _ = DialogHelper.ShowErrorAsync(XamlRoot, "无法打开终端", "请在命令行手动执行 wslc --help。");
        }
    }
    /// <summary>
    /// 导出诊断包：采集 → 排版 → 打包。按钮禁用 + InfoBar 给结果，不阻塞界面。
    /// </summary>
    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (!ExportDiagnosticsButton.IsEnabled) return;

        ExportDiagnosticsButton.IsEnabled = false;
        DiagnosticsInfoBar.IsOpen = false;

        try
        {
            var path = await DiagnosticsExporter.ExportAsync(_client, _apiChannel);

            _lastDiagnosticsPath = path;
            RevealDiagnosticsButton.Visibility = Visibility.Visible;

            var size = new FileInfo(path).Length;

            DiagnosticsInfoBar.Severity = InfoBarSeverity.Success;
            DiagnosticsInfoBar.Title = "诊断包已生成";
            DiagnosticsInfoBar.Message =
                $"{Path.GetFileName(path)}（{DiagnosticsReport.FormatBytes(size)}）{Environment.NewLine}{path}";
            DiagnosticsInfoBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            DiagnosticsInfoBar.Severity = InfoBarSeverity.Error;
            DiagnosticsInfoBar.Title = "导出失败";
            DiagnosticsInfoBar.Message = ex.Message;
            DiagnosticsInfoBar.IsOpen = true;
        }
        finally
        {
            ExportDiagnosticsButton.IsEnabled = true;
        }
    }

    private void RevealDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (_lastDiagnosticsPath is null) return;

        DiagnosticsExporter.RevealInExplorer(_lastDiagnosticsPath);
    }

    /// <summary>
    /// 一键安装/修复引擎组件（M20）。
    ///
    /// 这是"系统级改动"：先确认再动手，全程显示进度，装完**复查**缺失列表才算成功。
    /// 官方 API 走的是 WslcService 静态能力，不受 M14 会话附着问题影响。
    /// </summary>
    private async void RepairComponents_Click(object sender, RoutedEventArgs e)
    {
        if (!RepairComponentsButton.IsEnabled) return;

        var confirmed = await DialogHelper.ConfirmDestructiveAsync(
            XamlRoot,
            "安装 / 修复引擎组件？",
            "将通过官方 API 下载并安装缺失的 WSL 组件；可能需要管理员权限，期间容器可能短暂不可用。",
            "开始安装");

        if (!confirmed) return;

        RepairComponentsButton.IsEnabled = false;
        RepairProgressPanel.Visibility = Visibility.Visible;
        RepairProgressBar.Value = 0;
        RepairProgressBar.IsIndeterminate = true;
        RepairStatusText.Text = "正在准备…";

        try
        {
            var result = await _repairService.RepairAsync(progress =>
            {
                // 进度回调在引擎线程：挪回 UI 线程再动控件
                DispatcherQueue.TryEnqueue(() =>
                {
                    RepairProgressBar.IsIndeterminate = !progress.IsDeterminate;
                    RepairProgressBar.Value = progress.Percent;
                    RepairStatusText.Text = progress.Display;
                });
            });

            RepairProgressBar.IsIndeterminate = false;
            if (result.Success) RepairProgressBar.Value = 100;
            RepairStatusText.Text = result.Message;

            ReadinessInfoBar.Severity = result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            ReadinessInfoBar.Title = result.Success ? "组件安装完成" : "组件安装未完成";
            ReadinessInfoBar.Message = result.Message;
            ReadinessInfoBar.IsOpen = true;

            // 复查一遍：自检数字与"一键修复"按钮的显隐都要跟着变
            await RefreshAsync(userInitiated: true);
        }
        catch (Exception ex)
        {
            RepairStatusText.Text = $"安装失败：{ex.Message}";

            ReadinessInfoBar.Severity = InfoBarSeverity.Error;
            ReadinessInfoBar.Title = "组件安装失败";
            ReadinessInfoBar.Message = ex.Message;
            ReadinessInfoBar.IsOpen = true;
        }
        finally
        {
            RepairComponentsButton.IsEnabled = true;
        }
    }

}
