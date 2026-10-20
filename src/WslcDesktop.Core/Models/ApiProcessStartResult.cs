namespace WslcDesktop.Core.Models;

/// <summary>
/// 「在容器里起一个进程」的结果。
///
/// 用结果对象而不是 null：界面需要把"为什么没起来"说出来（通道没附着？容器不在运行？引擎报错？），
/// 只回 null 的话用户只会看到"点了没反应"。
/// </summary>
public sealed record ApiProcessStartResult
{
    /// <summary>起成功时的句柄；null 表示没起来（看 <see cref="Error"/>）。</summary>
    public Abstractions.IApiContainerProcess? Process { get; init; }

    /// <summary>没起来的原因；成功时为空。</summary>
    public string Error { get; init; } = string.Empty;

    public bool Available => Process is not null;

    public static ApiProcessStartResult Unavailable(string reason) => new() { Error = reason };

    public static ApiProcessStartResult Started(Abstractions.IApiContainerProcess process)
        => new() { Process = process };
}

/// <summary>
/// 把「字节块」拼成「完整的行」。
///
/// 为什么需要：官方 <c>ProcessOutputHandler</c> 的回调参数是 <c>byte[]</c> 片段
/// （不是一行一个字符串），片段可能**从一行中间断开**，也可能把一个 UTF-8 多字节字符拆开。
/// 直接 <c>Encoding.UTF8.GetString</c> 每块一次会造出乱码与断行，所以用 <see cref="System.Text.Decoder"/>
/// 保留跨块状态，再按 <c>\n</c> 切行。
/// </summary>
public sealed class Utf8LineSplitter
{
    private readonly System.Text.Decoder _decoder = System.Text.Encoding.UTF8.GetDecoder();
    private readonly System.Text.StringBuilder _pending = new();

    /// <summary>喂一段字节，返回其中**已经完整**的行（不含行尾 \r）。</summary>
    public IReadOnlyList<string> Push(byte[]? data)
    {
        if (data is null || data.Length == 0) return Array.Empty<string>();

        var chars = new char[_decoder.GetCharCount(data, 0, data.Length)];
        _decoder.GetChars(data, 0, data.Length, chars, 0);

        var lines = new List<string>();

        foreach (var ch in chars)
        {
            if (ch == '\n')
            {
                lines.Add(TrimPending());
                _pending.Clear();
            }
            else
            {
                _pending.Append(ch);
            }
        }

        return lines;
    }

    /// <summary>进程结束时把最后没换行的半行吐出来（没有就返回 null）。</summary>
    public string? Flush()
    {
        if (_pending.Length == 0) return null;

        var line = TrimPending();
        _pending.Clear();
        return line;
    }

    private string TrimPending()
        => _pending.Length > 0 && _pending[^1] == '\r'
            ? _pending.ToString(0, _pending.Length - 1)
            : _pending.ToString();
}
