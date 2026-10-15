using System.Text.Json;
using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Parsing;

/// <summary>
/// 解析 <c>wslc image inspect &lt;ref&gt; --format json</c>。
///
/// 实测（WSL 3.0.3.0，夹具 image_inspect_nginx.json / image_inspect_hello_world.json）：
/// 输出是一个 **JSON 数组**（不是 CLI 其它命令的 JSON Lines），元素里 Config 段含
/// <c>Cmd</c> / <c>Entrypoint</c> / <c>ExposedPorts</c>（对象，键形如 "80/tcp"）/ <c>Env</c> / <c>WorkingDir</c>。
/// 镜像不在本地时 wslc 返回错误文本而不是 JSON —— 这里返回 Found=false 并带可读原因。
/// </summary>
public static class ImageInspectParser
{
    public static ImageInspectInfo Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ImageInspectInfo { Found = false, Error = "引擎未返回镜像信息" };
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement entry;
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0)
                {
                    return new ImageInspectInfo { Found = false, Error = "镜像不在本地" };
                }
                // 数组首元素也必须判类型：引擎异常输出（如 ["error"]）时
                // 直接 TryGetProperty 会抛 InvalidOperationException，且不被下面的
                // catch (JsonException) 捕获，会一路冒泡到 UI 层弹全局错误框
                if (root[0].ValueKind != JsonValueKind.Object)
                {
                    return new ImageInspectInfo { Found = false, Error = "镜像信息格式无法识别" };
                }
                entry = root[0];
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                entry = root;
            }
            else
            {
                return new ImageInspectInfo { Found = false, Error = "镜像信息格式无法识别" };
            }

            IReadOnlyList<string> command = Array.Empty<string>();
            IReadOnlyList<string> entrypoint = Array.Empty<string>();
            IReadOnlyList<string> env = Array.Empty<string>();
            var exposed = new List<int>();
            var workingDir = string.Empty;

            if (entry.TryGetProperty("Config", out var config) && config.ValueKind == JsonValueKind.Object)
            {
                command = ReadStringArray(config, "Cmd");
                entrypoint = ReadStringArray(config, "Entrypoint");
                env = ReadStringArray(config, "Env");
                workingDir = config.TryGetProperty("WorkingDir", out var wd) && wd.ValueKind == JsonValueKind.String
                    ? wd.GetString() ?? string.Empty
                    : string.Empty;

                if (config.TryGetProperty("ExposedPorts", out var ports) && ports.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in ports.EnumerateObject())
                    {
                        var portText = property.Name.Split('/')[0];
                        if (int.TryParse(portText, out var port) && !exposed.Contains(port))
                        {
                            exposed.Add(port);
                        }
                    }
                }
            }

            return new ImageInspectInfo
            {
                Found = true,
                Command = command,
                Entrypoint = entrypoint,
                Environment = env,
                ExposedPorts = exposed.OrderBy(p => p).ToList(),
                WorkingDir = workingDir,
                RepoTags = ReadStringArray(entry, "RepoTags")
            };
        }
        catch (JsonException)
        {
            // 不是 JSON，通常是"未找到映像 'x'"这类引擎错误文本
            var reason = json.Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0 && !l.StartsWith("版权所有", StringComparison.Ordinal));

            return new ImageInspectInfo { Found = false, Error = reason ?? "镜像不在本地" };
        }
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrEmpty(value)) list.Add(value);
            }
        }

        return list;
    }
}
