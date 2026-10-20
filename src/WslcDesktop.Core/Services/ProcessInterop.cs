using System.Diagnostics;
using WslcDesktop.Core.Abstractions;

namespace WslcDesktop.Core.Services;

/// <summary>基于 <see cref="Process"/> 的等待实现（对应 <see cref="IProcessWaiter"/>）。</summary>
public sealed class SystemProcessWaiter : IProcessWaiter
{
    /// <summary>轮询粒度：ExitCode 拿不到时（跨会话/权限不足）退化成轮询探活。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    public bool WaitForExit(int processId, string? expectedProcessName, TimeSpan timeout)
    {
        Process? process;

        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            // 进程已经退出了（最常见的正常路径）
            return true;
        }
        catch (Exception)
        {
            // 权限不足等：拿不到句柄就没法等，按"已退出"处理会掩盖问题，
            // 所以这里返回 false，让上层走"等待超时"这条安全分支（不动文件）
            return false;
        }

        try
        {
            if (!NameMatches(process, expectedProcessName))
            {
                // PID 被复用给了别的进程 → 我们要等的那个早就没了
                return true;
            }

            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (process.HasExited) return true;
                }
                catch
                {
                    return true; // 对象失效通常意味着进程已消失
                }

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;

                Thread.Sleep(remaining < PollInterval ? remaining : PollInterval);
            }

            try
            {
                return process.HasExited;
            }
            catch
            {
                return true;
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private static bool NameMatches(Process process, string? expectedName)
    {
        if (string.IsNullOrWhiteSpace(expectedName)) return true;

        try
        {
            return string.Equals(process.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // 读不到名字（权限/已退出）时不因此否决，交给后面 HasExited 判定
            return true;
        }
    }
}

/// <summary>基于 <see cref="Process.Start(ProcessStartInfo)"/> 的启动实现。</summary>
public sealed class SystemProcessLauncher : IProcessLauncher
{
    /// <summary>
    /// 启动后的观察窗口：进程在这段时间内就退出，判定为"起不来"。
    ///
    /// 为什么不能只看 <c>Process.Start</c> 有没有抛异常：那个调用对"能被 CreateProcess 接受"
    /// 的文件都算成功——缺依赖、被杀软拦下、文件损坏的进程都是"启动成功然后立刻崩"。
    /// 实测踩过：把一份 PE 换成 <c>.dat</c> 后缀照样会被拉起来。
    /// 更新场景下这个差别是致命的：误判成成功就不会回滚，用户手里只剩一个起不来的程序。
    ///
    /// 单文件发布首次启动要自解压约 3 秒，但进程一直是活的（HasExited 为 false），
    /// 不会被这 1.5 秒误伤。
    /// </summary>
    private static readonly TimeSpan StartupProbe = TimeSpan.FromSeconds(1.5);

    public bool Start(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath)) return false;

        try
        {
            // UseShellExecute=false：不经过 Shell，避免被文件关联/策略拦截；
            // 目标本就是 exe，直接 CreateProcess 最省事。
            using var started = Process.Start(new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty
            });

            if (started is null) return false;

            try
            {
                // WaitForExit(毫秒) 返回 true 表示"已经退出了"，即启动失败
                return !started.WaitForExit((int)StartupProbe.TotalMilliseconds);
            }
            catch (Exception)
            {
                // 拿不到退出状态时不要否决一次已经发生的启动
                return true;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }
}
