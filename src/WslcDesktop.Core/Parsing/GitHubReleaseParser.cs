using System.Text.Json;
using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Parsing;

/// <summary>
/// GitHub Releases API 的「latest release」响应解析（纯函数，便于单测）。
///
/// 只看需要的字段；缺字段不抛异常，返回 null 由调用方降级成"无法解析"。
/// 资产挑选规则：优先名字以 <c>.exe</c> 结尾的第一个资产（本面板就是单文件 exe 分发），
/// 没有就退回发布页地址——绝不猜一个不存在的链接。
///
/// 同时带上资产的 <c>size</c> 与 <c>digest</c>：在线更新要用它们做下载进度估算与完整性校验。
/// 两者都可能缺失（老发布），缺失不影响解析成功，只影响更新流程能做到多严格。
/// </summary>
public static class GitHubReleaseParser
{
    public static ReleaseInfo? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var tag = GetString(root, "tag_name");
            if (string.IsNullOrWhiteSpace(tag)) return null;

            string? assetUrl = null;
            string? assetName = null;
            long? assetSize = null;
            string? assetDigest = null;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.ValueKind != JsonValueKind.Object) continue;

                    var name = GetString(asset, "name");
                    var url = GetString(asset, "browser_download_url");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                    assetName = name;
                    assetUrl = url;
                    assetSize = GetLong(asset, "size");
                    // 只认 sha256；其它算法（或字段缺失）一律当"没有摘要"处理
                    assetDigest = AssetDigest.NormalizeSha256(GetString(asset, "digest"));
                    break;
                }
            }

            return new ReleaseInfo
            {
                TagName = tag,
                Name = GetString(root, "name") ?? tag,
                HtmlUrl = GetString(root, "html_url") ?? string.Empty,
                Body = GetString(root, "body") ?? string.Empty,
                PublishedAt = ParseTime(GetString(root, "published_at")),
                AssetUrl = assetUrl,
                AssetName = assetName,
                AssetSize = assetSize,
                AssetSha256 = assetDigest
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>读数字字段；非数字、负数一律当"没有"（size 为负没有意义）。</summary>
    private static long? GetLong(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt64(out var parsed)
           && parsed >= 0
            ? parsed
            : null;

    private static DateTimeOffset? ParseTime(string? raw)
        => DateTimeOffset.TryParse(raw, out var parsed) ? parsed.ToLocalTime() : null;
}
