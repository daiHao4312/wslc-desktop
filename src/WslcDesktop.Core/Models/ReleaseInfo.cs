namespace WslcDesktop.Core.Models;

/// <summary>
/// 一条 GitHub Release 的关键信息（只保留更新检查需要的字段）。
/// </summary>
public record ReleaseInfo
{
    /// <summary>发布标签，形如 <c>v1.2.0</c>。</summary>
    public string TagName { get; init; } = string.Empty;

    /// <summary>发布标题（可能为空，回落到标签）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>发布页地址。</summary>
    public string HtmlUrl { get; init; } = string.Empty;

    /// <summary>发布说明（Markdown 原文）。</summary>
    public string Body { get; init; } = string.Empty;

    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>形如 <c>.exe</c> 的资产直链（有则优先于发布页）。</summary>
    public string? AssetUrl { get; init; }

    public string? AssetName { get; init; }

    /// <summary>
    /// 资产字节数。在线更新下载前用它估算进度与磁盘占用，缺省为 null（不阻塞流程）。
    /// </summary>
    public long? AssetSize { get; init; }

    /// <summary>
    /// 资产的 sha256（小写十六进制，不带 <c>sha256:</c> 前缀），来自 GitHub 的 <c>digest</c> 字段。
    ///
    /// 有它就把下载内容当作可验证的：校验不通过一律判为失败并删除落盘文件。
    /// 老发布可能没有该字段，此时退回 Content-Length 长度校验。
    /// </summary>
    public string? AssetSha256 { get; init; }

    /// <summary>去掉前缀 v 的版本号，用于版本比较与展示。</summary>
    public string VersionDisplay => TagName.TrimStart('v', 'V').Trim();

    /// <summary>下载落点：优先资产直链，否则发布页。</summary>
    public string DownloadUrl => string.IsNullOrWhiteSpace(AssetUrl) ? HtmlUrl : AssetUrl!;

    /// <summary>发布说明的紧凑摘要（单行、限长），用于 InfoBar 展示。</summary>
    public string NotesSummary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Body)) return string.Empty;

            var flat = string.Join(' ', Body
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimStart('#', '*', '-', ' ', '\t'))
                .Where(l => l.Length > 0));

            return flat.Length <= 200 ? flat : flat[..200] + "…";
        }
    }
}
