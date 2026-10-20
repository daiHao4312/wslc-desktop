using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;
using WslcDesktop.Core.Services;

namespace WslcDesktop.Core.Tests;

/// <summary>
/// M16 镜像仓库（Registry V2）：输入归一化、地址校验、URL 拼装、响应解析、tag 排序、curl 客户端。
///
/// 断言里的数字全部来自**真机抓取的夹具**（`fixtures/registry_*.json`），
/// 不是编出来的：alpine 224 个 tag、`latest` 排在字母序第 224 位、
/// 日期 tag（`20190228`）挤在字母序前面、index 里 8 条是 `unknown/unknown` 的 attestation。
/// </summary>
public class RegistryTests
{
    private static string FixturePath(string name)
        => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static string Fixture(string name) => File.ReadAllText(FixturePath(name));

    // ---------------- 输入归一化 ----------------

    [Theory]
    [InlineData("alpine", "library/alpine", null)]
    [InlineData("NGINX", "library/nginx", null)]
    [InlineData("library/nginx", "library/nginx", null)]
    [InlineData("grafana/grafana", "grafana/grafana", null)]
    [InlineData("nginx:1.27", "library/nginx", "1.27")]
    [InlineData("  alpine  ", "library/alpine", null)]
    public void 短名补_library_且统一小写(string input, string expectedRepository, string? expectedTag)
    {
        Assert.True(RegistryInput.TrySplitInput(input, "docker.1panel.live", out var reference, out var error), error);

        Assert.Equal("docker.1panel.live", reference.Host);
        Assert.Equal(expectedRepository, reference.Repository);
        Assert.Equal(expectedTag, reference.Tag);
    }

    [Theory]
    // 浏览器里直接复制来的形状：协议头 + /v2/ + /tags/list 全要剥掉
    [InlineData("https://docker.1panel.live/v2/library/alpine/tags/list", "library/alpine", null)]
    [InlineData("docker.1panel.live/v2/library/alpine/tags/list", "library/alpine", null)]
    [InlineData("https://docker.1panel.live/v2/library/nginx/manifests/1.27", "library/nginx", "1.27")]
    [InlineData("docker.1panel.live/library/nginx:1.27", "library/nginx", "1.27")]
    public void 粘整条_URL_也认(string input, string expectedRepository, string? expectedTag)
    {
        Assert.True(RegistryInput.TrySplitInput(input, "docker.1panel.live", out var reference, out var error), error);

        Assert.Equal(expectedRepository, reference.Repository);
        Assert.Equal(expectedTag, reference.Tag);
    }

    [Fact]
    public void 用户写了别的_registry_就用它()
    {
        Assert.True(RegistryInput.TrySplitInput(
            "docker.1ms.run/library/alpine:3.20", MirrorPresets.DefaultRegistry, out var reference, out _));

        Assert.Equal("docker.1ms.run", reference.Host);
        Assert.Equal("library/alpine", reference.Repository);
        Assert.Equal("3.20", reference.Tag);
    }

    [Fact]
    public void 端口号不会被当成_tag()
    {
        // `registry:5000/x` 里的冒号在最后一段之前 —— 只有最后一段的冒号才是 tag
        Assert.True(RegistryInput.TrySplitInput(
            "registry.local:5000/team/app", MirrorPresets.DefaultRegistry, out var reference, out var error), error);

        Assert.Equal("registry.local:5000", reference.Host);
        Assert.Equal("team/app", reference.Repository);
        Assert.Null(reference.Tag);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("alpine@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("nginx:")]
    [InlineData("https://docker.1panel.live/v2/")]
    public void 不认的输入被拒且给出原因(string input)
    {
        Assert.False(RegistryInput.TrySplitInput(input, MirrorPresets.DefaultRegistry, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void 交给_wslc_pull_的引用必须带_registry_前缀()
    {
        Assert.True(RegistryInput.TrySplitInput("alpine:3.20", "docker.1panel.live", out var reference, out _));

        // 不带前缀会去 Docker Hub 找然后超时（本机实测 registry-1.docker.io 不可达）
        Assert.Equal("docker.1panel.live/library/alpine:3.20", reference.ToPullReference());
        Assert.Equal("docker.1panel.live/library/alpine:3.19", reference.ToPullReference("3.19"));
        // 用户没写 tag 时补 latest
        Assert.Equal(
            "docker.1panel.live/library/alpine:latest",
            new RegistryReference { Host = "docker.1panel.live", Repository = "library/alpine" }.ToPullReference());
    }

    // ---------------- 安全边界 ----------------

    [Theory]
    [InlineData("library/alpine", true)]
    [InlineData("Library/alpine", false)]                   // 仓库名必须小写（tag 才允许大写）
    [InlineData("my--repo", true)]                          // 连续分隔符允许（比 Docker 正则略宽）
    [InlineData("a//b", false)]                             // 空段
    [InlineData("/a", false)]
    [InlineData("a/", false)]
    [InlineData("a/../b", false)]                           // 路径穿越
    [InlineData("a%2Fb", false)]                            // URL 编码
    [InlineData("a?x=1", false)]
    [InlineData("-a", false)]
    [InlineData("a-", false)]
    [InlineData(".a", false)]
    [InlineData("", false)]
    public void 仓库名校验(string repository, bool expected)
    {
        Assert.Equal(expected, RegistryInput.IsValidRepository(repository));
    }

    [Theory]
    [InlineData("latest", true)]
    [InlineData("3.20-alpine", true)]
    [InlineData("V1.0", true)]                              // tag 可以有大写（与仓库名不同）
    [InlineData("_x", true)]
    [InlineData("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("sha256:short", false)]                      // 冒号出现在 tag 里不算摘要，落到 tag 规则又被字符集挡住
    [InlineData("-x", false)]
    [InlineData("", false)]
    public void tag_校验(string reference, bool expected)
    {
        Assert.Equal(expected, RegistryInput.IsValidReference(reference));
    }

    [Theory]
    [InlineData("docker.1panel.live", true)]
    [InlineData("registry.local:5000", true)]
    [InlineData("host:port", false)]        // 占位符要挡住，否则是个"永远超时"的地址
    [InlineData("host:70000", false)]       // 端口范围
    [InlineData("host:0", false)]
    [InlineData("http://x.com", false)]     // 要的是纯主机名
    [InlineData("a_b.com", false)]
    [InlineData(".a.com", false)]
    [InlineData("", false)]
    public void registry_主机名校验(string host, bool expected)
    {
        Assert.Equal(expected, RegistryInput.IsValidHost(host));
    }

    [Theory]
    [InlineData("https://Docker.1Panel.Live/", "docker.1panel.live")]
    [InlineData("  http://a.com  ", "a.com")]
    [InlineData("a.com/", "a.com")]
    [InlineData("A.COM", "a.com")]
    public void 主机名归一化(string input, string expected)
    {
        Assert.Equal(expected, RegistryInput.NormalizeHost(input));
    }

    // ---------------- URL 与 curl 参数 ----------------

    [Fact]
    public void URL_拼装()
    {
        Assert.Equal(
            "https://docker.1panel.live/v2/library/alpine/tags/list",
            RegistryInput.TagsUrl("docker.1panel.live", "library/alpine"));

        Assert.Equal(
            "https://docker.1panel.live/v2/library/alpine/manifests/3.20",
            RegistryInput.ManifestUrl("docker.1panel.live", "library/alpine", "3.20"));
    }

    [Fact]
    public void curl_参数带上失败也回正文的开关与_UA()
    {
        var arguments = RegistryInput.CurlJsonArguments("https://docker.1panel.live/v2/library/alpine/tags/list");

        // --fail-with-body 是关键：HTTP 出错时正文照样打出来，才能拿到 registry 的错误信封
        Assert.Contains("--fail-with-body", arguments);
        Assert.Contains("--max-time 20", arguments);
        Assert.Contains("Accept: application/vnd.docker.distribution.manifest.v2+json", arguments);
        Assert.Contains("Mozilla/5.0", arguments);
        Assert.Contains("https://docker.1panel.live/v2/library/alpine/tags/list", arguments);
    }

    // ---------------- tag 解析与排序 ----------------

    [Fact]
    public void 解析真机抓的_alpine_tag_列表()
    {
        Assert.True(RegistryResponseParser.TryParseTags(Fixture("registry_tags_alpine.json"), out var tags, out var error), error);

        Assert.Equal(224, tags.Count);
        Assert.Equal("2.6", tags[0]);          // 接口给的是字母序，这里保持原样
        Assert.Equal("latest", tags[^1]);      // 实测 latest 排在**最后一个**
    }

    [Fact]
    public void 排序后_latest_第一且版本倒序()
    {
        Assert.True(RegistryResponseParser.TryParseTags(Fixture("registry_tags_alpine.json"), out var tags, out _));

        var sorted = RegistryResponseParser.SortTags(tags);

        Assert.Equal(224, sorted.Count);
        Assert.Equal("latest", sorted[0]);
        Assert.True(sorted.IndexOf("3.24") < sorted.IndexOf("3.23.6"), "3.24 应排在 3.23.6 前");
        // 日期 tag（20190228 之类）不该挤进前几名 —— 实测排除它们之后会落到第 190 位附近
        Assert.True(sorted.IndexOf("20190228") > 100, "日期 tag 不该排在前面");
    }

    [Fact]
    public void 排序规则_同版本按_tag_升序()
    {
        var sorted = RegistryResponseParser.SortTags(new[]
        {
            "1.31.6-alpine", "latest", "edge", "1.31.6", "1.31.5", "3.24.0", "3.24"
        });

        Assert.Equal(new[] { "latest", "3.24.0", "3.24", "1.31.6", "1.31.6-alpine", "1.31.5", "edge" }, sorted);
    }

    [Fact]
    public void 版本号按数值比较而不是字典序()
    {
        // 字典序会把 "3.24.9" 排在 "3.24.11" 后面 —— 版本号必须按数值比
        var sorted = RegistryResponseParser.SortTags(new[] { "3.24.9", "3.24.11", "3.24.100" });

        Assert.Equal(new[] { "3.24.100", "3.24.11", "3.24.9" }, sorted);
    }

    [Theory]
    [InlineData("3.24.2", 3)]
    [InlineData("3.24.2-alpine", 3)]     // 只吃开头的版本段
    [InlineData("v1.0", 0)]              // 不以数字开头 → 不是版本号
    [InlineData("20190228", 0)]          // 日期 tag：段值 > 9999
    [InlineData("1001", 0)]              // 第一段 > 1000
    [InlineData("1.2.3.4.5", 0)]         // 段数 > 4
    [InlineData("edge", 0)]
    public void 版本键识别(string tag, int expectedLength)
    {
        var key = RegistryResponseParser.VersionKey(tag);
        Assert.Equal(expectedLength, key?.Count ?? 0);
    }

    [Fact]
    public void tags_为_null_算错误而不是空列表()
    {
        // 界面上一片空白，用户分不清是"没 tag"还是"我们解错了"
        Assert.False(RegistryResponseParser.TryParseTags("""{"name":"library/x","tags":null}""", out _, out var error));
        Assert.Contains("没有任何 tag", error);
    }

    [Fact]
    public void 空正文与坏_JSON_给出不同原因()
    {
        Assert.False(RegistryResponseParser.TryParseTags("  ", out _, out var empty));
        Assert.Contains("空内容", empty);

        Assert.False(RegistryResponseParser.TryParseTags("not json", out _, out var bad));
        Assert.Contains("不是 JSON", bad);
    }

    // ---------------- 错误信封 ----------------

    [Fact]
    public void 错误信封被翻成人话()
    {
        Assert.False(RegistryResponseParser.TryParseTags(
            Fixture("registry_error_name_unknown.json"), out _, out var error));

        Assert.Equal("这个 registry 上没有仓库 library/no-such-image-xyz", error);
    }

    [Fact]
    public void 被拒时提示可能是私有仓库()
    {
        Assert.False(RegistryResponseParser.TryParseTags(
            Fixture("registry_error_denied.json"), out _, out var error));

        Assert.Contains("私有", error);
    }

    [Fact]
    public void 失败说明优先用错误信封_其次才是_curl_的话()
    {
        // 信封最具体（实测 404 / 403 都带），curl 的话只是兜底
        var withEnvelope = RegistryResponseParser.ExplainFailure(
            Fixture("registry_error_name_unknown.json"),
            "curl: (22) The requested URL returned error: 404");

        Assert.Contains("没有仓库", withEnvelope);

        var withoutEnvelope = RegistryResponseParser.ExplainFailure(
            string.Empty,
            "curl: (6) Could not resolve host: nope.invalid");

        Assert.Contains("连不上 registry", withoutEnvelope);
        Assert.Contains("Could not resolve host", withoutEnvelope);
    }

    [Fact]
    public void 完全没响应时给可读兜底()
    {
        var message = RegistryResponseParser.ExplainFailure(string.Empty, string.Empty);

        Assert.Contains("没有响应", message);
    }

    // ---------------- manifest ----------------

    [Fact]
    public void 从多架构_index_里挑出本机架构那一份()
    {
        var json = Fixture("registry_index_alpine.json");

        Assert.True(RegistryResponseParser.TryPickPlatformManifest(json, "amd64", out var amd64, out _));
        Assert.StartsWith("sha256:d56c381f", amd64);

        Assert.True(RegistryResponseParser.TryPickPlatformManifest(json, "arm64", out var arm64, out _));
        Assert.StartsWith("sha256:260479a1", arm64);

        // 同时看 os：8 条 unknown/unknown 的 attestation 一条都不能选中
        Assert.False(RegistryResponseParser.TryPickPlatformManifest(json, "s390x-wrong", out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void 单架构_manifest_里挑不出平台_应该失败()
    {
        // 客户端靠这一点区分"index"与"已经是单架构"：挑不出来就按单架构直接算体积
        Assert.False(RegistryResponseParser.TryPickPlatformManifest(
            Fixture("registry_manifest_alpine_amd64.json"), "amd64", out _, out _));
    }

    [Fact]
    public void 累加各层大小()
    {
        Assert.True(RegistryResponseParser.TrySumDownloadBytes(
            Fixture("registry_manifest_alpine_amd64.json"), out var bytes, out var error), error);

        Assert.True(bytes > 0);
        // 夹具里 config 611 字节 + 一个层；量出来的数应该就是这几项之和
        Assert.True(bytes < 10 * 1024 * 1024, $"alpine 不该超过 10 MB，实际 {bytes}");
    }

    [Fact]
    public void 没有_layers_时给明确原因()
    {
        Assert.False(RegistryResponseParser.TrySumDownloadBytes("""{"config":{"size":1}}""", out _, out var error));
        Assert.Contains("layers", error);
    }

    // ---------------- curl 客户端 ----------------

    [Fact]
    public async Task 列_tag_会去问_registry_并返回排好序的结果()
    {
        var runner = new RecordingProcessRunner
        {
            Handler = (_, args) => args.Contains("tags/list", StringComparison.Ordinal)
                ? new ProcessExecutionResult(0, Fixture("registry_tags_alpine.json"), string.Empty, true)
                : new ProcessExecutionResult(0, string.Empty, string.Empty, true)
        };
        var client = new CurlRegistryClient(runner);

        var result = await client.GetTagsAsync("docker.1panel.live", "library/alpine");

        Assert.True(result.Success, result.Error);
        Assert.Equal("latest", result.Tags[0]);
        Assert.Contains("curl.exe", Assert.Single(runner.Calls));
        Assert.Contains("https://docker.1panel.live/v2/library/alpine/tags/list", runner.Calls[0]);
    }

    [Fact]
    public async Task 地址或仓库名不合法时根本不调_curl()
    {
        var runner = new RecordingProcessRunner();
        var client = new CurlRegistryClient(runner);

        Assert.False((await client.GetTagsAsync("host:port", "library/alpine")).Success);
        Assert.False((await client.GetTagsAsync("docker.1panel.live", "a/../b")).Success);

        // 这是安全边界：URL 会作为裸 argv 交给 curl.exe
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task 查询失败时把错误信封翻成中文()
    {
        var runner = new RecordingProcessRunner
        {
            Handler = (_, _) => new ProcessExecutionResult(
                22, Fixture("registry_error_name_unknown.json"), "curl: (22) … 404", false)
        };
        var client = new CurlRegistryClient(runner);

        var result = await client.GetTagsAsync("docker.1panel.live", "library/no-such-image-xyz");

        Assert.False(result.Success);
        Assert.Contains("没有仓库 library/no-such-image-xyz", result.Error);
    }

    [Fact]
    public async Task 量体积要两次请求_index_再要挑中的那一份()
    {
        var calls = new List<string>();
        var runner = new RecordingProcessRunner
        {
            Handler = (_, args) =>
            {
                calls.Add(args);
                if (args.Contains("manifests/latest", StringComparison.Ordinal))
                {
                    return new ProcessExecutionResult(0, Fixture("registry_index_alpine.json"), string.Empty, true);
                }

                return new ProcessExecutionResult(0, Fixture("registry_manifest_alpine_amd64.json"), string.Empty, true);
            }
        };
        var client = new CurlRegistryClient(runner);

        var result = await client.GetSizeAsync("docker.1panel.live", "library/alpine", "latest", "amd64");

        Assert.True(result.Success, result.Error);
        Assert.True(result.Bytes > 0);

        // 内容协商省不掉那次：实测 manifests/latest 无视 Accept，永远先回 index
        Assert.Equal(2, calls.Count);
        Assert.Contains("manifests/latest", calls[0]);
        Assert.Contains("manifests/sha256:", calls[1]);
    }

    [Fact]
    public async Task 已经是单架构_manifest_时只请求一次()
    {
        var runner = new RecordingProcessRunner
        {
            Handler = (_, _) => new ProcessExecutionResult(
                0, Fixture("registry_manifest_alpine_amd64.json"), string.Empty, true)
        };
        var client = new CurlRegistryClient(runner);

        var result = await client.GetSizeAsync("docker.1panel.live", "library/alpine", "3.20", "amd64");

        Assert.True(result.Success, result.Error);
        Assert.Single(runner.Calls);
    }

    // ---------------- 预设与偏好 ----------------

    [Fact]
    public void 默认_registry_是实测免认证的那个站()
    {
        Assert.Equal("docker.1panel.live", RegistryDefaults.Host);
        Assert.Equal(RegistryDefaults.Host, MirrorPresets.DefaultRegistry);

        // 偏好项的默认值必须同一个来源 —— 否则"设置里显示 docker.io、实际用别的"这种漂移会重现
        Assert.Equal(RegistryDefaults.Host, new AppPreferences().DefaultRegistry);
    }

    [Fact]
    public void 加速站表标出了能不能列_tag()
    {
        var onePanel = MirrorPresets.Sites.Single(s => s.Host == "docker.1panel.live");
        Assert.True(onePanel.CanListTags);

        // 实测这两站列 tag 要认证令牌，界面上不能承诺能列出来
        Assert.False(MirrorPresets.Sites.Single(s => s.Host == "docker.1ms.run").CanListTags);
        Assert.False(MirrorPresets.Sites.Single(s => s.Host == "docker.m.daocloud.io").CanListTags);
    }

    [Fact]
    public void 常用镜像快捷入口不超过一屏()
    {
        // 铺满一屏的按钮墙已经被否过一次（那次的结论是改用下拉框）
        Assert.InRange(MirrorPresets.CommonRepositories.Count, 1, 12);
        Assert.Contains("nginx", MirrorPresets.CommonRepositories);
        Assert.Contains("alpine", MirrorPresets.CommonRepositories);
    }

    [Fact]
    public void 官方源在预设表里且可以列_tag()
    {
        var hub = MirrorPresets.Sites.Single(s => s.Host == RegistryDefaults.DockerHubFriendlyHost);

        // 用户开了代理就能用官方源；公开镜像靠匿名令牌即可列 tag
        Assert.True(hub.CanListTags);
        Assert.Contains("代理", hub.Note);

        // 下拉与展示都用用户认知里的名字，而不是内部 API 端点
        Assert.Contains("docker.io", MirrorPresets.Hosts);
    }

    [Fact]
    public void 下拉候选必须是纯主机名_不能掺展示文案()
    {
        // 回归：下拉项的字符串**就是取值**。曾经把候选写成 "docker.io（可列 tag）"，
        // 于是 IsValidHost 因全角括号失败 → 界面一路回落到默认源，
        // 症状是"镜像源选哪个都变成 docker.1panel.live"。
        Assert.NotEmpty(MirrorPresets.Hosts);
        Assert.Equal(MirrorPresets.Sites.Count, MirrorPresets.Hosts.Count);

        foreach (var host in MirrorPresets.Hosts)
        {
            Assert.True(RegistryInput.IsValidHost(host), $"候选不是合法主机名：{host}");
            Assert.DoesNotContain('（', host);
            Assert.DoesNotContain(' ', host);
        }
    }

    [Fact]
    public void 同一_registry_的两种写法视为同一个查询目标()
    {
        // 界面用 (ApiHost, 仓库) 当"tag 列表属于谁"的键 —— 这两个写法问的是同一个 registry，
        // 切换它们不该被当成"换了源"而白白清空一次已列出的 tag
        Assert.True(RegistryInput.TrySplitInput("nginx", "docker.io", out var friendly, out _));
        Assert.True(RegistryInput.TrySplitInput("nginx", "registry-1.docker.io", out var api, out _));

        Assert.NotEqual(friendly.Host, api.Host);      // 展示上确实不同
        Assert.Equal(friendly.ApiHost, api.ApiHost);   // 但查询目标是同一个
    }

    [Fact]
    public void 每个候选主机名都能反查回预设项()
    {
        // 下拉里选中的值必须能被 Find 认出来，否则提示行会误报"不在已知表里"
        foreach (var host in MirrorPresets.Hosts)
        {
            Assert.NotNull(MirrorPresets.Find(host));
        }
    }

    [Fact]
    public void 官方源两种写法都能命中同一条预设()
    {
        // Find 走 canonical 比较，用户填哪个都能拿到正确的备注
        Assert.NotNull(MirrorPresets.Find("docker.io"));
        Assert.NotNull(MirrorPresets.Find("registry-1.docker.io"));
        Assert.NotNull(MirrorPresets.Find("index.docker.io"));
        Assert.Equal(
            MirrorPresets.Find("docker.io")!.Value.Note,
            MirrorPresets.Find("registry-1.docker.io")!.Value.Note);
    }

    // ---------------- Docker Hub 别名与匿名令牌 ----------------

    [Theory]
    [InlineData("docker.io", "registry-1.docker.io")]
    [InlineData("index.docker.io", "registry-1.docker.io")]
    [InlineData("registry-1.docker.io", "registry-1.docker.io")]
    [InlineData("https://docker.io/", "registry-1.docker.io")]
    [InlineData("DOCKER.IO", "registry-1.docker.io")]
    [InlineData("docker.1panel.live", "docker.1panel.live")]
    public void docker_hub_的别名统一成真正的_API_端点(string input, string expected)
    {
        // 不统一的话，用户按习惯填 docker.io 时 /v2/ 会打到网站而不是 registry
        Assert.Equal(expected, RegistryInput.CanonicalHost(input));
    }

    [Fact]
    public void 别名统一后_url_也走_API_端点()
    {
        Assert.Equal(
            "https://registry-1.docker.io/v2/library/alpine/tags/list",
            RegistryInput.TagsUrl("docker.io", "library/alpine"));
    }

    [Fact]
    public void 用户填_docker_io_时对外保持原样_只在发请求时转成_API_端点()
    {
        Assert.True(RegistryInput.TrySplitInput("docker.io/library/nginx:1.27", MirrorPresets.DefaultRegistry, out var reference, out var error), error);

        // 展示与拉取引用都保持用户认知里的 docker.io —— 界面上突然变成 registry-1
        // 会让人以为选错了（这正是用户反馈过的问题）
        Assert.Equal("docker.io", reference.Host);
        Assert.Equal("library/nginx", reference.Repository);
        Assert.Equal("1.27", reference.Tag);
        Assert.Equal("docker.io/library/nginx:1.27", reference.ToPullReference());

        // 只有真正发 HTTP 时才换成 API 端点
        Assert.Equal("registry-1.docker.io", reference.ApiHost);
        Assert.Equal(
            "https://registry-1.docker.io/v2/library/nginx/tags/list",
            RegistryInput.TagsUrl(reference.Host, reference.Repository));
    }

    [Fact]
    public void 只有已知站点才有匿名令牌端点()
    {
        Assert.NotNull(RegistryDefaults.AnonymousTokenEndpointFor("registry-1.docker.io"));
        Assert.NotNull(RegistryDefaults.AnonymousTokenEndpointFor("docker.io"));

        // 端点是固定形状的 Docker Hub 专用写法；其它站需要通用挑战发现（本版没做）
        Assert.Null(RegistryDefaults.AnonymousTokenEndpointFor("docker.1panel.live"));
        Assert.Null(RegistryDefaults.AnonymousTokenEndpointFor("docker.1ms.run"));
    }

    [Fact]
    public void 解析令牌响应时两个字段名都要认()
    {
        // 参考项目实测字段名可能是 access_token 而不是 token；只认一个会得到
        // "拿到了正文却没有令牌"这种最难查的失败
        Assert.True(RegistryResponseParser.TryParseAuthToken("""{"token":"abc"}""", out var a, out _));
        Assert.Equal("abc", a);

        Assert.True(RegistryResponseParser.TryParseAuthToken("""{"access_token":"xyz"}""", out var b, out _));
        Assert.Equal("xyz", b);

        Assert.False(RegistryResponseParser.TryParseAuthToken("""{"expires_in":300}""", out _, out var error));
        Assert.Contains("没有 token", error);

        Assert.False(RegistryResponseParser.TryParseAuthToken(
            Fixture("registry_error_denied.json"), out _, out var denied));
        Assert.Contains("私有", denied);
    }

    [Fact]
    public void curl_参数带上_Bearer_头()
    {
        var withToken = RegistryInput.CurlJsonArguments("https://x/v2/", bearerToken: "abc123");

        Assert.Contains("Authorization: Bearer abc123", withToken);
        Assert.Contains("--fail-with-body", withToken);

        // 不带令牌时不该出现这个头
        Assert.DoesNotContain("Authorization", RegistryInput.CurlJsonArguments("https://x/v2/"));
    }

    [Fact]
    public async Task 官方源先换匿名令牌再带令牌取_tag()
    {
        var calls = new List<string>();
        var runner = new RecordingProcessRunner
        {
            Handler = (_, args) =>
            {
                calls.Add(args);
                return args.Contains("auth.docker.io/token", StringComparison.Ordinal)
                    ? new ProcessExecutionResult(0, """{"token":"tok-123","expires_in":300}""", string.Empty, true)
                    : new ProcessExecutionResult(0, Fixture("registry_tags_alpine.json"), string.Empty, true);
            }
        };
        var client = new CurlRegistryClient(runner);

        var result = await client.GetTagsAsync(RegistryDefaults.DockerHubHost, "library/alpine");

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, calls.Count);

        // 第一次换令牌（端点是固定形状的 Docker Hub 写法）
        Assert.Contains("auth.docker.io/token?service=registry.docker.io&scope=repository:library/alpine:pull", calls[0]);
        // 第二次带令牌问 tag，且打的是真正的 API 端点
        Assert.Contains("Authorization: Bearer tok-123", calls[1]);
        Assert.Contains("https://registry-1.docker.io/v2/library/alpine/tags/list", calls[1]);
    }

    [Fact]
    public async Task 用户填_docker_io_时也走令牌流程()
    {
        var calls = new List<string>();
        var runner = new RecordingProcessRunner
        {
            Handler = (_, args) =>
            {
                calls.Add(args);
                return args.Contains("auth.docker.io", StringComparison.Ordinal)
                    ? new ProcessExecutionResult(0, """{"access_token":"tok-9"}""", string.Empty, true)
                    : new ProcessExecutionResult(0, Fixture("registry_tags_alpine.json"), string.Empty, true);
            }
        };
        var client = new CurlRegistryClient(runner);

        Assert.True((await client.GetTagsAsync("docker.io", "library/alpine")).Success);
        Assert.Contains("https://registry-1.docker.io/v2/", calls[1]);
    }

    [Fact]
    public async Task 免认证站不会去换令牌()
    {
        var runner = new RecordingProcessRunner
        {
            Handler = (_, _) => new ProcessExecutionResult(0, Fixture("registry_tags_alpine.json"), string.Empty, true)
        };
        var client = new CurlRegistryClient(runner);

        Assert.True((await client.GetTagsAsync("docker.1panel.live", "library/alpine")).Success);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task 换令牌失败时说明是令牌的问题()
    {
        var runner = new RecordingProcessRunner
        {
            Handler = (_, args) => args.Contains("auth.docker.io", StringComparison.Ordinal)
                ? new ProcessExecutionResult(22, string.Empty, "curl: (6) Could not resolve host", false)
                : new ProcessExecutionResult(0, string.Empty, string.Empty, true)
        };
        var client = new CurlRegistryClient(runner);

        var result = await client.GetTagsAsync(RegistryDefaults.DockerHubHost, "library/alpine");

        Assert.False(result.Success);
        Assert.Contains("匿名令牌", result.Error);
    }

    [Fact]
    public async Task 给了私有仓库令牌就不再换匿名令牌()
    {
        var runner = new RecordingProcessRunner
        {
            Handler = (_, _) => new ProcessExecutionResult(0, Fixture("registry_tags_alpine.json"), string.Empty, true)
        };
        var client = new CurlRegistryClient(runner);

        var result = await client.GetTagsAsync(
            RegistryDefaults.DockerHubHost,
            "library/alpine",
            bearerToken: "private-token");

        Assert.True(result.Success, result.Error);

        // 私有仓库换不到匿名令牌 —— 多跑一次只会把真正的失败原因盖掉
        Assert.Single(runner.Calls);
        Assert.Contains("Authorization: Bearer private-token", runner.Calls[0]);
    }

    [Fact]
    public async Task 量体积也带私有仓库令牌()
    {
        var runner = new RecordingProcessRunner
        {
            Handler = (_, _) => new ProcessExecutionResult(0, Fixture("registry_manifest_alpine_amd64.json"), string.Empty, true)
        };
        var client = new CurlRegistryClient(runner);

        Assert.True((await client.GetSizeAsync("myregistry.local", "team/app", "1.0", "amd64", "private-token")).Success);
        Assert.Contains("Authorization: Bearer private-token", Assert.Single(runner.Calls));
    }

    // ---------------- 重命名用引用校验 ----------------

    [Theory]
    [InlineData("my-alpine:3.20", true)]
    [InlineData("alpine", true)]
    [InlineData("team/app:1.0", true)]
    [InlineData("registry-1.docker.io/library/nginx:1.27", true)]
    [InlineData("localhost:5000/app", true)]
    [InlineData("MyImage:1.0", false)]          // 仓库名必须小写
    [InlineData("a b:1.0", false)]
    [InlineData("a@sha256:abc", false)]
    [InlineData("a%2Fb", false)]
    [InlineData("a:1.0:2.0", false)]            // 第二个冒号落在 tag 里
    [InlineData(":1.0", false)]
    [InlineData("", false)]
    public void 重命名引用校验_不补_library(string input, bool expected)
    {
        // 与 TrySplitInput 的关键区别：**不补 library/**。
        // 重命名目标常是 my-alpine 这种本地短名，补了会变成另一个名字。
        Assert.Equal(expected, RegistryInput.TryValidateTagReference(input, out _));
    }

    [Fact]
    public void 重命名引用不补_library_前缀()
    {
        Assert.True(RegistryInput.TryValidateTagReference("my-alpine:3.20", out _));

        // 对照：同一个输入走 TrySplitInput 会被补成 library/my-alpine（那是拉取路径的规矩）
        Assert.True(RegistryInput.TrySplitInput("my-alpine:3.20", MirrorPresets.DefaultRegistry, out var reference, out _));
        Assert.Equal("library/my-alpine", reference.Repository);
    }

    // ---------------- 打标签命令 ----------------

    [Fact]
    public async Task 打标签走_wslc_tag()
    {
        var runner = new RecordingProcessRunner();
        var client = new CliWslcClient(runner);

        Assert.True(await client.TagImageAsync("alpine:latest", "my-alpine:3.20"));

        Assert.Equal("wslc.exe tag alpine:latest my-alpine:3.20", Assert.Single(runner.Calls));
    }

    [Theory]
    [InlineData("", "x:1")]
    [InlineData("x:1", "")]
    [InlineData("   ", "x:1")]
    public async Task 打标签参数不全时不调引擎(string source, string target)
    {
        var runner = new RecordingProcessRunner();
        var client = new CliWslcClient(runner);

        Assert.False(await client.TagImageAsync(source, target));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task 含空格的名字会被引用起来()
    {
        var runner = new RecordingProcessRunner();
        var client = new CliWslcClient(runner);

        await client.TagImageAsync("a b:1", "c d:2");

        Assert.Equal("wslc.exe tag \"a b:1\" \"c d:2\"", Assert.Single(runner.Calls));
    }

    // ---------------- 自定义镜像源（M16c）----------------

    [Fact]
    public void 预设站点认得出来_自定义源认不出来()
    {
        Assert.True(MirrorPresets.IsPreset("docker.io"));
        Assert.True(MirrorPresets.IsPreset("registry-1.docker.io")); // 别名也算预设
        Assert.False(MirrorPresets.IsPreset("git.example.com"));
    }

    }
