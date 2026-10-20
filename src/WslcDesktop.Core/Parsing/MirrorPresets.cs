namespace WslcDesktop.Core.Parsing;

using WslcDesktop.Core.Models;

/// <summary>一个镜像站点的预设项（界面上的快捷入口 + 实测备注）。</summary>
public readonly record struct MirrorSite(string Host, string Note, bool CanListTags);

/// <summary>
/// 镜像加速站与常用镜像的预设。
///
/// 表里的可用性是**真机实测**结论（2026-10-13，来源：参考项目 wslc-panel 的
/// <c>crates/wslc-core/src/registry.rs</c> 模块说明），不是从文档抄的 ——
/// 界面上快捷按钮点下去就 404 是最难解释的一种失败。
///
/// <code>
/// | 站点                    | tags/list | manifests/&lt;tag&gt;              |
/// |-------------------------|-----------|-------------------------------|
/// | docker.1panel.live      | 200       | 200（免认证）                 |
/// | docker.1ms.run          | 200       | 401 → 要它自己域名下的 token  |
/// | docker.m.daocloud.io    | 401       | 401（realm 在 m.daocloud.io） |
/// | registry-1.docker.io    | 需连通外网 | 匿名令牌可读（realm=auth.docker.io）|
/// </code>
///
/// <c>registry-1.docker.io</c> 那一行的"000（不可达）"是参考项目在本机**无代理**时的实测；
/// 有代理的环境下它是可用的，而它对公开镜像只要求一个**匿名令牌**
/// （<c>auth.docker.io/token?service=registry.docker.io&amp;scope=repository:&lt;repo&gt;:pull</c>），
/// 所以本面板对它做了令牌挑战，能列 tag。
/// 其余需要认证的站点没有做通用挑战发现（见 <c>CurlRegistryClient</c> 的说明）。
///
/// ⚠️ 默认站仍然是 <c>docker.1panel.live</c>：它在**无代理**环境下也能用，
/// 而官方源在直连环境（国内）基本连不上。
/// </summary>
public static class MirrorPresets
{
    /// <summary>默认 registry（值定义在 <see cref="RegistryDefaults"/>，与偏好项的默认值同源）。</summary>
    public const string DefaultRegistry = RegistryDefaults.Host;

    /// <summary>curl 用的 UA。若干站点对默认 UA（<c>curl/x.y</c>）会直接拒。</summary>
    public const string BrowserUserAgent = RegistryDefaults.BrowserUserAgent;

    /// <summary>界面上的加速站快捷入口（实测备注写清楚，用户才不会白点）。</summary>
    public static readonly IReadOnlyList<MirrorSite> Sites = new[]
    {
        new MirrorSite("docker.1panel.live", "实测 tag 与 manifest 都免认证，默认用它，免代理可用", true),
        new MirrorSite(
            RegistryDefaults.DockerHubFriendlyHost,
            "Docker Hub 官方源；需要能连通外网（国内直连常超时，可开代理）。公开镜像用匿名令牌即可列 tag",
            true),
        new MirrorSite("docker.1ms.run", "能列 tag，但拉 manifest 需要它自己域名下的令牌", false),
        new MirrorSite("docker.m.daocloud.io", "需要认证（realm 在 m.daocloud.io）", false)
    };

    /// <summary>
    /// 常用镜像的快捷入口。
    ///
    /// 每一个都在 <c>docker.1panel.live</c> 上实测过 200。刻意**只放 10 个**：
    /// 铺满一屏的按钮墙已经被否过一次（那次的结论是改用下拉框）。
    /// </summary>
    public static readonly IReadOnlyList<string> CommonRepositories = new[]
    {
        "nginx",
        "redis",
        "mysql",
        "postgres",
        "mongo",
        "alpine",
        "ubuntu",
        "node",
        "python",
        "hello-world"
    };

    /// <summary>
    /// 预设站点的主机名。
    ///
    /// ⚠️ **必须是纯主机名**，不能是带后缀的展示文案（如 <c>docker.io（可列 tag）</c>）：
    /// 下拉项的字符串就是取值，一旦掺进全角括号，`IsValidHost` 会失败，
    /// 界面就会一路回落到默认源 —— 症状是"选哪个都变成 docker.1panel.live"（踩过一次）。
    /// 每个站点能不能列 tag 由 <see cref="MirrorSite.CanListTags"/> 表达，
    /// 显示在该行行的备注里。
    ///
    /// ⚠️ 这里只有**内置**站点。界面上的候选（内置 + 自定义、去掉停用的）由
    /// <c>RegistrySourceService.EnabledHosts()</c> 给出，不要自己拼。
    /// </summary>
    public static IReadOnlyList<string> Hosts { get; } = Sites.Select(s => s.Host).ToArray();

    /// <summary>这个主机名是不是内置站点（用户自己加的源返回 false）。</summary>
    public static bool IsPreset(string? host) => Find(host) is not null;

    /// <summary>
    /// 按主机名找一个预设；用户自己填的地址返回 null（说明它不在已知表里）。
    ///
    /// 比较走 <see cref="RegistryInput.CanonicalHost"/>：这样 <c>docker.io</c> 与
    /// <c>registry-1.docker.io</c> 会命中同一条，用户填哪个都能拿到正确的备注与"能不能列 tag"。
    /// </summary>
    public static MirrorSite? Find(string? host)
    {
        var canonical = RegistryInput.CanonicalHost(host);
        foreach (var site in Sites)
        {
            if (RegistryInput.CanonicalHost(site.Host) == canonical) return site;
        }

        return null;
    }

    /// <summary>
    /// 本机架构在 registry 里的写法。
    ///
    /// <c>arm64</c> 原样，其余一律当 <c>amd64</c> —— registry 只有这两种主流值，
    /// 把 x86 / arm 之类翻译成错的反而更难查。
    /// </summary>
    public static string CurrentArchitecture =>
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            == System.Runtime.InteropServices.Architecture.Arm64
                ? "arm64"
                : "amd64";
}
