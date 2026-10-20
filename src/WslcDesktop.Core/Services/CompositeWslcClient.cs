using Microsoft.WSL.Containers;
using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Core.Services;

/// <summary>
/// 组合通道（双通道路由）：
/// <list type="bullet">
/// <item><b>CLI 是主力</b>——列表类查询（容器/镜像/网络/卷/会话）CLI 的信息<b>比 API 更全</b>，
/// API 的 <c>Container</c> 只有 Id/State/InitProcess（没名称没端口）、<c>ImageInfo</c> 没有多仓库引用，
/// 整体切过去会让界面信息变少。</item>
/// <item><b>API 只补 CLI 拿不到的</b>——目前是镜像拉取的真实字节进度（外加它带来的私有仓库凭据）。</item>
/// <item><b>官方 API 补充系统信息</b>（组件缺失检测、会话管理器版本）。</item>
/// </list>
/// 这样官方 API 覆盖面的变化既不会打断面板主流程，也不会让界面丢信息。
/// </summary>
public class CompositeWslcClient : IWslcClient
{
    private readonly IWslcClient _cli;
    private readonly IWslcApiChannel? _apiChannel;

    public CompositeWslcClient(IWslcClient? cli = null, IWslcApiChannel? apiChannel = null)
    {
        _cli = cli ?? new CliWslcClient();
        _apiChannel = apiChannel;
    }

    public async Task<SystemInfoModel> GetSystemInfoAsync(CancellationToken cancellationToken = default)
    {
        var model = await _cli.GetSystemInfoAsync(cancellationToken);

        // 用官方 API 补充组件检测与版本（API/CLI 任一侧不可用都不影响整体可用性）
        try
        {
            var missingComponents = WslcService.GetMissingComponents();
            var missingList = missingComponents.Select(c => c.ToString()).ToList();
            var ver = WslcService.GetVersion();
            var serviceVerString = $"{ver.Major}.{ver.Minor}.{ver.Revision}";

            model = model with
            {
                MissingComponents = missingList,
                SessionManagerVersion = string.IsNullOrEmpty(model.SessionManagerVersion)
                    ? serviceVerString
                    : model.SessionManagerVersion
            };
        }
        catch
        {
            // 官方 API 不可用时保持 CLI 结果
        }

        return model;
    }

    public async Task<IReadOnlyList<ContainerModel>> GetContainersAsync(bool all = true, CancellationToken cancellationToken = default)
    {
        // ⚠️ list 与 stats 是**两次独立的进程调用**，必须并行发起。
        // 串行时列表要等两个 wslc 进程依次跑完 —— 用户看到的就是"进页面要等几秒才有内容"。
        var containersTask = _cli.GetContainersAsync(all, cancellationToken);
        var statsTask = TryGetStatsAsync(cancellationToken);

        var containers = await containersTask;
        if (containers.Count == 0) return containers;

        // 把 stats 按 ID 前缀匹配合并进容器行（stats 输出 64 位 ID，list 是 12 位）
        var stats = await statsTask;
        if (stats.Count == 0) return containers;

        var merged = new List<ContainerModel>(containers.Count);
        foreach (var c in containers)
        {
            var stat = stats.FirstOrDefault(s =>
                OutputSanitizer.IdMatches(s.ContainerId, c.Id) ||
                OutputSanitizer.IdMatches(s.ContainerId, c.FullId) ||
                string.Equals(s.Name, c.Name, StringComparison.OrdinalIgnoreCase));

            merged.Add(c with { Stats = stat });
        }

        return merged;
    }

    /// <summary>
    /// stats 拿不到不影响列表：这里统一吞掉异常（stats 失败 / 被取消都只是"没有 CPU 内存数据"）。
    /// 单独包一层是为了让上面两个任务**都有人 await** —— 否则先返回时另一路会变成未观察的异常。
    /// </summary>
    private async Task<IReadOnlyList<ContainerStatsModel>> TryGetStatsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _cli.GetContainerStatsAsync(cancellationToken);
        }
        catch
        {
            return Array.Empty<ContainerStatsModel>();
        }
    }

    public Task<IReadOnlyList<ContainerStatsModel>> GetContainerStatsAsync(CancellationToken cancellationToken = default)
        => _cli.GetContainerStatsAsync(cancellationToken);

    public Task<IReadOnlyList<ImageModel>> GetImagesAsync(CancellationToken cancellationToken = default)
        => _cli.GetImagesAsync(cancellationToken);

    public Task<IReadOnlyList<NetworkModel>> GetNetworksAsync(CancellationToken cancellationToken = default)
        => _cli.GetNetworksAsync(cancellationToken);

    public Task<IReadOnlyList<VolumeModel>> GetVolumesAsync(CancellationToken cancellationToken = default)
        => _cli.GetVolumesAsync(cancellationToken);

    public async Task<IReadOnlyList<SessionModel>> GetSessionsAsync(CancellationToken cancellationToken = default)
    {
        var sessions = await _cli.GetSessionsAsync(cancellationToken);

        // CLI 不给存储信息，按会话名扫 %LOCALAPPDATA%\wslc\sessions\<名>\storage.vhdx 补上占用
        return SessionStorageLocator.Enrich(sessions);
    }

    public Task<bool> StartContainerAsync(string idOrName, CancellationToken cancellationToken = default)
        => _cli.StartContainerAsync(idOrName, cancellationToken);

    public Task<bool> StopContainerAsync(string idOrName, int timeoutSeconds = 10, CancellationToken cancellationToken = default)
        => _cli.StopContainerAsync(idOrName, timeoutSeconds, cancellationToken);

    public Task<bool> RestartContainerAsync(string idOrName, CancellationToken cancellationToken = default)
        => _cli.RestartContainerAsync(idOrName, cancellationToken);

    public Task<bool> KillContainerAsync(string idOrName, CancellationToken cancellationToken = default)
        => _cli.KillContainerAsync(idOrName, cancellationToken);

    public Task<bool> DeleteContainerAsync(string idOrName, bool force = false, CancellationToken cancellationToken = default)
        => _cli.DeleteContainerAsync(idOrName, force, cancellationToken);

    public Task<CreateContainerResult> CreateContainerAsync(ContainerCreateOptions options, CancellationToken cancellationToken = default)
        => _cli.CreateContainerAsync(options, cancellationToken);

    public Task<string> InspectContainerAsync(string idOrName, CancellationToken cancellationToken = default)
        => _cli.InspectContainerAsync(idOrName, cancellationToken);

    public Task<string> GetContainerLogsAsync(string idOrName, int tail = 100, CancellationToken cancellationToken = default)
        => _cli.GetContainerLogsAsync(idOrName, tail, cancellationToken);

    /// <summary>跟随日志：API 通道没有这个能力（没有 logs 概念），固定走 CLI。</summary>
    public Task<ProcessExecutionResult> StreamContainerLogsAsync(
        string idOrName,
        Action<string> onOutputLine,
        Action<string>? onErrorLine = null,
        CancellationToken cancellationToken = default)
        => _cli.StreamContainerLogsAsync(idOrName, onOutputLine, onErrorLine, cancellationToken);

    public Task<ExecResult> ExecAsync(string idOrName, string command, CancellationToken cancellationToken = default)
        => _cli.ExecAsync(idOrName, command, cancellationToken);

    /// <summary>可停止的命令执行：API 的 Signal 用不上（会话被 CLI 独占），所以固定走 CLI 包装。</summary>
    public Task<ProcessExecutionResult> StreamTrackedCommandAsync(
        string idOrName,
        string command,
        Action<string> onOutputLine,
        CancellationToken cancellationToken = default)
        => _cli.StreamTrackedCommandAsync(idOrName, command, onOutputLine, cancellationToken);

    public Task<ExecResult> StopTrackedCommandAsync(string idOrName, CancellationToken cancellationToken = default)
        => _cli.StopTrackedCommandAsync(idOrName, cancellationToken);

    /// <summary>
    /// 可管理的容器内进程（M19）：**只有 API 通道能给信号**，所以固定走 API；
    /// 通道不可用时如实说明，由界面回落一次性执行。
    /// </summary>
    public Task<ApiProcessStartResult> TryStartContainerProcessAsync(
        string idOrName,
        string command,
        Action<string> onOutputLine,
        CancellationToken cancellationToken = default)
        => _apiChannel is null
            ? Task.FromResult(ApiProcessStartResult.Unavailable("官方 API 通道未注册"))
            : _apiChannel.TryStartProcessAsync(idOrName, command, onOutputLine, cancellationToken);

    public async Task<PullResult> PullImageAsync(
        string imageUri,
        string? registryAuth = null,
        Action<PullProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        // API 优先：这是当前唯一"API 严格强于 CLI"的能力——真实字节进度（CLI 只回状态关键字），
        // 顺带还是私有仓库令牌的唯一通路。
        // 返回 null 表示通道没接上（或尚未产生任何下载动作就失败），此时回落 CLI 是安全的。
        if (_apiChannel is not null)
        {
            var viaApi = await _apiChannel.TryPullImageAsync(
                imageUri,
                registryAuth,
                onProgress,
                cancellationToken);

            if (viaApi is not null) return viaApi;
        }

        // 回落 CLI：registryAuth 在那边用不了（见 CliWslcClient 的说明）
        return await _cli.PullImageAsync(imageUri, registryAuth, onProgress, cancellationToken);
    }

    public Task<bool> DeleteImageAsync(string idOrName, bool force = false, CancellationToken cancellationToken = default)
        => _cli.DeleteImageAsync(idOrName, force, cancellationToken);

    public Task<bool> TagImageAsync(string source, string target, CancellationToken cancellationToken = default)
        => _cli.TagImageAsync(source, target, cancellationToken);

    public Task<ImageInspectInfo> InspectImageAsync(string reference, CancellationToken cancellationToken = default)
        => _cli.InspectImageAsync(reference, cancellationToken);

    /// <summary>镜像导出：官方 API 没有归档能力（API覆盖矩阵里搜不到 save/load），固定走 CLI。</summary>
    public Task<ImageArchiveResult> ExportImagesAsync(
        IReadOnlyList<string> references,
        string outputPath,
        Action<ImageArchiveProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
        => _cli.ExportImagesAsync(references, outputPath, onProgress, cancellationToken);

    /// <summary>镜像导入：同上，只有 CLI 的 <c>load -i</c>；API 通道没有归档入口，不给它留口子。</summary>
    public Task<ImageArchiveResult> ImportImageArchiveAsync(
        string archivePath,
        Action<ImageArchiveProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
        => _cli.ImportImageArchiveAsync(archivePath, onProgress, cancellationToken);

    public Task<bool> CreateNetworkAsync(string name, string driver = "bridge", CancellationToken cancellationToken = default)
        => _cli.CreateNetworkAsync(name, driver, cancellationToken);

    public Task<bool> RemoveNetworkAsync(string idOrName, CancellationToken cancellationToken = default)
        => _cli.RemoveNetworkAsync(idOrName, cancellationToken);

    public Task<bool> CreateVolumeAsync(string name, CancellationToken cancellationToken = default)
        => _cli.CreateVolumeAsync(name, cancellationToken);

    public Task<bool> RemoveVolumeAsync(string name, bool force = false, CancellationToken cancellationToken = default)
        => _cli.RemoveVolumeAsync(name, force, cancellationToken);

    public Task<bool> PruneVolumesAsync(CancellationToken cancellationToken = default)
        => _cli.PruneVolumesAsync(cancellationToken);

    public Task<bool> TerminateSessionAsync(int sessionId, CancellationToken cancellationToken = default)
        => _cli.TerminateSessionAsync(sessionId, cancellationToken);

    public Task StreamEventsAsync(
        Action<WslcEvent> onEvent,
        DateTimeOffset? since = null,
        CancellationToken cancellationToken = default,
        Action? onSubscribed = null)
        => _cli.StreamEventsAsync(onEvent, since, cancellationToken, onSubscribed);
}
