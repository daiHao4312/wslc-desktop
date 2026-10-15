using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Helpers;

namespace WslcDesktop.Pages;

/// <summary>
/// 创建容器表单（M6）：所有输入即时反映到"等效命令预览"，预览与执行共用
/// <c>WslcCommandBuilder</c>，保证不会出现"预览好看、执行跑偏"。
/// </summary>
public sealed partial class CreateContainerPage : Page
{
    private readonly IWslcClient _client;
    private readonly CancellationTokenSource _pageCts = new();
    private bool _ready;
    private bool _prefillPortsDone;
    private bool _isBusy;

    public CreateContainerPage()
    {
        InitializeComponent();
        _client = App.Services.GetRequiredService<IWslcClient>();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        await LoadImagesAsync();
        await LoadNetworksAsync();

        PullPolicyCombo.SelectedIndex = 0;

        if (e.Parameter is string prefillImage && !string.IsNullOrWhiteSpace(prefillImage))
        {
            ImageCombo.Text = prefillImage;
            await ApplyImageFactsAsync(prefillImage);
        }

        _ready = true;
        UpdatePreview();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // 离开页面即取消在途的镜像/网络列表读取与预填查询
        _pageCts.Cancel();
    }

    private async Task LoadImagesAsync()
    {
        try
        {
            var images = await _client.GetImagesAsync(_pageCts.Token);
            ImageCombo.ItemsSource = images.Select(i => i.FullName).Distinct().OrderBy(n => n).ToList();
        }
        catch (OperationCanceledException)
        {
            // 页面已离开：静默收尾
        }
        catch
        {
            // 列表拉不到不妨碍手输镜像名
        }
    }

    private async Task LoadNetworksAsync()
    {
        var preset = new List<string> { "bridge", "host", "none" };
        try
        {
            var networks = await _client.GetNetworksAsync(_pageCts.Token);
            foreach (var name in networks.Select(n => n.Name).Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                if (!preset.Contains(name, StringComparer.OrdinalIgnoreCase)) preset.Add(name);
            }
        }
        catch (OperationCanceledException)
        {
            // 页面已离开：不再往下写控件
            return;
        }
        catch
        {
            // 忽略：保底预设已够用
        }

        NetworkCombo.ItemsSource = preset;
        NetworkCombo.Text = "bridge";
    }

    /// <summary>读取镜像声明的端口与命令并预填表单（镜像不在本地时给提示，不阻断）。</summary>
    private async Task ApplyImageFactsAsync(string reference)
    {
        try
        {
            var facts = await _client.InspectImageAsync(reference, _pageCts.Token);

            if (!facts.Found)
            {
                ImageHintText.Text = $"未读取到本地镜像信息（{facts.Error}）。创建时将按“拉取策略”处理。";
                return;
            }

            ImageHintText.Text = $"{facts.ExposedPortsSummary} · 启动命令：{facts.StartupSummary}";

            if (!_prefillPortsDone && facts.ExposedPorts.Count > 0 && string.IsNullOrWhiteSpace(PortsBox.Text))
            {
                PortsBox.Text = string.Join(Environment.NewLine, facts.ExposedPorts.Select(p => $"{p}:{p}"));
                _prefillPortsDone = true;
            }
        }
        catch (OperationCanceledException)
        {
            // 页面已离开：静默收尾
        }
        catch (Exception ex)
        {
            ImageHintText.Text = $"读取镜像信息失败：{ex.Message}";
        }
    }

    private async void ImageCombo_TextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        await ApplyImageFactsAsync(args.Text);
        UpdatePreview();
    }

    private void Field_Changed(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void Field_ComboChanged(object sender, object e) => UpdatePreview();

    private void Field_Toggled(object sender, RoutedEventArgs e) => UpdatePreview();

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(ContainersPage));
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        // 重入保护：创建期间禁用按钮。双击"创建并启动"会并发两个 CreateContainerAsync，
        // 真的建出两个容器，且第二个续延回来时页面已离开，提示打在看不见的地方
        if (_isBusy) return;
        _isBusy = true;
        CreateButton.IsEnabled = false;

        try
        {
            var options = BuildOptions();

            if (string.IsNullOrWhiteSpace(options.Image))
            {
                ShowMessage("请先填写镜像名称。", InfoBarSeverity.Error);
                return;
            }

            ShowMessage("正在创建容器…", InfoBarSeverity.Informational);

            var result = await _client.CreateContainerAsync(options);

            if (result.Success)
            {
                ShowMessage($"容器已创建并启动。{Environment.NewLine}{result.CommandLine}", InfoBarSeverity.Success);
                await Task.Delay(600);
                Frame.Navigate(typeof(ContainersPage));
                return;
            }

            ShowMessage($"{result.Message}{Environment.NewLine}等效命令：{result.CommandLine}", InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            ShowMessage($"创建容器失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            _isBusy = false;
            CreateButton.IsEnabled = true;
        }
    }

    private void CopyCommand_Click(object sender, RoutedEventArgs e)
        => TerminalLauncher.CopyToClipboard(CommandPreviewText.Text);

    private void UpdatePreview()
    {
        if (!_ready) return;
        CommandPreviewText.Text = WslcText(BuildOptions());
    }

    private static string WslcText(ContainerCreateOptions options)
        => WslcDesktop.Core.Parsing.WslcCommandBuilder.BuildRunCommandLine(options);

    /// <summary>表单 → 参数对象。多行输入按行拆，去空行与行内注释。</summary>
    private ContainerCreateOptions BuildOptions() => new()
    {
        Image = (ImageCombo.Text ?? string.Empty).Trim(),
        Name = NullIfBlank(NameBox.Text),
        Command = NullIfBlank(CommandBox.Text),
        Ports = SplitLines(PortsBox.Text),
        EnvironmentVariables = SplitLines(EnvBox.Text),
        Volumes = SplitLines(VolumesBox.Text),
        Network = NullIfBlank(NetworkCombo.Text),
        Memory = NullIfBlank(MemoryBox.Text),
        Cpus = NullIfBlank(CpusBox.Text),
        Workdir = NullIfBlank(WorkdirBox.Text),
        Hostname = NullIfBlank(HostnameBox.Text),
        PullPolicy = (PullPolicyCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "missing",
        RemoveOnExit = RemoveOnExitToggle.IsOn
    };

    private static IReadOnlyList<string> SplitLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        return text
            .Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void ShowMessage(string message, InfoBarSeverity severity)
    {
        MessageInfoBar.Severity = severity;
        MessageInfoBar.Title = severity == InfoBarSeverity.Error ? "创建失败" : "创建容器";
        MessageInfoBar.Message = message;
        MessageInfoBar.IsOpen = true;
    }
}
