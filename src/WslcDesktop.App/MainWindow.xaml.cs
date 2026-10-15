using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Helpers;
using WslcDesktop.Pages;

namespace WslcDesktop;

public sealed partial class MainWindow : Window
{
    private const string UpgradeCommand = "wsl --update";

    /// <summary>事件合并窗口：一次启动容器会连出 create/connect/start 多条，没必要刷三遍。</summary>
    private static readonly TimeSpan EventDebounce = TimeSpan.FromMilliseconds(300);

    /// <summary>事件流兜底轮询的下限（事件流通了就不该再靠 3 秒轮询）。</summary>
    private const int EventStreamFallbackSeconds = 30;

    /// <summary>Ctrl+数字 的页面跳转表，顺序与左侧导航一致。</summary>
    private static readonly (VirtualKey Key, string Tag)[] PageShortcuts =
    {
        (VirtualKey.Number1, "overview"),
        (VirtualKey.Number2, "containers"),
        (VirtualKey.Number3, "images"),
        (VirtualKey.Number4, "networks"),
        (VirtualKey.Number5, "volumes"),
        (VirtualKey.Number6, "sessions"),
        (VirtualKey.Number7, "distros"),
        (VirtualKey.Number8, "activity")
    };

    private readonly IWslcClient _client;
    private readonly IPrefsService _prefsService;
    private readonly IEventStreamService _eventStream;

    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _eventDebounceTimer;

    private TrayIcon? _tray;

    /// <summary>窗口图标句柄：由 <see cref="AppIconLoader"/> 从 exe 提取，窗口销毁时必须释放。</summary>
    private IntPtr _windowIcon;

    private bool _allowClose;
    private bool _trayHintShown;
    private bool _engineCheckDone;

    public MainWindow()
    {
        InitializeComponent();

        _client = App.Services.GetRequiredService<IWslcClient>();
        _prefsService = App.Services.GetRequiredService<IPrefsService>();
        _eventStream = App.Services.GetRequiredService<IEventStreamService>();

        // 窗口关闭时退订：单例单窗口下不会真的泄漏，但订阅方生命周期要对齐
        Closed += MainWindow_Closed;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        // 窗口图标（标题栏 / 任务栏 / Alt+Tab）从 exe 的 PE 资源取。
        // 别用 AppWindow.SetIcon(string)：那个重载按**当前工作目录**解析相对路径，
        // 而单文件发布下 Assets 在临时解压目录，两者对不上，SetIcon 会静默失败
        // ——实测双击发布版 exe 时任务栏一片空白。
        _windowIcon = AppIconLoader.LoadLarge();
        if (_windowIcon != IntPtr.Zero)
        {
            AppWindow.SetIcon(Win32Interop.GetIconIdFromIcon(_windowIcon));
        }

        var prefs = _prefsService.GetPreferences();
        ApplyTheme(prefs.Theme);

        // 必须赶在首次 Navigate 之前订阅：内容区导航完成后统一回写左侧高亮
        NavFrame.Navigated += NavFrame_Navigated;
        NavFrame.Navigate(typeof(OverviewPage), prefs);

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(1, prefs.RefreshIntervalSeconds))
        };
        _refreshTimer.Tick += RefreshTimer_Tick;

        _eventDebounceTimer = new DispatcherTimer { Interval = EventDebounce };
        _eventDebounceTimer.Tick += EventDebounceTimer_Tick;

        // 先订阅再启动：否则事件流首次回补的那批历史事件会全部落空
        _eventStream.EventReceived += OnEngineEvent;
        _eventStream.StatusChanged += OnEventStreamStatusChanged;

        AppWindow.Closing += OnWindowClosing;

        SetupTray();
        RegisterShortcuts();

        // 先启动事件流再套用轮询间隔：事件流首次 StatusChanged 会把轮询放宽到 30s，
        // 反过来的话启动后有数秒仍在按 3 秒空转
        if (prefs.UseEventStream)
        {
            _eventStream.Start();
        }

        ApplyEffectiveInterval(prefs);

        _refreshTimer.Start();

        _ = UpdateStatusBarAsync(userInitiated: false);

    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        // 订阅方生命周期纪律：退订与 Activate 对称（ActivityPage 的 Subscribe/Unsubscribe 同款）
        _eventStream.EventReceived -= OnEngineEvent;
        _eventStream.StatusChanged -= OnEventStreamStatusChanged;

        AppIconLoader.Release(_windowIcon);
        _windowIcon = IntPtr.Zero;
    }

    // ---------- 托盘 ----------

    private void SetupTray()
    {
        _tray = new TrayIcon(
            "wslc-desktop",
            onActivate: BringToFront,
            onRefresh: TrayRefresh,
            onExit: ExitApplication);

        if (_tray.TryCreate()) return;

        // 托盘建不出来就彻底放弃这条路：否则"关闭到托盘"会把窗口藏进一个不存在的图标里
        _tray.Dispose();
        _tray = null;
    }

    public void DisposeTray()
    {
        _tray?.Dispose();
        _tray = null;
    }

    /// <summary>把主窗口唤到前台（可能来自托盘点击或第二个实例，不一定在 UI 线程）。</summary>
    public void BringToFront()
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            try
            {
                if (AppWindow.Presenter is OverlappedPresenter presenter &&
                    presenter.State == OverlappedPresenterState.Minimized)
                {
                    presenter.Restore();
                }

                if (!AppWindow.IsVisible)
                {
                    AppWindow.Show();
                }

                Activate();
            }
            catch
            {
                // 唤前台失败不该抛给调用方
            }
        });
    }

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        if (_tray is null) return;
        if (!_prefsService.GetPreferences().CloseToTray) return;

        args.Cancel = true;
        AppWindow.Hide();

        // 只提示一次：否则每次关闭都弹气泡会烦人，但第一次必须说清楚还能去哪儿找它
        if (_trayHintShown) return;

        _trayHintShown = true;
        _tray.ShowBalloon(
            "wslc-desktop 仍在后台运行",
            "双击托盘图标可重新打开；右键托盘图标可「退出」，或在设置里关闭「关闭到托盘」。");
    }

    /// <summary>真正退出（托盘菜单 / 设置项）。</summary>
    public void ExitApplication()
    {
        _allowClose = true;
        DispatcherQueue?.TryEnqueue(() => Close());
    }

    private void TrayRefresh()
    {
        DispatcherQueue?.TryEnqueue(async () =>
        {
            try
            {
                await RefreshCurrentPageAsync(userInitiated: true);
                await UpdateStatusBarAsync(userInitiated: true);
            }
            catch
            {
                // 托盘触发的刷新失败不弹窗
            }
        });
    }

    // ---------- 快捷键 ----------

    private void RegisterShortcuts()
    {
        AddShortcut(VirtualKey.N, VirtualKeyModifiers.Control, () => NavigateTo(typeof(CreateContainerPage)));
        AddShortcut(VirtualKey.R, VirtualKeyModifiers.Control, TrayRefresh);

        foreach (var (key, tag) in PageShortcuts)
        {
            AddShortcut(key, VirtualKeyModifiers.Control, () => NavigateToTag(tag));
        }
    }

    private void AddShortcut(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            action();
        };

        RootGrid.KeyboardAccelerators.Add(accelerator);
    }

    private void NavigateToTag(string tag)
    {
        var pageType = PageTypeForTag(tag);
        if (pageType is null) return;

        // 高亮不在这里设：统一交给 NavFrame_Navigated，避免两条路径各写一份而漂移
        NavigateTo(pageType);
    }

    /// <summary>
    /// 内容区导航完成后回写左侧高亮。
    ///
    /// 返回按钮（<see cref="TitleBar_BackRequested"/>）只回退 Frame，不会触发 NavigationView 的
    /// SelectionChanged —— 这正是"内容退回去了、高亮还停在原页"的根因。
    /// 挂在 Navigated 上一次兜住：返回、快捷键、Ctrl+N、代码内跳转全部覆盖。
    /// </summary>
    private void NavFrame_Navigated(object sender, NavigationEventArgs e)
    {
        // Frame.CanGoBack 不是依赖属性、Frame 也不实现 INotifyPropertyChanged：
        // x:Bind OneWay 没有变更通知源，返回按钮会永远停在初始值。只能每次导航后手动同步
        AppTitleBar.IsBackButtonVisible = NavFrame.CanGoBack;

        SyncNavSelection(e.SourcePageType);
    }

    private void SyncNavSelection(Type? pageType)
    {
        var tag = NavTagForPage(pageType);
        if (tag is null) return;

        // 「设置」不是我们自己摆的 NavigationViewItem，而是 NavigationView 内置的那一项：
        // 它不在 MenuItems 也不在 FooterMenuItems 里，只能通过 SettingsItem 引用去比对。
        if (tag == "settings")
        {
            var settingsItem = NavView.SettingsItem;
            if (settingsItem is null || ReferenceEquals(NavView.SelectedItem, settingsItem)) return;

            NavView.SelectedItem = settingsItem;
            return;
        }

        // 已经高亮在同一项就别再赋值：重复赋值会二次触发 SelectionChanged
        if (NavView.SelectedItem is NavigationViewItem current && current.Tag as string == tag) return;

        // 「关于」在 FooterMenuItems 里，其余在 MenuItems 里：两处都要找
        foreach (var item in NavView.MenuItems.Concat(NavView.FooterMenuItems).OfType<NavigationViewItem>())
        {
            if (item.Tag as string == tag)
            {
                NavView.SelectedItem = item;
                return;
            }
        }
    }

    /// <summary>页面类型 → 导航项 Tag。不在导航里的页（新建容器）归到它的入口页，避免高亮空掉。</summary>
    private static string? NavTagForPage(Type? pageType)
    {
        if (pageType == typeof(OverviewPage)) return "overview";
        if (pageType == typeof(ContainersPage) || pageType == typeof(CreateContainerPage)) return "containers";
        if (pageType == typeof(ImagesPage)) return "images";
        if (pageType == typeof(NetworksPage)) return "networks";
        if (pageType == typeof(VolumesPage)) return "volumes";
        if (pageType == typeof(SessionsPage)) return "sessions";
        if (pageType == typeof(DistrosPage)) return "distros";
        if (pageType == typeof(ActivityPage)) return "activity";
        if (pageType == typeof(AboutPage)) return "about";
        if (pageType == typeof(SettingsPage)) return "settings";
        return null;
    }

    private static Type? PageTypeForTag(string tag) => tag switch
    {
        "overview" => typeof(OverviewPage),
        "containers" => typeof(ContainersPage),
        "images" => typeof(ImagesPage),
        "networks" => typeof(NetworksPage),
        "volumes" => typeof(VolumesPage),
        "sessions" => typeof(SessionsPage),
        "distros" => typeof(DistrosPage),
        "activity" => typeof(ActivityPage),
        "about" => typeof(AboutPage),
        _ => null
    };

    /// <summary>主题三态：立即重绘（原生控件只有一套配色，无需同步组件库）。</summary>
    public void ApplyTheme(string themeStr)
    {
        RootGrid.RequestedTheme = themeStr switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    /// <summary>设置页改动轮询间隔 / 事件流开关后即时生效。</summary>
    public void ApplyRefreshSettings() => ApplyEffectiveInterval(_prefsService.GetPreferences());

    /// <summary>兼容旧调用点：设置页选了新的轮询周期。</summary>
    public void ApplyRefreshInterval(int seconds) => ApplyRefreshSettings();

    /// <summary>设置页切换"引擎事件流"。</summary>
    public void ApplyEventStreamEnabled(bool enabled)
    {
        if (enabled)
        {
            _eventStream.Start();
        }
        else
        {
            _eventStream.Stop();
        }

        ApplyRefreshSettings();
    }

    /// <summary>刷新当前可见页（手动刷新与后台轮询共用）。</summary>
    public async Task RefreshCurrentPageAsync(bool userInitiated)
    {
        if (NavFrame.Content is IRefreshable refreshable)
        {
            await refreshable.RefreshAsync(userInitiated);
        }
    }

    // ---------- 刷新调度 ----------

    private static int EffectiveIntervalSeconds(AppPreferences prefs, bool eventStreamRunning)
    {
        var baseInterval = Math.Max(1, prefs.RefreshIntervalSeconds);

        // 事件流通了 → 轮询只做兜底：放宽到不低于 30 秒
        return prefs.UseEventStream && eventStreamRunning
            ? Math.Max(EventStreamFallbackSeconds, baseInterval)
            : baseInterval;
    }

    private void ApplyEffectiveInterval(AppPreferences prefs)
    {
        var effective = EffectiveIntervalSeconds(prefs, _eventStream.IsRunning);

        if (Math.Abs(_refreshTimer.Interval.TotalSeconds - effective) > 0.01)
        {
            _refreshTimer.Interval = TimeSpan.FromSeconds(effective);
        }

        UpdateRefreshModeText(prefs, effective);
    }

    private void UpdateRefreshModeText(AppPreferences prefs, int effectiveSeconds)
    {
        if (!prefs.UseEventStream)
        {
            StatusRefreshModeText.Text = $"{effectiveSeconds}s";
            return;
        }

        // 按真实状态分档：以前只判断 IsRunning，于是"订阅已建立但暂无事件"和"压根连不上"
        // 都显示成"连接中"。首次订阅回补 6 小时，这段时间内没有事件是常见情况，
        // 界面这么写会让用户以为事件流坏了。
        StatusRefreshModeText.Text = _eventStream.State switch
        {
            EventStreamState.Running => $"事件流（兜底 {effectiveSeconds}s）",
            EventStreamState.Subscribed => "事件流已订阅（等待事件）",
            EventStreamState.Connecting => "事件流连接中…",
            EventStreamState.Reconnecting => "事件流重连中…",
            _ => $"{effectiveSeconds}s"
        };
    }

    private async void RefreshTimer_Tick(object? sender, object e)
    {
        var prefs = _prefsService.GetPreferences();
        ApplyEffectiveInterval(prefs);

        if (!prefs.AutoRefresh) return;

        // 后台轮询：不显示"刷新中"，静默更新（§7.3-4 刷新不打扰）
        await RefreshCurrentPageAsync(userInitiated: false);
        await UpdateStatusBarAsync(userInitiated: false);
    }

    private void OnEngineEvent(object? sender, WslcEvent evt)
    {
        // 事件在后台线程抛出：先切回 UI 线程再碰控件
        DispatcherQueue?.TryEnqueue(() =>
        {
            StatusEventText.Text = $"{evt.Time:HH:mm:ss} · {evt.Summary}";

            // 合并短时间内的连续事件
            _eventDebounceTimer.Stop();
            _eventDebounceTimer.Start();
        });
    }

    private void OnEventStreamStatusChanged(object? sender, EventStreamStatus status)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            var prefs = _prefsService.GetPreferences();
            ApplyEffectiveInterval(prefs);

            // "已断开/未能订阅"这类话必须让用户看见，否则会以为面板还在实时同步
            if (!string.IsNullOrWhiteSpace(status.Message))
            {
                StatusEventText.Text = status.Message!;
            }
        });
    }

    private async void EventDebounceTimer_Tick(object? sender, object e)
    {
        _eventDebounceTimer.Stop();

        // 与轮询同一条纪律：关了自动刷新，事件也不该偷偷刷
        if (!_prefsService.GetPreferences().AutoRefresh) return;

        await RefreshCurrentPageAsync(userInitiated: false);
        await UpdateStatusBarAsync(userInitiated: false);
    }

    private async void GlobalRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshCurrentPageAsync(userInitiated: true);
        await UpdateStatusBarAsync(userInitiated: true);
    }

    private async void RefreshAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RefreshCurrentPageAsync(userInitiated: true);
        await UpdateStatusBarAsync(userInitiated: true);
    }

    private async Task UpdateStatusBarAsync(bool userInitiated)
    {
        try
        {
            var info = await _client.GetSystemInfoAsync();

            StatusWslVersionText.Text = string.IsNullOrWhiteSpace(info.WslVersion) ? "未检测到" : info.WslVersion;
            StatusSessionText.Text = info.ActiveSessions.Count.ToString();

            if (userInitiated || !_engineCheckDone)
            {
                StatusLastUpdateText.Text = $"更新于 {DateTime.Now:HH:mm:ss}";
            }

            UpdateEngineInfoBar(info);
        }
        catch
        {
            // 后台状态刷新失败不打断界面
        }
        finally
        {
            _engineCheckDone = true;
        }
    }

    /// <summary>
    /// 环境检测引导（P0-1）：组件缺失 / 引擎缺失 / 版本过低，三种降级路径都给可读解释与可执行下一步。
    /// </summary>
    private void UpdateEngineInfoBar(SystemInfoModel info)
    {
        if (info.MissingComponents.Count > 0)
        {
            ShowEngineInfoBar(
                "运行环境未就绪",
                $"缺少 WSL 容器组件：{string.Join("、", info.MissingComponents)}。请在终端执行 “{UpgradeCommand}” 后重启面板。",
                InfoBarSeverity.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(info.WslVersion))
        {
            ShowEngineInfoBar(
                "未检测到 WSL 引擎",
                $"本机未安装或无法访问 WSL。请以管理员身份执行 “{UpgradeCommand}”；若企业策略禁用了 WSL 容器，请联系管理员。",
                InfoBarSeverity.Error);
            return;
        }

        if (!IsVersionAtLeast(info.WslVersion, 3, 0, 1))
        {
            ShowEngineInfoBar(
                "WSL 版本过低",
                $"当前引擎版本 {info.WslVersion}，容器功能需要 3.0.1 及以上。请执行 “{UpgradeCommand}” 升级。",
                InfoBarSeverity.Warning);
            return;
        }

        EngineInfoBar.IsOpen = false;
    }

    private void ShowEngineInfoBar(string title, string message, InfoBarSeverity severity)
    {
        if (EngineInfoBar.IsOpen && EngineInfoBar.Title == title && EngineInfoBar.Message == message)
        {
            return;
        }

        EngineInfoBar.Severity = severity;
        EngineInfoBar.Title = title;
        EngineInfoBar.Message = message;

        if (EngineInfoBar.ActionButton is null)
        {
            var copyButton = new Button { Content = "复制升级命令" };
            copyButton.Click += (_, _) =>
            {
                TerminalLauncher.CopyToClipboard(UpgradeCommand);
                StatusLastUpdateText.Text = "升级命令已复制到剪贴板";
            };
            EngineInfoBar.ActionButton = copyButton;
        }

        EngineInfoBar.IsOpen = true;
    }

    private static bool IsVersionAtLeast(string version, int major, int minor, int patch)
    {
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var nums = new int[3];
        for (var i = 0; i < 3 && i < parts.Length; i++)
        {
            _ = int.TryParse(parts[i], out nums[i]);
        }

        if (nums[0] != major) return nums[0] > major;
        if (nums[1] != minor) return nums[1] > minor;
        return nums[2] >= patch;
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        if (NavFrame.CanGoBack)
        {
            NavFrame.GoBack();
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavigateTo(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            var pageType = PageTypeForTag(tag);
            if (pageType != null) NavigateTo(pageType);
        }
    }

    private void NavigateTo(Type pageType)
    {
        if (NavFrame.CurrentSourcePageType == pageType) return;

        NavFrame.Navigate(pageType);

        // 不在这里再刷一次：新页的 OnNavigatedTo 会自己拉数据。
        // 双刷新会让两个 RefreshAsync 并发交错（选中项错乱、CLI 进程翻倍）
    }
}
