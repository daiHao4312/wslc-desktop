namespace WslcDesktop.Helpers;

/// <summary>
/// 支持后台轮询刷新的页面。
/// 主窗口的刷新定时器只调用"当前可见页"的刷新，避免后台页面白跑引擎命令。
/// </summary>
public interface IRefreshable
{
    /// <summary>
    /// 刷新页面数据。<paramref name="userInitiated"/> = true 表示用户主动刷新（需要显示进度/不打扰纪律的另一半）。
    /// </summary>
    Task RefreshAsync(bool userInitiated);
}
