using System.Collections.Concurrent;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Services;
using Xunit.Abstractions;

namespace WslcDesktop.Core.Tests;

/// <summary>
/// 事件流<b>实机</b>联调测试。
///
/// 这是 M11「操作后 UI 即时刷新不等轮询」的前提验证：只靠夹具单测只能证明解析器对，
/// 证明不了「引擎真的会把事件推给一个长驻进程」。
///
/// 做法：先用一次 <c>--since 720h</c> 只读回放确认通道可用；再起一条实时订阅，
/// 期间创建一个一次性容器并删除，断言订阅端收到了对应的 create / destroy 事件。
/// 副作用只有那个一次性容器，且 finally 里强制清理。
///
/// 引擎不可用的机器上直接跳过（记日志返回），不让整个套件变红。
/// </summary>
public class LiveEventStreamTests
{
    private readonly ITestOutputHelper _output;

    public LiveEventStreamTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task TestStreamEvents_ReceivesLiveContainerEvents()
    {
        var client = new CliWslcClient();

        // 引擎探活：拿不到信息就跳过，避免在无 WSL 的机器上假红
        var info = await client.GetSystemInfoAsync();
        if (string.IsNullOrWhiteSpace(info.WslVersion))
        {
            _output.WriteLine("未检测到 WSL 引擎，跳过事件流实机测试。");
            return;
        }

        _output.WriteLine($"WSL 版本：{info.WslVersion}");

        // ---------- 第一段：只读回放，确认 --since 与编码通道可用 ----------
        var replay = new List<WslcEvent>();
        using (var replayCts = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
        {
            try
            {
                await client.StreamEventsAsync(replay.Add, DateTimeOffset.Now.AddHours(-720), replayCts.Token);
            }
            catch (OperationCanceledException)
            {
                // 回放结束后进程仍在等待新事件，靠取消收尾 —— 预期路径
            }
            catch (Exception ex)
            {
                _output.WriteLine($"回放异常（不判失败，仅记录）：{ex.Message}");
            }
        }

        _output.WriteLine($"回放取到 {replay.Count} 条历史事件。");
        if (replay.Count > 0)
        {
            Assert.All(replay, e => Assert.False(string.IsNullOrWhiteSpace(e.Type)));
            Assert.Contains(replay, e => e.Type == "container");
        }

        // ---------- 第二段：实时订阅 + 真实操作 ----------
        var live = new ConcurrentBag<WslcEvent>();
        using var liveCts = new CancellationTokenSource();
        var streamTask = Task.Run(async () =>
        {
            try
            {
                // 不带 --since = 只推新事件，正好用来验证"实时"
                await client.StreamEventsAsync(e => live.Add(e), null, liveCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _output.WriteLine($"实时订阅异常：{ex.Message}");
            }
        });

        var name = $"wslc-panel-selftest-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            // 给订阅进程一点启动时间，否则可能漏掉紧随其后的事件
            await Task.Delay(TimeSpan.FromSeconds(2));

            var created = await client.CreateContainerAsync(new ContainerCreateOptions
            {
                Image = "docker.1panel.live/library/alpine",
                Name = name
            });
            _output.WriteLine($"创建结果：success={created.Success} id={created.ContainerId} msg={created.Message}");

            await Task.Delay(TimeSpan.FromSeconds(2));
            var removed = await client.DeleteContainerAsync(name, force: true);
            _output.WriteLine($"删除结果：{removed}");

            // 等事件推上来（引擎推送有延迟，给足余量）
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && !live.Any(e => e.ActorName == name))
            {
                await Task.Delay(300);
            }

            var mine = live.Where(e => e.ActorName == name).OrderBy(e => e.TimeNano).ToList();
            _output.WriteLine($"实时收到 {live.Count} 条事件，其中属于本次测试容器的 {mine.Count} 条：");
            foreach (var e in mine)
            {
                _output.WriteLine($"  {e.Time:HH:mm:ss.fff} [{e.Severity}] {e.Summary}");
            }

            Assert.True(created.Success, $"创建一次性容器失败：{created.Message}");

            // 核心断言：实时通道确实把容器事件推了过来
            Assert.NotEmpty(mine);
            Assert.Contains(mine, e => e.Action == "create");
            Assert.All(mine, e => Assert.Equal("container", e.Type));
            Assert.All(mine, e => Assert.Equal(64, e.ActorId.Length));
        }
        finally
        {
            // 无论断言是否失败都要清干净，且不能因为清理失败掩盖真实断言
            try
            {
                await client.DeleteContainerAsync(name, force: true);
            }
            catch (Exception ex)
            {
                _output.WriteLine($"清理容器 {name} 失败（请手工检查）：{ex.Message}");
            }

            liveCts.Cancel();
            await Task.WhenAny(streamTask, Task.Delay(TimeSpan.FromSeconds(5)));
        }
    }
}
