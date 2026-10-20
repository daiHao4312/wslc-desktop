using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Abstractions;

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

    /// <summary>
    /// **跟随**容器日志：每读到一行回调一次，直到容器停止、调用方取消、或引擎退出。
    ///
    /// ⚠️ 跟随只能走 CLI：官方 API 里没有 logs 这个概念
    /// （<c>Process</c> 的流是"容器内某个进程"的 I/O，不是容器日志），
    /// 所以这里不给 API 通道留口子，免得以后有人以为"API 也能跟随"。
    ///
    /// ⚠️ **两个回调都要接**：<c>wslc logs</c> 用**自己的 stdout 承载容器的 stdout**、
    /// **自己的 stderr 承载容器的 stderr**。只接 <paramref name="onOutputLine"/> 的后果是
    /// "日志缺一大半"——MySQL / nginx 这类把运行日志写到 stderr 的镜像，
    /// 面板里只会剩下 entrypoint 脚本那几行（实测踩过，对照终端里的 <c>logs -f</c> 才看出来）。
    /// 引擎自己的报错也走 stderr，会被当成一行日志显示出来 —— 这是可接受的代价：
    /// 宁可把原文摆出来，也不要静默丢日志。
    ///
    /// 没有 tail 参数是刻意的：跟随命令不带上限（与终端里的 <c>logs -f</c> 同形状），
    /// 行数由调用方的环形缓冲控制 —— 少一个参数，就少一次"参数放错位置"的机会。
    /// </summary>
    Task<ProcessExecutionResult> StreamContainerLogsAsync(
        string idOrName,
        Action<string> onOutputLine,
        Action<string>? onErrorLine = null,
        CancellationToken cancellationToken = default);

    /// <summary>在容器内执行一次性命令（非交互），返回合并输出。</summary>
    Task<ExecResult> ExecAsync(string idOrName, string command, CancellationToken cancellationToken = default);

    /// <summary>
    /// 在容器里起一个**可管理**的进程（M19）：实时输出 + SIGTERM/SIGKILL + 退出码。
    ///
    /// 只有官方 API 通道能给信号（信号发给容器内进程本身）；
    /// CLI 的 <c>wslc exec</c> 杀掉的只是客户端进程，容器里那个可能还在跑 ——
    /// 所以通道不可用时返回带原因的失败，由界面回落一次性执行并**说明差别**，不假装一样。
    /// </summary>
    Task<ApiProcessStartResult> TryStartContainerProcessAsync(
        string containerIdOrName,
        string command,
        Action<string> onOutputLine,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 在容器内**可停止**地执行命令（CLI 实现）：实时输出 + 按容器内 pidfile 发信号。
    ///
    /// 这是 API 走不通时的正路：官方会话是"机器级唯一、进程独占"的（CLI 的容器都在
    /// <c>wslc-cli-*</c> 会话里，API 附着会拿到 <c>0x80040607 SessionReserved</c>），
    /// 所以停止改为"容器内记 PID → 再发 TERM/KILL"。
    /// </summary>
    Task<ProcessExecutionResult> StreamTrackedCommandAsync(
        string idOrName,
        string command,
        Action<string> onOutputLine,
        CancellationToken cancellationToken = default);

    /// <summary>停止上一次 <see cref="StreamTrackedCommandAsync"/> 起的命令（TERM，200 ms 后补 KILL）。</summary>
    Task<ExecResult> StopTrackedCommandAsync(string idOrName, CancellationToken cancellationToken = default);

    // ---------- 镜像 ----------
    /// <summary>
    /// 拉取镜像；<paramref name="onProgress"/> 会在每读到一行引擎输出时被调用。
    ///
    /// <paramref name="registryAuth"/> 是私有仓库的身份令牌（来自
    /// <c>IWslcApiChannel.AuthenticateAsync</c>）。⚠️ **只有官方 API 通道能用它**：
    /// CLI 的 <c>pull</c> 没有这个参数，回落时会忽略（引擎自己的凭据缓存仍可能让它成功）。
    /// </summary>
    Task<PullResult> PullImageAsync(
        string imageUri,
        string? registryAuth = null,
        Action<PullProgress>? onProgress = null,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteImageAsync(string idOrName, bool force = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// 给本地镜像打一个新标签（<c>wslc tag &lt;源&gt; &lt;目标&gt;</c>）。
    ///
    /// 注意语义：这是**新增一个引用**，同一个镜像 ID 会同时挂在两个名字下，原引用不会消失。
    /// 想真正"改名"还要再删掉旧引用（<see cref="DeleteImageAsync"/>）。
    /// </summary>
    Task<bool> TagImageAsync(string source, string target, CancellationToken cancellationToken = default);

    /// <summary>读取本地镜像声明的启动信息（端口/命令），用于创建容器表单预填。</summary>
    Task<ImageInspectInfo> InspectImageAsync(string reference, CancellationToken cancellationToken = default);

    /// <summary>
    /// 把本地镜像导出成一个 tar 归档（<c>wslc save -o &lt;文件&gt; &lt;镜像&gt;...</c>）。
    ///
    /// 一个 tar 里可以放多个镜像（引擎支持一次给多个引用），这是"搬运一整套镜像"最省事的方式。
    /// 进度只能靠**轮询目标文件大小**：<c>wslc save</c> 成功时一句话都不输出（实测 8.4 MB 的 alpine），
    /// 与 <c>wsl --export</c> 同一个毛病。
    /// </summary>
    /// <remarks>
    /// 失败或取消时会**删除目标文件**——半截 tar 拿去 <c>load</c> 只会得到一句含糊的错，
    /// 留着比删掉更误导人。
    /// </remarks>
    Task<ImageArchiveResult> ExportImagesAsync(
        IReadOnlyList<string> references,
        string outputPath,
        Action<ImageArchiveProgress>? onProgress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 从 tar 归档导入镜像（<c>wslc load -i &lt;文件&gt;</c>）。
    ///
    /// 引擎每加载完一个镜像就打一行 <c>已加载映像: repo:tag</c>，所以进度只能报"已经加载了哪些"，
    /// 没有字节数可显示。同名引用会被覆盖成归档里的内容（与 docker load 一致）。
    /// </summary>
    Task<ImageArchiveResult> ImportImageArchiveAsync(
        string archivePath,
        Action<ImageArchiveProgress>? onProgress = null,
        CancellationToken cancellationToken = default);

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
