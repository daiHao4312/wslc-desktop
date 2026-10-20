using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Parsing;

/// <summary>
/// 添加发行版的装前检查与参数拼装（纯函数，可完整单测）。
///
/// 为什么单独抽出来：<c>wsl.exe</c> 在沙箱里跑不起来，"点了没反应"这类问题
/// 只能靠参数拼装的可测性来兜住。界面上的命令预览与真实执行共用这里的输出。
///
/// 四种装法的命令形态（已按官方文档核对，见 `开发资料/PLAN-v2.0.md` §8.1）：
/// <code>
///   wsl --import &lt;发行版&gt; &lt;安装目录&gt; &lt;tar 文件&gt; --version 1|2
///   wsl --import &lt;发行版&gt; &lt;安装目录&gt; &lt;vhdx 文件&gt; --vhd
///   wsl --install --from-file &lt;.wsl 文件&gt; [--location &lt;目录&gt;] [--name &lt;名字&gt;]
///   wsl --install -d &lt;在线清单名&gt; [--location &lt;目录&gt;]
/// </code>
///
/// 两个易错点，都是核对文档时才发现的：
/// <list type="number">
/// <item><c>--vhd</c> / <c>--version</c> 是<b>选项</b>，写在三个位置参数<b>之后</b>
/// （<c>wsl --import &lt;Distro&gt; &lt;InstallLocation&gt; &lt;FileName&gt; [options]</c>），
/// 写成 <c>--import --vhd &lt;Distro&gt; ...</c> 不是文档形态。</item>
/// <item><c>.wsl</c> 包<b>同样支持</b> <c>--location</c> 与 <c>--name</c>
/// （包内清单只提供默认值），所以这两项不能因为是 .wsl 就不让填。</item>
/// </list>
/// </summary>
public static class DistroInstallPlanner
{
    /// <summary>tar 家族后缀（大小写不敏感）。</summary>
    private static readonly string[] ArchiveExtensions =
    {
        ".tar", ".tar.gz", ".tgz", ".tar.xz", ".txz", ".tar.bz2", ".tbz2", ".tar.zst"
    };

    public const int DefaultVersion = 2;

    /// <summary>按后缀判断装法。认不出来时按 tar 归档处理，由 <see cref="Plan"/> 报"后缀不认识"。</summary>
    public static DistroInstallSource DetectSource(string? filePath)
    {
        var path = (filePath ?? string.Empty).Trim();
        if (path.Length == 0) return DistroInstallSource.LocalArchive;

        if (path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase)) return DistroInstallSource.LocalVhd;
        if (path.EndsWith(".wsl", StringComparison.OrdinalIgnoreCase)) return DistroInstallSource.WslBundle;
        if (ArchiveExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
        {
            return DistroInstallSource.LocalArchive;
        }

        return DistroInstallSource.LocalArchive;
    }

    public static bool IsRecognizedLocalFile(string? filePath)
    {
        var path = (filePath ?? string.Empty).Trim();
        if (path.Length == 0) return false;

        return path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".wsl", StringComparison.OrdinalIgnoreCase)
            || ArchiveExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>组装检查结果。纯函数：只读文件系统做存在性判断，不执行任何命令。</summary>
    public static DistroInstallPlan Plan(DistroInstallRequest request, IReadOnlyList<DistroModel>? existing = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var problems = new List<string>();

        if (request.Source == DistroInstallSource.Online)
        {
            PlanOnline(request, problems);
        }
        else
        {
            PlanLocal(request, existing ?? Array.Empty<DistroModel>(), problems);
        }

        var arguments = problems.Count == 0
            ? BuildArguments(request)
            : string.Empty;

        return new DistroInstallPlan
        {
            IsValid = problems.Count == 0,
            Problems = problems,
            Mode = DescribeMode(request),
            Arguments = arguments,
            CommandLine = arguments.Length == 0 ? string.Empty : $"wsl {arguments}",
            Steps = BuildSteps(request)
        };
    }

    /// <summary>
    /// 参数拼装。与界面预览共用——<b>不要在别处再拼一份</b>。
    /// 注意这里用 <see cref="WslcCommandBuilder.Quote"/> 而不是裸拼双引号：
    /// 安装目录与文件名经常带空格/中文，转义错了引擎只会回一句"参数无效"。
    /// </summary>
    public static string BuildArguments(DistroInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Source switch
        {
            DistroInstallSource.LocalArchive => JoinParts(
                "--import",
                WslcCommandBuilder.Quote(request.Name.Trim()),
                WslcCommandBuilder.Quote(request.InstallDirectory.Trim()),
                WslcCommandBuilder.Quote(request.FilePath.Trim()),
                VersionArgument(request.Version)),

            DistroInstallSource.LocalVhd => JoinParts(
                "--import",
                WslcCommandBuilder.Quote(request.Name.Trim()),
                WslcCommandBuilder.Quote(request.InstallDirectory.Trim()),
                WslcCommandBuilder.Quote(request.FilePath.Trim()),
                "--vhd"),

            DistroInstallSource.WslBundle => JoinParts(
                "--install --from-file",
                WslcCommandBuilder.Quote(request.FilePath.Trim()),
                LocationArgument(request.InstallDirectory),
                NameArgument(request.Name)),

            DistroInstallSource.Online => JoinParts(
                "--install -d",
                WslcCommandBuilder.Quote(request.OnlineName.Trim()),
                LocationArgument(request.InstallDirectory)),

            _ => string.Empty
        };
    }

    /// <summary>拼参数：跳过空片段，避免预览里出现多余空格或空参数。</summary>
    private static string JoinParts(params string[] parts)
        => string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    public static string DescribeMode(DistroInstallRequest request) => request.Source switch
    {
        DistroInstallSource.LocalArchive => "导入 tar 归档",
        DistroInstallSource.LocalVhd => "导入 VHDX 虚拟磁盘",
        DistroInstallSource.WslBundle => "安装 .wsl 打包文件",
        DistroInstallSource.Online => "在线安装（微软发行版清单）",
        _ => "未知方式"
    };

    // ---------------- 内部 ----------------

    private static void PlanLocal(
        DistroInstallRequest request,
        IReadOnlyList<DistroModel> existing,
        List<string> problems)
    {
        var file = request.FilePath.Trim();

        if (file.Length == 0)
        {
            problems.Add("请选择要安装的文件。");
        }
        else if (!File.Exists(file))
        {
            problems.Add($"找不到安装包：{file}");
        }
        else if (!IsRecognizedLocalFile(file))
        {
            problems.Add("不认识的文件类型。支持 .tar / .tar.gz / .tgz / .tar.xz / .vhdx / .wsl。");
        }

        // .wsl 包的名字与位置都是**可选**的（包内清单有默认值），但填了就要合法
        if (request.Source == DistroInstallSource.WslBundle)
        {
            var bundleName = request.Name.Trim();
            if (bundleName.Length > 0 &&
                existing.Any(d => string.Equals(d.Name, bundleName, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"已存在同名发行版「{bundleName}」，请换一个名字（或留空用包内默认名）。");
            }

            var bundleDirectory = request.InstallDirectory.Trim();
            if (bundleDirectory.Length > 0 && !IsAbsolutePath(bundleDirectory))
            {
                problems.Add("安装目录必须是绝对路径（留空则用引擎默认位置）。");
            }

            return;
        }

        var name = request.Name.Trim();
        if (name.Length == 0)
        {
            problems.Add("请填写发行版名（导入时必须指定，否则无法注册）。");
        }
        else if (existing.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add($"已存在同名发行版「{name}」，请换一个名字（同名导入会失败）。");
        }

        var directory = request.InstallDirectory.Trim();
        if (directory.Length == 0)
        {
            problems.Add("请选择安装目录。");
        }
        else
        {
            try
            {
                if (!IsAbsolutePath(directory))
                {
                    problems.Add("安装目录必须是绝对路径。");
                }
                else if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    // wsl --import 要求目标目录为空（它会把自己的 vhdx 放进去）
                    problems.Add($"安装目录已存在且非空：{directory}。请换一个空目录。");
                }
            }
            catch (Exception ex)
            {
                problems.Add($"无法访问安装目录：{ex.Message}");
            }
        }

        // 版本只在 tar 导入时才是可选项：.vhdx 只能是 WSL 2，.wsl 由包内清单决定
        if (UsesVersionSelector(request.Source) && request.Version is not (1 or 2))
        {
            problems.Add("WSL 版本只能是 1 或 2。");
        }
    }

    private static void PlanOnline(DistroInstallRequest request, List<string> problems)
    {
        if (request.OnlineName.Trim().Length == 0)
        {
            problems.Add("请选择要安装的发行版。");
        }

        var directory = request.InstallDirectory.Trim();
        if (directory.Length == 0) return;   // 在线安装允许留空 = 用引擎默认位置

        try
        {
            if (!IsAbsolutePath(directory))
            {
                problems.Add("安装目录必须是绝对路径（留空则用引擎默认位置）。");
            }
        }
        catch (Exception ex)
        {
            problems.Add($"无法访问安装目录：{ex.Message}");
        }
    }

    /// <summary>
    /// Windows 绝对路径：<c>D:\x</c> / <c>D:/x</c> / UNC <c>\\server\share</c>。
    ///
    /// 不用 <see cref="Path.IsPathRooted"/>：它对 <c>\foo</c> 也返回 true，
    /// 而那是"当前盘根下"的相对写法，交给 wsl.exe 会落到意想不到的位置。
    /// </summary>
    private static bool IsAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;

        return path.Length >= 3
            && char.IsLetter(path[0])
            && path[1] == ':'
            && (path[2] == '\\' || path[2] == '/');
    }

    private static List<string> BuildSteps(DistroInstallRequest request) => request.Source switch
    {
        DistroInstallSource.LocalArchive => new List<string>
        {
            "校验安装包与目标目录",
            $"把归档导入为发行版「{request.Name.Trim()}」（WSL {request.Version}）",
            "刷新发行版列表"
        },
        DistroInstallSource.LocalVhd => new List<string>
        {
            "校验虚拟磁盘与目标目录",
            $"把 VHDX 注册为发行版「{request.Name.Trim()}」（仅 WSL 2，不会转换格式）",
            "刷新发行版列表"
        },
        DistroInstallSource.WslBundle => new List<string>
        {
            "校验 .wsl 打包文件",
            request.Name.Trim().Length > 0
                ? $"以「{request.Name.Trim()}」为名安装（覆盖包内默认名）"
                : "按包内清单的默认名安装",
            request.InstallDirectory.Trim().Length > 0
                ? $"安装到 {request.InstallDirectory.Trim()}"
                : "安装到引擎默认位置",
            "首次启动时包内清单可能要求创建用户账号或确认许可",
            "刷新发行版列表"
        },
        DistroInstallSource.Online => new List<string>
        {
            $"下载 {request.OnlineName.Trim()}",
            request.InstallDirectory.Trim().Length > 0
                ? $"安装到 {request.InstallDirectory.Trim()}"
                : "安装到引擎默认位置",
            "首次启动可能需要创建用户账号",
            "刷新发行版列表"
        },
        _ => new List<string>()
    };

    private static string VersionArgument(int version)
        => version is 1 or 2 ? $"--version {version}" : string.Empty;

    private static string LocationArgument(string directory)
        => directory.Trim().Length == 0 ? string.Empty : $"--location {WslcCommandBuilder.Quote(directory.Trim())}";

    private static string NameArgument(string name)
        => name.Trim().Length == 0 ? string.Empty : $"--name {WslcCommandBuilder.Quote(name.Trim())}";

    /// <summary>该来源是否用得上 WSL 版本选择（.vhdx 只能是 WSL 2，.wsl 由包内清单决定）。</summary>
    public static bool UsesVersionSelector(DistroInstallSource source)
        => source == DistroInstallSource.LocalArchive;
}
