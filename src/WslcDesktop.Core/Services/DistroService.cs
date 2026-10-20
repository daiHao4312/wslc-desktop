using System.Diagnostics;
using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Core.Services;

/// <summary>
/// 发行版管理：wsl.exe（列表/启停/设默认/注销）+ 注册表 Lxss（安装位置、VHDX 大小）。
/// 列表优先用 wsl.exe，注册表只用于补充 wsl.exe 给不出的信息（安装目录、磁盘占用）。
/// </summary>
public class DistroService : IDistroService
{
    private const string LxssRegistryPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss";

    private readonly IProcessRunner _runner;
    private readonly string _wslExecutable;
    private readonly string _regExecutable;

    public DistroService(
        IProcessRunner? runner = null,
        string wslExecutable = "wsl.exe",
        string regExecutable = "reg.exe")
    {
        _runner = runner ?? new CliProcessRunner();
        _wslExecutable = wslExecutable;
        _regExecutable = regExecutable;
    }

    public async Task<IReadOnlyList<DistroModel>> GetDistrosAsync(CancellationToken cancellationToken = default)
    {
        // wsl -l -v 与注册表查询**互不依赖**，并行发起：
        // 串行时每刷一次发行版页要等两次进程启动（wsl.exe 起一次就几百毫秒）。
        var listTask = Start("--list --verbose", cancellationToken);
        var registryTask = Start($"query {LxssRegistryPath} /s", cancellationToken, registry: true);

        var listResult = await listTask;
        if (!listResult.IsSuccess && string.IsNullOrWhiteSpace(listResult.StandardOutput))
        {
            Observe(registryTask);
            return Array.Empty<DistroModel>();
        }

        var distros = DistroOutputParser.ParseList(listResult.StandardOutput);
        if (distros.Count == 0)
        {
            Observe(registryTask);
            return distros;
        }

        // 注册表查询失败（企业策略/权限）时仍然返回 wsl.exe 的结果
        var registryResult = await registryTask;
        if (registryResult.IsSuccess)
        {
            distros = DistroOutputParser.MergeRegistryInfo(distros, registryResult.StandardOutput);
        }

        return distros;
    }

    /// <summary>
    /// 起一个进程调用并**兜住同步异常**。
    ///
    /// 并行发起两个调用时，第一个 await 之前就可能抛出（<see cref="IProcessRunner"/> 的实现
    /// 允许同步抛，例如取消时直接抛 <see cref="OperationCanceledException"/>）。
    /// 同步抛会让另一路刚起的任务无人认领，也会把调用方从"并行两路"变成"一条都没跑完就炸"。
    /// 统一转成 faulted task 后，异常仍然在 await 处冒出来 —— 语义不变，因果清楚。
    /// </summary>
    private Task<ProcessExecutionResult> Start(string arguments, CancellationToken cancellationToken, bool registry = false)
    {
        try
        {
            return _runner.RunAsync(registry ? _regExecutable : _wslExecutable, arguments, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            return Task.FromException<ProcessExecutionResult>(ex);
        }
    }

    /// <summary>
    /// 提前返回时把另一路任务"接住"：不 await 也不观察的话，它抛出的异常会变成
    /// 未观察的任务异常（<c>TaskScheduler.UnobservedTaskException</c>），排查时很难对上因果。
    /// </summary>
    private static void Observe(Task task)
        => _ = task.ContinueWith(
            t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    public async Task<bool> StartAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        // --exec /bin/true：只把发行版拉起来，不驻留交互 shell
        var result = await _runner.RunAsync(
            _wslExecutable,
            $"-d {WslcCommandBuilder.Quote(name)} --exec /bin/true",
            cancellationToken: cancellationToken);

        return result.IsSuccess;
    }

    public async Task<bool> TerminateAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        var result = await _runner.RunAsync(
            _wslExecutable,
            $"--terminate {WslcCommandBuilder.Quote(name)}",
            cancellationToken: cancellationToken);

        return result.IsSuccess;
    }

    public async Task<bool> SetDefaultAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        var result = await _runner.RunAsync(
            _wslExecutable,
            $"--set-default {WslcCommandBuilder.Quote(name)}",
            cancellationToken: cancellationToken);

        return result.IsSuccess;
    }

    public async Task<bool> UnregisterAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        var result = await _runner.RunAsync(
            _wslExecutable,
            $"--unregister {WslcCommandBuilder.Quote(name)}",
            cancellationToken: cancellationToken);

        return result.IsSuccess;
    }

    public async Task<string> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(_wslExecutable, "--status", cancellationToken: cancellationToken);
        return string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput;
    }

    // ---------- 添加发行版（M17）----------

    public async Task<IReadOnlyList<OnlineDistroModel>> GetOnlineDistrosAsync(CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(_wslExecutable, "--list --online", cancellationToken: cancellationToken);

        // 清单在老版本走 stdout，某些环境会全丢到 stderr（引擎自己也把它当"帮助文本"）
        var payload = string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput;
        return DistroOutputParser.ParseOnlineList(payload);
    }

    public async Task<DistroInstallPlan> PlanInstallAsync(DistroInstallRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 重名检查要靠当前列表；列表拿不到（引擎不可用）时不因此拦下安装——那是引擎自己的事
        var existing = await GetDistrosAsync(cancellationToken);

        return DistroInstallPlanner.Plan(request, existing);
    }

    public async Task<DistroInstallResult> InstallAsync(
        DistroInstallRequest request,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var plan = await PlanInstallAsync(request, cancellationToken);
        if (!plan.IsValid)
        {
            return new DistroInstallResult
            {
                Success = false,
                Message = string.Join("；", plan.Problems),
                CommandLine = plan.CommandLine
            };
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // 流式执行：安装可能持续数分钟，把引擎输出实时喂给界面
            var result = await _runner.RunStreamingAsync(
                _wslExecutable,
                plan.Arguments,
                line => onOutput?.Invoke(line),
                line => onOutput?.Invoke(line),
                cancellationToken: cancellationToken);

            stopwatch.Stop();

            return new DistroInstallResult
            {
                Success = result.IsSuccess,
                Message = result.IsSuccess
                    ? "安装完成"
                    : FirstReadableLine(result.StandardError, result.StandardOutput) ?? "安装失败（引擎未返回原因）",
                CommandLine = plan.CommandLine,
                Duration = stopwatch.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new DistroInstallResult
            {
                Success = false,
                Message = "已取消安装",
                CommandLine = plan.CommandLine,
                Duration = stopwatch.Elapsed
            };
        }
    }

    // ---------- 发行版详情（M18）----------

    /// <summary>导出时轮询目标文件大小的间隔。<c>wsl --export</c> 不打百分比，只能这么拿进度。</summary>
    private static readonly TimeSpan ExportPollInterval = TimeSpan.FromMilliseconds(500);

    public Task<DistroOperationResult> CompactAsync(string name, CancellationToken cancellationToken = default)
        => RunManageAsync(name, DistroManagePlanner.BuildCompactArguments, cancellationToken);

    public Task<DistroOperationResult> SetSparseAsync(string name, bool sparse, CancellationToken cancellationToken = default)
        => RunManageAsync(name, n => DistroManagePlanner.BuildSparseArguments(n, sparse), cancellationToken);

    public async Task<DistroOperationResult> ResizeAsync(string name, string size, CancellationToken cancellationToken = default)
    {
        if (!DistroManagePlanner.TryValidateDistroName(name, out var nameError)) return Failure(nameError);

        // 格式不规范就本地挡下，不要丢给引擎去报一句含糊的错
        if (!DistroManagePlanner.TryNormalizeSize(size, out var normalized, out var sizeError)) return Failure(sizeError);

        return await RunManageAsync(name, n => DistroManagePlanner.BuildResizeArguments(n, normalized), cancellationToken);
    }

    public async Task<DistroOperationResult> MoveAsync(string name, string targetDirectory, CancellationToken cancellationToken = default)
    {
        if (!DistroManagePlanner.TryValidateDistroName(name, out var nameError)) return Failure(nameError);
        if (!DistroManagePlanner.TryValidateMoveTarget(targetDirectory, out var targetError)) return Failure(targetError);

        return await RunManageAsync(name, n => DistroManagePlanner.BuildMoveArguments(n, targetDirectory), cancellationToken);
    }

    public async Task<DistroOperationResult> SetDefaultUserAsync(string name, string user, CancellationToken cancellationToken = default)
    {
        if (!DistroManagePlanner.TryValidateDistroName(name, out var nameError)) return Failure(nameError);

        if (string.IsNullOrWhiteSpace(user))
        {
            return Failure("用户名不能为空");
        }

        return await RunManageAsync(name, n => DistroManagePlanner.BuildSetDefaultUserArguments(n, user), cancellationToken);
    }

    public async Task<DistroOperationResult> ExportAsync(
        string name,
        string filePath,
        Action<DistroExportProgress>? onProgress = null,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        if (!DistroManagePlanner.TryValidateDistroName(name, out var nameError)) return Failure(nameError);
        if (!DistroManagePlanner.TryValidateExportPath(filePath, out var pathError)) return Failure(pathError);

        var arguments = DistroManagePlanner.BuildExportArguments(name, filePath);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var streaming = _runner.RunStreamingAsync(
                _wslExecutable,
                arguments,
                line => onOutput?.Invoke(line),
                line => onOutput?.Invoke(line),
                cancellationToken: cancellationToken);

            // 进度靠轮询目标文件：wsl 只偶尔打几行状态，字节数是这里唯一连续的量
            while (!streaming.IsCompleted)
            {
                try
                {
                    await Task.Delay(ExportPollInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (streaming.IsCompleted) break;
                onProgress?.Invoke(new DistroExportProgress { BytesWritten = TryGetFileLength(filePath) });
            }

            var result = await streaming.ConfigureAwait(false);

            stopwatch.Stop();
            onProgress?.Invoke(new DistroExportProgress { BytesWritten = TryGetFileLength(filePath) });

            return new DistroOperationResult
            {
                Success = result.IsSuccess,
                Message = result.IsSuccess
                    ? "导出完成"
                    : FirstReadableLine(result.StandardError, result.StandardOutput) ?? "导出失败（引擎未返回原因）",
                CommandLine = $"wsl {arguments}",
                Duration = stopwatch.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new DistroOperationResult
            {
                Success = false,
                Message = "已取消导出",
                CommandLine = $"wsl {arguments}",
                Duration = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new DistroOperationResult
            {
                Success = false,
                Message = ex.Message,
                CommandLine = $"wsl {arguments}",
                Duration = stopwatch.Elapsed
            };
        }
    }

    public async Task<WslConfSnapshot> ReadWslConfAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!DistroManagePlanner.TryValidateDistroName(name, out var nameError))
        {
            return new WslConfSnapshot { Success = false, Error = nameError };
        }

        // 脚本永远退出 0：文件不存在时返回空串，而不是让我们去猜 stderr 里那句本地化的"没有那个文件"
        var script = DistroManagePlanner.BuildReadWslConfScript();
        var arguments = $"-d {WslcCommandBuilder.Quote(name.Trim())} -e sh -c {WslcCommandBuilder.Quote(script)}";

        try
        {
            var result = await _runner.RunAsync(_wslExecutable, arguments, cancellationToken: cancellationToken);

            if (!result.IsSuccess)
            {
                return new WslConfSnapshot
                {
                    Success = false,
                    Error = FirstReadableLine(result.StandardError, result.StandardOutput) ?? $"读取 {DistroManagePlanner.WslConfPath} 失败"
                };
            }

            // 只剥可能的前导 BOM，**不要** trim_end：
            // 结尾换行被吃掉的话，"打开什么都没改、点一次保存"也会被当成有改动
            return new WslConfSnapshot { Success = true, Content = result.StandardOutput.TrimStart('\uFEFF') };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new WslConfSnapshot { Success = false, Error = ex.Message };
        }
    }

    public async Task<DistroOperationResult> WriteWslConfAsync(string name, string text, CancellationToken cancellationToken = default)
    {
        if (!DistroManagePlanner.TryValidateDistroName(name, out var nameError)) return Failure(nameError);
        if (!DistroManagePlanner.TryValidateWslConfText(text, out var textError)) return Failure(textError);

        var quoted = WslcCommandBuilder.Quote(name.Trim());
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // 1) 先备份：写坏了还能从 .bak 捞回来
            var backupArguments =
                $"-d {quoted} -u root -e sh -c {WslcCommandBuilder.Quote(DistroManagePlanner.BuildBackupWslConfScript())}";

            var backup = await _runner.RunAsync(_wslExecutable, backupArguments, cancellationToken: cancellationToken);
            if (!backup.IsSuccess)
            {
                stopwatch.Stop();
                return new DistroOperationResult
                {
                    Success = false,
                    Message = FirstReadableLine(backup.StandardError, backup.StandardOutput) ?? $"备份 {DistroManagePlanner.WslConfPath} 失败",
                    CommandLine = $"wsl {backupArguments}",
                    Duration = stopwatch.Elapsed
                };
            }

            // 2) 再写。带引号的 heredoc：$ / 反引号 / 反斜杠都不会被 shell 展开
            var writeArguments =
                $"-d {quoted} -u root -e sh -c {WslcCommandBuilder.Quote(DistroManagePlanner.BuildWriteWslConfScript(text))}";

            var result = await RunWslAsync(writeArguments, cancellationToken);

            stopwatch.Stop();
            return result with { Duration = stopwatch.Elapsed };
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new DistroOperationResult { Success = false, Message = "已取消", Duration = stopwatch.Elapsed };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new DistroOperationResult { Success = false, Message = ex.Message, Duration = stopwatch.Elapsed };
        }
    }

    /// <summary>跑一条 <c>wsl.exe</c> 参数串并包成统一结果（校验在前，命令在拼装函数里）。</summary>
    private async Task<DistroOperationResult> RunManageAsync(
        string name,
        Func<string, string> buildArguments,
        CancellationToken cancellationToken)
    {
        if (!DistroManagePlanner.TryValidateDistroName(name, out var nameError)) return Failure(nameError);

        return await RunWslAsync(buildArguments(name.Trim()), cancellationToken);
    }

    private async Task<DistroOperationResult> RunWslAsync(string arguments, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await _runner.RunAsync(_wslExecutable, arguments, cancellationToken: cancellationToken);
            stopwatch.Stop();

            return new DistroOperationResult
            {
                Success = result.IsSuccess,
                Message = result.IsSuccess
                    ? "操作完成"
                    : FirstReadableLine(result.StandardError, result.StandardOutput) ?? "操作失败（引擎未返回原因）",
                CommandLine = $"wsl {arguments}",
                Duration = stopwatch.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new DistroOperationResult
            {
                Success = false,
                Message = "已取消",
                CommandLine = $"wsl {arguments}",
                Duration = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new DistroOperationResult
            {
                Success = false,
                Message = ex.Message,
                CommandLine = $"wsl {arguments}",
                Duration = stopwatch.Elapsed
            };
        }
    }

    private static DistroOperationResult Failure(string message)
        => new() { Success = false, Message = message };

    private static long TryGetFileLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>取第一行有内容的输出；跳过引擎的版权尾巴与 <c>.wslconfig</c> 配置告警。</summary>
    private static string? FirstReadableLine(string? stderr, string? stdout)
    {
        foreach (var text in new[] { stderr, stdout })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;

            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed.StartsWith("版权所有", StringComparison.Ordinal)) continue;
                if (trimmed.StartsWith("有关此产品的隐私", StringComparison.Ordinal)) continue;
                if (trimmed.StartsWith("如果此错误是意外错误", StringComparison.Ordinal)) continue;
                if (IsConfigWarning(trimmed)) continue;
                return trimmed;
            }
        }

        return null;
    }

    /// <summary>
    /// 过滤 <c>wsl.exe</c> 打在 stderr 上、与本操作无关的 <c>.wslconfig</c> 配置告警。
    ///
    /// 实测形态（进发行版的命令与 <c>--manage</c> 会在 VM 启动时报一次）：
    /// <code>
    /// wsl: interop.appendWindowsPath:C:\Users\x\.wslconfig 中的键"12"未知
    /// </code>
    /// 它们不影响退出码，但会把错误消息污染得看不出真正原因——用户看到
    /// "操作失败：键12未知"只会更迷惑。
    /// </summary>
    private static bool IsConfigWarning(string trimmedLine)
        => trimmedLine.StartsWith("wsl: ", StringComparison.Ordinal);
}
