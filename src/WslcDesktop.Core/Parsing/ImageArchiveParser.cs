using System.Text.RegularExpressions;

namespace WslcDesktop.Core.Parsing;

/// <summary>
/// 镜像归档（<c>wslc save -o</c> / <c>wslc load -i</c>）的输出解析与路径校验。
///
/// 实测事实（2026-10-20，本机 wslc）：
/// <list type="bullet">
/// <item><c>wslc save [选项] &lt;image&gt;...</c> —— 选项在前、镜像在后；**不给 <c>-o</c> 就把 tar 写到 stdout**，
///   面板里绝不能省这个参数（省了等于把二进制倒进管道）。</item>
/// <item>成功时**一句话都不输出**（8.4 MB 的 alpine 实测）；失败走 stderr，
///   形如 <c>来自引擎的错误信息: reference does not exist</c> + <c>错误代码： WSLC_E_IMAGE_NOT_FOUND</c>。</item>
/// <item><c>wslc load -i &lt;tar&gt;</c> 每个镜像打一行 <c>已加载映像: repo:tag</c>，同样没有字节数。</item>
/// </list>
/// </summary>
public static class ImageArchiveParser
{
    /// <summary>面板约定：导出文件统一用 <c>.tar</c>（<c>wslc save</c> 的产物格式）。</summary>
    public const string ArchiveExtension = ".tar";

    // 中英文各一套：引擎输出语言跟随系统，别只认中文（CI / 英文系统上就全空）
    private static readonly Regex LoadedReferenceRegex = new(
        @"^(?:已加载映像|已加载图像|Loaded image|Loaded Image)\s*[:：]\s*(?<ref>\S.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex EngineMessageRegex = new(
        @"(?:来自引擎的错误信息|Error message from engine)\s*[:：]\s*(?<msg>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ErrorCodeRegex = new(
        @"(?:错误代码|Error code)\s*[:：]\s*(?<code>[A-Za-z0-9_]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>解析 <c>已加载映像: repo:tag</c> 形式的行。</summary>
    public static bool TryParseLoadedReference(string? line, out string reference)
    {
        reference = string.Empty;
        if (string.IsNullOrWhiteSpace(line)) return false;

        var match = LoadedReferenceRegex.Match(line.Trim());
        if (!match.Success) return false;

        reference = match.Groups["ref"].Value.Trim();
        return reference.Length > 0;
    }

    /// <summary>
    /// 从引擎输出里挑出一句能行动的错误原因。
    ///
    /// 直接 <c>trim</c> 整段会把"版权所有 (c) Microsoft Corporation"和"如果此错误是意外错误…"
    /// 这类固定噪声一起摆给用户（发行版那边已经踩过一次），所以这里按行过滤。
    /// </summary>
    public static string ExtractError(string? standardError, string? standardOutput, string fallback = "引擎没有返回原因")
    {
        var message = string.Empty;
        var code = string.Empty;
        var firstReadable = string.Empty;

        foreach (var text in new[] { standardError, standardOutput })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || IsNoise(line)) continue;

                var engineMatch = EngineMessageRegex.Match(line);
                if (engineMatch.Success && message.Length == 0)
                {
                    message = engineMatch.Groups["msg"].Value.Trim();
                    continue;
                }

                var codeMatch = ErrorCodeRegex.Match(line);
                if (codeMatch.Success && code.Length == 0)
                {
                    code = codeMatch.Groups["code"].Value.Trim();
                    continue;
                }

                if (firstReadable.Length == 0) firstReadable = line;
            }
        }

        if (message.Length > 0)
        {
            return code.Length > 0 ? $"{message}（{code}）" : message;
        }

        if (firstReadable.Length > 0)
        {
            return code.Length > 0 ? $"{firstReadable}（{code}）" : firstReadable;
        }

        return fallback;
    }

    /// <summary>引擎退出码非 0 但一句话都没给时的兜底文案。</summary>
    public static string DescribeFailure(bool isExport)
        => isExport ? "导出失败（引擎没有返回原因）" : "导入失败（引擎没有返回原因）";

    /// <summary>导出路径：必须是绝对路径，且是文件名而不是目录。</summary>
    public static bool TryValidateExportPath(string? filePath, out string error)
    {
        var path = (filePath ?? string.Empty).Trim();

        if (path.Length == 0)
        {
            error = "导出路径不能为空";
            return false;
        }

        if (!DistroManagePlanner.IsAbsoluteWindowsPath(path))
        {
            error = $"导出路径必须是绝对路径（如 D:\\backup\\alpine.tar）：{path}";
            return false;
        }

        if (path.EndsWith('\\') || path.EndsWith('/'))
        {
            error = $"导出路径要写成文件名（如 D:\\backup\\alpine.tar），不能只给目录：{path}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>导入路径：必须是绝对路径，且文件真实存在（不存在时让用户改选，而不是丢给引擎报一句本地化的错）。</summary>
    public static bool TryValidateImportPath(string? filePath, out string error)
    {
        var path = (filePath ?? string.Empty).Trim();

        if (path.Length == 0)
        {
            error = "请选择要导入的 tar 文件";
            return false;
        }

        if (!DistroManagePlanner.IsAbsoluteWindowsPath(path))
        {
            error = $"导入路径必须是绝对路径：{path}";
            return false;
        }

        if (Directory.Exists(path))
        {
            error = $"这是目录不是文件，请选 tar 文件本身：{path}";
            return false;
        }

        if (!File.Exists(path))
        {
            error = $"文件不存在：{path}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// 建议的导出文件名：第一个镜像名最后一段 + <c>.tar</c>。
    /// <c>docker.1panel.live/library/alpine:latest</c> → <c>alpine.tar</c>。
    /// </summary>
    public static string SuggestFileName(string? firstReference, DateTimeOffset now)
    {
        var name = (firstReference ?? string.Empty).Trim();

        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];

        var colon = name.IndexOf(':');
        if (colon > 0) name = name[..colon];

        if (name.Length == 0) name = "images";

        return ExportName.Suggest(name, ArchiveExtension, now);
    }

    private static bool IsNoise(string line)
        => line.StartsWith("版权所有", StringComparison.Ordinal)
           || line.StartsWith("有关此产品的隐私", StringComparison.Ordinal)
           || line.StartsWith("如果此错误是意外错误", StringComparison.Ordinal)
           || line.StartsWith("If this error is unexpected", StringComparison.OrdinalIgnoreCase)
           || line.StartsWith("Copyright (c)", StringComparison.OrdinalIgnoreCase);
}
