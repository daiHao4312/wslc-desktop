using System.Globalization;

namespace WslcDesktop.Core.Models;

/// <summary>
/// 更新辅助进程的命令行协议（M13）。
///
/// 为什么需要辅助进程：Windows 上正在运行的 exe 无法被覆盖，而"等待旧进程退出"这件事
/// 只能由**另一个进程**来做。做法是主进程把新版 exe 直接起来（它自己就在更新包的落点里），
/// 带上本协议参数；新进程在 WinUI 初始化之前分流到更新逻辑，做完替换再把自己拉起一次。
///
/// 协议形如：<c>wslc-desktop.exe --apply-update &lt;旧进程PID&gt; "&lt;目标exe完整路径&gt;"</c>
///
/// 目标是**可往返解析**（单测覆盖）：主进程怎么拼，辅助进程就怎么解。
/// 真正的启动走 <c>ProcessStartInfo.ArgumentList</c>（由系统做转义），
/// 这里的字符串构建主要用于日志展示与测试。
/// </summary>
public sealed record UpdateHelperArguments
{
    /// <summary>分流开关。放在首参，保证一见即分流，不进入正常启动流程。</summary>
    public const string Switch = "--apply-update";

    /// <summary>等待旧进程退出的上限（超过就放弃，不动任何文件）。</summary>
    public static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>将要被替换掉的旧进程 PID。</summary>
    public int OldProcessId { get; init; }

    /// <summary>要被替换的 exe 完整路径（即用户双击的那个文件）。</summary>
    public string TargetPath { get; init; } = string.Empty;

    /// <summary>拼出用于启动/日志的命令行。</summary>
    public static string Build(int oldProcessId, string targetPath)
        => $"{Switch} {oldProcessId.ToString(CultureInfo.InvariantCulture)} \"{targetPath}\"";

    /// <summary>
    /// 从命令行参数里识别本协议。
    /// 不满足（没开关、参数不够、PID 不是数字、路径为空）时返回 false——
    /// **宁可当作普通启动，也不要误吞正常启动**。
    /// </summary>
    public static bool TryParse(IReadOnlyList<string>? args, out UpdateHelperArguments parsed)
    {
        parsed = new UpdateHelperArguments();

        if (args is null) return false;

        var index = -1;
        for (var i = 0; i < args.Count; i++)
        {
            // 只认命令行的首个非空参数位，避免把别处的同名文本当开关
            if (string.IsNullOrWhiteSpace(args[i])) continue;

            if (!string.Equals(args[i].Trim(), Switch, StringComparison.OrdinalIgnoreCase)) return false;

            index = i;
            break;
        }

        if (index < 0 || index + 2 >= args.Count) return false;

        var pidText = args[index + 1].Trim();
        if (!int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
        {
            return false;
        }

        var target = Unquote(args[index + 2]);
        if (target.Length == 0) return false;

        parsed = new UpdateHelperArguments { OldProcessId = pid, TargetPath = target };
        return true;
    }

    /// <summary>去掉可能残留的外层引号（命令行被手工粘贴时很常见）。</summary>
    private static string Unquote(string raw)
    {
        var text = raw.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            text = text[1..^1].Trim();
        }

        return text;
    }
}
