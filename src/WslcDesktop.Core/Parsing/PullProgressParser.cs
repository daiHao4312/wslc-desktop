using System.Globalization;
using System.Text.RegularExpressions;
using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Parsing;

/// <summary>
/// 解析 <c>wslc pull</c> 的逐行输出。
///
/// 实测（WSL 3.0.3.0，夹具 fixtures/pull_progress_alpine.txt）本机 wslc **只输出状态关键字、不带字节数**：
/// <code>
/// latest: Pulling from library/alpine
/// e2de96513ba9: Pulling fs layer
/// e2de96513ba9: Downloading
/// e2de96513ba9: Pull complete
/// Digest: sha256:...
/// Status: Downloaded newer image for docker.1panel.live/library/alpine:latest
/// </code>
/// 因此字节/百分比是可选项：拿得到就用确定进度，拿不到就让界面用"不确定进度条 + 状态文本"。
/// </summary>
public static class PullProgressParser
{
    private static readonly Regex LayerLineRegex = new(
        @"^(?<layer>[0-9a-fA-F]{6,})(?::|\s)\s*(?<rest>.+)$", RegexOptions.Compiled);

    private static readonly Regex BytePairRegex = new(
        @"(?<dl>[\d.]+)\s*(?<du>[kKmMgGtT]?i?B?)\s*/\s*(?<tl>[\d.]+)\s*(?<tu>[kKmMgGtT]?i?B?)",
        RegexOptions.Compiled);

    private static readonly string[] TerminalKeywords =
    {
        "pull complete", "download complete", "already exists", "image is up to date", "downloaded newer image"
    };

    /// <summary>尝试把一行输出解析为进度快照；无法识别为进度行时返回 false。</summary>
    public static bool TryParse(string? line, string imageReference, out PullProgress progress)
    {
        progress = new PullProgress { ImageReference = imageReference };

        if (string.IsNullOrWhiteSpace(line)) return false;

        var raw = line.Trim();
        var layerId = string.Empty;
        var rest = raw;

        var layerMatch = LayerLineRegex.Match(raw);
        // 只把"十六进制层 ID"当层，避免把 sha256:xxx 或普通单词误判
        if (layerMatch.Success && !raw.StartsWith("sha256", StringComparison.OrdinalIgnoreCase))
        {
            layerId = layerMatch.Groups["layer"].Value;
            rest = layerMatch.Groups["rest"].Value.Trim();
        }

        ulong? downloaded = null;
        ulong? total = null;
        double? percent = null;

        var byteMatch = BytePairRegex.Match(rest);
        if (byteMatch.Success)
        {
            downloaded = ToBytes(byteMatch.Groups["dl"].Value, byteMatch.Groups["du"].Value);
            total = ToBytes(byteMatch.Groups["tl"].Value, byteMatch.Groups["tu"].Value);
            if (downloaded.HasValue && total.HasValue && total.Value > 0)
            {
                percent = Math.Clamp(downloaded.Value * 100.0 / total.Value, 0, 100);
            }
            rest = rest[..byteMatch.Index].Trim();
        }

        // 去掉进度条图形残留（====> / [ ]）
        rest = rest.Trim('[', ']', ' ', '=', '>', '-');

        var status = ExtractStatusKeyword(rest);
        if (string.IsNullOrEmpty(status) && string.IsNullOrEmpty(layerId))
        {
            return false;
        }

        var isTerminal = TerminalKeywords.Any(k => raw.Contains(k, StringComparison.OrdinalIgnoreCase));

        progress = new PullProgress
        {
            ImageReference = imageReference,
            LayerId = layerId,
            Status = string.IsNullOrEmpty(status) ? rest : status,
            DownloadedBytes = downloaded,
            TotalBytes = total,
            Percent = percent,
            IsTerminal = isTerminal,
            RawLine = raw
        };

        return true;
    }

    /// <summary>从整段输出解析出最终结果（成功/失败 + 可读消息）。</summary>
    public static PullResult BuildResult(string imageReference, ProcessExecutionResult raw, TimeSpan duration)
    {
        var stdout = raw.StandardOutput ?? string.Empty;
        var stderr = raw.StandardError ?? string.Empty;

        if (raw.IsSuccess)
        {
            var statusLine = stdout
                .Split('\n')
                .Select(l => l.Trim())
                .LastOrDefault(l => l.StartsWith("Status:", StringComparison.OrdinalIgnoreCase));

            return new PullResult
            {
                Success = true,
                ImageReference = imageReference,
                Message = statusLine is null
                    ? $"镜像 {imageReference} 已就绪"
                    : statusLine["Status:".Length..].Trim(),
                Duration = duration
            };
        }

        var errorLine = FirstMeaningfulLine(stderr);
        if (string.IsNullOrEmpty(errorLine)) errorLine = FirstMeaningfulLine(stdout);

        return new PullResult
        {
            Success = false,
            ImageReference = imageReference,
            Message = string.IsNullOrEmpty(errorLine) ? "拉取失败（引擎未返回原因）" : errorLine,
            Duration = duration
        };
    }

    private static string FirstMeaningfulLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith("版权所有", StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith("有关此产品的隐私", StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith("如果此错误是意外错误", StringComparison.Ordinal)) continue;
            return trimmed;
        }

        return string.Empty;
    }

    private static string ExtractStatusKeyword(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var lower = text.ToLowerInvariant();
        if (lower.StartsWith("pulling fs layer")) return "Pulling fs layer";
        if (lower.StartsWith("pulling from")) return "Pulling from";
        if (lower.StartsWith("downloading")) return "Downloading";
        if (lower.StartsWith("verifying checksum")) return "Verifying Checksum";
        if (lower.StartsWith("download complete")) return "Download complete";
        if (lower.StartsWith("extracting")) return "Extracting";
        if (lower.StartsWith("pull complete")) return "Pull complete";
        if (lower.StartsWith("waiting")) return "Waiting";
        if (lower.StartsWith("already exists")) return "Already exists";
        if (lower.StartsWith("digest")) return "Digest";
        if (lower.StartsWith("status")) return "Status";

        return text;
    }

    private static ulong? ToBytes(string number, string unit)
    {
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        // 负值在 unchecked 下转 ulong 会得到巨大无符号数（percent 趋 0、数据错乱）
        if (value < 0) return null;

        var multiplier = 1UL;
        var u = (unit ?? string.Empty).Trim().ToUpperInvariant();
        multiplier = u switch
        {
            "KB" => 1000UL,
            "KIB" or "K" => 1024UL,
            "MB" => 1000UL * 1000UL,
            "MIB" or "M" => 1024UL * 1024UL,
            "GB" => 1000UL * 1000UL * 1000UL,
            "GIB" or "G" => 1024UL * 1024UL * 1024UL,
            "TB" => 1000UL * 1000UL * 1000UL * 1000UL,
            "TIB" or "T" => 1024UL * 1024UL * 1024UL * 1024UL,
            _ => 1UL
        };

        return (ulong)Math.Round(value * multiplier);
    }
}
