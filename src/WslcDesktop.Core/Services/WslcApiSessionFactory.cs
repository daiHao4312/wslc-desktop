using Microsoft.WSL.Containers;
using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Core.Services;

/// <summary>
/// 默认的会话附着实现：用引擎<b>当前正在用的</b>会话名与存储位置开一个官方 <c>Session</c>。
///
/// 两条判据都为了同一个目标——<b>只附着、不新建</b>：
/// <list type="number">
/// <item>先在磁盘上找到目标会话的目录，而且必须真的有 <c>storage.vhdx</c>
/// （只存在空目录不算，那可能是残留）；</item>
/// <item>交给引擎的 <c>StoragePath</c> 是**存储根目录**而不是会话目录 ——
/// 引擎自己会在下面拼 <c>wslc\sessions\&lt;会话名&gt;\storage.vhdx</c>。</item>
/// </list>
///
/// ⚠️ 第 2 条是踩过的坑：原先传的是会话目录，引擎于是去找
/// <c>…\wslc\sessions\wslc-cli-X\wslc\sessions\wslc-cli-X\storage.vhdx</c>（不存在），
/// 抛出一个 HRESULT 说不清的错 —— 用户看到的就是"附着会话失败：无法找到与此错误代码关联的文本"。
/// 实测依据见 <see cref="SessionStorageLocator.GetStorageRoot"/>。
/// </summary>
public sealed class WslcApiSessionFactory : IWslcApiSessionFactory
{
    private readonly Func<string, string, IWslcApiSession> _create;

    /// <param name="create">
    /// 会话构造器。默认开真的官方 <c>Session</c>；测试注入替身以便在没有引擎的机器上验证路由。
    /// 第二个参数是**存储根目录**。
    /// </param>
    public WslcApiSessionFactory(Func<string, string, IWslcApiSession>? create = null)
    {
        _create = create ?? DefaultCreate;
    }

    public IWslcApiSession? TryAttach(SessionModel target, out string? reason)
    {
        reason = null;

        if (target is null)
        {
            reason = "没有可附着的会话";
            return null;
        }

        var directory = ResolveExistingDirectory(target);
        if (directory is null)
        {
            reason = $"未找到会话「{target.DisplayName}」在磁盘上的存储目录";
            return null;
        }

        if (!SessionStorageLocator.HasStorageFile(directory))
        {
            reason = $"会话目录存在但没有 storage.vhdx，不是引擎在用的那个会话：{directory}";
            return null;
        }

        try
        {
            var session = _create(target.DisplayName, SessionStorageLocator.GetStorageRoot());

            // 附着成功但读不出镜像 = 附到了一个空会话（多半是猜错了路径）。
            // 这种"看起来成功"的情形必须当成失败，否则用户会以为 CLI 里的镜像丢了。
            var names = session.GetImageNames();
            if (names is null)
            {
                SafeDispose(session);
                reason = "会话可读性校验失败（返回 null）";
                return null;
            }

            return session;
        }
        catch (Exception ex)
        {
            // 这类 HRESULT 没有本地化文本，认出来就换成人话（见 ApiErrorCodes）
            var friendly = ApiErrorCodes.Describe(ex.HResult);
            reason = friendly is null
                ? $"附着会话失败：{ExceptionText.Describe(ex)}"
                : $"附着会话失败：{friendly}";
            return null;
        }
    }

    /// <summary>会话目录：优先按会话名在 sessions 根下定位（引擎实测布局），退回数据模型里的路径。</summary>
    private static string? ResolveExistingDirectory(SessionModel target)
    {
        var byName = SessionStorageLocator.FindExistingSessionDirectory(target.DisplayName);
        if (byName is not null) return byName;

        // SessionModel.StoragePath 补全后是 storage.vhdx 的完整路径，取它的目录
        if (!string.IsNullOrWhiteSpace(target.StoragePath))
        {
            try
            {
                var dir = Path.GetDirectoryName(target.StoragePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
            }
            catch
            {
                // 路径畸形：当作定位失败
            }
        }

        return null;
    }

    private static IWslcApiSession DefaultCreate(string sessionName, string storagePath)
    {
        var session = new Session(new SessionSettings(sessionName, storagePath));
        var readOnlyAttach = false;

        try
        {
            session.Start();
        }
        catch (Exception ex) when (ApiErrorCodes.IsSessionReserved(ex))
        {
            // ⚠️ 会话名已被 CLI 进程独占（0x80040607 SessionReserved）。
            //
            // 关键实验：`Start()` 是"创建/启动"，需要独占会话名；但**读操作未必需要 Start**。
            // 所以这里不放弃，而是把它当"只读句柄"继续往下走 ——
            // 能不能用，交给调用方的 GetImageNames() 判定：
            //   · 读得出来 → 只读附着成立（拉取/查询就能走 API）；
            //   · 读不出来 → 抛异常，外层回落 CLI（等同本次修复前的行为，无回归）。
            // 这条判据由用户在真机验证，日志里会写明走了哪条路。
            readOnlyAttach = true;
        }

        return new WslcApiSession(session, sessionName, storagePath, readOnlyAttach);
    }

    private static void SafeDispose(IWslcApiSession session)
    {
        try
        {
            session.Dispose();
        }
        catch
        {
            // 忽略
        }
    }
}
