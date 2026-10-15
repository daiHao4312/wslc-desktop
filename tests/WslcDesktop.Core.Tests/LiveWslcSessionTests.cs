using Microsoft.WSL.Containers;
using Xunit.Abstractions;

namespace WslcDesktop.Core.Tests;

public class LiveWslcSessionTests
{
    private readonly ITestOutputHelper _output;

    public LiveWslcSessionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void TestCreateAppSessionAndInspect()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var sessionPath = Path.Combine(localAppData, "wslc", "sessions", "WslcDesktopTest");
        Directory.CreateDirectory(sessionPath);
        _output.WriteLine($"Custom session path: {sessionPath}");

        var settings = new SessionSettings("WslcDesktopTest", sessionPath)
        {
            CpuCount = 2,
            MemorySizeInMB = 2048
        };
        
        using var session = new Session(settings);
        try
        {
            session.Start();
            _output.WriteLine("Custom session started successfully!");

            var images = session.GetImages();
            _output.WriteLine($"Image count in custom session: {images.Count}");

            session.Terminate();
            _output.WriteLine("Custom session terminated successfully!");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Exception: {ex.GetType().FullName}");
            _output.WriteLine($"HResult: 0x{ex.HResult:X8} ({ex.HResult})");
            if (Enum.IsDefined(typeof(Microsoft.WSL.Containers.Error), ex.HResult))
            {
                _output.WriteLine($"Matched Error Enum: {(Microsoft.WSL.Containers.Error)ex.HResult}");
            }
            _output.WriteLine($"Message: {ex.Message}");
            throw;
        }
    }
}
