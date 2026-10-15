using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WSLC.Panel.Core.Abstractions;
using WSLC_Panel_App.Helpers;

namespace WSLC_Panel_App.Pages;

/// <summary>关于页：项目信息 + 环境自检（升级引导的可操作落点，对应 P0-1 与风险表 #1/#7）。</summary>
public sealed partial class AboutPage : Page, IRefreshable
{
    private const string UpgradeCommand = "wsl --update";

    private readonly IWslcClient _client;

    public AboutPage()
    {
        InitializeComponent();
        _client = App.Services.GetRequiredService<IWslcClient>();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await RefreshAsync(userInitiated: false);
    }

    public async Task RefreshAsync(bool userInitiated)
    {
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
