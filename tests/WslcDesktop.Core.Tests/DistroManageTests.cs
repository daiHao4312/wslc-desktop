using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;
using WslcDesktop.Core.Services;

namespace WslcDesktop.Core.Tests;

/// <summary>
/// M18 发行版详情操作：参数拼装、前置校验、wsl.conf 的 heredoc 脚本、服务装配。
///
/// <c>wsl.exe</c> 在沙箱里跑不起来，所以参数形状与"不合法时不调引擎"这两件事
/// 只能靠这里钉死——它们错了的表现是"点了没反应"或"配置被静默写坏"，都很难查。
/// </summary>
public class DistroManageTests
{
    // ---------------- 参数拼装 ----------------

    [Fact]
    public void 压缩命令()
    {
        Assert.Equal("--manage Ubuntu --compact", DistroManagePlanner.BuildCompactArguments("Ubuntu"));
    }

    [Fact]
    public void 开关稀疏盘_开启时必须带_allow_unsafe()
    {
        // WSL 3.0.1 起默认拒绝开稀疏盘（理由是潜在数据损坏），
        // 不带 --allow-unsafe 这条操作在界面上永远失败
        Assert.Equal(
            "--manage Ubuntu --set-sparse true --allow-unsafe",
            DistroManagePlanner.BuildSparseArguments("Ubuntu", sparse: true));

        // 关的时候不需要那个开关
        Assert.Equal(
            "--manage Ubuntu --set-sparse false",
            DistroManagePlanner.BuildSparseArguments("Ubuntu", sparse: false));
    }

    [Fact]
    public void 调整大小与移动与默认用户()
    {
        Assert.Equal("--manage Ubuntu --resize 50GB", DistroManagePlanner.BuildResizeArguments("Ubuntu", "50GB"));
        Assert.Equal(
            "--manage Ubuntu --move \"D:\\WSL\\Ubuntu\"",
            DistroManagePlanner.BuildMoveArguments("Ubuntu", @"D:\WSL\Ubuntu"));
        Assert.Equal(
            "--manage Ubuntu --set-default-user alice",
            DistroManagePlanner.BuildSetDefaultUserArguments("Ubuntu", "alice"));
    }

    [Fact]
    public void 导出命令()
    {
        Assert.Equal(
            "--export Ubuntu \"D:\\backup\\ubuntu.tar\"",
            DistroManagePlanner.BuildExportArguments("Ubuntu", @"D:\backup\ubuntu.tar"));
    }

    [Fact]
    public void 含空格的发行版名与路径被正确引用()
    {
        // 本机就存在 "Ubuntu 24.04 LTS" 这种带空格的名字
        Assert.Equal("--manage \"Ubuntu 24.04 LTS\" --compact",
            DistroManagePlanner.BuildCompactArguments("Ubuntu 24.04 LTS"));

        Assert.Equal(
            "--manage Ubuntu --move \"D:\\my wsl\\Ubuntu 24.04\"",
            DistroManagePlanner.BuildMoveArguments("Ubuntu", @"D:\my wsl\Ubuntu 24.04"));
    }

    // ---------------- 大小校验 ----------------

    [Theory]
    [InlineData("50GB", "50GB")]
    [InlineData(" 50gb ", "50GB")]
    [InlineData("1.5TB", "1.5TB")]
    [InlineData("512MB", "512MB")]
    [InlineData("100 KB", "100KB")]
    [InlineData("0.5GB", "0.5GB")]
    public void 大小被规范化(string input, string expected)
    {
        Assert.True(DistroManagePlanner.TryNormalizeSize(input, out var normalized, out var error), error);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("GB")]
    [InlineData("50")]
    [InlineData("50PB")]
    [InlineData("50GiB")]
    [InlineData("50g")]
    [InlineData("1.GB")]
    [InlineData(".5GB")]
    [InlineData("50..GB")]
    [InlineData("1.2.3GB")]
    public void 不规范的大小被本地挡下(string input)
    {
        // 放过去只会让用户收到一句含糊的引擎报错；这里能提前给出"哪个字符不对"
        Assert.False(DistroManagePlanner.TryNormalizeSize(input, out var normalized, out var error));
        Assert.Equal(string.Empty, normalized);
        Assert.NotEmpty(error);
    }

    // ---------------- 路径校验 ----------------

    [Theory]
    [InlineData(@"D:\x", true)]
    [InlineData(@"D:/x", true)]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"\foo", false)]
    [InlineData(@"wsl\X", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void 绝对路径判定(string path, bool expected)
    {
        Assert.Equal(expected, DistroManagePlanner.IsAbsoluteWindowsPath(path));
    }

    [Fact]
    public void 移动目标必须是绝对路径()
    {
        Assert.False(DistroManagePlanner.TryValidateMoveTarget(@"wsl\X", out var error));
        Assert.Contains("绝对路径", error);

        Assert.False(DistroManagePlanner.TryValidateMoveTarget("   ", out _));
        Assert.True(DistroManagePlanner.TryValidateMoveTarget(@"D:\WSL\Ubuntu", out _));
    }

    [Fact]
    public void 导出路径必须是文件名而不是目录()
    {
        // 以分隔符结尾的是目录，而 --export 要的是文件名；
        // 不挡的话引擎会报一句难懂的话，还可能先建出一个空文件
        Assert.False(DistroManagePlanner.TryValidateExportPath(@"D:\backup\", out var error));
        Assert.Contains("不能只给目录", error);

        Assert.False(DistroManagePlanner.TryValidateExportPath(@"D:/backup/", out _));
        Assert.False(DistroManagePlanner.TryValidateExportPath(@"backup\a.tar", out _));
        Assert.False(DistroManagePlanner.TryValidateExportPath("", out _));
        Assert.True(DistroManagePlanner.TryValidateExportPath(@"D:\backup\ubuntu.tar", out _));
    }

    // ---------------- shell 转义 ----------------

    [Fact]
    public void shell_quote_中和注入()
    {
        Assert.Equal("'ubuntu'", DistroManagePlanner.ShellQuote("ubuntu"));
        Assert.Equal("''", DistroManagePlanner.ShellQuote(""));
        // 单引号本身：关引号 → 转义的单引号 → 再开引号
        Assert.Equal(@"'it'\''s'", DistroManagePlanner.ShellQuote("it's"));

        foreach (var nasty in new[] { "; rm -rf /", "$(whoami)", "`id`", "a b", "a\"b", "a\\b" })
        {
            var quoted = DistroManagePlanner.ShellQuote(nasty);

            Assert.StartsWith("'", quoted);
            Assert.EndsWith("'", quoted);
            // 关键：内部**不能**还有裸引号 —— 那才是能逃出去的地方
            Assert.DoesNotContain("'", quoted[1..^1]);
        }
    }

    // ---------------- wsl.conf 脚本 ----------------

    [Fact]
    public void 读wsl_conf的脚本文件不存在也退出0()
    {
        var script = DistroManagePlanner.BuildReadWslConfScript();

        Assert.Contains("[ -f /etc/wsl.conf ]", script);
        Assert.Contains("cat /etc/wsl.conf", script);
        Assert.EndsWith("|| true", script);
    }

    [Fact]
    public void 写wsl_conf用带引号的heredoc()
    {
        var script = DistroManagePlanner.BuildWriteWslConfScript("[user]\ndefault = u\n");

        // 带引号的定界符：$ / 反引号 / 反斜杠都不会被 shell 展开
        Assert.StartsWith("cat << 'WSL_CONF_EOF' > /etc/wsl.conf\n", script);
        Assert.Contains("[user]\ndefault = u\n", script);
        Assert.EndsWith("WSL_CONF_EOF\n", script);
    }

    [Fact]
    public void 正文里出现定界符时换一个()
    {
        // 撞上了会把后面的内容漏到 shell 里去**执行**，属于静默写坏配置的一类 bug
        var nasty = "[user]\ndefault = u\nWSL_CONF_EOF\nrm -rf /tmp/oops\n";
        var script = DistroManagePlanner.BuildWriteWslConfScript(nasty);

        Assert.DoesNotContain("<< 'WSL_CONF_EOF'", script);
        Assert.Contains("WSL_CONF_EOF_1", script);
        Assert.Contains("rm -rf /tmp/oops", script);
    }

    [Fact]
    public void 正文没有结尾换行时自己补上()
    {
        // 不补的话定界符会粘在最后一行上，heredoc 不结束 ——
        // sh 报 here-document delimited by end-of-file，并把定界符当正文写进文件
        var script = DistroManagePlanner.BuildWriteWslConfScript("[user]\ndefault = u");

        Assert.EndsWith("default = u\nWSL_CONF_EOF\n", script);
    }

    [Fact]
    public void 空内容也能写出一个合法脚本()
    {
        var script = DistroManagePlanner.BuildWriteWslConfScript(string.Empty);

        Assert.Equal("cat << 'WSL_CONF_EOF' > /etc/wsl.conf\nWSL_CONF_EOF\n", script);
    }

    [Fact]
    public void 含NUL的内容被拒()
    {
        Assert.False(DistroManagePlanner.TryValidateWslConfText("a\0b", out var error));
        Assert.NotEmpty(error);
        Assert.True(DistroManagePlanner.TryValidateWslConfText("[user]\ndefault = u\n", out _));
    }

    // ---------------- 服务装配 ----------------

    [Fact]
    public async Task 压缩走_wsl_manage_compact()
    {
        var runner = new RecordingProcessRunner();
        var service = new DistroService(runner);

        var result = await service.CompactAsync("Ubuntu");

        Assert.True(result.Success);
        Assert.Equal("wsl.exe --manage Ubuntu --compact", Assert.Single(runner.Calls));
        Assert.Equal("wsl --manage Ubuntu --compact", result.CommandLine);
    }

    [Fact]
    public async Task 开稀疏盘带上_allow_unsafe()
    {
        var runner = new RecordingProcessRunner();
        var service = new DistroService(runner);

        await service.SetSparseAsync("Ubuntu", sparse: true);

        Assert.Contains("--set-sparse true --allow-unsafe", Assert.Single(runner.Calls));
    }

    [Fact]
    public async Task 大小不合法时根本不调用引擎()
    {
        var runner = new RecordingProcessRunner();
        var service = new DistroService(runner);

        var result = await service.ResizeAsync("Ubuntu", "50PB");

        Assert.False(result.Success);
        Assert.Contains("单位只认", result.Message);
        Assert.Empty(runner.Calls);            // 关键：本地就挡下了
        Assert.Equal(string.Empty, result.CommandLine);
    }

    [Fact]
    public async Task 发行版名为空时不调用引擎()
    {
        var runner = new RecordingProcessRunner();
        var service = new DistroService(runner);

        var result = await service.CompactAsync("  ");

        Assert.False(result.Success);
        Assert.Contains("发行版名不能为空", result.Message);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task 引擎错误里的配置告警行被剥掉()
    {
        var runner = new RecordingProcessRunner
        {
            Handler = (_, _) => new ProcessExecutionResult(
                1,
                string.Empty,
                "wsl: interop.appendWindowsPath:C:\\Users\\x\\.wslconfig 中的键\"12\"未知\r\n不存在具有所提供名称的分发。\r\n",
                false)
        };
        var service = new DistroService(runner);

        var result = await service.CompactAsync("Nope");

        Assert.False(result.Success);
        // 用户看到的必须是真正的错误，而不是"键12未知"
        Assert.Equal("不存在具有所提供名称的分发。", result.Message);
    }

    [Fact]
    public async Task 导出完成时把目标文件大小报成进度()
    {
        // 目标文件预先存在并已有 2 MB：导出结束时收尾那一次进度报的就是它
        var target = Path.Combine(Path.GetTempPath(), "wslcdesktop-export-" + Guid.NewGuid().ToString("N") + ".tar");
        File.WriteAllBytes(target, new byte[2 * 1024 * 1024]);

        try
        {
            var runner = new RecordingProcessRunner();
            var service = new DistroService(runner);

            var progress = new List<DistroExportProgress>();
            var result = await service.ExportAsync("Ubuntu", target, progress.Add);

            Assert.True(result.Success);
            Assert.StartsWith("wsl.exe --export Ubuntu", Assert.Single(runner.Calls));

            var report = Assert.Single(progress);
            Assert.Equal(2 * 1024 * 1024, report.BytesWritten);
            Assert.Equal("2.0 MB", report.Display);
        }
        finally
        {
            if (File.Exists(target)) File.Delete(target);
        }
    }

    [Fact]
    public async Task 导出路径非法时不调用引擎()
    {
        var runner = new RecordingProcessRunner();
        var service = new DistroService(runner);

        var result = await service.ExportAsync("Ubuntu", @"D:\backup\");

        Assert.False(result.Success);
        Assert.Contains("不能只给目录", result.Message);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task 读wsl_conf_文件不存在也算成功且内容为空()
    {
        // 脚本永远退出 0、文件不存在就没有输出 —— 没配过 wsl.conf 是完全正常的状态
        var runner = new RecordingProcessRunner();
        var service = new DistroService(runner);

        var snapshot = await service.ReadWslConfAsync("Ubuntu");

        Assert.True(snapshot.Success);
        Assert.Equal(string.Empty, snapshot.Content);
        Assert.Contains("-d Ubuntu -e sh -c", Assert.Single(runner.Calls));
    }

    [Fact]
    public async Task 读wsl_conf_剥掉前导BOM但保留结尾换行()
    {
        var runner = new RecordingProcessRunner
        {
            Handler = (_, _) => new ProcessExecutionResult(0, "\uFEFF[user]\ndefault = u\n", string.Empty, true)
        };
        var service = new DistroService(runner);

        var snapshot = await service.ReadWslConfAsync("Ubuntu");

        Assert.True(snapshot.Success);
        // 结尾换行不能被吃掉，否则"打开什么都没改、点一次保存"也会被当成有改动
        Assert.Equal("[user]\ndefault = u\n", snapshot.Content);
    }

    [Fact]
    public async Task 读wsl_conf_引擎报错时如实返回原因()
    {
        var runner = new RecordingProcessRunner
        {
            Handler = (_, _) => new ProcessExecutionResult(1, string.Empty, "不存在具有所提供名称的分发。", false)
        };
        var service = new DistroService(runner);

        var snapshot = await service.ReadWslConfAsync("Nope");

        Assert.False(snapshot.Success);
        Assert.Contains("不存在具有所提供名称的分发", snapshot.Error);
    }

    [Fact]
    public async Task 写wsl_conf_先备份再写()
    {
        var runner = new RecordingProcessRunner();
        var service = new DistroService(runner);

        var result = await service.WriteWslConfAsync("Ubuntu", "[user]\ndefault = u\n");

        Assert.True(result.Success);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Contains("cp /etc/wsl.conf /etc/wsl.conf.bak", runner.Calls[0]);
        Assert.Contains("cat << 'WSL_CONF_EOF'", runner.Calls[1]);
    }

    [Fact]
    public async Task 备份失败时不写文件()
    {
        var runner = new RecordingProcessRunner
        {
            Handler = (_, args) => args.Contains("cp /etc/wsl.conf", StringComparison.Ordinal)
                ? new ProcessExecutionResult(1, string.Empty, "权限不足", false)
                : new ProcessExecutionResult(0, string.Empty, string.Empty, true)
        };
        var service = new DistroService(runner);

        var result = await service.WriteWslConfAsync("Ubuntu", "[user]\ndefault = u\n");

        Assert.False(result.Success);
        Assert.Contains("权限不足", result.Message);
        // 只跑了备份那一次；写的那次绝不能发生
        Assert.Single(runner.Calls);
        Assert.DoesNotContain(runner.Calls, c => c.Contains("cat <<", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 取消被包成可读结果而不是异常()
    {
        var runner = new RecordingProcessRunner
        {
            Handler = (_, _) => throw new OperationCanceledException()
        };
        var service = new DistroService(runner);

        var result = await service.MoveAsync("Ubuntu", @"D:\WSL\Ubuntu");

        Assert.False(result.Success);
        Assert.Equal("已取消", result.Message);
    }

    [Fact]
    public void 操作说明写清了后果()
    {
        var move = DistroManagePlanner.DescribeOperation(DistroOperationKind.Move, "Ubuntu");
        Assert.Contains("拷数据", move);

        var conf = DistroManagePlanner.DescribeOperation(DistroOperationKind.WriteWslConf, "Ubuntu");
        Assert.Contains("/etc/wsl.conf.bak", conf);

        var sparse = DistroManagePlanner.DescribeOperation(DistroOperationKind.SetSparse, "Ubuntu");
        Assert.Contains("数据损坏", sparse);
    }
}
