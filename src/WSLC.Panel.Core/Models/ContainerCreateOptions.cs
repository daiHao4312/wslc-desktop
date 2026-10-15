namespace WSLC.Panel.Core.Models;

/// <summary>创建容器时用户填写的参数（对应 <c>wslc run</c> 的常用旗标）。</summary>
public record ContainerCreateOptions
{
    public string Image { get; init; } = string.Empty;
    public string? Name { get; init; }

    /// <summary>覆盖镜像默认命令（可选）。</summary>
    public string? Command { get; init; }

    /// <summary>后台运行（默认 true，与面板交互预期一致）。</summary>
    public bool Detach { get; init; } = true;

    /// <summary>端口映射，形如 "8080:80"（主机端口:容器端口）。</summary>
    public IReadOnlyList<string> Ports { get; init; } = Array.Empty<string>();

    /// <summary>环境变量，形如 "KEY=VALUE"。</summary>
    public IReadOnlyList<string> EnvironmentVariables { get; init; } = Array.Empty<string>();

    /// <summary>卷挂载，形如 "volName:/data" 或 "C:\data:/data"。</summary>
    public IReadOnlyList<string> Volumes { get; init; } = Array.Empty<string>();

    public string? Network { get; init; }

    /// <summary>内存限制，形如 "512M" / "1G"。</summary>
    public string? Memory { get; init; }

    /// <summary>CPU 数，形如 "0.5" / "2"。</summary>
    public string? Cpus { get; init; }

    public string? Workdir { get; init; }
    public string? Hostname { get; init; }

    /// <summary>镜像拉取策略：always | missing | never。</summary>
    public string PullPolicy { get; init; } = "missing";

    /// <summary>停止后自动删除（--rm）。</summary>
    public bool RemoveOnExit { get; init; }
}
