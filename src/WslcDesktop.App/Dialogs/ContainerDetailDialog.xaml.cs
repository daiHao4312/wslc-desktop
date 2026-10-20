using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;
using WslcDesktop.Helpers;

namespace WslcDesktop.Dialogs;

/// <summary>
/// 容器详情（M7）三件套：inspect / 日志（快照 + **内嵌跟随**）/ 一次性 exec。
///
/// 日志跟随（M15）走 CLI 流式：<c>wslc logs -n N &lt;容器&gt; --follow</c> 的输出逐行进
/// <see cref="LogBuffer"/>（固定容量，满了丢最旧），界面每 300 ms 刷一次 ——
/// 实时能看到，也不会因为刷得太勤把 UI 拖死。
/// 交互式终端仍交给 Windows Terminal（不内嵌 ConPTY，理由见 PLAN-v2.0 §13）。
/// </summary>
public sealed partial class ContainerDetailDialog : ContentDialog
{
    /// <summary>界面刷新间隔。跟随日志可能刷得很快，逐行重排 TextBlock 会把 UI 拖住。</summary>
    private static readonly TimeSpan LogFlushInterval = TimeSpan.FromMilliseconds(300);

    private readonly IWslcClient _client;
    private readonly ContainerModel _container;

    private readonly LogBuffer _logBuffer = new();

    /// <summary>跟随的取消源；非 null = 正在跟随。</summary>
    private CancellationTokenSource? _followCts;

    /// <summary>
    /// 跟随的代次。每次开始 / 停止都 +1；在飞的异步任务只有"代次还等于自己"时才收尾，
    /// 避免点了停止之后又被旧任务的 finally 把状态改回去，或把开关再拨一下。
    /// </summary>
    private int _followGeneration;

    /// <summary>把缓冲里的新行搬到 TextBlock 的定时器。</summary>
    private DispatcherQueueTimer? _logTimer;

    /// <summary>已经渲染到界面上的行数（没新行就不重排）。</summary>
    private long _renderedTotal = -1;

    /// <summary>日志区当前状态，显示在工具条右侧。</summary>
    private string _logState = "正在读取…";

    /// <summary>「执行命令」的输出缓冲（M19）：长命令可能刷很多行，和有界缓冲同理。</summary>
    private readonly LogBuffer _execBuffer = new(500);

    /// <summary>执行输出的刷新定时器（200 ms 批量刷，避免逐行重排 TextBlock）。</summary>
    private DispatcherQueueTimer? _execTimer;

    private long _execRenderedTotal = -1;

    /// <summary>正在运行的容器内进程（只有 API 通道能提供，可发信号）。</summary>
    private IApiContainerProcess? _runningProcess;

    /// <summary>「执行命令」的取消源：关闭对话框时取消，避免续延操作已卸载的界面。</summary>
    private CancellationTokenSource? _execCts;

    /// <summary>当前是否是 CLI 模式的可停止命令（停止需要按 pidfile 发信号）。</summary>
    private bool _trackedRunning;

    /// <summary>程序化改 <see cref="FollowToggle"/> 时不要当成用户点击。</summary>
    private bool _suppressFollowToggle;

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
        SecondaryButtonText = "在终端中看日志";

        if (DispatcherQueue is { } queue)
        {
            _logTimer = queue.CreateTimer();
            _logTimer.Interval = LogFlushInterval;
            _logTimer.Tick += (_, _) => FlushLogs();

            _execTimer = queue.CreateTimer();
            _execTimer.Interval = TimeSpan.FromMilliseconds(200);
            _execTimer.Tick += (_, _) => FlushExecOutput();
        }

        Opened += (_, _) =>
        {
            // inspect 与日志是**两次独立的进程调用**：并行发起。
            // 之前是"取完 inspect 再去取日志"，两个 wslc 进程排队跑 —— 打开弹窗要空等几秒。
            _ = LoadInspectAsync();
            _ = LoadLogsAsync();
        };

        // 把真正会执行的命令挂在开关的提示里：跟随出问题时，用户一看就知道跑的是什么
        // （上一版就是参数位置写错，界面上却看不出任何线索）。
        ToolTipService.SetToolTip(
            FollowToggle,
            "持续追加新日志：\n" + WslcCommandBuilder.BuildLogsArguments(
                container.Id,
                WslcCommandBuilder.DefaultLogTail,
                follow: true));

        // 对话框一关就把跟随停掉：留着就是后台挂一个 wslc logs --follow 进程
        Closed += (_, _) =>
        {
            StopFollow();

            // 执行中的进程跟着关闭一起收掉：这个面板是"前台执行"，留着就成了没人管的孤儿进程
            _execCts?.Cancel();
            _runningProcess?.Kill();

            // CLI 模式杀客户端进程不够，还要按 pidfile 让容器内进程退出（best-effort）
            if (_trackedRunning) _ = _client.StopTrackedCommandAsync(_container.Id);
        };

        // ContentDialog 在 WinUI 3 里只有事件，没有可重写的 OnPrimaryButtonClick
        PrimaryButtonClick += (_, _) => OpenTerminalRequested = true;
        SecondaryButtonClick += (_, _) => OpenLogsInTerminalRequested = true;
    }

    /// <summary>用户点击"在终端中打开"（进容器 shell）。</summary>
    public bool OpenTerminalRequested { get; private set; }

    /// <summary>
    /// 用户点击"在终端中看日志"。
    ///
    /// 跟随现在就在本面板里（<see cref="StartFollowAsync"/>），这条只是**逃生口**：
    /// 需要在真实控制台里翻历史、按 Ctrl+C、或把输出拷给别处时用它。
    /// </summary>
    public bool OpenLogsInTerminalRequested { get; private set; }

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
        }
        catch (Exception ex)
        {
            ShowError($"读取 inspect 失败：{ex.Message}");
        }
    }

    // ---------------- 日志 ----------------

    /// <summary>
    /// 取一次快照。**会先停止跟随** —— 两条流同时往同一个面板写只会互相覆盖。
    /// </summary>
    private async Task LoadLogsAsync()
    {
        StopFollow();

        try
        {
            var logs = await _client.GetContainerLogsAsync(_container.Id, SelectedTail());

            _logBuffer.Clear();
            foreach (var line in SplitLines(logs)) _logBuffer.Append(line);

            // 把"为什么只有这几行"写在状态行里：快照是按行数取的，想看全部要点「跟随」
            _logState = $"快照（最近 {SelectedTail()} 行）";
            FlushLogs(force: true);
        }
        catch (Exception ex)
        {
            _logState = "读取失败";
            FlushLogs(force: true);
            ShowError($"读取日志失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 开始跟随。<c>wslc logs … --follow</c> 的输出逐行进缓冲，界面由定时器搬。
    ///
    /// 结束的三种情形都要在状态栏说清楚（容器停止 / 被用户停止 / 引擎不认参数），
    /// 否则用户会以为"日志不刷新了"是 bug。
    /// </summary>
    private async Task StartFollowAsync()
    {
        if (_followCts is not null) return;

        // 每次跟随有自己的**代次**：StopFollow 会把代次 +1，让在飞的那次认不出自己，
        // 于是它既不会覆盖新状态，也不会把开关再拨回去。
        var generation = ++_followGeneration;

        var cts = new CancellationTokenSource();
        _followCts = cts;

        _logBuffer.Clear();
        _renderedTotal = -1;
        _logState = "正在连接日志流…";
        FlushLogs(force: true);
        _logTimer?.Start();

        try
        {
            // 容器的 stdout / stderr 是两条流，**都要接**：
            // wslc 用自己的 stderr 承载容器的 stderr，MySQL / nginx 这类镜像的运行日志全在那儿。
            // 只接 stdout 的表现是"面板里只有 entrypoint 那几行，终端里却一大片"（实测踩过）。
            // 这个计数只用于"要不要回退快照"：引擎自己的报错也走 stderr，不能算进日志行数。
            var outputLines = 0;

            var result = await _client.StreamContainerLogsAsync(
                _container.Id,
                line =>
                {
                    outputLines++;
                    AppendLogLine(line);
                },
                AppendLogLine,
                cts.Token);

            _logState = result.IsSuccess
                ? "跟随已结束（容器可能已停止）"
                : AppendFirstLine($"跟随被引擎中断（退出码 {result.ExitCode}）", result.StandardError);

            // stdout 一行都没收到就失败：多半是引擎不认参数或容器不存在 → 回退快照，
            // 让用户至少能看到历史，同时把"为什么回退"留在状态行。
            if (!result.IsSuccess && outputLines == 0)
            {
                var snapshot = await _client.GetContainerLogsAsync(_container.Id, SelectedTail());

                _logBuffer.Clear();
                foreach (var line in SplitLines(snapshot)) _logBuffer.Append(line);

                _logState = "跟随没起来（引擎报了错），已回退为快照";
            }
        }
        catch (OperationCanceledException)
        {
            if (generation == _followGeneration) _logState = "已停止跟随";
        }
        catch (Exception ex)
        {
            if (generation == _followGeneration) _logState = $"跟随失败：{ex.Message}";
        }
        finally
        {
            cts.Dispose();

            // 代次变了 = 期间被停止或被新的跟随取代，收尾交给 StopFollow / 新的那次
            if (generation == _followGeneration)
            {
                _followCts = null;
                _logTimer?.Stop();
                FlushLogs(force: true);
                SyncFollowToggle();
            }
        }
    }

    /// <summary>
    /// 停止跟随。状态与开关**立刻**复位，不等进程真正退出 ——
    /// 否则点了像没反应（进程要等几百毫秒才咽气）。
    /// </summary>
    private void StopFollow()
    {
        if (_followCts is null) return;

        _followGeneration++;
        _followCts.Cancel();
        _followCts = null;
        _logTimer?.Stop();

        _logState = "已停止跟随";
        FlushLogs(force: true);
        SyncFollowToggle();
    }

    /// <summary>
    /// 把缓冲搬到界面。**没有新行就直接返回**：空转的容器不该占 UI 时间
    /// （跟随可能挂几小时，一秒空跑几次重排是纯浪费）。
    /// </summary>
    private void FlushLogs(bool force = false)
    {
        if (!force && _logBuffer.TotalAppended == _renderedTotal) return;

        var atBottom = IsLogAtBottom();

        LogText.Text = _logBuffer.Count == 0 ? "（暂无日志输出）" : _logBuffer.Text;
        _renderedTotal = _logBuffer.TotalAppended;

        if (atBottom && _logBuffer.Count > 0) ScrollLogsToBottom();

        UpdateLogStatus();
    }

    /// <summary>用户在底部才自动贴底；上滚看历史时不抢他的滚动位置。</summary>
    private bool IsLogAtBottom() => LogScroller.ScrollableHeight - LogScroller.VerticalOffset <= 24;

    private void ScrollLogsToBottom()
    {
        // 刚换完文本时 ScrollableHeight 还是旧值，等这一轮布局结束再滚
        DispatcherQueue.TryEnqueue(() =>
            LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null, disableAnimation: true));
    }

    private void UpdateLogStatus()
    {
        // 已经在出日志就不该再显示"正在连接"—— 否则用户会以为卡住了（截图里就是这样）
        if (_followCts is not null && _logBuffer.TotalAppended > 0) _logState = "跟随中";

        var dropped = _logBuffer.DroppedLines > 0
            ? $"，更早的 {_logBuffer.DroppedLines} 行已丢弃"
            : string.Empty;

        LogStatusText.Text = $"{_logState}　共 {_logBuffer.TotalAppended} 行{dropped}";
    }

    /// <summary>
    /// 日志流的一行。**只入缓冲、不碰界面**（回调在进程的读取线程上）。
    ///
    /// 正常情况下由 300 ms 定时器批量搬运；拿不到 DispatcherQueue 时退回"每行排一次队"——
    /// 否则会出现"日志进来了但界面永远不刷新"这种最难查的静默失败。
    /// </summary>
    private void AppendLogLine(string line)
    {
        _logBuffer.Append(line);

        if (_logTimer is null) DispatcherQueue?.TryEnqueue(() => FlushLogs());
    }

    private void SyncFollowToggle()
    {
        _suppressFollowToggle = true;
        FollowToggle.IsChecked = _followCts is not null;
        _suppressFollowToggle = false;
    }

    private static IEnumerable<string> SplitLines(string? text)
        => string.IsNullOrEmpty(text)
            ? Array.Empty<string>()
            : text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

    private static string AppendFirstLine(string prefix, string? detail)
    {
        var text = (detail ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (text.Length == 0) return prefix;

        var newline = text.IndexOf('\n');
        return $"{prefix}：{(newline >= 0 ? text[..newline] : text)}";
    }

    // ---------------- 日志工具条 ----------------

    private async void FollowToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressFollowToggle) return;

        if (FollowToggle.IsChecked == true) await StartFollowAsync();
        else StopFollow();
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        _logBuffer.Clear();
        _renderedTotal = -1;
        FlushLogs(force: true);
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
        if (_runningProcess is not null) return;   // 已有进程在跑：先停再加

        var command = CommandBox.Text.Trim();
        if (string.IsNullOrEmpty(command)) return;

        _execBuffer.Clear();
        _execRenderedTotal = -1;
        FlushExecOutput(force: true);

        RunCommandButton.IsEnabled = false;
        StopCommandButton.IsEnabled = false;
        _execTimer?.Start();

        // 立刻给反馈：探测通道可能要几百毫秒，期间不能让界面像没反应
        CommandStatusText.Text = "正在启动…";

        _execCts = new CancellationTokenSource();

        try
        {
            // 先问 API：只有它能给信号（停止作用于**容器内进程**，而不是我们自己的客户端进程）
            var started = await _client.TryStartContainerProcessAsync(
                _container.Id,
                command,
                AppendExecLine,
                _execCts.Token);

            if (started.Available)
            {
                _runningProcess = started.Process;
                StopCommandButton.IsEnabled = true;
                CommandStatusText.Text = $"运行中（PID {_runningProcess!.Pid}）· 输出实时刷新，可随时停止";

                var exitCode = await _runningProcess.WaitForExitAsync(_execCts.Token);
                FlushExecOutput(force: true);

                CommandStatusText.Text = exitCode == 0
                    ? "已完成（退出码 0）"
                    : $"进程结束（退出码 {exitCode}）";

                return;
            }

            // API 通道给不了信号（引擎的会话是"机器级唯一、进程独占"的，CLI 的容器在
            // wslc-cli-* 会话里，API 附着只会拿到 0x80040607 SessionReserved）。
            // 改用「容器内记 PID + 按 PID 发信号」的 CLI 包装 —— 停止照样能用，只是实现换了。
            var note = started.Error.Contains("关闭", StringComparison.Ordinal)
                ? "CLI 模式（官方 API 通道已关闭）"
                : $"CLI 模式（API 不可用：{started.Error}）";

            CommandStatusText.Text = $"{note}；正在执行（可停止）…";
            StopCommandButton.IsEnabled = true;
            _trackedRunning = true;

            // 流式执行：实时输出，停止时按 pidfile 发 TERM → KILL
            var result = await _client.StreamTrackedCommandAsync(
                _container.Id,
                command,
                AppendExecLine,
                _execCts.Token);

            FlushExecOutput(force: true);

            CommandStatusText.Text = result.IsSuccess
                ? $"已完成（退出码 0）· {note}"
                : $"执行失败（退出码 {result.ExitCode}）· {note}";
        }
        catch (OperationCanceledException)
        {
            CommandStatusText.Text = "已取消";
        }
        catch (Exception ex)
        {
            CommandStatusText.Text = $"执行失败：{ex.Message}";
            ShowError($"执行命令失败：{ex.Message}");
        }
        finally
        {
            RunCommandButton.IsEnabled = true;
            StopCommandButton.IsEnabled = false;
            _execTimer?.Stop();
            FlushExecOutput(force: true);
            _runningProcess?.Dispose();
            _runningProcess = null;

            _execCts?.Dispose();
            _execCts = null;
            _trackedRunning = false;
        }
    }

    /// <summary>
    /// 停止容器内进程：先 SIGTERM，3 秒没退再 SIGKILL。
    ///
    /// 两级信号是刻意的 —— 上来就 SIGKILL 会让容器内的进程来不及落盘/收尾；
    /// 但只发 SIGTERM 又可能遇到不响应的进程，那样"停止"按钮就成了摆设。
    /// </summary>
    private async void StopCommand_Click(object sender, RoutedEventArgs e)
    {
        StopCommandButton.IsEnabled = false;

        if (_runningProcess is { } process)
        {
            process.Terminate();
            CommandStatusText.Text = "已发送 SIGTERM，等待进程退出…";

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill();
                CommandStatusText.Text = "进程未响应 SIGTERM，已发送 SIGKILL";
            }

            return;
        }

        // CLI 模式：按容器内 pidfile 发信号（TERM → 200 ms → KILL）
        CommandStatusText.Text = "正在停止（按 PID 发送 SIGTERM/SIGKILL）…";

        try
        {
            var stop = await _client.StopTrackedCommandAsync(_container.Id);
            CommandStatusText.Text = stop.Success
                ? "已发送停止信号（SIGTERM → SIGKILL）"
                : AppendFirstLine("停止失败", stop.Error);
        }
        catch (Exception ex)
        {
            CommandStatusText.Text = $"停止失败：{ex.Message}";
        }
    }

    /// <summary>输出的一行（回调可能在引擎线程上）：只入缓冲，刷新交给定时器。</summary>
    private void AppendExecLine(string line)
    {
        _execBuffer.Append(line);

        if (_execTimer is null) DispatcherQueue?.TryEnqueue(() => FlushExecOutput());
    }

    private void FlushExecOutput(bool force = false)
    {
        if (!force && _execBuffer.TotalAppended == _execRenderedTotal) return;

        CommandOutputText.Text = _execBuffer.Count == 0 ? string.Empty : _execBuffer.Text;
        _execRenderedTotal = _execBuffer.TotalAppended;

        DispatcherQueue.TryEnqueue(() =>
            CommandOutputScroller.ChangeView(null, CommandOutputScroller.ScrollableHeight, null, disableAnimation: true));
    }

    private void ShowError(string message)
    {
        DialogErrorBar.Title = "操作失败";
        DialogErrorBar.Message = message;
        DialogErrorBar.IsOpen = true;
    }
}
