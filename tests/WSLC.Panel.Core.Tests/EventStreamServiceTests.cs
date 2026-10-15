using System.Diagnostics;
using WSLC.Panel.Core.Abstractions;
using WSLC.Panel.Core.Models;
using WSLC.Panel.Core.Services;

namespace WSLC.Panel.Core.Tests;

/// <summary>
/// 事件流服务的行为测试（用 <see cref="FakeWslcClient"/> 替身，不依赖真机）。
///
/// 重点覆盖三件最容易错、错了也最难查的事：
/// ① 续接是否真的按"上一条事件时间"要；② 重复投递有没有压掉；③ 引擎不可用时会不会空转。
/// </summary>
public class EventStreamServiceTests
{
    private static WslcEvent MakeEvent(string action, string name, long timeNano)
        => new()
        {
            Time = DateTimeOffset.FromUnixTimeMilliseconds(timeNano / 1_000_000).ToLocalTime(),
            TimeNano = timeNano,
            Type = "container",
            Action = action,
            ActorId = new string('a', 64),
            ActorName = name,
            Image = "alpine"
        };

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }

        return condition();
    }

    [Fact]
    public async Task TestStart_ReceivesAndBuffersEvents()
    {
        var fake = new FakeWslcClient();
        var received = new List<WslcEvent>();

        using var service = new EventStreamService(fake);
        service.EventReceived += (_, e) =>
        {
            lock (received) received.Add(e);
        };

        fake.StreamBehavior = async (onEvent, _, ct) =>
        {
            onEvent(MakeEvent("create", "c1", 1_791_962_211_131_552_884));
            onEvent(MakeEvent("start", "c1", 1_791_962_212_131_552_884));
            onEvent(MakeEvent("die", "c1", 1_791_962_213_131_552_884));

            // 之后挂着等新事件 —— 模拟真实的长驻订阅
            await Task.Delay(Timeout.Infinite, ct);
        };

        service.Start();

        Assert.True(await WaitUntilAsync(() => service.RecentEvents.Count == 3));

        Assert.True(service.IsRunning);
        Assert.Equal(EventStreamState.Running, service.State);

        Assert.Equal(3, received.Count);
        Assert.Equal(new[] { "create", "start", "die" }, service.RecentEvents.Select(e => e.Action));
    }

    /// <summary>
    /// 订阅建立后、第一个事件到来前，状态必须是"已订阅"而不是"连接中"。
    ///
    /// 这个空窗期可以很长：首次订阅的回补窗口是 6 小时，如果这 6 小时内一次容器操作都没有，
    /// 引擎不会吐出任何事件，界面就会一直显示"事件流连接中…"——订阅其实是健康的。
    /// </summary>
    [Fact]
    public async Task TestStart_MarksSubscribedBeforeFirstEventArrives()
    {
        var fake = new FakeWslcClient();

        using var service = new EventStreamService(fake);

        fake.StreamBehavior = async (_, _, ct) =>
        {
            // 订阅正常建立，但一直没有事件发生
            await Task.Delay(Timeout.Infinite, ct);
        };

        service.Start();

        Assert.True(await WaitUntilAsync(() => service.State == EventStreamState.Subscribed));
        Assert.True(service.IsRunning);

        await Task.Delay(300);

        // 空窗期内不能有事件，也不能掉回 Connecting/Reconnecting
        Assert.Empty(service.RecentEvents);
        Assert.Equal(EventStreamState.Subscribed, service.State);
        Assert.True(service.IsRunning);
    }

    [Fact]
    public async Task TestPublish_DeduplicatesRepeatedDelivery()
    {
        var fake = new FakeWslcClient();
        var count = 0;
        var evt = MakeEvent("start", "c1", 1_791_962_211_131_552_884);

        using var service = new EventStreamService(fake);
        service.EventReceived += (_, _) => Interlocked.Increment(ref count);

        fake.StreamBehavior = async (onEvent, _, ct) =>
        {
            onEvent(evt);
            onEvent(evt);                                  // 续接重叠造成的重复
            onEvent(evt with { Action = "stop" });          // 不同动作，必须保留

            await Task.Delay(Timeout.Infinite, ct);
        };

        service.Start();

        Assert.True(await WaitUntilAsync(() => service.RecentEvents.Count == 2));
        await Task.Delay(200); // 留出可能多投的时间窗

        Assert.Equal(2, count);
        Assert.Equal(new[] { "start", "stop" }, service.RecentEvents.Select(e => e.Action));
    }

    [Fact]
    public async Task TestReconnect_ResumesFromLastEventTimeMinusOverlap()
    {
        var fake = new FakeWslcClient();
        var calls = 0;
        var evt = MakeEvent("start", "c1", 1_791_962_211_131_552_884);

        using var service = new EventStreamService(fake);

        fake.StreamBehavior = async (onEvent, _, ct) =>
        {
            calls++;
            if (calls == 1)
            {
                onEvent(evt);
                return; // 模拟引擎进程退出
            }

            await Task.Delay(Timeout.Infinite, ct);
        };

        service.Start();

        Assert.True(await WaitUntilAsync(() => fake.ReceivedSince.Count >= 2, timeoutMs: 8000));

        var first = fake.ReceivedSince[0];
        var second = fake.ReceivedSince[1];

        // 首次订阅：回补历史窗口（约 6 小时前），而不是干脆不回补
        Assert.NotNull(first);
        Assert.InRange((DateTimeOffset.Now - first!.Value).TotalHours, 5.9, 6.1);

        // 续接：上一条事件时间往前留 2 秒重叠，避免边界事件被漏掉
        Assert.Equal(evt.Time - TimeSpan.FromSeconds(2), second);
    }

    [Fact]
    public async Task TestReconnect_BacksOffInsteadOfSpinningWhenEngineYieldsNothing()
    {
        var fake = new FakeWslcClient();

        // 立即返回且一个事件都不给 —— 模拟引擎不可用
        fake.StreamBehavior = (_, _, _) => Task.CompletedTask;

        using var service = new EventStreamService(fake);
        service.Start();

        await Task.Delay(2500);

        // 有退避就不会在 2.5 秒里重试成百上千次
        Assert.True(
            fake.ReceivedSince.Count <= 5,
            $"期望指数退避，实际 2.5 秒内重试了 {fake.ReceivedSince.Count} 次");

        Assert.False(service.IsRunning);
        Assert.Equal(EventStreamState.Reconnecting, service.State);
    }

    [Fact]
    public async Task TestClear_EmptiesBufferButKeepsStreaming()
    {
        var fake = new FakeWslcClient();

        using var service = new EventStreamService(fake);

        fake.StreamBehavior = async (onEvent, _, ct) =>
        {
            onEvent(MakeEvent("create", "c1", 1_791_962_211_131_552_884));
            await Task.Delay(Timeout.Infinite, ct);
        };

        service.Start();
        Assert.True(await WaitUntilAsync(() => service.RecentEvents.Count == 1));

        service.Clear();
        Assert.Empty(service.RecentEvents);

        // 清空只是清缓冲，不该把订阅一起停掉
        Assert.True(service.IsRunning);
    }

    [Fact]
    public async Task TestStop_ReturnsToStoppedState()
    {
        var fake = new FakeWslcClient();

        using var service = new EventStreamService(fake);

        fake.StreamBehavior = async (onEvent, _, ct) =>
        {
            onEvent(MakeEvent("create", "c1", 1_791_962_211_131_552_884));
            await Task.Delay(Timeout.Infinite, ct);
        };

        service.Start();
        Assert.True(await WaitUntilAsync(() => service.IsRunning));

        service.Stop();

        Assert.False(service.IsRunning);
        Assert.Equal(EventStreamState.Stopped, service.State);

        // 停下之后不该再有新订阅被拉起
        var callsAfterStop = fake.ReceivedSince.Count;
        await Task.Delay(1600);
        Assert.Equal(callsAfterStop, fake.ReceivedSince.Count);
    }

    [Fact]
    public async Task TestStart_IsIdempotent()
    {
        var fake = new FakeWslcClient();

        using var service = new EventStreamService(fake);

        fake.StreamBehavior = async (onEvent, _, ct) =>
        {
            onEvent(MakeEvent("create", "c1", 1_791_962_211_131_552_884));
            await Task.Delay(Timeout.Infinite, ct);
        };

        service.Start();
        service.Start();
        service.Start();

        Assert.True(await WaitUntilAsync(() => service.IsRunning));
        await Task.Delay(300);

        // 三个 Start 只能有一条订阅，不能叠加
        Assert.Equal(1, fake.MaxConcurrentCalls);
        Assert.Single(fake.ReceivedSince);
    }
}
