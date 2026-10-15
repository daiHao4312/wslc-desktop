using WSLC.Panel.Core.Models;
using WSLC.Panel.Core.Parsing;

namespace WSLC.Panel.Core.Tests;

/// <summary>会话存储扫描测试（用临时目录模拟 %LOCALAPPDATA%\wslc\sessions 结构）。</summary>
public class SessionStorageLocatorTests : IDisposable
{
    private readonly string _root;

    public SessionStorageLocatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"wslc_sessions_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }

    private void CreateSession(string name, int storageBytes, int? swapBytes = null)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "storage.vhdx"), new byte[storageBytes]);
        if (swapBytes.HasValue)
        {
            File.WriteAllBytes(Path.Combine(dir, "swap.vhdx"), new byte[swapBytes.Value]);
        }
    }

    [Fact]
    public void TestEnrich_MatchesDirectoryByName()
    {
        CreateSession("wslc-cli-UMS", 4096, 1024);
        CreateSession("WSLCPanelTest", 512);

        var sessions = new[]
        {
            new SessionModel { Id = 1, DisplayName = "wslc-cli-UMS" },
            new SessionModel { Id = 2, DisplayName = "WSLCPanelTest" }
        };

        var enriched = SessionStorageLocator.Enrich(sessions, _root);

        var first = enriched.Single(s => s.DisplayName == "wslc-cli-UMS");
        Assert.Equal(4096, first.VhdSizeBytes);
        Assert.Equal(1024, first.SwapSizeBytes);
        Assert.EndsWith("storage.vhdx", first.StoragePath);

        var second = enriched.Single(s => s.DisplayName == "WSLCPanelTest");
        Assert.Equal(512, second.VhdSizeBytes);
        Assert.Null(second.SwapSizeBytes);
    }

    [Fact]
    public void TestEnrich_MissingDirectoryKeepsSession()
    {
        var sessions = new[] { new SessionModel { Id = 9, DisplayName = "不存在-会话" } };

        var enriched = SessionStorageLocator.Enrich(sessions, _root);

        var only = Assert.Single(enriched);
        Assert.Equal("不存在-会话", only.DisplayName);
        Assert.Null(only.VhdSizeBytes);
        Assert.Equal("未知", only.VhdSizeDisplay);
    }

    [Fact]
    public void TestGetTotalUsageBytes_SumsStorageAndSwap()
    {
        CreateSession("a", 1000, 500);
        CreateSession("b", 2000);

        var enriched = SessionStorageLocator.Enrich(new[]
        {
            new SessionModel { Id = 1, DisplayName = "a" },
            new SessionModel { Id = 2, DisplayName = "b" }
        }, _root);

        Assert.Equal(3500, SessionStorageLocator.GetTotalUsageBytes(enriched));
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2.0 KB")]
    [InlineData(5 * 1024 * 1024, "5.0 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3.00 GB")]
    public void TestFormatBytes(long bytes, string expected)
    {
        Assert.Equal(expected, SessionStorageLocator.FormatBytes(bytes));
    }

    [Fact]
    public void TestFormatBytes_NullIsUnknown()
    {
        Assert.Equal("未知", SessionStorageLocator.FormatBytes(null));
    }

    [Fact]
    public void TestGetSessionsRoot_UsesOverride()
    {
        Assert.Equal(Path.Combine("D:\\data", "wslc", "sessions"), SessionStorageLocator.GetSessionsRoot("D:\\data"));
    }
}
