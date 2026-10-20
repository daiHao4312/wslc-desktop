using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Core.Tests;

/// <summary>
/// 「可停止的容器内命令」的命令行包装（M19 修订）。
///
/// 起因：官方 API 的会话是机器级唯一、进程独占的（CLI 的容器在 <c>wslc-cli-*</c> 会话里，
/// API 附着会拿到 <c>0x80040607 SessionReserved</c>），所以停止只能靠"容器内记 PID + 发信号"。
/// </summary>
public class ContainerExecWrapperTests
{
    [Theory]
    [InlineData("ls -la")]
    [InlineData("ls | head -3")]
    [InlineData("sh -c 'i=0; while [ $i -lt 6 ]; do echo tick $i; sleep 1; done'")]
    [InlineData("echo \"双引号\" && echo '单引号'")]
    public void 命令经base64往返后一字不差(string command)
    {
        var script = ContainerExecWrapper.BuildTrackedScript(command);

        Assert.Equal(command, ContainerExecWrapper.DecodeTrackedScript(script));
    }

    /// <summary>
    /// 关键点：命令**不直接**出现在命令行里（否则又要和多层引号打架），
    /// 而是变成 base64 再交给容器内的 sh 执行。
    /// </summary>
    [Fact]
    public void 包装脚本里不含原始命令的引号()
    {
        const string command = "sh -c 'echo it''s tricky'";

        var script = ContainerExecWrapper.BuildTrackedScript(command);

        Assert.DoesNotContain("tricky", script);
        Assert.Contains("base64 -d | sh", script);
    }

    [Fact]
    public void 包装脚本会记下PID并保持前台()
    {
        var script = ContainerExecWrapper.BuildTrackedScript("sleep 300");

        Assert.Contains($"echo $! > {ContainerExecWrapper.PidFile}", script);
        Assert.EndsWith("wait", script); // wait 让输出与退出码仍然走前台
        Assert.Contains("&", script);    // 后台起，前台等
    }

    [Fact]
    public void 停止脚本先TERM后KILL且没有pid文件时安全退出()
    {
        var script = ContainerExecWrapper.BuildStopScript();

        Assert.Contains(ContainerExecWrapper.PidFile, script);
        Assert.Contains("kill -TERM", script);
        Assert.Contains("kill -KILL", script);
        Assert.Contains("|| exit 0", script);
    }

    [Fact]
    public void 容器名要引用_整条脚本是单个参数()
    {
        var arguments = ContainerExecWrapper.BuildTrackedExecArguments("my container", "ls");

        Assert.StartsWith("exec \"my container\" /bin/sh -c \"", arguments);
        Assert.EndsWith("\"", arguments);   // 整条脚本被一对引号包住 = 一个参数
    }

    [Fact]
    public void 空命令要被拒绝()
        => Assert.Throws<ArgumentException>(() => ContainerExecWrapper.BuildTrackedScript("  "));
}
