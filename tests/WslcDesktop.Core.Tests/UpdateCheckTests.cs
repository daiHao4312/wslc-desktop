using System.Net;
using System.Text;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;
using WslcDesktop.Core.Services;

namespace WslcDesktop.Core.Tests;

/// <summary>
/// M12 更新检查：发布信息解析（纯函数）+ 版本比较 + 服务层降级路径。
/// 全部用假 HttpMessageHandler，不依赖网络。
///
/// 夹具 JSON 按 GitHub「Get the latest release」的响应结构手工构造
/// （字段名与真实响应一致，含 assets 数组与 published_at）。
/// </summary>
public class UpdateCheckTests
{
    private const string ReleaseJson = """
    {
      "tag_name": "v1.2.0",
      "name": "wslc-desktop 1.2.0",
      "html_url": "https://github.com/example/wslc-panel/releases/tag/v1.2.0",
      "body": "# 更新内容\n\n- 修掉批量条点不动的问题\n- 端口列显示 IP\n",
      "published_at": "2026-10-01T08:30:00Z",
      "assets": [
        { "name": "checksums.txt", "browser_download_url": "https://example.com/checksums.txt" },
        {
          "name": "WslcDesktop.App.exe",
          "browser_download_url": "https://example.com/WslcDesktop.App.exe",
          "size": 95724931,
          "digest": "sha256:8EEFA14F0AB353CCBF6A88B638B4B6124C3C70FBDF440622EC83F18249FEDB49"
        }
      ]
    }
    """;

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly Exception? _throw;

        public HttpRequestMessage? LastRequest { get; private set; }

        public StubHandler(HttpStatusCode status, string body = "", Exception? toThrow = null)
        {
            _status = status;
            _body = body;
            _throw = toThrow;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;

            if (_throw is not null) throw _throw;

            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }

    // ---------- 解析 ----------

    [Fact]
    public void TestParseRelease_ReadsTagNotesAndPicksExeAsset()
    {
        var release = GitHubReleaseParser.Parse(ReleaseJson);

        Assert.NotNull(release);
        Assert.Equal("v1.2.0", release!.TagName);
        Assert.Equal("1.2.0", release.VersionDisplay);
        Assert.Equal("WslcDesktop.App.exe", release.AssetName);
        Assert.Equal("https://example.com/WslcDesktop.App.exe", release.DownloadUrl);
        Assert.Equal(2026, release.PublishedAt!.Value.Year);
        Assert.Contains("批量条点不动", release.NotesSummary);
        // 摘要必须是单行紧凑文本
        Assert.DoesNotContain('\n', release.NotesSummary);

        // 在线更新要用的大小与摘要（GitHub 的 digest 会带算法前缀，要归一化成小写十六进制）
        Assert.Equal(95724931L, release.AssetSize);
        Assert.Equal("8eefa14f0ab353ccbf6a88b638b4b6124c3c70fbdf440622ec83f18249fedb49", release.AssetSha256);
    }

    [Fact]
    public void TestParseRelease_ToleratesAssetsWithoutSizeOrDigest()
    {
        const string json = """
        { "tag_name": "v1.1.0", "html_url": "https://example.com",
          "assets": [ { "name": "wslc-desktop.exe", "browser_download_url": "https://example.com/wslc-desktop.exe",
                        "size": -1, "digest": "sha512:zzz" } ] }
        """;

        var release = GitHubReleaseParser.Parse(json);

        Assert.NotNull(release);
        // 负数大小与不认识的算法都当"没有"，不能拿错的哈希去校验好包
        Assert.Null(release!.AssetSize);
        Assert.Null(release.AssetSha256);
        Assert.NotNull(release.AssetUrl);
    }

    [Fact]
    public void TestParseRelease_WithoutExeAssetFallsBackToReleasePage()
    {
        const string json = """
        { "tag_name": "1.0.1", "html_url": "https://github.com/example/wslc-panel/releases/tag/1.0.1",
          "assets": [ { "name": "notes.md", "browser_download_url": "https://example.com/notes.md" } ] }
        """;

        var release = GitHubReleaseParser.Parse(json);

        Assert.NotNull(release);
        Assert.Null(release!.AssetUrl);
        Assert.Equal("https://github.com/example/wslc-panel/releases/tag/1.0.1", release.DownloadUrl);
    }

    [Fact]
    public async Task TestCheck_WithoutExeAssetCannotAutoUpdate()
    {
        const string json = """
        { "tag_name": "v1.2.0", "html_url": "https://github.com/example/wslc-panel/releases/tag/v1.2.0",
          "assets": [ { "name": "source.zip", "browser_download_url": "https://example.com/source.zip" } ] }
        """;

        using var checker = new UpdateChecker(new HttpClient(new StubHandler(HttpStatusCode.OK, json)));
        var result = await checker.CheckAsync("example/wslc-panel", "1.0.0");

        Assert.True(result.HasUpdate);
        // 能点开下载页，但没有可替换的 exe 资产 → 不能在线更新
        Assert.True(result.CanOpenDownload);
        Assert.False(result.CanAutoUpdate);
        Assert.Null(result.ToPackage());
    }

    [Fact]
    public void TestParseRelease_RejectsGarbage()
    {
        Assert.Null(GitHubReleaseParser.Parse(null));
        Assert.Null(GitHubReleaseParser.Parse(""));
        Assert.Null(GitHubReleaseParser.Parse("not json"));
        Assert.Null(GitHubReleaseParser.Parse("[1,2,3]"));
        // 没有 tag_name 就没有可比较的版本号
        Assert.Null(GitHubReleaseParser.Parse("""{ "name": "no tag" }"""));
    }

    // ---------- 版本比较 ----------

    [Theory]
    [InlineData("1.0.0", "1.0.1", true)]
    [InlineData("1.0.0", "v1.1.0", true)]
    [InlineData("1.0.0", "2.0.0", true)]
    [InlineData("1.10.0", "1.9.0", false)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0.0", "1.0", false)]
    [InlineData("1.2", "1.2.0", false)]
    [InlineData("1.0.0", "1.0.0-beta.1", false)]
    [InlineData("1.0.0-beta.1", "1.0.0", true)]
    [InlineData("garbage", "1.0.0", false)]
    public void TestVersionCompare_IsNewer(string current, string candidate, bool expected)
        => Assert.Equal(expected, VersionCompare.IsNewer(current, candidate));

    [Fact]
    public void TestVersionCompare_TryParse()
    {
        Assert.True(VersionCompare.TryParse("v1.2.3", out var seg, out var pre));
        Assert.Equal(new[] { 1, 2, 3 }, seg);
        Assert.Equal("", pre);

        Assert.True(VersionCompare.TryParse("1.2.3-rc.1", out _, out var pre2));
        Assert.Equal("rc.1", pre2);

        Assert.False(VersionCompare.TryParse("", out _, out _));
        Assert.False(VersionCompare.TryParse("x.y.z", out _, out _));
    }

    // ---------- 服务层降级 ----------

    [Fact]
    public async Task TestCheck_EmptyRepoIsNotConfigured()
    {
        using var checker = new UpdateChecker(new HttpClient(new StubHandler(HttpStatusCode.OK)));

        var result = await checker.CheckAsync("   ", "1.0.0");

        Assert.Equal(UpdateCheckStatus.NotConfigured, result.Status);
        Assert.False(result.CanOpenDownload);
    }

    [Fact]
    public async Task TestCheck_InvalidSlugFailsBeforeRequest()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        using var checker = new UpdateChecker(new HttpClient(handler));

        var result = await checker.CheckAsync("not-a-repo-slug", "1.0.0");

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains("owner/repo", result.Message);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task TestCheck_UpdateAvailableWithDownloadUrl()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ReleaseJson);
        using var checker = new UpdateChecker(new HttpClient(handler));

        var result = await checker.CheckAsync("example/wslc-panel", "1.0.0");

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("1.2.0", result.LatestVersion);
        Assert.True(result.CanOpenDownload);
        Assert.Equal("https://example.com/WslcDesktop.App.exe", result.DownloadUrl);

        // 可以从检查结果直接走到"下载并替换"
        Assert.True(result.CanAutoUpdate);
        var package = result.ToPackage();
        Assert.NotNull(package);
        Assert.Equal("WslcDesktop.App.exe", package!.FileName);
        Assert.Equal(95724931L, package.Size);
        Assert.True(package.HasChecksum);

        // 打的是 latest release 接口，且带了 GitHub 必需的 UA
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("https://api.github.com/repos/example/wslc-panel/releases/latest", handler.LastRequest!.RequestUri!.ToString());
        Assert.NotNull(handler.LastRequest.Headers.UserAgent.ToString());
        Assert.NotEmpty(handler.LastRequest.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task TestCheck_SameVersionIsUpToDate()
    {
        using var checker = new UpdateChecker(new HttpClient(new StubHandler(HttpStatusCode.OK, ReleaseJson)));

        var result = await checker.CheckAsync("example/wslc-panel", "1.2.0");

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.False(result.CanOpenDownload);
        Assert.Contains("已是最新", result.Message);
    }

    [Fact]
    public async Task TestCheck_NotFoundAndServerErrorDegradeGracefully()
    {
        using var notFound = new UpdateChecker(new HttpClient(new StubHandler(HttpStatusCode.NotFound)));
        var r1 = await notFound.CheckAsync("example/missing", "1.0.0");
        Assert.Equal(UpdateCheckStatus.Failed, r1.Status);
        Assert.Contains("找不到仓库", r1.Message);

        using var serverError = new UpdateChecker(new HttpClient(new StubHandler(HttpStatusCode.InternalServerError)));
        var r2 = await serverError.CheckAsync("example/wslc-panel", "1.0.0");
        Assert.Equal(UpdateCheckStatus.Failed, r2.Status);
        Assert.Contains("500", r2.Message);
    }

    [Fact]
    public async Task TestCheck_NetworkFailureIsReportedNotThrown()
    {
        var handler = new StubHandler(HttpStatusCode.OK, toThrow: new HttpRequestException("名字解析失败"));
        using var checker = new UpdateChecker(new HttpClient(handler));

        var result = await checker.CheckAsync("example/wslc-panel", "1.0.0");

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains("无法连接 GitHub", result.Message);
    }

    [Fact]
    public async Task TestCheck_UnparsableTagIsReported()
    {
        const string json = """{ "tag_name": "nightly", "html_url": "https://example.com" }""";
        using var checker = new UpdateChecker(new HttpClient(new StubHandler(HttpStatusCode.OK, json)));

        var result = await checker.CheckAsync("example/wslc-panel", "1.0.0");

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains("nightly", result.Message);
    }
}
