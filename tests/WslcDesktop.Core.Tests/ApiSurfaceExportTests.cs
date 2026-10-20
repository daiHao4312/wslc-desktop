using System.Reflection;
using System.Text;
using Microsoft.WSL.Containers;
using Xunit.Abstractions;

namespace WslcDesktop.Core.Tests;

/// <summary>
/// M0 交付物：官方 API 覆盖矩阵导出 + 契约断言。
///
/// 作用有二：
/// ① 把 <c>Microsoft.WSL.Containers</c> 的完整 API 面导出到 <c>开发资料/API覆盖矩阵.md</c>，
///    作为"哪些能力走 API、哪些必须走 CLI"的决策依据（每次升级 SDK 重跑比对）；
/// ② 断言面板实际依赖的 API 成员仍然存在——官方投影仍在预览期，升级不能悄悄断我们的调用。
/// </summary>
public class ApiSurfaceExportTests
{
    private readonly ITestOutputHelper _output;

    public ApiSurfaceExportTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void TestOfficialApiContract_StillExposesMembersWeDependOn()
    {
        // WslcService：组件检测与版本查询（CompositeWslcClient 用到）
        Assert.NotNull(typeof(WslcService).GetMethod("GetMissingComponents", BindingFlags.Public | BindingFlags.Static));
        Assert.NotNull(typeof(WslcService).GetMethod("GetVersion", BindingFlags.Public | BindingFlags.Static));

        // 版本对象必须有 Major/Minor/Revision（面板拼版本串用）
        var versionType = typeof(WslcService).GetMethod("GetVersion", BindingFlags.Public | BindingFlags.Static)!.ReturnType;
        foreach (var name in new[] { "Major", "Minor", "Revision" })
        {
            Assert.NotNull(versionType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance));
        }
    }

    [Fact]
    public void TestOfficialApiContract_ApiChannelMembersStillExist()
    {
        // M14 官方 API 通道（WslcApiSession / WslcApiSessionFactory）实际调用的成员。
        // 投影仍在预览期、一期一变，这里做编译期之外的兜底：升级 SDK 后断了要立刻红。
        var session = typeof(Session);

        Assert.NotNull(session.GetConstructor(new[] { typeof(SessionSettings) }));
        Assert.NotNull(session.GetMethod("Start", Type.EmptyTypes));
        Assert.NotNull(session.GetMethod("GetImages", Type.EmptyTypes));
        Assert.NotNull(session.GetMethod("PullImageAsync", new[] { typeof(PullImageOptions) }));
        Assert.True(typeof(IDisposable).IsAssignableFrom(session));

        // 会话锚点：必须能按 (名称, 存储路径) 定位到引擎已有的那个会话
        Assert.NotNull(typeof(SessionSettings).GetConstructor(new[] { typeof(string), typeof(string) }));

        // 私有仓库凭据——只有 API 通道能带（CLI 的 pull 没有这个参数）
        Assert.NotNull(typeof(PullImageOptions).GetProperty("RegistryAuth"));

        // 私有仓库认证（M16b）：Session.Authenticate → AuthenticateResult.IdentityToken
        Assert.NotNull(session.GetMethod("Authenticate", new[] { typeof(Uri), typeof(string), typeof(string) }));
        Assert.NotNull(typeof(AuthenticateResult).GetProperty("IdentityToken"));
        Assert.NotNull(typeof(AuthenticateResult).GetProperty("TokenType"));

        // 真实字节数——API 相对 CLI 的唯一硬优势（CLI 只回状态关键字），
        // 也是界面从"不确定进度条"升级到"确定进度条 + 百分比"的依据
        foreach (var name in new[] { "CurrentBytes", "TotalBytes", "Status", "Id" })
        {
            Assert.NotNull(typeof(ImageProgress).GetProperty(name));
        }

        // 容器内可管理的进程（M19）：OpenContainer → CreateProcess → Start/Signal
        // 这是"执行命令能停止"的唯一依据 —— CLI 的 exec 杀不掉容器内进程
        Assert.NotNull(session.GetMethod("OpenContainer", new[] { typeof(string), typeof(ProcessOutputMode) }));
        Assert.NotNull(typeof(Container).GetMethod("CreateProcess", new[] { typeof(ProcessSettings) }));
        Assert.NotNull(typeof(Process).GetMethod("Start", Type.EmptyTypes));
        Assert.NotNull(typeof(Process).GetMethod("Signal", new[] { typeof(Signal) }));
        Assert.NotNull(typeof(Process).GetProperty("Pid"));
        Assert.NotNull(typeof(Process).GetProperty("ExitCode"));
        Assert.NotNull(typeof(ProcessSettings).GetProperty("CommandLine"));
        Assert.NotNull(typeof(ProcessSettings).GetProperty("OutputMode"));

        // ⚠️ 输出的回调参数是**原始字节**、退出回调是 int —— 拼行与 UTF-8 解码必须自己做
        // （见 WslcApiSession 里的 Utf8LineSplitter；这条断言锁住"为什么需要它"）
        var outputInvoke = typeof(ProcessOutputHandler).GetMethod("Invoke")!;
        Assert.Equal(typeof(byte[]), outputInvoke.GetParameters()[0].ParameterType);

        var exitInvoke = typeof(ProcessExitHandler).GetMethod("Invoke")!;
        Assert.Equal(typeof(int), exitInvoke.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void ExportApiSurface_ToProjectDocs()
    {
        var asm = Assembly.Load("wslcsdkcs");
        var sb = new StringBuilder();
        sb.AppendLine("# Microsoft.WSL.Containers API 覆盖矩阵");
        sb.AppendLine();
        sb.AppendLine($"> 由 `ApiSurfaceExportTests.ExportApiSurface_ToProjectDocs` 自动导出（程序集：`{asm.FullName}`）。");
        sb.AppendLine("> 用途：判断哪些能力有官方 API、哪些必须走 CLI 兜底；SDK 升级后重跑本测试比对差异。");
        sb.AppendLine();

        foreach (var type in asm.GetExportedTypes().OrderBy(t => t.FullName))
        {
            var kind = type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsValueType ? "struct" : "class";
            sb.AppendLine($"## {type.FullName} ({kind})");

            if (type.IsEnum)
            {
                foreach (var name in Enum.GetNames(type))
                {
                    sb.AppendLine($"- {name} = {Convert.ToInt64(Enum.Parse(type, name))}");
                }
                sb.AppendLine();
                continue;
            }

            var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            if (constructors.Length > 0)
            {
                sb.AppendLine("### Constructors");
                foreach (var ctor in constructors)
                {
                    sb.AppendLine($"- new({string.Join(", ", ctor.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
                }
            }

            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            if (props.Length > 0)
            {
                sb.AppendLine("### Properties");
                foreach (var prop in props)
                {
                    sb.AppendLine($"- {prop.PropertyType.Name} {prop.Name} {{ {(prop.CanRead ? "get; " : "")}{(prop.CanWrite ? "set; " : "")}}}");
                }
            }

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName)
                .ToList();
            if (methods.Count > 0)
            {
                sb.AppendLine("### Methods");
                foreach (var m in methods)
                {
                    sb.AppendLine($"- {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
                }
            }

            var events = type.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            if (events.Length > 0)
            {
                sb.AppendLine("### Events");
                foreach (var ev in events)
                {
                    sb.AppendLine($"- event {ev.EventHandlerType?.Name} {ev.Name}");
                }
            }

            sb.AppendLine();
        }

        var outputDir = Path.Combine(FindDocsRoot(), "开发资料");
        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, "API覆盖矩阵.md");
        File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);

        _output.WriteLine($"API 覆盖矩阵已导出：{outputPath}");
        Assert.True(File.Exists(outputPath));
    }

    /// <summary>
    /// 找到仓库里的「开发资料」目录所在层级。
    /// 取**最外层**匹配：代码目录下若也存在同名目录（历史遗留），不能把产物写进去。
    /// </summary>
    private static string FindDocsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        var match = string.Empty;

        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "开发资料")))
            {
                match = dir.FullName;
            }

            dir = dir.Parent;
        }

        if (!string.IsNullOrEmpty(match)) return match;

        // 没有「开发资料」目录时退回到解决方案所在目录
        dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WslcDesktop.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }
}
