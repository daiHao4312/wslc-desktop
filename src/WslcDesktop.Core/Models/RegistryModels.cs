namespace WslcDesktop.Core.Models;

/// <summary>
/// registry 相关的默认常量。
///
/// 放在 Models 而不是 <c>MirrorPresets</c>：偏好项（<see cref="AppPreferences.DefaultRegistry"/>）
/// 与预设表都要用它，常量放在被引用的那一侧才不会绕出反向依赖。
/// </summary>
public static class RegistryDefaults
{
    /// <summary>
    /// 默认 registry。
    ///
    /// ⚠️ 原来默认是 <c>docker.io</c>，而实测本机 <c>registry-1.docker.io</c>（以及
    /// <c>auth.docker.io</c> / <c>hub.docker.com</c>）**全部不可达**——照那个默认值拉取必然超时。
    /// 换成实测唯一一个 tag 与 manifest 都免认证的加速站。
    ///
    /// 官方源虽然也进了可选表，但**不能当默认**：它在国内直连环境下基本连不上。
    /// </summary>
    public const string Host = "docker.1panel.live";

    /// <summary>Docker Hub 真正的 registry API 端点（<c>docker.io</c> 只是它的友好名）。</summary>
    public const string DockerHubHost = "registry-1.docker.io";

    /// <summary>
    /// Docker Hub 对外**展示与拉取**用的名字。
    ///
    /// 和 <see cref="DockerHubHost"/> 分开是刻意的：<c>registry-1.docker.io</c> 是内部 API 端点，
    /// 用户认知与手敲的都是 <c>docker.io</c>。界面上把用户选的 <c>docker.io</c>
    /// 显示成 <c>registry-1.docker.io</c> 会让人以为选错了 —— 转换只该发生在发 HTTP 的那一刻。
    /// </summary>
    public const string DockerHubFriendlyHost = "docker.io";

    /// <summary>curl 用的 UA：若干站点对默认 UA（<c>curl/x.y</c>）会直接拒。</summary>
    public const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";

    /// <summary>Docker Hub 的几种写法。用户习惯写 <c>docker.io</c>，而 API 只在 <c>registry-1</c> 上。</summary>
    private static readonly string[] DockerHubAliases = { "docker.io", "index.docker.io", "registry-1.docker.io" };

    /// <summary>
    /// 把主机名收拾成真正要连的那个。
    ///
    /// <c>docker.io</c> / <c>index.docker.io</c> 与 <c>registry-1.docker.io</c> 是**同一个 registry**
    /// （前两个是友好名，后一个是 API 端点）；不统一的话，用户按习惯填 <c>docker.io</c> 时
    /// <c>/v2/</c> 会打到网站上而不是 registry 上。
    /// </summary>
    public static string CanonicalHost(string? host)
    {
        var text = (host ?? string.Empty).Trim();

        foreach (var prefix in new[] { "https://", "http://" })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..];
                break;
            }
        }

        text = text.TrimEnd('/').Trim().ToLowerInvariant();

        return DockerHubAliases.Contains(text, StringComparer.Ordinal) ? DockerHubHost : text;
    }

    /// <summary>
    /// 已知站点的**匿名令牌端点**（<c>{0}</c> 换成仓库名）；没有就返回 null。
    ///
    /// 只对**端点形状已知**的站点做令牌挑战。通用做法要先发一次请求、
    /// 从 <c>WWW-Authenticate</c> 头里解析 realm 再重试——多一轮往返，还要解析响应头。
    /// Docker Hub 的端点形状是固定的（<c>auth.docker.io/token?service=registry.docker.io&amp;scope=…</c>），
    /// 直接写死更稳，也少一次请求。
    /// </summary>
    public static string? AnonymousTokenEndpointFor(string? host)
        => CanonicalHost(host) == DockerHubHost
            ? "https://auth.docker.io/token?service=registry.docker.io&scope=repository:{0}:pull"
            : null;
}

/// <summary>一次可拉取的镜像引用（已归一化）。</summary>
public record RegistryReference
{
    public string Host { get; init; } = string.Empty;

    /// <summary>仓库名，短名已补成 <c>library/xxx</c>，且已小写。</summary>
    public string Repository { get; init; } = string.Empty;

    /// <summary>用户写出来的 tag；没写就是 null。</summary>
    public string? Tag { get; init; }

    /// <summary>
    /// 真正发 HTTP 请求时要连的端点（<c>docker.io</c> → <c>registry-1.docker.io</c>）。
    ///
    /// 只在"问 registry 有哪些 tag / 量体积"时用；<see cref="Host"/> 保留用户填的写法，
    /// 用于展示与拉取引用。
    /// </summary>
    public string ApiHost => RegistryDefaults.CanonicalHost(Host);

    /// <summary>交给 <c>wslc pull</c> 的完整引用。<paramref name="tagOverride"/> 用于界面上另选的 tag。</summary>
    public string ToPullReference(string? tagOverride = null)
        => $"{Host}/{Repository}:{tagOverride ?? Tag ?? "latest"}";

    public string Display => Tag is null ? $"{Host}/{Repository}" : $"{Host}/{Repository}:{Tag}";
}

/// <summary>tag 列表查询结果（<see cref="Tags"/> 已按"用户会想点的顺序"排好）。</summary>
public record RegistryTagsResult
{
    public bool Success { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public string Error { get; init; } = string.Empty;
}

/// <summary>
/// 「为某个镜像源准备令牌」的结果。
///
/// 与 <see cref="RegistryCredentialResult"/> 的区别：这一层还回答**要不要认证**——
/// 没存凭据时不是失败，而是"公开镜像，直接走匿名"。
/// </summary>
public sealed record RegistryTokenResult
{
    /// <summary>可直接用于 <c>RegistryAuth</c> / <c>Authorization: Bearer</c> 的令牌；公开镜像为 null。</summary>
    public string? Token { get; init; }

    /// <summary>令牌类型（<c>Token</c> / <c>Credentials</c>），只用于排错提示。</summary>
    public string TokenKind { get; init; } = string.Empty;

    /// <summary>这次用的是哪条凭据的用户名；null = 没存凭据。</summary>
    public string? Username { get; init; }

    /// <summary>
    /// 非空 = **有凭据但换不到令牌**（密码不对、API 通道不可用、凭据解不开）。
    /// 调用方看到它就应当停下，不要退回匿名重试。
    /// </summary>
    public string Error { get; init; } = string.Empty;

    public bool HasToken => !string.IsNullOrEmpty(Token);

    /// <summary>存了凭据而且换到了令牌。</summary>
    public bool UsedCredential => Username is not null && Error.Length == 0;
}

/// <summary>镜像体积查询结果（拉取区显示的"下载约 N MB"就是这个数）。</summary>
public record RegistrySizeResult
{
    public bool Success { get; init; }

    /// <summary>各层压缩后之和 + config，即实际要下载的字节数。</summary>
    public long Bytes { get; init; }

    public string Error { get; init; } = string.Empty;

    public string Display => Bytes switch
    {
        <= 0 => "未知",
        < 1024L * 1024 => $"{Bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{Bytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{Bytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
    };
}

/// <summary>
/// 私有仓库凭据换到的身份令牌。
///
/// 令牌由官方 <c>Session.Authenticate</c> 产出，直接喂给 <c>PullImageOptions.RegistryAuth</c>
/// 与 registry 的 <c>Authorization</c> 头。
///
/// ⚠️ **令牌本身不落盘**：它只在这一次操作的内存里活着。
/// 用户填的**用户名密码**则由设置页保存到 prefs.json —— 但存的是
/// DPAPI 密文（见 <see cref="RegistryCredential"/>），明文永不落盘。
/// </summary>
public record RegistryCredentialResult
{
    public bool Success { get; init; }

    /// <summary>可直接用于 <c>RegistryAuth</c> / <c>Authorization: Bearer</c> 的令牌串。</summary>
    public string Token { get; init; } = string.Empty;

    /// <summary>
    /// 令牌类型（<c>Token</c> / <c>Credentials</c> / <c>Unknown</c>）。
    ///
    /// 官方枚举 <c>IdentityTokenType</c> 里有 <c>Credentials</c> 这一档，说明令牌不一定是 Bearer token。
    /// 这里原样带出来只用于**排错提示**，不据此改变用法（拿到的串直接透传）。
    /// </summary>
    public string TokenKind { get; init; } = string.Empty;

    public string Error { get; init; } = string.Empty;
}
