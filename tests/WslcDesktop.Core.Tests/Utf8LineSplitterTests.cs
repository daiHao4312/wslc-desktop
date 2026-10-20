using System.Text;
using WslcDesktop.Core.Models;

namespace WslcDesktop.Core.Tests;

/// <summary>
/// 容器内进程输出的拼行（M19）。
///
/// 引擎给的是 <c>byte[]</c> 片段，可能从一行中间、甚至一个 UTF-8 字符中间断开 ——
/// 直接对每个片段 <c>GetString</c> 会造出乱码和断行，这也是这个类存在的唯一理由。
/// </summary>
public class Utf8LineSplitterTests
{
    [Fact]
    public void 一次给出多行时只返回完整的行()
    {
        var splitter = new Utf8LineSplitter();

        var lines = splitter.Push(Encoding.UTF8.GetBytes("a\nb\nc"));

        Assert.Equal(new[] { "a", "b" }, lines); // "c" 还没换行
        Assert.Equal("c", splitter.Flush());     // 结束时吐出最后半行
    }

    [Fact]
    public void CRLF的行尾不会串进内容()
    {
        var splitter = new Utf8LineSplitter();

        Assert.Equal(new[] { "hello" }, splitter.Push(Encoding.UTF8.GetBytes("hello\r\n")));
        Assert.Null(splitter.Flush());
    }

    [Fact]
    public void 空行要保留()
    {
        var splitter = new Utf8LineSplitter();

        Assert.Equal(new[] { "a", "" }, splitter.Push(Encoding.UTF8.GetBytes("a\n\n")));
    }

    /// <summary>关键用例：一个汉字被拆到两个字节块里，绝不能出乱码。</summary>
    [Fact]
    public void 多字节字符被拆到两个字节块也不乱码()
    {
        var splitter = new Utf8LineSplitter();
        var bytes = Encoding.UTF8.GetBytes("中文\n"); // 中(3) 文(3) \n(1)

        Assert.Empty(splitter.Push(bytes[..2]));  // "中" 只到了一半
        Assert.Empty(splitter.Push(bytes[2..5])); // "中" 完整、"文" 一半，且还没有换行
        Assert.Equal(new[] { "中文" }, splitter.Push(bytes[5..]));
    }

    [Fact]
    public void 行被拆到两个字节块时能拼起来()
    {
        var splitter = new Utf8LineSplitter();
        var bytes = Encoding.UTF8.GetBytes("prefix-suffix\n");

        Assert.Empty(splitter.Push(bytes[..6]));
        Assert.Equal(new[] { "prefix-suffix" }, splitter.Push(bytes[6..]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(8)]
    public void 任意切点都切出同一组行(int cut)
    {
        var text = "line1\n中文行\nline3\n";
        var bytes = Encoding.UTF8.GetBytes(text);
        var splitter = new Utf8LineSplitter();

        var lines = new List<string>();
        lines.AddRange(splitter.Push(bytes[..cut]));
        lines.AddRange(splitter.Push(bytes[cut..]));
        if (splitter.Flush() is { } tail) lines.Add(tail);

        Assert.Equal(new[] { "line1", "中文行", "line3" }, lines);
    }

    [Fact]
    public void 空输入不算一行()
    {
        var splitter = new Utf8LineSplitter();

        Assert.Empty(splitter.Push(null));
        Assert.Empty(splitter.Push(Array.Empty<byte>()));
        Assert.Null(splitter.Flush());
    }
}
