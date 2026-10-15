using WSLC.Panel.Core.Models;
using WSLC.Panel.Core.Parsing;

namespace WSLC.Panel.Core.Tests;

/// <summary>
/// 事件流解析测试。夹具 <c>events_real.jsonl</c> / <c>events_table_real.txt</c> 为真机抓取
/// （wslc 3.0.3.0，同一次 720h 窗口，两种格式各 24 条，可交叉比对）。
/// </summary>
public class WslcEventParserTests
{
    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    // ---------- JSON 主通道 ----------

    [Fact]
    public void TestParseJson_FromRealOutput()
    {
        var events = WslcEventParser.Parse(Fixture("events_real.jsonl"));

        Assert.Equal(24, events.Count);

        // 实测分布：container 15 / network 9
        Assert.Equal(15, events.Count(e => e.Type == "container"));
        Assert.Equal(9, events.Count(e => e.Type == "network"));

        // 每条都必须有类别、动作、Actor ID 与可用时间
        Assert.All(events, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Type));
            Assert.False(string.IsNullOrWhiteSpace(e.Action));
            Assert.Equal(64, e.ActorId.Length);
            Assert.True(e.TimeNano > 0);
        });
    }

    [Fact]
    public void TestParseJson_ActionDistributionMatchesRealCapture()
    {
        var events = WslcEventParser.Parse(Fixture("events_real.jsonl"));

        var byAction = events.GroupBy(e => e.Action).ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(3, byAction["create"]);
        Assert.Equal(5, byAction["start"]);
        Assert.Equal(4, byAction["die"]);
        Assert.Equal(2, byAction["destroy"]);
        Assert.Equal(1, byAction["kill"]);
        Assert.Equal(5, byAction["connect"]);
        Assert.Equal(4, byAction["disconnect"]);
    }

    [Fact]
    public void TestParseJson_TimeIsUnixEpochConvertedToLocal()
    {
        var events = WslcEventParser.Parse(Fixture("events_real.jsonl"));
        var first = events[0];

        // 1591962211 → 2026-10-14T07:16:51.131Z，与 table 首行 "…15:16:51.131552884+08:00" 指向同一瞬间。
        // 断言 UTC 值，避免测试依赖运行机器的时区。
        var expectedUtc = new DateTime(2026, 10, 14, 7, 16, 51, DateTimeKind.Utc).AddTicks(1_310_000);
        var utc = first.Time.ToUniversalTime();
        Assert.Equal(expectedUtc, utc.UtcDateTime);

        // 毫秒级精度来自 timeNano，不能退化成整秒
        Assert.Equal(131, utc.Millisecond);
    }

    [Fact]
    public void TestParseJson_ContainerCreateCarriesNameAndImage()
    {
        var events = WslcEventParser.Parse(Fixture("events_real.jsonl"));

        var create = events.First(e => e.Type == "container" && e.Action == "create");

        Assert.Equal("mysql", create.ActorName);
        Assert.Equal("docker.1panel.live/library/mysql:latest", create.Image);
        Assert.Equal("local", create.Scope);
        Assert.Equal("创建容器 mysql（镜像 docker.1panel.live/library/mysql:latest）", create.Summary);
        Assert.Equal("container.create", create.FilterKey);
        Assert.Equal("mysql", create.DisplayName);
    }

    [Fact]
    public void TestParseJson_KillEventExposesSignal()
    {
        var events = WslcEventParser.Parse(Fixture("events_real.jsonl"));

        var kill = events.First(e => e.Action == "kill");

        Assert.Equal("9", kill.Signal);
        Assert.Equal("强制终止容器 mysql（信号 9）", kill.Summary);
        Assert.Equal("caution", kill.Severity);
    }

    [Fact]
    public void TestParseJson_NonZeroExitCodeIsCritical()
    {
        var events = WslcEventParser.Parse(Fixture("events_real.jsonl"));

        var abnormal = events.First(e => e.Action == "die" && e.ExitCode is int c && c != 0);

        Assert.Equal(1, abnormal.ExitCode);
        Assert.Equal("critical", abnormal.Severity);
        Assert.Contains("异常退出（退出码 1）", abnormal.Summary);

        // 退出码 137 那条（强杀后）同样算异常
        Assert.Contains(events, e => e.Action == "die" && e.ExitCode == 137);
    }

    [Fact]
    public void TestParseJson_NetworkEventPutsContainerInAttributes()
    {
        var events = WslcEventParser.Parse(Fixture("events_real.jsonl"));

        var connect = events.First(e => e.Type == "network" && e.Action == "connect");

        // 网络事件的 ActorName 是网络名（bridge），容器 ID 藏在 Attributes.container 里
        Assert.Equal("bridge", connect.ActorName);
        Assert.Equal(64, connect.RelatedContainerId.Length);
        Assert.Equal("bridge", connect.Attributes["name"]);
        Assert.Equal("bridge", connect.Attributes["type"]);

        Assert.StartsWith("容器 ", connect.Summary);
        Assert.EndsWith(" 接入网络 bridge", connect.Summary);
    }

    [Fact]
    public void TestParseJson_EmptyInputYieldsEmptyList()
    {
        Assert.Empty(WslcEventParser.Parse(null));
        Assert.Empty(WslcEventParser.Parse(""));
        Assert.Empty(WslcEventParser.Parse("   \n  \n"));
    }

    // ---------- table 兜底通道 ----------

    [Fact]
    public void TestParseTable_FallbackProducesSameDistribution()
    {
        var tableEvents = WslcEventParser.Parse(Fixture("events_table_real.txt"));

        Assert.Equal(24, tableEvents.Count);
        Assert.Equal(15, tableEvents.Count(e => e.Type == "container"));
        Assert.Equal(9, tableEvents.Count(e => e.Type == "network"));
        Assert.Equal(5, tableEvents.Count(e => e.Action == "start"));
        Assert.Equal(4, tableEvents.Count(e => e.Action == "disconnect"));
    }

    [Fact]
    public void TestParseTable_TruncatesNanosecondFractionInsteadOfDroppingLine()
    {
        // 引擎给 9 位小数（131552884），.NET 只认 7 位 —— 不截断的话整行会被丢掉
        var evt = WslcEventParser.Parse(
            "2026-10-14T15:16:51.131552884+08:00 container create " +
            "34ae5fc1241be5aa43a62d8c53a892e24880c5a282472cd48cafea4a7d05dede (image=alpine:latest, name=mysql)");

        var single = Assert.Single(evt);
        Assert.Equal("container", single.Type);
        Assert.Equal("create", single.Action);
        Assert.Equal("mysql", single.ActorName);
        Assert.Equal("alpine:latest", single.Image);

        // 9 位小数被截到 7 位：131552884 → 1315528 tick（100ns 粒度），不是丢精度成整秒
        Assert.Equal(
            new DateTime(2026, 10, 14, 7, 16, 51, DateTimeKind.Utc).AddTicks(1_315_528),
            single.Time.ToUniversalTime().UtcDateTime);

        // table 通道没有 timeNano，时间精度只到毫秒
        Assert.Equal(0, single.TimeNano);
    }

    // ---------- 容错 ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("版权所有 (c) Microsoft Corporation。保留所有权利。")]
    [InlineData("有关此产品的隐私信息，请访问 https://aka.ms/privacy")]
    [InlineData("如果此错误是意外错误，请考虑搜索现有问题")]
    [InlineData("{ 这不是合法 JSON")]
    [InlineData("既不是时间戳也不是 JSON 的一句话")]
    public void TestTryParseLine_RejectsNoise(string? line)
    {
        Assert.False(WslcEventParser.TryParseLine(line, out _));
    }

    [Fact]
    public void TestTryParseLine_AcceptsMinimalJson()
    {
        Assert.True(WslcEventParser.TryParseLine(
            """{"Action":"start","Type":"container","Actor":{"ID":"abc"},"time":1791962211}""",
            out var evt));

        Assert.Equal("container", evt.Type);
        Assert.Equal("start", evt.Action);
        Assert.Equal("abc", evt.ActorId);
    }

    [Fact]
    public void TestTryParseLine_FallsBackToStatusWhenActionMissing()
    {
        Assert.True(WslcEventParser.TryParseLine(
            """{"status":"die","Type":"container","id":"abc","time":1791962211,"Actor":{"Attributes":{"exitCode":"0"}}}""",
            out var evt));

        Assert.Equal("die", evt.Action);
        Assert.Equal(0, evt.ExitCode);
        // 退出码 0 = 正常退出，不该报 critical
        Assert.Equal("caution", evt.Severity);
        Assert.Equal("容器 abc 正常退出", evt.Summary);
    }

    // ---------- 续接与去重 ----------

    [Fact]
    public void TestFormatSinceArgument_IsUtcRfc3339WithZAndNoWhitespace()
    {
        // 必须转 UTC 并用 Z 形式：引擎明确拒绝 "+0800"（无冒号）这种偏移写法
        var arg = WslcEventParser.FormatSinceArgument(
            new DateTimeOffset(2026, 10, 14, 15, 16, 51, 131, TimeSpan.FromHours(8)));

        Assert.Equal("2026-10-14T07:16:51.131Z", arg);
        Assert.DoesNotContain(' ', arg);
        Assert.DoesNotContain('+', arg);
        Assert.EndsWith("Z", arg);
    }

    [Fact]
    public void TestDedupKey_StableForSameEventAndDiffersAcrossEvents()
    {
        var events = WslcEventParser.Parse(Fixture("events_real.jsonl"));

        // 同一个事件对象重复求值必须稳定（续接补拉时用来压掉重复投递）
        var again = events[0] with { Time = events[0].Time };
        Assert.Equal(events[0].DedupKey, again.DedupKey);

        // 24 条真实事件两两不同，去重键不能撞车
        Assert.Equal(events.Count, events.Select(e => e.DedupKey).Distinct().Count());
    }

    [Fact]
    public void TestSeverity_AndSummary_AcrossActionFamilies()
    {
        WslcEvent Make(string type, string action, params (string Key, string Value)[] attrs)
            => new()
            {
                Time = DateTimeOffset.Now,
                Type = type,
                Action = action,
                ActorId = new string('a', 64),
                ActorName = "demo",
                Attributes = attrs.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase)
            };

        Assert.Equal("success", Make("container", "start").Severity);
        Assert.Equal("success", Make("container", "create").Severity);
        Assert.Equal("caution", Make("container", "stop").Severity);
        Assert.Equal("caution", Make("container", "destroy").Severity);
        Assert.Equal("critical", Make("container", "oom").Severity);
        Assert.Equal("neutral", Make("container", "rename").Severity);

        Assert.Equal("启动容器 demo", Make("container", "start").Summary);
        Assert.Equal("容器 demo 已退出", Make("container", "die").Summary);
        Assert.Equal("暂停容器 demo", Make("container", "pause").Summary);

        // 网络事件：非 connect/disconnect 动作走默认分支；connect 要把容器 ID 带进摘要
        Assert.Equal("启动网络 demo", Make("network", "start").Summary);
        Assert.Equal(
            "容器 bbbbbbbbbbbb 接入网络 demo",
            Make("network", "connect", ("container", new string('b', 64))).Summary);

        Assert.Equal("容器", Make("container", "start").TypeDisplay);
        Assert.Equal("数据卷", Make("volume", "create").TypeDisplay);
        Assert.Equal("引擎", Make("daemon", "reload").TypeDisplay);
    }

    [Fact]
    public void TestActorShortId_TruncatesLongIdAndKeepsShortOne()
    {
        Assert.Equal(new string('3', 12), new WslcEvent { ActorId = new string('3', 64) }.ActorShortId);
        Assert.Equal("abc", new WslcEvent { ActorId = "abc" }.ActorShortId);
        Assert.Equal(string.Empty, new WslcEvent().ActorShortId);

        // 与真机事件对齐：64 位 ID 截成 12 位
        var real = WslcEventParser.Parse(Fixture("events_real.jsonl"))[0];
        Assert.Equal(real.ActorId[..12], real.ActorShortId);
        Assert.Equal(12, real.ActorShortId.Length);
    }
}
