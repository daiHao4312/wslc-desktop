namespace WSLC.Panel.Core.Models;

public record NetworkModel
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Driver { get; init; } = string.Empty;
    public string Scope { get; init; } = "local";
    public bool IPv4 { get; init; }
    public bool IPv6 { get; init; }
    public bool Internal { get; init; }
    public string CreatedAt { get; init; } = string.Empty;

    /// <summary>内置网络（bridge/host/none）由引擎管理，不允许删除。</summary>
    public bool IsBuiltIn => Name is "bridge" or "host" or "none";

    public bool IsRemovable => !IsBuiltIn;

    public string IpSupportDisplay
    {
        get
        {
            var parts = new List<string>();
            if (IPv4) parts.Add("IPv4");
            if (IPv6) parts.Add("IPv6");
            if (Internal) parts.Add("内部");

            return parts.Count == 0 ? "—" : string.Join(" / ", parts);
        }
    }

    public string RemoveHint => IsBuiltIn
        ? $"内置网络 {Name} 由引擎管理，不能删除"
        : $"删除网络 {Name}（仍有容器连接时会失败）";
}
