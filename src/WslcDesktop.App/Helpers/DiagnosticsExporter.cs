using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Helpers;

/// <summary>
/// 导出诊断包（M20 第一片）。
///
/// 目标很具体：**减少"再截一张图"的来回**。包里有版本、引擎/会话状态、规模计数、
/// API 通道探测结果，以及 <c>error.log</c> 的末尾 —— 这些恰好是这几轮排障每次都缺的东西。
///
/// 不含：容器环境变量、任何凭据（<c>prefs.json</c> 先脱敏再放进去，见 <see cref="DiagnosticsRedactor"/>）。
/// </summary>
public static class DiagnosticsExporter
{
    /// <summary>error.log 只取末尾若干行：诊断包要能随手发出去，不该是几十 MB。</summary>
    private const int ErrorLogTailLines = 300;

    /// <summary>面板数据目录（与 <c>PreferencesService</c> 一致）。</summary>
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WslcDesktop");

    public static async Task<string> ExportAsync(
        IWslcClient client,
        IWslcApiChannel? apiChannel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        // 采集：任何一项失败都不该让整个诊断包失败（缺哪项就在报告里写"未读到"）
        var system = await TryAsync(() => client.GetSystemInfoAsync(cancellationToken), cancellationToken)
            .ConfigureAwait(false) ?? new SystemInfoModel();

        var containers = await TryAsync(
                () => client.GetContainersAsync(all: true, cancellationToken), cancellationToken)
            .ConfigureAwait(false) ?? Array.Empty<ContainerModel>();

        var images = await TryAsync(() => client.GetImagesAsync(cancellationToken), cancellationToken)
            .ConfigureAwait(false) ?? Array.Empty<ImageModel>();

        var volumes = await TryAsync(() => client.GetVolumesAsync(cancellationToken), cancellationToken)
            .ConfigureAwait(false) ?? Array.Empty<VolumeModel>();

        var networks = await TryAsync(() => client.GetNetworksAsync(cancellationToken), cancellationToken)
            .ConfigureAwait(false) ?? Array.Empty<NetworkModel>();

        var (apiState, apiReason) = await DescribeApiChannelAsync(apiChannel, cancellationToken)
            .ConfigureAwait(false);

        var snapshot = new DiagnosticSnapshot
        {
            AppVersion = AppInfo.Version,
            Runtime = RuntimeInformation.FrameworkDescription,
            OsDescription = RuntimeInformation.OSDescription,
            EngineVersion = system.WslVersion,
            SessionManagerVersion = system.SessionManagerVersion,
            MissingComponents = system.MissingComponents,
            ApiChannelState = apiState,
            ApiChannelReason = apiReason,
            Sessions = system.ActiveSessions.Select(ToDiagnosticSession).ToList(),
            ContainerTotal = containers.Count,
            ContainerRunning = containers.Count(c => c.IsRunning),
            ImageTotal = images.Count,
            VolumeTotal = volumes.Count,
            NetworkTotal = networks.Count
        };

        var directory = Path.Combine(DataDirectory, "diagnostics");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"wslc-desktop-diag-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        AddText(archive, "diagnostics.md", DiagnosticsReport.Build(snapshot));

        AddText(
            archive,
            "prefs.redacted.json",
            DiagnosticsRedactor.RedactPreferences(ReadTextOrNull(Path.Combine(DataDirectory, "prefs.json"))));

        if (ReadErrorLogTail() is { } tail)
        {
            AddText(archive, "error.log", tail);
        }

        return path;
    }

    /// <summary>在资源管理器里选中刚生成的包（用户下一步通常是"发给我"）。</summary>
    public static void RevealInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // 打不开资源管理器不影响"包已经生成"这件事
        }
    }

    private static DiagnosticSession ToDiagnosticSession(SessionModel session) => new()
    {
        Name = session.DisplayName,
        State = session.IsCliDefault ? "CLI 默认" : "会话",
        StoragePath = session.StoragePathDisplay,
        SizeBytes = session.TotalSizeBytes,
        IsDefault = session.IsCliDefault
    };

    private static async Task<(string State, string? Reason)> DescribeApiChannelAsync(
        IWslcApiChannel? channel,
        CancellationToken cancellationToken)
    {
        if (channel is null) return ("未注册（始终走 CLI）", null);

        try
        {
            var capabilities = await channel.ProbeAsync(cancellationToken).ConfigureAwait(false);

            if (!capabilities.Enabled)
            {
                return ("已关闭（设置项）", capabilities.Reason);
            }

            if (capabilities.AttachedSessionName is null)
            {
                return ("不可用 → 走 CLI", capabilities.Reason);
            }

            return capabilities.AttachedReadOnly
                ? ($"可用 · 会话「{capabilities.AttachedSessionName}」（只读附着）", capabilities.Reason)
                : ($"可用 · 会话「{capabilities.AttachedSessionName}」", capabilities.Reason);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ("探测失败", ex.Message);
        }
    }

    private static void AddText(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    /// <summary>只取末尾若干行：日志可能被追加很久，整份塞进包里不合理。</summary>
    private static string? ReadErrorLogTail()
    {
        var path = Path.Combine(DataDirectory, "error.log");
        if (!File.Exists(path)) return null;

        try
        {
            var lines = File.ReadLines(path).TakeLast(ErrorLogTailLines).ToList();
            return lines.Count == 0
                ? null
                : $"（仅末尾 {lines.Count} 行）{Environment.NewLine}{string.Join(Environment.NewLine, lines)}";
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadTextOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<T?> TryAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null; // 单项失败：报告里显示"未读到"，不影响整体
        }
    }
}
