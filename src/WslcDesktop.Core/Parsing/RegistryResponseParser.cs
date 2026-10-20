using System.Text.Json;

namespace WslcDesktop.Core.Parsing;

/// <summary>
/// Registry V2 响应解析（纯函数，夹具驱动单测）。
///
/// 夹具都是**真机抓取**的（`tests/fixtures/registry_*.json`，从参考项目继承）：
/// tag 列表、多架构 index、单架构 manifest、错误信封。
/// </summary>
public static class RegistryResponseParser
{
    /// <summary>实测：<c>library/alpine</c> 有 224 个 tag，正文 2067 字节。</summary>
    private const int MaxTags = 20000;

    // ---------------- tag 列表 ----------------

    /// <summary>
    /// 解析 <c>{"name":"…","tags":[…]}</c>，**保持 registry 给的顺序**（排序是
    /// <see cref="SortTags"/> 的事）。
    ///
    /// <c>tags</c> 为 <c>null</c>（仓库存在但没有 tag）算**错误**而不是空列表：
    /// 界面上一片空白，用户分不清是"没 tag"还是"我们解错了"。
    /// </summary>
    public static bool TryParseTags(string? json, out List<string> tags, out string error)
    {
        tags = new List<string>();
        error = string.Empty;

        if (!TryParse(json, out var root, out error)) return false;

        if (TryReadEnvelopeError(root, out var envelope))
        {
            error = envelope;
            return false;
        }

        if (!root.TryGetProperty("tags", out var tagsNode) ||
            tagsNode.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            error = "这个仓库没有任何 tag";
            return false;
        }

        if (tagsNode.ValueKind != JsonValueKind.Array)
        {
            error = "registry 返回的 tags 不是数组";
            return false;
        }

        foreach (var item in tagsNode.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;

            var text = (item.GetString() ?? string.Empty).Trim();
            if (text.Length > 0 && tags.Count < MaxTags) tags.Add(text);
        }

        if (tags.Count == 0)
        {
            error = "这个仓库没有任何 tag";
            return false;
        }

        return true;
    }

    // ---------------- tag 排序 ----------------

    /// <summary>
    /// 把 tag 排成"用户会想点的顺序"。
    ///
    /// ⚠️ **接口给的是字母序，照原样显示是错的**：实测 <c>library/alpine</c> 的 224 个 tag
    /// 前 3 个是 <c>2.6, 2.7, 20190228</c>，而 <c>latest</c> 排在**最后一位（224）**。
    /// 规则：
    /// <list type="number">
    /// <item><c>latest</c> 第一；</item>
    /// <item>像版本号的按**版本倒序**（<c>3.24.2 &gt; 3.24.1 &gt; 3.24 &gt; 3.23.6</c>），
    /// 同版本再按 tag 原样升序（<c>1.31.6</c> 在 <c>1.31.6-alpine</c> 前面）；</item>
    /// <item>其余按原样升序（<c>edge</c> / <c>stable</c> / <c>trixie</c> 这类）。</item>
    /// </list>
    /// </summary>
    public static List<string> SortTags(IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var latest = new List<string>();
        var versioned = new List<(string Tag, IReadOnlyList<int> Key)>();
        var others = new List<string>();

        foreach (var tag in tags)
        {
            if (tag.Equals("latest", StringComparison.OrdinalIgnoreCase))
            {
                latest.Add(tag);
                continue;
            }

            var key = VersionKey(tag);
            if (key is null) others.Add(tag);
            else versioned.Add((tag, key));
        }

        versioned.Sort((a, b) =>
        {
            var byVersion = CompareVersionKeys(b.Key, a.Key);      // 版本倒序
            return byVersion != 0 ? byVersion : string.CompareOrdinal(a.Tag, b.Tag);
        });

        others.Sort(StringComparer.Ordinal);

        var result = new List<string>(latest);
        result.AddRange(versioned.Select(v => v.Tag));
        result.AddRange(others);
        return result;
    }

    /// <summary>
    /// 抠出 tag 开头的 <c>数字(.数字)*</c> 当版本段；**不像版本号就返回 null**。
    ///
    /// 三条"不像版本号"的判据都有实测依据：
    /// <list type="bullet">
    /// <item>不以数字开头 —— <c>edge</c> / <c>stable</c> / <c>trixie</c>；</item>
    /// <item>某段大于 9999 —— alpine 里有一大批 <c>20190228</c> 这样的**日期 tag**，
    /// 不排除的话它们会挤进前几名（实测排除后 <c>20190228</c> 落到第 190 位）；</item>
    /// <item>段数大于 4、或第一段大于 1000 —— 挡住时间戳式的长数字。</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<int>? VersionKey(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return null;

        var parts = new List<int>();
        var current = string.Empty;

        foreach (var c in tag)
        {
            if (c is >= '0' and <= '9')
            {
                current += c;
            }
            else if (c == '.' && current.Length > 0)
            {
                if (!int.TryParse(current, out var parsed)) return null;
                parts.Add(parsed);
                current = string.Empty;
            }
            else
            {
                // 版本段到此结束（`3.24.2-alpine` 只吃 `3.24.2`）
                break;
            }
        }

        if (current.Length == 0) return null;
        if (!int.TryParse(current, out var last)) return null;
        parts.Add(last);

        if (parts.Count > 4) return null;
        if (parts.Any(part => part > 9999)) return null;
        if (parts[0] > 1000) return null;

        return parts;
    }

    // ---------------- manifest ----------------

    /// <summary>
    /// 从多架构 index 里挑出**本机架构**那一份的摘要（拿它当 <c>manifests/&lt;digest&gt;</c> 用）。
    ///
    /// ⚠️ 必须同时看 <c>architecture</c> 与 <c>os</c>：实测 hello-world 的 index 里有
    /// **两条 <c>amd64</c>**（一条 linux、两条 windows），只看架构就会挑到 Windows 那份。
    /// 而且每条真平台后面都跟一条 <c>unknown/unknown</c> 的 <b>attestation-manifest</b>
    /// （alpine 的 16 条里 8 条是它），所以"取第一条"必然取到它。
    /// </summary>
    public static bool TryPickPlatformManifest(string? json, string architecture, out string digest, out string error)
    {
        digest = string.Empty;
        error = string.Empty;

        if (!TryParse(json, out var root, out error)) return false;

        if (!root.TryGetProperty("manifests", out var manifests) || manifests.ValueKind != JsonValueKind.Array)
        {
            error = "registry 返回的不是多架构 index";
            return false;
        }

        foreach (var manifest in manifests.EnumerateArray())
        {
            if (!manifest.TryGetProperty("platform", out var platform)) continue;
            if (platform.ValueKind != JsonValueKind.Object) continue;

            var arch = platform.TryGetProperty("architecture", out var a) ? a.GetString() : null;
            var os = platform.TryGetProperty("os", out var o) ? o.GetString() : null;

            if (!string.Equals(arch, architecture, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(os, "linux", StringComparison.OrdinalIgnoreCase)) continue;

            var value = manifest.TryGetProperty("digest", out var d) ? d.GetString() : null;
            if (string.IsNullOrWhiteSpace(value)) continue;

            digest = value!;
            return true;
        }

        error = $"index 里没有 {architecture}/linux 这一份";
        return false;
    }

    /// <summary>
    /// 累加 <c>config.size + layers[].size</c> —— 这就是实际要下载的字节数
    /// （界面上的"下载约 N MB"）。注意它和本地 <c>wslc images</c> 报的体积**不是一个口径**：
    /// 后者是解压后的落盘占用（实测 nginx 60.9 MB vs 163 MB，差 2.7 倍，两个数都对）。
    /// </summary>
    public static bool TrySumDownloadBytes(string? json, out long bytes, out string error)
    {
        bytes = 0;
        error = string.Empty;

        if (!TryParse(json, out var root, out error)) return false;

        if (root.TryGetProperty("config", out var config) &&
            config.ValueKind == JsonValueKind.Object &&
            config.TryGetProperty("size", out var configSize) &&
            configSize.TryGetInt64(out var configBytes))
        {
            bytes += configBytes;
        }

        if (!root.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array)
        {
            error = "manifest 里没有 layers（可能是 schema 1 的老格式）";
            return false;
        }

        var counted = 0;
        foreach (var layer in layers.EnumerateArray())
        {
            if (!layer.TryGetProperty("size", out var size) || !size.TryGetInt64(out var layerBytes)) continue;
            bytes += layerBytes;
            counted++;
        }

        if (counted == 0)
        {
            error = "manifest 里的层没有可用的 size";
            return false;
        }

        return true;
    }

    // ---------------- 匿名令牌 ----------------

    /// <summary>
    /// 解析令牌端点返回的 <c>{"token":"…","access_token":"…","expires_in":300}</c>。
    ///
    /// ⚠️ 两个字段名都要认：参考项目实测该响应里的字段名可能是 <c>access_token</c> 而不是
    /// <c>token</c>（Docker Hub 两个都给，但别的实现只给一个）。只认一个会得到
    /// "拿到了正文却没有令牌"这种最难查的失败。
    /// </summary>
    public static bool TryParseAuthToken(string? json, out string token, out string error)
    {
        token = string.Empty;
        error = string.Empty;

        if (!TryParse(json, out var root, out error)) return false;

        if (TryReadEnvelopeError(root, out var envelope))
        {
            error = envelope;
            return false;
        }

        foreach (var name in new[] { "token", "access_token" })
        {
            if (!root.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.String) continue;

            var value = (node.GetString() ?? string.Empty).Trim();
            if (value.Length > 0)
            {
                token = value;
                return true;
            }
        }

        error = "registry 的令牌响应里没有 token / access_token 字段";
        return false;
    }

    // ---------------- 错误信封 ----------------

    /// <summary>
    /// 一次失败的请求 → 能给用户看的一句话。
    ///
    /// 顺序有讲究：**先看正文里的错误信封**（最具体，而且实测 404 / 403 都带），
    /// 拿不到再退回 curl 自己的诊断（<c>curl: (6) Could not resolve host: …</c>）。
    /// </summary>
    public static string ExplainFailure(string? body, string? stderr)
    {
        if (TryParse(body, out var root, out _) && TryReadEnvelopeError(root, out var envelope))
        {
            return envelope;
        }

        var line = (stderr ?? string.Empty)
            .Split('\n')
            .Select(l => l.Trim())
            .LastOrDefault(l => l.Length > 0);

        return string.IsNullOrEmpty(line)
            ? "registry 没有响应（网络不通，或这个 registry 地址不对）"
            : $"连不上 registry：{line}";
    }

    /// <summary>
    /// 单个错误码 → 中文一句话。
    ///
    /// 前几个码是**实测见过**的，其余原样带出来 ——
    /// 编一句自己也不确定的中文，比把 registry 的原话给用户看更糟。
    /// </summary>
    public static string ExplainErrorCode(string? code, string? message, string? name)
    {
        var c = code ?? string.Empty;
        var m = message ?? string.Empty;
        var n = name ?? string.Empty;

        return c switch
        {
            "NAME_UNKNOWN" => n.Length == 0 ? "这个 registry 上没有这个仓库" : $"这个 registry 上没有仓库 {n}",
            "DENIED" or "UNAUTHORIZED" => $"registry 拒绝了这次请求（{c}）—— 这个仓库可能是私有的，需要填写用户名与密码",
            "MANIFEST_UNKNOWN" => "这个 tag 在 registry 上不存在（可能已被删除，换一个 tag 试试）",
            "TOOMANYREQUESTS" or "TOO_MANY_REQUESTS" => "registry 限流了，等一会儿再试（这个站点有按小时计数的限流）",
            "" => $"registry 报错：{m}",
            _ => $"registry 报错（{c}）：{m}"
        };
    }

    // ---------------- 内部 ----------------

    private static bool TryReadEnvelopeError(JsonElement root, out string message)
    {
        message = string.Empty;

        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array) return false;

        var first = errors.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object) return false;

        var code = first.TryGetProperty("code", out var c) ? c.GetString() : null;
        var text = first.TryGetProperty("message", out var m) ? m.GetString() : null;
        string? name = null;
        if (first.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.Object)
        {
            name = detail.TryGetProperty("name", out var n) ? n.GetString() : null;
        }

        message = ExplainErrorCode(code, text, name);
        return true;
    }

    private static bool TryParse(string? json, out JsonElement root, out string error)
    {
        root = default;
        error = string.Empty;

        // 前导 BOM 要剥掉：curl 的输出在某些站点上带它，JsonDocument 会直接抛
        var text = (json ?? string.Empty).TrimStart('\uFEFF').Trim();
        if (text.Length == 0)
        {
            error = "registry 返回了空内容";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException ex)
        {
            error = $"registry 返回的不是 JSON：{ex.Message}";
            return false;
        }
    }

    private static int CompareVersionKeys(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        var max = Math.Max(a.Count, b.Count);
        for (var i = 0; i < max; i++)
        {
            var left = i < a.Count ? a[i] : 0;
            var right = i < b.Count ? b[i] : 0;
            if (left != right) return left.CompareTo(right);
        }

        // 段数多的算大（3.24.1 > 3.24）；同段数时短的在前（3.24 在 3.24.0 前）
        return a.Count.CompareTo(b.Count);
    }
}
