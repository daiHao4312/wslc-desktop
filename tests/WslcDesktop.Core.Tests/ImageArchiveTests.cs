using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;
using WslcDesktop.Core.Services;

namespace WslcDesktop.Core.Tests;

/// <summary>
/// 镜像导出／导入（<c>wslc save -o</c> / <c>wslc load -i</c>）。
///
/// 这些断言锁的都是**实测出来的引擎行为**，不是猜的：
/// <list type="bullet">
/// <item><c>save</c> 不给 <c>-o</c> 就把 tar 写 stdout → 参数形状必须由 builder 钉住；</item>
/// <item>成功时引擎一句话都不输出 → 只能靠目标文件大小判断"真的写出来了"；</item>
/// <item>失败/取消留下的半截 tar 必须删掉，否则下次 load 会得到一句含糊的错；</item>
/// <item><c>load</c> 每个镜像打一行 <c>已加载映像: repo:tag</c>，这是唯一的"导入了什么"来源。</item>
/// </list>
/// </summary>
public class ImageArchiveTests
{
    // ---------- 参数拼装 ----------

    [Fact]
    public void BuildSaveArguments_PutsOutputOptionBeforeImages()
    {
        var args = WslcCommandBuilder.BuildSaveArguments(
            new[] { "docker.1panel.live/library/alpine:latest", "my-app:1.0" },
            @"D:\backup\images.tar");

        // 含反斜杠的参数一律被 Quote 包成双引号（CommandLineToArgvW 的往返解析仍得到原路径）
        Assert.Equal(
            "save -o \"D:\\backup\\images.tar\" docker.1panel.live/library/alpine:latest my-app:1.0",
            args);
    }

    [Fact]
    public void BuildSaveArguments_QuotesPathWithSpaces()
    {
        var args = WslcCommandBuilder.BuildSaveArguments(
            new[] { "alpine:3.20" },
            @"C:\my backup\alpine.tar");

        // 转义由 WslcCommandBuilder.Quote 负责，这里只钉"路径确实被当成一个参数"
        Assert.Contains("-o \"C:\\my backup\\alpine.tar\"", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSaveArguments_SkipsBlankReferences()
    {
        var args = WslcCommandBuilder.BuildSaveArguments(
            new[] { "  alpine:3.20  ", "", "   " },
            "alpine.tar");

        Assert.Equal("save -o alpine.tar alpine:3.20", args);
    }

    [Fact]
    public void BuildLoadArguments_UsesInputOption()
    {
        Assert.Equal("load -i alpine.tar", WslcCommandBuilder.BuildLoadArguments("alpine.tar"));
    }

    // ---------- 输出解析 ----------

    [Theory]
    [InlineData("已加载映像: docker.1panel.live/library/alpine:latest", "docker.1panel.live/library/alpine:latest")]
    [InlineData("已加载映像：my-alpine:test", "my-alpine:test")]
    [InlineData("Loaded image: nginx:1.27", "nginx:1.27")]
    public void TryParseLoadedReference_AcceptsBothLanguages(string line, string expected)
    {
        Assert.True(ImageArchiveParser.TryParseLoadedReference(line, out var reference));
        Assert.Equal(expected, reference);
    }

    [Theory]
    [InlineData("")]
    [InlineData("版权所有 (c) Microsoft Corporation。保留所有权利。")]
    [InlineData("正在加载，请稍候…")]
    public void TryParseLoadedReference_RejectsOtherLines(string line)
    {
        Assert.False(ImageArchiveParser.TryParseLoadedReference(line, out _));
    }

    [Fact]
    public void ExtractError_PrefersEngineMessageAndCode()
    {
        const string stderr =
            "版权所有 (c) Microsoft Corporation。保留所有权利。\n" +
            "来自引擎的错误信息: reference does not exist\n" +
            "错误代码： WSLC_E_IMAGE_NOT_FOUND\n" +
            "如果此错误是意外错误，请考虑搜索现有问题或在 https://github.com/microsoft/WSL/issues 提交新问题。\n";

        var message = ImageArchiveParser.ExtractError(stderr, string.Empty);

        Assert.Equal("reference does not exist（WSLC_E_IMAGE_NOT_FOUND）", message);
    }

    [Fact]
    public void ExtractError_SkipsBoilerplateWhenNoEngineMessage()
    {
        const string stderr =
            "版权所有 (c) Microsoft Corporation。保留所有权利。\n" +
            "系统找不到指定的文件。 \n" +
            "错误代码： ERROR_FILE_NOT_FOUND\n";

        var message = ImageArchiveParser.ExtractError(stderr, string.Empty);

        Assert.Equal("系统找不到指定的文件。（ERROR_FILE_NOT_FOUND）", message);
    }

    [Fact]
    public void ExtractError_FallsBackWhenNothingReadable()
    {
        var message = ImageArchiveParser.ExtractError(
            "版权所有 (c) Microsoft Corporation。保留所有权利。\n",
            null,
            "兜底原因");

        Assert.Equal("兜底原因", message);
    }

    [Fact]
    public void SuggestFileName_StripsRegistryAndTag()
    {
        var name = ImageArchiveParser.SuggestFileName(
            "docker.1panel.live/library/alpine:latest",
            new DateTimeOffset(2026, 10, 20, 16, 0, 0, TimeSpan.FromHours(8)));

        Assert.Equal("alpine-20261020-160000.tar", name);
    }

    [Fact]
    public void SuggestFileName_HandlesEmptyReference()
    {
        var name = ImageArchiveParser.SuggestFileName(null, new DateTimeOffset(2026, 10, 20, 16, 0, 0, TimeSpan.Zero));
        Assert.StartsWith("images-", name, StringComparison.Ordinal);
        Assert.EndsWith(".tar", name, StringComparison.Ordinal);
    }

    // ---------- 路径校验 ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative.tar")]
    [InlineData(@"\root-only.tar")]
    [InlineData(@"D:\backup\")]
    public void TryValidateExportPath_RejectsBadPaths(string? path)
    {
        Assert.False(ImageArchiveParser.TryValidateExportPath(path, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryValidateExportPath_AcceptsAbsoluteFile()
    {
        Assert.True(ImageArchiveParser.TryValidateExportPath(@"D:\backup\alpine.tar", out var error));
        Assert.Empty(error);
    }

    [Fact]
    public void TryValidateImportPath_RejectsMissingFile()
    {
        Assert.False(ImageArchiveParser.TryValidateImportPath(@"D:\nope\missing.tar", out var error));
        Assert.Contains("不存在", error, StringComparison.Ordinal);
    }

    // ---------- CLI 实现 ----------

    [Fact]
    public async Task ExportImagesAsync_FailsWithoutCallingEngine_WhenNoReference()
    {
        var runner = new RecordingProcessRunner();
        var client = new CliWslcClient(runner);

        var result = await client.ExportImagesAsync(Array.Empty<string>(), @"D:\backup\x.tar");

        Assert.False(result.Success);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task ExportImagesAsync_ReportsSuccessWithFileSize()
    {
        var path = TempPath();
        var runner = new RecordingProcessRunner
        {
            Handler = (_, _) =>
            {
                // 引擎"写"出文件：导出成功的唯一证据就是这个文件确实有内容
                File.WriteAllBytes(path, new byte[4096]);
                return new ProcessExecutionResult(0, string.Empty, string.Empty, true);
            }
        };
        var client = new CliWslcClient(runner);
        var progressSeen = new List<ImageArchiveProgress>();

        try
        {
            var result = await client.ExportImagesAsync(
                new[] { "alpine:3.20" },
                path,
                progressSeen.Add);

            Assert.True(result.Success);
            Assert.Contains("save -o", runner.Calls[0], StringComparison.Ordinal);
            Assert.Contains("alpine:3.20", runner.Calls[0], StringComparison.Ordinal);
            Assert.Contains("4.0 KB", result.Message, StringComparison.Ordinal);
            Assert.Contains(progressSeen, p => p.BytesWritten > 0);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ExportImagesAsync_DeletesPartialFile_WhenEngineFails()
    {
        var path = TempPath();
        var runner = new RecordingProcessRunner
        {
            Handler = (_, _) =>
            {
                File.WriteAllBytes(path, new byte[128]);
                return new ProcessExecutionResult(
                    1,
                    string.Empty,
                    "来自引擎的错误信息: reference does not exist\n错误代码： WSLC_E_IMAGE_NOT_FOUND\n",
                    false);
            }
        };
        var client = new CliWslcClient(runner);

        var result = await client.ExportImagesAsync(new[] { "nope:latest" }, path);

        Assert.False(result.Success);
        Assert.False(result.Cancelled);
        Assert.Contains("WSLC_E_IMAGE_NOT_FOUND", result.Message, StringComparison.Ordinal);
        // 半截 tar 留着只会让下次 load 报一句更含糊的错
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ExportImagesAsync_DeletesFile_WhenEngineSilentlyProducesNothing()
    {
        var path = TempPath();
        var runner = new RecordingProcessRunner
        {
            Handler = (_, _) => new ProcessExecutionResult(0, string.Empty, string.Empty, true)
        };
        var client = new CliWslcClient(runner);

        var result = await client.ExportImagesAsync(new[] { "alpine:3.20" }, path);

        Assert.False(result.Success);
        Assert.Contains("不存在或为空", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportImagesAsync_ReportsCancelled_AndDeletesLeftover()
    {
        var path = TempPath();
        var runner = new RecordingProcessRunner
        {
            Handler = (_, _) =>
            {
                File.WriteAllBytes(path, new byte[64]);
                throw new OperationCanceledException();
            }
        };
        var client = new CliWslcClient(runner);

        var result = await client.ExportImagesAsync(new[] { "alpine:3.20" }, path);

        Assert.False(result.Success);
        Assert.True(result.Cancelled);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ImportImageArchiveAsync_CollectsLoadedReferences()
    {
        var path = TempTar();
        var runner = new RecordingProcessRunner
        {
            Handler = (_, _) => new ProcessExecutionResult(
                0,
                "已加载映像: docker.1panel.live/library/alpine:latest\n已加载映像: my-alpine:test\n",
                string.Empty,
                true)
        };
        var client = new CliWslcClient(runner);

        try
        {
            var result = await client.ImportImageArchiveAsync(path);

            Assert.True(result.Success);
            Assert.Equal(2, result.References.Count);
            Assert.Contains("my-alpine:test", result.References);
            Assert.Contains("load -i", runner.Calls[0], StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ImportImageArchiveAsync_FailsLocally_WhenFileMissing()
    {
        var runner = new RecordingProcessRunner();
        var client = new CliWslcClient(runner);

        var result = await client.ImportImageArchiveAsync(@"D:\nope\missing.tar");

        Assert.False(result.Success);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task ImportImageArchiveAsync_SurfacesEngineError()
    {
        var path = TempTar();
        var runner = new RecordingProcessRunner
        {
            Handler = (_, _) => new ProcessExecutionResult(
                1,
                string.Empty,
                "来自引擎的错误信息: archive is not a valid image tarball\n错误代码： WSLC_E_INVALID_ARGUMENT\n",
                false)
        };
        var client = new CliWslcClient(runner);

        try
        {
            var result = await client.ImportImageArchiveAsync(path);

            Assert.False(result.Success);
            Assert.Contains("archive is not a valid image tarball", result.Message, StringComparison.Ordinal);
            Assert.Contains("WSLC_E_INVALID_ARGUMENT", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), $"wslc-test-{Guid.NewGuid():N}.tar");

    private static string TempTar()
    {
        var path = TempPath();
        File.WriteAllBytes(path, new byte[32]);
        return path;
    }
}
