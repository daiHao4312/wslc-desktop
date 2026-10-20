using System.Net;
using System.Security.Cryptography;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;
using WslcDesktop.Core.Services;

namespace WslcDesktop.Core.Tests;

/// <summary>
/// M13 在线更新：摘要工具 + 更新包下载器。
/// 全部用假 HttpMessageHandler，不依赖网络；落盘用测试专用临时目录。
/// </summary>
public class UpdateDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "wslc-update-tests", Guid.NewGuid().ToString("N"));

    public UpdateDownloadTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不该让测试变红
        }

        GC.SuppressFinalize(this);
    }

    // ---------- 假 handler ----------

    /// <summary>可以指定字节内容、声明长度与抛出的异常，并记录请求次数。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly byte[] _body;
        private readonly long? _declaredLength;
        private readonly Exception? _throw;

        public int Requests { get; private set; }

        public StubHandler(
            HttpStatusCode status = HttpStatusCode.OK,
            byte[]? body = null,
            long? declaredLength = null,
            Exception? toThrow = null)
        {
            _status = status;
            _body = body ?? Array.Empty<byte>();
            _declaredLength = declaredLength;
            _throw = toThrow;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            cancellationToken.ThrowIfCancellationRequested();

            if (_throw is not null) throw _throw;

            var response = new HttpResponseMessage(_status);

            // 非 2xx 时不放内容，贴近真实响应
            if (_status == HttpStatusCode.OK)
            {
                response.Content = new RawContent(_body, _declaredLength);
            }

            return Task.FromResult(response);
        }
    }

    /// <summary>能伪造 Content-Length 的内容体（用于验证"长度对不上要判失败"）。</summary>
    private sealed class RawContent : HttpContent
    {
        private readonly byte[] _data;
        private readonly long? _declaredLength;

        public RawContent(byte[] data, long? declaredLength)
        {
            _data = data;
            _declaredLength = declaredLength;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _declaredLength ?? _data.Length;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(_data).AsTask();

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new MemoryStream(_data, writable: false));
    }

    private UpdatePackage Package(byte[] payload, string name = "wslc-desktop-1.2.0.exe")
    {
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        return new UpdatePackage
        {
            Version = "1.2.0",
            DownloadUrl = "https://example.com/wslc-desktop.exe",
            FileName = name,
            Size = payload.Length,
            Sha256 = hash
        };
    }

    // ---------- 摘要工具 ----------

    [Theory]
    [InlineData("sha256:ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789",
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    [InlineData("SHA256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    public void TestNormalizeSha256_AcceptsPrefixedAndBareHex(string raw, string expected)
        => Assert.Equal(expected, AssetDigest.NormalizeSha256(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sha256:")]                                     // 没有主体
    [InlineData("md5:d41d8cd98f00b204e9800998ecf8427e")]        // 算法不对且长度不足
    [InlineData("abcdef")]                                      // 太短
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")] // 非十六进制
    public void TestNormalizeSha256_RejectsInvalid(string? raw)
        => Assert.Null(AssetDigest.NormalizeSha256(raw));

    [Fact]
    public void TestAssetDigest_AreEqual_NeverPassesOnMissingDigest()
    {
        const string hash = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

        Assert.True(AssetDigest.AreEqual(hash, hash.ToUpperInvariant()));
        Assert.False(AssetDigest.AreEqual(null, null));
        Assert.False(AssetDigest.AreEqual(hash, null));
        Assert.False(AssetDigest.AreEqual(null, hash));
    }

    [Fact]
    public void TestTryComputeFileSha256_MatchesFrameworkHash()
    {
        var payload = "wslc-desktop"u8.ToArray();
        var path = Path.Combine(_root, "sample.bin");
        File.WriteAllBytes(path, payload);

        var expected = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        Assert.Equal(expected, AssetDigest.TryComputeFileSha256(path));
        Assert.Null(AssetDigest.TryComputeFileSha256(Path.Combine(_root, "missing.bin")));
        Assert.Null(AssetDigest.TryComputeFileSha256(null));
    }

    /// <summary>
    /// 同步收集进度。不用 <see cref="Progress{T}"/>：它把回调派发到线程池，
    /// 多个回调的先后顺序在多核上不保证，会让"最后一条是不是收尾值"变得不可断言。
    /// </summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly List<T> _values;

        public SyncProgress(List<T> values) => _values = values;

        public void Report(T value) => _values.Add(value);
    }

    // ---------- 下载 ----------

    [Fact]
    public async Task TestDownload_SuccessWritesFileAndReportsProgress()
    {
        var payload = RandomBytes(300_000);
        var handler = new StubHandler(body: payload);
        using var downloader = new UpdateDownloader(new HttpClient(handler));

        var reports = new List<UpdateDownloadProgress>();
        var progress = new SyncProgress<UpdateDownloadProgress>(reports);

        var result = await downloader.DownloadAsync(Package(payload), _root, progress);

        Assert.True(result.Succeeded);
        Assert.Equal(payload.Length, result.BytesWritten);
        Assert.Equal(Path.Combine(_root, "wslc-desktop-1.2.0.exe"), result.FilePath);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath!));

        // 半成品必须清干净，否则下次替换会拿到不完整的包
        Assert.False(File.Exists(result.FilePath + ".part"));

        Assert.NotNull(result.Sha256);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), result.Sha256);

        // 最后一条进度必须是"收完了"，否则进度条会永远停在 90 几
        Assert.NotEmpty(reports);
        Assert.Equal(payload.Length, reports[^1].BytesReceived);
        Assert.Equal(100d, reports[^1].Percent!.Value, 3);
        Assert.False(reports[^1].IsIndeterminate);
    }

    [Fact]
    public async Task TestDownload_ChecksumMismatchDeletesFile()
    {
        var payload = RandomBytes(4096);
        var handler = new StubHandler(body: payload);
        using var downloader = new UpdateDownloader(new HttpClient(handler));

        var tampered = Package(payload);
        tampered = tampered with
        {
            Sha256 = new string('a', 64)
        };

        var result = await downloader.DownloadAsync(tampered, _root);

        Assert.Equal(UpdateDownloadStatus.Failed, result.Status);
        Assert.Contains("校验失败", result.Message);
        Assert.False(File.Exists(Path.Combine(_root, "wslc-desktop-1.2.0.exe")));
        Assert.False(File.Exists(Path.Combine(_root, "wslc-desktop-1.2.0.exe.part")));
    }

    [Fact]
    public async Task TestDownload_TruncatedBodyIsRejected()
    {
        var payload = RandomBytes(2000);
        // 声明 5000 字节，实际只发 2000：传输被截断
        var handler = new StubHandler(body: payload, declaredLength: 5000);

        // 声明长度被当作 expected 大小，同时也让 package 自己声明 2000 以模拟"上游给的 size 也不可信"
        var package = Package(payload) with { Sha256 = null };
        using var downloader = new UpdateDownloader(new HttpClient(handler));

        var result = await downloader.DownloadAsync(package, _root);

        Assert.Equal(UpdateDownloadStatus.Failed, result.Status);
        Assert.Contains("下载不完整", result.Message);
        Assert.False(File.Exists(Path.Combine(_root, "wslc-desktop-1.2.0.exe")));
    }

    [Fact]
    public async Task TestDownload_ReusesExistingVerifiedPackage()
    {
        var payload = RandomBytes(8192);
        var target = Path.Combine(_root, "wslc-desktop-1.2.0.exe");
        await File.WriteAllBytesAsync(target, payload);

        var handler = new StubHandler(body: payload);
        using var downloader = new UpdateDownloader(new HttpClient(handler));

        var result = await downloader.DownloadAsync(Package(payload), _root);

        Assert.True(result.Succeeded);
        Assert.True(result.ReusedExisting);
        Assert.Equal(0, handler.Requests); // 没有重新下载
    }

    [Fact]
    public async Task TestDownload_DoesNotReuseTamperedExistingPackage()
    {
        var payload = RandomBytes(8192);
        var target = Path.Combine(_root, "wslc-desktop-1.2.0.exe");
        // 本地文件长度对得上，但内容被改过
        await File.WriteAllBytesAsync(target, RandomBytes(8192));

        var handler = new StubHandler(body: payload);
        using var downloader = new UpdateDownloader(new HttpClient(handler));

        var result = await downloader.DownloadAsync(Package(payload), _root);

        Assert.True(result.Succeeded);
        Assert.False(result.ReusedExisting);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(payload, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task TestDownload_NotFoundAndNetworkFailureDegradeGracefully()
    {
        using (var downloader = new UpdateDownloader(new HttpClient(new StubHandler(HttpStatusCode.NotFound))))
        {
            var result = await downloader.DownloadAsync(Package(RandomBytes(16)), _root);
            Assert.Equal(UpdateDownloadStatus.Failed, result.Status);
            Assert.Contains("不存在", result.Message);
        }

        using (var downloader = new UpdateDownloader(
            new HttpClient(new StubHandler(toThrow: new HttpRequestException("连接被重置")))))
        {
            var result = await downloader.DownloadAsync(Package(RandomBytes(16)), _root);
            Assert.Equal(UpdateDownloadStatus.Failed, result.Status);
            Assert.Contains("无法连接下载服务器", result.Message);
        }
    }

    [Fact]
    public async Task TestDownload_CancellationCleansPartFile()
    {
        var payload = RandomBytes(4096);
        using var downloader = new UpdateDownloader(new HttpClient(new StubHandler(body: payload)));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await downloader.DownloadAsync(Package(payload), _root, cancellationToken: cts.Token);

        Assert.Equal(UpdateDownloadStatus.Cancelled, result.Status);
        Assert.False(File.Exists(Path.Combine(_root, "wslc-desktop-1.2.0.exe.part")));
        Assert.False(File.Exists(Path.Combine(_root, "wslc-desktop-1.2.0.exe")));
    }

    [Fact]
    public async Task TestDownload_SanitizesAssetName()
    {
        var payload = RandomBytes(512);
        var handler = new StubHandler(body: payload);
        using var downloader = new UpdateDownloader(new HttpClient(handler));

        // 远端资产名带路径分隔符时只能落成文件名，不允许写到目录外
        var package = Package(payload, name: @"..\..\evil.exe");

        var result = await downloader.DownloadAsync(package, _root);

        Assert.True(result.Succeeded);
        Assert.Equal(Path.Combine(_root, "evil.exe"), result.FilePath);
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "evil.exe")));
    }

    [Fact]
    public async Task TestDownload_EmptyPackageInfoFailsFast()
    {
        using var downloader = new UpdateDownloader(new HttpClient(new StubHandler()));

        Assert.Equal(UpdateDownloadStatus.Failed,
            (await downloader.DownloadAsync(new UpdatePackage(), _root)).Status);

        Assert.Equal(UpdateDownloadStatus.Failed,
            (await downloader.DownloadAsync(Package(RandomBytes(8)), "  ")).Status);
    }

    [Fact]
    public void TestProgressDescribe_HandlesKnownAndUnknownTotal()
    {
        var known = new UpdateDownloadProgress(50 * 1024 * 1024, 100 * 1024 * 1024);
        Assert.False(known.IsIndeterminate);
        Assert.Equal(50d, known.Percent!.Value, 3);
        Assert.Contains("MB", known.Describe());

        var unknown = new UpdateDownloadProgress(1024, null);
        Assert.True(unknown.IsIndeterminate);
        Assert.Null(unknown.Percent);
        Assert.Equal("已下载 1.0 KB", unknown.Describe());
    }

    private static byte[] RandomBytes(int count)
    {
        var data = new byte[count];
        Random.Shared.NextBytes(data);
        return data;
    }
}
