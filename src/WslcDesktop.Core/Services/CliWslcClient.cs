using System.Diagnostics;
using System.Text.RegularExpressions;
using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Core.Services;

/// <summary>
/// wslc.exe 命令行通道。
/// 纪律：注入 WSL_UTF8=1；--format json 按 JSONL 逐行解析；空输出=空列表；解析失败不抛异常。
/// </summary>
public class CliWslcClient : IWslcClient
{
    private static readonly Regex FullIdRegex = new(@"\b[0-9a-fA-F]{64}\b", RegexOptions.Compiled);

    private readonly IProcessRunner _runner;
    private readonly string _wslcExecutable;

    public CliWslcClient(IProcessRunner? runner = null, string wslcExecutable = "wslc.exe")
    {
        _runner = runner ?? new CliProcessRunner();
        _wslcExecutable = wslcExecutable;
    }

    // ---------- 查询 ----------

    public async Task<SystemInfoModel> GetSystemInfoAsync(CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(_wslcExecutable, "info --format json", cancellationToken: cancellationToken);
        var model = result.IsSuccess ? JsonLinesParser.ParseSystemInfo(result.StandardOutput) : new SystemInfoModel();

        if (model.ActiveSessions.Count == 0)
        {
            var sessions = await GetSessionsAsync(cancellationToken);
            model = model with { ActiveSessions = sessions };
        }

        return model;
    }

    public async Task<IReadOnlyList<ContainerModel>> GetContainersAsync(bool all = true, CancellationToken cancellationToken = default)
    {
        var args = all ? "list -a --format json" : "list --format json";
        var result = await _runner.RunAsync(_wslcExecutable, args, cancellationToken: cancellationToken);
        if (!result.IsSuccess) return Array.Empty<ContainerModel>();

        return JsonLinesParser.ParseContainers(result.StandardOutput);
    }

    public async Task<IReadOnlyList<ContainerStatsModel>> GetContainerStatsAsync(CancellationToken cancellationToken = default)
    {
        // 不要写 --no-stream：那是 docker stats 的选项，wslc 的 stats 本身就是单次快照语义，
        // 多传这个选项引擎直接报「选项名称未被识别」→ IsSuccess=false → 整列 CPU/内存永远显示 —。
        var result = await _runner.RunAsync(_wslcExecutable, "stats --format json", cancellationToken: cancellationToken);
        if (!result.IsSuccess) return Array.Empty<ContainerStatsModel>();

        return JsonLinesParser.ParseStats(result.StandardOutput);
    }

    public async Task<IReadOnlyList<ImageModel>> GetImagesAsync(CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(_wslcExecutable, "images --format json", cancellationToken: cancellationToken);
        if (!result.IsSuccess) return Array.Empty<ImageModel>();

        return JsonLinesParser.ParseImages(result.StandardOutput);
    }

    public async Task<IReadOnlyList<NetworkModel>> GetNetworksAsync(CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(_wslcExecutable, "network list --format json", cancellationToken: cancellationToken);
        if (!result.IsSuccess) return Array.Empty<NetworkModel>();

        return JsonLinesParser.ParseNetworks(result.StandardOutput);
    }

    public async Task<IReadOnlyList<VolumeModel>> GetVolumesAsync(CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(_wslcExecutable, "volume list --format json", cancellationToken: cancellationToken);
        if (!result.IsSuccess) return Array.Empty<VolumeModel>();

        return JsonLinesParser.ParseVolumes(result.StandardOutput);
    }

    public async Task<IReadOnlyList<SessionModel>> GetSessionsAsync(CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(_wslcExecutable, "system session list", cancellationToken: cancellationToken);
        if (!result.IsSuccess) return Array.Empty<SessionModel>();

        return TableParser.ParseSessions(result.StandardOutput);
    }

    // ---------- 容器生命周期 ----------

    public async Task<bool> StartContainerAsync(string idOrName, CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(_wslcExecutable, $"start {WslcCommandBuilder.Quote(idOrName)}", cancellationToken: cancellationToken);
        return result.IsSuccess;
    }

    public async Task<bool> StopContainerAsync(string idOrName, int timeoutSeconds = 10, CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(_wslcExecutable, $"stop -t {timeoutSeconds} {WslcCommandBuilder.Quote(idOrName)}", cancellationToken: cancellationToken);
        return result.IsSuccess;
    }

    public async Task<bool> RestartContainerAsync(string idOrName, CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(_wslcExecutable, $"restart {WslcCommandBuilder.Quote(idOrName)}", cancellationToken: cancellationToken);
        return result.IsSuccess;
    }

    public async Task<bool> KillContainerAsync(string idOrName, CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(_wslcExecutable, $"kill {WslcCommandBuilder.Quote(idOrName)}", cancellationToken: cancellationToken);
        return result.IsSuccess;
    }

    public async Task<bool> DeleteContainerAsync(string idOrName, bool force = false, CancellationToken cancellationToken = default)
    {
        var flag = force ? "-f " : "";
        var result = await _runner.RunAsync(_wslcExecutable, $"rm {flag}{WslcCommandBuilder.Quote(idOrName)}", cancellationToken: cancellationToken);
        return result.IsSuccess;
    }

    public async Task<CreateContainerResult> CreateContainerAsync(
        ContainerCreateOptions options,
        CancellationToken cancellationToken = default)
    {
        if (options is null || string.IsNullOrWhiteSpace(options.Image))
        {
            return new CreateContainerResult { Success = false, Message = "镜像名称不能为空" };
        }

        var arguments = WslcCommandBuilder.BuildRunArguments(options);
        var commandLine = "wslc " + arguments;

        var result = await _runner.RunAsync(_wslcExecutable, arguments, cancellationToken: cancellationToken);
        if (!result.IsSuccess)
        {
            var reason = FirstErrorLine(result.StandardError, result.StandardOutput);
            return new CreateContainerResult
            {
                Success = false,
                CommandLine = commandLine,
                Message = reason
            };
        }

        return new CreateContainerResult
        {
            Success = true,
            ContainerId = ExtractContainerId(result.StandardOutput),
            CommandLine = commandLine,
            Message = "容器已创建"
        };
    }

    // ---------- 详情 ----------

    public async Task<string> InspectContainerAsync(string idOrName, CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(_wslcExecutable, $"inspect {WslcCommandBuilder.Quote(idOrName)}", cancellationToken: cancellationToken);
        return string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput;
    }

    public async Task<string> GetContainerLogsAsync(string idOrName, int tail = 100, CancellationToken cancellationToken = default)
    {
        // 参数拼装与"跟随"共用 BuildLogsArguments：两处各拼一份，迟早出现行数不一致的怪现象
        var result = await _runner.RunAsync(
            _wslcExecutable,
            WslcCommandBuilder.BuildLogsArguments(idOrName, tail, follow: false),
            cancellationToken: cancellationToken);

        return MergeStreams(result.StandardOutput, result.StandardError);
    }

    /// <summary>
    /// 把容器 stdout 与 stderr 合成一份。
    ///
    /// ⚠️ **两路都要**：<c>wslc logs</c> 用**自己的 stdout/stderr 分别承载容器的 stdout/stderr**。
    /// 这里曾经写成"stdout 非空就返回 stdout"，后果是 MySQL 这类镜像
    /// 只显示 entrypoint 脚本那十几行（写 stdout），而 mysqld 的运行日志（写 stderr）全被丢掉 ——
    /// 界面上看起来"日志只有创建时的那点"，跟着随走一遍才发现两路都得接。
    ///
    /// 一次性的快照拿不到两路的真实交错顺序（各自一个管道，读完才知道），
    /// 所以按"先 stdout 后 stderr"拼；要看真实时序请用跟随。
    /// </summary>
    private static string MergeStreams(string? standardOutput, string? standardError)
    {
        var output = standardOutput ?? string.Empty;
        var error = standardError ?? string.Empty;

        if (error.Trim().Length == 0) return output;
        if (output.Length == 0) return error;

        return output.EndsWith('\n') ? output + error : output + Environment.NewLine + error;
    }

    public Task<ProcessExecutionResult> StreamContainerLogsAsync(
        string idOrName,
        Action<string> onOutputLine,
        Action<string>? onErrorLine = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onOutputLine);

        // ⚠️ stderr 也要接：wslc 用它承载**容器的 stderr**。
        // 不接的话，把运行日志写到 stderr 的镜像（MySQL / nginx）在面板里只剩 entrypoint 那几行。
        return _runner.RunStreamingAsync(
            _wslcExecutable,
            WslcCommandBuilder.BuildLogsArguments(idOrName, WslcCommandBuilder.DefaultLogTail, follow: true),
            onOutputLine,
            onErrorLine,
            customEnvironment: null,
            cancellationToken: cancellationToken);
    }

    public async Task<ExecResult> ExecAsync(string idOrName, string command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return new ExecResult { Success = false, Error = "命令不能为空" };
        }

        var result = await _runner.RunAsync(
            _wslcExecutable,
            WslcCommandBuilder.BuildExecArguments(idOrName, command),
            cancellationToken: cancellationToken);

        return new ExecResult
        {
            Success = result.IsSuccess,
            Output = result.StandardOutput,
            Error = result.StandardError
        };
    }

    public Task<ProcessExecutionResult> StreamTrackedCommandAsync(
        string idOrName,
        string command,
        Action<string> onOutputLine,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onOutputLine);

        // stderr 也是这条命令的输出（脚本里的报错同样要给用户看）
        return _runner.RunStreamingAsync(
            _wslcExecutable,
            ContainerExecWrapper.BuildTrackedExecArguments(idOrName, command),
            onOutputLine,
            onOutputLine,
            customEnvironment: null,
            cancellationToken: cancellationToken);
    }

    public async Task<ExecResult> StopTrackedCommandAsync(string idOrName, CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(
            _wslcExecutable,
            ContainerExecWrapper.BuildStopArguments(idOrName),
            cancellationToken: cancellationToken);

        return new ExecResult
        {
            Success = result.IsSuccess,
            Output = result.StandardOutput,
            Error = result.StandardError
        };
    }

    /// <summary>
    /// CLI 通道没有"可管理进程"这个能力：<c>wslc exec</c> 是一次性执行，
    /// 杀掉客户端进程也不保证容器里的进程跟着结束（只有 API 的 Signal 能保证）。
    /// 所以这里如实报不可用，让界面回落并说明差别 —— 不假装两者一样。
    /// </summary>
    public Task<ApiProcessStartResult> TryStartContainerProcessAsync(
        string idOrName,
        string command,
        Action<string> onOutputLine,
        CancellationToken cancellationToken = default)
        => Task.FromResult(ApiProcessStartResult.Unavailable("CLI 通道只能一次性执行，不能发信号"));

    // ---------- 镜像 ----------

    public async Task<PullResult> PullImageAsync(
        string imageUri,
        string? registryAuth = null,
        Action<PullProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        // ⚠️ registryAuth 在 CLI 通道上**用不了**：`wslc pull` 没有凭据参数。
        // 这里不报错也不静默改写命令，只是忽略它 —— 引擎自己的凭据缓存仍可能让这条命令成功，
        // 提前拦下反而会挡掉本来能用的路径。私有仓库真正需要令牌时要靠官方 API 通道。
        _ = registryAuth;
        if (string.IsNullOrWhiteSpace(imageUri))
        {
            return new PullResult { Success = false, ImageReference = imageUri ?? string.Empty, Message = "镜像引用不能为空" };
        }

        var reference = imageUri.Trim();

        void HandleLine(string line)
        {
            if (onProgress == null) return;
            if (PullProgressParser.TryParse(line, reference, out var progress))
            {
                onProgress(progress);
            }
        }

        // 进度可能走 stdout，错误信息实测走 stderr —— 两边都送进解析器
        var stopwatch = Stopwatch.StartNew();
        var raw = await _runner.RunStreamingAsync(
            _wslcExecutable,
            $"pull {WslcCommandBuilder.Quote(reference)}",
            HandleLine,
            HandleLine,
            cancellationToken: cancellationToken);
        stopwatch.Stop();

        return PullProgressParser.BuildResult(reference, raw, stopwatch.Elapsed);
    }

    public async Task<bool> DeleteImageAsync(string idOrName, bool force = false, CancellationToken cancellationToken = default)
    {
        var flag = force ? "-f " : "";
        var result = await _runner.RunAsync(_wslcExecutable, $"rmi {flag}{WslcCommandBuilder.Quote(idOrName)}", cancellationToken: cancellationToken);
        return result.IsSuccess;
    }

    public async Task<bool> TagImageAsync(string source, string target, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)) return false;

        var result = await _runner.RunAsync(
            _wslcExecutable,
            $"tag {WslcCommandBuilder.Quote(source.Trim())} {WslcCommandBuilder.Quote(target.Trim())}",
            cancellationToken: cancellationToken);

        return result.IsSuccess;
    }

    public async Task<ImageInspectInfo> InspectImageAsync(string reference, CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(
            _wslcExecutable,
            $"image inspect {WslcCommandBuilder.Quote(reference)} --format json",
            cancellationToken: cancellationToken);

        var payload = string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput;
        return ImageInspectParser.Parse(payload);
    }

    /// <summary>导出进度的轮询间隔（<c>wslc save</c> 自己一行都不打，文件大小是唯一连续量）。</summary>
    private static readonly TimeSpan ArchivePollInterval = TimeSpan.FromMilliseconds(400);

    public async Task<ImageArchiveResult> ExportImagesAsync(
        IReadOnlyList<string> references,
        string outputPath,
        Action<ImageArchiveProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var refs = (references ?? Array.Empty<string>())
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .ToList();

        if (refs.Count == 0)
        {
            return ArchiveFailure("请至少选择一个镜像");
        }

        if (!ImageArchiveParser.TryValidateExportPath(outputPath, out var pathError))
        {
            return ArchiveFailure(pathError);
        }

        var path = outputPath.Trim();
        var arguments = WslcCommandBuilder.BuildSaveArguments(refs, path);
        var commandLine = $"wslc {arguments}";
        var stopwatch = Stopwatch.StartNew();
        var status = refs.Count == 1 ? $"正在导出 {refs[0]}…" : $"正在导出 {refs.Count} 个镜像…";

        try
        {
            // save 正常时一句话都不输出；stderr 仍然要接（错误全在那边）
            var streaming = _runner.RunStreamingAsync(
                _wslcExecutable,
                arguments,
                _ => { },
                _ => { },
                cancellationToken: cancellationToken);

            // 进度只能靠轮询目标文件：引擎不打百分比，文件大小是这里唯一连续的量
            while (!streaming.IsCompleted)
            {
                try
                {
                    await Task.Delay(ArchivePollInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (streaming.IsCompleted) break;
                onProgress?.Invoke(new ImageArchiveProgress { BytesWritten = TryGetFileLength(path), Status = status });
            }

            var result = await streaming.ConfigureAwait(false);
            stopwatch.Stop();

            if (!result.IsSuccess)
            {
                // 半截 tar 拿去 load 只会得到一句含糊的错 —— 失败就删掉，不留误导人的产物
                TryDeleteFile(path);
                return new ImageArchiveResult
                {
                    Success = false,
                    Message = ImageArchiveParser.ExtractError(
                        result.StandardError,
                        result.StandardOutput,
                        ImageArchiveParser.DescribeFailure(isExport: true)),
                    Path = path,
                    CommandLine = commandLine,
                    Duration = stopwatch.Elapsed,
                    References = refs
                };
            }

            var bytes = TryGetFileLength(path);
            if (bytes <= 0)
            {
                TryDeleteFile(path);
                return new ImageArchiveResult
                {
                    Success = false,
                    Message = "引擎报告成功，但目标文件不存在或为空（磁盘满 / 路径被占用？）",
                    Path = path,
                    CommandLine = commandLine,
                    Duration = stopwatch.Elapsed,
                    References = refs
                };
            }

            onProgress?.Invoke(new ImageArchiveProgress { BytesWritten = bytes, Status = "导出完成" });

            return new ImageArchiveResult
            {
                Success = true,
                Message = $"已导出 {refs.Count} 个镜像（{ImageArchiveProgress.FormatSize(bytes)}）",
                Path = path,
                CommandLine = commandLine,
                Duration = stopwatch.Elapsed,
                References = refs
            };
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            TryDeleteFile(path);
            return new ImageArchiveResult
            {
                Success = false,
                Cancelled = true,
                Message = "已取消导出，未完成的文件已删除",
                Path = path,
                CommandLine = commandLine,
                Duration = stopwatch.Elapsed,
                References = refs
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            TryDeleteFile(path);
            return new ImageArchiveResult
            {
                Success = false,
                Message = ex.Message,
                Path = path,
                CommandLine = commandLine,
                Duration = stopwatch.Elapsed,
                References = refs
            };
        }
    }

    public async Task<ImageArchiveResult> ImportImageArchiveAsync(
        string archivePath,
        Action<ImageArchiveProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (!ImageArchiveParser.TryValidateImportPath(archivePath, out var pathError))
        {
            return ArchiveFailure(pathError);
        }

        var path = archivePath.Trim();
        var arguments = WslcCommandBuilder.BuildLoadArguments(path);
        var commandLine = $"wslc {arguments}";
        var stopwatch = Stopwatch.StartNew();

        // 回调来自读取线程（stdout / stderr 各一个），累计结果必须加锁
        var loaded = new List<string>();
        var gate = new object();

        void HandleLine(string line)
        {
            if (!ImageArchiveParser.TryParseLoadedReference(line, out var reference)) return;

            lock (gate)
            {
                if (loaded.Any(x => string.Equals(x, reference, StringComparison.OrdinalIgnoreCase))) return;
                loaded.Add(reference);
            }

            // 导入没有字节数可报，能报的就是"已经加载了哪些"
            onProgress?.Invoke(new ImageArchiveProgress { Status = $"已加载 {reference}" });
        }

        onProgress?.Invoke(new ImageArchiveProgress
        {
            Status = $"正在读取 {Path.GetFileName(path)}（{ImageArchiveProgress.FormatSize(TryGetFileLength(path))}）…"
        });

        try
        {
            var result = await _runner.RunStreamingAsync(
                _wslcExecutable,
                arguments,
                HandleLine,
                HandleLine,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();

            IReadOnlyList<string> references;
            lock (gate)
            {
                references = loaded.ToList();
            }

            if (!result.IsSuccess)
            {
                return new ImageArchiveResult
                {
                    Success = false,
                    Message = ImageArchiveParser.ExtractError(
                        result.StandardError,
                        result.StandardOutput,
                        ImageArchiveParser.DescribeFailure(isExport: false)),
                    Path = path,
                    CommandLine = commandLine,
                    Duration = stopwatch.Elapsed,
                    References = references
                };
            }

            return new ImageArchiveResult
            {
                Success = true,
                Message = references.Count == 0
                    // 退出码 0 但一行都没报：可能是归档里没有镜像，也可能引擎换了措辞。
                    // 不当失败（镜像确实可能已经进来了），但也不假装"导入了 N 个"。
                    ? "引擎报告成功，但没有报告任何镜像（归档可能是空的，或引擎改了口径）"
                    : $"已导入 {references.Count} 个镜像",
                Path = path,
                CommandLine = commandLine,
                Duration = stopwatch.Elapsed,
                References = references
            };
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            IReadOnlyList<string> references;
            lock (gate)
            {
                references = loaded.ToList();
            }

            return new ImageArchiveResult
            {
                Success = false,
                Cancelled = true,
                // 已加载的部分引擎已经落盘了，如实说清楚，别让用户以为"取消了就等于什么都没发生"
                Message = references.Count == 0
                    ? "已取消导入"
                    : $"已取消导入；取消前已加载 {references.Count} 个镜像（它们已在本地列表中）",
                Path = path,
                CommandLine = commandLine,
                Duration = stopwatch.Elapsed,
                References = references
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new ImageArchiveResult
            {
                Success = false,
                Message = ex.Message,
                Path = path,
                CommandLine = commandLine,
                Duration = stopwatch.Elapsed
            };
        }
    }

    private static ImageArchiveResult ArchiveFailure(string message)
        => new() { Success = false, Message = message };

    private static long TryGetFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            // 文件正被引擎写入时长度读取可能失败，当作"还没写出来"，不打断轮询
            return 0;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 删不掉（仍被占用）不该盖掉真正的错误原因
        }
    }

    // ---------- 网络 ----------

    public async Task<bool> CreateNetworkAsync(string name, string driver = "bridge", CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(
            _wslcExecutable,
            $"network create -d {WslcCommandBuilder.Quote(driver)} {WslcCommandBuilder.Quote(name)}",
            cancellationToken: cancellationToken);
        return result.IsSuccess;
    }

    public async Task<bool> RemoveNetworkAsync(string idOrName, CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(
            _wslcExecutable,
            $"network remove {WslcCommandBuilder.Quote(idOrName)}",
            cancellationToken: cancellationToken);
        return result.IsSuccess;
    }

    // ---------- 卷 ----------

    public async Task<bool> CreateVolumeAsync(string name, CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(
            _wslcExecutable,
            $"volume create {WslcCommandBuilder.Quote(name)}",
            cancellationToken: cancellationToken);
        return result.IsSuccess;
    }

    public async Task<bool> RemoveVolumeAsync(string name, bool force = false, CancellationToken cancellationToken = default)
    {
        var flag = force ? "-f " : "";
        var result = await _runner.RunAsync(
            _wslcExecutable,
            $"volume remove {flag}{WslcCommandBuilder.Quote(name)}",
            cancellationToken: cancellationToken);
        return result.IsSuccess;
    }

    public async Task<bool> PruneVolumesAsync(CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(_wslcExecutable, "volume prune -f", cancellationToken: cancellationToken);
        return result.IsSuccess;
    }

    // ---------- 会话 ----------

    public async Task<bool> TerminateSessionAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        // terminate 不接受会话 ID 参数，靠全局 --session 指定目标会话
        var result = await _runner.RunAsync(
            _wslcExecutable,
            $"--session {sessionId} system session terminate",
            cancellationToken: cancellationToken);
        return result.IsSuccess;
    }

    // ---------- 事件流 ----------

    public Task StreamEventsAsync(
        Action<WslcEvent> onEvent,
        DateTimeOffset? since = null,
        CancellationToken cancellationToken = default,
        Action? onSubscribed = null)
    {
        ArgumentNullException.ThrowIfNull(onEvent);

        // 不带 --since：只推新事件（不回放历史），正是"实时订阅"想要的语义
        var arguments = since is DateTimeOffset from
            ? $"events --format json --since {WslcEventParser.FormatSinceArgument(from)}"
            : "events --format json";

        void HandleLine(string line)
        {
            if (WslcEventParser.TryParseLine(line, out var evt))
            {
                onEvent(evt);
            }
        }

        // stdout 是事件主通道；stderr 也过一遍解析器（非事件文本会被 TryParseLine 自然拒掉）
        return _runner.RunStreamingAsync(
            _wslcExecutable,
            arguments,
            HandleLine,
            HandleLine,
            cancellationToken: cancellationToken,
            onStarted: onSubscribed);
    }

    private static string ExtractContainerId(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return string.Empty;

        var full = FullIdRegex.Match(stdout);
        if (full.Success) return full.Value;

        return stdout
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0) ?? string.Empty;
    }

    private static string FirstErrorLine(string? stderr, string? stdout)
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
                return trimmed;
            }
        }

        return "创建容器失败（引擎未返回原因）";
    }
}
