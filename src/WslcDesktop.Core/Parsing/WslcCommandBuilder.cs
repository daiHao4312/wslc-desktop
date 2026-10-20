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

        // 用长选项：`-h` 在不少 CLI 里是 help，docker 系却是 hostname —— 同一个字母两种含义，
        // 不值得赌（参考项目也用 --hostname）。
        if (!string.IsNullOrWhiteSpace(options.Hostname))
        {
            sb.Append(" --hostname ").Append(Quote(options.Hostname!.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(options.DomainName))
        {
            sb.Append(" --domainname ").Append(Quote(options.DomainName!.Trim()));
        }

        // GPU 直通：只有 `--gpus all` 这一种写法有官方示例，device=N 的引号规则没实测，不做
        if (options.EnableGpu) sb.Append(" --gpus all");

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

    /// <summary><c>wslc logs</c> 的默认回看行数。</summary>
    public const int DefaultLogTail = 100;

    /// <summary>
    /// 拼 <c>wslc exec</c> 的参数。
    ///
    /// ⚠️ 一律包成 <c>/bin/sh -c "&lt;整条命令&gt;"</c>：<c>wslc exec</c> 把剩余参数**按 argv 直接交给
    /// 目标进程**，不经过 shell。不包的话：
    /// <list type="bullet">
    /// <item><c>ls | head</c> 的 <c>|</c> 会变成 ls 的一个参数（静默失效）；</item>
    /// <item>带引号的命令会被拆碎 —— 实测报错 <c>unexpected EOF while looking for matching `''</c>
    /// （<c>'i=0;</c> 被当成一个独立 argv 传给了 <c>sh -c</c>）。</item>
    /// </list>
    /// 整条命令作为**一个**参数交给 sh，语义才和终端里一致。
    /// </summary>
    public static string BuildExecArguments(string idOrName, string command)
        => $"exec {Quote(idOrName)} /bin/sh -c {Quote(command)}";

    /// <summary>
    /// 拼 <c>wslc logs</c> 的参数。
    ///
    /// ⚠️ **容器名必须永远在最后** —— 这是参考项目写成单测的一条不变量：
    /// "名字跑到开关前面会被 <c>wslc</c> 当位置参数吃掉，名字后面的开关就变成容器名的一部分"。
    /// 踩过一次：跟随写成 <c>logs -n 200 &lt;容器&gt; --follow</c>，
    /// 引擎收到的是容器名 <c>"&lt;容器&gt; --follow"</c> → 查不到容器 → 面板里只剩快照、新日志永远不来。
    /// 所以 follow 选项**紧跟 <c>logs</c>**，绝不追加在末尾。
    ///
    /// 跟随**不带上限**：与「在终端中看日志」跑的 <c>wslc logs -f &lt;容器&gt;</c> 形状一致
    /// （那是用户机器上实测能出完整日志的命令）；行数由界面侧环形缓冲
    /// （<see cref="Models.LogBuffer"/>）控制，不去赌 <c>-n</c> 与 <c>--follow</c> 的组合语义。
    /// </summary>
    public static string BuildLogsArguments(string idOrName, int tail, bool follow)
    {
        if (follow) return $"logs --follow {Quote(idOrName)}";

        var limit = tail <= 0 ? DefaultLogTail : tail;
        return $"logs -n {limit} {Quote(idOrName)}";
    }

    /// <summary>
    /// 拼 <c>wslc save</c> 的参数（镜像导出）。
    ///
    /// 形状按引擎帮助定：<c>save [选项] &lt;image&gt;...</c> —— **选项在前、镜像在后**。
    /// ⚠️ <c>-o</c> 绝不能省：不给它时引擎把 tar 直接写 stdout（实测），
    /// 省了就会把整个镜像倒进输出管道。
    /// </summary>
    public static string BuildSaveArguments(IEnumerable<string> references, string outputPath)
    {
        var sb = new StringBuilder("save -o ").Append(Quote(outputPath));

        foreach (var reference in references ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(reference)) continue;
            sb.Append(' ').Append(Quote(reference.Trim()));
        }

        return sb.ToString();
    }

    /// <summary>拼 <c>wslc load</c> 的参数（镜像导入）：<c>load [选项]</c>，路径靠 <c>-i</c> 给。</summary>
    public static string BuildLoadArguments(string archivePath)
        => $"load -i {Quote(archivePath)}";

    /// <summary>
    /// 参数级转义：含空格/引号/中文路径的参数一律加双引号并转义内部引号，
    /// 避免 <c>--name my container</c> 被拆成两个参数。
    ///
    /// 反斜杠的处理必须严格照 <c>CommandLineToArgvW</c> 的规则来：
    /// 反斜杠**只有在紧邻双引号时**才具有转义含义——
    /// <list type="bullet">
    /// <item>反斜杠后面不是引号 → 原样保留（<c>\"</c> 之外一律不消费）；</item>
    /// <item>引号前的 n 个反斜杠 → 输出 n/2 个反斜杠，n 为奇数时再输出一个字面引号。</item>
    /// </list>
    ///
    /// 所以只有**字符串结尾**以及**内部引号之前**的连续反斜杠需要加倍。
    /// 曾经把这句理解成"把所有反斜杠都加倍"，后果是
    /// <c>C:\my data\share</c> 被传成 <c>C:\\my data\\share</c>、
    /// UNC 路径 <c>\\wsl$\Ubuntu\home</c> 直接报废。
    /// </summary>
    public static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";

        var needsQuote = value.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '&' or '|' or '<' or '>' or '(' or ')' or '\\');
        if (!needsQuote) return value;

        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');

        var pendingBackslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\')
            {
                pendingBackslashes++;
                continue;
            }

            if (c == '"')
            {
                // 内部引号前：反斜杠加倍（避免被当成转义）+ 一个转义用的反斜杠，再输出字面引号
                sb.Append('\\', pendingBackslashes * 2 + 1);
                sb.Append('"');
            }
            else
            {
                sb.Append('\\', pendingBackslashes);
                sb.Append(c);
            }

            pendingBackslashes = 0;
        }

        // 收尾：结尾反斜杠必须加倍，否则会把收尾引号转义掉、参数边界整体错乱
        sb.Append('\\', pendingBackslashes * 2);
        sb.Append('"');

        return sb.ToString();
    }
}
