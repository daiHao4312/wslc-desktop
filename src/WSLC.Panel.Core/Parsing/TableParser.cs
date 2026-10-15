using System.Text.RegularExpressions;
using WSLC.Panel.Core.Models;

namespace WSLC.Panel.Core.Parsing;

public static class TableParser
{
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    public static List<SessionModel> ParseSessions(string? tableText)
    {
        var result = new List<SessionModel>();
        if (string.IsNullOrWhiteSpace(tableText)) return result;

        using var reader = new StringReader(tableText);
        string? headerLine = null;

        // Skip to header line containing "ID" and ("PID" or "创建者")
        while ((headerLine = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(headerLine)) continue;
            if (headerLine.Contains("ID", StringComparison.OrdinalIgnoreCase) &&
                (headerLine.Contains("PID", StringComparison.OrdinalIgnoreCase) || headerLine.Contains("创建者")))
            {
                break;
            }
        }

        if (headerLine == null) return result;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = WhitespaceRegex.Split(line.Trim());
            if (parts.Length >= 3 && int.TryParse(parts[0], out var id) && int.TryParse(parts[1], out var pid))
            {
                var name = string.Join(" ", parts.Skip(2));
                result.Add(new SessionModel
                {
                    Id = id,
                    CreatorPid = pid,
                    DisplayName = name
                });
            }
        }

        return result;
    }
}
