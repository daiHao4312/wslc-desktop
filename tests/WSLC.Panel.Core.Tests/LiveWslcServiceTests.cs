using Microsoft.WSL.Containers;
using Xunit.Abstractions;

namespace WSLC.Panel.Core.Tests;

public class LiveWslcServiceTests
{
    private readonly ITestOutputHelper _output;

    public LiveWslcServiceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void TestGetVersionAndComponents()
    {
        var version = WslcService.GetVersion();
        _output.WriteLine($"WSLC Service Version: {version.Major}.{version.Minor}.{version.Revision}");
        Assert.True(version.Major >= 3);

        var missing = WslcService.GetMissingComponents();
        _output.WriteLine($"Missing components count: {missing.Count}");
        foreach (var comp in missing)
        {
            _output.WriteLine($"  Missing: {comp}");
        }
        Assert.Empty(missing);
    }
}
