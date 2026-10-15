using System.Diagnostics;
using System.Text.RegularExpressions;
using WSLC.Panel.Core.Abstractions;
using WSLC.Panel.Core.Models;
using WSLC.Panel.Core.Parsing;

namespace WSLC.Panel.Core.Services;

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
        var result = await _runner.RunAsync(_wslcExecutable, "stats --no-stream --format json", cancellationToken: cancellationToken);
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
        var result = await _runner.RunAsync(_wslcExecutable, $"logs -n {tail} {WslcCommandBuilder.Quote(idOrName)}", cancellationToken: cancellationToken);
        return string.IsNullOrEmpty(result.StandardOutput) ? result.StandardError : result.StandardOutput;
    }

    public async Task<ExecResult> ExecAsync(string idOrName, string command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return new ExecResult { Success = false, Error = "命令不能为空" };
        }

        var result = await _runner.RunAsync(
            _wslcExecutable,
            $"exec {WslcCommandBuilder.Quote(idOrName)} {command}",
            cancellationToken: cancellationToken);

        return new ExecResult
        {
            Success = result.IsSuccess,
            Output = result.StandardOutput,
            Error = result.StandardError
        };
    }

    // ---------- 镜像 ----------

    public async Task<PullResult> PullImageAsync(
        string imageUri,
        Action<PullProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
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

    public async Task<ImageInspectInfo> InspectImageAsync(string reference, CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(
            _wslcExecutable,
            $"image inspect {WslcCommandBuilder.Quote(reference)} --format json",
            cancellationToken: cancellationToken);

        var payload = string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput;
        return ImageInspectParser.Parse(payload);
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
