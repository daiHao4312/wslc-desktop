using System.Text.Json;
using System.Text.Json.Nodes;

namespace WslcDesktop.Core.Parsing;

/// <summary>
/// 诊断包里的 <c>prefs.json</c> 脱敏。
///
/// 为什么必须做：prefs 里有 `RegistryCredentials`（DPAPI 密文 + 用户名）。
/// 密文对别人没用，但**不该出现在一个会被发出去的文件里** ——
/// 诊断包的默认假设是"用户会把它贴给别人"。
///
/// 策略：按**键名**匹配（password / token / secret / credential 等）整项删除，
/// 而不是逐字段挑 —— 以后新增敏感字段也不会漏。
/// </summary>
public static class DiagnosticsRedactor
{
    /// <summary>键名命中任意一个片段就整项删除（大小写不敏感）。</summary>
    private static readonly string[] SensitiveFragments =
    {
        "password",
        "token",
        "secret",
        "credential"
    };

    public static string RedactPreferences(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
            {
                return Omitted("prefs.json 不是对象");
            }

            RedactObject(root);
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            // 解析不了就没法保证脱敏干净 —— 宁可省略，也不原样带出去
            return Omitted("prefs.json 解析失败");
        }
    }

    private static void RedactObject(JsonObject obj)
    {
        foreach (var key in obj.Select(pair => pair.Key).ToList())
        {
            if (IsSensitive(key))
            {
                obj.Remove(key);
                continue;
            }

            switch (obj[key])
            {
                case JsonObject child:
                    RedactObject(child);
                    break;

                case JsonArray array:
                    foreach (var item in array)
                    {
                        if (item is JsonObject itemObject) RedactObject(itemObject);
                    }

                    break;
            }
        }
    }

    private static bool IsSensitive(string key)
        => SensitiveFragments.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static string Omitted(string reason)
        => $"{{ \"note\": \"{reason}，已省略（避免带出未脱敏内容）\" }}";
}
