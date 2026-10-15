using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WSLC.Panel.Core.Abstractions;
using WSLC.Panel.Core.Models;
using WSLC_Panel_App.Helpers;

namespace WSLC_Panel_App.Dialogs;

/// <summary>
/// 容器详情三件套（M7）：inspect / 日志快照 / 一次性 exec。
/// 交互式终端与日志跟随交给 Windows Terminal（M7 决策：不内嵌 ConPTY）。
/// </summary>
public sealed partial class ContainerDetailDialog : ContentDialog
{
    private readonly IWslcClient _client;
    private readonly ContainerModel _container;

    public ContainerDetailDialog(ContainerModel container, IWslcClient client)
    {
        InitializeComponent();

        _container = container;
        _client = client;

        NameText.Text = container.Name;
        StateText.Text = container.StateDisplay;
        SummaryText.Text = $"{container.Image} · ID {container.ShortId} · {container.UptimeDisplay}";
        PortsText.Text = container.PortMappings.Count == 0
            ? "无端口映射"
            : "端口：" + string.Join("，", container.PortMappings.Select(p => p.DisplayString));

        ApplyStatePill(container);
        DetailSelector.SelectedItem = InspectTab;

        PrimaryButtonText = "在终端中打开";
        SecondaryButtonText = "跟随日志";
        Opened += (_, _) => _ = LoadInspectAsync();

        // ContentDialog 在 WinUI 3 里只有事件，没有可重写的 OnPrimaryButtonClick
        PrimaryButtonClick += (_, _) => OpenTerminalRequested = true;
        SecondaryButtonClick += (_, _) => FollowLogsRequested = true;
    }

    /// <summary>用户点击"在终端中打开"。</summary>
    public bool OpenTerminalRequested { get; private set; }

    /// <summary>用户点击"跟随日志"。</summary>
    public bool FollowLogsRequested { get; private set; }

    /// <summary>
    /// 分段切换：三块 Pane 叠在同一个 <c>*</c> 行里，只切 <c>Visibility</c>。
    /// 不用 Frame / 懒加载是为了让对话框高度在切页时保持不动。
    /// </summary>
    private void DetailSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var selected = sender.SelectedItem;

        InspectPane.Visibility = ReferenceEquals(selected, InspectTab) ? Visibility.Visible : Visibility.Collapsed;
        LogPane.Visibility = ReferenceEquals(selected, LogTab) ? Visibility.Visible : Visibility.Collapsed;
        ExecPane.Visibility = ReferenceEquals(selected, ExecTab) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 状态徽章改用「浅底 + 同色深字」，取主题资源而非写死颜色：
    /// 原来的 Accent 实心填充 + 白字在深色/浅色主题下都过于抢眼，也不承载语义。
    /// </summary>
    private void ApplyStatePill(ContainerModel container)
    {
        var (backgroundKey, foregroundKey) = container.State.ToLowerInvariant() switch
        {
            "running" => ("SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush"),
            "created" => ("SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush"),
            "exited" => ("ControlFillColorSecondaryBrush", "TextFillColorSecondaryBrush"),
            _ => ("ControlFillColorSecondaryBrush", "TextFillColorSecondaryBrush")
        };

        if (TryBrush(backgroundKey) is { } background) StatePill.Background = background;
        if (TryBrush(foregroundKey) is { } foreground) StateText.Foreground = foreground;
    }

    /// <summary>主题资源查不到就返回 null，让 XAML 里的默认值兜底，不让配色阻断对话框显示。</summary>
    private static Brush? TryBrush(string key)
    {
        try
        {
            return Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
                ? brush
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task LoadInspectAsync()
    {
        try
        {
            var json = await _client.InspectContainerAsync(_container.Id);
            InspectText.Text = string.IsNullOrWhiteSpace(json) ? "（引擎未返回 inspect 内容）" : json;
            _ = LoadLogsAsync();
        }
        catch (Exception ex)
        {
            ShowError($"读取 inspect 失败：{ex.Message}");
        }
    }

    private async Task LoadLogsAsync()
    {
        try
        {
            var tail = SelectedTail();
            var logs = await _client.GetContainerLogsAsync(_container.Id, tail);
            LogText.Text = string.IsNullOrWhiteSpace(logs) ? "（暂无日志输出）" : logs;
        }
        catch (Exception ex)
        {
            ShowError($"读取日志失败：{ex.Message}");
        }
    }

    private int SelectedTail()
        => LogTailCombo.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var value)
            ? value
            : 100;

    private async void LoadInspect_Click(object sender, RoutedEventArgs e)
        => await LoadInspectAsync();

    private void CopyInspect_Click(object sender, RoutedEventArgs e)
        => TerminalLauncher.CopyToClipboard(InspectText.Text);

    private async void ReloadLogs_Click(object sender, RoutedEventArgs e)
        => await LoadLogsAsync();

    private async void LogTail_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LogText is null) return;
        await LoadLogsAsync();
    }

    private async void RunCommand_Click(object sender, RoutedEventArgs e)
        => await RunCommandAsync();

    private async void CommandBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        await RunCommandAsync();
    }

    private async Task RunCommandAsync()
    {
        var command = CommandBox.Text.Trim();
        if (string.IsNullOrEmpty(command)) return;

        RunCommandButton.IsEnabled = false;
        CommandOutputText.Text = "执行中…";

        try
        {
            var result = await _client.ExecAsync(_container.Id, command);

            var text = result.Output;
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                text = string.IsNullOrWhiteSpace(text) ? result.Error : $"{text}\n[stderr]\n{result.Error}";
            }

            CommandOutputText.Text = string.IsNullOrWhiteSpace(text)
                ? "（命令执行完成，无输出）"
                : text;
        }
        catch (Exception ex)
        {
            ShowError($"执行命令失败：{ex.Message}");
        }
        finally
        {
            RunCommandButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        DialogErrorBar.Title = "操作失败";
        DialogErrorBar.Message = message;
        DialogErrorBar.IsOpen = true;
    }
}
