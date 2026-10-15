using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Core.Tests;

/// <summary>
/// 发行版侧解析测试。
/// 夹具来源：参考项目 wslc-panel（Apache-2.0）真机抓取输出，原样复用以免"手编夹具正好符合解析器"。
/// </summary>
public class DistroOutputParserTests
{
    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    [Fact]
    public void TestParseList_Single()
    {
        var distros = DistroOutputParser.ParseList(Fixture("distro_list.txt"));

        var only = Assert.Single(distros);
        Assert.Equal("Ubuntu-26.04", only.Name);
        Assert.Equal("Stopped", only.State);
        Assert.Equal(2, only.Version);
        Assert.True(only.IsDefault);
        Assert.False(only.IsRunning);
        Assert.Equal("已停止", only.StateDisplay);
    }

    [Fact]
    public void TestParseList_Multi_HandlesNameWithSpaces()
    {
        var distros = DistroOutputParser.ParseList(Fixture("distro_list_multi.txt"));

        Assert.Equal(3, distros.Count);

        // 关键回归点：名称含空格时不能按空格切列
        Assert.Equal("Ubuntu 24.04 LTS", distros[0].Name);
        Assert.Equal("Running", distros[0].State);
        Assert.True(distros[0].IsRunning);

        Assert.Equal("Debian", distros[1].Name);
        Assert.True(distros[1].IsDefault);

        Assert.Equal("kali-linux", distros[2].Name);
        Assert.Equal(1, distros[2].Version);
    }

    [Fact]
    public void TestMergeRegistryInfo_AttachesBasePathAndDefault()
    {
        var distros = DistroOutputParser.ParseList(Fixture("distro_list.txt"));
        var merged = DistroOutputParser.MergeRegistryInfo(distros, Fixture("lxss_query.txt"));

        var only = Assert.Single(merged);
        Assert.Equal(@"D:\linux\Ubuntu-26.04", only.BasePath);
        Assert.Equal("ext4.vhdx", only.VhdFileName);
        Assert.True(only.IsDefault);
        Assert.Equal(2, only.Version);
        Assert.EndsWith("ext4.vhdx", only.VhdPath);
    }

    [Fact]
    public void TestParseList_EmptyOrGarbage_ReturnsEmpty()
    {
        Assert.Empty(DistroOutputParser.ParseList(null));
        Assert.Empty(DistroOutputParser.ParseList(""));
        Assert.Empty(DistroOutputParser.ParseList("没有任何发行版。"));
    }

    [Fact]
    public void TestMergeRegistryInfo_DefaultDistributionGuidWithoutLeadingBackslash()
    {
        // 回归：DefaultDistribution 注册表值是不含前导反斜杠的 {guid}，而键行 GUID 含 "\"。
        // 不归一化时 perGuid 查找永远失败，wsl --list 又没打 * 时默认标记整体丢失
        var distros = new List<DistroModel>
        {
            new() { Name = "Debian", State = "Stopped", Version = 2 }
        };

        var registry =
            "HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Lxss\r\n" +
            "    DefaultDistribution    REG_SZ            {2a3b4c5d-6e7f-48a9-b0c1-d2e3f4a5b6c7}\r\n" +
            "\r\n" +
            "HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Lxss\\{2a3b4c5d-6e7f-48a9-b0c1-d2e3f4a5b6c7}\r\n" +
            "    DistributionName    REG_SZ            Debian\r\n" +
            "    BasePath    REG_SZ            D:\\linux\\Debian\r\n";

        var merged = DistroOutputParser.MergeRegistryInfo(distros, registry);

        var only = Assert.Single(merged);
        Assert.True(only.IsDefault, "注册表 DefaultDistribution 命中的发行版必须标记为默认");
        Assert.Equal(@"D:\linux\Debian", only.BasePath);
    }
}
