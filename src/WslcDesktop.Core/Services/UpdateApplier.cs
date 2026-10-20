using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Core.Services;

/// <summary>
/// 原地替换正在使用的 exe（M13 在线更新的"换"这一段）。
///
/// 执行顺序刻意设计成"先备份、再落地、最后校验启动"，任何一步失败都能退回去：
///
/// <code>
///   等旧进程退出 → 可写探测 → 旧版改名 .old → 新版复制到位 → 哈希复核 → 拉起新版
///                              ↑ 从这里开始才动文件        ↑ 不一致就回滚   ↑ 起不来就回滚
/// </code>
///
/// 三个"不动文件就放弃"的前置检查（<see cref="UpdateApplyStatus.WaitTimedOut"/> /
/// <see cref="UpdateApplyStatus.TargetNotWritable"/> / <see cref="UpdateApplyStatus.StagingMissing"/>）
/// 保证了：更新失败最常见的几种原因，都不会让用户的程序处于半残状态。
///
/// 依赖 <see cref="IProcessWaiter"/> / <see cref="IProcessLauncher"/> 注入，
/// 因此除了"真的等一个进程"和"真的拉起进程"之外，整条路径都能在单测里跑。
/// </summary>
public sealed class UpdateApplier
{
    /// <summary>
    /// 进程退出后到文件句柄真正释放之间的宽限期。
    /// 实测这一步省掉会偶发 Move 失败（进程已死但内核还没回收映像句柄）。
    /// </summary>
    private static readonly TimeSpan DefaultReleaseGrace = TimeSpan.FromMilliseconds(600);

    private const int MoveRetryCount = 6;
    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>旧版本备份的后缀。</summary>
    public const string BackupSuffix = ".old";

    private readonly IProcessWaiter _waiter;
    private readonly IProcessLauncher _launcher;
    private readonly Action<string>? _log;
    private readonly TimeSpan _releaseGrace;

    public UpdateApplier(
        IProcessWaiter waiter,
        IProcessLauncher launcher,
        Action<string>? log = null,
        TimeSpan? releaseGrace = null)
    {
        _waiter = waiter ?? throw new ArgumentNullException(nameof(waiter));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _log = log;
        _releaseGrace = releaseGrace ?? DefaultReleaseGrace;
    }

    /// <summary>
    /// 把 <paramref name="stagingPath"/>（新版 exe，通常就是辅助进程自己所在的文件）
    /// 替换到 <paramref name="targetPath"/>，然后拉起新版。
    /// </summary>
    /// <param name="expectedProcessName">用于核对 PID 归属的进程名（不带 .exe）；不校验传 null。</param>
    public UpdateApplyResult Apply(
        string stagingPath,
        string targetPath,
        int oldProcessId,
        string? expectedProcessName = null,
        TimeSpan? waitTimeout = null)
    {
        var wait = waitTimeout ?? UpdateHelperArguments.DefaultWaitTimeout;

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return UpdateApplyResult.Failure(UpdateApplyStatus.StagingMissing, "缺少目标路径参数。");
        }

        string fullTarget;
        try
        {
            fullTarget = Path.GetFullPath(targetPath);
        }
        catch (Exception ex)
        {
            return UpdateApplyResult.Failure(UpdateApplyStatus.Failed, $"目标路径不合法：{ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(stagingPath) || !File.Exists(stagingPath))
        {
            return UpdateApplyResult.Failure(
                UpdateApplyStatus.StagingMissing, "更新包文件不存在（可能被杀毒软件移除），本次未做任何改动。", fullTarget);
        }

        Log($"等待旧进程退出：pid={oldProcessId}，上限 {wait.TotalSeconds:0} 秒");

        if (!_waiter.WaitForExit(oldProcessId, expectedProcessName, wait))
        {
            return UpdateApplyResult.Failure(
                UpdateApplyStatus.WaitTimedOut,
                $"旧进程在 {wait.TotalSeconds:0} 秒内没有退出，已放弃更新（未改动任何文件）。",
                fullTarget);
        }

        if (_releaseGrace > TimeSpan.Zero) Thread.Sleep(_releaseGrace);

        var directory = Path.GetDirectoryName(fullTarget);
        if (directory is null || !DirectoryWriteProbe.CanWrite(directory))
        {
            return UpdateApplyResult.Failure(
                UpdateApplyStatus.TargetNotWritable,
                $"程序所在目录没有写入权限（{directory}）。请把程序移到用户可写目录后重试，"
                + "或改用「打开下载页」手动替换。",
                fullTarget);
        }

        // ---- 从这一步开始才真正动文件 ----

        var hadTarget = File.Exists(fullTarget);
        var backupPath = fullTarget + BackupSuffix;

        if (hadTarget && !CreateBackup(fullTarget, ref backupPath))
        {
            return UpdateApplyResult.Failure(
                UpdateApplyStatus.Failed,
                "无法备份当前程序（文件仍被占用或权限不足），未做任何替换。",
                fullTarget);
        }

        try
        {
            File.Copy(stagingPath, fullTarget, overwrite: true);
        }
        catch (Exception ex)
        {
            return Rollback(fullTarget, backupPath, hadTarget, $"写入新版本失败：{ex.Message}");
        }

        // 校验落地文件的哈希与更新包一致：复制被中断/被安全软件改写都能在这里拦住
        var stagingHash = AssetDigest.TryComputeFileSha256(stagingPath);
        var landedHash = AssetDigest.TryComputeFileSha256(fullTarget);

        if (stagingHash is null || landedHash is null || !string.Equals(stagingHash, landedHash, StringComparison.Ordinal))
        {
            return Rollback(fullTarget, backupPath, hadTarget, "替换后的文件与更新包不一致");
        }

        Log($"替换完成，拉起新版本：{fullTarget}");

        if (!_launcher.Start(fullTarget))
        {
            return Rollback(fullTarget, backupPath, hadTarget, "新版本已就位但启动失败");
        }

        Log($"更新完成：{fullTarget}");

        return new UpdateApplyResult
        {
            Status = UpdateApplyStatus.Applied,
            Message = "更新已应用，新版本已启动。",
            TargetPath = fullTarget,
            BackupPath = hadTarget ? backupPath : null,
            NewProcessStarted = true
        };
    }

    /// <summary>
    /// 把旧程序改名成 <c>.old</c>。
    /// 重名时优先覆盖已存在的旧备份；覆盖不掉（被占用）就换一个带时间戳的名字——
    /// 备份是"能救命"的东西，不能因为清理旧备份失败就放弃整次更新。
    /// </summary>
    private bool CreateBackup(string fullTarget, ref string backupPath)
    {
        if (File.Exists(backupPath) && !TryDelete(backupPath))
        {
            backupPath = $"{fullTarget}.{DateTime.Now:yyyyMMddHHmmss}{BackupSuffix}";
        }

        if (TryMove(fullTarget, backupPath))
        {
            Log($"已备份旧版本：{backupPath}");
            return true;
        }

        return false;
    }

    /// <summary>
    /// 回滚：把备份移回原位。原本就没有目标文件时直接删掉半成品。
    /// 回滚本身再失败就只能交给用户手工处理——这种情况必须如实说清楚路径。
    /// </summary>
    private UpdateApplyResult Rollback(string fullTarget, string backupPath, bool hadTarget, string reason)
    {
        if (!hadTarget)
        {
            TryDelete(fullTarget);

            return UpdateApplyResult.Failure(
                UpdateApplyStatus.Failed, $"{reason}，且原本没有可恢复的旧版本。", fullTarget);
        }

        if (!File.Exists(backupPath))
        {
            return UpdateApplyResult.Failure(
                UpdateApplyStatus.Failed, $"{reason}，且找不到备份文件，需要重新下载。", fullTarget);
        }

        TryDelete(fullTarget);

        if (TryMove(backupPath, fullTarget))
        {
            Log($"已回滚到替换前的版本：{fullTarget}");

            return UpdateApplyResult.Failure(
                UpdateApplyStatus.RolledBack, $"{reason}，已恢复为替换前的版本。", fullTarget) with
            {
                BackupPath = null
            };
        }

        return UpdateApplyResult.Failure(
            UpdateApplyStatus.Failed,
            $"{reason}，回滚也未能完成。请手工把 {backupPath} 改名为 {fullTarget}。",
            fullTarget) with { BackupPath = backupPath };
    }

    private bool TryMove(string source, string destination)
    {
        for (var attempt = 0; attempt < MoveRetryCount; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return true;
            }
            catch (IOException) when (attempt < MoveRetryCount - 1)
            {
                Thread.Sleep(MoveRetryDelay);
            }
            catch (UnauthorizedAccessException) when (attempt < MoveRetryCount - 1)
            {
                Thread.Sleep(MoveRetryDelay);
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void Log(string message) => _log?.Invoke(message);
}
