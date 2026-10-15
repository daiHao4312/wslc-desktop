using WSLC.Panel.Core.Models;

namespace WSLC.Panel.Core.Abstractions;

public interface IWslcClient
{
    // ---------- 查询 ----------
    Task<SystemInfoModel> GetSystemInfoAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContainerModel>> GetContainersAsync(bool all = true, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContainerStatsModel>> GetContainerStatsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ImageModel>> GetImagesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NetworkModel>> GetNetworksAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VolumeModel>> GetVolumesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionModel>> GetSessionsAsync(CancellationToken cancellationToken = default);

    // ---------- 容器生命周期 ----------
    Task<bool> StartContainerAsync(string idOrName, CancellationToken cancellationToken = default);

    Task<bool> StopContainerAsync(string idOrName, int timeoutSeconds = 10, CancellationToken cancellationToken = default);

    Task<bool> RestartContainerAsync(string idOrName, CancellationToken cancellationToken = default);

    Task<bool> KillContainerAsync(string idOrName, CancellationToken cancellationToken = default);

    Task<bool> DeleteContainerAsync(string idOrName, bool force = false, CancellationToken cancellationToken = default);

    /// <summary>创建并（默认）后台启动容器。</summary>
    Task<CreateContainerResult> CreateContainerAsync(
        ContainerCreateOptions options,
        CancellationToken cancellationToken = default);

    // ---------- 详情 ----------
    Task<string> InspectContainerAsync(string idOrName, CancellationToken cancellationToken = default);

    Task<string> GetContainerLogsAsync(string idOrName, int tail = 100, CancellationToken cancellationToken = default);

    /// <summary>在容器内执行一次性命令（非交互），返回合并输出。</summary>
    Task<ExecResult> ExecAsync(string idOrName, string command, CancellationToken cancellationToken = default);

    // ---------- 镜像 ----------
    /// <summary>拉取镜像；<paramref name="onProgress"/> 会在每读到一行引擎输出时被调用。</summary>
    Task<PullResult> PullImageAsync(
        string imageUri,
        Action<PullProgress>? onProgress = null,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteImageAsync(string idOrName, bool force = false, CancellationToken cancellationToken = default);

    /// <summary>读取本地镜像声明的启动信息（端口/命令），用于创建容器表单预填。</summary>
    Task<ImageInspectInfo> InspectImageAsync(string reference, CancellationToken cancellationToken = default);

    // ---------- 网络 ----------
    Task<bool> CreateNetworkAsync(string name, string driver = "bridge", CancellationToken cancellationToken = default);

    Task<bool> RemoveNetworkAsync(string idOrName, CancellationToken cancellationToken = default);

    // ---------- 卷 ----------
    Task<bool> CreateVolumeAsync(string name, CancellationToken cancellationToken = default);

    Task<bool> RemoveVolumeAsync(string name, bool force = false, CancellationToken cancellationToken = default);

    Task<bool> PruneVolumesAsync(CancellationToken cancellationToken = default);

    // ---------- 会话 ----------
    /// <summary>终止会话（会连带停下该会话中运行的容器，但 VHD 数据不会删除）。</summary>
    Task<bool> TerminateSessionAsync(int sessionId, CancellationToken cancellationToken = default);

    // ---------- 事件流 ----------
    /// <summary>
    /// 流式订阅引擎事件。这是一个<b>长驻</b>调用：直到取消令牌触发或引擎进程退出才返回。
    ///
    /// 语义要点（实测）：不带 <paramref name="since"/> 时引擎只推<b>新</b>事件，不回放历史；
    /// 带 <paramref name="since"/> 时先把该时刻之后的事件补发一遍，因此可用于断线续接。
    /// 重连与去重由上层 <c>IEventStreamService</c> 负责，本方法只管一次订阅。
    /// </summary>
    /// <param name="onSubscribed">
    /// 订阅<b>已建立</b>（引擎侧进程起来了）时回调一次。用于区分"订阅健康、还没有事件"
    /// 与"连不上"——前者可能持续很久，界面不该一直显示"连接中"。
    /// </param>
    Task StreamEventsAsync(
        Action<WslcEvent> onEvent,
        DateTimeOffset? since = null,
        CancellationToken cancellationToken = default,
        Action? onSubscribed = null);
}
