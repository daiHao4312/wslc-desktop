using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Tests;

/// <summary>
/// 跟随日志的缓冲（M15）。容量上限是硬约束：
/// 跟随可能挂几小时，不设上限就是内存泄漏 + 界面越来越卡。
/// </summary>
public class LogBufferTests
{
    [Fact]
    public void 按顺序拼成文本_末尾没有多余空行()
    {
        var buffer = new LogBuffer(10);

        buffer.Append("第一行");
        buffer.Append("第二行");

        Assert.Equal("第一行\n第二行", buffer.Text);
        Assert.Equal(2, buffer.Count);
        Assert.Equal(0, buffer.DroppedLines);
    }

    [Fact]
    public void 超过容量丢最旧的行_并记录丢弃数()
    {
        var buffer = new LogBuffer(3);

        for (var i = 1; i <= 5; i++) buffer.Append($"L{i}");

        Assert.Equal(new[] { "L3", "L4", "L5" }, buffer.Lines);
        Assert.Equal(3, buffer.Count);
        Assert.Equal(5, buffer.TotalAppended); // 累计数不因丢弃而减少
        Assert.Equal(2, buffer.DroppedLines);
    }

    [Fact]
    public void 空行也要保留_否则日志会串行()
    {
        var buffer = new LogBuffer(10);

        buffer.Append("上面");
        buffer.Append(null);
        buffer.Append("下面");

        Assert.Equal("上面\n\n下面", buffer.Text);
        Assert.Equal(3, buffer.Count);
    }

    [Fact]
    public void 清空后计数归零()
    {
        var buffer = new LogBuffer(10);
        buffer.Append("x");

        buffer.Clear();

        Assert.Empty(buffer.Lines);
        Assert.Equal(string.Empty, buffer.Text);
        Assert.Equal(0, buffer.TotalAppended);
        Assert.Equal(0, buffer.DroppedLines);
    }

    [Fact]
    public void 容量必须大于零()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new LogBuffer(0));

    /// <summary>
    /// 写入来自流式进程的回调（线程池），读取来自界面定时器 —— 不加锁就会丢行或炸在 Queue 上。
    /// </summary>
    [Fact]
    public void 多线程并发写入不丢行()
    {
        var buffer = new LogBuffer(LogBuffer.DefaultCapacity);

        Parallel.For(0, 4, worker =>
        {
            for (var i = 0; i < 250; i++) buffer.Append($"{worker}-{i}");
        });

        Assert.Equal(1000, buffer.TotalAppended);
        Assert.Equal(1000, buffer.Count);
        Assert.Equal(0, buffer.DroppedLines);
    }
}
