using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Tests;

/// <summary>
/// <see cref="IWslcClient"/> 的测试替身：只有 <see cref="StreamEventsAsync"/> 有行为，
/// 其余成员返回空结果（事件流测试用不到）。
///
/// 之所以完整实现而不是用动态代理：接口是<b>编译期契约</b>，多一个成员就该让这里编译报错，
/// 提醒补上行为，而不是运行时才发现。
/// </summary>
internal sealed class FakeWslcClient : IWslcClient
{
    /// <summary>每次订阅的回调；实现方决定推什么、何时返回（返回 = 模拟引擎进程退出）。</summary>
    public Func<Action<WslcEvent>, DateTimeOffset?, CancellationToken, Task>? StreamBehavior { get; set; }

    /// <summary>按调用顺序记录收到的 <c>--since</c> 实参（null 表示没传）。</summary>
    public List<DateTimeOffset?> ReceivedSince { get; } = new();

    /// <summary>拉取行为（M14 的通道回落测试用）；未设置时返回空结果。</summary>
    public Func<string, Action<PullProgress>?, CancellationToken, Task<PullResult>>? PullBehavior { get; set; }

    /// <summary>按调用顺序记录被要求拉取的镜像引用。</summary>
    public List<string> PullRequests { get; } = new();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _concurrentCalls;

    public int MaxConcurrentCalls { get; private set; }

    public async Task StreamEventsAsync(
        Action<WslcEvent> onEvent,
        DateTimeOffset? since = null,
        CancellationToken cancellationToken = default,
        Action? onSubscribed = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ReceivedSince.Add(since);
            _concurrentCalls++;
            MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, _concurrentCalls);
        }
        finally
        {
            _gate.Release();
        }

        // 真实实现里"订阅进程起来了"就会回调一次；替身照做，
        // 否则测试永远覆盖不到"已订阅但还没有事件"这段空窗期。
        try
        {
            onSubscribed?.Invoke();
        }
        catch
        {
            // 回调异常不该影响建流，和真实运行器的行为保持一致
        }

        try
        {
            if (StreamBehavior != null)
            {
                await StreamBehavior(onEvent, since, cancellationToken);
            }
            else
            {
                // 默认行为：一直挂着，直到被取消（模拟"订阅正常、等待新事件"）
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _concurrentCalls);
        }
    }

    // ---------- 以下成员事件流测试用不到 ----------

    public Task<SystemInfoModel> GetSystemInfoAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new SystemInfoModel());

    public Task<IReadOnlyList<ContainerModel>> GetContainersAsync(bool all = true, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ContainerModel>>(Array.Empty<ContainerModel>());

    public Task<IReadOnlyList<ContainerStatsModel>> GetContainerStatsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ContainerStatsModel>>(Array.Empty<ContainerStatsModel>());

    public Task<IReadOnlyList<ImageModel>> GetImagesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ImageModel>>(Array.Empty<ImageModel>());

    public Task<IReadOnlyList<NetworkModel>> GetNetworksAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<NetworkModel>>(Array.Empty<NetworkModel>());

    public Task<IReadOnlyList<VolumeModel>> GetVolumesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<VolumeModel>>(Array.Empty<VolumeModel>());

    public Task<IReadOnlyList<SessionModel>> GetSessionsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SessionModel>>(Array.Empty<SessionModel>());

    public Task<bool> StartContainerAsync(string idOrName, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<bool> StopContainerAsync(string idOrName, int timeoutSeconds = 10, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<bool> RestartContainerAsync(string idOrName, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<bool> KillContainerAsync(string idOrName, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<bool> DeleteContainerAsync(string idOrName, bool force = false, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<CreateContainerResult> CreateContainerAsync(ContainerCreateOptions options, CancellationToken cancellationToken = default)
        => Task.FromResult(new CreateContainerResult { Success = true });

    public Task<string> InspectContainerAsync(string idOrName, CancellationToken cancellationToken = default)
        => Task.FromResult(string.Empty);

    public Task<string> GetContainerLogsAsync(string idOrName, int tail = 100, CancellationToken cancellationToken = default)
        => Task.FromResult(string.Empty);

    /// <summary>测试注入：模拟"跟随日志"的两路行流（out / err）与结束方式。</summary>
    public Func<string, Action<string>, Action<string>?, CancellationToken, Task<ProcessExecutionResult>>? LogStreamBehavior { get; set; }

    public Task<ProcessExecutionResult> StreamContainerLogsAsync(
        string idOrName,
        Action<string> onOutputLine,
        Action<string>? onErrorLine = null,
        CancellationToken cancellationToken = default)
        => LogStreamBehavior?.Invoke(idOrName, onOutputLine, onErrorLine, cancellationToken)
           ?? Task.FromResult(new ProcessExecutionResult(0, string.Empty, string.Empty, IsSuccess: true));

    public Task<ExecResult> ExecAsync(string idOrName, string command, CancellationToken cancellationToken = default)
        => Task.FromResult(new ExecResult());

    /// <summary>测试注入：模拟"可管理的容器内进程"（默认判为不可用，走一次性执行路径）。</summary>
    public Func<string, string, Action<string>, CancellationToken, Task<ApiProcessStartResult>>? StartProcessBehavior { get; set; }

    public Task<ApiProcessStartResult> TryStartContainerProcessAsync(
        string idOrName,
        string command,
        Action<string> onOutputLine,
        CancellationToken cancellationToken = default)
        => StartProcessBehavior?.Invoke(idOrName, command, onOutputLine, cancellationToken)
           ?? Task.FromResult(ApiProcessStartResult.Unavailable("测试替身：没有 API 通道"));

    /// <summary>测试注入：模拟"CLI 模式的可停止命令"。</summary>
    public Func<string, string, Action<string>, CancellationToken, Task<ProcessExecutionResult>>? TrackedCommandBehavior { get; set; }

    public int StopTrackedCalls { get; private set; }

    public Task<ProcessExecutionResult> StreamTrackedCommandAsync(
        string idOrName,
        string command,
        Action<string> onOutputLine,
        CancellationToken cancellationToken = default)
        => TrackedCommandBehavior?.Invoke(idOrName, command, onOutputLine, cancellationToken)
           ?? Task.FromResult(new ProcessExecutionResult(0, string.Empty, string.Empty, IsSuccess: true));

    public Task<ExecResult> StopTrackedCommandAsync(string idOrName, CancellationToken cancellationToken = default)
    {
        StopTrackedCalls++;
        return Task.FromResult(new ExecResult { Success = true });
    }

    public Task<PullResult> PullImageAsync(
        string imageUri,
        string? registryAuth = null,
        Action<PullProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        PullRequests.Add(imageUri);
        ReceivedRegistryAuth.Add(registryAuth);
        return PullBehavior?.Invoke(imageUri, onProgress, cancellationToken) ?? Task.FromResult(new PullResult());
    }

    /// <summary>按调用顺序记录收到的私有仓库令牌（null 表示没带）。</summary>
    public List<string?> ReceivedRegistryAuth { get; } = new();

    public Task<bool> DeleteImageAsync(string idOrName, bool force = false, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<bool> TagImageAsync(string source, string target, CancellationToken cancellationToken = default)
    {
        TagRequests.Add((source, target));
        return Task.FromResult(true);
    }

    /// <summary>按调用顺序记录打过的标签。</summary>
    public List<(string Source, string Target)> TagRequests { get; } = new();

    public Task<ImageInspectInfo> InspectImageAsync(string reference, CancellationToken cancellationToken = default)
        => Task.FromResult(new ImageInspectInfo());

    public Task<bool> CreateNetworkAsync(string name, string driver = "bridge", CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<bool> RemoveNetworkAsync(string idOrName, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<bool> CreateVolumeAsync(string name, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<bool> RemoveVolumeAsync(string name, bool force = false, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<bool> PruneVolumesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<bool> TerminateSessionAsync(int sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult(true);
}
