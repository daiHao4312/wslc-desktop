using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Abstractions;

/// <summary>
/// 镜像仓库（Registry V2）的只读查询。
///
/// 存在的理由：官方 API 与 <c>wslc</c> 都**不提供 tag 列表** ——
/// <c>wslc images</c> 只报本地已有的镜像。想要"这个仓库有哪些 tag"只能直接问 registry。
///
/// 用 <c>curl.exe</c> 而不是 <c>HttpClient</c>：与项目既有的"外部程序走
/// <see cref="IProcessRunner"/>"一致（可被单测替身接管），也顺带绕开
/// .NET 默认代理配置与 TLS 策略在不同机器上的差异。
/// </summary>
public interface IRegistryClient
{
    /// <summary>
    /// 列出一个仓库的 tag（返回时已按"用户会想点的顺序"排序）。
    ///
    /// <paramref name="bearerToken"/> 是私有仓库的身份令牌（用户在拉取对话框里填凭据换来的）；
    /// 给了就用它，不再去换匿名令牌。
    /// </summary>
    Task<RegistryTagsResult> GetTagsAsync(
        string host,
        string repository,
        string? bearerToken = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 量一个 tag 的下载体积（config + 各层压缩后之和）。
    /// <paramref name="architecture"/> 用 <c>amd64</c> / <c>arm64</c>，多架构 index 靠它挑那一份。
    /// </summary>
    Task<RegistrySizeResult> GetSizeAsync(
        string host,
        string repository,
        string reference,
        string architecture,
        string? bearerToken = null,
        CancellationToken cancellationToken = default);
}
