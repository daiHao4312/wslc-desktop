using System.Reflection;
using System.Text;
using Microsoft.WSL.Containers;
using Xunit.Abstractions;

namespace WSLC.Panel.Core.Tests;

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
            if (File.Exists(Path.Combine(dir.FullName, "WSLC-Panel.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }
}
