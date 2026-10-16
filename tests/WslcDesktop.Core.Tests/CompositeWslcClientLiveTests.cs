using WslcDesktop.Core.Services;
using Xunit.Abstractions;

namespace WslcDesktop.Core.Tests;

public class CompositeWslcClientLiveTests
{
    private readonly ITestOutputHelper _output;

    public CompositeWslcClientLiveTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task TestLiveSystemInfo()
    {
        var client = new CompositeWslcClient();
        var sysInfo = await client.GetSystemInfoAsync();

        _output.WriteLine($"WSL Version: {sysInfo.WslVersion}");
        _output.WriteLine($"Kernel Version: {sysInfo.KernelVersion}");
        _output.WriteLine($"Active Sessions: {sysInfo.ActiveSessions.Count}");
        _output.WriteLine($"Missing Components: {sysInfo.MissingComponents.Count}");

        Assert.NotEmpty(sysInfo.WslVersion);
        Assert.True(sysInfo.IsWslcReady);
    }

    [Fact]
    public async Task TestLiveContainersAndStats()
    {
        var client = new CompositeWslcClient();
        var containers = await client.GetContainersAsync(all: true);

        _output.WriteLine($"Found containers: {containers.Count}");
        foreach (var c in containers)
        {
            _output.WriteLine($"Container: Id={c.Id}, Name={c.Name}, Image={c.Image}, State={c.State}");
            if (c.PortMappings.Count > 0)
            {
                foreach (var p in c.PortMappings)
                {
                    _output.WriteLine($"  Port: {p.DisplayString} -> {p.BrowserUrl}");
                }
            }
            if (c.Stats != null)
            {
                _output.WriteLine($"  Stats: CPU={c.Stats.CpuPercentRaw}, Mem={c.Stats.MemoryUsage}, Net={c.Stats.NetIO}");
            }
        }

        // 这里原来只有 WriteLine、一条断言都没有，于是「stats 命令参数写错 → 整列 CPU/内存
        // 永远是 —」这种故障可以一路绿灯溜过去（实测踩过：多传了 docker 才有的 --no-stream，
        // 引擎直接回「选项名称未被识别」）。有运行中的容器却一条 stats 都拿不到，就是坏了。
        var running = containers.Where(c => c.IsRunning).ToList();
        if (running.Count == 0)
        {
            _output.WriteLine("没有运行中的容器，跳过 stats 断言（引擎/会话不可用时也走这条路）");
            return;
        }

        Assert.Contains(running, c => c.Stats is not null);
        Assert.Contains(running, c => !string.IsNullOrWhiteSpace(c.Stats!.MemoryUsage));
    }

    [Fact]
    public async Task TestLiveImages()
    {
        var client = new CompositeWslcClient();
        var images = await client.GetImagesAsync();

        _output.WriteLine($"Found images: {images.Count}");
        foreach (var img in images)
        {
            _output.WriteLine($"Image: Id={img.Id}, Repo={img.Repository}, Tag={img.Tag}, Size={img.Size}");
        }
    }
}
