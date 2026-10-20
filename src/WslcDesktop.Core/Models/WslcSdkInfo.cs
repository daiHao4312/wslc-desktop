namespace WslcDesktop.Core.Models;

/// <summary>官方 SDK（<c>WslcService</c>）的探测快照。不抛异常，失败也是一种结果。</summary>
public record WslcSdkInfo
{
    public bool Available { get; init; }

    /// <summary>会话管理器版本（<c>3.0.3</c>）；取不到为 null。</summary>
    public string? SessionManagerVersion { get; init; }

    public IReadOnlyList<string> MissingComponents { get; init; } = Array.Empty<string>();

    /// <summary>不可用原因（引擎缺失 / 组件不全 / 投影异常）。</summary>
    public string? Error { get; init; }
}
