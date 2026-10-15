namespace WSLC.Panel.Core.Models;

public record PortMappingModel
{
    /// <summary>引擎未给出绑定地址时的兜底值：等价于"监听所有网卡"。</summary>
    public const string WildcardIp = "0.0.0.0";

    /// <summary>
    /// 主机侧绑定地址。**原样保留引擎给的值**（0.0.0.0 就是 0.0.0.0），
    /// 不再改写成 127.0.0.1——详情页据此展示，改写会让用户看到与 `wslc ps` 不符的 IP。
    /// </summary>
    public string HostIp { get; init; } = WildcardIp;

    public int HostPort { get; init; }
    public int ContainerPort { get; init; }
    public string Protocol { get; init; } = "tcp";

    /// <summary>端口列文案：0.0.0.0:3306→3306</summary>
    public string ShortDisplay => $"{HostIp}:{HostPort}→{ContainerPort}";

    /// <summary>完整写法（详情页）：0.0.0.0:3306-&gt;3306/tcp</summary>
    public string DisplayString => $"{HostIp}:{HostPort}->{ContainerPort}/{Protocol}";

    /// <summary>通配地址（0.0.0.0 / ::）或回环地址（127.x / ::1）都不能直接当 URL 主机名用。</summary>
    public bool IsWildcardOrLoopback => IsWildcardAddress(HostIp) || IsLoopbackAddress(HostIp);

    /// <summary>浏览器可访问的主机名：通配/回环走 localhost，其余用引擎给出的真实 IP。</summary>
    public string BrowserHost => IsWildcardOrLoopback ? "localhost" : HostIp;

    public string BrowserUrl => $"http://{BrowserHost}:{HostPort}";

    /// <summary>是否值得用浏览器打开（tcp 且有主机端口）。</summary>
    public bool IsBrowsable => HostPort > 0 && Protocol.Equals("tcp", StringComparison.OrdinalIgnoreCase);

    public static bool IsWildcardAddress(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return true;
        var trimmed = ip.Trim();
        return trimmed is "0.0.0.0" or "::" or "*" or "[::]" or "[::0]";
    }

    public static bool IsLoopbackAddress(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;
        var trimmed = ip.Trim();
        return trimmed.StartsWith("127.", StringComparison.Ordinal)
            || trimmed is "::1" or "localhost" or "[::1]";
    }
}
