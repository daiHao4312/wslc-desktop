namespace WslcDesktop.Core.Parsing;

/// <summary>
/// 把异常整理成能给用户看的一句话。
///
/// 起因是一次真实排障：API 通道附着失败时界面只显示
/// <c>附着会话失败：无法找到与此错误代码关联的文本。 无法找到与此错误代码关联的文本。</c>
/// ——**没有 HRESULT 码，用户和我都无从下手**；而且同一句话还被拼了两遍
/// （WinRT 投影异常会把内层消息接在 Message 后面）。
/// </summary>
public static class ExceptionText
{
    /// <summary>异常 → 一句话（去重的消息 + 十六进制 HRESULT）。</summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var message = CollapseDuplicate(exception.Message);
        var code = exception.HResult != 0 ? $"（HRESULT 0x{exception.HResult:X8}）" : string.Empty;

        // 内层异常常常才是真正的原因（例如投影包的 TargetInvocationException）
        var inner = exception.InnerException;
        var innerText = inner is null ? string.Empty : $"，内层：{CollapseDuplicate(inner.Message)}";

        return message.Length == 0
            ? $"{exception.GetType().Name}{code}{innerText}"
            : $"{message}{code}{innerText}";
    }

    /// <summary>
    /// 同一句话被拼了两遍时只留一遍。
    ///
    /// 判据刻意保守：按空格切成偶数段、且前后半段**完全相等**才折叠 ——
    /// 宁可留着重复，也不要把两段本来就不同的话误合成一段。
    /// </summary>
    public static string CollapseDuplicate(string? message)
    {
        var text = (message ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts.Length % 2 == 0)
        {
            var half = parts.Length / 2;
            if (parts.Take(half).SequenceEqual(parts.Skip(half)))
            {
                return string.Join(' ', parts.Take(half));
            }
        }

        return text;
    }
}
