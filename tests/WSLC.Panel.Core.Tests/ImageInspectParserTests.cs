using WSLC.Panel.Core.Parsing;

namespace WSLC.Panel.Core.Tests;

/// <summary>镜像 inspect 解析测试（夹具为参考项目真机抓取的 nginx / hello-world 输出）。</summary>
public class ImageInspectParserTests
{
    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    [Fact]
    public void TestParseNginx_ExtractsPortsCommandAndEnv()
    {
        var info = ImageInspectParser.Parse(Fixture("image_inspect_nginx.json"));

        Assert.True(info.Found);
        Assert.Equal(new[] { 80 }, info.ExposedPorts);
        Assert.Equal(new[] { "nginx", "-g", "daemon off;" }, info.Command);
        Assert.Equal(new[] { "/docker-entrypoint.sh" }, info.Entrypoint);
        Assert.Contains(info.Environment, e => e.StartsWith("NGINX_VERSION="));
        Assert.Contains("nginx", info.StartupSummary);
        Assert.Equal("镜像声明端口：80", info.ExposedPortsSummary);
    }

    [Fact]
    public void TestParseHelloWorld_NoExposedPorts()
    {
        var info = ImageInspectParser.Parse(Fixture("image_inspect_hello_world.json"));

        Assert.True(info.Found);
        Assert.Empty(info.ExposedPorts);
        Assert.Equal("镜像未声明端口", info.ExposedPortsSummary);
    }

    [Fact]
    public void TestParse_EmptyArrayMeansNotLocal()
    {
        var info = ImageInspectParser.Parse("[]");

        Assert.False(info.Found);
        Assert.Equal("镜像不在本地", info.Error);
    }

    [Fact]
    public void TestParse_EngineErrorTextIsSurfaced()
    {
        var info = ImageInspectParser.Parse("未找到映像 'not-exist'。\n错误代码： E_FAIL");

        Assert.False(info.Found);
        Assert.Contains("未找到映像", info.Error);
    }

    [Fact]
    public void TestParse_EmptyInput()
    {
        var info = ImageInspectParser.Parse("");

        Assert.False(info.Found);
        Assert.False(string.IsNullOrWhiteSpace(info.Error));
    }

    [Fact]
    public void TestParse_ArrayOfNonObjectsDoesNotThrow()
    {
        // 引擎异常输出（如 ["error text"]）：旧实现直接 root[0].TryGetProperty，
        // 抛 InvalidOperationException 且不被 catch (JsonException) 捕获，
        // 会一路冒泡到 UI 层弹全局错误框
        var info = ImageInspectParser.Parse("[\"error text\"]");

        Assert.False(info.Found);
        Assert.Equal("镜像信息格式无法识别", info.Error);
    }
}
