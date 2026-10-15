using Microsoft.WSL.Containers;
using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Core.Services;

/// <summary>
/// 组合通道：以 CLI 为主通道执行，用官方 C# API 补充只有它才知道的信息
/// （组件缺失检测、会话管理器版本）。这样官方 API 覆盖面的变化不会打断面板主流程。
/// </summary>
public class CompositeWslcClient : IWslcClient
{
    private readonly CliWslcClient _cli;

    public CompositeWslcClient(CliWslcClient? cli = null)
    {
        _cli = cli ?? new CliWslcClient();
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
        var containers = await _cli.GetContainersAsync(all, cancellationToken);
        if (containers.Count == 0) return containers;

        // 把 stats 按 ID 前缀匹配合并进容器行（stats 输出 64 位 ID，list 是 12 位）
        try
        {
            var stats = await _cli.GetContainerStatsAsync(cancellationToken);
            if (stats.Count > 0)
            {
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
        }
        catch
        {
            // stats 失败不影响列表
        }

        return containers;
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

    public Task<ExecResult> ExecAsync(string idOrName, string command, CancellationToken cancellationToken = default)
        => _cli.ExecAsync(idOrName, command, cancellationToken);

    public Task<PullResult> PullImageAsync(string imageUri, Action<PullProgress>? onProgress = null, CancellationToken cancellationToken = default)
        => _cli.PullImageAsync(imageUri, onProgress, cancellationToken);

    public Task<bool> DeleteImageAsync(string idOrName, bool force = false, CancellationToken cancellationToken = default)
        => _cli.DeleteImageAsync(idOrName, force, cancellationToken);

    public Task<ImageInspectInfo> InspectImageAsync(string reference, CancellationToken cancellationToken = default)
        => _cli.InspectImageAsync(reference, cancellationToken);

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
