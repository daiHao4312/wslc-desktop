using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Abstractions;

/// <summary>
/// 官方 API 通道：<b>只接管 CLI 拿不到的那部分能力</b>，其余一律留在 CLI。
///
/// 为什么不整体替换：API 的对象模型比 CLI 的 JSON 更薄——
/// <c>Container</c> 只有 Id/State/InitProcess（没有名称、没有端口映射），
/// <c>ImageInfo</c> 只有 Name/Sha256/Size/Created（没有多仓库引用聚合）。
/// 整体切过去会让列表页信息变少，是退步。当前唯一"API 严格更强"的能力是
/// <b>镜像拉取的真实字节进度</b>（外加它顺带带来的私有仓库凭据）。
/// </summary>
public interface IWslcApiChannel : IDisposable
{
    /// <summary>探测通道可用性（结果缓存；探测过程不产生副作用）。</summary>
    Task<WslcApiCapabilities> ProbeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 用官方 API 拉取镜像。
    ///
    /// 返回 <c>null</c> 表示<b>通道没接上、调用方可以安全回落 CLI</b>（尚未产生任何下载动作）；
    /// 返回非 null 表示 API 已经动过手，结果以它为准（哪怕失败），不再回落——
    /// 否则"下了一半失败 → 换 CLI 重下"会白白重下。
    /// </summary>
    Task<PullResult?> TryPullImageAsync(
        string imageUri,
        string? registryAuth,
        Action<PullProgress>? onProgress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 用用户名/密码向指定 registry 换一个身份令牌（私有仓库拉取用）。
    ///
    /// 需要通道可用：<c>Session.Authenticate</c> 只在附着到引擎会话之后才能调。
    /// 通道不可用时返回失败并说明原因，**不会**把凭据丢给别处去试。
    /// </summary>
    Task<RegistryCredentialResult> AuthenticateAsync(
        string serverAddress,
        string username,
        string password,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 在容器里起一个进程（M19：可实时输出、可 SIGTERM/SIGKILL）。
    ///
    /// 与 <see cref="TryPullImageAsync"/> 不同，这里**不区分"没开始"与"下一半"**：
    /// 起不来就是没起来（返回带原因的失败），界面会回落成一次性 <c>wslc exec</c> 并说明差别。
    /// </summary>
    Task<ApiProcessStartResult> TryStartProcessAsync(
        string containerIdOrName,
        string command,
        Action<string> onOutputLine,
        CancellationToken cancellationToken = default);
}
