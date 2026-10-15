using System.Diagnostics;

namespace WSLC_Panel_App.Helpers;

/// <summary>
/// 把交互式命令交给 Windows Terminal（wt）执行。
///
/// 设计取舍（继承参考项目的教训、按 §13 决策 2）：M7 先不内嵌 ConPTY 终端，
/// 而是"接管 Windows Terminal"——vi/top 这类交互式程序在真终端里才能真正可用。
/// wt 不可用（未安装）时退回到 wsl.exe 直接开控制台窗口，保证功能不丢。
/// </summary>
public static class TerminalLauncher
{
    /// <summary>在容器里打开交互式 shell。</summary>
    public static bool OpenContainerShell(string containerIdOrName)
        => Launch($"wslc exec -it {Quote(containerIdOrName)} sh", "wslc.exe", $"exec -it {Quote(containerIdOrName)} sh");

    /// <summary>跟随容器日志（logs --follow 永不返回，只能放真终端）。</summary>
    public static bool FollowContainerLogs(string containerIdOrName)
        => Launch($"wslc logs -f {Quote(containerIdOrName)}", "wslc.exe", $"logs -f {Quote(containerIdOrName)}");

    /// <summary>进入发行版。</summary>
    public static bool OpenDistroShell(string distroName)
        => Launch($"wsl -d {Quote(distroName)}", "wsl.exe", $"-d {Quote(distroName)}");

    /// <summary>打开交互式命令提示符，方便用户手敲 wslc 命令。</summary>
    public static bool OpenWslcPrompt()
        => Launch("wslc --help", "cmd.exe", "/k wslc --help");

    /// <summary>
    /// 用 wt 新开标签执行；失败则退回默认控制台。
    /// </summary>
    public static bool Launch(string commandLine, string fallbackFileName, string fallbackArgs = "")
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wt.exe",
                Arguments = $"new-tab -- {commandLine}",
                UseShellExecute = true,
                CreateNoWindow = true
            };

            Process.Start(psi);
            return true;
        }
        catch
        {
            return LaunchFallback(fallbackFileName, fallbackArgs);
        }
    }

    private static bool LaunchFallback(string fileName, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>用默认浏览器打开地址（端口一键打开）。</summary>
    public static bool OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>在资源管理器中定位文件/目录。</summary>
    public static bool RevealInExplorer(string path)
    {
        try
        {
            var target = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = target,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>复制文本到剪贴板（升级命令一键复制）。</summary>
    public static void CopyToClipboard(string text)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch
        {
            // 剪贴板被占用时静默失败，不打断主流程
        }
    }

    private static string Quote(string value) => $"\"{value}\"";
}
