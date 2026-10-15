using System.Text;

namespace WSLC.Panel.Core.Parsing;

/// <summary>
/// wslc 的 settings.yaml 读写器。
///
/// 设计取舍（§5.3 偏好隔离 + §6.2-13 设置页）：
/// 面板**只**编辑 wslc 自己的配置键，不往里塞自定义字段；编辑走"逐行替换"而不是
/// YAML 库的重序列化，这样**注释和排版原样保留**——用户下次手改文件时不会一脸懵。
///
/// 键路径形如 <c>session.cpuCount</c>、<c>credentialStore</c>。模板里这些键默认是注释掉的
/// （<c># cpuCount: default</c>），设置时把那一行改成有效键值行。
/// </summary>
public static class WslcSettingsEditor
{
    /// <summary>读取所有生效的键（section.key → value）。忽略注释行与空行。</summary>
    public static Dictionary<string, string> Read(string? yaml)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(yaml)) return result;

        var section = string.Empty;

        foreach (var rawLine in yaml.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (string.IsNullOrWhiteSpace(line)) continue;

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#')) continue;

            var indent = line.Length - trimmed.Length;

            // 顶格且以 ':' 结尾 → 段落
            if (indent == 0 && trimmed.EndsWith(':'))
            {
                section = trimmed.TrimEnd(':').Trim();
                continue;
            }

            // 顶格的键值行属于顶层，必须把段落上下文清掉
            if (indent == 0) section = string.Empty;

            var colon = trimmed.IndexOf(':');
            if (colon <= 0) continue;

            var key = trimmed[..colon].Trim();
            var value = trimmed[(colon + 1)..].Trim();

            if (value.Length == 0) continue;

            result[string.IsNullOrEmpty(section) ? key : $"{section}.{key}"] = value;
        }

        return result;
    }

    /// <summary>
    /// 写入一个键。<paramref name="value"/> 为 null/空表示"恢复默认"（把该行注释掉并写回 default）。
    /// 返回新的 YAML 文本；找不到对应行时追加到所属段落末尾。
    /// </summary>
    public static string Set(string yaml, string keyPath, string? value)
    {
        if (string.IsNullOrWhiteSpace(keyPath)) return yaml;

        var parts = keyPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var section = parts.Length > 1 ? parts[0] : string.Empty;
        var key = parts[^1];

        var effective = string.IsNullOrWhiteSpace(value)
            ? "default"
            // 换行/制表符会破坏 YAML 行结构：值只允许单行，异常字符一律替换为空格
            : value!.Trim().ReplaceLineEndings(" ").Replace("\t", "  ");
        var rendered = string.IsNullOrEmpty(section)
            ? $"{key}: {effective}"
            : $"  {key}: {effective}";

        var lines = (yaml ?? string.Empty).Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        var currentSection = string.Empty;
        var sectionStart = -1;
        var sectionEnd = lines.Count;
        var replaced = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.Length == 0) continue;

            var isComment = trimmed.StartsWith('#');
            var content = isComment ? trimmed[1..].TrimStart() : trimmed;
            var indent = isComment ? line.IndexOf('#') : line.Length - trimmed.Length;

            // 段落头：顶格、非注释、以 ':' 结尾
            if (!isComment && indent == 0 && trimmed.EndsWith(':'))
            {
                if (currentSection.Length > 0 && sectionEnd == lines.Count)
                {
                    sectionEnd = i;
                }

                currentSection = trimmed.TrimEnd(':').Trim();

                if (string.Equals(currentSection, section, StringComparison.OrdinalIgnoreCase))
                {
                    sectionStart = i;
                    sectionEnd = lines.Count;
                }

                continue;
            }

            // 段落内的键（含被注释掉的模板键）
            if (content.IndexOf(':') <= 0) continue;

            var candidateKey = content[..content.IndexOf(':')].Trim();
            if (!string.Equals(candidateKey, key, StringComparison.OrdinalIgnoreCase)) continue;

            // 归属判定按"行的实际缩进"来：缩进 0（含顶格注释键）就是顶层键。
            // 官方模板里 session 的键注释为两空格缩进，而 credentialStore 顶格 —— 靠这个区分。
            var lineSection = indent == 0 ? string.Empty : currentSection;

            var inTargetSection = string.IsNullOrEmpty(section)
                ? lineSection.Length == 0
                : string.Equals(lineSection, section, StringComparison.OrdinalIgnoreCase);

            if (!inTargetSection) continue;

            lines[i] = rendered;
            replaced = true;
            break;
        }

        if (!replaced)
        {
            if (sectionStart >= 0)
            {
                var insertAt = sectionEnd == lines.Count ? lines.Count : sectionEnd;
                // 插到段落末尾前，跳过尾随空行
                while (insertAt > sectionStart + 1 && string.IsNullOrWhiteSpace(lines[insertAt - 1]))
                {
                    insertAt--;
                }

                lines.Insert(insertAt, rendered);
            }
            else if (string.IsNullOrEmpty(section))
            {
                lines.Add(rendered);
            }
            else
            {
                lines.Add(string.Empty);
                lines.Add($"{section}:");
                lines.Add(rendered);
            }
        }

        var builder = new StringBuilder();
        builder.Append(string.Join('\n', lines));

        // 保留原始末尾换行
        if ((yaml ?? string.Empty).EndsWith('\n')) builder.Append('\n');

        return builder.ToString();
    }
}
