using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Abstractions;

/// <summary>把"找到一个可附着的引擎会话"这件事抽出来，便于测试注入失败/成功两种情形。</summary>
public interface IWslcApiSessionFactory
{
    /// <summary>
    /// 尝试<b>附着</b>到已经存在的引擎会话。
    ///
    /// 硬约束：<b>绝不新建会话</b>。用引擎当前正在用的名字与存储位置去开同一个会话；
    /// 一旦发现不了目标会话（或它打不开），就返回 null 并给出原因，
    /// 否则会在磁盘上多出一个 CLI 侧看不见的 <c>storage.vhdx</c>——
    /// 用户会以为"面板拉取的镜像凭空消失了"。
    /// </summary>
    IWslcApiSession? TryAttach(SessionModel target, out string? reason);
}
