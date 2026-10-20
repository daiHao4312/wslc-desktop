using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Services;
using WslcDesktop.Helpers;

namespace WslcDesktop;

/// <summary>
/// 应用入口：搭建服务容器（DI）并向页面暴露 <see cref="Services"/>。
/// </summary>
public partial class App : Application
{
    private static MainWindow? _mainWindow;
    private static SingleInstanceGate? _instanceGate;

    /// <summary>本进程是"后开的第二份"，只负责唤醒主实例然后退出。</summary>
    private static bool _isSecondaryInstance;

    /// <summary>全局服务容器。页面统一通过 App.Services.GetRequiredService&lt;T&gt;() 解析依赖。</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>当前主窗口（主题切换等全局操作需要）。</summary>
    public static MainWindow? MainWindow => _mainWindow;

    public App()
    {
        // ---- 在线更新的辅助进程必须在这里分流（M13）----
        // 它本质上是"第二个实例"（被主进程拉起，负责等旧进程退出后替换 exe），
        // 一旦走到下面的单实例门闸就会被判成后开的那份而自杀，所以判定必须抢在最前面。
        // 参数里没有协议开关时立刻返回 false，正常启动路径零影响。
        if (UpdateHelper.TryRunAsHelper(Environment.GetCommandLineArgs().Skip(1).ToArray()))
        {
            return; // 理论上到不了：TryRunAsHelper 内部已 Environment.Exit
        }

        InitializeComponent();

        // 单实例门闸要在建窗口之前判定：多开意味着多个 wslc events 子进程、
        // 多个托盘图标，且退出时互相抢引擎会话。
        _instanceGate = new SingleInstanceGate();
        if (!_instanceGate.TryAcquire())
        {
            _isSecondaryInstance = true;
            _instanceGate.SignalPrimary();
            return;
        }

        Services = ConfigureServices();

        // 启动/运行期异常必须可见：写日志 + 弹提示，而不是让窗口静默消失
        UnhandledException += OnUnhandledException;
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        var logPath = WriteErrorLog(e);

        try
        {
            if (_mainWindow?.Content is Microsoft.UI.Xaml.FrameworkElement root)
            {
                _ = new Microsoft.UI.Xaml.Controls.ContentDialog
                {
                    Title = "发生未处理的错误",
                    Content = new Microsoft.UI.Xaml.Controls.TextBlock
                    {
                        Text = $"{e.Message}\n\n详细信息已写入：\n{logPath}",
                        TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                        IsTextSelectionEnabled = true
                    },
                    CloseButtonText = "关闭",
                    XamlRoot = root.XamlRoot
                }.ShowAsync();
            }
        }
        catch
        {
            // 提示失败也不能再抛异常
        }

        // 面板是工具型应用：单个页面出错不应让整个进程退出
        e.Handled = true;
    }

    private static string WriteErrorLog(Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WslcDesktop");
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, "error.log");
            var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Message}{Environment.NewLine}{e.Exception}{Environment.NewLine}{new string('-', 60)}{Environment.NewLine}";
            File.AppendAllText(path, text);

            return path;
        }
        catch
        {
            return "(日志写入失败)";
        }
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // 数据通道：CLI 是主力（列表类查询信息比 API 更全），官方 API 只补它拿不到的能力
        services.AddSingleton<CliWslcClient>();
        // 官方 API 通道（M14）：能力探测 + 会话附着 + 真实字节进度拉取。
        // 探测不通过（引擎缺失 / 组件不全 / 找不到可附着会话）会自动回落 CLI，不会打断主流程。
        services.AddSingleton<IWslcApiChannel>(sp =>
        {
            var cli = sp.GetRequiredService<CliWslcClient>();
            var prefs = sp.GetRequiredService<IPrefsService>();
            return new WslcApiChannel(
                sessionProvider: cancellationToken => cli.GetSessionsAsync(cancellationToken),
                isEnabled: () => prefs.GetPreferences().UseApiChannel);
        });
        services.AddSingleton<IWslcClient>(sp => new CompositeWslcClient(
            sp.GetRequiredService<CliWslcClient>(),
            sp.GetService<IWslcApiChannel>()));
        // 镜像仓库（Registry V2）查询：tag 列表与下载体积。官方 API 与 wslc 都不提供，只能直接问 registry
        services.AddSingleton<IRegistryClient, CurlRegistryClient>();
        // 引擎组件安装/修复（M20）：官方 WslcService 静态能力，不依赖会话
        services.AddSingleton<IEnvironmentRepairService, WslcEnvRepair>();

        // 镜像源与凭据（M16c）：Store 只管持久化与加解密，Service 管业务规则（默认唯一、内置不可删…）
        services.AddSingleton<RegistrySourceStore>();
        services.AddSingleton<RegistrySourceService>();
        // 「镜像源 → 令牌」的唯一入口：凭据来自设置页，换令牌走官方 API 通道。
        // 通道在设置里被关掉时它仍会被解析出来，只是每次换令牌都会如实报"通道不可用"。
        services.AddSingleton(sp => new RegistryAuthService(
            sp.GetRequiredService<RegistrySourceStore>(),
            sp.GetService<IWslcApiChannel>()));
        // 发行版通道（wsl.exe + 注册表 Lxss），与容器通道相互独立
        services.AddSingleton<IDistroService, DistroService>();
        // 面板自身偏好（%LOCALAPPDATA%\WslcDesktop\prefs.json），与 wslc 的 settings.yaml 隔离
        services.AddSingleton<IPrefsService, PreferencesService>();
        // wslc 引擎配置（%LOCALAPPDATA%\wslc\settings.yaml）读写 + 备份
        services.AddSingleton<IWslcSettingsService, WslcSettingsService>();
        // 引擎事件流长驻订阅（断线重连 / 续接去重），供"操作后即时刷新"用
        services.AddSingleton<IEventStreamService, EventStreamService>();
        // 应用自身更新检查（GitHub Releases），与容器数据通道无关
        services.AddSingleton<IUpdateChecker, UpdateChecker>();
        // 更新包下载（M13 在线更新）：与检查分离，检查失败不影响已有下载结果
        services.AddSingleton<IUpdateDownloader, UpdateDownloader>();

        return services.BuildServiceProvider();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // 后开的一份不建窗口：唤醒主实例后立刻退出，避免出现第二个托盘图标
        if (_isSecondaryInstance)
        {
            // 释放门闸句柄再退：次要实例也创建了 Mutex/EventWaitHandle
            _instanceGate?.Dispose();
            _instanceGate = null;
            Exit();
            return;
        }

        // 清理上一轮在线更新留下的残骸（已用过的包、半成品）。
        // 放后台：扫目录+删文件是纯 IO，不该拖慢窗口出现
        _ = Task.Run(UpdateHelper.CleanUpAfterUpdate);

        _mainWindow = new MainWindow();

        // 事件流的启停由主窗口按偏好决定（订阅必须先于 Start，否则回补的历史事件会落空）；
        // 这里只负责窗口真正关闭时收掉那条长驻子进程，别留孤儿进程。
        _mainWindow.Closed += (_, _) =>
        {
            // 事件流 Stop 内部会同步等待后台循环退出（最长 2s）：放在 UI 线程的 Closed 里
            // 会让"退出"操作卡住。挪到线程池收尾，窗口先消失
            _ = Task.Run(() =>
            {
                var eventStream = Services.GetService<IEventStreamService>();
                eventStream?.Stop();
                (eventStream as IDisposable)?.Dispose();
            });

            _mainWindow?.DisposeTray();
            _instanceGate?.Dispose();
        };

        // 托盘/第二个实例要求"唤到前台"时统一走这里（回调在后台线程）
        _instanceGate?.StartListening(() => _mainWindow?.BringToFront());

        _mainWindow.Activate();
    }

    /// <summary>转发主题切换请求到当前主窗口。</summary>
    public static void ApplyTheme(string themeStr) => _mainWindow?.ApplyTheme(themeStr);
}
