using System.Diagnostics;
using System.Globalization;
using System.Text;
using WSLC.Panel.Core.Abstractions;

namespace WSLC.Panel.Core.Parsing;

public class CliProcessRunner : IProcessRunner
{
    static CliProcessRunner()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch (Exception)
        {
            // 已注册或平台不支持：ResolveEncoding 里有兜底，不影响主流程
        }
    }

    public Task<ProcessExecutionResult> RunAsync(
        string fileName,
        string arguments,
        IDictionary<string, string>? customEnvironment = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(fileName, arguments, null, null, customEnvironment, cancellationToken);
    }

    public Task<ProcessExecutionResult> RunStreamingAsync(
        string fileName,
        string arguments,
        Action<string> onOutputLine,
        Action<string>? onErrorLine = null,
        IDictionary<string, string>? customEnvironment = null,
        CancellationToken cancellationToken = default,
        Action? onStarted = null)
    {
        return ExecuteAsync(fileName, arguments, onOutputLine, onErrorLine, customEnvironment, cancellationToken, onStarted);
    }

    private static async Task<ProcessExecutionResult> ExecuteAsync(
        string fileName,
        string arguments,
        Action<string>? onOutputLine,
        Action<string>? onErrorLine,
        IDictionary<string, string>? customEnvironment,
        CancellationToken cancellationToken,
        Action? onStarted = null)
    {
        var outputEncoding = ResolveEncoding(fileName);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = outputEncoding,
            StandardErrorEncoding = outputEncoding
        };

        // 统一注入 WSL_UTF8=1，确保 wsl/wslc 输出 UTF-8 而不是 UTF-16LE
        psi.Environment["WSL_UTF8"] = "1";

        if (customEnvironment != null)
        {
            foreach (var (k, v) in customEnvironment)
            {
                psi.Environment[k] = v;
            }
        }

        using var process = new Process { StartInfo = psi };

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        var stdoutDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null)
            {
                stdoutDone.TrySetResult(true);
                return;
            }

            stdoutBuilder.AppendLine(e.Data);
            SafeInvoke(onOutputLine, e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null)
            {
                stderrDone.TrySetResult(true);
                return;
            }

            stderrBuilder.AppendLine(e.Data);
            SafeInvoke(onErrorLine, e.Data);
        };

        try
        {
            if (!process.Start())
            {
                return new ProcessExecutionResult(-1, "", "无法启动进程", false);
            }

            // "进程起来了"是长驻订阅唯一能拿到的"已连接"信号，必须赶在读取之前发出
            SafeInvokeStarted(onStarted);

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdoutDone.Task, stderrDone.Task);

            return new ProcessExecutionResult(
                process.ExitCode,
                stdoutBuilder.ToString(),
                stderrBuilder.ToString(),
                process.ExitCode == 0);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception ex)
        {
            TryKill(process);
            return new ProcessExecutionResult(-1, stdoutBuilder.ToString(), ex.Message, false);
        }
    }

    /// <summary>
    /// 按可执行文件决定输出编码。
    ///
    /// WSL 工具链（wsl / wslc）由 <c>WSL_UTF8=1</c> 保证输出 UTF-8；
    /// 但同一个运行器也用来跑 reg.exe 这类 Windows 自带工具——它们的输出被重定向时
    /// 走的是 **OEM 代码页**（简体中文为 936/GBK）。一律按 UTF-8 解会把非 ASCII 内容解成乱码：
    /// Lxss 注册表里的 BasePath 一旦含中文（中文用户名 / 中文安装路径），
    /// 就会拿不到 VHDX 大小那一列。
    /// </summary>
    private static Encoding ResolveEncoding(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);

        if (name.Equals("wsl", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("wslc", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception)
        {
            // 取不到本地代码页就退回原行为，绝不能因为编码探测让进程起不来
            return Encoding.UTF8;
        }
    }

    private static void SafeInvokeStarted(Action? callback)
    {
        if (callback == null) return;
        try
        {
            callback();
        }
        catch
        {
            // 回调异常不能影响进程读取
        }
    }

    private static void SafeInvoke(Action<string>? callback, string line)
    {
        if (callback == null) return;
        try
        {
            callback(line);
        }
        catch
        {
            // 回调异常不能影响进程读取
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore kill errors
        }
    }
}
