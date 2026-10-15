namespace WSLC.Panel.Core.Models;

/// <summary>
/// 本地镜像声明的启动信息（来自 <c>wslc image inspect</c>）。
/// 用途：创建容器表单预填端口与命令，并告诉用户"这个镜像会跑什么"。
/// </summary>
public record ImageInspectInfo
{
    public bool Found { get; init; }

    /// <summary>镜像自述的默认命令（Cmd）。</summary>
    public IReadOnlyList<string> Command { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Entrypoint { get; init; } = Array.Empty<string>();

    /// <summary>镜像声明的暴露端口（容器侧端口号）。</summary>
    public IReadOnlyList<int> ExposedPorts { get; init; } = Array.Empty<int>();

    public IReadOnlyList<string> Environment { get; init; } = Array.Empty<string>();

    public string WorkingDir { get; init; } = string.Empty;

    public IReadOnlyList<string> RepoTags { get; init; } = Array.Empty<string>();

    public string Error { get; init; } = string.Empty;

    /// <summary>一句话描述这个镜像会跑什么（表单里当提示用）。</summary>
    public string StartupSummary
    {
        get
        {
            var parts = new List<string>();

            if (Entrypoint.Count > 0) parts.Add(string.Join(" ", Entrypoint));
            if (Command.Count > 0) parts.Add(string.Join(" ", Command));

            return parts.Count == 0 ? "（镜像未声明默认命令）" : string.Join(" ", parts);
        }
    }

    public string ExposedPortsSummary => ExposedPorts.Count == 0
        ? "镜像未声明端口"
        : "镜像声明端口：" + string.Join("、", ExposedPorts);
}
