using System.Globalization;

namespace WSLC.Panel.Core.Parsing;

/// <summary>
/// 语义化版本比较（只覆盖本面板需要的规则，刻意做得保守）。
///
/// 规则：
///   * 允许前缀 <c>v</c>；不足三段的补 0（<c>1.2</c> == <c>1.2.0</c>）。
///   * 先比数字段（逐段、缺者为 0）。数字更大即更新。
///   * 数字相同时看预发布后缀：正式版 &gt; 预发布版（<c>1.0.0</c> &gt; <c>1.0.0-beta.1</c>）；
///     两边都带预发布后缀时**视为同版本**（不比较 beta.1 / beta.2 这类次序——
///     真要用预发布通道时再扩展，避免现在引入猜测）。
/// </summary>
public static class VersionCompare
{
    public static bool TryParse(string? raw, out int[] segments, out string preRelease)
    {
        segments = new[] { 0, 0, 0 };
        preRelease = string.Empty;

        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = raw.Trim().TrimStart('v', 'V').Trim();
        if (text.Length == 0) return false;

        var dash = text.IndexOf('-');
        var numeric = dash >= 0 ? text[..dash] : text;
        preRelease = dash >= 0 ? text[(dash + 1)..].Trim() : string.Empty;

        var parts = numeric.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Length > 4) return false;

        var parsed = new int[3];
        for (var i = 0; i < parts.Length && i < 3; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out parsed[i]))
            {
                return false;
            }
        }

        segments = parsed;
        return true;
    }

    /// <summary><paramref name="candidate"/> 是否比 <paramref name="current"/> 新。任一侧无法解析时返回 false。</summary>
    public static bool IsNewer(string? current, string? candidate)
    {
        if (!TryParse(current, out var now, out var nowPre)) return false;
        if (!TryParse(candidate, out var next, out var nextPre)) return false;

        for (var i = 0; i < 3; i++)
        {
            if (next[i] != now[i]) return next[i] > now[i];
        }

        var nowIsPre = nowPre.Length > 0;
        var nextIsPre = nextPre.Length > 0;

        // 数字段相同：正式版比预发布版新；同为预发布不加戏
        return nowIsPre && !nextIsPre;
    }
}
