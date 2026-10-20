namespace WslcDesktop.Core.Models;

/// <summary>
/// 按行累积的日志缓冲，容量固定：满了**丢最旧的行**而不是无限长。
///
/// 存在的理由：<c>logs --follow</c> 可能连续输出几小时，界面若一直往字符串里追加，
/// 内存与重排成本都会无限长（这是"跟随"最容易做崩的地方）。丢掉旧行不影响排障 ——
/// 需要完整历史时用「重新读取」取更大快照，或到外置终端里看。
///
/// 线程安全：写入方是流式进程的回调（线程池线程），读取方是界面定时器（UI 线程），
/// 所以 <see cref="Append"/> / <see cref="Text"/> 等全部加锁。
/// </summary>
public sealed class LogBuffer
{
    /// <summary>默认容量。1000 行足够看清一次请求的上下文，重排成本也可控。</summary>
    public const int DefaultCapacity = 1000;

    private readonly object _gate = new();
    private readonly Queue<string> _lines;

    public LogBuffer(int capacity = DefaultCapacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "容量必须大于 0");

        Capacity = capacity;
        _lines = new Queue<string>(capacity);
    }

    public int Capacity { get; }

    /// <summary>当前保留的行数（≤ <see cref="Capacity"/>）。</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _lines.Count;
        }
    }

    /// <summary>累计写入的行数（含已被丢弃的）。界面用它判断"有没有新行"，避免无谓重排。</summary>
    public long TotalAppended { get; private set; }

    /// <summary>因为超出容量而被丢弃的行数。非 0 时界面要提示"更早的已丢弃"。</summary>
    public long DroppedLines
    {
        get
        {
            lock (_gate) return TotalAppended - _lines.Count;
        }
    }

    /// <summary>当前保留的行（副本，按时间顺序）。</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate) return _lines.ToArray();
        }
    }

    /// <summary>拼好的全文（每行一个 <c>\n</c>，末尾不加空行）。</summary>
    public string Text
    {
        get
        {
            lock (_gate)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var line in _lines)
                {
                    sb.Append(line).Append('\n');
                }

                return sb.Length == 0 ? string.Empty : sb.ToString(0, sb.Length - 1);
            }
        }
    }

    /// <summary>
    /// 追加一行。<c>null</c> 视为空行（<c>wslc logs</c> 会输出空行，丢掉会把日志弄乱）。
    /// </summary>
    public void Append(string? line)
    {
        var text = line ?? string.Empty;

        lock (_gate)
        {
            _lines.Enqueue(text);
            TotalAppended++;

            while (_lines.Count > Capacity) _lines.Dequeue();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
            TotalAppended = 0;
        }
    }
}
