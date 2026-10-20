namespace WslcDesktop.Core.Models;

/// <summary>添加发行版的来源。装法完全不同，靠来源分岔。</summary>
public enum DistroInstallSource
{
    /// <summary>tar / tar.gz / tgz / tar.xz → <c>wsl --import</c></summary>
    LocalArchive,

    /// <summary>.vhdx → <c>wsl --import --vhd</c></summary>
    LocalVhd,

    /// <summary>.wsl 打包文件 → <c>wsl --install --from-file</c></summary>
    WslBundle,

    /// <summary>在线清单（<c>wsl --list --online</c>）→ <c>wsl --install -d</c></summary>
    Online
}

/// <summary>在线可安装的发行版（<c>wsl --list --online</c> 的一行）。</summary>
public record OnlineDistroModel
{
    /// <summary>安装时用的名字（如 <c>Ubuntu-24.04</c>）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>友好名称（如 <c>Ubuntu 24.04 LTS</c>）。</summary>
    public string FriendlyName { get; init; } = string.Empty;

    public string Display => string.IsNullOrWhiteSpace(FriendlyName)
        ? Name
        : $"{FriendlyName}（{Name}）";
}

/// <summary>一次添加发行版的输入。</summary>
public record DistroInstallRequest
{
    public DistroInstallSource Source { get; init; } = DistroInstallSource.LocalArchive;

    /// <summary>本地安装包路径（前三种来源用）。</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>发行版名。本地导入必填；在线安装由引擎决定，用 <see cref="OnlineName"/>。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>安装目录。本地导入必填；在线安装可留空（用引擎默认位置）。</summary>
    public string InstallDirectory { get; init; } = string.Empty;

    /// <summary>在线清单里的名字（<see cref="DistroInstallSource.Online"/> 用）。</summary>
    public string OnlineName { get; init; } = string.Empty;

    /// <summary>WSL 版本（1 或 2），仅本地导入时作为 <c>--version</c> 生效。</summary>
    public int Version { get; init; } = 2;
}

/// <summary>
/// 装前检查结果 + 等效命令预览。
///
/// <see cref="Arguments"/> 同时服务"界面预览"与"真实执行"——和创建容器页一样的纪律，
/// 两处共用一份拼装逻辑，预览与执行不会漂移。
/// </summary>
public record DistroInstallPlan
{
    public bool IsValid { get; init; }

    /// <summary>不通过的原因（可读中文），逐条展示给用户。</summary>
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();

    /// <summary>装法说明（如"导入 tar 归档"）。</summary>
    public string Mode { get; init; } = string.Empty;

    /// <summary>传给 wsl.exe 的参数串。</summary>
    public string Arguments { get; init; } = string.Empty;

    /// <summary>界面上展示的完整命令（含程序名）。</summary>
    public string CommandLine { get; init; } = string.Empty;

    /// <summary>将要执行的步骤预览。装前让用户知道会发生什么。</summary>
    public IReadOnlyList<string> Steps { get; init; } = Array.Empty<string>();
}

/// <summary>添加发行版的执行结果。</summary>
public record DistroInstallResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string CommandLine { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
}
