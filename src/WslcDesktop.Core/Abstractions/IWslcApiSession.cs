using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Abstractions;

/// <summary>
/// 官方 <c>Microsoft.WSL.Containers.Session</c> 的防腐层抽象。
///
/// 为什么不直接用 <c>Session</c>：它是 CsWinRT 投影类，构造即会真的连引擎，
/// 在没有 WSL 的机器（含 CI）上根本造不出来。路由与映射逻辑必须能脱离引擎单测，
/// 否则这一层永远只能"靠人肉点"验证。
/// </summary>
public interface IWslcApiSession : IDisposable
{
    string SessionName { get; }

    /// <summary>
    /// 会话的**存储根目录**（引擎侧 <c>session.storagePath</c> 的语义）——
    /// 默认是 <c>%LOCALAPPDATA%</c>，引擎在其下拼 <c>wslc\sessions\&lt;会话名&gt;\storage.vhdx</c>。
    /// </summary>
    string StorageRoot { get; }

    /// <summary>
    /// 是否是**只读附着**（没有调用 <c>Start()</c>）。
    ///
    /// 会话名被 CLI 进程独占时（<c>0x80040607 SessionReserved</c>），我们不做 Start 而是直接当句柄用。
    /// 界面上要能看出这个区别 —— 否则"API 通道可用"这句话在两种情况下含义完全不同。
    /// </summary>
    bool ReadOnlyAttach { get; }

    /// <summary>
    /// 读一次本地镜像名。既是功能，也是"会话确实可读"的探针——
    /// 附着成功但读不出内容说明附错了会话，必须降级而不是继续用。
    /// </summary>
    IReadOnlyList<string> GetImageNames();

    /// <summary>拉取镜像。<paramref name="onProgress"/> 携带真实字节数（API 独有）。</summary>
    Task PullImageAsync(
        string imageUri,
        string? registryAuth,
        Action<ApiImageProgress>? onProgress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 用用户名/密码向指定 registry 换一个身份令牌（官方 <c>Session.Authenticate</c>）。
    ///
    /// 同步阻塞调用——调用方负责挪出 UI 线程。
    /// </summary>
    RegistryCredentialResult Authenticate(string serverAddress, string username, string password);

    /// <summary>
    /// 在容器里起一个进程（<c>Session.OpenContainer</c> → <c>Container.CreateProcess</c> → <c>Process.Start</c>）。
    ///
    /// 命令统一包成 <c>/bin/sh -c &lt;命令&gt;</c>：与 CLI 的 exec 同一种语义（管道、引号、<c>&amp;&amp;</c> 都能用），
    /// 而不是按空格切分 —— 后者会让 <c>ls | head</c> 静默失效（参考项目踩过这个坑）。
    ///
    /// 输出**按行**回调（引擎给的是 <c>byte[]</c> 片段，拼行由 <see cref="Models.Utf8LineSplitter"/> 负责）。
    /// 起不来时抛出，由通道层转成可读结果；返回的句柄支持 SIGTERM/SIGKILL。
    /// </summary>
    IApiContainerProcess TryStartProcess(string containerIdOrName, string command, Action<string> onOutputLine);
}
