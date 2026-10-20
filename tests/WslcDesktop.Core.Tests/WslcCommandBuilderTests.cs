using System.Runtime.InteropServices;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Core.Tests;

/// <summary>
/// <c>wslc run</c> 参数拼装测试。
/// 这是"界面预览"与"真实执行"共用的唯一实现，错了只表现为"点了没反应"，所以必须测。
/// </summary>
public class WslcCommandBuilderTests
{
    /// <summary>
    /// 用真实的 Windows 命令行解析器验证转义，而不是自己写个解析器自证。
    /// 这正是上一次翻车的地方：注释声称"加倍后仍解析回原值"，实际并没有。
    /// </summary>
    private static string[] SplitCommandLine(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var argc);
        if (argv == IntPtr.Zero) return Array.Empty<string>();

        try
        {
            var result = new string[argc];
            for (var i = 0; i < argc; i++)
            {
                var pointer = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                result[i] = Marshal.PtrToStringUni(pointer) ?? string.Empty;
            }

            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

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
        // 用长选项 --hostname：`-h` 在不少 CLI 里是 help，docker 系却是 hostname，
        // 同一个字母两种含义不值得赌（参考项目也用 --hostname）
        Assert.Contains("--hostname dbhost", args);
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

        // 反斜杠只在紧邻引号时才有转义含义，所以路径中间的反斜杠**原样保留**；
        // 只有结尾与内部引号之前才需要加倍（旧实现把所有反斜杠都加倍，会把路径改坏）
        Assert.Equal("\"C:\\my data\\share:/mnt/data\"", quoted);
    }

    [Fact]
    public void TestQuote_TrailingBackslashDoesNotEatClosingQuote()
    {
        // -w C:\work\ ：尾部反斜杠会把收尾引号转义掉、参数边界错乱，所以必须加倍
        var quoted = WslcCommandBuilder.Quote(@"C:\work\");

        Assert.Equal("\"C:\\work\\\\\"", quoted);
    }

    [Fact]
    public void TestQuote_UncPathKeepsItsLeadingBackslashes()
    {
        // \\wsl$\Ubuntu\home ：旧实现输出 \\\\wsl$\\Ubuntu\\home，UNC 直接报废
        var quoted = WslcCommandBuilder.Quote(@"\\wsl$\Ubuntu\home");

        Assert.Equal("\"\\\\wsl$\\Ubuntu\\home\"", quoted);
    }

    [Fact]
    public void TestQuote_InternalQuoteIsEscaped()
    {
        var quoted = WslcCommandBuilder.Quote("say \"hi\"");

        Assert.Equal("\"say \\\"hi\\\"\"", quoted);
    }

    /// <summary>
    /// 转义的**唯一**验收标准是"交给 CommandLineToArgvW 后能原样解析回来"。
    /// 前面几个断言只是快速定位；这一条用真实的 Win32 解析器做往返验证，
    /// 防止再出现"注释声称能解析回原值、实际不能"的情况。
    /// </summary>
    [Theory]
    [InlineData(@"C:\my data\share")]
    [InlineData(@"C:\work\")]
    [InlineData(@"\\wsl$\Ubuntu\home")]
    [InlineData(@"\\?\C:\very long\path\")]
    [InlineData("say \"hi\"")]
    [InlineData(@"C:\Program Files\My App\data.tar.gz")]
    [InlineData("/mnt/c/Users/x/Downloads/ubuntu.tar.gz")]
    [InlineData("déjà vu")]
    public void TestQuote_RoundTripsThroughCommandLineToArgvW(string value)
    {
        var commandLine = $"wsl.exe --import \"Ubuntu\" {WslcCommandBuilder.Quote(value)}";

        var argv = SplitCommandLine(commandLine);

        // argv[0]=wsl.exe, [1]=--import, [2]=Ubuntu, [3]=被转义的值
        Assert.Equal(4, argv.Length);
        Assert.Equal("Ubuntu", argv[2]);
        Assert.Equal(value, argv[3]);
    }

    [Fact]
    public void TestQuote_PlainValueUnchanged()
    {
        Assert.Equal("nginx:latest", WslcCommandBuilder.Quote("nginx:latest"));
    }

    // ---------------- wslc logs（M15：快照与跟随共用一份拼装） ----------------

    [Theory]
    [InlineData(100, false, "logs -n 100 c1")]
    [InlineData(0, false, "logs -n 100 c1")]      // 0 / 负数 → 回到默认行数，不把 -n 0 抛给引擎
    [InlineData(-5, false, "logs -n 100 c1")]
    [InlineData(200, true, "logs --follow c1")]   // 跟随不带上限：行数交给界面侧的环形缓冲
    public void 日志参数_行数兜底与follow形状(int tail, bool follow, string expected)
        => Assert.Equal(expected, WslcCommandBuilder.BuildLogsArguments("c1", tail, follow));

    /// <summary>
    /// 回归：曾经把 follow 追加在容器名之后（`logs -n 200 web --follow`），
    /// 而 <c>wslc</c> 会把名字后面的开关当成**容器名的一部分** → 查不到容器 → 跟随永远不出新日志。
    /// 这条不变量来自参考项目写成单测的记录："名字跑到开关前面会被当位置参数吃掉"。
    /// </summary>
    [Fact]
    public void 跟随参数_容器名必须最后_开关紧跟logs()
    {
        var arguments = WslcCommandBuilder.BuildLogsArguments("web", 200, follow: true);

        Assert.Equal("logs --follow web", arguments);
        Assert.StartsWith("logs --follow", arguments);
        Assert.EndsWith("web", arguments);
    }

    [Fact]
    public void 日志参数_容器名含空格要引用且仍在最后()
        => Assert.EndsWith(
            "\"my container\"",
            WslcCommandBuilder.BuildLogsArguments("my container", 50, follow: true));

    // ---------------- wslc run 的高级选项（M19） ----------------

    [Fact]
    public void GPU直通只拼官方示例里的写法()
    {
        var args = WslcCommandBuilder.BuildRunArguments(new ContainerCreateOptions
        {
            Image = "pytorch/pytorch:2.5.1-cuda12.4",
            EnableGpu = true
        });

        Assert.Contains("--gpus all", args);
        Assert.EndsWith("pytorch/pytorch:2.5.1-cuda12.4", args); // 镜像仍在最后
    }

    [Fact]
    public void 没开GPU时不出现gpus开关()
        => Assert.DoesNotContain(
            "gpus",
            WslcCommandBuilder.BuildRunArguments(new ContainerCreateOptions { Image = "alpine" }));

    [Fact]
    public void 域名用domainname长选项()
    {
        var args = WslcCommandBuilder.BuildRunArguments(new ContainerCreateOptions
        {
            Image = "alpine",
            DomainName = "corp.local"
        });

        Assert.Contains("--domainname corp.local", args);
    }

    /// <summary>
    /// 引擎只认大写单位（512M 可以、512m 被拒）—— 输入规范化在 Core 里做，界面直接显示规范后的命令。
    /// </summary>
    [Theory]
    [InlineData("512M", "512M")]
    [InlineData("512m", "512M")]
    [InlineData(" 1g ", "1G")]
    [InlineData("2G", "2G")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void 内存单位能规范化成大写(string? input, string expected)
    {
        Assert.True(ContainerOptionRules.TryNormalizeMemory(input, out var normalized, out var error), error);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("512MB")]   // 双字母单位引擎不认
    [InlineData("M512")]
    [InlineData("-1G")]
    [InlineData("abc")]
    public void 非法的内存写法要报可读错误(string input)
    {
        Assert.False(ContainerOptionRules.TryNormalizeMemory(input, out _, out var error));
        Assert.Contains("内存限制", error);
    }

    // ---------------- wslc exec（M19：必须走 shell） ----------------

    /// <summary>
    /// 回归：`wslc exec` 把剩余参数**按 argv 直接交给目标进程**，不经过 shell。
    /// 曾经原样拼成 `exec &lt;容器&gt; sh -c 'i=0; while …'`，于是 `'i=0;` 被当成一个独立 argv，
    /// 容器里报 "unexpected EOF while looking for matching `''"；
    /// 同理 `ls | head` 的管道也会失效。所以整条命令必须作为**一个**参数交给 /bin/sh -c。
    /// </summary>
    [Fact]
    public void exec的整条命令要作为单个参数交给sh()
    {
        var arguments = WslcCommandBuilder.BuildExecArguments("db", "ls | head -3");

        Assert.Equal("exec db /bin/sh -c \"ls | head -3\"", arguments);
        Assert.Contains("-c \"ls | head -3\"", arguments); // 管道在被引用的那一个参数里
    }

    [Fact]
    public void exec的引号命令不会被拆碎()
    {
        var arguments = WslcCommandBuilder.BuildExecArguments("db", "sh -c 'i=0; while [ $i -lt 6 ]; do echo tick; done'");

        Assert.StartsWith("exec db /bin/sh -c \"", arguments);
        Assert.EndsWith("done'\"", arguments);
    }

    [Fact]
    public void exec的容器名含空格要引用()
        => Assert.StartsWith(
            "exec \"my container\" /bin/sh -c ",
            WslcCommandBuilder.BuildExecArguments("my container", "ls"));
}
