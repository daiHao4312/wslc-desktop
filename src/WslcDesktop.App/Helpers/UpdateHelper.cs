using WslcDesktop.Core.Models;
using WslcDesktop.Core.Services;

namespace WslcDesktop.Helpers;

/// <summary>
/// 在线更新的"辅助进程"宿主（M13）。
///
/// 工作方式：主进程把下载好的新版 exe 直接启动起来（它就在更新包落点里），
/// 命令行带上 <c>--apply-update &lt;旧PID&gt; &lt;目标路径&gt;</c>；这个新进程在
/// <see cref="App"/> 的构造函数里**第一时间**分流到下面的逻辑，做完替换再把自己拉起一次。
///
/// 为什么分流必须早于单实例门闸：辅助进程本质上是第二个实例，
/// 一旦走到 <see cref="SingleInstanceGate"/>，它会被判成"后开的那份"然后自杀。
///
/// 为什么要有辅助进程：Windows 不允许覆盖正在运行的 exe，
/// 而"等旧进程退出"这件事不可能由即将退出的旧进程自己完成。
/// </summary>
internal static class UpdateHelper
{
    /// <summary>被辅助进程打过标记的更新包后缀（"这只包已经用过了"）。</summary>
    private const string CleanupSuffix = ".cleanup";

    /// <summary>
    /// 判断本进程是否为辅助进程并完成更新。
    ///
    /// 返回 true 表示"已经处理过了，调用方必须立刻结束进程"（内部已调用
    /// <see cref="Environment.Exit(int)"/>，正常不会返回）。
    /// </summary>
    public static bool TryRunAsHelper(string[] args)
    {
        if (!UpdateHelperArguments.TryParse(args, out var parsed)) return false;

        var exitCode = RunHelper(parsed);
        Environment.Exit(exitCode);

        return true;
    }

    private static int RunHelper(UpdateHelperArguments parsed)
    {
        var log = CreateLogger();

        try
        {
            // 正在运行的这个文件就是"新版 exe"（更新包本体），把它复制到目标位置
            var staging = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(staging))
            {
                log("辅助进程无法确定自身路径，放弃更新。");
                return 1;
            }

            log($"辅助进程启动：staging={staging} → target={parsed.TargetPath}（等待 pid={parsed.OldProcessId}）");

            // 用目标文件名反推旧进程名，避免 PID 被系统回收后等错进程
            var expectedName = Path.GetFileNameWithoutExtension(parsed.TargetPath);

            var applier = new UpdateApplier(
                new SystemProcessWaiter(),
                new SystemProcessLauncher(),
                log);

            var result = applier.Apply(
                staging, parsed.TargetPath, parsed.OldProcessId, expectedName,
                UpdateHelperArguments.DefaultWaitTimeout);

            log($"替换结果：{result.Status}（{result.Message}）");

            return result.IsSuccess ? 0 : 1;
        }
        catch (Exception ex)
        {
            // 辅助进程没有界面，异常只能落到日志里
            log($"辅助进程异常：{ex}");
            return 1;
        }
        finally
        {
            // 把更新包文件改名成 .cleanup：下次正常启动时清掉，
            // 顺带避免它被误认为"一个可用的更新包"
            TryMarkSelfForCleanup(log);
        }
    }

    /// <summary>
    /// 把当前进程所在的更新包文件改名，交给下一次启动清理。
    ///
    /// 允许改名正在运行的 exe（Windows 只禁止删除/覆盖），所以这里能成功；
    /// 删除自己则要等进程退出，因此留给下次启动。
    /// </summary>
    private static void TryMarkSelfForCleanup(Action<string> log)
    {
        try
        {
            var self = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(self) || !File.Exists(self)) return;

            var updatesDirectory = UpdateLocations.UpdatesDirectory();
            if (!Path.GetFullPath(self).StartsWith(Path.GetFullPath(updatesDirectory), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (self.EndsWith(CleanupSuffix, StringComparison.OrdinalIgnoreCase)) return;

            File.Move(self, self + CleanupSuffix, overwrite: true);
            log($"已把更新包标记为待清理：{self}{CleanupSuffix}");
        }
        catch (Exception ex)
        {
            // 标记失败只是留个几百 MB 的包，不该影响更新结果
            log($"标记待清理失败（可忽略）：{ex.Message}");
        }
    }

    /// <summary>
    /// 主实例正常启动时调用：清掉上一轮更新留下的残骸。
    ///
    /// 保留规则：更新目录里**最近写入的一个包**不动——那可能是用户刚下好、
    /// 选了「稍后安装」的那只包，删了就得重下 96 MB。
    /// 其余全部清掉：被辅助进程打过 <c>.cleanup</c> 标记的（已用过）、更早的旧包、半成品。
    ///
    /// **不碰目标 exe 旁边的 <c>.old</c> 备份**：那是新版出问题时的唯一退路，
    /// 什么时候删由用户在关于页决定。
    /// </summary>
    public static void CleanUpAfterUpdate()
    {
        var log = CreateLogger();

        try
        {
            var directory = UpdateLocations.UpdatesDirectory();
            if (!Directory.Exists(directory)) return;

            var self = Environment.ProcessPath ?? string.Empty;
            var removed = 0;
            var keptPending = false;

            var files = Directory.EnumerateFiles(directory)
                .OrderByDescending(f => SafeLastWriteTime(f))
                .ToList();

            foreach (var file in files)
            {
                // 万一程序本身就跑在更新目录里（辅助进程重启后的那几毫秒），别把自己删了
                if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(self), StringComparison.OrdinalIgnoreCase))
                {
                    keptPending = true;
                    continue;
                }

                var isMarked = file.EndsWith(CleanupSuffix, StringComparison.OrdinalIgnoreCase);

                if (!isMarked && !keptPending)
                {
                    // 最新的那只包留着（"稍后安装"要用的）
                    keptPending = true;
                    continue;
                }

                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch
                {
                    // 辅助进程可能还没退干净，留到下次启动
                }
            }

            if (removed > 0) log($"已清理 {removed} 个更新残留文件。");
        }
        catch (Exception ex)
        {
            log($"清理更新残留失败（可忽略）：{ex.Message}");
        }
    }

    /// <summary>把一条说明写进更新日志（界面层记录更新相关失败时用）。</summary>
    public static void Log(string message) => CreateLogger()(message);

    private static DateTime SafeLastWriteTime(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>更新日志写入 <c>%LOCALAPPDATA%\WslcDesktop\update.log</c>；写不进去也不能影响更新本身。</summary>
    private static Action<string> CreateLogger()
    {
        var path = UpdateLocations.LogPath();

        return message =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch
            {
                // 日志失败静默
            }
        };
    }
}
