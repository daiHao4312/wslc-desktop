using System.Diagnostics;
using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Core.Services;

/// <summary>
/// 官方 API 通道的默认实现：探测可用性 → 附着会话 → 把 API 独有的能力接出来。
///
/// 设计要点（都是被"投影不确定"逼出来的）：
/// <list type="bullet">
/// <item>探测结果<b>缓存</b>：会话附着会开一个引擎会话，不能每次刷新都做一遍。</item>
/// <item>开关关闭时<b>不缓存</b>：用户随时可能在设置里打开，缓存 false 会让它一直不生效。</item>
/// <item>并发探测用信号量收口，避免两次刷新同时开两个会话。</item>
/// </list>
/// </summary>
public sealed class WslcApiChannel : IWslcApiChannel
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<SessionModel>>> _sessionProvider;
    private readonly IWslcApiSessionFactory _sessionFactory;
    private readonly IWslcSdkProbe _sdkProbe;
    private readonly Func<bool> _isEnabled;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private WslcApiCapabilities? _capabilities;
    private IWslcApiSession? _session;
    private bool _disposed;

    public WslcApiChannel(
        Func<CancellationToken, Task<IReadOnlyList<SessionModel>>> sessionProvider,
        IWslcApiSessionFactory? sessionFactory = null,
        IWslcSdkProbe? sdkProbe = null,
        Func<bool>? isEnabled = null)
    {
        _sessionProvider = sessionProvider ?? throw new ArgumentNullException(nameof(sessionProvider));
        _sessionFactory = sessionFactory ?? new WslcApiSessionFactory();
        _sdkProbe = sdkProbe ?? new WslcSdkProbe();
        _isEnabled = isEnabled ?? (() => true);
    }

    /// <summary>当前探测结果（未探测过为 null）。给关于页/日志用。</summary>
    public WslcApiCapabilities? Current => _capabilities;

    public async Task<WslcApiCapabilities> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (_capabilities is not null) return _capabilities;

        // 开关关闭的结果不缓存：用户随时可能在设置里打开
        if (!_isEnabled())
        {
            return new WslcApiCapabilities { Enabled = false, Reason = "设置中已关闭官方 API 通道（UseApiChannel）" };
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_capabilities is not null) return _capabilities;

            _capabilities = await ProbeCoreAsync(cancellationToken).ConfigureAwait(false);
            return _capabilities;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PullResult?> TryPullImageAsync(
        string imageUri,
        string? registryAuth,
        Action<PullProgress>? onProgress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imageUri)) return null;

        var capabilities = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        var session = _session;

        // 通道没接上：返回 null 让调用方安全回落 CLI（此时尚未产生任何下载动作）
        if (!capabilities.CanPullWithProgress || session is null) return null;

        var reference = imageUri.Trim();
        var sawProgress = false;
        var stopwatch = Stopwatch.StartNew();

        void HandleProgress(ApiImageProgress progress)
        {
            sawProgress = true;
            onProgress?.Invoke(ToPullProgress(reference, progress));
        }

        try
        {
            await session.PullImageAsync(reference, registryAuth, HandleProgress, cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();

            return new PullResult
            {
                Success = true,
                ImageReference = reference,
                Message = "拉取完成",
                Duration = stopwatch.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            // 一行进度都没出来就炸了 → 等价于"通道没接上"，回落 CLI 不会造成重复下载
            if (!sawProgress) return null;

            // 已经下过一段了：结果以 API 为准，换 CLI 重下只会白下一遍
            return new PullResult
            {
                Success = false,
                ImageReference = reference,
                Message = ex.Message,
                Duration = stopwatch.Elapsed
            };
        }
    }

    public async Task<RegistryCredentialResult> AuthenticateAsync(
        string serverAddress,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var capabilities = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        var session = _session;

        if (!capabilities.Enabled || session is null)
        {
            return new RegistryCredentialResult
            {
                Error = $"私有仓库认证需要官方 API 通道，当前不可用（{capabilities.Reason}）。" +
                        "可在「设置」里检查「官方 API 通道」开关与引擎状态。"
            };
        }

        // 认证要发给 API 端点（docker.io → registry-1.docker.io），而不是网站。
        // 这一步放在通道层是有意的：它是**可单测**的那一层；
        // WslcApiSession 里再转一次只是兜底（幂等），因为那边没法脱离引擎单测。
        var endpoint = RegistryInput.CanonicalHost(serverAddress);

        // Authenticate 是同步阻塞调用：挪到线程池，别卡住 UI 线程
        return await Task.Run(
            () => session.Authenticate(endpoint, username, password),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiProcessStartResult> TryStartProcessAsync(
        string containerIdOrName,
        string command,
        Action<string> onOutputLine,
        CancellationToken cancellationToken = default)
    {
        var capabilities = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        var session = _session;

        if (!capabilities.Enabled || session is null)
        {
            return ApiProcessStartResult.Unavailable($"官方 API 通道不可用（{capabilities.Reason}）");
        }

        try
        {
            // OpenContainer / CreateProcess / Start 全是同步阻塞调用：挪出 UI 线程
            var process = await Task.Run(
                () => session.TryStartProcess(containerIdOrName, command, onOutputLine),
                cancellationToken).ConfigureAwait(false);

            return ApiProcessStartResult.Started(process);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 容器不在运行 / 名字对不上 / 投影拒绝：统一归到"起不来"，界面回落 CLI 并说明差别
            var friendly = ApiErrorCodes.Describe(ex.HResult);
            return ApiProcessStartResult.Unavailable(friendly ?? ExceptionText.Describe(ex));
        }
    }

    private async Task<WslcApiCapabilities> ProbeCoreAsync(CancellationToken cancellationToken)    {
        var sdk = _sdkProbe.Query();

        if (!sdk.Available)
        {
            return new WslcApiCapabilities
            {
                SdkAvailable = false,
                Reason = $"官方 SDK 不可用：{sdk.Error ?? "未知原因"}"
            };
        }

        if (sdk.MissingComponents.Count > 0)
        {
            return new WslcApiCapabilities
            {
                SdkAvailable = true,
                SessionManagerVersion = sdk.SessionManagerVersion,
                MissingComponents = sdk.MissingComponents,
                Reason = $"引擎组件缺失（{string.Join("、", sdk.MissingComponents)}）"
            };
        }

        IReadOnlyList<SessionModel> sessions;
        try
        {
            sessions = await _sessionProvider(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new WslcApiCapabilities
            {
                SdkAvailable = true,
                SessionManagerVersion = sdk.SessionManagerVersion,
                Reason = $"枚举引擎会话失败：{ex.Message}"
            };
        }

        var target = PickSession(sessions);
        if (target is null)
        {
            return new WslcApiCapabilities
            {
                SdkAvailable = true,
                SessionManagerVersion = sdk.SessionManagerVersion,
                Reason = "未发现可附着的引擎会话"
            };
        }

        var session = _sessionFactory.TryAttach(target, out var reason);
        if (session is null)
        {
            return new WslcApiCapabilities
            {
                SdkAvailable = true,
                SessionManagerVersion = sdk.SessionManagerVersion,
                Reason = reason ?? "会话附着失败"
            };
        }

        _session = session;

        return new WslcApiCapabilities
        {
            SdkAvailable = true,
            SessionManagerVersion = sdk.SessionManagerVersion,
            AttachedSessionName = session.SessionName,

            // 会话归 CLI 进程所有时，我们没有 Start（抢不到名字，0x80040607）。
            // 这种情况能读、不一定能创建，界面必须能看出区别。
            AttachedReadOnly = session.ReadOnlyAttach,
            Reason = session.ReadOnlyAttach
                ? "只读附着（会话由 CLI 进程独占，未调用 Start）：可读镜像，写入类操作可能失败"
                : null
        };
    }

    /// <summary>
    /// 挑目标会话：优先引擎 CLI 自己的那条（<c>wslc-cli-*</c>）——
    /// 面板拉取的镜像必须落进 CLI 也能看到的同一个会话，否则用户会以为"镜像凭空消失"。
    /// </summary>
    private static SessionModel? PickSession(IReadOnlyList<SessionModel> sessions)
        => sessions.FirstOrDefault(session => session.IsCliDefault)
           ?? sessions.FirstOrDefault();

    /// <summary>把 API 进度映射成界面已经在消费的 CLI 进度模型（<c>Percent</c> 有值即确定进度条）。</summary>
    public static PullProgress ToPullProgress(string reference, ApiImageProgress progress) => new()
    {
        ImageReference = reference,
        LayerId = progress.ImageId,
        Status = progress.Status,
        DownloadedBytes = progress.DownloadedBytes,
        TotalBytes = progress.TotalBytes,
        Percent = progress.Percent,
        IsTerminal = progress.IsTerminal,
        RawLine = FormatRawLine(progress)
    };

    private static string FormatRawLine(ApiImageProgress progress)
        => progress.TotalBytes > 0
            ? $"{progress.Status} {FormatBytes(progress.DownloadedBytes)}/{FormatBytes(progress.TotalBytes)}"
            : progress.Status;

    private static string FormatBytes(ulong bytes) => bytes switch
    {
        < 1024UL => $"{bytes} B",
        < 1024UL * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024UL * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _session?.Dispose();
        }
        catch
        {
            // 忽略
        }

        _session = null;
        _gate.Dispose();
    }
}
