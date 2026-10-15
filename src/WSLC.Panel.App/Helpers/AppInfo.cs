using System.Reflection;

namespace WSLC_Panel_App.Helpers;

/// <summary>
/// 应用自身的版本与标识信息，**单一来源**是 csproj 里的 &lt;Version&gt;。
///
/// 视图层不许再写死版本字符串：改版本只改 csproj 一处，
/// 关于页、更新检查、标题栏都用这里读出来的值，避免三处各写一份而漂移。
/// </summary>
internal static class AppInfo
{
    public const string DisplayName = "WSLC 面板工具";

    /// <summary>形如 "1.0.0"。优先取 InformationalVersion（去掉 +commit 后缀），退回程序集版本。</summary>
    public static string Version { get; } = ResolveVersion();

    /// <summary>形如 "1.0.0.0"。</summary>
    public static string FileVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";

    /// <summary>当前进程 exe 的完整路径（单文件发布下同样有效）。</summary>
    public static string ProcessPath { get; } =
        Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;

    private static string ResolveVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // SourceLink/版本戳会追加 "+<commit>"，展示与比较都只需要前面的语义版本
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
