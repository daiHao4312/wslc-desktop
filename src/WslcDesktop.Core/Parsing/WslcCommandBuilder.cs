using System.Text;
using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Parsing;

/// <summary>
/// 把 <see cref="ContainerCreateOptions"/> 拼成 <c>wslc run</c> 参数串。
///
/// 这里同时服务两件事：① 执行；② 界面上的"等效命令预览"。
/// 两者必须用同一份实现，否则预览与真实执行会漂移。
/// </summary>
public static class WslcCommandBuilder
{
    public static string BuildRunArguments(ContainerCreateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var sb = new StringBuilder("run");

        if (options.Detach) sb.Append(" -d");
        if (options.RemoveOnExit) sb.Append(" --rm");

        if (!string.IsNullOrWhiteSpace(options.Name))
        {
            sb.Append(" --name ").Append(Quote(options.Name!.Trim()));
        }

        foreach (var port in options.Ports.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            sb.Append(" -p ").Append(Quote(port.Trim()));
        }

        foreach (var env in options.EnvironmentVariables.Where(e => !string.IsNullOrWhiteSpace(e)))
        {
            sb.Append(" -e ").Append(Quote(env.Trim()));
        }

        foreach (var volume in options.Volumes.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            sb.Append(" -v ").Append(Quote(volume.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(options.Network))
        {
            sb.Append(" --network ").Append(Quote(options.Network!.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(options.Memory))
        {
            sb.Append(" -m ").Append(Quote(options.Memory!.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(options.Cpus))
        {
            sb.Append(" --cpus ").Append(Quote(options.Cpus!.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(options.Workdir))
        {
            sb.Append(" -w ").Append(Quote(options.Workdir!.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(options.Hostname))
        {
            sb.Append(" -h ").Append(Quote(options.Hostname!.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(options.PullPolicy) &&
            !string.Equals(options.PullPolicy, "missing", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(" --pull ").Append(Quote(options.PullPolicy!.Trim()));
        }

        sb.Append(' ').Append(Quote(options.Image.Trim()));

        if (!string.IsNullOrWhiteSpace(options.Command))
        {
            sb.Append(' ').Append(options.Command!.Trim());
        }

        return sb.ToString();
    }

    /// <summary>界面上展示的完整命令行（含程序名）。</summary>
    public static string BuildRunCommandLine(ContainerCreateOptions options)
        => $"wslc {BuildRunArguments(options)}";

    /// <summary>
    /// 参数级转义：含空格/引号/中文路径的参数一律加双引号并转义内部引号，
    /// 避免 <c>--name my container</c> 被拆成两个参数。
    ///
    /// 反斜杠必须加倍：Windows <c>CommandLineToArgvW</c> 规则下 <c>\"</c> 表示字面引号，
    /// 而引号前连续反斜杠会被成对消费——<c>"C:\work\"</c> 的尾部反斜杠会把收尾引号
    /// 转义掉，参数边界整体错乱（典型场景：<c>-w C:\work\</c>）。
    /// </summary>
    public static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";

        var needsQuote = value.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '&' or '|' or '<' or '>' or '(' or ')' or '\\');
        if (!needsQuote) return value;

        // 先加倍反斜杠再转义引号，顺序不能反（CRT 解析规则的保序转义）
        return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }
}
