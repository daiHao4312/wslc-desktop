using Microsoft.WSL.Containers;
using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Core.Services;

/// <summary>
/// 引擎组件安装/修复：官方 <c>WslcService.InstallWithDependenciesAsync</c> 的唯一包装点。
///
/// 两个刻意的决定：
/// <list type="number">
/// <item><b>装完再查一次</b>：引擎可能"调用成功但没生效"，只有 <c>GetMissingComponents()</c> 变空才算成功 ——
/// 否则用户会以为修好了，下次启动又看到同样的警告。</item>
/// <item><b>进度如实转述</b>：<c>Progress/Total</c> 为 0 时界面显示不确定态，不编造百分比。</item>
/// </list>
/// </summary>
public sealed class WslcEnvRepair : IEnvironmentRepairService
{
    public async Task<EnvRepairResult> RepairAsync(
        Action<EnvRepairProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Component> missing;

        try
        {
            missing = WslcService.GetMissingComponents();
        }
        catch (Exception ex)
        {
            return EnvRepairResult.Failed($"无法检测组件：{Describe(ex)}");
        }

        if (missing.Count == 0)
        {
            return EnvRepairResult.NothingToDo("组件齐全，无需安装或修复。");
        }

        var components = missing.Select(component => component.ToString()).ToList();

        try
        {
            var options = new InstallOptions
            {
                Components = missing.ToList(),

                // Repair = true：让引擎在"已安装但损坏"的情况下也重装，而不是直接跳过
                Repair = true
            };

            var operation = WslcService.InstallWithDependenciesAsync(options);

            if (onProgress is not null)
            {
                operation.Progress = (_, progress) => onProgress(new EnvRepairProgress
                {
                    Component = progress.Component.ToString(),
                    Done = progress.Progress,
                    Total = progress.Total
                });
            }

            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    operation.Cancel();
                }
                catch
                {
                    // 操作已结束或投影不支持取消：交给 await 给出真实结果
                }
            });

            await operation.AsTask();
        }
        catch (OperationCanceledException)
        {
            return new EnvRepairResult { Success = false, Message = "已取消安装。", Repaired = components };
        }
        catch (Exception ex)
        {
            return new EnvRepairResult { Success = false, Message = $"安装失败：{Describe(ex)}", Repaired = components };
        }

        // 装完复查：只有缺失列表变空才算真的成功
        try
        {
            var remaining = WslcService.GetMissingComponents();
            if (remaining.Count == 0)
            {
                return new EnvRepairResult
                {
                    Success = true,
                    Message = "组件已安装完成，运行环境就绪。",
                    Repaired = components
                };
            }

            var names = string.Join("、", remaining.Select(c => EnvRepairPlanner.Describe(c.ToString())));
            return new EnvRepairResult
            {
                Success = false,
                Message = $"安装已结束，但仍有组件缺失：{names}。可能需要管理员权限或重启。",
                Repaired = components
            };
        }
        catch (Exception ex)
        {
            return new EnvRepairResult
            {
                Success = false,
                Message = $"安装已执行，但复查失败：{Describe(ex)}",
                Repaired = components
            };
        }
    }

    /// <summary>HRESULT 有官方短语就用短语（否则又是一句"无法找到与此错误代码关联的文本"）。</summary>
    private static string Describe(Exception exception)
        => ApiErrorCodes.Describe(exception.HResult) ?? exception.Message;
}
