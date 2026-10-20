using System.Text;
using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Parsing;

/// <summary>
/// 把 <see cref="DiagnosticSnapshot"/> 排成一份能直接读（也能直接贴进 issue）的报告。
///
/// 设计原则：**只写结论与计数，不贴原始 JSON**。
/// 容器的 inspect / list 输出里可能带环境变量（口令、令牌），诊断包不该成为泄露渠道。
/// </summary>
public static class DiagnosticsReport
{
    public static string Build(DiagnosticSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var sb = new StringBuilder();

        sb.AppendLine("# wslc-desktop 诊断包");
        sb.AppendLine();
        sb.AppendLine($"> 生成时间：{snapshot.CapturedAt:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"> 说明：本文件只含版本、状态与计数；凭据与容器环境变量**不会**被导出。");
        sb.AppendLine();

        sb.AppendLine("## 环境");
        sb.AppendLine($"- 面板版本：{Fallback(snapshot.AppVersion)}");
        sb.AppendLine($"- 运行时：{Fallback(snapshot.Runtime)}");
        sb.AppendLine($"- 系统：{Fallback(snapshot.OsDescription)}");
        sb.AppendLine($"- WSL 引擎：{Fallback(snapshot.EngineVersion)}");
        sb.AppendLine($"- 会话管理器：{Fallback(snapshot.SessionManagerVersion)}");
        sb.AppendLine($"- 缺失组件：{(snapshot.MissingComponents.Count == 0 ? "无" : string.Join("、", snapshot.MissingComponents))}");
        sb.AppendLine();

        sb.AppendLine("## 官方 API 通道");
        sb.AppendLine($"- 状态：{Fallback(snapshot.ApiChannelState)}");
        if (!string.IsNullOrWhiteSpace(snapshot.ApiChannelReason))
        {
            sb.AppendLine($"- 说明：{snapshot.ApiChannelReason}");
        }

        sb.AppendLine();

        sb.AppendLine($"## 会话（{snapshot.Sessions.Count}）");
        if (snapshot.Sessions.Count == 0)
        {
            sb.AppendLine("- （未读到会话）");
        }
        else
        {
            sb.AppendLine("| 名称 | 状态 | 默认 | 存储 |");
            sb.AppendLine("| --- | --- | --- | --- |");
            foreach (var session in snapshot.Sessions)
            {
                sb.AppendLine(
                    $"| {session.Name} | {session.State} | {(session.IsDefault ? "是" : "")} | " +
                    $"{Fallback(session.StoragePath)}（{FormatBytes(session.SizeBytes)}） |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## 规模");
        sb.AppendLine($"- 容器：{snapshot.ContainerTotal}（运行中 {snapshot.ContainerRunning}）");
        sb.AppendLine($"- 镜像：{snapshot.ImageTotal}");
        sb.AppendLine($"- 数据卷：{snapshot.VolumeTotal}");
        sb.AppendLine($"- 网络：{snapshot.NetworkTotal}");

        return sb.ToString();
    }

    /// <summary>人类可读的字节数（诊断包里别出现 8 位裸数字）。</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "—";

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    private static string Fallback(string? value)
        => string.IsNullOrWhiteSpace(value) ? "—" : value;
}
