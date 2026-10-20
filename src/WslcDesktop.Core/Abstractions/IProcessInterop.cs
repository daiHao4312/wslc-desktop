namespace WslcDesktop.Core.Abstractions;

/// <summary>
/// 等待某个进程退出（更新替换的前置条件）。
///
/// 抽成接口是为了让替换流程可以在单测里跑完：真实实现要等一个活进程，
/// 单测里换成"立刻返回已退出"的假实现，就能把备份/复制/回滚整条路径都验到。
/// </summary>
public interface IProcessWaiter
{
    /// <summary>
    /// 等 <paramref name="processId"/> 退出。
    ///
    /// 返回 true 表示"该进程已经不在了"（包括本来就找不到这个 PID 的情况）；
    /// false 表示等到超时它还在跑。
    ///
    /// <paramref name="expectedProcessName"/> 非空时用它核对 PID 归属（不带 .exe 后缀，不分大小写）：
    /// PID 会被系统回收复用，靠名字能避免"等错进程"而误判成已退出。
    /// </summary>
    bool WaitForExit(int processId, string? expectedProcessName, TimeSpan timeout);
}

/// <summary>启动一个已存在的可执行文件（替换完成后拉起新版）。</summary>
public interface IProcessLauncher
{
    /// <summary>
    /// 启动**并确认它活下来**。
    ///
    /// 返回 true 才算"新版本起来了"；文件缺失、被拒绝执行、启动后立刻崩溃都返回 false，
    /// 由调用方据此回滚。实现必须自己把异常收干净，不向外抛。
    /// </summary>
    bool Start(string executablePath);
}
