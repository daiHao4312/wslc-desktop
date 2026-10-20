using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Abstractions;

/// <summary>
/// 更新包下载（M13 在线更新的"取"这一段）。
///
/// 契约与 <see cref="IUpdateChecker"/> 一致：**任何失败都降级成结果对象，不抛异常**。
/// 取消是唯一的例外语义——同样返回 <see cref="UpdateDownloadStatus.Cancelled"/>，不向外抛
/// <see cref="OperationCanceledException"/>，避免调用方在 async void 事件里被未捕获异常打断。
/// </summary>
public interface IUpdateDownloader
{
    /// <summary>
    /// 把 <paramref name="package"/> 下载到 <paramref name="destinationDirectory"/>。
    ///
    /// 幂等：目标文件已存在且能通过同样的完整性校验时，直接复用不重下（对应"稍后安装"场景）。
    /// 下载过程先写 <c>.part</c> 再原子改名，中途失败/取消不会在目标目录留下半个 exe。
    /// </summary>
    Task<UpdateDownloadResult> DownloadAsync(
        UpdatePackage package,
        string destinationDirectory,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
