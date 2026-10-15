using System.Globalization;
using System.Text.RegularExpressions;
using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Parsing;

public static class OutputSanitizer
{
    private static readonly Regex PortPattern = new(
        @"(?:(?<ip>[\d\.]+):)?(?<hostPort>\d+)->(?<containerPort>\d+)/(?<proto>\w+)",
        RegexOptions.Compiled);

    public static string NormalizeContainerName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;
        var trimmed = rawName.Trim();
        return trimmed.StartsWith('/') ? trimmed[1..] : trimmed;
    }

    public static bool IdMatches(string? idA, string? idB)
    {
        if (string.IsNullOrWhiteSpace(idA) || string.IsNullOrWhiteSpace(idB)) return false;
        var a = idA.Trim();
        var b = idB.Trim();
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        if (a.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) a = a[7..];
        if (b.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) b = b[7..];
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    public static List<PortMappingModel> ParsePorts(string? portsRaw)
    {
        var result = new List<PortMappingModel>();
        if (string.IsNullOrWhiteSpace(portsRaw)) return result;

        // 端口段形如 "0.0.0.0:80->80/tcp, 127.0.0.1:8080->80/tcp"
        var segments = portsRaw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
        {
            var match = PortPattern.Match(seg.Trim());
            if (match.Success)
            {
                // 绑定地址**原样保留**：以前把 0.0.0.0 改写成 127.0.0.1，
                // 详情页显示的 IP 与 `wslc ps` 不一致（用户实测报错）。
                // 只有引擎压根没写地址时才兜底成通配地址。
                var ip = match.Groups["ip"].Success
                    ? match.Groups["ip"].Value
                    : PortMappingModel.WildcardIp;

                // 必须 TryParse：正则 \d+ 不限位数，畸形端口（如 99999999999）会让
                // int.Parse 抛 OverflowException——调用方在 try/catch 里，整行容器会被静默丢弃
                if (!int.TryParse(match.Groups["hostPort"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hostPort) ||
                    !int.TryParse(match.Groups["containerPort"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var containerPort))
                {
                    continue;
                }

                var proto = match.Groups["proto"].Value;

                result.Add(new PortMappingModel
                {
                    HostIp = ip,
                    HostPort = hostPort,
                    ContainerPort = containerPort,
                    Protocol = proto
                });
            }
        }
        return result;
    }

    public static double ParsePercentage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0.0;
        var clean = raw.Replace("%", "").Trim();
        return double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out var val) ? val : 0.0;
    }

    public static DateTimeOffset? ParseDateTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("0001-01-01") || trimmed == "0") return null;

        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
        {
            return dto;
        }

        // Try format "2026-10-08 16:34:46 +0800 GMT+8"
        var idx = trimmed.IndexOf("GMT", StringComparison.OrdinalIgnoreCase);
        if (idx > 0)
        {
            var substring = trimmed[..idx].Trim();
            if (DateTimeOffset.TryParse(substring, CultureInfo.InvariantCulture, DateTimeStyles.None, out dto))
            {
                return dto;
            }
        }

        return null;
    }
}
