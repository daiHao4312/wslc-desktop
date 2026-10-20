namespace WslcDesktop.Core.Models;

/// <summary>
/// 官方 C# API 通道（<c>Microsoft.WSL.Containers</c>）的能力探测结果。
///
/// 存在的理由：官方投影仍在预览期，且它<b>不是</b> CLI 的超集——
/// 对象模型比 CLI 的 JSON 更薄（<c>Container</c> 没有名称/端口，<c>ImageInfo</c> 没有多仓库引用）。
/// 所以"API 优先"必须逐项决策，而不是整通道替换；这份结果就是逐项决策的依据。
/// </summary>
public record WslcApiCapabilities
{
    /// <summary>用户是否允许使用 API 通道（设置项 <c>UseApiChannel</c>）。false 时其余字段均无意义。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>官方 SDK 可查询（<c>WslcService</c> 未抛异常）。</summary>
    public bool SdkAvailable { get; init; }

    /// <summary>会话管理器版本（如 <c>3.0.3</c>）；取不到为 null。</summary>
    public string? SessionManagerVersion { get; init; }

    /// <summary>缺失的组件（非空说明引擎装不全，面板应引导修复）。</summary>
    public IReadOnlyList<string> MissingComponents { get; init; } = Array.Empty<string>();

    /// <summary>已附着的引擎会话名；null 表示没附着上（此时<b>不会</b>新建会话）。</summary>
    public string? AttachedSessionName { get; init; }

    /// <summary>
    /// 是否是**只读附着**（会话归 CLI 进程所有，我们没有调用 <c>Start()</c>）。
    /// true 时 API 能做读操作，但不保证能创建/启动会话级对象。
    /// </summary>
    public bool AttachedReadOnly { get; init; }

    /// <summary>降级原因。写给日志与关于页，不作为错误弹窗打扰用户。</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// 镜像拉取能拿到真实字节进度——这是 API 独有能力：
    /// 实测 CLI 只回状态关键字（<c>Pulling</c>/<c>Downloading</c>/<c>Extracting</c>），不返回字节数。
    /// </summary>
    public bool CanPullWithProgress => Enabled && SdkAvailable && AttachedSessionName is not null;

    /// <summary>能用私有仓库凭据（同样只在 API 通道上成立）。</summary>
    public bool CanUseRegistryAuth => CanPullWithProgress;

    /// <summary>日志/关于页用的一行摘要。</summary>
    public string Summary => !Enabled
        ? "已关闭（设置项 UseApiChannel = false）"
        : CanPullWithProgress
            ? $"可用（会话 {AttachedSessionName}，会话管理器 {SessionManagerVersion ?? "未知"}）"
            : $"不可用：{Reason ?? "原因未知"}";
}
