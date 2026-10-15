using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WSLC.Panel.Core.Abstractions;
using WSLC.Panel.Core.Models;
using WSLC_Panel_App.Helpers;

namespace WSLC_Panel_App.Pages;

/// <summary>
/// 关于页：项目信息 + 版本更新检查 + 环境自检（升级引导的可操作落点，对应 P0-1 与风险表 #1/#7）。
/// </summary>
public sealed partial class AboutPage : Page, IRefreshable
{
    private const string UpgradeCommand = "wsl --update";

    private readonly IWslcClient _client;
    private readonly IPrefsService _prefsService;
    private readonly IUpdateChecker _updateChecker;

    public AboutPage()
    {
        InitializeComponent();
        _client = App.Services.GetRequiredService<IWslcClient>();
        _prefsService = App.Services.GetRequiredService<IPrefsService>();
        _updateChecker = App.Services.GetRequiredService<IUpdateChecker>();

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
        UpdateInfoBar.ActionButton = null;

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

        UpdateInfoBar.Message = message;

        if (result.CanOpenDownload)
        {
            var url = result.DownloadUrl!;
            var openButton = new Button { Content = "打开下载页" };
            openButton.Click += (_, _) =>
            {
                if (!TerminalLauncher.OpenUrl(url))
                {
                    _ = DialogHelper.ShowErrorAsync(XamlRoot, "无法打开浏览器", $"请手动访问：{url}");
                }
            };

            UpdateInfoBar.ActionButton = openButton;
        }

        UpdateInfoBar.IsOpen = true;
        LastCheckText.Text = $"{result.CheckedAt:yyyy-MM-dd HH:mm:ss}";
    }

    private void CopyVersionInfo_Click(object sender, RoutedEventArgs e)
    {
        var prefs = _prefsService.GetPreferences();
        var text =
            $"WSLC 面板工具 {AppInfo.Version}（FileVersion {AppInfo.FileVersion}）{Environment.NewLine}" +
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
}
