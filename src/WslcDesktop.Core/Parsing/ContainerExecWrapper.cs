using System.Text;

namespace WslcDesktop.Core.Parsing;

/// <summary>
/// 「可停止的容器内命令」的命令行包装（**CLI 通道专用**，M19 修订）。
///
/// 为什么不能用 API 的 <c>Process.Signal</c>：官方 API 的会话是**机器级唯一、进程独占**的
/// （<c>0x80040607 = SessionReserved</c>）。CLI 的容器都在 <c>wslc-cli-*</c> 会话里、由
/// <c>wslc.exe</c> 进程拥有，API 只能新建自己的会话 —— 看不到那些容器，也就没法给它们发信号。
///
/// 所以"停止"改成纯 CLI 实现：
/// <list type="number">
/// <item>把用户命令 **base64** 后塞进包装脚本，解码后用 <c>sh</c> 执行（base64 不含引号，
/// 彻底绕开多层引号转义的坑）；</item>
/// <item>后台启动并把它自己的 PID 写进容器内的 pidfile，再 <c>wait</c> 保持前台输出；</item>
/// <item>「停止」= 再起一条 <c>kill</c> 命令按 pidfile 发 TERM，200 ms 后补 KILL。</item>
/// </list>
///
/// ⚠️ 已知限制：复合命令（如 <c>a; b</c>）里被拉起的**子进程**可能不随父进程一起退出 ——
/// 常见用法（<c>sleep</c> / <c>tail -f</c> / 单个脚本）是准的。要百分百精确只能等引擎放开 API 会话附着。
/// </summary>
public static class ContainerExecWrapper
{
    /// <summary>容器内的 pidfile（一次只允许一个前台命令，所以用固定名）。</summary>
    public const string PidFile = "/tmp/.wslcpanel-exec.pid";

    /// <summary>
    /// 包装脚本：解码 → 后台执行 → 记 PID → 前台 wait（保持输出与退出码）。
    /// </summary>
    public static string BuildTrackedScript(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));

        // base64 只含 A-Za-z0-9+/=，可以安全地放进单引号里
        return $"echo '{payload}' | base64 -d | sh & echo $! > {PidFile}; wait";
    }

    /// <summary>停止脚本：按 pidfile 先 TERM，200 ms 后补 KILL，最后别把 pidfile 当错误源。</summary>
    public static string BuildStopScript()
        => $"p=$(cat {PidFile} 2>/dev/null); " +
           "[ -n \"$p\" ] || exit 0; " +
           "kill -TERM $p 2>/dev/null; sleep 0.2; " +
           "kill -KILL $p 2>/dev/null; true";

    /// <summary>完整的 <c>wslc exec</c> 参数（可停止版）。</summary>
    public static string BuildTrackedExecArguments(string idOrName, string command)
        => WslcCommandBuilder.BuildExecArguments(idOrName, BuildTrackedScript(command));

    /// <summary>完整的 <c>wslc exec</c> 参数（停止）。</summary>
    public static string BuildStopArguments(string idOrName)
        => WslcCommandBuilder.BuildExecArguments(idOrName, BuildStopScript());

    /// <summary>把 base64 解回原命令 —— 测试用，也用来在日志里复核包装没改坏命令。</summary>
    public static string DecodeTrackedScript(string script)
    {
        const string prefix = "echo '";
        var start = script.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0) return string.Empty;

        start += prefix.Length;
        var end = script.IndexOf('\'', start);
        if (end < 0) return string.Empty;

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(script[start..end]));
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }
}
