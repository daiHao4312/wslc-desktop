using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Core.Tests;

/// <summary>
/// 诊断包的报告与脱敏（M20）。
///
/// 报告要"能贴进 issue"：有版本、有状态、有计数；**没有**原始 JSON、没有凭据。
/// </summary>
public class DiagnosticsReportTests
{
    private static DiagnosticSnapshot Sample() => new()
    {
        AppVersion = "1.1.1",
        Runtime = ".NET 10.0.0",
        OsDescription = "Microsoft Windows 10.0.26100",
        EngineVersion = "3.0.3.0",
        SessionManagerVersion = "3.0.3",
        MissingComponents = Array.Empty<string>(),
        ApiChannelState = "可用 · 会话「wslc-cli-UMS」（只读附着）",
        ApiChannelReason = "只读附着（会话由 CLI 进程独占，未调用 Start）",
        Sessions = new[]
        {
            new DiagnosticSession
            {
                Name = "wslc-cli-UMS",
                State = "CLI 默认",
                StoragePath = @"C:\Users\UMS\AppData\Local\wslc\sessions\wslc-cli-UMS\storage.vhdx",
                SizeBytes = 2L * 1024 * 1024 * 1024,
                IsDefault = true
            }
        },
        ContainerTotal = 3,
        ContainerRunning = 2,
        ImageTotal = 7,
        VolumeTotal = 1,
        NetworkTotal = 4
    };

    [Fact]
    public void 报告里要有环境与规模()
    {
        var report = DiagnosticsReport.Build(Sample());

        Assert.Contains("面板版本：1.1.1", report);
        Assert.Contains("WSL 引擎：3.0.3.0", report);
        Assert.Contains("会话管理器：3.0.3", report);
        Assert.Contains("缺失组件：无", report);
        Assert.Contains("容器：3（运行中 2）", report);
        Assert.Contains("镜像：7", report);
    }

    [Fact]
    public void 报告要写清API通道的真实状态()
    {
        var report = DiagnosticsReport.Build(Sample());

        Assert.Contains("只读附着", report);
        Assert.Contains("wslc-cli-UMS", report);
    }

    [Fact]
    public void 会话按表格列出并带可读体积()
    {
        var report = DiagnosticsReport.Build(Sample());

        Assert.Contains("| wslc-cli-UMS | CLI 默认 | 是 |", report);
        Assert.Contains("2 GB", report);
    }

    [Fact]
    public void 没有数据时用短横线而不是空白()
    {
        var report = DiagnosticsReport.Build(new DiagnosticSnapshot());

        Assert.Contains("面板版本：—", report);
        Assert.Contains("（未读到会话）", report);
    }

    [Theory]
    [InlineData(0, "—")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    public void 字节数格式化(long bytes, string expected)
        => Assert.Equal(expected, DiagnosticsReport.FormatBytes(bytes));
}
