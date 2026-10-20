namespace WslcDesktop.Core.Models;

public record AppPreferences
{
    public string Theme { get; init; } = "Default"; // "Default", "Light", "Dark"
    public int RefreshIntervalSeconds { get; init; } = 3;
    public bool AutoRefresh { get; init; } = true;
    public string TerminalCommand { get; init; } = "wt";

    /// <summary>
    /// 拉取镜像时，用户没写 registry 前缀所用的站点。
    ///
    /// 默认取 <see cref="RegistryDefaults.Host"/>（实测可用的加速站）。
    /// **旧版本的默认值 <c>docker.io</c> 在国内网络下直连必然超时**，
    /// 而且这个键以前只是"存起来没人读"——现在真的被拉取路径消费了。
    /// </summary>
    public string DefaultRegistry { get; init; } = RegistryDefaults.Host;

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

    /// <summary>
    /// 允许官方 C# API 通道接管"它比 CLI 更强"的那部分能力。
    ///
    /// 当前只用于镜像拉取：API 给得出真实字节数（CLI 只回状态关键字），
    /// 进度条因此能从不确定态变成确定态；同时也带来私有仓库凭据的入口。
    ///
    /// 默认开启，但**探测不通过时会自动回落 CLI**（引擎缺失、组件不全、
    /// 找不到可附着的会话都会回落），所以关掉它只是"明确不用 API"，不是"修故障"。
    /// 官方投影仍在预览期，这个开关是留给出问题时的一键退路。
    /// </summary>
    public bool UseApiChannel { get; init; } = true;

    /// <summary>
    /// 镜像源的个人设置（**唯一的一份真相**）：自定义源、被停用的内置源都在这里。
    ///
    /// 内置站点本身仍是代码里的常量（`MirrorPresets.Sites`），这里只存"与默认不同的部分"，
    /// 所以新增内置站点时老用户的列表会自动补上，不需要迁移。
    /// 界面看到的统一视图由 <c>RegistrySourceService</c> 合成，不要直接读这个字段。
    /// </summary>
    public IReadOnlyList<RegistrySourcePreference> RegistrySources { get; init; } = Array.Empty<RegistrySourcePreference>();

    /// <summary>
    /// ⚠️ 旧版本字段（v1.1.1 之前）：自定义镜像源的主机名列表。
    ///
    /// 只用于**读取旧 prefs.json 时的一次性兼容**（见 <c>RegistrySourceStore</c>），
    /// 任何一次写入都会把它清空并转成 <see cref="RegistrySources"/>。新代码不要读它。
    /// </summary>
    public IReadOnlyList<string> CustomRegistries { get; init; } = Array.Empty<string>();

    /// <summary>⚠️ 旧版本字段：被移除的内置站点。同上，写入时清空。</summary>
    public IReadOnlyList<string> HiddenRegistries { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 私有镜像源的登录凭据；**密码是 DPAPI 密文**（见 <see cref="RegistryCredential"/>）。
    ///
    /// 放密文而不是明文是硬约束：prefs.json 是明文 JSON，
    /// 一旦写进明文密码，任何能读这个文件的进程（或同步盘）就都拿到了它。
    /// </summary>
    public IReadOnlyList<RegistryCredential> RegistryCredentials { get; init; } = Array.Empty<RegistryCredential>();
}
