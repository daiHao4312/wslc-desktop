using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;
using WslcDesktop.Core.Services;

namespace WslcDesktop.Core.Tests;

/// <summary>
/// M17 添加发行版：装法分岔、装前检查、参数拼装、在线清单解析、服务装配。
///
/// <c>wsl.exe</c> 在开发沙箱里跑不起来，所以这一层必须靠纯函数单测兜住——
/// 界面上的命令预览与真实执行共用 <see cref="DistroInstallPlanner.BuildArguments"/>，
/// 参数拼错在这里就会红，而不是等到用户点下去只收到一句"参数无效"。
/// </summary>
public class DistroInstallTests
{
    // ---------------- 替身 ----------------

    /// <summary>造一个临时工作目录，用完删掉。</summary>
    private sealed class TempWorkspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "wslcdesktop-distro-" + Guid.NewGuid().ToString("N"));

        public TempWorkspace() => Directory.CreateDirectory(Root);

        public string CreateFile(string name)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllText(path, "dummy");
            return path;
        }

        public string Dir(string name)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* 清理失败无所谓 */ }
        }
    }

    // ---------------- 装法分岔 ----------------

    [Theory]
    [InlineData("ubuntu.tar", DistroInstallSource.LocalArchive)]
    [InlineData("ubuntu.tar.gz", DistroInstallSource.LocalArchive)]
    [InlineData("ubuntu.TGZ", DistroInstallSource.LocalArchive)]
    [InlineData("ubuntu.tar.xz", DistroInstallSource.LocalArchive)]
    [InlineData(@"C:\pkg\kali.vhdx", DistroInstallSource.LocalVhd)]
    [InlineData(@"C:\pkg\KALI.VHDX", DistroInstallSource.LocalVhd)]
    [InlineData(@"C:\pkg\alpine.wsl", DistroInstallSource.WslBundle)]
    public void 按后缀分岔装法(string file, DistroInstallSource expected)
    {
        Assert.Equal(expected, DistroInstallPlanner.DetectSource(file));
    }

    [Theory]
    [InlineData("ubuntu.tar", true)]
    [InlineData("ubuntu.vhdx", true)]
    [InlineData("ubuntu.wsl", true)]
    [InlineData("ubuntu.zip", false)]
    [InlineData("ubuntu", false)]
    [InlineData("", false)]
    public void 只认识已知后缀(string file, bool expected)
    {
        Assert.Equal(expected, DistroInstallPlanner.IsRecognizedLocalFile(file));
    }

    // ---------------- 参数拼装（精确串） ----------------

    [Fact]
    public void 导入tar的参数串()
    {
        var arguments = DistroInstallPlanner.BuildArguments(new DistroInstallRequest
        {
            Source = DistroInstallSource.LocalArchive,
            FilePath = @"D:\pkg\ubuntu.tar.gz",
            Name = "Ubuntu-24.04",
            InstallDirectory = @"C:\WSL\Ubuntu",
            Version = 2
        });

        // Quote 只在必要时加引号：名字不含空格就原样输出，路径含反斜杠会被引起来
        Assert.Equal(
            "--import Ubuntu-24.04 \"C:\\WSL\\Ubuntu\" \"D:\\pkg\\ubuntu.tar.gz\" --version 2",
            arguments);
    }

    [Fact]
    public void 导入vhdx时_vhd_开关写在位置参数之后()
    {
        // 官方语法是 wsl --import <Distro> <InstallLocation> <FileName> [options]，
        // 选项在三个位置参数之后——写成 --import --vhd <Distro> ... 不是文档形态
        var arguments = DistroInstallPlanner.BuildArguments(new DistroInstallRequest
        {
            Source = DistroInstallSource.LocalVhd,
            FilePath = @"D:\pkg\kali.vhdx",
            Name = "Kali",
            InstallDirectory = @"C:\WSL\Kali",
            Version = 1
        });

        Assert.Equal(
            "--import Kali \"C:\\WSL\\Kali\" \"D:\\pkg\\kali.vhdx\" --vhd",
            arguments);
    }

    [Fact]
    public void wsl打包文件可以留空名字与位置()
    {
        var arguments = DistroInstallPlanner.BuildArguments(new DistroInstallRequest
        {
            Source = DistroInstallSource.WslBundle,
            FilePath = @"D:\pkg\alpine.wsl"
        });

        Assert.Equal("--install --from-file \"D:\\pkg\\alpine.wsl\"", arguments);
    }

    [Fact]
    public void wsl打包文件也支持覆盖名字与位置()
    {
        // .wsl 的名字与位置来自包内清单，但命令行可以覆盖它们——不能因为是 .wsl 就不让填
        var arguments = DistroInstallPlanner.BuildArguments(new DistroInstallRequest
        {
            Source = DistroInstallSource.WslBundle,
            FilePath = @"D:\pkg\ubuntu-24.04.wsl",
            Name = "Ubuntu-24.04",
            InstallDirectory = @"D:\WSL\Ubuntu-24.04"
        });

        Assert.Equal(
            "--install --from-file \"D:\\pkg\\ubuntu-24.04.wsl\" --location \"D:\\WSL\\Ubuntu-24.04\" --name Ubuntu-24.04",
            arguments);
    }

    [Theory]
    [InlineData(DistroInstallSource.LocalArchive, true)]
    [InlineData(DistroInstallSource.LocalVhd, false)]
    [InlineData(DistroInstallSource.WslBundle, false)]
    [InlineData(DistroInstallSource.Online, false)]
    public void 只有tar导入才有版本可选(DistroInstallSource source, bool expected)
    {
        Assert.Equal(expected, DistroInstallPlanner.UsesVersionSelector(source));
    }

    [Fact]
    public void 在线安装不带位置时不加_location()
    {
        var arguments = DistroInstallPlanner.BuildArguments(new DistroInstallRequest
        {
            Source = DistroInstallSource.Online,
            OnlineName = "Ubuntu-24.04"
        });

        Assert.Equal("--install -d Ubuntu-24.04", arguments);
    }

    [Fact]
    public void 在线安装带位置时加_location()
    {
        var arguments = DistroInstallPlanner.BuildArguments(new DistroInstallRequest
        {
            Source = DistroInstallSource.Online,
            OnlineName = "Ubuntu-24.04",
            InstallDirectory = @"D:\WSL\Ubuntu"
        });

        Assert.Equal("--install -d Ubuntu-24.04 --location \"D:\\WSL\\Ubuntu\"", arguments);
    }

    [Fact]
    public void 含空格的安装目录被正确引用()
    {
        var arguments = DistroInstallPlanner.BuildArguments(new DistroInstallRequest
        {
            Source = DistroInstallSource.LocalArchive,
            FilePath = @"D:\my pkg\u buntu.tar",
            Name = "Ubuntu 24.04 LTS",
            InstallDirectory = @"C:\Program Files\WSL Data\Ubuntu",
            Version = 2
        });

        // 发行版名本身可以含空格（本机就存在 "Ubuntu 24.04 LTS" 这种名字）
        Assert.Contains("\"Ubuntu 24.04 LTS\"", arguments);
        Assert.Contains("\"C:\\Program Files\\WSL Data\\Ubuntu\"", arguments);
        Assert.EndsWith("--version 2", arguments);
    }

    // ---------------- 装前检查 ----------------

    [Fact]
    public void 空输入逐条报出问题且不拼命令()
    {
        var plan = DistroInstallPlanner.Plan(new DistroInstallRequest
        {
            Source = DistroInstallSource.LocalArchive
        });

        Assert.False(plan.IsValid);
        Assert.True(plan.Problems.Count >= 3);          // 文件、名字、目录
        Assert.Equal(string.Empty, plan.CommandLine);   // 不合法就不给可复制的命令
        Assert.NotEmpty(plan.Steps);                    // 步骤预览照给，让用户知道会发生什么
    }

    [Fact]
    public void 文件不存在要报出来()
    {
        using var space = new TempWorkspace();

        var plan = DistroInstallPlanner.Plan(new DistroInstallRequest
        {
            Source = DistroInstallSource.LocalArchive,
            FilePath = Path.Combine(space.Root, "没有这个文件.tar.gz"),
            Name = "Ubuntu",
            InstallDirectory = Path.Combine(space.Root, "install")
        });

        Assert.False(plan.IsValid);
        Assert.Contains(plan.Problems, p => p.Contains("找不到安装包"));
    }

    [Fact]
    public void 后缀不认识要报出来()
    {
        using var space = new TempWorkspace();
        var file = space.CreateFile("ubuntu.zip");

        var plan = DistroInstallPlanner.Plan(new DistroInstallRequest
        {
            Source = DistroInstallSource.LocalArchive,
            FilePath = file,
            Name = "Ubuntu",
            InstallDirectory = Path.Combine(space.Root, "install")
        });

        Assert.False(plan.IsValid);
        Assert.Contains(plan.Problems, p => p.Contains("不认识的文件类型"));
    }

    [Fact]
    public void 同名发行版要被拦下()
    {
        using var space = new TempWorkspace();
        var file = space.CreateFile("ubuntu.tar.gz");

        var plan = DistroInstallPlanner.Plan(
            new DistroInstallRequest
            {
                Source = DistroInstallSource.LocalArchive,
                FilePath = file,
                Name = "ubuntu-24.04",
                InstallDirectory = Path.Combine(space.Root, "install")
            },
            new[] { new DistroModel { Name = "Ubuntu-24.04" } });   // 大小写不同也算重名

        Assert.False(plan.IsValid);
        Assert.Contains(plan.Problems, p => p.Contains("已存在同名发行版"));
    }

    [Fact]
    public void 安装目录必须绝对且为空()
    {
        using var space = new TempWorkspace();
        var file = space.CreateFile("ubuntu.tar.gz");

        var relative = DistroInstallPlanner.Plan(new DistroInstallRequest
        {
            Source = DistroInstallSource.LocalArchive,
            FilePath = file,
            Name = "Ubuntu",
            InstallDirectory = "install"
        });
        Assert.Contains(relative.Problems, p => p.Contains("绝对路径"));

        // 非空目录：wsl --import 会把自己的 vhdx 放进去，非空会失败
        var occupied = space.Dir("occupied");
        File.WriteAllText(Path.Combine(occupied, "keep.txt"), "x");

        var notEmpty = DistroInstallPlanner.Plan(new DistroInstallRequest
        {
            Source = DistroInstallSource.LocalArchive,
            FilePath = file,
            Name = "Ubuntu",
            InstallDirectory = occupied
        });
        Assert.Contains(notEmpty.Problems, p => p.Contains("非空"));
    }

    [Fact]
    public void 合法输入给出可执行计划()
    {
        using var space = new TempWorkspace();
        var file = space.CreateFile("ubuntu.tar.gz");
        var target = Path.Combine(space.Root, "install");

        var plan = DistroInstallPlanner.Plan(new DistroInstallRequest
        {
            Source = DistroInstallSource.LocalArchive,
            FilePath = file,
            Name = "Ubuntu",
            InstallDirectory = target,
            Version = 2
        });

        Assert.True(plan.IsValid);
        Assert.Empty(plan.Problems);
        Assert.Equal("导入 tar 归档", plan.Mode);
        Assert.StartsWith("wsl --import ", plan.CommandLine);
        Assert.NotEmpty(plan.Steps);
    }

    [Fact]
    public void wsl打包文件不需要名字与目录也能通过检查()
    {
        using var space = new TempWorkspace();
        var file = space.CreateFile("alpine.wsl");

        var plan = DistroInstallPlanner.Plan(new DistroInstallRequest
        {
            Source = DistroInstallSource.WslBundle,
            FilePath = file
        });

        Assert.True(plan.IsValid);
        Assert.Equal("安装 .wsl 打包文件", plan.Mode);
        Assert.Equal($"wsl --install --from-file \"{file}\"", plan.CommandLine);
    }

    [Fact]
    public void 在线安装没选发行版要报出来()
    {
        var plan = DistroInstallPlanner.Plan(new DistroInstallRequest
        {
            Source = DistroInstallSource.Online
        });

        Assert.False(plan.IsValid);
        Assert.Contains(plan.Problems, p => p.Contains("请选择要安装的发行版"));
    }

    [Fact]
    public void 在线安装允许留空位置()
    {
        var plan = DistroInstallPlanner.Plan(new DistroInstallRequest
        {
            Source = DistroInstallSource.Online,
            OnlineName = "Ubuntu-24.04"
        });

        Assert.True(plan.IsValid);
        Assert.Equal("在线安装（微软发行版清单）", plan.Mode);
    }

    [Fact]
    public void 版本号只能是1或2()
    {
        using var space = new TempWorkspace();
        var file = space.CreateFile("ubuntu.tar.gz");

        var plan = DistroInstallPlanner.Plan(new DistroInstallRequest
        {
            Source = DistroInstallSource.LocalArchive,
            FilePath = file,
            Name = "Ubuntu",
            InstallDirectory = Path.Combine(space.Root, "install"),
            Version = 3
        });

        Assert.Contains(plan.Problems, p => p.Contains("只能是 1 或 2"));
    }

    // ---------------- 在线清单解析 ----------------

    [Fact]
    public void 解析在线清单跳过中文前言与表头()
    {
        const string output =
            "以下是可安装的有效分发的列表。\r\n" +
            "使用\"wsl.exe --install <Distro>\"进行安装。\r\n" +
            "\r\n" +
            "NAME                                   FRIENDLY NAME\r\n" +
            "Ubuntu                                 Ubuntu\r\n" +
            "Ubuntu-24.04                           Ubuntu 24.04 LTS\r\n" +
            "Debian                                 Debian GNU/Linux\r\n" +
            "kali-linux                             Kali Linux Rolling\r\n" +
            "OracleLinux_8_5                        Oracle Linux 8.5\r\n";

        var list = DistroOutputParser.ParseOnlineList(output);

        Assert.Equal(5, list.Count);
        Assert.Equal("Ubuntu", list[0].Name);
        Assert.Equal("Ubuntu", list[0].FriendlyName);
        Assert.Equal("Ubuntu-24.04", list[1].Name);
        Assert.Equal("Ubuntu 24.04 LTS", list[1].FriendlyName);
        Assert.Equal("OracleLinux_8_5", list[4].Name);
        Assert.Equal("Oracle Linux 8.5", list[4].FriendlyName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\r\n")]
    [InlineData("以下是可安装的有效分发的列表。")]
    public void 在线清单没有数据行时返回空列表(string output)
    {
        Assert.Empty(DistroOutputParser.ParseOnlineList(output));
    }

    [Fact]
    public void 在线清单展示文案带友好名()
    {
        var item = new OnlineDistroModel { Name = "Ubuntu-24.04", FriendlyName = "Ubuntu 24.04 LTS" };

        Assert.Equal("Ubuntu 24.04 LTS（Ubuntu-24.04）", item.Display);
        Assert.Equal("Debian", new OnlineDistroModel { Name = "Debian" }.Display);
    }

    // ---------------- 服务装配 ----------------

    [Fact]
    public async Task 在线清单走_wsl_list_online()
    {
        var runner = new RecordingProcessRunner
        {
            Handler = (_, _) => new ProcessExecutionResult(
                0,
                "NAME                 FRIENDLY NAME\r\nUbuntu               Ubuntu\r\n",
                string.Empty,
                true)
        };
        var service = new DistroService(runner);

        var list = await service.GetOnlineDistrosAsync();

        Assert.Equal("wsl.exe --list --online", Assert.Single(runner.Calls));
        Assert.Equal("Ubuntu", Assert.Single(list).Name);
    }

    [Fact]
    public async Task 安装走拼装出来的参数并把输出逐行回调()
    {
        using var space = new TempWorkspace();
        var file = space.CreateFile("ubuntu.tar.gz");

        var runner = new RecordingProcessRunner
        {
            Handler = (_, args) => args.StartsWith("--list", StringComparison.Ordinal)
                ? new ProcessExecutionResult(0, string.Empty, string.Empty, true)       // 重名检查用的列表
                : new ProcessExecutionResult(0, "正在导入…\n完成\n", string.Empty, true)
        };
        var service = new DistroService(runner);

        var lines = new List<string>();
        var result = await service.InstallAsync(
            new DistroInstallRequest
            {
                Source = DistroInstallSource.LocalArchive,
                FilePath = file,
                Name = "Ubuntu",
                InstallDirectory = Path.Combine(space.Root, "install"),
                Version = 2
            },
            lines.Add);

        Assert.True(result.Success);
        Assert.Contains("--import Ubuntu", runner.Calls[^1]);
        Assert.Equal(new[] { "正在导入…", "完成" }, lines);
    }

    [Fact]
    public async Task 装前检查不过时根本不调用引擎()
    {
        var runner = new RecordingProcessRunner();
        var service = new DistroService(runner);

        var result = await service.InstallAsync(new DistroInstallRequest
        {
            Source = DistroInstallSource.LocalArchive      // 什么都没填
        });

        Assert.False(result.Success);
        Assert.Contains("请选择要安装的文件", result.Message);
        Assert.DoesNotContain(runner.Calls, c => c.Contains("--import"));
    }

    [Fact]
    public async Task 取消安装返回可读结果而不是抛异常()
    {
        using var space = new TempWorkspace();
        var file = space.CreateFile("ubuntu.tar.gz");

        var runner = new RecordingProcessRunner
        {
            Handler = (_, args) => args.StartsWith("--list", StringComparison.Ordinal)
                ? new ProcessExecutionResult(0, string.Empty, string.Empty, true)
                : throw new OperationCanceledException()
        };
        var service = new DistroService(runner);

        var result = await service.InstallAsync(new DistroInstallRequest
        {
            Source = DistroInstallSource.LocalArchive,
            FilePath = file,
            Name = "Ubuntu",
            InstallDirectory = Path.Combine(space.Root, "install")
        });

        Assert.False(result.Success);
        Assert.Equal("已取消安装", result.Message);
    }

    [Fact]
    public async Task 安装失败时给出引擎的第一行可读输出()
    {
        using var space = new TempWorkspace();
        var file = space.CreateFile("ubuntu.tar.gz");

        var runner = new RecordingProcessRunner
        {
            Handler = (_, args) => args.StartsWith("--list", StringComparison.Ordinal)
                ? new ProcessExecutionResult(0, string.Empty, string.Empty, true)
                : new ProcessExecutionResult(
                    1,
                    string.Empty,
                    "版权所有 (C) Microsoft Corporation。保留所有权利。\r\n安装失败：目标目录已存在。\r\n",
                    false)
        };
        var service = new DistroService(runner);

        var result = await service.InstallAsync(new DistroInstallRequest
        {
            Source = DistroInstallSource.LocalArchive,
            FilePath = file,
            Name = "Ubuntu",
            InstallDirectory = Path.Combine(space.Root, "install")
        });

        Assert.False(result.Success);
        Assert.Equal("安装失败：目标目录已存在。", result.Message);
    }
}
