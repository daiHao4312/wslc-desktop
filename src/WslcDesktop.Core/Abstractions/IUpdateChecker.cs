using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Abstractions;

/// <summary>
/// 应用自身的更新检查（M12）。与容器数据通道无关，故独立于 IWslcClient。
/// </summary>
public interface IUpdateChecker
{
    /// <summary>
    /// 查 <paramref name="repo"/>（owner/repo）的最新 Release，与 <paramref name="currentVersion"/> 比较。
    /// 任何失败都返回 <see cref="UpdateCheckStatus"/> 里对应的降级结果，不抛异常。
    /// </summary>
    Task<UpdateCheckResult> CheckAsync(string? repo, string currentVersion, CancellationToken cancellationToken = default);
}
