using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Core.Tests;

public class ParserFixtureTests
{
    private static string LoadFixture(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", fileName);
        if (!File.Exists(path))
        {
            // Fallback for direct source tree
            path = Path.Combine(@"C:\Work\workSpace\资料\学习\wslc面板工具\代码\tests\WslcDesktop.Core.Tests\fixtures", fileName);
        }
        return File.ReadAllText(path);
    }

    [Fact]
    public void TestParseContainers_FromFixture()
    {
        var jsonl = LoadFixture("container_list.jsonl");
        var containers = JsonLinesParser.ParseContainers(jsonl);

        Assert.Single(containers);
        var c = containers[0];
        Assert.Equal("ff0667ee90fb", c.Id);
        Assert.Equal("wslc-panel-probe", c.Name);
        Assert.Equal("docker.1ms.run/library/alpine:latest", c.Image);
        Assert.Equal("running", c.State);
        Assert.True(c.IsRunning);
        Assert.Single(c.PortMappings);
        Assert.Equal(18080, c.PortMappings[0].HostPort);
        Assert.Equal(80, c.PortMappings[0].ContainerPort);
        Assert.Equal("http://localhost:18080", c.PortMappings[0].BrowserUrl);
    }

    [Fact]
    public void TestParseStats_FromFixture()
    {
        var jsonl = LoadFixture("container_stats.jsonl");
        var statsList = JsonLinesParser.ParseStats(jsonl);

        Assert.Single(statsList);
        var stats = statsList[0];
        Assert.StartsWith("ff0667ee90fb", stats.ContainerId);
        Assert.Equal("wslc-panel-probe", stats.Name);
        Assert.Equal("0.00%", stats.CpuPercentRaw);
        Assert.Equal("3.465MiB / 15.48GiB", stats.MemoryUsage);
        Assert.Equal(0.02, stats.MemoryPercent, 2);
        Assert.Equal(1, stats.Pids);
    }

    [Fact]
    public void TestParseImages_FromFixture()
    {
        var jsonl = LoadFixture("images.jsonl");
        var images = JsonLinesParser.ParseImages(jsonl);

        Assert.Equal(3, images.Count);
        Assert.Equal("320994c3b997", images[0].Id);
        Assert.Equal("docker.1ms.run/library/alpine", images[0].Repository);
        Assert.Equal("latest", images[0].Tag);
        Assert.Equal("docker.1ms.run/library/alpine:latest", images[0].FullName);
    }

    [Fact]
    public void TestParseNetworks_FromFixture()
    {
        var jsonl = LoadFixture("network_list.jsonl");
        var networks = JsonLinesParser.ParseNetworks(jsonl);

        Assert.Equal(3, networks.Count);
        Assert.Contains(networks, n => n.Name == "bridge" && n.IsBuiltIn);
        Assert.Contains(networks, n => n.Name == "host" && n.IsBuiltIn);
        Assert.Contains(networks, n => n.Name == "none" && n.IsBuiltIn);
    }

    [Fact]
    public void TestParseSessions_FromFixture()
    {
        var tableText = LoadFixture("session_list.txt");
        var sessions = TableParser.ParseSessions(tableText);

        Assert.Single(sessions);
        Assert.Equal(1, sessions[0].Id);
        Assert.Equal(21684, sessions[0].CreatorPid);
        Assert.Equal("wslc-cli-76434", sessions[0].DisplayName);
        Assert.True(sessions[0].IsCliDefault);
    }

    [Fact]
    public void TestParseSystemInfo_FromFixture()
    {
        var json = LoadFixture("info.json");
        var info = JsonLinesParser.ParseSystemInfo(json);

        Assert.Equal("3.0.1.0", info.WslVersion);
        Assert.Equal("6.18.40.1-1", info.KernelVersion);
        Assert.Equal("3.0.1", info.SessionManagerVersion);
        Assert.Single(info.ActiveSessions);
        Assert.Equal("wslc-cli-76434", info.ActiveSessions[0].DisplayName);
    }

    [Fact]
    public void TestIdMatches_HandlesPrefixAndSha256()
    {
        var shortId = "ff0667ee90fb";
        var fullId = "ff0667ee90fbc7c73f75ab9bf4fc642b924a6e6bdf0ab6e11dfc647e7dddada0";
        var shaPrefixed = "sha256:ff0667ee90fbc7c73f75ab9bf4fc642b924a6e6bdf0ab6e11dfc647e7dddada0";

        Assert.True(OutputSanitizer.IdMatches(shortId, fullId));
        Assert.True(OutputSanitizer.IdMatches(fullId, shortId));
        Assert.True(OutputSanitizer.IdMatches(shortId, shaPrefixed));
        Assert.False(OutputSanitizer.IdMatches(shortId, "e2ac70e7319a"));
    }

    [Fact]
    public void TestNormalizeContainerName_RemovesLeadingSlash()
    {
        Assert.Equal("my-container", OutputSanitizer.NormalizeContainerName("/my-container"));
        Assert.Equal("my-container", OutputSanitizer.NormalizeContainerName("my-container"));
        Assert.Equal("", OutputSanitizer.NormalizeContainerName(null));
    }
}
