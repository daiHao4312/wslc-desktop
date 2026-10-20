using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using WslcDesktop.Core.Abstractions;
using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Core.Services;

/// <summary>
/// 从 GitHub Releases 下载更新包（单文件 exe）。
///
/// 设计取舍：
///   * **先写 .part 再改名**：更新包接近 100 MB，半截文件一旦被当成"已下载"就会在替换阶段炸掉，
///     所以任何失败/取消路径都必须保证目标目录里不存在不完整的包。
///   * **边下边算 sha256**：96 MB 读两遍不值当，用 <see cref="IncrementalHash"/> 在写盘的同时累积。
///   * **校验不通过就删**：宁可让用户重下一次，也不要把一个哈希对不上的 exe 送去替换。
///   * **进度节流**：81920 字节的缓冲会产生成百上千次回调，直接往 UI 抛会拖死界面，
///     这里按 100 ms 合并上报（结束/首次另算）。
///   * 超时给到 30 分钟：家用带宽下 96 MB 可能要好几分钟，不能沿用检查更新那 10 秒。
/// </summary>
public sealed class UpdateDownloader : IUpdateDownloader, IDisposable
{
    private const int BufferSize = 81920;

    /// <summary>进度回调的最小间隔（合并高频小回调）。</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private const string PartSuffix = ".part";

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public UpdateDownloader(HttpClient? httpClient = null)
    {
        if (httpClient is null)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            _ownsClient = true;
        }
        else
        {
            _http = httpClient;
            _ownsClient = false;
        }
    }

    public async Task<UpdateDownloadResult> DownloadAsync(
        UpdatePackage package,
        string destinationDirectory,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (package is null || string.IsNullOrWhiteSpace(package.DownloadUrl))
        {
            return UpdateDownloadResult.Failure("更新包信息不完整（缺少下载地址）。");
        }

        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            return UpdateDownloadResult.Failure("未指定更新包存放目录。");
        }

        // 资产名来自远端，只取文件名部分，防止 "..\\..\\x.exe" 这类名字写到目录外
        var safeName = Path.GetFileName(package.FileName?.Trim() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = $"wslc-desktop-{package.Version}.exe";
        }

        string targetPath;
        string partPath;
        try
        {
            Directory.CreateDirectory(destinationDirectory);
            targetPath = Path.Combine(destinationDirectory, safeName);
            partPath = targetPath + PartSuffix;
        }
        catch (Exception ex)
        {
            return UpdateDownloadResult.Failure($"无法准备下载目录：{ex.Message}");
        }

        // 幂等：本地已有同名且校验通过（或至少有相同长度）的包，直接复用
        if (VerifyExisting(targetPath, package) is { } reused)
        {
            progress?.Report(new UpdateDownloadProgress(reused, package.Size));
            return UpdateDownloadResult.Ok(targetPath, reused, package.Sha256, reused: true);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, package.DownloadUrl);
            // GitHub 资产下载同样要求 UA
            request.Headers.UserAgent.ParseAdd($"wslc-desktop/{package.Version}");

            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return UpdateDownloadResult.Failure(response.StatusCode switch
                {
                    HttpStatusCode.NotFound => "更新包不存在（发布可能已被删除或替换）。",
                    HttpStatusCode.Forbidden => "GitHub 拒绝了下载请求（可能是频率限制），稍后再试。",
                    _ => $"下载失败：GitHub 返回 {(int)response.StatusCode}。"
                });
            }

            var declaredLength = response.Content.Headers.ContentLength;
            var total = declaredLength ?? package.Size;

            var clock = Stopwatch.StartNew();
            var lastReportMs = -1L;
            long received = 0;
            string actualHash;

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var sink = new FileStream(
                partPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[BufferSize];

                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken);
                    if (read <= 0) break;

                    await sink.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hash.AppendData(buffer, 0, read);
                    received += read;

                    // 首次立即报一次（让进度条别干等着），其后按间隔合并
                    if (lastReportMs < 0 || clock.ElapsedMilliseconds - lastReportMs >= ProgressInterval.TotalMilliseconds)
                    {
                        lastReportMs = clock.ElapsedMilliseconds;
                        progress?.Report(new UpdateDownloadProgress(received, total));
                    }
                }

                await sink.FlushAsync(cancellationToken);
                actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            progress?.Report(new UpdateDownloadProgress(received, total));

            // 完整性校验：哈希优先（能挡住"内容被换掉"），长度兜底（能挡住"传到一半断了"）
            if (package.HasChecksum && !AssetDigest.AreEqual(actualHash, package.Sha256))
            {
                DeleteQuietly(partPath);
                return UpdateDownloadResult.Failure(
                    "下载内容校验失败（sha256 与发布不一致），已删除该文件。请重试或改用「打开下载页」手动下载。");
            }

            if (declaredLength is > 0 && received != declaredLength.Value)
            {
                DeleteQuietly(partPath);
                return UpdateDownloadResult.Failure(
                    $"下载不完整（收到 {received} 字节，应为 {declaredLength.Value} 字节），已删除该文件。请重试。");
            }

            // 校验过了才落成正式文件名
            File.Move(partPath, targetPath, overwrite: true);

            return UpdateDownloadResult.Ok(targetPath, received, actualHash);
        }
        catch (OperationCanceledException)
        {
            DeleteQuietly(partPath);
            return UpdateDownloadResult.Cancelled();
        }
        catch (HttpRequestException ex)
        {
            DeleteQuietly(partPath);
            return UpdateDownloadResult.Failure($"无法连接下载服务器：{ex.Message}");
        }
        catch (Exception ex)
        {
            DeleteQuietly(partPath);
            return UpdateDownloadResult.Failure($"下载失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 已存在的目标文件能否直接复用：返回它的字节数，不能复用返回 null。
    ///
    /// 有摘要时**必须**校验摘要——否则"上次下到一半被改名"的坏包会被当成好包。
    /// 没摘要时退回长度比对（长度也没有就只能重下）。
    /// </summary>
    private static long? VerifyExisting(string targetPath, UpdatePackage package)
    {
        try
        {
            if (!File.Exists(targetPath)) return null;

            var length = new FileInfo(targetPath).Length;
            if (length <= 0) return null;
            if (package.Size is > 0 && length != package.Size.Value) return null;

            if (!package.HasChecksum) return package.Size is > 0 ? length : null;

            var existing = AssetDigest.TryComputeFileSha256(targetPath);
            return AssetDigest.AreEqual(existing, package.Sha256) ? length : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 清理失败不该掩盖真正的失败原因；残留的 .part 下次下载会覆盖
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
