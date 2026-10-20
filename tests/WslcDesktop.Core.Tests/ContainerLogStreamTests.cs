using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Services;

namespace WslcDesktop.Core.Tests;

/// <summary>
/// 日志跟随的传输层（M15）：跟随必须真的带 follow 选项，快照必须不带。
///
/// 这条断言看着琐碎，但它是"跟随能不能用"的唯一自动化证据 ——
/// 沙箱里跑不了 <c>wslc</c>，参数拼错的表现只是"点了没反应"。
/// </summary>
public class ContainerLogStreamTests
{
    [Fact]
    public async Task 跟随会带follow选项并逐行回调()
    {
        var runner = new RecordingProcessRunner
        {
            Handler = (_, _) => new ProcessExecutionResult(0, "line1\nline2\n", string.Empty, true)
        };

        var lines = new List<string>();
        var result = await new CliWslcClient(runner).StreamContainerLogsAsync("my container", lines.Add);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "line1", "line2" }, lines);

        var call = Assert.Single(runner.Calls);

        // ⚠️ 回归：容器名必须最后。写成 `logs -n 200 "my container" --follow` 时，
        // 引擎会把 "--follow" 当成容器名的一部分 → 查不到容器 → 面板里只有快照。
        Assert.Equal("wslc.exe logs --follow \"my container\"", call);
    }

    [Fact]
    public async Task 快照不带follow_行数为0时用默认值()
    {
        var runner = new RecordingProcessRunner
        {
            Handler = (_, _) => new ProcessExecutionResult(0, "snapshot", string.Empty, true)
        };

        var text = await new CliWslcClient(runner).GetContainerLogsAsync("c1", tail: 0);

        Assert.Equal("snapshot", text);
        var call = Assert.Single(runner.Calls);
        Assert.Contains("logs -n 100 c1", call);
        Assert.DoesNotContain("--follow", call);
    }

    /// <summary>
    /// 回归：只接 stdout 会丢一半日志。
    /// <c>wslc logs</c> 用自己的 stderr 承载**容器的 stderr** —— MySQL / nginx 这类镜像的运行日志
    /// 全在那儿，漏掉的表现就是"面板里只有 entrypoint 那几行"。
    /// </summary>
    [Fact]
    public async Task 跟随必须同时接stderr_否则只有一半日志()
    {
        var runner = new RecordingProcessRunner
        {
            Handler = (_, _) => new ProcessExecutionResult(
                0,
                "[Entrypoint] initializing database\n",
                "mysqld: ready for connections\n",
                true)
        };

        var stdout = new List<string>();
        var stderr = new List<string>();

        await new CliWslcClient(runner).StreamContainerLogsAsync("db", stdout.Add, stderr.Add);

        Assert.Equal(new[] { "[Entrypoint] initializing database" }, stdout);
        Assert.Equal(new[] { "mysqld: ready for connections" }, stderr);
    }

    /// <summary>
    /// 回归：快照也必须把两路合起来。
    /// 旧实现是"stdout 非空就返回 stdout"，于是 MySQL 这类镜像只显示 entrypoint 那十几行
    /// （写 stdout），mysqld 的运行日志（写 stderr）全部消失 —— 用户看到的就是"进日志只有这么点"。
    /// </summary>
    [Fact]
    public async Task 快照必须同时返回stdout与stderr()
    {
        var runner = new RecordingProcessRunner
        {
            Handler = (_, _) => new ProcessExecutionResult(
                0,
                "[Entrypoint] initializing database\n",
                "mysqld: ready for connections\n",
                true)
        };

        var text = await new CliWslcClient(runner).GetContainerLogsAsync("db", 200);

        Assert.Contains("[Entrypoint] initializing database", text);
        Assert.Contains("mysqld: ready for connections", text); // ← 关键：不能丢 stderr
    }

    [Fact]
    public async Task 没有标准输出时返回标准错误_便于界面解释失败原因()
    {
        var runner = new RecordingProcessRunner
        {
            Handler = (_, _) => new ProcessExecutionResult(1, string.Empty, "no such container", false)
        };

        var text = await new CliWslcClient(runner).GetContainerLogsAsync("missing");

        Assert.Equal("no such container", text);
    }
}
