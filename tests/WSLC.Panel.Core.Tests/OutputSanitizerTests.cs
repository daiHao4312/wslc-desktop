using WSLC.Panel.Core.Parsing;

namespace WSLC.Panel.Core.Tests;

/// <summary>
/// OutputSanitizer 边界测试。
/// 重点回归：端口 int.Parse 溢出曾让 JsonLinesParser 的 try/catch 吞掉整行容器
/// （用户侧表现是"列表里少了一个容器"且无任何提示）。
/// </summary>
public class OutputSanitizerTests
{
    [Fact]
    public void TestParsePorts_OverflowPortSkipsMappingButKeepsOthers()
    {
        // 正则 \d+ 不限位数：99999999999 超出 int 范围
        var ports = OutputSanitizer.ParsePorts("0.0.0.0:99999999999->80/tcp, 0.0.0.0:8080->80/tcp");

        Assert.Single(ports);
        Assert.Equal(8080, ports[0].HostPort);
    }

    [Fact]
    public void TestParsePorts_KeepsRealBindingAddressAndReadsAllFields()
    {
        var ports = OutputSanitizer.ParsePorts("0.0.0.0:443->443/tcp");

        var only = Assert.Single(ports);
        // 0.0.0.0 必须原样保留：详情页要与 `wslc ps` 的 "0.0.0.0:3306->3306/tcp" 一致
        Assert.Equal("0.0.0.0", only.HostIp);
        Assert.Equal(443, only.HostPort);
        Assert.Equal(443, only.ContainerPort);
        Assert.Equal("tcp", only.Protocol);
    }

    [Fact]
    public void TestParsePorts_MissingAddressFallsBackToWildcard()
    {
        var only = Assert.Single(OutputSanitizer.ParsePorts("8080->80/tcp"));

        Assert.Equal("0.0.0.0", only.HostIp);
        Assert.Equal(8080, only.HostPort);
    }

    [Fact]
    public void TestParsePorts_DisplayKeepsIpAndBrowsableUrlUsesLocalhostForWildcard()
    {
        var wildcard = Assert.Single(OutputSanitizer.ParsePorts("0.0.0.0:3306->3306/tcp"));
        Assert.Equal("0.0.0.0:3306→3306", wildcard.ShortDisplay);
        Assert.Equal("0.0.0.0:3306->3306/tcp", wildcard.DisplayString);
        // 0.0.0.0 不是可连接地址：浏览器一律走 localhost
        Assert.Equal("http://localhost:3306", wildcard.BrowserUrl);

        var bound = Assert.Single(OutputSanitizer.ParsePorts("192.168.1.20:9000->9000/tcp"));
        Assert.Equal("192.168.1.20:9000→9000", bound.ShortDisplay);
        Assert.Equal("http://192.168.1.20:9000", bound.BrowserUrl);

        var loopback = Assert.Single(OutputSanitizer.ParsePorts("127.0.0.1:8080->80/tcp"));
        Assert.Equal("http://localhost:8080", loopback.BrowserUrl);
    }

    [Fact]
    public void TestParsePorts_MultipleMappingsAllKeepTheirOwnAddress()
    {
        var ports = OutputSanitizer.ParsePorts("0.0.0.0:80->80/tcp, 127.0.0.1:5432->5432/tcp");

        Assert.Equal(2, ports.Count);
        Assert.Equal("0.0.0.0", ports[0].HostIp);
        Assert.Equal("127.0.0.1", ports[1].HostIp);
    }

    [Fact]
    public void TestParsePorts_EmptyOrGarbageReturnsEmpty()
    {
        Assert.Empty(OutputSanitizer.ParsePorts(null));
        Assert.Empty(OutputSanitizer.ParsePorts(""));
        Assert.Empty(OutputSanitizer.ParsePorts("not-a-port"));
    }

    [Fact]
    public void TestIdMatches_PrefixAndSha256()
    {
        Assert.True(OutputSanitizer.IdMatches("abc123def456", "abc123def456789 full"));
        Assert.True(OutputSanitizer.IdMatches("sha256:abc123", "abc123"));
        Assert.False(OutputSanitizer.IdMatches("abc", "xyz"));
        Assert.False(OutputSanitizer.IdMatches(null, "abc"));
    }
}
