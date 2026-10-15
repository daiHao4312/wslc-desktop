namespace WslcDesktop.Core.Models;

public record VolumeModel
{
    public string Name { get; init; } = string.Empty;
    public string Driver { get; init; } = "local";
    public string Scope { get; init; } = "local";
    public string Size { get; init; } = string.Empty;
    public string Mountpoint { get; init; } = string.Empty;
    public string Labels { get; init; } = string.Empty;
    public int ContainerCount { get; init; }

    /// <summary>匿名卷（wslc 会在标签里标 com.docker.volume.anonymous）。清理时优先考虑。</summary>
    public bool IsAnonymous => Labels.Contains("com.docker.volume.anonymous", StringComparison.OrdinalIgnoreCase);

    /// <summary>名称过长时用于列表显示（匿名卷名是 64 位摘要）。</summary>
    public string ShortName => Name.Length <= 12 ? Name : string.Concat(Name.AsSpan(0, 12), "…");

    public string DisplayLabel => IsAnonymous ? "匿名卷" : "命名卷";

    /// <summary>wslc 通常返回 "N/A"，此时显示"未知"而不是把 N/A 丢给用户。</summary>
    public string SizeDisplay => string.IsNullOrWhiteSpace(Size) ? "未知" : Size;
}
