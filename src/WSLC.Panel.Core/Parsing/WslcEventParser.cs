using System.Globalization;
using System.Text.Json;
using WSLC.Panel.Core.Models;

namespace WSLC.Panel.Core.Parsing;

/// <summary>
/// 解析 <c>wslc events</c> 输出。
///
/// 主通道是 <c>--format json</c>（JSON Lines，逐行一个事件对象）；
/// 另提供 table 格式兜底解析——引擎若不认 <c>--format json</c>（老版本/参数变更），
/// 活动记录页仍然能用，不至于整块功能失效。
///
/// 纪律（与其它解析器一致）：纯函数、不抛异常、坏行跳过。
/// </summary>
public static class WslcEventParser
{
    /// <summary>解析 JSONL 事件流（多行）。</summary>
    public static List<WslcEvent> Parse(string? jsonLines)
    {
        var result = new List<WslcEvent>();
        if (string.IsNullOrWhiteSpace(jsonLines)) return result;

        using var reader = new StringReader(jsonLines);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (TryParseLine(line, out var evt)) result.Add(evt);
        }

        return result;
    }

    /// <summary>
    /// 解析单行事件（流式场景逐行调用）。自动识别 JSON 与 table 两种形态。
    /// </summary>
    public static bool TryParseLine(string? line, out WslcEvent evt)
    {
        evt = null!;
        if (string.IsNullOrWhiteSpace(line)) return false;

        var trimmed = line.Trim();

        // 引擎偶尔会混入版权/隐私模板行，直接跳过
        if (trimmed.StartsWith("版权所有", StringComparison.Ordinal) ||
            trimmed.StartsWith("有关此产品的隐私", StringComparison.Ordinal) ||
            trimmed.StartsWith("如果此错误是意外错误", StringComparison.Ordinal))
        {
            return false;
        }

        if (trimmed.StartsWith('{'))
        {
            return TryParseJsonLine(trimmed, out evt);
        }

        return TryParseTableLine(trimmed, out evt);
    }

    /// <summary>
    /// 断线续接用的 <c>--since</c> 实参。
    ///
    /// 实测引擎只认 <b>Unix 秒</b>、<b>相对时长</b>（<c>24h</c>）和 <b>RFC3339</b> 三种；
    /// 且 RFC3339 的时区偏移<b>必须带冒号</b>——<c>2026-10-14T17:58:18+0800</c> 会被拒绝并报
    /// 「预期为 Unix epoch 时间戳或 RFC3339 日期时间(例如 2024-01-15T10:30:00Z)」。
    /// 因此统一转 UTC 并用引擎自己示例里的 <c>Z</c> 形式，避开偏移写法的坑。
    /// 保留毫秒是为了重连后少补拉一批重复事件（重复仍由 <see cref="WslcEvent.DedupKey"/> 兜底）。
    /// </summary>
    public static string FormatSinceArgument(DateTimeOffset since)
        => since.ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    // ---------- JSON ----------

    private static bool TryParseJsonLine(string line, out WslcEvent evt)
    {
        evt = null!;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            var type = GetString(root, "Type");
            var action = GetString(root, "Action");

            // Action 缺失时退回 status（引擎里两者同值）
            if (string.IsNullOrEmpty(action)) action = GetString(root, "status");

            var actorId = string.Empty;
            var actorName = string.Empty;
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (root.TryGetProperty("Actor", out var actor) && actor.ValueKind == JsonValueKind.Object)
            {
                actorId = GetString(actor, "ID");
                if (actor.TryGetProperty("Attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in attrs.EnumerateObject())
                    {
                        attributes[p.Name] = ScalarToString(p.Value);
                    }
                    attributes.TryGetValue("name", out var n);
                    actorName = n ?? string.Empty;
                }
            }

            // 顶层 id 与 Actor.ID 同值，兜底用
            if (string.IsNullOrEmpty(actorId)) actorId = GetString(root, "id");

            var image = GetString(root, "from");
            if (string.IsNullOrEmpty(image))
            {
                attributes.TryGetValue("image", out var img);
                image = img ?? string.Empty;
            }

            var timeNano = GetInt64(root, "timeNano");
            var time = ResolveTime(timeNano, GetInt64(root, "time"));

            evt = new WslcEvent
            {
                Time = time,
                TimeNano = timeNano,
                Type = type,
                Action = action,
                ActorId = actorId,
                ActorName = actorName,
                Image = image,
                Scope = GetString(root, "scope"),
                Attributes = attributes
            };

            // 连 type/action 都没有的行没有展示价值
            return !string.IsNullOrEmpty(type) || !string.IsNullOrEmpty(action);
        }
        catch
        {
            return false;
        }
    }

    // ---------- table 兜底 ----------

    // 形如：
    // 2026-10-14T15:16:51.131552884+08:00 container create <64hex> (image=..., name=mysql)
    private static bool TryParseTableLine(string line, out WslcEvent evt)
    {
        evt = null!;

        var firstSpace = line.IndexOf(' ');
        if (firstSpace <= 0) return false;

        var timestampRaw = line[..firstSpace];
        var time = ParseTimestamp(timestampRaw);
        if (time is null) return false;

        var rest = line[(firstSpace + 1)..].Trim();
        if (rest.Length == 0) return false;

        // 属性括号先摘掉，避免里面含空格影响分词
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parenStart = rest.IndexOf('(');
        if (parenStart >= 0)
        {
            var parenEnd = rest.LastIndexOf(')');
            if (parenEnd > parenStart)
            {
                ParseAttributes(rest[(parenStart + 1)..parenEnd], attributes);
                rest = rest[..parenStart].Trim();
            }
        }

        var tokens = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return false;

        var type = tokens[0];
        var action = tokens[1];
        var actorId = tokens.Length > 2 ? tokens[2] : string.Empty;

        attributes.TryGetValue("name", out var actorName);
        attributes.TryGetValue("image", out var image);

        evt = new WslcEvent
        {
            Time = time.Value,
            TimeNano = 0,
            Type = type,
            Action = action,
            ActorId = actorId,
            ActorName = actorName ?? string.Empty,
            Image = image ?? string.Empty,
            Scope = string.Empty,
            Attributes = attributes
        };

        return true;
    }

    private static void ParseAttributes(string raw, IDictionary<string, string> target)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;

        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var key = pair[..eq].Trim();
            var value = pair[(eq + 1)..].Trim();
            if (key.Length > 0) target[key] = value;
        }
    }

    // ---------- 工具 ----------

    /// <summary>
    /// 时间解析：优先纳秒时间戳，退回整秒。
    /// 引擎给的是 Unix 纪元值（实测 1791962211 → 2026-10-14），统一转本地时区展示。
    /// </summary>
    private static DateTimeOffset ResolveTime(long timeNano, long timeSeconds)
    {
        if (timeNano > 0)
        {
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(timeNano / 1_000_000).ToLocalTime();
            }
            catch
            {
                // 越界值走下面的秒级兜底
            }
        }

        if (timeSeconds > 0)
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(timeSeconds).ToLocalTime();
            }
            catch
            {
                // 落到当前时间
            }
        }

        return DateTimeOffset.Now;
    }

    /// <summary>
    /// table 通道的时间戳带 9 位纳秒小数，而 .NET 只认 7 位（100ns 刻度），
    /// 必须先把小数截到 7 位再解析，否则整行作废。
    /// </summary>
    private static DateTimeOffset? ParseTimestamp(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var s = raw.Trim();
        var dot = s.IndexOf('.');
        if (dot >= 0)
        {
            var end = dot + 1;
            while (end < s.Length && char.IsDigit(s[end])) end++;
            var digits = end - (dot + 1);
            if (digits > 7)
            {
                s = string.Concat(s.AsSpan(0, dot + 1 + 7), s.AsSpan(end));
            }
        }

        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)
            ? dto
            : null;
    }

    private static string ScalarToString(JsonElement elem) => elem.ValueKind switch
    {
        JsonValueKind.String => elem.GetString() ?? string.Empty,
        JsonValueKind.Number => elem.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => string.Empty,
        _ => string.Empty
    };

    private static string GetString(JsonElement elem, string propName)
        => elem.TryGetProperty(propName, out var p) ? ScalarToString(p) : string.Empty;

    private static long GetInt64(JsonElement elem, string propName)
    {
        if (!elem.TryGetProperty(propName, out var p)) return 0;

        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v)) return v;
        if (p.ValueKind == JsonValueKind.String &&
            long.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sv))
        {
            return sv;
        }

        return 0;
    }
}
