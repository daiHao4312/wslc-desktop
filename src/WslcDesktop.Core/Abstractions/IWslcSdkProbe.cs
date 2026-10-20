using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Abstractions;

/// <summary>
/// 官方 SDK 探测。<c>WslcService</c> 是静态类且引擎缺失时会抛异常，
/// 抽成接口后单测（与 CI）能在没有 WSL 的机器上模拟"SDK 可用 / 组件缺失 / 引擎不存在"三种情形。
/// </summary>
public interface IWslcSdkProbe
{
    /// <summary>查询一次；<b>不抛异常</b>，失败信息放进 <see cref="WslcSdkInfo.Error"/>。</summary>
    WslcSdkInfo Query();
}
