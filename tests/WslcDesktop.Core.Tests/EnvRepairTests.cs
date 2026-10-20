using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Tests;

/// <summary>
/// 组件安装/修复的纯逻辑（M20）。
///
/// WinRT 的 <c>InstallProgress</c> 不能在这里构造，所以把"百分比 + 文案"抽成模型再测 ——
/// 真机上只验"有没有装上"，这两件事分开就好查。
/// </summary>
public class EnvRepairTests
{
    [Theory]
    [InlineData(0u, 0u, 0d, false)]
    [InlineData(0u, 100u, 0d, true)]
    [InlineData(50u, 100u, 50d, true)]
    [InlineData(100u, 100u, 100d, true)]
    [InlineData(200u, 100u, 100d, true)] // 引擎给出越界值时夹到 100
    public void 进度百分比与确定性(uint done, uint total, double percent, bool determinate)
    {
        var progress = new EnvRepairProgress { Component = "WslPackage", Done = done, Total = total };

        Assert.Equal(percent, progress.Percent, 3);
        Assert.Equal(determinate, progress.IsDeterminate);
    }

    [Fact]
    public void 有总数时文案带计数与百分比()
    {
        var progress = new EnvRepairProgress { Component = "WslPackage", Done = 3, Total = 4 };

        Assert.Contains("WSL 运行时包", progress.Display);
        Assert.Contains("3/4", progress.Display);
        Assert.Contains("75%", progress.Display);
    }

    [Fact]
    public void 没有总数时只说正在安装_不编造百分比()
    {
        var progress = new EnvRepairProgress { Component = "VirtualMachinePlatform", Done = 0, Total = 0 };

        Assert.Contains("正在安装", progress.Display);
        Assert.DoesNotContain("%", progress.Display);
    }

    [Theory]
    [InlineData("VirtualMachinePlatform", "虚拟机平台")]
    [InlineData("WslPackage", "WSL 运行时包")]
    [InlineData("SdkNeedsUpdate", "SDK")]
    public void 组件名要翻成人话(string component, string expected)
        => Assert.Contains(expected, EnvRepairPlanner.Describe(component));

    [Fact]
    public void 不认识的组件名原样返回()
        => Assert.Equal("SomethingNew", EnvRepairPlanner.Describe("SomethingNew"));

    [Fact]
    public void 缺少组件时才需要修复()
    {
        Assert.False(EnvRepairPlanner.NeedsRepair(Array.Empty<string>()));
        Assert.False(EnvRepairPlanner.NeedsRepair(null));
        Assert.True(EnvRepairPlanner.NeedsRepair(new[] { "WslPackage" }));
    }

    [Fact]
    public void 无需修复是成功且标记为跳过()
    {
        var result = EnvRepairResult.NothingToDo("组件齐全");

        Assert.True(result.Success);
        Assert.True(result.Skipped);
        Assert.Empty(result.Repaired);
    }
}
