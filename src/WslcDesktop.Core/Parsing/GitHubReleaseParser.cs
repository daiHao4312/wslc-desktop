using System.Text.Json;
using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Parsing;

/// <summary>
/// GitHub Releases API 的「latest release」响应解析（纯函数，便于单测）。
///
/// 只看需要的字段；缺字段不抛异常，返回 null 由调用方降级成"无法解析"。
/// 资产挑选规则：优先名字以 <c>.exe</c> 结尾的第一个资产（本面板就是单文件 exe 分发），
/// 没有就退回发布页地址——绝不猜一个不存在的链接。
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
                AssetName = assetName
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

    private static DateTimeOffset? ParseTime(string? raw)
        => DateTimeOffset.TryParse(raw, out var parsed) ? parsed.ToLocalTime() : null;
}
