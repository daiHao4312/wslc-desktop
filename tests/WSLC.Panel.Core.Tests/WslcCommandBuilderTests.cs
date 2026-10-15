using WSLC.Panel.Core.Models;
using WSLC.Panel.Core.Parsing;

namespace WSLC.Panel.Core.Tests;

/// <summary>
/// <c>wslc run</c> 参数拼装测试。
/// 这是"界面预览"与"真实执行"共用的唯一实现，错了只表现为"点了没反应"，所以必须测。
/// </summary>
public class WslcCommandBuilderTests
{
    [Fact]
    public void TestBuildRun_MinimalImage()
    {
        var args = WslcCommandBuilder.BuildRunArguments(new ContainerCreateOptions { Image = "nginx:latest" });

        Assert.Equal("run -d nginx:latest", args);
    }

    [Fact]
    public void TestBuildRun_FullOptions()
    {
        var options = new ContainerCreateOptions
        {
            Image = "mysql:8",
            Name = "mysql-db",
            Ports = new[] { "3306:3306", "33060:33060" },
            EnvironmentVariables = new[] { "MYSQL_ROOT_PASSWORD=secret" },
            Volumes = new[] { "mysql-data:/var/lib/mysql" },
            Network = "bridge",
            Memory = "1G",
            Cpus = "2",
            Workdir = "/app",
            Hostname = "dbhost",
            PullPolicy = "always",
            RemoveOnExit = true
        };

        var args = WslcCommandBuilder.BuildRunArguments(options);

        Assert.StartsWith("run -d --rm --name mysql-db", args);
        Assert.Contains("-p 3306:3306 -p 33060:33060", args);
        Assert.Contains("-e MYSQL_ROOT_PASSWORD=secret", args);
        Assert.Contains("-v mysql-data:/var/lib/mysql", args);
        Assert.Contains("--network bridge", args);
        Assert.Contains("-m 1G", args);
        Assert.Contains("--cpus 2", args);
        Assert.Contains("-w /app", args);
        Assert.Contains("-h dbhost", args);
        Assert.Contains("--pull always", args);
        Assert.EndsWith("mysql:8", args);
    }

    [Fact]
    public void TestBuildRun_SkipsDefaultPullPolicy()
    {
        var args = WslcCommandBuilder.BuildRunArguments(new ContainerCreateOptions { Image = "alpine" });

        // missing 是 CLI 默认值，不必写进命令行，保持预览简洁
        Assert.DoesNotContain("--pull", args);
    }

    [Fact]
    public void TestBuildRun_DefaultBindingAddressUsesLocalhost()
    {
        // 端口映射不带 IP 时，wslc 会按 settings.yaml 的 defaultBindingAddress 处理；
        // 面板统一写 "主机端口:容器端口"，把绑定地址交给引擎默认值
        var args = WslcCommandBuilder.BuildRunArguments(new ContainerCreateOptions
        {
            Image = "nginx",
            Ports = new[] { "8080:80" }
        });

        Assert.Contains("-p 8080:80", args);
        Assert.DoesNotContain("0.0.0.0", args);
    }

    [Fact]
    public void TestBuildRun_QuotesArgumentsWithSpaces()
    {
        var args = WslcCommandBuilder.BuildRunArguments(new ContainerCreateOptions
        {
            Image = "alpine",
            Name = "my container",
            EnvironmentVariables = new[] { "GREETING=hello world" }
        });

        Assert.Contains("--name \"my container\"", args);
        Assert.Contains("-e \"GREETING=hello world\"", args);
    }

    [Fact]
    public void TestBuildRunCommandLine_HasProgramName()
    {
        var line = WslcCommandBuilder.BuildRunCommandLine(new ContainerCreateOptions { Image = "redis:7" });

        Assert.Equal("wslc run -d redis:7", line);
    }

    [Fact]
    public void TestBuildRun_AppendsCommandAfterImage()
    {
        var args = WslcCommandBuilder.BuildRunArguments(new ContainerCreateOptions
        {
            Image = "alpine",
            Command = "sh -c \"echo hi\""
        });

        Assert.EndsWith("alpine sh -c \"echo hi\"", args);
    }

    [Fact]
    public void TestQuote_WindowsPathWithSpaces()
    {
        var quoted = WslcCommandBuilder.Quote(@"C:\my data\share:/mnt/data");

        // CRT 规则：引号内反斜杠成对消费。加倍后 CommandLineToArgvW 仍解析回原值，
        // 同时覆盖"尾部反斜杠吃掉收尾引号"的场景
        Assert.Equal("\"C:\\\\my data\\\\share:/mnt/data\"", quoted);
    }

    [Fact]
    public void TestQuote_TrailingBackslashDoesNotEatClosingQuote()
    {
        // -w C:\work\ ：旧实现输出 "C:\work\"，尾部反斜杠会把收尾引号转义掉，参数边界错乱
        var quoted = WslcCommandBuilder.Quote(@"C:\work\");

        Assert.Equal("\"C:\\\\work\\\\\"", quoted);
    }

    [Fact]
    public void TestQuote_PlainValueUnchanged()
    {
        Assert.Equal("nginx:latest", WslcCommandBuilder.Quote("nginx:latest"));
    }
}
