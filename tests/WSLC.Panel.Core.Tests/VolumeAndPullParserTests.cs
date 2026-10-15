using WSLC.Panel.Core.Parsing;

namespace WSLC.Panel.Core.Tests;

/// <summary>卷与拉取进度的解析测试，夹具均为真机抓取。</summary>
public class VolumeAndPullParserTests
{
    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    [Fact]
    public void TestParseVolumes_FromRealOutput()
    {
        var volumes = JsonLinesParser.ParseVolumes(Fixture("volume_list.jsonl"));

        Assert.Equal(3, volumes.Count);
        Assert.All(volumes, v =>
        {
            Assert.False(string.IsNullOrWhiteSpace(v.Name));
            Assert.Equal("guest", v.Driver);
            Assert.Equal("local", v.Scope);
        });

        // wslc 的 Size 是 "N/A" → 归一为空串，交给界面显示"未知"
        Assert.All(volumes, v => Assert.Equal(string.Empty, v.Size));
        Assert.All(volumes, v => Assert.True(v.IsAnonymous));
        Assert.All(volumes, v => Assert.Equal("匿名卷", v.DisplayLabel));
        Assert.Contains(volumes, v => v.Mountpoint.StartsWith("/var/lib/docker/volumes/"));
    }

    [Fact]
    public void TestParseVolumes_EmptyInput()
    {
        Assert.Empty(JsonLinesParser.ParseVolumes(null));
        Assert.Empty(JsonLinesParser.ParseVolumes(""));
    }

    [Fact]
    public void TestPullProgress_RecognizesStatusSequence()
    {
        var lines = Fixture("pull_progress_alpine.txt")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var parsed = lines
            .Select(l => PullProgressParser.TryParse(l, "alpine:latest", out var p) ? p : null)
            .Where(p => p != null)
            .Select(p => p!)
            .ToList();

        Assert.Contains(parsed, p => p.Status == "Pulling fs layer" && p.LayerId == "e2de96513ba9");
        Assert.Contains(parsed, p => p.Status == "Downloading");
        Assert.Contains(parsed, p => p.Status == "Extracting");
        Assert.Contains(parsed, p => p.Status == "Pull complete" && p.IsTerminal);
        Assert.Contains(parsed, p => p.Status == "Digest");

        // 本机 wslc 不给字节数 → 百分比必须为 null，界面据此走"不确定进度条"
        Assert.All(parsed, p => Assert.Null(p.Percent));
    }

    [Fact]
    public void TestPullProgress_ParsesBytesWhenPresent()
    {
        var ok = PullProgressParser.TryParse("9a02b1d1b3f5: Downloading [====>     ]  1.5MB/3MB", "nginx:latest", out var progress);

        Assert.True(ok);
        Assert.Equal("9a02b1d1b3f5", progress.LayerId);
        Assert.Equal("Downloading", progress.Status);
        Assert.Equal(1_500_000UL, progress.DownloadedBytes);
        Assert.Equal(3_000_000UL, progress.TotalBytes);
        Assert.NotNull(progress.Percent);
        Assert.Equal(50, progress.Percent!.Value, 0);
    }

    [Fact]
    public void TestPullResult_FailureSurfacesRealReason()
    {
        var raw = new WSLC.Panel.Core.Abstractions.ProcessExecutionResult(
            1,
            string.Empty,
            "来自引擎的错误信息: Get \"https://registry-1.docker.io/v2/\": context deadline exceeded\n错误代码： E_FAIL\n如果此错误是意外错误，请考虑搜索现有问题\n",
            false);

        var result = PullProgressParser.BuildResult("alpine:latest", raw, TimeSpan.FromSeconds(16));

        Assert.False(result.Success);
        Assert.Contains("context deadline exceeded", result.Message);
        // 页脚那两句模板噪声必须被过滤掉
        Assert.DoesNotContain("如果此错误是意外错误", result.Message);
        Assert.Equal(16, result.Duration.TotalSeconds, 0);
    }

    [Fact]
    public void TestPullResult_SuccessUsesStatusLine()
    {
        var raw = new WSLC.Panel.Core.Abstractions.ProcessExecutionResult(
            0,
            Fixture("pull_progress_alpine.txt"),
            string.Empty,
            true);

        var result = PullProgressParser.BuildResult("alpine:latest", raw, TimeSpan.FromSeconds(9));

        Assert.True(result.Success);
        Assert.Contains("Downloaded newer image", result.Message);
    }
}
