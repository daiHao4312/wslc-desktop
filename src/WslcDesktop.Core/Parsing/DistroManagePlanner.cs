using System.Text;
using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Parsing;

/// <summary>
/// 发行版详情操作的参数拼装与前置校验（纯函数，可完整单测）。
///
/// <c>wsl --manage</c> 下的选项来自参考项目对本机 <c>wsl.exe --help</c> 的实测：
/// <code>
///   --move &lt;Location&gt;            移动安装位置
///   --compact                    压缩 VHDX，回收已释放的块
///   --set-sparse &lt;true|false&gt;    开关稀疏 VHD
///   --resize &lt;MemoryString&gt;      调整磁盘大小（形如 50GB）
///   --set-default-user &lt;Name&gt;    设置默认用户
/// </code>
///
/// 两个"不看文档就会一直失败"的点：
/// <list type="number">
/// <item><b>开稀疏盘必须带 <c>--allow-unsafe</c></b>：WSL 3.0.1.0 起默认拒绝
/// （理由是潜在的数据损坏），报错里它自己给出的命令就带这个开关；关的时候不需要。</item>
/// <item><c>--export</c> 要的是<b>文件名</b>，给一个以分隔符结尾的目录路径会得到
/// 一句难懂的错、还可能先建出一个空文件，所以本地就挡下。</item>
/// </list>
/// </summary>
public static class DistroManagePlanner
{
    /// <summary>发行版内 <c>wsl.conf</c> 的路径（全局设置，按发行版生效）。</summary>
    public const string WslConfPath = "/etc/wsl.conf";

    /// <summary>写 wsl.conf 前先备份到这里；改坏了还能捞回来。</summary>
    public const string WslConfBackupPath = "/etc/wsl.conf.bak";

    private static readonly string[] SizeUnits = { "B", "KB", "MB", "GB", "TB" };

    private const string HeredocDelimiterBase = "WSL_CONF_EOF";

    // ---------------- 参数拼装 ----------------

    public static string BuildCompactArguments(string name)
        => $"--manage {QuoteName(name)} --compact";

    /// <summary>
    /// 开关稀疏盘。开的时候**必须**带 <c>--allow-unsafe</c>，否则 WSL 3.0.1.0 起一律拒绝。
    /// </summary>
    public static string BuildSparseArguments(string name, bool sparse)
        => sparse
            ? $"--manage {QuoteName(name)} --set-sparse true --allow-unsafe"
            : $"--manage {QuoteName(name)} --set-sparse false";

    /// <summary><paramref name="normalizedSize"/> 必须已过 <see cref="TryNormalizeSize"/>。</summary>
    public static string BuildResizeArguments(string name, string normalizedSize)
        => $"--manage {QuoteName(name)} --resize {normalizedSize}";

    public static string BuildMoveArguments(string name, string targetDirectory)
        => $"--manage {QuoteName(name)} --move {WslcCommandBuilder.Quote(targetDirectory.Trim())}";

    public static string BuildSetDefaultUserArguments(string name, string user)
        => $"--manage {QuoteName(name)} --set-default-user {WslcCommandBuilder.Quote(user.Trim())}";

    public static string BuildExportArguments(string name, string filePath)
        => $"--export {QuoteName(name)} {WslcCommandBuilder.Quote(filePath.Trim())}";

    /// <summary>读 <c>/etc/wsl.conf</c> 的脚本：文件不存在也<b>退出 0</b>（返回空串）。</summary>
    public static string BuildReadWslConfScript()
        => $"[ -f {WslConfPath} ] && cat {WslConfPath} || true";

    /// <summary>备份 <c>/etc/wsl.conf</c> 的脚本：文件不存在时也退出 0。</summary>
    public static string BuildBackupWslConfScript()
        => $"[ -f {WslConfPath} ] && cp {WslConfPath} {WslConfBackupPath} || true";

    /// <summary>
    /// 写 <c>/etc/wsl.conf</c> 的脚本（<b>先备份由调用方单独跑</b>）。
    ///
    /// 用**带引号的 heredoc**（<c>&lt;&lt; '定界符'</c>）：<c>$</c> / 反引号 / 反斜杠都不会被 shell 展开。
    /// 定界符会挑一个正文里没出现过的（见 <see cref="PickDelimiter"/>）——
    /// 撞上了会把后面的内容当命令<b>执行</b>，是静默写坏配置的那类 bug。
    /// </summary>
    public static string BuildWriteWslConfScript(string text)
    {
        // 定界符必须独占一行 heredoc 才结束；正文不以换行收尾时自己补一个，
        // 否则定界符会粘在最后一行上，sh 报 "here-document delimited by end-of-file"
        // 并把定界符当正文写进文件（那一行就成了坏配置项）。
        var body = text.Length == 0 || text.EndsWith('\n') ? text : text + "\n";
        var delimiter = PickDelimiter(body);

        var sb = new StringBuilder();
        sb.Append("cat << '").Append(delimiter).Append("' > ").Append(WslConfPath).Append('\n');
        sb.Append(body);
        sb.Append(delimiter).Append('\n');
        return sb.ToString();
    }

    /// <summary>挑一个正文里没出现过的 heredoc 定界符。</summary>
    public static string PickDelimiter(string text)
    {
        var candidate = HeredocDelimiterBase;
        var n = 0;

        while (text.Split('\n').Any(line => line.TrimEnd('\r').TrimEnd() == candidate))
        {
            n++;
            candidate = $"{HeredocDelimiterBase}_{n}";
        }

        return candidate;
    }

    /// <summary>发行版内的命令要过 shell，用户输入必须转义——否则「默认用户」填个
    /// <c>; rm -rf /</c> 就直接执行了。</summary>
    public static string ShellQuote(string text) => $"'{text.Replace("'", "'\\''")}'";

    // ---------------- 校验 ----------------

    public static bool TryValidateDistroName(string? name, out string error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "发行版名不能为空";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// 校验并规范化 <c>--resize</c> 的大小，形如 <c>50GB</c>（大小写不敏感，允许小数）。
    ///
    /// 只做**格式**校验，数值合不合理（比当前盘还小？）交给 WSL 判断，我们不猜。
    /// 注意要显式挡掉 <c>1.GB</c> / <c>.5GB</c> 这种"能当数字解析但是残缺"的写法——
    /// 放过去只会让用户收到一句含糊的引擎报错。
    /// </summary>
    public static bool TryNormalizeSize(string? raw, out string normalized, out string error)
    {
        normalized = string.Empty;
        var text = (raw ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            error = "大小不能为空";
            return false;
        }

        var splitAt = 0;
        while (splitAt < text.Length && (char.IsAsciiDigit(text[splitAt]) || text[splitAt] == '.'))
        {
            splitAt++;
        }

        var number = text[..splitAt];
        var unit = text[splitAt..].Trim().ToUpperInvariant();

        var digits = number.Count(char.IsAsciiDigit);
        var dots = number.Count(c => c == '.');

        if (digits == 0 || dots > 1 || number.StartsWith('.') || number.EndsWith('.'))
        {
            error = $"「{text}」里没有有效的数字";
            return false;
        }

        if (!SizeUnits.Contains(unit))
        {
            error = $"单位只认 {string.Join(" / ", SizeUnits)}（例如 50GB），收到「{unit}」";
            return false;
        }

        normalized = number + unit;
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Windows 绝对路径：<c>D:\x</c> / <c>D:/x</c> / UNC <c>\\server\share</c>。
    /// 不用 <c>Path.IsPathRooted</c>——它对 <c>\foo</c> 也返回 true，那是"当前盘根下"的相对写法。
    /// </summary>
    public static bool IsAbsoluteWindowsPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;

        return path.Length >= 3
            && char.IsLetter(path[0])
            && path[1] == ':'
            && (path[2] == '\\' || path[2] == '/');
    }

    /// <summary>移动目标校验。</summary>
    public static bool TryValidateMoveTarget(string? directory, out string error)
    {
        var target = (directory ?? string.Empty).Trim();

        if (target.Length == 0)
        {
            error = "目标位置不能为空";
            return false;
        }

        if (!IsAbsoluteWindowsPath(target))
        {
            error = $"目标位置必须是绝对路径（如 D:\\WSL\\Ubuntu）：{target}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>导出路径校验：必须是绝对路径，且是<b>文件名</b>而不是目录。</summary>
    public static bool TryValidateExportPath(string? filePath, out string error)
    {
        var path = (filePath ?? string.Empty).Trim();

        if (path.Length == 0)
        {
            error = "导出路径不能为空";
            return false;
        }

        if (!IsAbsoluteWindowsPath(path))
        {
            error = $"导出路径必须是绝对路径（如 D:\\backup\\Ubuntu.tar）：{path}";
            return false;
        }

        if (path.EndsWith('\\') || path.EndsWith('/'))
        {
            error = $"导出路径要写成文件名（如 D:\\backup\\Ubuntu.tar），不能只给目录：{path}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>wsl.conf 内容校验：NUL 会让 wsl 侧解析出奇怪的结果，本地就拒掉。</summary>
    public static bool TryValidateWslConfText(string? text, out string error)
    {
        if (text is not null && text.Contains('\0'))
        {
            error = "配置内容里含有 NUL 字符，无法写入";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// 给用户看的操作说明（确认框里用）。破坏性操作必须写清后果。
    /// </summary>
    public static string DescribeOperation(DistroOperationKind kind, string name) => kind switch
    {
        DistroOperationKind.Compact =>
            $"压缩发行版 {name} 的虚拟磁盘：回收已删除文件占用的块，文件会变小。\n" +
            "不会动里面的数据，但需要一点时间（碎片多时更久）。",

        DistroOperationKind.SetSparse =>
            $"把 {name} 的虚拟磁盘切换为稀疏模式，之后 WSL 会自动回收已释放的块。\n" +
            "WSL 3.0.1 起默认认为这个操作有潜在的数据损坏风险（需要显式允许才执行）。",

        DistroOperationKind.Resize =>
            $"调整发行版 {name} 的磁盘大小。\n注意：缩小到比当前已用空间还小会失败。",

        DistroOperationKind.Move =>
            $"把发行版 {name} 移动到新位置。\n" +
            "跨盘移动是**真的在拷数据**，几十 GB 可能需要数分钟到数十分钟；期间不要关闭面板。",

        DistroOperationKind.Export =>
            $"把发行版 {name} 导出成一个 tar 归档。\n" +
            "归档里包含完整的 Linux 文件系统（含密钥、密码哈希），请妥善保管。",

        DistroOperationKind.WriteWslConf =>
            $"写入 {name} 的 {WslConfPath}。\n" +
            $"原文件会先备份到 {WslConfBackupPath}；这个文件写错会让发行版下次启动失败。",

        DistroOperationKind.SetDefaultUser =>
            $"把 {name} 的默认登录用户改为指定用户。\n用户名不存在时发行版下次启动会失败。",

        _ => $"对 {name} 执行操作。"
    };

    private static string QuoteName(string name) => WslcCommandBuilder.Quote(name.Trim());
}

/// <summary>发行版详情操作类别（文案与日志用）。</summary>
public enum DistroOperationKind
{
    Compact,
    SetSparse,
    Resize,
    Move,
    Export,
    WriteWslConf,
    SetDefaultUser
}
