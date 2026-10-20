using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Parsing;

/// <summary>
/// 镜像仓库（Registry V2）的输入归一化、地址校验与 URL 拼装。
///
/// 这一层同时是<b>体验</b>和<b>安全边界</b>：返回值会拼进 URL，再作为裸 argv 交给
/// <c>curl.exe</c>。所以校验只在这一处做，别处不许再拼。
///
/// 归一化规则与实测依据（样本来自参考项目在 <c>docker.1panel.live</c> 上的真机验证）：
/// <code>
///   alpine                                    → library/alpine
///   NGINX                                     → library/nginx（仓库名必须小写）
///   library/nginx                             → 原样
///   grafana/grafana                           → 原样（非 library/ 的命名空间合法）
///   docker.1panel.live/library/nginx          → host=docker.1panel.live, repo=library/nginx
///   nginx:1.27                                → (library/nginx, tag=1.27)
///   https://docker.1panel.live/v2/library/alpine/tags/list  → 同上（浏览器里粘来的形状）
/// </code>
///
/// ⚠️ 两条不看实现就会踩的坑：
/// <list type="number">
/// <item><b>短名必须我们补 <c>library/</c></b>，不能指望 registry：实测
/// <c>docker.1ms.run/v2/alpine/tags/list</c> 会自己补，而
/// <c>docker.1panel.live/v2/alpine/tags/list</c> 直接 404 <c>NAME_UNKNOWN</c>。</item>
/// <item><b>找 tag 只能看最后一段的冒号</b>，否则 <c>registry:5000/x</c> 里的端口号会被当成 tag。</item>
/// </list>
/// </summary>
public static class RegistryInput
{
    /// <summary>一次 manifest 请求带的 <c>Accept</c>；顺带让 registry 别回 schema 1 的老格式。</summary>
    public const string ManifestAccept =
        "application/vnd.docker.distribution.manifest.v2+json," +
        "application/vnd.oci.image.manifest.v1+json," +
        "application/vnd.oci.image.index.v1+json," +
        "application/vnd.docker.distribution.manifest.list.v2+json";

    /// <summary>
    /// 把用户在输入框里写的东西拆成（host, 仓库名, 可选 tag）。
    ///
    /// <paramref name="defaultHost"/> 是用户没写 registry 时用的站点——
    /// 这条就是 <c>AppPreferences.DefaultRegistry</c> 真正生效的地方。
    /// </summary>
    public static bool TrySplitInput(
        string? input,
        string defaultHost,
        out RegistryReference reference,
        out string error)
    {
        reference = new RegistryReference();
        error = string.Empty;

        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            error = "镜像名不能为空";
            return false;
        }

        if (text.Contains('@'))
        {
            error = "不支持按摘要（@sha256:…）拉取，请填写 tag";
            return false;
        }

        // 用户很可能把整个 URL 粘进来，先把协议头与接口路径剥掉。
        // ⚠️ 顺序要紧：`/v2/` 在主机名**之后**，先剥主机名就找不到它了。
        foreach (var prefix in new[] { "https://", "http://" })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..];
                break;
            }
        }

        var v2At = text.IndexOf("/v2/", StringComparison.Ordinal);
        if (v2At >= 0)
        {
            text = text[(v2At + "/v2/".Length)..];
        }
        else if (text.StartsWith("v2/", StringComparison.OrdinalIgnoreCase))
        {
            text = text[3..];
        }

        // 尾部的接口路径也剥掉：`/tags/list`，或 `/manifests/<ref>`（<ref> 就是 tag）
        string? tagFromPath = null;
        if (text.EndsWith("/tags/list", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^"/tags/list".Length];
        }
        else
        {
            var manifestsAt = text.IndexOf("/manifests/", StringComparison.Ordinal);
            if (manifestsAt >= 0)
            {
                var pathTag = text[(manifestsAt + "/manifests/".Length)..].Trim('/');
                if (pathTag.Length > 0) tagFromPath = pathTag;
                text = text[..manifestsAt];
            }
        }

        // 以最后一个 '/' 为界找 tag：只有冒号在**最后一段**里才算 tag
        var lastSlash = text.LastIndexOf('/');
        var name = text;
        string? tag = null;
        var lastColon = text.LastIndexOf(':');
        if (lastColon >= 0 && (lastSlash < 0 || lastColon > lastSlash))
        {
            name = text[..lastColon];
            var raw = text[(lastColon + 1)..].Trim();
            if (raw.Length == 0)
            {
                error = "冒号后面没有 tag";
                return false;
            }

            tag = raw;
        }

        name = name.Trim('/');
        if (name.Length == 0)
        {
            error = "镜像名不能为空";
            return false;
        }

        // 第一段带 `.` / `:`，或者就叫 localhost —— 按 Docker 的规矩那是 registry 地址
        var host = NormalizeHost(defaultHost);
        var firstSlash = name.IndexOf('/');
        if (firstSlash > 0)
        {
            var first = name[..firstSlash];
            if (first.Contains('.') || first.Contains(':') || first.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                host = NormalizeHost(first);
                name = name[(firstSlash + 1)..];
            }
        }

        // ⚠️ 这里**只**做形状归一化，**不做别名转换**：
        // 别名转换（docker.io → registry-1.docker.io）只在发 HTTP 的那一刻做
        // （见 TagsUrl / ManifestUrl / RegistryDefaults.AnonymousTokenEndpointFor）。
        // 理由是用户视角：他填/选的是 docker.io，界面上、拉取引用里就该一直是 docker.io，
        // 中途改成内部 API 端点会让人以为选错了。
        host = NormalizeHost(host);

        var repository = name.ToLowerInvariant();
        if (repository.Length == 0)
        {
            error = "镜像名不能为空";
            return false;
        }

        // 单段（`alpine`）按官方镜像算 —— Docker Hub 的既定约定，也是 1panel 这类站的前提
        if (!repository.Contains('/')) repository = $"library/{repository}";

        if (!IsValidRepository(repository))
        {
            error = $"仓库名不合法：{repository}（只允许小写字母、数字与 . _ - / ，且每段首尾必须是字母或数字）";
            return false;
        }

        if (!IsValidHost(host))
        {
            error = $"registry 地址不合法：{host}";
            return false;
        }

        // 路径里带出来的 tag 优先（两条路只可能有一条命中，这里只做合并）
        tag ??= tagFromPath;
        if (tag is not null && !IsValidReference(tag))
        {
            error = $"tag 不合法：{tag}";
            return false;
        }

        reference = new RegistryReference { Host = host, Repository = repository, Tag = tag };
        return true;
    }

    /// <summary>
    /// 仓库名合法吗（短名要先过 <see cref="TrySplitInput"/> 补成 <c>library/alpine</c>）。
    ///
    /// 规则是 Docker 那套的**安全子集**：<c>/</c> 分段，每段由小写字母数字与 <c>.</c> <c>_</c> <c>-</c>
    /// 组成且首尾必须是字母数字。顺手挡掉 <c>..</c>（路径穿越）、空段、<c>%</c>（URL 编码）、
    /// <c>?</c> <c>#</c>（查询与片段）以及一切非 ASCII。
    /// </summary>
    public static bool IsValidRepository(string? repository)
    {
        if (string.IsNullOrEmpty(repository) || repository.Length > 255) return false;
        if (repository.StartsWith('/') || repository.EndsWith('/')) return false;

        return repository.Split('/').All(IsValidSegment);
    }

    private static bool IsValidSegment(string segment)
    {
        if (segment.Length == 0) return false;

        var first = segment[0];
        var last = segment[^1];
        if (!IsAsciiAlphanumeric(first) || !IsAsciiAlphanumeric(last)) return false;

        // ⚠️ 只能用**小写**字母：仓库名是大小写敏感的，Docker 的规则里不允许大写
        // （tag 才允许）。以前这里写成了 IsAsciiAlphanumeric，把 `Library/alpine` 也放过去了。
        return segment.All(c => IsAsciiLowerLetter(c) || IsAsciiDigit(c) || c is '.' or '_' or '-');
    }

    /// <summary>
    /// tag / 摘要合法吗。tag <b>可以有大写</b>（与仓库名不同）：字符集
    /// <c>[A-Za-z0-9_][A-Za-z0-9._-]{0,127}</c>；摘要走 <c>sha256:&lt;十六进制&gt;</c> 那形状。
    /// </summary>
    public static bool IsValidReference(string? reference)
    {
        if (string.IsNullOrEmpty(reference) || reference.Length > 256) return false;

        var colon = reference.IndexOf(':');
        if (colon > 0)
        {
            var algorithm = reference[..colon];
            var hex = reference[(colon + 1)..];
            var algorithmOk = algorithm.Length > 0
                && algorithm.All(c => IsAsciiLowerLetter(c) || IsAsciiAlphanumeric(c) || c is '_' or '-' or '+');

            if (algorithmOk && hex.Length >= 32 && hex.All(Uri.IsHexDigit)) return true;
            // 冒号出现在 tag 里不算摘要 —— 落到下面按 tag 再判一次
        }

        if (reference.Length > 128) return false;
        var first = reference[0];
        if (!IsAsciiAlphanumeric(first) && first != '_') return false;

        return reference.All(c => IsAsciiAlphanumeric(c) || c is '_' or '.' or '-');
    }

    /// <summary>
    /// registry 主机名（可带端口）合法吗。
    ///
    /// 同样是安全边界：host 由用户可编辑，会拼进 URL 再交给 curl.exe。
    /// 刻意不收 <c>/</c> <c>?</c> <c>#</c> <c>%</c> 空格与一切非 ASCII。
    /// 端口必须是 1–65535 的数字 —— <c>host:port</c> 这种占位符要在这里挡住，
    /// 否则它会变成一个"永远超时"的地址，用户完全不知道为什么。
    /// </summary>
    public static bool IsValidHost(string? host)
    {
        var text = (host ?? string.Empty).Trim();
        if (text.Length == 0 || text.Length > 253) return false;

        // rsplit：IPv6 字面量不在支持范围内，用最后一个冒号更不容易误判
        var colon = text.LastIndexOf(':');
        var name = colon >= 0 ? text[..colon] : text;
        if (colon >= 0)
        {
            if (!int.TryParse(text[(colon + 1)..], out var port) || port is < 1 or > 65535) return false;
        }

        if (name.Length == 0) return false;

        return name.Split('.').All(label =>
            label.Length is > 0 and <= 63
            && IsAsciiAlphanumeric(label[0])
            && IsAsciiAlphanumeric(label[^1])
            && label.All(c => IsAsciiAlphanumeric(c) || c == '-'));
    }

    /// <summary>把用户填的 registry 收拾成纯主机名：去空白、去协议头、去结尾斜杠、统一小写。</summary>
    public static string NormalizeHost(string? input)
    {
        var text = (input ?? string.Empty).Trim();

        foreach (var prefix in new[] { "https://", "http://" })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..];
                break;
            }
        }

        return text.TrimEnd('/').Trim().ToLowerInvariant();
    }

    /// <summary>
    /// 归一化 + 别名统一：<c>docker.io</c> / <c>index.docker.io</c> → <c>registry-1.docker.io</c>。
    ///
    /// 它们本来就是同一个 registry，只是 API 只在后者上。用户按习惯填 <c>docker.io</c> 时
    /// 不统一的话 <c>/v2/</c> 会打到网站而不是 registry。
    /// </summary>
    public static string CanonicalHost(string? input) => RegistryDefaults.CanonicalHost(NormalizeHost(input));

    /// <summary>
    /// 校验一个**本地镜像引用**（<c>wslc tag</c> 的源与目标）。
    ///
    /// 与 <see cref="TrySplitInput"/> 的区别：**不补 <c>library/</c>**。
    /// 重命名的目标往往是 <c>my-alpine:3.20</c> 这种本地短名，补了会变成另一个名字。
    /// registry 前缀仍然是可选的。
    /// </summary>
    public static bool TryValidateTagReference(string? input, out string error)
    {
        error = string.Empty;

        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            error = "引用不能为空";
            return false;
        }

        if (text.Any(c => char.IsWhiteSpace(c) || c is '@' or '?' or '#' or '%' or '\\'))
        {
            error = $"引用里不能有空格或 @ ? # % \\ 这些字符：{text}";
            return false;
        }

        var name = text;
        string? tag = null;
        var lastSlash = name.LastIndexOf('/');
        var lastColon = name.LastIndexOf(':');
        if (lastColon > 0 && (lastSlash < 0 || lastColon > lastSlash))
        {
            name = text[..lastColon];
            tag = text[(lastColon + 1)..];
            if (tag.Length == 0)
            {
                error = "冒号后面没有 tag";
                return false;
            }
        }

        name = name.Trim('/');
        if (name.Length == 0)
        {
            error = "引用不能为空";
            return false;
        }

        // 剥掉可选的 registry 前缀（第一段带 . 或 :，或就叫 localhost）
        var firstSlash = name.IndexOf('/');
        if (firstSlash > 0)
        {
            var first = name[..firstSlash];
            if (first.Contains('.') || first.Contains(':') || first.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsValidHost(first))
                {
                    error = $"registry 地址不合法：{first}";
                    return false;
                }

                name = name[(firstSlash + 1)..];
            }
        }

        // ⚠️ 这里**不**做 ToLower：仓库名必须是小写，`MyImage` 这种要如实报"不合法"。
        // 拉取路径（TrySplitInput）才会把用户输入统一小写，因为那是在"帮用户补全"；
        // 重命名是把一个具体引用写进引擎，改写了反而不是用户要的。
        if (!IsValidRepository(name))
        {
            error = $"仓库名不合法：{name}（只允许小写字母、数字与 . _ - / ，且每段首尾必须是字母或数字）";
            return false;
        }

        if (tag is not null && !IsValidReference(tag))
        {
            error = $"tag 不合法：{tag}";
            return false;
        }

        return true;
    }

    /// <summary><c>GET /v2/&lt;repo&gt;/tags/list</c>。调用方必须先过 <see cref="IsValidRepository"/>。</summary>
    public static string TagsUrl(string host, string repository)
        => $"https://{CanonicalHost(host)}/v2/{repository}/tags/list";

    /// <summary><c>GET /v2/&lt;repo&gt;/manifests/&lt;ref&gt;</c>。调用方必须先过 <see cref="IsValidReference"/>。</summary>
    public static string ManifestUrl(string host, string repository, string reference)
        => $"https://{CanonicalHost(host)}/v2/{repository}/manifests/{reference}";

    /// <summary>
    /// <c>curl.exe</c> 取 JSON 的参数。
    ///
    /// <c>--fail-with-body</c> 是关键：HTTP 出错时**正文照样打出来**，
    /// 所以"失败了"和"为什么失败"能同时拿到，不用去猜状态码
    /// （registry 的错误信封在 404 / 403 上都会给）。
    /// </summary>
    public static string CurlJsonArguments(string url, TimeSpan? maxTime = null, string? bearerToken = null)
    {
        var timeout = (int)(maxTime ?? TimeSpan.FromSeconds(20)).TotalSeconds;
        var parts = new List<string>
        {
            "-sS",
            "--fail-with-body",
            $"--max-time {timeout}",
            "--header", WslcCommandBuilder.Quote($"Accept: {ManifestAccept}")
        };

        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            parts.Add("--header");
            parts.Add(WslcCommandBuilder.Quote($"Authorization: Bearer {bearerToken}"));
        }

        parts.Add("--user-agent");
        parts.Add(WslcCommandBuilder.Quote(MirrorPresets.BrowserUserAgent));
        parts.Add(WslcCommandBuilder.Quote(url));

        return string.Join(' ', parts);
    }

    private static bool IsAsciiAlphanumeric(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static bool IsAsciiLowerLetter(char c) => c is >= 'a' and <= 'z';

    private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';
}
