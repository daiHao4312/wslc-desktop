using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WSLC.Panel.Core.Abstractions;
using WSLC_Panel_App.Helpers;

namespace WSLC_Panel_App.Pages;

/// <summary>
/// WSL 发行版管理（M10）。走 wsl.exe + 注册表 Lxss，与容器通道完全独立——
/// 本机没有发行版或 wsl.exe 被策略禁用时，这里只是空态，不影响面板其它功能。
/// </summary>
public sealed partial class DistrosPage : Page, IRefreshable
{
    private readonly IDistroService _distroService;
    private readonly CancellationTokenSource _pageCts = new();

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
        // 离开页面即取消在途刷新：续延不再操作已卸载的 UI
        _pageCts.Cancel();
    }

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
                ErrorInfoBar.Severity = InfoBarSeverity.Warning;
                ErrorInfoBar.Title = "未读取到发行版";
                ErrorInfoBar.Message = "wsl.exe 未返回任何发行版。若确认已安装，请检查是否被组策略禁用（.wslconfig / Intune）。";
                ErrorInfoBar.IsOpen = true;
            }
            else
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
            ErrorInfoBar.Severity = InfoBarSeverity.Error;
            ErrorInfoBar.Title = "读取发行版失败";
            ErrorInfoBar.Message = ex.Message;
            ErrorInfoBar.IsOpen = true;
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await RefreshAsync(userInitiated: true);

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

    private async void Start_Click(object sender, RoutedEventArgs e)
        => await RunAsync(sender, name => _distroService.StartAsync(name), "启动发行版失败");

    private async void Terminate_Click(object sender, RoutedEventArgs e)
        => await RunAsync(sender, name => _distroService.TerminateAsync(name), "终止发行版失败");

    private async void SetDefault_Click(object sender, RoutedEventArgs e)
        => await RunAsync(sender, name => _distroService.SetDefaultAsync(name), "设为默认发行版失败");

    private void OpenTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name }) return;

        if (!TerminalLauncher.OpenDistroShell(name))
        {
            _ = DialogHelper.ShowErrorAsync(XamlRoot, "无法打开终端", "未找到可用的终端程序，请在命令行执行：wsl -d " + name);
        }
    }

    private async void Unregister_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name }) return;

        var confirmed = await DialogHelper.ConfirmDestructiveAsync(
            XamlRoot,
            "注销（删除）发行版？",
            $"将注销发行版 {name}：其中所有文件、已安装软件与用户数据都会被删除（对应 VHDX 文件也会被移除）。\n如需保留数据，请先导出备份。此操作不可撤销。",
            "注销并删除");

        if (!confirmed) return;

        await RunAsync(sender, n => _distroService.UnregisterAsync(n), "注销发行版失败");
    }

    private async Task RunAsync(object sender, Func<string, Task<bool>> action, string failureTitle)
    {
        if (sender is not FrameworkElement { Tag: string name }) return;

        LoadingBar.Visibility = Visibility.Visible;
        try
        {
            var ok = await action(name);
            if (!ok) _ = DialogHelper.ShowErrorAsync(XamlRoot, failureTitle, $"对 {name} 的操作被引擎拒绝（可点右上角查看 wsl --status）。");
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
}
