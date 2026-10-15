namespace WslcDesktop.Core.Models;

public record AppPreferences
{
    public string Theme { get; init; } = "Default"; // "Default", "Light", "Dark"
    public int RefreshIntervalSeconds { get; init; } = 3;
    public bool AutoRefresh { get; init; } = true;
    public string TerminalCommand { get; init; } = "wt";
    public string DefaultRegistry { get; init; } = "docker.io";

    /// <summary>
    /// 优先用引擎事件流（<c>wslc events</c>）做即时刷新；关闭则退回纯轮询。
    ///
    /// 开启时轮询降级为兜底（间隔放宽到不低于 30 秒），因为主要刷新由事件驱动。
    /// 旧版本 prefs.json 没有这个键，反序列化后取默认值 true。
    /// </summary>
    public bool UseEventStream { get; init; } = true;

    /// <summary>
    /// 点关闭按钮时最小化到托盘而不是退出。
    ///
    /// 默认开启（面板带事件流与托盘，通常希望常驻）；
    /// 托盘不可用时会自动失效，避免出现"关不掉窗口"的死路。
    /// </summary>
    public bool CloseToTray { get; init; } = true;

    /// <summary>
    /// 更新检查用的 GitHub 仓库（<c>owner/repo</c>）；留空即关闭更新检查。
    ///
    /// 默认指向本项目仓库：面板只读它的 latest Release 标签与自身版本比较，
    /// 不在其中放任何用户数据。私有仓库/无 Release/仓库名写错都会在关于页给出可读提示。
    /// </summary>
    public string UpdateRepo { get; init; } = "daiHao4312/wslc-desktop";
}
