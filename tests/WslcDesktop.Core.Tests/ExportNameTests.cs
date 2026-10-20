using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Core.Tests;

/// <summary>
/// 导出文件名的生成（日志/命令输出另存）。
/// 容器名里可能有 <c>/ : *</c> 之类的字符，Windows 会直接拒绝写文件。
/// </summary>
public class ExportNameTests
{
    private static readonly DateTimeOffset Fixed = new(2026, 10, 20, 16, 1, 2, TimeSpan.FromHours(8));

    [Fact]
    public void 名字带前缀与时间戳()
        => Assert.Equal("container-nginx-logs-20261020-160102.txt", ExportName.Suggest("container-nginx-logs", ".txt", Fixed));

    [Theory]
    [InlineData("a/b", "a-b-20261020-160102.txt")]
    [InlineData("a:b", "a-b-20261020-160102.txt")]
    [InlineData("a*b", "a-b-20261020-160102.txt")]
    [InlineData("a\\b", "a-b-20261020-160102.txt")]
    public void 非法字符换成短横线(string prefix, string expectedSuffix)
        => Assert.EndsWith(expectedSuffix, ExportName.Suggest(prefix, ".txt", Fixed));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void 空前缀回退成export(string? prefix)
        => Assert.StartsWith("export-", ExportName.Suggest(prefix!, ".txt", Fixed));

    [Fact]
    public void 扩展名缺点的会自动补上()
        => Assert.EndsWith(".log", ExportName.Suggest("x", "log", Fixed));
}
