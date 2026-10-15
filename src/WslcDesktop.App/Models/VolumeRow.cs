using CommunityToolkit.Mvvm.ComponentModel;

namespace WslcDesktop.Models;

/// <summary>
/// 创建容器页「目录映射」的一行。
///
/// 本机侧既可以是**主机目录**（点「浏览」从资源管理器选），也可以是**命名卷**
/// （如 <c>mysql-data</c>，直接手输）；容器侧永远是手输的容器内路径。
/// 两种用法共用同一个输入框，点不点「浏览」都不影响拼装。
///
/// 必须实现变更通知：选完文件夹后要由 <c>HostPath</c> 把新值推回输入框（OneWay 绑定）。
/// </summary>
public partial class VolumeRow : ObservableObject
{
    // 用 partial property 而不是字段：WinUI 3 下字段版 [ObservableProperty] 生成的代码
    // 不是 AOT 兼容的（CsWinRT 需要属性级的部分声明才能生成封送代码），会报 MVVMTK0045。
    // 代价是声明部分不能带初始化器，初值只能放进构造函数。
    [ObservableProperty]
    public partial string HostPath { get; set; }

    [ObservableProperty]
    public partial string ContainerPath { get; set; }

    public VolumeRow()
    {
        HostPath = string.Empty;
        ContainerPath = string.Empty;
    }

    /// <summary>
    /// 拼成 wslc 的挂载参数；任一栏为空返回 null（该行视为未填完，不参与拼装）。
    /// </summary>
    public string? ToMount()
    {
        var host = HostPath.Trim();
        var container = ContainerPath.Trim();

        return host.Length == 0 || container.Length == 0 ? null : $"{host}:{container}";
    }
}
