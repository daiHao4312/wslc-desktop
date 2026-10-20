using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Abstractions;

/// <summary>
/// 引擎组件安装/修复（M20）。
///
/// 注意它**不依赖会话**：官方 <c>WslcService.InstallWithDependenciesAsync</c> 是静态能力，
/// 所以不受"M14 会话附着失败"的影响 —— 这也是这块先做的原因。
/// </summary>
public interface IEnvironmentRepairService
{
    /// <summary>
    /// 安装/修复缺失的引擎组件。组件齐全时直接返回 <see cref="EnvRepairResult.Skipped"/>。
    /// </summary>
    Task<EnvRepairResult> RepairAsync(
        Action<EnvRepairProgress>? onProgress = null,
        CancellationToken cancellationToken = default);
}
