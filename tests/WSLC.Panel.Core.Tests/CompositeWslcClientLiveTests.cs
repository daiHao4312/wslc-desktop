using WSLC.Panel.Core.Services;
using Xunit.Abstractions;

namespace WSLC.Panel.Core.Tests;

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
